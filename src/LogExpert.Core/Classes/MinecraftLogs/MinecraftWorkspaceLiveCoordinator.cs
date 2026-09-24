namespace LogExpert.Core.Classes.MinecraftLogs;

#pragma warning disable CA1031 // Per-source creation, start, and teardown failures must stay isolated.

public enum MinecraftWorkspaceSourceStatus
{
    Active,
    DeferredHandoff,
    Faulted
}

public enum MinecraftWorkspaceSourceReason
{
    RequiresSegmentHandoff,
    SessionCreationFailed,
    SessionStartFailed
}

/// <summary>Immutable runtime state for one discovered physical source.</summary>
public sealed record MinecraftWorkspaceSourceRuntimeState (
    DiscoveredSourceFile Source,
    MinecraftWorkspaceSourceStatus Status,
    MinecraftWorkspaceSourceReason? Reason,
    long EmittedEventCount);

/// <summary>An immutable event envelope in workspace ingress order.</summary>
public sealed record MinecraftWorkspaceIngressEvent (
    long IngressSequence,
    string WorkspaceId,
    string SourceId,
    string FileId,
    MinecraftSourceSegmentRole SegmentRole,
    NormalizedLogEvent Event);

/// <summary>
/// Reconciles YEE-39 physical discovery with YEE-42 single-file sessions. The ingress queue
/// is intentionally unbounded and no-drop in this slice; a memory/backpressure budget is a
/// later performance limitation. Reconcile is explicit and adds no discovery timer.
/// </summary>
public sealed class MinecraftWorkspaceLiveCoordinator : IDisposable
{
    private readonly MinecraftSourceDiscovery _discovery;
    private readonly IMinecraftWorkspaceLiveSourceSessionFactory _sessionFactory;
    private readonly object _reconcileGate = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceHandle> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MinecraftWorkspaceSourceRuntimeState> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedActivePartSourceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activatedPrimarySourceIds = new(StringComparer.Ordinal);
    private readonly Queue<MinecraftWorkspaceIngressEvent> _pendingEvents = new();
    private long _nextIngressSequence = 1;
    private bool _hasReconciled;
    private bool _disposeStarted;
    private bool _disposeComplete;

    public MinecraftWorkspaceLiveCoordinator (
        MinecraftSourceDiscovery discovery,
        IMinecraftWorkspaceLiveSourceSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(sessionFactory);

        _discovery = discovery;
        _sessionFactory = sessionFactory;
    }

    /// <summary>Number of queued, undrained workspace events.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingEvents.Count;
            }
        }
    }

    /// <summary>Rescans known paths and reconciles sessions in discovery order.</summary>
    public void Reconcile ()
    {
        lock (_reconcileGate)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeStarted, this);
            }

            MinecraftDiscoveryResult snapshot = _discovery.Rescan();
            var presentFileIds = snapshot.Files.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
            bool isInitialSnapshot;
            HashSet<string> observedActiveParts;
            HashSet<string> activatedPrimaries;
            SourceHandle[] disappearedSessions;

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeStarted, this);
                isInitialSnapshot = !_hasReconciled;
                observedActiveParts = new HashSet<string>(_observedActivePartSourceIds, StringComparer.Ordinal);
                activatedPrimaries = new HashSet<string>(_activatedPrimarySourceIds, StringComparer.Ordinal);
                disappearedSessions = _sessions.Values
                    .Where(handle => !presentFileIds.Contains(handle.Source.FileId))
                    .OrderBy(handle => handle.Source.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(handle => handle.Source.RelativePath, StringComparer.Ordinal)
                    .ToArray();
            }

            foreach (SourceHandle handle in disappearedSessions)
            {
                DisposeHandle(handle, removeRuntimeState: true);
            }

            lock (_gate)
            {
                foreach (string fileId in _sources.Keys.Where(fileId => !presentFileIds.Contains(fileId)).ToArray())
                {
                    _sources.Remove(fileId);
                }
            }

            // Discovery already returns a stable path order. Do not reorder by timestamps or
            // logical source identity; initial startup must follow that exact physical order.
            // Faulted state is retained while a FileId stays present, so unchanged rescans do
            // not retry. An observed disappearance clears it and a later appearance gets one retry.
            foreach (DiscoveredSourceFile source in snapshot.Files)
            {
                lock (_gate)
                {
                    if (_sources.ContainsKey(source.FileId))
                    {
                        continue;
                    }
                }

                if (RequiresDeferredHandoff(source, isInitialSnapshot, observedActiveParts, activatedPrimaries))
                {
                    lock (_gate)
                    {
                        _sources[source.FileId] = new MinecraftWorkspaceSourceRuntimeState(
                            source,
                            MinecraftWorkspaceSourceStatus.DeferredHandoff,
                            MinecraftWorkspaceSourceReason.RequiresSegmentHandoff,
                            EmittedEventCount: 0);
                    }

                    continue;
                }

                StartSource(source);
            }

            lock (_gate)
            {
                foreach (DiscoveredSourceFile source in snapshot.Files)
                {
                    if (source.Family == MinecraftSourceFamily.CactusMonitor &&
                        source.SegmentRole == MinecraftSourceSegmentRole.ActivePart)
                    {
                        _observedActivePartSourceIds.Add(source.SourceId);
                    }
                }

                _hasReconciled = true;
            }
        }
    }

    /// <summary>Returns a stable immutable snapshot of current active, deferred, and faulted sources.</summary>
    public IReadOnlyList<MinecraftWorkspaceSourceRuntimeState> GetRuntimeSources ()
    {
        lock (_gate)
        {
            MinecraftWorkspaceSourceRuntimeState[] snapshot = _sources.Values
                .OrderBy(state => state.Source.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.Source.RelativePath, StringComparer.Ordinal)
                .ThenBy(state => state.Source.FileId, StringComparer.Ordinal)
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }

    /// <summary>Atomically drains all currently pending events in ingress-sequence order.</summary>
    public IReadOnlyList<MinecraftWorkspaceIngressEvent> DrainPendingEvents ()
    {
        lock (_gate)
        {
            var drained = new MinecraftWorkspaceIngressEvent[_pendingEvents.Count];
            for (int index = 0; index < drained.Length; index++)
            {
                drained[index] = _pendingEvents.Dequeue();
            }

            return Array.AsReadOnly(drained);
        }
    }

    /// <summary>Stops and disposes all sessions once while retaining their final events.</summary>
    public void Stop () => Dispose();

    public void Dispose ()
    {
        lock (_reconcileGate)
        {
            SourceHandle[] sessions;
            lock (_gate)
            {
                if (_disposeComplete)
                {
                    return;
                }

                _disposeStarted = true;
                sessions = _sessions.Values
                    .OrderBy(handle => handle.Source.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(handle => handle.Source.RelativePath, StringComparer.Ordinal)
                    .ToArray();
            }

            foreach (SourceHandle handle in sessions)
            {
                DisposeHandle(handle, removeRuntimeState: false);
            }

            lock (_gate)
            {
                _sources.Clear();
                _disposeComplete = true;
            }
        }
    }

    private void StartSource (DiscoveredSourceFile source)
    {
        IMinecraftWorkspaceLiveSourceSession? session = null;
        SourceHandle? handle = null;
        bool created = false;

        try
        {
            session = _sessionFactory.Create(source);
            if (session is null)
            {
                throw new InvalidOperationException();
            }

            created = true;
            handle = new SourceHandle(source, session);
            handle.Handler = (sender, args) => OnSessionEvents(handle, sender, args);

            lock (_gate)
            {
                _sessions.Add(source.FileId, handle);
            }

            session.EventsProduced += handle.Handler;
            session.StartMonitoring();

            lock (_gate)
            {
                if (_sessions.TryGetValue(source.FileId, out SourceHandle? current) && ReferenceEquals(current, handle))
                {
                    _sources[source.FileId] = new MinecraftWorkspaceSourceRuntimeState(
                        source,
                        MinecraftWorkspaceSourceStatus.Active,
                        Reason: null,
                        EmittedEventCount: handle.EmittedEventCount);

                    if (source.Family == MinecraftSourceFamily.Yeezus &&
                        source.SegmentRole == MinecraftSourceSegmentRole.Primary)
                    {
                        _activatedPrimarySourceIds.Add(source.SourceId);
                    }
                }
            }
        }
        catch (Exception)
        {
            if (handle is not null)
            {
                DisposeHandle(handle, removeRuntimeState: false);
            }
            else if (session is not null)
            {
                TryDispose(session);
            }

            lock (_gate)
            {
                _sources[source.FileId] = new MinecraftWorkspaceSourceRuntimeState(
                    source,
                    MinecraftWorkspaceSourceStatus.Faulted,
                    created
                        ? MinecraftWorkspaceSourceReason.SessionStartFailed
                        : MinecraftWorkspaceSourceReason.SessionCreationFailed,
                    handle?.EmittedEventCount ?? 0);
            }
        }
    }

    private void OnSessionEvents (
        SourceHandle handle,
        object? sender,
        MinecraftLiveSourceEventsProducedEventArgs args)
    {
        if (!ReferenceEquals(sender, handle.Session))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposeComplete ||
                !_sessions.TryGetValue(handle.Source.FileId, out SourceHandle? current) ||
                !ReferenceEquals(current, handle))
            {
                return;
            }

            foreach (NormalizedLogEvent parsedEvent in args.Events)
            {
                long ingressSequence = _nextIngressSequence;
                _nextIngressSequence = checked(_nextIngressSequence + 1);
                _pendingEvents.Enqueue(new MinecraftWorkspaceIngressEvent(
                    ingressSequence,
                    handle.Source.WorkspaceId,
                    handle.Source.SourceId,
                    handle.Source.FileId,
                    handle.Source.SegmentRole,
                    parsedEvent));
                handle.EmittedEventCount = checked(handle.EmittedEventCount + 1);
            }

            if (_sources.TryGetValue(handle.Source.FileId, out MinecraftWorkspaceSourceRuntimeState? state))
            {
                _sources[handle.Source.FileId] = state with { EmittedEventCount = handle.EmittedEventCount };
            }
        }
    }

    private void DisposeHandle (SourceHandle handle, bool removeRuntimeState)
    {
        TryDispose(handle.Session);

        try
        {
            if (handle.Handler is not null)
            {
                handle.Session.EventsProduced -= handle.Handler;
            }
        }
        catch (Exception)
        {
            // A faulty source cannot prevent the remaining workspace sessions from stopping.
        }

        lock (_gate)
        {
            if (_sessions.TryGetValue(handle.Source.FileId, out SourceHandle? current) && ReferenceEquals(current, handle))
            {
                _sessions.Remove(handle.Source.FileId);
            }

            if (removeRuntimeState &&
                _sources.TryGetValue(handle.Source.FileId, out MinecraftWorkspaceSourceRuntimeState? state) &&
                ReferenceEquals(state.Source, handle.Source))
            {
                _sources.Remove(handle.Source.FileId);
            }
        }
    }

    private static void TryDispose (IMinecraftWorkspaceLiveSourceSession session)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception)
        {
            // Session teardown remains isolated to its physical source.
        }
    }

    private static bool RequiresDeferredHandoff (
        DiscoveredSourceFile source,
        bool isInitialSnapshot,
        ISet<string> observedActiveParts,
        ISet<string> activatedPrimaries)
    {
        if (isInitialSnapshot)
        {
            return false;
        }

        // Only predecessor history from an earlier reconcile defers a successor. This keeps
        // historical Rotated/Final files already in the first snapshot valid independent inputs.
        return source.Family switch
        {
            MinecraftSourceFamily.Yeezus =>
                source.SegmentRole == MinecraftSourceSegmentRole.Rotated &&
                activatedPrimaries.Contains(source.SourceId),
            MinecraftSourceFamily.CactusMonitor =>
                source.SegmentRole == MinecraftSourceSegmentRole.Final &&
                observedActiveParts.Contains(source.SourceId),
            _ => false
        };
    }

    private sealed class SourceHandle
    {
        public SourceHandle (DiscoveredSourceFile source, IMinecraftWorkspaceLiveSourceSession session)
        {
            Source = source;
            Session = session;
        }

        public DiscoveredSourceFile Source { get; }

        public IMinecraftWorkspaceLiveSourceSession Session { get; }

        public EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? Handler { get; set; }

        public long EmittedEventCount { get; set; }
    }
}

#pragma warning restore CA1031
