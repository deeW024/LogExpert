using LogExpert.Core.Classes.Log;
using LogExpert.Core.Classes.Log.Streamreaders;

namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>
/// Bridges one discovered physical Minecraft source to the existing LogfileReader,
/// generation-aware framer, and source parser. The reader owns all file reads and polling.
/// </summary>
public sealed class MinecraftLiveSourceSession :
    IMinecraftPhysicalLineObserver,
    IMinecraftWorkspaceLiveSourceSession,
    IMinecraftWorkspaceLiveSourceSessionProgress,
    IMinecraftWorkspaceSourceHandoffSession
{
    public const long InitialGeneration = 1;

    private readonly object _gate = new();
    private readonly DiscoveredSourceFile _source;
    private readonly LogfileReader _reader;
    private readonly GenerationAwareLogicalRecordFramer _framer;
    private readonly MinecraftWorkspaceSourceReadRequest _readRequest;
    private readonly List<NormalizedLogEvent> _snapshotEvents = [];

    private FileRef _currentFile;
    private PhysicalLineObservation? _pendingPhysicalLine;
    private MinecraftSourceHandoffCheckpoint? _lastHandoffCheckpoint;
    private long _nextLineNumber = 1;
    private long _nextExpectedByteOffset;
    private bool _isDeleted;
    private bool _isStarted;
    private bool _isReaderMonitoring;
    private bool _isDisposed;
    private bool _handoffClosed;
    private bool _immutableComplete;
    private bool _snapshotFailed;

    public MinecraftLiveSourceSession (
        DiscoveredSourceFile source,
        LogfileReader reader,
        MinecraftWorkspaceSourceReadRequest? readRequest = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.FileSize != 0 || reader.LineCount != 0)
        {
            throw new InvalidOperationException();
        }

        _source = source;
        _reader = reader;
        _readRequest = readRequest ?? MinecraftWorkspaceSourceReadRequest.Live();
        _framer = MinecraftSourceFramingResolver.CreateFramer(
            source.AdapterHint,
            _readRequest.InitialSourceLocalSequence);
        _currentFile = new FileRef(source.FileId, source.FullPath, _readRequest.InitialGeneration);
        _nextLineNumber = _readRequest.StartPhysicalLineNumber;
        _nextExpectedByteOffset = _readRequest.StartByteOffset;
        _ = _framer.BeginGeneration(_currentFile);

        // Begin the requested generation before LogfileReader performs its initial physical read.
        _reader.MinecraftPhysicalLineReadStartByteOffset = _readRequest.StartByteOffset;
        _reader.MinecraftPhysicalLineObserver = this;
    }

    public event EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? EventsProduced;

    public event EventHandler<MinecraftWorkspaceHandoffCheckpointEventArgs>? HandoffCheckpointCaptured;

    public FileRef CurrentFile
    {
        get
        {
            lock (_gate)
            {
                return _currentFile;
            }
        }
    }

    public bool IsDeleted
    {
        get
        {
            lock (_gate)
            {
                return _isDeleted;
            }
        }
    }

    public long NextSourceLocalSequence
    {
        get
        {
            lock (_gate)
            {
                return _framer.NextSourceLocalSequence;
            }
        }
    }

    public bool IsImmutableComplete
    {
        get
        {
            lock (_gate)
            {
                return _immutableComplete;
            }
        }
    }

    public void StartMonitoring ()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isStarted)
            {
                throw new InvalidOperationException();
            }

            _isStarted = true;
        }

        if (_readRequest.ImmutableSnapshot)
        {
            ReadImmutableSnapshot();
        }
        else
        {
            lock (_gate)
            {
                _isReaderMonitoring = true;
            }

            try
            {
                _reader.StartMonitoring();
            }
            catch
            {
                lock (_gate)
                {
                    _isReaderMonitoring = false;
                }

                throw;
            }
        }
    }

    public void OnPhysicalLine (MinecraftPhysicalLineRead line)
    {
        ArgumentNullException.ThrowIfNull(line);

        IReadOnlyList<LogParserInput> inputs;
        lock (_gate)
        {
            if (_isDisposed || _isDeleted || _handoffClosed)
            {
                return;
            }

            inputs = AcceptPhysicalLine(line);
        }

        Publish(inputs);
    }

    public void OnSourceTruncated ()
    {
        OnSourceTruncated(_reader.FileSize);
    }

    public void OnSourceTruncated (long previouslyObservedFileLength)
    {
        IReadOnlyList<LogParserInput> inputs;
        MinecraftWorkspaceHandoffCheckpointEventArgs? checkpointArgs = null;
        lock (_gate)
        {
            if (_isDisposed || _isDeleted || _handoffClosed)
            {
                return;
            }

            if (_source.Family == MinecraftSourceFamily.Yeezus &&
                _source.SegmentRole == MinecraftSourceSegmentRole.Primary &&
                TryGetRotatedCandidate(out long? rotatedLength))
            {
                MinecraftSourceHandoffCheckpoint checkpoint = CreateHandoffCheckpoint();
                long predecessorLength = previouslyObservedFileLength;
                _framer.AbandonGeneration(reserveSourceLocalSequenceForReplay: checkpoint.HasUnemittedState);
                inputs = BeginNextGeneration();
                checkpointArgs = new MinecraftWorkspaceHandoffCheckpointEventArgs(
                    MinecraftHandoffTransitionKind.YeezusPrimaryToRotated,
                    checkpoint,
                    predecessorLength,
                    rotatedLength);
            }
            else
            {
                inputs = BeginNextGeneration();
            }
        }

        if (checkpointArgs is not null)
        {
            HandoffCheckpointCaptured?.Invoke(this, checkpointArgs);
        }

        Publish(inputs);
    }

    public void OnSourceDeleted ()
    {
        IReadOnlyList<LogParserInput> inputs;
        MinecraftWorkspaceHandoffCheckpointEventArgs? checkpointArgs = null;
        lock (_gate)
        {
            if (_isDisposed || _isDeleted || _handoffClosed)
            {
                return;
            }

            _isDeleted = true;
            _pendingPhysicalLine = null;
            if (_source.Family == MinecraftSourceFamily.Yeezus &&
                _source.SegmentRole == MinecraftSourceSegmentRole.Primary &&
                TryGetRotatedCandidate(out long? rotatedLength))
            {
                MinecraftSourceHandoffCheckpoint checkpoint = CreateHandoffCheckpoint();
                long predecessorLength = _reader.FileSize;
                _framer.AbandonGeneration(reserveSourceLocalSequenceForReplay: checkpoint.HasUnemittedState);
                _lastHandoffCheckpoint = checkpoint;
                inputs = Array.Empty<LogParserInput>();
                checkpointArgs = new MinecraftWorkspaceHandoffCheckpointEventArgs(
                    MinecraftHandoffTransitionKind.YeezusPrimaryToRotated,
                    checkpoint,
                    predecessorLength,
                    rotatedLength);
            }
            else if (_source.Family == MinecraftSourceFamily.CactusMonitor &&
                _source.SegmentRole == MinecraftSourceSegmentRole.ActivePart)
            {
                MinecraftSourceHandoffCheckpoint checkpoint = CreateHandoffCheckpoint();
                _framer.AbandonGeneration(reserveSourceLocalSequenceForReplay: checkpoint.HasUnemittedState);
                _lastHandoffCheckpoint = checkpoint;
                _handoffClosed = true;
                inputs = Array.Empty<LogParserInput>();
                checkpointArgs = new MinecraftWorkspaceHandoffCheckpointEventArgs(
                    MinecraftHandoffTransitionKind.CactusMonitorActivePartToFinal,
                    checkpoint,
                    _reader.FileSize,
                    successorFileLength: null);
            }
            else
            {
                inputs = _framer.FinalizeGeneration();
            }
        }

        if (checkpointArgs is not null)
        {
            HandoffCheckpointCaptured?.Invoke(this, checkpointArgs);
        }

        Publish(inputs);
    }

    public void OnSourceRecreated ()
    {
        IReadOnlyList<LogParserInput> inputs;
        lock (_gate)
        {
            if (_isDisposed || !_isDeleted)
            {
                return;
            }

            inputs = BeginNextGeneration();
            _isDeleted = false;
            _handoffClosed = false;
            _lastHandoffCheckpoint = null;
        }

        Publish(inputs);
    }

    public void Dispose ()
    {
        bool shouldStop;
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            shouldStop = _isReaderMonitoring;
            _isReaderMonitoring = false;
        }

        if (shouldStop)
        {
            _reader.StopMonitoring();
        }

        _reader.MinecraftPhysicalLineObserver = null;

        IReadOnlyList<LogParserInput> inputs;
        lock (_gate)
        {
            _pendingPhysicalLine = null;
            inputs = _handoffClosed || _snapshotFailed
                ? Array.Empty<LogParserInput>()
                : _framer.FinalizeGeneration();
        }

        Publish(inputs);
    }

    /// <summary>Returns the current reader and framer replay position without changing either.</summary>
    public MinecraftSourceHandoffCheckpoint GetHandoffCheckpoint ()
    {
        lock (_gate)
        {
            return _lastHandoffCheckpoint ?? CreateHandoffCheckpoint();
        }
    }

    /// <summary>Captures the replay point and stops without finalizing pending state.</summary>
    public MinecraftSourceHandoffCheckpoint CaptureAndCloseForHandoff ()
    {
        bool shouldStop;
        MinecraftSourceHandoffCheckpoint checkpoint;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_lastHandoffCheckpoint is not null)
            {
                checkpoint = _lastHandoffCheckpoint;
            }
            else
            {
                checkpoint = GetHandoffCheckpoint();
                _framer.AbandonGeneration(reserveSourceLocalSequenceForReplay: checkpoint.HasUnemittedState);
                _pendingPhysicalLine = null;
                _lastHandoffCheckpoint = checkpoint;
                _handoffClosed = true;
            }

            shouldStop = _isReaderMonitoring;
            _isReaderMonitoring = false;
        }

        if (shouldStop)
        {
            _reader.StopMonitoring();
        }

        _reader.MinecraftPhysicalLineObserver = null;
        return checkpoint;
    }

    private IReadOnlyList<LogParserInput> AcceptPhysicalLine (MinecraftPhysicalLineRead line)
    {
        PhysicalLineReadMetadata metadata = line.Metadata;

        // A repeated full ReadFiles() pass is a buffer refresh, not new source data.
        // Length-only replacement detection remains owned by LogfileReader.
        if (metadata.StartByteOffset < _nextExpectedByteOffset)
        {
            return Array.Empty<LogParserInput>();
        }

        if (metadata.StartByteOffset != _nextExpectedByteOffset)
        {
            throw new InvalidOperationException();
        }

        PhysicalLineObservation observation;
        if (_pendingPhysicalLine is not null)
        {
            PhysicalLineObservation pending = _pendingPhysicalLine;
            if (metadata.StartByteOffset != pending.EndByteOffset)
            {
                throw new InvalidOperationException();
            }

            observation = new PhysicalLineObservation(
                _currentFile,
                pending.LineNumber,
                pending.StartByteOffset,
                metadata.EndByteOffset,
                string.Concat(pending.Content, line.Content),
                metadata.Terminator,
                metadata.TerminatorByteLength);
        }
        else
        {
            observation = new PhysicalLineObservation(
                _currentFile,
                _nextLineNumber,
                metadata.StartByteOffset,
                metadata.EndByteOffset,
                line.Content,
                metadata.Terminator,
                metadata.TerminatorByteLength);
        }

        IReadOnlyList<LogParserInput> emitted = _framer.Accept(observation);

        if (observation.IsTerminated)
        {
            _pendingPhysicalLine = null;
            _nextLineNumber = checked(observation.LineNumber + 1);
            _nextExpectedByteOffset = checked(observation.EndByteOffset + observation.TerminatorByteLength);
        }
        else
        {
            _pendingPhysicalLine = observation;
            _nextExpectedByteOffset = observation.EndByteOffset;
        }

        return emitted;
    }

    private IReadOnlyList<LogParserInput> BeginNextGeneration ()
    {
        var nextFile = _currentFile with { Generation = checked(_currentFile.Generation + 1) };
        IReadOnlyList<LogParserInput> finalized = _framer.BeginGeneration(nextFile);

        _currentFile = nextFile;
        _pendingPhysicalLine = null;
        _nextLineNumber = 1;
        _nextExpectedByteOffset = 0;

        return finalized;
    }

    private MinecraftSourceHandoffCheckpoint CreateHandoffCheckpoint ()
    {
        LogicalRecordReplayStart replayStart = _framer.GetReplayStart(
            _nextExpectedByteOffset,
            _nextLineNumber);
        return new MinecraftSourceHandoffCheckpoint(
            _source.SourceId,
            _currentFile,
            _source.AdapterHint,
            _source.SegmentRole,
            _nextExpectedByteOffset,
            _nextLineNumber,
            replayStart.ByteOffset,
            replayStart.PhysicalLineNumber,
            replayStart.ByteOffset < _nextExpectedByteOffset,
            replayStart.HasUnemittedState,
            _framer.NextSourceLocalSequence);
    }

    private bool TryGetRotatedCandidate (out long? rotatedLength)
    {
        string directory = Path.GetDirectoryName(_source.FullPath) ?? string.Empty;
        string rotatedPath = Path.Combine(directory, "yeezus.log.1");
        try
        {
            rotatedLength = new FileInfo(rotatedPath).Length;
            return true;
        }
        catch (FileNotFoundException)
        {
            rotatedLength = null;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            rotatedLength = null;
            return false;
        }
        catch (IOException)
        {
            rotatedLength = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            rotatedLength = null;
            return false;
        }
    }

    private void ReadImmutableSnapshot ()
    {
        IReadOnlyList<NormalizedLogEvent> snapshotEvents;
        try
        {
            var fileInfo = new FileInfo(_source.FullPath);
            if (!fileInfo.Exists || fileInfo.Length < _readRequest.StartByteOffset)
            {
                throw new IOException("The immutable successor is shorter than its replay start.");
            }

            long initialLength = fileInfo.Length;
            _reader.ReadFiles();
            fileInfo.Refresh();
            if (!fileInfo.Exists || fileInfo.Length != initialLength || _reader.IsFileUnavailable)
            {
                throw new IOException("The immutable successor changed or became unavailable during the read.");
            }

            lock (_gate)
            {
                if (_nextExpectedByteOffset > fileInfo.Length)
                {
                    throw new IOException("The immutable successor did not honor its replay start.");
                }

                IReadOnlyList<LogParserInput> finalInputs = _framer.FinalizeGeneration();
                AppendSnapshotEvents(finalInputs);
                _immutableComplete = true;
                snapshotEvents = Array.AsReadOnly(_snapshotEvents.ToArray());
                _snapshotEvents.Clear();
            }
        }
        catch
        {
            lock (_gate)
            {
                _snapshotFailed = true;
                _snapshotEvents.Clear();
            }

            throw;
        }

        if (snapshotEvents.Count > 0)
        {
            EventsProduced?.Invoke(this, new MinecraftLiveSourceEventsProducedEventArgs(snapshotEvents));
        }
    }

    private void Publish (IReadOnlyList<LogParserInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        IReadOnlyList<NormalizedLogEvent> events = MinecraftSourceEventParser.Parse(_source.AdapterHint, inputs);
        if (events.Count > 0)
        {
            if (_readRequest.ImmutableSnapshot)
            {
                lock (_gate)
                {
                    if (!_snapshotFailed)
                    {
                        _snapshotEvents.AddRange(events);
                    }
                }
            }
            else
            {
                EventsProduced?.Invoke(this, new MinecraftLiveSourceEventsProducedEventArgs(events));
            }
        }
    }

    private void AppendSnapshotEvents (IReadOnlyList<LogParserInput> inputs)
    {
        if (inputs.Count > 0)
        {
            _snapshotEvents.AddRange(MinecraftSourceEventParser.Parse(_source.AdapterHint, inputs));
        }
    }
}
