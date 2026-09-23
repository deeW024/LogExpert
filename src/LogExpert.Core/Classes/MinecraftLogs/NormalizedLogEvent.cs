using System.Diagnostics.CodeAnalysis;

namespace LogExpert.Core.Classes.MinecraftLogs;

public sealed record FileRef (string FileId, string Path, long Generation);

/// <summary>
/// Stable location of an event in one source file. Byte offsets are half-open and
/// line numbers are one-based. SourceLocalSequence follows file order, not timestamp order.
/// </summary>
public sealed record EventRef (
    FileRef File,
    long SourceLocalSequence,
    long StartByteOffset,
    long EndByteOffset,
    long StartLineNumber,
    long EndLineNumber);

public enum AttributionProvenance
{
    ExplicitMetadata,
    Adapter,
    Parsed,
    Heuristic,
    Unknown
}

public enum AttributionConfidence
{
    Exact,
    High,
    Medium,
    Low,
    Unknown
}

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

/// <summary>A known value and how it was attributed, or an explicit Unknown value.</summary>
public sealed record AttributedValue<T>
{
    private readonly T? _value;

    internal AttributedValue (T? value, AttributionProvenance provenance, AttributionConfidence confidence)
    {
        _value = value;
        Provenance = provenance;
        Confidence = confidence;
    }

    public T Value => TryGetValue(out T? value)
        ? value
        : throw new InvalidOperationException();

    public AttributionProvenance Provenance { get; }

    public AttributionConfidence Confidence { get; }

    public bool IsKnown => Provenance != AttributionProvenance.Unknown;

    public bool TryGetValue ([MaybeNullWhen(false)] out T value)
    {
        if (IsKnown)
        {
            value = _value!;
            return true;
        }

        value = default!;
        return false;
    }
}

public static class Attribution
{
    public static AttributedValue<T> Unknown<T> () =>
        new(default, AttributionProvenance.Unknown, AttributionConfidence.Unknown);

    public static AttributedValue<T> From<T> (
        T value,
        AttributionProvenance provenance,
        AttributionConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (provenance == AttributionProvenance.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(provenance));
        }

        return new AttributedValue<T>(value, provenance, confidence);
    }

    /// <summary>Chooses the stronger evidence; producer metadata always outranks heuristics.</summary>
    public static AttributedValue<T> Prefer<T> (AttributedValue<T> first, AttributedValue<T> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        int firstProvenance = GetPrecedence(first.Provenance);
        int secondProvenance = GetPrecedence(second.Provenance);

        if (firstProvenance != secondProvenance)
        {
            return firstProvenance < secondProvenance ? first : second;
        }

        return GetConfidencePrecedence(first.Confidence) <= GetConfidencePrecedence(second.Confidence)
            ? first
            : second;
    }

    private static int GetPrecedence (AttributionProvenance provenance) => provenance switch
    {
        AttributionProvenance.ExplicitMetadata => 0,
        AttributionProvenance.Adapter => 1,
        AttributionProvenance.Parsed => 2,
        AttributionProvenance.Heuristic => 3,
        _ => 4
    };

    private static int GetConfidencePrecedence (AttributionConfidence confidence) => confidence switch
    {
        AttributionConfidence.Exact => 0,
        AttributionConfidence.High => 1,
        AttributionConfidence.Medium => 2,
        AttributionConfidence.Low => 3,
        _ => 4
    };
}

public enum TimestampProvenance
{
    Source,
    Ingest,
    Unknown
}

public sealed record EventTimestamp
{
    private EventTimestamp (DateTimeOffset? value, string? rawValue, TimestampProvenance provenance)
    {
        Value = value;
        RawValue = rawValue;
        Provenance = provenance;
    }

    public DateTimeOffset? Value { get; }

    public string? RawValue { get; }

    public TimestampProvenance Provenance { get; }

    public static EventTimestamp FromSource (DateTimeOffset value, string rawValue) =>
        new(value, rawValue, TimestampProvenance.Source);

    public static EventTimestamp FromIngest (DateTimeOffset value) =>
        new(value, null, TimestampProvenance.Ingest);

    public static EventTimestamp Unknown (string? rawValue = null) =>
        new(null, rawValue, TimestampProvenance.Unknown);
}

public enum EventParseStatus
{
    Parsed,
    Partial,
    Malformed,
    Unsupported
}

/// <summary>
/// One source-local event. RawText retains the complete source record, including any
/// continuation lines; Message is the parser's display-ready text.
/// </summary>
public sealed record NormalizedLogEvent (
    EventRef Ref,
    AttributedValue<string> Source,
    AttributedValue<string> Component,
    AttributedValue<LogLevel> Level,
    string? RawLevel,
    AttributedValue<string> Thread,
    EventTimestamp Timestamp,
    EventParseStatus ParseStatus,
    string Message,
    string RawText,
    long? ProducerSequence = null);
