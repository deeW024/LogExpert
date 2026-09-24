using LogExpert.Core.Classes.Log;
using LogExpert.Core.Classes.Log.Streamreaders;

namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>
/// Bridges one discovered physical Minecraft source to the existing LogfileReader,
/// generation-aware framer, and source parser. The reader owns all file reads and polling.
/// </summary>
public sealed class MinecraftLiveSourceSession : IMinecraftPhysicalLineObserver, IDisposable
{
    public const long InitialGeneration = 1;

    private readonly object _gate = new();
    private readonly DiscoveredSourceFile _source;
    private readonly LogfileReader _reader;
    private readonly GenerationAwareLogicalRecordFramer _framer;

    private FileRef _currentFile;
    private PhysicalLineObservation? _pendingPhysicalLine;
    private long _nextLineNumber = 1;
    private long _nextExpectedByteOffset;
    private bool _isDeleted;
    private bool _isStarted;
    private bool _isDisposed;

    public MinecraftLiveSourceSession (DiscoveredSourceFile source, LogfileReader reader)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.FileSize != 0 || reader.LineCount != 0)
        {
            throw new InvalidOperationException();
        }

        _source = source;
        _reader = reader;
        _framer = MinecraftSourceFramingResolver.CreateFramer(source.AdapterHint);
        _currentFile = new FileRef(source.FileId, source.FullPath, InitialGeneration);
        _ = _framer.BeginGeneration(_currentFile);

        // Begin generation 1 before LogfileReader can perform its initial physical read.
        _reader.MinecraftPhysicalLineObserver = this;
    }

    public event EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? EventsProduced;

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

        _reader.StartMonitoring();
    }

    public void OnPhysicalLine (MinecraftPhysicalLineRead line)
    {
        ArgumentNullException.ThrowIfNull(line);

        IReadOnlyList<LogParserInput> inputs;
        lock (_gate)
        {
            if (_isDisposed || _isDeleted)
            {
                return;
            }

            inputs = AcceptPhysicalLine(line);
        }

        Publish(inputs);
    }

    public void OnSourceTruncated ()
    {
        IReadOnlyList<LogParserInput> inputs;
        lock (_gate)
        {
            if (_isDisposed || _isDeleted)
            {
                return;
            }

            inputs = BeginNextGeneration();
        }

        Publish(inputs);
    }

    public void OnSourceDeleted ()
    {
        IReadOnlyList<LogParserInput> inputs;
        lock (_gate)
        {
            if (_isDisposed || _isDeleted)
            {
                return;
            }

            _isDeleted = true;
            _pendingPhysicalLine = null;
            inputs = _framer.FinalizeGeneration();
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
            shouldStop = _isStarted;
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
            inputs = _framer.FinalizeGeneration();
        }

        Publish(inputs);
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

    private void Publish (IReadOnlyList<LogParserInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        IReadOnlyList<NormalizedLogEvent> events = MinecraftSourceEventParser.Parse(_source.AdapterHint, inputs);
        if (events.Count > 0)
        {
            EventsProduced?.Invoke(this, new MinecraftLiveSourceEventsProducedEventArgs(events));
        }
    }
}
