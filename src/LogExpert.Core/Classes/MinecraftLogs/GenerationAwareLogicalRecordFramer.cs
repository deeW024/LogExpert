using System.Text;

namespace LogExpert.Core.Classes.MinecraftLogs;

public enum LogicalRecordFramingStrategy
{
    LineDelimited,
    HeaderDelimited
}

/// <summary>Maps discovered source adapter hints to deterministic framing strategies.</summary>
public static class MinecraftSourceFramingResolver
{
    public static LogicalRecordFramingStrategy Resolve (MinecraftSourceAdapterHint adapterHint) =>
        adapterHint switch
        {
            MinecraftSourceAdapterHint.ReCactusSessionText => LogicalRecordFramingStrategy.LineDelimited,
            MinecraftSourceAdapterHint.CactusMonitorSessionJsonl => LogicalRecordFramingStrategy.LineDelimited,
            MinecraftSourceAdapterHint.MinecraftLatestLog => LogicalRecordFramingStrategy.HeaderDelimited,
            MinecraftSourceAdapterHint.YeezusTextLog => LogicalRecordFramingStrategy.HeaderDelimited,
            _ => throw new ArgumentOutOfRangeException(nameof(adapterHint), adapterHint, null)
        };

    public static GenerationAwareLogicalRecordFramer CreateFramer (MinecraftSourceAdapterHint adapterHint) =>
        new(adapterHint);
}

/// <summary>
/// Frames supplied physical-line observations into parser inputs. It owns no file reader;
/// the caller supplies the initial FileRef.Generation and every byte/line position.
/// Structural API misuse throws InvalidOperationException; malformed log content is preserved.
/// </summary>
public sealed class GenerationAwareLogicalRecordFramer
{
    private readonly MinecraftSourceAdapterHint _adapterHint;
    private readonly LogicalRecordFramingStrategy _strategy;
    private FileRef? _file;
    private PhysicalLineObservation? _lastObservation;
    private PhysicalLineObservation? _pendingLine;
    private List<PhysicalLineObservation>? _currentRecord;
    private long _nextSourceLocalSequence = 1;
    private bool _generationFinalized;

    public GenerationAwareLogicalRecordFramer (MinecraftSourceAdapterHint adapterHint)
    {
        _adapterHint = adapterHint;
        _strategy = MinecraftSourceFramingResolver.Resolve(adapterHint);
    }

    public MinecraftSourceAdapterHint AdapterHint => _adapterHint;

    public LogicalRecordFramingStrategy Strategy => _strategy;

    /// <summary>
    /// Starts an externally identified generation. If another generation is active, its
    /// pending record is finalized and returned before the new generation is initialized.
    /// A framer instance is bound to one stable FileId and path for its lifetime.
    /// </summary>
    public IReadOnlyList<LogParserInput> BeginGeneration (FileRef file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (_file is null)
        {
            InitializeGeneration(file);
            return Array.Empty<LogParserInput>();
        }

        if (!string.Equals(_file.FileId, file.FileId, StringComparison.Ordinal) ||
            !string.Equals(_file.Path, file.Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        if (file.Generation <= _file.Generation)
        {
            throw new InvalidOperationException();
        }

        IReadOnlyList<LogParserInput> finalized = _generationFinalized
            ? Array.Empty<LogParserInput>()
            : FinalizeGeneration();

        InitializeGeneration(file);
        return finalized;
    }

    /// <summary>
    /// Accepts a line in source order. Repeated identical delivery of the most recent
    /// observation is idempotent. A non-terminated line may be updated by a longer
    /// cumulative snapshot with the same line number and start offset.
    /// </summary>
    public IReadOnlyList<LogParserInput> Accept (PhysicalLineObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (_file is null)
        {
            throw new InvalidOperationException();
        }

        if (_generationFinalized)
        {
            throw new InvalidOperationException();
        }

        if (!Equals(observation.File, _file))
        {
            throw new InvalidOperationException();
        }

        if (_lastObservation is null)
        {
            _lastObservation = observation;
            return AcceptNewLine(observation);
        }

        if (observation.LineNumber == _lastObservation.LineNumber)
        {
            if (observation == _lastObservation)
            {
                return Array.Empty<LogParserInput>();
            }

            return UpdatePendingLine(observation);
        }

        if (observation.LineNumber < _lastObservation.LineNumber)
        {
            throw new InvalidOperationException();
        }

        ValidateNextLine(observation);
        _lastObservation = observation;
        return AcceptNewLine(observation);
    }

    /// <summary>Flushes pending content once. Repeated calls return no additional records.</summary>
    public IReadOnlyList<LogParserInput> FinalizeGeneration ()
    {
        if (_file is null || _generationFinalized)
        {
            return Array.Empty<LogParserInput>();
        }

        var emitted = new List<LogParserInput>();
        if (_pendingLine is not null)
        {
            ProcessLine(_pendingLine, emitted);
            _pendingLine = null;
        }

        if (_strategy == LogicalRecordFramingStrategy.HeaderDelimited)
        {
            EmitCurrentRecord(emitted);
        }

        _generationFinalized = true;
        return emitted;
    }

    private void InitializeGeneration (FileRef file)
    {
        _file = file;
        _lastObservation = null;
        _pendingLine = null;
        _currentRecord = null;
        _generationFinalized = false;
    }

    private IReadOnlyList<LogParserInput> AcceptNewLine (PhysicalLineObservation observation)
    {
        if (!observation.IsTerminated)
        {
            _pendingLine = observation;
            return Array.Empty<LogParserInput>();
        }

        _pendingLine = null;
        var emitted = new List<LogParserInput>();
        ProcessLine(observation, emitted);
        return emitted;
    }

    private IReadOnlyList<LogParserInput> UpdatePendingLine (PhysicalLineObservation observation)
    {
        PhysicalLineObservation previous = _lastObservation!;
        if (previous.IsTerminated ||
            observation.StartByteOffset != previous.StartByteOffset ||
            observation.EndByteOffset < previous.EndByteOffset ||
            !observation.Content.StartsWith(previous.Content, StringComparison.Ordinal) ||
            (observation.EndByteOffset == previous.EndByteOffset &&
             !string.Equals(observation.Content, previous.Content, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException();
        }

        _lastObservation = observation;
        if (!observation.IsTerminated)
        {
            _pendingLine = observation;
            return Array.Empty<LogParserInput>();
        }

        _pendingLine = null;
        var emitted = new List<LogParserInput>();
        ProcessLine(observation, emitted);
        return emitted;
    }

    private void ValidateNextLine (PhysicalLineObservation observation)
    {
        PhysicalLineObservation previous = _lastObservation!;
        if (!previous.IsTerminated)
        {
            throw new InvalidOperationException();
        }

        if (previous.LineNumber == long.MaxValue || observation.LineNumber != previous.LineNumber + 1)
        {
            throw new InvalidOperationException();
        }

        long expectedOffset;
        try
        {
            expectedOffset = checked(previous.EndByteOffset + previous.TerminatorByteLength);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(null, exception);
        }

        if (observation.StartByteOffset != expectedOffset)
        {
            throw new InvalidOperationException();
        }
    }

    private void ProcessLine (PhysicalLineObservation line, ICollection<LogParserInput> emitted)
    {
        if (_strategy == LogicalRecordFramingStrategy.LineDelimited)
        {
            emitted.Add(CreateInput([line]));
            return;
        }

        bool isHeader = _adapterHint switch
        {
            MinecraftSourceAdapterHint.MinecraftLatestLog =>
                MinecraftLogHeaderRecognizer.IsMinecraftLatestLogHeader(line.Content),
            MinecraftSourceAdapterHint.YeezusTextLog =>
                MinecraftLogHeaderRecognizer.IsYeezusTextLogHeader(line.Content),
            _ => throw new InvalidOperationException()
        };

        if (isHeader)
        {
            EmitCurrentRecord(emitted);
            _currentRecord = [line];
        }
        else if (_currentRecord is null)
        {
            // Orphan continuations remain visible as individual records for the parser.
            emitted.Add(CreateInput([line]));
        }
        else
        {
            _currentRecord.Add(line);
        }
    }

    private void EmitCurrentRecord (ICollection<LogParserInput> emitted)
    {
        if (_currentRecord is null)
        {
            return;
        }

        emitted.Add(CreateInput(_currentRecord));
        _currentRecord = null;
    }

    private LogParserInput CreateInput (IReadOnlyList<PhysicalLineObservation> lines)
    {
        var rawText = new StringBuilder();
        for (int index = 0; index < lines.Count; index++)
        {
            PhysicalLineObservation line = lines[index];
            rawText.Append(line.Content);
            if (index < lines.Count - 1)
            {
                if (!line.IsTerminated)
                {
                    throw new InvalidOperationException();
                }

                rawText.Append(line.TerminatorText);
            }
        }

        PhysicalLineObservation first = lines[0];
        PhysicalLineObservation last = lines[^1];
        var input = new LogParserInput(
            _file!,
            _nextSourceLocalSequence,
            first.StartByteOffset,
            last.EndByteOffset,
            first.LineNumber,
            last.LineNumber,
            rawText.ToString(),
            last.IsTerminated);
        _nextSourceLocalSequence = checked(_nextSourceLocalSequence + 1);
        return input;
    }
}

/// <summary>Composes framed inputs with the existing source parser resolver.</summary>
public static class MinecraftSourceEventParser
{
    public static IReadOnlyList<NormalizedLogEvent> Parse (
        MinecraftSourceAdapterHint adapterHint,
        IEnumerable<LogParserInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        ILogEventParser parser = MinecraftSourceParserResolver.Resolve(adapterHint);
        var events = new List<NormalizedLogEvent>();
        foreach (LogParserInput input in inputs)
        {
            events.Add(parser.Parse(input));
        }

        return events;
    }
}
