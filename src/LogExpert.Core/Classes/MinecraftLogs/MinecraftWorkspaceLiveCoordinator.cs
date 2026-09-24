namespace LogExpert.Core.Classes.MinecraftLogs;

#pragma warning disable CA1031 // Per-source creation, start, handoff, and teardown failures stay isolated.

public enum MinecraftWorkspaceSourceStatus
{
    Active,
    ImmutableComplete,
    DeferredHandoff,
    HandoffFaulted,
    Faulted
}

public enum MinecraftWorkspaceSourceReason
{
    RequiresSegmentHandoff,
    SuccessorTooShort,
    UnsafeSuccessorLength,
    AmbiguousHandoff,
    SessionCreationFailed,
    SessionStartFailed,
    HandoffStartFailed
}

/// <summary>Immutable runtime state for one discovered physical source.</summary>
public sealed record MinecraftWorkspaceSourceRuntimeState (
    DiscoveredSourceFile Source,
    MinecraftWorkspaceSourceStatus Status,
    MinecraftWorkspaceSourceReason? Reason,
    long EmittedEventCount,
    long Generation = 1,
    long NextSourceLocalSequence = 1,
    MinecraftSourceHandoffCheckpoint? Checkpoint = null);

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
    private readonly Dictionary<string, SourceProgress> _fileProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingHandoff> _pendingCfmHandoffs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingHandoff> _pendingYeezusHandoffs = new(StringComparer.Ordinal);
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
                if (handle.Source.Family == MinecraftSourceFamily.CactusMonitor &&
                    handle.Source.SegmentRole == MinecraftSourceSegmentRole.ActivePart)
                {
                    CaptureCfmHandoffAndDispose(handle);
                }
                else if (handle.Source.Family == MinecraftSourceFamily.Yeezus &&
                    handle.Source.SegmentRole == MinecraftSourceSegmentRole.Primary &&
                    snapshot.Files.Any(source =>
                        source.Family == MinecraftSourceFamily.Yeezus &&
                        source.SegmentRole == MinecraftSourceSegmentRole.Rotated &&
                        source.SourceId == handle.Source.SourceId))
                {
                    CaptureYeezusHandoffAndDispose(handle);
                }
                else
                {
                    DisposeHandle(handle, removeRuntimeState: true);
                }
            }

            lock (_gate)
            {
                foreach (string fileId in _sources.Keys.Where(fileId => !presentFileIds.Contains(fileId)).ToArray())
                {
                    _sources.Remove(fileId);
                }

                RemoveMissingSuccessors(_pendingCfmHandoffs, presentFileIds);
                RemoveMissingSuccessors(_pendingYeezusHandoffs, presentFileIds);
            }

            ProcessPendingYeezusHandoffs(snapshot.Files);
            ProcessPendingCfmHandoffs(snapshot.Files);

            // Discovery already returns a stable path order. Historical Rotated/Final
            // segments start as immutable reads on the first snapshot.
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
                    SetRuntimeState(
                        source,
                        MinecraftWorkspaceSourceStatus.DeferredHandoff,
                        MinecraftWorkspaceSourceReason.RequiresSegmentHandoff,
                        emittedEventCount: 0);
                    continue;
                }

                MinecraftWorkspaceSourceReadRequest readRequest = CreateDefaultReadRequest(source);
                StartSource(source, readRequest, checkpoint: null);
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

    /// <summary>Returns a stable immutable snapshot of current active, completed, deferred, and faulted sources.</summary>
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
                _pendingCfmHandoffs.Clear();
                _pendingYeezusHandoffs.Clear();
                _disposeComplete = true;
            }
        }
    }

    private void CaptureCfmHandoffAndDispose (SourceHandle handle)
    {
        MinecraftSourceHandoffCheckpoint? checkpoint = null;
        if (handle.Session is IMinecraftWorkspaceSourceHandoffSession handoffSession)
        {
            try
            {
                checkpoint = handoffSession.CaptureAndCloseForHandoff();
            }
            catch (Exception)
            {
                // A checkpoint already captured by the reader callback remains usable.
            }
        }

        if (checkpoint is not null)
        {
            StorePendingHandoff(
                _pendingCfmHandoffs,
                handle.Source.SourceId,
                new PendingHandoff(
                    checkpoint,
                    checkpoint.ConsumedByteFrontier,
                    SuccessorLengthAtBoundary: null,
                    SuccessorFileId: null,
                    IsAmbiguous: false));
        }

        DisposeHandle(handle, removeRuntimeState: true);
    }

    private void CaptureYeezusHandoffAndDispose (SourceHandle handle)
    {
        MinecraftSourceHandoffCheckpoint? checkpoint = null;
        if (handle.Session is IMinecraftWorkspaceSourceHandoffSession handoffSession)
        {
            try
            {
                checkpoint = handoffSession.CaptureAndCloseForHandoff();
            }
            catch (Exception)
            {
                // The reader callback may already have captured the checkpoint.
            }
        }

        if (checkpoint is not null)
        {
            StorePendingHandoff(
                _pendingYeezusHandoffs,
                handle.Source.SourceId,
                new PendingHandoff(
                    checkpoint,
                    checkpoint.ConsumedByteFrontier,
                    SuccessorLengthAtBoundary: null,
                    SuccessorFileId: null,
                    IsAmbiguous: false));
        }

        DisposeHandle(handle, removeRuntimeState: true);
    }

    private void ProcessPendingYeezusHandoffs (IReadOnlyList<DiscoveredSourceFile> discoveredFiles)
    {
        PendingHandoffEntry[] pending;
        lock (_gate)
        {
            pending = _pendingYeezusHandoffs
                .Select(pair => new PendingHandoffEntry(pair.Key, pair.Value))
                .ToArray();
        }

        foreach (PendingHandoffEntry entry in pending)
        {
            DiscoveredSourceFile? successor = discoveredFiles.FirstOrDefault(source =>
                source.Family == MinecraftSourceFamily.Yeezus &&
                source.SegmentRole == MinecraftSourceSegmentRole.Rotated &&
                source.SourceId == entry.Pending.Checkpoint.SourceId);
            if (successor is null)
            {
                continue;
            }

            PendingHandoff current = entry.Pending with { SuccessorFileId = successor.FileId };
            StorePendingHandoff(_pendingYeezusHandoffs, entry.Key, current);
            if (current.IsAmbiguous)
            {
                SetHandoffFaulted(successor, current.Checkpoint, MinecraftWorkspaceSourceReason.AmbiguousHandoff);
                continue;
            }

            long? successorLength = TryGetLength(successor.FullPath);
            long requiredLength = Math.Max(
                current.Checkpoint.ConsumedByteFrontier,
                current.PredecessorObservedLength);
            if (current.SuccessorLengthAtBoundary is long boundaryLength)
            {
                requiredLength = Math.Max(requiredLength, boundaryLength);
            }
            if (successorLength is null || successorLength < requiredLength ||
                successorLength < current.Checkpoint.ReplayStartByteOffset)
            {
                MinecraftWorkspaceSourceReason reason = successorLength is not null &&
                    successorLength < Math.Max(requiredLength, current.Checkpoint.ReplayStartByteOffset)
                        ? MinecraftWorkspaceSourceReason.SuccessorTooShort
                        : MinecraftWorkspaceSourceReason.UnsafeSuccessorLength;
                SetHandoffFaulted(successor, current.Checkpoint, reason);
                continue;
            }

            MinecraftWorkspaceSourceReadRequest readRequest = CreateHandoffReadRequest(successor, current.Checkpoint);
            if (StartSource(successor, readRequest, current.Checkpoint))
            {
                RemovePending(_pendingYeezusHandoffs, entry.Key, current);
            }
        }
    }

    private void ProcessPendingCfmHandoffs (IReadOnlyList<DiscoveredSourceFile> discoveredFiles)
    {
        PendingHandoffEntry[] pending;
        lock (_gate)
        {
            pending = _pendingCfmHandoffs
                .Select(pair => new PendingHandoffEntry(pair.Key, pair.Value))
                .ToArray();
        }

        foreach (PendingHandoffEntry entry in pending)
        {
            DiscoveredSourceFile? successor = discoveredFiles.FirstOrDefault(source =>
                source.Family == MinecraftSourceFamily.CactusMonitor &&
                source.SegmentRole == MinecraftSourceSegmentRole.Final &&
                source.SourceId == entry.Pending.Checkpoint.SourceId);
            if (successor is null)
            {
                continue;
            }

            PendingHandoff current = entry.Pending with { SuccessorFileId = successor.FileId };
            StorePendingHandoff(_pendingCfmHandoffs, entry.Key, current);
            if (current.IsAmbiguous)
            {
                SetHandoffFaulted(successor, current.Checkpoint, MinecraftWorkspaceSourceReason.AmbiguousHandoff);
                continue;
            }

            long? successorLength = TryGetLength(successor.FullPath);
            long requiredLength = Math.Max(
                current.Checkpoint.ConsumedByteFrontier,
                current.Checkpoint.ReplayStartByteOffset);
            if (successorLength is null || successorLength < requiredLength)
            {
                SetHandoffFaulted(
                    successor,
                    current.Checkpoint,
                    successorLength is not null
                        ? MinecraftWorkspaceSourceReason.SuccessorTooShort
                        : MinecraftWorkspaceSourceReason.UnsafeSuccessorLength);
                continue;
            }

            MinecraftWorkspaceSourceReadRequest readRequest = CreateHandoffReadRequest(successor, current.Checkpoint);
            if (StartSource(successor, readRequest, current.Checkpoint))
            {
                RemovePending(_pendingCfmHandoffs, entry.Key, current);
            }
        }
    }

    private bool StartSource (
        DiscoveredSourceFile source,
        MinecraftWorkspaceSourceReadRequest requestedReadRequest,
        MinecraftSourceHandoffCheckpoint? checkpoint)
    {
        bool acceptsReadRequest = _sessionFactory is IMinecraftWorkspaceLiveSourceSessionFactoryWithReadRequest;
        bool immutableSnapshot = requestedReadRequest.ImmutableSnapshot && acceptsReadRequest;

        if ((checkpoint is not null || requestedReadRequest.ImmutableSnapshot) && !acceptsReadRequest)
        {
            if (checkpoint is not null)
            {
                SetHandoffFaulted(source, checkpoint, MinecraftWorkspaceSourceReason.HandoffStartFailed);
            }
            else
            {
                SetRuntimeState(
                    source,
                    MinecraftWorkspaceSourceStatus.Faulted,
                    MinecraftWorkspaceSourceReason.SessionCreationFailed,
                    emittedEventCount: 0);
            }

            return false;
        }

        MinecraftWorkspaceSourceReadRequest readRequest = acceptsReadRequest
            ? requestedReadRequest
            : MinecraftWorkspaceSourceReadRequest.Live(
                requestedReadRequest.InitialGeneration,
                requestedReadRequest.InitialSourceLocalSequence);

        IMinecraftWorkspaceLiveSourceSession session;
        try
        {
            session = acceptsReadRequest
                ? ((IMinecraftWorkspaceLiveSourceSessionFactoryWithReadRequest)_sessionFactory).Create(source, readRequest)
                : _sessionFactory.Create(source);
            if (session is null)
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception)
        {
            if (checkpoint is not null)
            {
                SetHandoffFaulted(source, checkpoint, MinecraftWorkspaceSourceReason.HandoffStartFailed);
            }
            else
            {
                SetRuntimeState(
                    source,
                    MinecraftWorkspaceSourceStatus.Faulted,
                    MinecraftWorkspaceSourceReason.SessionCreationFailed,
                    emittedEventCount: 0);
            }

            return false;
        }

        var handle = new SourceHandle(source, session, readRequest, immutableSnapshot);
        handle.Handler = (sender, args) => OnSessionEvents(handle, sender, args);
        handle.HandoffHandler = (sender, args) => OnHandoffCheckpoint(handle, sender, args);

        try
        {
            lock (_gate)
            {
                _sessions.Add(source.FileId, handle);
            }

            session.EventsProduced += handle.Handler;
            if (session is IMinecraftWorkspaceSourceHandoffSession handoffSession)
            {
                handoffSession.HandoffCheckpointCaptured += handle.HandoffHandler;
            }

            session.StartMonitoring();

            if (immutableSnapshot && session is IMinecraftWorkspaceLiveSourceSessionProgress progress &&
                !progress.IsImmutableComplete)
            {
                throw new InvalidOperationException();
            }

            CaptureProgress(handle);
            if (immutableSnapshot)
            {
                DisposeHandle(handle, removeRuntimeState: false);
                SetRuntimeState(
                    source,
                    MinecraftWorkspaceSourceStatus.ImmutableComplete,
                    reason: null,
                    handle.EmittedEventCount,
                    checkpoint);
            }
            else
            {
                SetRuntimeState(
                    source,
                    MinecraftWorkspaceSourceStatus.Active,
                    reason: null,
                    handle.EmittedEventCount,
                    checkpoint: null);
                if (source.Family == MinecraftSourceFamily.Yeezus &&
                    source.SegmentRole == MinecraftSourceSegmentRole.Primary)
                {
                    lock (_gate)
                    {
                        _activatedPrimarySourceIds.Add(source.SourceId);
                    }
                }
            }

            return true;
        }
        catch (Exception)
        {
            DisposeHandle(handle, removeRuntimeState: false);

            if (checkpoint is not null)
            {
                SetHandoffFaulted(source, checkpoint, MinecraftWorkspaceSourceReason.HandoffStartFailed);
            }
            else
            {
                SetRuntimeState(
                    source,
                    MinecraftWorkspaceSourceStatus.Faulted,
                    MinecraftWorkspaceSourceReason.SessionStartFailed,
                    handle.EmittedEventCount);
            }

            return false;
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

            CaptureProgress(handle);
            if (_sources.TryGetValue(handle.Source.FileId, out MinecraftWorkspaceSourceRuntimeState? state))
            {
                _sources[handle.Source.FileId] = state with
                {
                    EmittedEventCount = handle.EmittedEventCount,
                    Generation = handle.CurrentGeneration,
                    NextSourceLocalSequence = handle.NextSourceLocalSequence
                };
            }
        }
    }

    private void OnHandoffCheckpoint (
        SourceHandle handle,
        object? sender,
        MinecraftWorkspaceHandoffCheckpointEventArgs args)
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

            var pending = new PendingHandoff(
                args.Checkpoint,
                args.PredecessorFileLength,
                args.SuccessorFileLength,
                SuccessorFileId: null,
                IsAmbiguous: false);
            Dictionary<string, PendingHandoff> target = args.TransitionKind switch
            {
                MinecraftHandoffTransitionKind.YeezusPrimaryToRotated => _pendingYeezusHandoffs,
                MinecraftHandoffTransitionKind.CactusMonitorActivePartToFinal => _pendingCfmHandoffs,
                _ => throw new ArgumentOutOfRangeException(nameof(args))
            };
            StorePendingHandoff(target, handle.Source.SourceId, pending);
            CaptureProgress(handle);
            if (_sources.TryGetValue(handle.Source.FileId, out MinecraftWorkspaceSourceRuntimeState? state))
            {
                _sources[handle.Source.FileId] = state with
                {
                    Generation = handle.CurrentGeneration,
                    NextSourceLocalSequence = handle.NextSourceLocalSequence
                };
            }
        }
    }

    private void DisposeHandle (SourceHandle handle, bool removeRuntimeState)
    {
        CaptureProgress(handle);
        TryDispose(handle.Session);
        CaptureProgress(handle);

        try
        {
            if (handle.Handler is not null)
            {
                handle.Session.EventsProduced -= handle.Handler;
            }

            if (handle.HandoffHandler is not null &&
                handle.Session is IMinecraftWorkspaceSourceHandoffSession handoffSession)
            {
                handoffSession.HandoffCheckpointCaptured -= handle.HandoffHandler;
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

    private void CaptureProgress (SourceHandle handle)
    {
        if (handle.Session is not IMinecraftWorkspaceLiveSourceSessionProgress progress ||
            (handle.ImmutableSnapshot && !progress.IsImmutableComplete))
        {
            return;
        }

        handle.CurrentGeneration = progress.CurrentFile.Generation;
        handle.NextSourceLocalSequence = progress.NextSourceLocalSequence;
        lock (_gate)
        {
            _fileProgress[handle.Source.FileId] = new SourceProgress(
                handle.CurrentGeneration,
                handle.NextSourceLocalSequence);
        }
    }

    private void SetRuntimeState (
        DiscoveredSourceFile source,
        MinecraftWorkspaceSourceStatus status,
        MinecraftWorkspaceSourceReason? reason,
        long emittedEventCount,
        MinecraftSourceHandoffCheckpoint? checkpoint = null)
    {
        SourceProgress progress = GetProgress(source.FileId);
        lock (_gate)
        {
            _sources[source.FileId] = new MinecraftWorkspaceSourceRuntimeState(
                source,
                status,
                reason,
                emittedEventCount,
                progress.Generation,
                progress.NextSourceLocalSequence,
                checkpoint);
        }
    }

    private void SetHandoffFaulted (
        DiscoveredSourceFile source,
        MinecraftSourceHandoffCheckpoint checkpoint,
        MinecraftWorkspaceSourceReason reason)
    {
        long eventCount;
        lock (_gate)
        {
            eventCount = _sources.TryGetValue(source.FileId, out MinecraftWorkspaceSourceRuntimeState? state)
                ? state.EmittedEventCount
                : 0;
        }

        SourceProgress progress = GetProgress(source.FileId);
        lock (_gate)
        {
            _sources[source.FileId] = new MinecraftWorkspaceSourceRuntimeState(
                source,
                MinecraftWorkspaceSourceStatus.HandoffFaulted,
                reason,
                eventCount,
                progress.Generation,
                progress.NextSourceLocalSequence,
                checkpoint);
        }
    }

    private MinecraftWorkspaceSourceReadRequest CreateDefaultReadRequest (DiscoveredSourceFile source)
    {
        SourceProgress next = GetNextProgress(source.FileId);
        return source.SegmentRole is MinecraftSourceSegmentRole.Rotated or MinecraftSourceSegmentRole.Final
            ? MinecraftWorkspaceSourceReadRequest.Snapshot(
                generation: next.Generation,
                nextSequence: next.NextSourceLocalSequence)
            : MinecraftWorkspaceSourceReadRequest.Live(next.Generation, next.NextSourceLocalSequence);
    }

    private MinecraftWorkspaceSourceReadRequest CreateHandoffReadRequest (
        DiscoveredSourceFile successor,
        MinecraftSourceHandoffCheckpoint checkpoint)
    {
        SourceProgress next = GetNextProgress(successor.FileId);
        return MinecraftWorkspaceSourceReadRequest.Snapshot(
            checkpoint.ReplayStartByteOffset,
            checkpoint.ReplayStartPhysicalLineNumber,
            next.Generation,
            checkpoint.NextSourceLocalSequence);
    }

    private SourceProgress GetNextProgress (string fileId)
    {
        lock (_gate)
        {
            return _fileProgress.TryGetValue(fileId, out SourceProgress? progress)
                ? progress with { Generation = checked(progress.Generation + 1) }
                : new SourceProgress(1, 1);
        }
    }

    private SourceProgress GetProgress (string fileId)
    {
        lock (_gate)
        {
            return _fileProgress.TryGetValue(fileId, out SourceProgress? progress)
                ? progress
                : new SourceProgress(1, 1);
        }
    }

    private void StorePendingHandoff (
        Dictionary<string, PendingHandoff> target,
        string key,
        PendingHandoff pending)
    {
        lock (_gate)
        {
            if (target.TryGetValue(key, out PendingHandoff? existing))
            {
                if (existing.Checkpoint == pending.Checkpoint)
                {
                    target[key] = existing with
                    {
                        SuccessorFileId = pending.SuccessorFileId ?? existing.SuccessorFileId
                    };
                    return;
                }

                target[key] = existing with { IsAmbiguous = true };
                return;
            }

            target[key] = pending;
        }
    }

    private void RemovePending (
        Dictionary<string, PendingHandoff> target,
        string key,
        PendingHandoff expected)
    {
        lock (_gate)
        {
            if (target.TryGetValue(key, out PendingHandoff? current) && current.Checkpoint == expected.Checkpoint)
            {
                target.Remove(key);
            }
        }
    }

    private static void RemoveMissingSuccessors (
        Dictionary<string, PendingHandoff> target,
        ISet<string> presentFileIds)
    {
        foreach (string key in target.Keys.ToArray())
        {
            PendingHandoff pending = target[key];
            if (pending.SuccessorFileId is not null && !presentFileIds.Contains(pending.SuccessorFileId))
            {
                target.Remove(key);
            }
        }
    }

    private static long? TryGetLength (string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists ? fileInfo.Length : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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

    private sealed class SourceHandle
    {
        public SourceHandle (
            DiscoveredSourceFile source,
            IMinecraftWorkspaceLiveSourceSession session,
            MinecraftWorkspaceSourceReadRequest readRequest,
            bool immutableSnapshot)
        {
            Source = source;
            Session = session;
            ImmutableSnapshot = immutableSnapshot;
            CurrentGeneration = readRequest.InitialGeneration;
            NextSourceLocalSequence = readRequest.InitialSourceLocalSequence;
        }

        public DiscoveredSourceFile Source { get; }

        public IMinecraftWorkspaceLiveSourceSession Session { get; }

        public bool ImmutableSnapshot { get; }

        public EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? Handler { get; set; }

        public EventHandler<MinecraftWorkspaceHandoffCheckpointEventArgs>? HandoffHandler { get; set; }

        public long EmittedEventCount { get; set; }

        public long CurrentGeneration { get; set; }

        public long NextSourceLocalSequence { get; set; }
    }

    private sealed record SourceProgress (long Generation, long NextSourceLocalSequence);

    private sealed record PendingHandoff (
        MinecraftSourceHandoffCheckpoint Checkpoint,
        long PredecessorObservedLength,
        long? SuccessorLengthAtBoundary,
        string? SuccessorFileId,
        bool IsAmbiguous);

    private sealed record PendingHandoffEntry (string Key, PendingHandoff Pending);
}

#pragma warning restore CA1031
