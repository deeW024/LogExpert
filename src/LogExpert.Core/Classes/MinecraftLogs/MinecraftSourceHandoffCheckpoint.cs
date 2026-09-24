namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>Immutable reader/framer state needed to replay the earliest un-emitted source bytes.</summary>
public sealed record MinecraftSourceHandoffCheckpoint (
    string SourceId,
    FileRef PredecessorFile,
    MinecraftSourceAdapterHint AdapterHint,
    MinecraftSourceSegmentRole SegmentRole,
    long ConsumedByteFrontier,
    long NextPhysicalLineNumber,
    long ReplayStartByteOffset,
    long ReplayStartPhysicalLineNumber,
    bool ReplayStartsBeforeConsumedFrontier,
    bool HasUnemittedState,
    long NextSourceLocalSequence);

/// <summary>Read behavior and exact initial position for a workspace-owned physical source.</summary>
public sealed record MinecraftWorkspaceSourceReadRequest
{
    public MinecraftWorkspaceSourceReadRequest (
        long startByteOffset = 0,
        long startPhysicalLineNumber = 1,
        long initialGeneration = MinecraftLiveSourceSession.InitialGeneration,
        long initialSourceLocalSequence = 1,
        bool immutableSnapshot = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startByteOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(startPhysicalLineNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialSourceLocalSequence, 1);

        StartByteOffset = startByteOffset;
        StartPhysicalLineNumber = startPhysicalLineNumber;
        InitialGeneration = initialGeneration;
        InitialSourceLocalSequence = initialSourceLocalSequence;
        ImmutableSnapshot = immutableSnapshot;
    }

    public long StartByteOffset { get; }

    public long StartPhysicalLineNumber { get; }

    public long InitialGeneration { get; }

    public long InitialSourceLocalSequence { get; }

    public bool ImmutableSnapshot { get; }

    public static MinecraftWorkspaceSourceReadRequest Live (long generation = 1, long nextSequence = 1) =>
        new(initialGeneration: generation, initialSourceLocalSequence: nextSequence);

    public static MinecraftWorkspaceSourceReadRequest Snapshot (
        long startByteOffset = 0,
        long startPhysicalLineNumber = 1,
        long generation = 1,
        long nextSequence = 1) =>
        new(startByteOffset, startPhysicalLineNumber, generation, nextSequence, immutableSnapshot: true);
}

/// <summary>Optional factory seam for sessions that support replay positions and immutable reads.</summary>
public interface IMinecraftWorkspaceLiveSourceSessionFactoryWithReadRequest
{
    IMinecraftWorkspaceLiveSourceSession Create (
        DiscoveredSourceFile source,
        MinecraftWorkspaceSourceReadRequest readRequest);
}

/// <summary>Optional state exposed by the real reader-backed source session.</summary>
public interface IMinecraftWorkspaceLiveSourceSessionProgress
{
    FileRef CurrentFile { get; }

    long NextSourceLocalSequence { get; }

    bool IsImmutableComplete { get; }
}

/// <summary>Optional handoff operations supported by the real reader-backed source session.</summary>
public interface IMinecraftWorkspaceSourceHandoffSession
{
    event EventHandler<MinecraftWorkspaceHandoffCheckpointEventArgs>? HandoffCheckpointCaptured;

    MinecraftSourceHandoffCheckpoint GetHandoffCheckpoint ();

    MinecraftSourceHandoffCheckpoint CaptureAndCloseForHandoff ();
}

public enum MinecraftHandoffTransitionKind
{
    YeezusPrimaryToRotated,
    CactusMonitorActivePartToFinal
}

/// <summary>Lightweight handoff state published without reading the successor from the callback.</summary>
public sealed class MinecraftWorkspaceHandoffCheckpointEventArgs : EventArgs
{
    public MinecraftWorkspaceHandoffCheckpointEventArgs (
        MinecraftHandoffTransitionKind transitionKind,
        MinecraftSourceHandoffCheckpoint checkpoint,
        long predecessorFileLength,
        long? successorFileLength)
    {
        TransitionKind = transitionKind;
        Checkpoint = checkpoint;
        PredecessorFileLength = predecessorFileLength;
        SuccessorFileLength = successorFileLength;
    }

    public MinecraftHandoffTransitionKind TransitionKind { get; }

    public MinecraftSourceHandoffCheckpoint Checkpoint { get; }

    public long PredecessorFileLength { get; }

    public long? SuccessorFileLength { get; }
}
