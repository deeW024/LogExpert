using System.Security.Cryptography;

namespace LogExpert.Core.Classes.MinecraftLogs;

#pragma warning disable CA1031 // Per-source creation, start, handoff, and teardown failures stay isolated.

public enum MinecraftWorkspaceSourceStatus
{
    Active,
    ImmutableComplete,
    DeferredHandoff,
    HandoffFaulted,
    Faulted,
    Disabled
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
    NormalizedLogEvent Event,
    DateTimeOffset IngestedAtUtc);

/// <summary>A discovered physical segment and its current state within one logical source.</summary>
public sealed record MinecraftWorkspaceLogicalSourceSegmentSnapshot (
    DiscoveredSourceFile Source,
    MinecraftWorkspaceSourceStatus Status,
    MinecraftWorkspaceSourceReason? Reason,
    long EmittedEventCount,
    bool HasResumeCheckpoint);

/// <summary>UI-independent state for a logical source, keyed by stable SourceId.</summary>
public sealed record MinecraftWorkspaceLogicalSourceSnapshot (
    string SourceId,
    MinecraftSourceFamily Family,
    string DisplayLabel,
    bool IsEnabled,
    IReadOnlyList<MinecraftWorkspaceLogicalSourceSegmentSnapshot> Segments,
    MinecraftWorkspaceSourceStatus Status,
    long EmittedEventCount,
    bool HasResumeCheckpoint);

/// <summary>
/// Reconciles YEE-39 physical discovery with YEE-42 single-file sessions. The ingress queue
/// is intentionally unbounded and no-drop in this slice; a memory/backpressure budget is a
/// later performance limitation. Reconcile is explicit and adds no discovery timer.
/// </summary>
public sealed class MinecraftWorkspaceLiveCoordinator : IDisposable
{
    private readonly MinecraftSourceDiscovery _discovery;
    private readonly IMinecraftWorkspaceLiveSourceSessionFactory _sessionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly object _reconcileGate = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceHandle> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MinecraftWorkspaceSourceRuntimeState> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceProgress> _fileProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fileEventCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiscoveredSourceFile> _discoveredSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SuspendedSource> _suspendedSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabledSourceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedResumeFileIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingHandoff> _pendingCfmHandoffs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingHandoff> _pendingYeezusHandoffs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedActivePartSourceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activatedPrimarySourceIds = new(StringComparer.Ordinal);
    private readonly Queue<MinecraftWorkspaceIngressEvent> _pendingEvents = new();
    private long _nextIngressSequence = 1;
    private DateTimeOffset? _lastIngestedAtUtc;
    private bool _hasReconciled;
    private bool _disposeStarted;
    private bool _disposeComplete;

    public MinecraftWorkspaceLiveCoordinator (
        MinecraftSourceDiscovery discovery,
        IMinecraftWorkspaceLiveSourceSessionFactory sessionFactory,
        TimeProvider? timeProvider = null,
        IEnumerable<string>? initiallyDisabledSourceIds = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(sessionFactory);

        _discovery = discovery;
        _sessionFactory = sessionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (initiallyDisabledSourceIds is not null)
        {
            _disabledSourceIds.UnionWith(initiallyDisabledSourceIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        }
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
                _discoveredSources.Clear();
                foreach (DiscoveredSourceFile source in snapshot.Files)
                {
                    _discoveredSources[source.FileId] = source;
                }
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

            ReconcileSuspendedSources(snapshot.Files);

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

                if (!IsSourceEnabled(source.SourceId))
                {
                    SetRuntimeState(
                        source,
                        MinecraftWorkspaceSourceStatus.Disabled,
                        reason: null,
                        GetEventCount(source.FileId),
                        GetSuspendedCheckpoint(source.SourceId));
                    continue;
                }

                if (IsResumeBlocked(source.FileId))
                {
                    continue;
                }

                if (TryResumeSuspendedSource(source))
                {
                    continue;
                }

                if (HasSuspendedSource(source.SourceId))
                {
                    // A different physical path may only be opened after its deterministic
                    // YEE-45 successor has passed the saved-prefix check.
                    continue;
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

    public IReadOnlyList<MinecraftWorkspaceLogicalSourceSnapshot> GetLogicalSourceSnapshot ()
    {
        lock (_gate)
        {
            MinecraftWorkspaceLogicalSourceSnapshot[] snapshot = _discoveredSources.Values
                .GroupBy(source => source.SourceId, StringComparer.Ordinal)
                .Select(group => CreateLogicalSourceSnapshot(group.OrderBy(source => source.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(source => source.RelativePath, StringComparer.Ordinal).ToArray()))
                .OrderBy(source => source.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.SourceId, StringComparer.Ordinal)
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }

    public bool IsSourceEnabled (string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        lock (_gate)
        {
            return !_disabledSourceIds.Contains(sourceId);
        }
    }

    /// <summary>Changes reader ownership for a complete logical source without finalizing pending records.</summary>
    public void SetSourceEnabled (string sourceId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        lock (_reconcileGate)
        {
            SourceHandle[] toSuspend;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeStarted, this);
                if (enabled)
                {
                    _disabledSourceIds.Remove(sourceId);
                    foreach (string fileId in _sources
                        .Where(pair => pair.Value.Source.SourceId == sourceId && pair.Value.Status == MinecraftWorkspaceSourceStatus.Disabled)
                        .Select(pair => pair.Key)
                        .ToArray())
                    {
                        _sources.Remove(fileId);
                    }

                    toSuspend = [];
                }
                else
                {
                    _disabledSourceIds.Add(sourceId);
                    toSuspend = _sessions.Values
                        .Where(handle => string.Equals(handle.Source.SourceId, sourceId, StringComparison.Ordinal))
                        .OrderBy(handle => handle.Source.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(handle => handle.Source.RelativePath, StringComparer.Ordinal)
                        .ToArray();
                }
            }

            foreach (SourceHandle handle in toSuspend)
            {
                SuspendSource(handle);
            }

            if (enabled)
            {
                Reconcile();
            }
            else
            {
                lock (_gate)
                {
                    foreach (DiscoveredSourceFile source in _discoveredSources.Values.Where(source => source.SourceId == sourceId))
                    {
                        if (_sources.TryGetValue(source.FileId, out MinecraftWorkspaceSourceRuntimeState? existing) &&
                            existing.Status == MinecraftWorkspaceSourceStatus.ImmutableComplete)
                        {
                            continue;
                        }

                        MinecraftSourceHandoffCheckpoint? checkpoint = GetSuspendedCheckpointUnsafe(sourceId);
                        SourceProgress progress = _fileProgress.TryGetValue(source.FileId, out SourceProgress? saved)
                            ? saved
                            : new SourceProgress(1, 1);
                        long count = _fileEventCounts.TryGetValue(source.FileId, out long savedCount) ? savedCount : 0;
                        _sources[source.FileId] = new MinecraftWorkspaceSourceRuntimeState(
                            source,
                            MinecraftWorkspaceSourceStatus.Disabled,
                            Reason: null,
                            count,
                            progress.Generation,
                            progress.NextSourceLocalSequence,
                            checkpoint);
                    }
                }
            }
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

    private void SuspendSource (SourceHandle handle)
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
                // A source that cannot provide a replay point is kept blocked on resume.
            }
        }

        long observedLength = TryGetLength(handle.Source.FullPath) ?? checkpoint?.ConsumedByteFrontier ?? 0;
        byte[]? prefixHash = TryGetPrefixHash(handle.Source.FullPath, observedLength);
        lock (_gate)
        {
            _suspendedSources[handle.Source.FileId] = new SuspendedSource(
                handle.Source,
                checkpoint,
                observedLength,
                prefixHash);
        }

        DisposeHandle(handle, removeRuntimeState: false);
    }

    private void ReconcileSuspendedSources (IReadOnlyList<DiscoveredSourceFile> discoveredFiles)
    {
        SuspendedSource[] suspended;
        lock (_gate)
        {
            suspended = _suspendedSources.Values.ToArray();
        }

        var filesById = discoveredFiles.ToDictionary(source => source.FileId, StringComparer.Ordinal);
        foreach (SuspendedSource state in suspended)
        {
            if (state.Checkpoint is null)
            {
                lock (_gate)
                {
                    _blockedResumeFileIds.Add(state.Source.FileId);
                }

                continue;
            }

            bool originalStillPresent = filesById.TryGetValue(state.Source.FileId, out DiscoveredSourceFile? original);
            if (originalStillPresent && HasMatchingSuspendedPrefix(state, original!.FullPath))
            {
                lock (_gate)
                {
                    _blockedResumeFileIds.Remove(state.Source.FileId);
                }

                continue;
            }

            if (originalStillPresent)
            {
                lock (_gate)
                {
                    _blockedResumeFileIds.Add(state.Source.FileId);
                }
            }

            DiscoveredSourceFile? successor = FindDeterministicSuccessor(state.Source, discoveredFiles);
            if (successor is null)
            {
                if (originalStillPresent && IsSourceEnabled(state.Source.SourceId))
                {
                    SetHandoffFaulted(
                        original!,
                        state.Checkpoint,
                        MinecraftWorkspaceSourceReason.UnsafeSuccessorLength);
                }

                continue;
            }

            if (!HasMatchingSuspendedPrefix(state, successor.FullPath))
            {
                if (IsSourceEnabled(state.Source.SourceId))
                {
                    SetHandoffFaulted(
                        successor,
                        state.Checkpoint,
                        MinecraftWorkspaceSourceReason.UnsafeSuccessorLength);
                }

                continue;
            }

            Dictionary<string, PendingHandoff> handoffs = state.Source.Family == MinecraftSourceFamily.Yeezus
                ? _pendingYeezusHandoffs
                : _pendingCfmHandoffs;
            StorePendingHandoff(
                handoffs,
                state.Source.SourceId,
                new PendingHandoff(
                    state.Checkpoint,
                    Math.Max(state.ObservedLength, state.Checkpoint.ConsumedByteFrontier),
                    SuccessorLengthAtBoundary: null,
                    successor.FileId,
                    IsAmbiguous: false));
        }
    }

    private bool TryResumeSuspendedSource (DiscoveredSourceFile source)
    {
        SuspendedSource? suspended;
        lock (_gate)
        {
            suspended = _suspendedSources.GetValueOrDefault(source.FileId);
        }

        if (suspended is null)
        {
            return false;
        }

        MinecraftSourceHandoffCheckpoint? checkpoint = suspended.Checkpoint;
        if (checkpoint is null || !HasMatchingSuspendedPrefix(suspended, source.FullPath))
        {
            lock (_gate)
            {
                _blockedResumeFileIds.Add(source.FileId);
            }

            SetHandoffFaulted(
                source,
                checkpoint ?? CreateUnavailableCheckpoint(suspended.Source),
                MinecraftWorkspaceSourceReason.UnsafeSuccessorLength);
            return true;
        }

        long? currentLength = TryGetLength(source.FullPath);
        if (currentLength is null || currentLength < checkpoint.ConsumedByteFrontier ||
            currentLength < checkpoint.ReplayStartByteOffset)
        {
            lock (_gate)
            {
                _blockedResumeFileIds.Add(source.FileId);
            }

            SetHandoffFaulted(source, checkpoint, currentLength is null
                ? MinecraftWorkspaceSourceReason.UnsafeSuccessorLength
                : MinecraftWorkspaceSourceReason.SuccessorTooShort);
            return true;
        }

        SourceProgress next = GetNextProgress(source.FileId);
        SourceProgress previous = GetProgress(source.FileId);
        long initialSequence = Math.Max(checkpoint.NextSourceLocalSequence, previous.NextSourceLocalSequence);
        var readRequest = new MinecraftWorkspaceSourceReadRequest(
            checkpoint.ReplayStartByteOffset,
            checkpoint.ReplayStartPhysicalLineNumber,
            next.Generation,
            initialSequence);
        if (StartSource(source, readRequest, checkpoint))
        {
            lock (_gate)
            {
                _suspendedSources.Remove(source.FileId);
                _blockedResumeFileIds.Remove(source.FileId);
            }
        }

        return true;
    }

    private static bool HasMatchingSuspendedPrefix (SuspendedSource suspended, string candidatePath)
    {
        if (suspended.PrefixHash is null || TryGetLength(candidatePath) is not long candidateLength ||
            candidateLength < suspended.ObservedLength)
        {
            return false;
        }

        byte[]? candidateHash = TryGetPrefixHash(candidatePath, suspended.ObservedLength);
        return candidateHash is not null && CryptographicOperations.FixedTimeEquals(suspended.PrefixHash, candidateHash);
    }

    private static byte[]? TryGetPrefixHash (string path, long byteCount)
    {
        if (byteCount < 0)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long remaining = byteCount;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    return null;
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return hash.GetHashAndReset();
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

    private static DiscoveredSourceFile? FindDeterministicSuccessor (
        DiscoveredSourceFile predecessor,
        IReadOnlyList<DiscoveredSourceFile> discoveredFiles)
    {
        MinecraftSourceFamily family = predecessor.Family;
        MinecraftSourceSegmentRole successorRole = predecessor.SegmentRole switch
        {
            MinecraftSourceSegmentRole.Primary when family == MinecraftSourceFamily.Yeezus => MinecraftSourceSegmentRole.Rotated,
            MinecraftSourceSegmentRole.ActivePart when family == MinecraftSourceFamily.CactusMonitor => MinecraftSourceSegmentRole.Final,
            _ => MinecraftSourceSegmentRole.None
        };
        return successorRole == MinecraftSourceSegmentRole.None
            ? null
            : discoveredFiles.FirstOrDefault(source => source.SourceId == predecessor.SourceId &&
                source.Family == family && source.SegmentRole == successorRole);
    }

    private bool IsResumeBlocked (string fileId)
    {
        lock (_gate)
        {
            return _blockedResumeFileIds.Contains(fileId);
        }
    }

    private bool HasSuspendedSource (string sourceId)
    {
        lock (_gate)
        {
            return _suspendedSources.Values.Any(source => source.Source.SourceId == sourceId);
        }
    }

    private MinecraftSourceHandoffCheckpoint? GetSuspendedCheckpoint (string sourceId)
    {
        lock (_gate)
        {
            return GetSuspendedCheckpointUnsafe(sourceId);
        }
    }

    private MinecraftSourceHandoffCheckpoint? GetSuspendedCheckpointUnsafe (string sourceId) =>
        _suspendedSources.Values.FirstOrDefault(source => source.Source.SourceId == sourceId)?.Checkpoint;

    private bool IsHandoffPending (string sourceId)
    {
        lock (_gate)
        {
            return _pendingCfmHandoffs.ContainsKey(sourceId) || _pendingYeezusHandoffs.ContainsKey(sourceId);
        }
    }

    private long GetEventCount (string fileId)
    {
        lock (_gate)
        {
            return _fileEventCounts.GetValueOrDefault(fileId);
        }
    }

    private MinecraftWorkspaceLogicalSourceSnapshot CreateLogicalSourceSnapshot (IReadOnlyList<DiscoveredSourceFile> sources)
    {
        bool enabled = !_disabledSourceIds.Contains(sources[0].SourceId);
        bool hasFault = false;
        bool hasActive = false;
        bool allImmutable = true;
        MinecraftWorkspaceLogicalSourceSegmentSnapshot[] segments = sources
            .Select(source =>
            {
                MinecraftWorkspaceSourceRuntimeState? state = _sources.GetValueOrDefault(source.FileId);
                MinecraftSourceHandoffCheckpoint? checkpoint = state?.Checkpoint ??
                    _suspendedSources.Values.FirstOrDefault(saved => saved.Source.SourceId == source.SourceId)?.Checkpoint;
                MinecraftWorkspaceSourceStatus status = !enabled
                    ? MinecraftWorkspaceSourceStatus.Disabled
                    : state?.Status ?? MinecraftWorkspaceSourceStatus.DeferredHandoff;
                hasFault |= status is MinecraftWorkspaceSourceStatus.HandoffFaulted or MinecraftWorkspaceSourceStatus.Faulted;
                hasActive |= status == MinecraftWorkspaceSourceStatus.Active;
                allImmutable &= status == MinecraftWorkspaceSourceStatus.ImmutableComplete;
                return new MinecraftWorkspaceLogicalSourceSegmentSnapshot(
                    source,
                    status,
                    state?.Reason,
                    state?.EmittedEventCount ?? _fileEventCounts.GetValueOrDefault(source.FileId),
                    checkpoint is not null);
            })
            .ToArray();
        MinecraftWorkspaceSourceStatus aggregateStatus = !enabled
            ? MinecraftWorkspaceSourceStatus.Disabled
            : hasFault
                ? MinecraftWorkspaceSourceStatus.HandoffFaulted
                : hasActive
                    ? MinecraftWorkspaceSourceStatus.Active
                    : allImmutable
                        ? MinecraftWorkspaceSourceStatus.ImmutableComplete
                        : MinecraftWorkspaceSourceStatus.DeferredHandoff;
        return new MinecraftWorkspaceLogicalSourceSnapshot(
            sources[0].SourceId,
            sources[0].Family,
            CreateSourceDisplayLabel(sources),
            enabled,
            Array.AsReadOnly(segments),
            aggregateStatus,
            segments.Sum(segment => segment.EmittedEventCount),
            segments.Any(segment => segment.HasResumeCheckpoint));
    }

    private static string CreateSourceDisplayLabel (IReadOnlyList<DiscoveredSourceFile> sources) => sources[0].Family switch
    {
        MinecraftSourceFamily.Minecraft => "Minecraft · latest.log",
        MinecraftSourceFamily.Yeezus => "Yeezus",
        MinecraftSourceFamily.ReCactus => $"ReCactus · {Path.GetFileName(sources[0].RelativePath)}",
        MinecraftSourceFamily.CactusMonitor => $"Cactus Monitor · {Path.GetFileName(sources[0].RelativePath)}",
        _ => sources[0].Family.ToString()
    };

    private static MinecraftSourceHandoffCheckpoint CreateUnavailableCheckpoint (DiscoveredSourceFile source) =>
        new(source.SourceId, new FileRef(source.FileId, source.FullPath, 1), source.AdapterHint, source.SegmentRole,
            0, 1, 0, 1, ReplayStartsBeforeConsumedFrontier: false, HasUnemittedState: false, NextSourceLocalSequence: 1);

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
            if (!IsSourceEnabled(entry.Key))
            {
                continue;
            }

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

            if (!IsSuspendedCheckpointValidForSuccessor(current.Checkpoint, successor.FullPath))
            {
                SetHandoffFaulted(successor, current.Checkpoint, MinecraftWorkspaceSourceReason.UnsafeSuccessorLength);
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
                RemoveSuspendedCheckpoint(current.Checkpoint);
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
            if (!IsSourceEnabled(entry.Key))
            {
                continue;
            }

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

            if (!IsSuspendedCheckpointValidForSuccessor(current.Checkpoint, successor.FullPath))
            {
                SetHandoffFaulted(successor, current.Checkpoint, MinecraftWorkspaceSourceReason.UnsafeSuccessorLength);
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
                RemoveSuspendedCheckpoint(current.Checkpoint);
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

        var handle = new SourceHandle(source, session, readRequest, immutableSnapshot)
        {
            EmittedEventCount = GetEventCount(source.FileId)
        };
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
                DateTimeOffset ingestedAtUtc = CaptureIngestedAtUtc();
                _pendingEvents.Enqueue(new MinecraftWorkspaceIngressEvent(
                    ingressSequence,
                    handle.Source.WorkspaceId,
                    handle.Source.SourceId,
                    handle.Source.FileId,
                    handle.Source.SegmentRole,
                    parsedEvent,
                    ingestedAtUtc));
                handle.EmittedEventCount = checked(handle.EmittedEventCount + 1);
            }

            _fileEventCounts[handle.Source.FileId] = handle.EmittedEventCount;

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

    private DateTimeOffset CaptureIngestedAtUtc ()
    {
        DateTimeOffset ingestedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        if (_lastIngestedAtUtc is DateTimeOffset previous && ingestedAtUtc < previous)
        {
            ingestedAtUtc = previous;
        }

        _lastIngestedAtUtc = ingestedAtUtc;
        return ingestedAtUtc;
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
        SourceProgress successorProgress = GetNextProgress(successor.FileId);
        // Reused successor FileIds retain their prior sequence frontier across generations.
        long initialSourceLocalSequence = Math.Max(
            checkpoint.NextSourceLocalSequence,
            successorProgress.NextSourceLocalSequence);
        return MinecraftWorkspaceSourceReadRequest.Snapshot(
            checkpoint.ReplayStartByteOffset,
            checkpoint.ReplayStartPhysicalLineNumber,
            successorProgress.Generation,
            initialSourceLocalSequence);
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

    private bool IsSuspendedCheckpointValidForSuccessor (MinecraftSourceHandoffCheckpoint checkpoint, string path)
    {
        SuspendedSource? suspended;
        lock (_gate)
        {
            suspended = _suspendedSources.Values.FirstOrDefault(state => state.Checkpoint == checkpoint);
        }

        return suspended is null || HasMatchingSuspendedPrefix(suspended, path);
    }

    private void RemoveSuspendedCheckpoint (MinecraftSourceHandoffCheckpoint checkpoint)
    {
        lock (_gate)
        {
            foreach (string fileId in _suspendedSources
                .Where(pair => pair.Value.Checkpoint == checkpoint)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _suspendedSources.Remove(fileId);
                _blockedResumeFileIds.Remove(fileId);
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

    private sealed record SuspendedSource (
        DiscoveredSourceFile Source,
        MinecraftSourceHandoffCheckpoint? Checkpoint,
        long ObservedLength,
        byte[]? PrefixHash);

    private sealed record PendingHandoff (
        MinecraftSourceHandoffCheckpoint Checkpoint,
        long PredecessorObservedLength,
        long? SuccessorLengthAtBoundary,
        string? SuccessorFileId,
        bool IsAmbiguous);

    private sealed record PendingHandoffEntry (string Key, PendingHandoff Pending);
}

#pragma warning restore CA1031
