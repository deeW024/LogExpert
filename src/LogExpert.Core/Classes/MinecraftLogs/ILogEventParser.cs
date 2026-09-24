namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>
/// A logical source record passed to an adapter. RawText contains the first physical
/// line and any continuation lines assigned to that record. An incomplete final
/// physical line is passed with IsComplete=false when the source is finalized.
/// </summary>
public sealed record LogParserInput (
    FileRef File,
    long SourceLocalSequence,
    long StartByteOffset,
    long EndByteOffset,
    long StartLineNumber,
    long EndLineNumber,
    string RawText,
    bool IsComplete);

/// <summary>
/// Parses one logical record without depending on workspace, timeline, or FilterPipe UI.
/// Implementations return one event per input, preserve source-local sequence and
/// location, keep continuation lines in that event, and mark incomplete inputs Partial.
/// Unknown fields remain Unknown when the record does not provide reliable values.
/// </summary>
public interface ILogEventParser
{
    string ParserId { get; }

    NormalizedLogEvent Parse (LogParserInput input);
}

/// <summary>Fallback adapter that preserves unsupported records without guessing fields.</summary>
public sealed class UnknownLogEventParser : ILogEventParser
{
    public string ParserId => "unknown";

    public NormalizedLogEvent Parse (LogParserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var eventRef = new EventRef(
            input.File,
            input.SourceLocalSequence,
            input.StartByteOffset,
            input.EndByteOffset,
            input.StartLineNumber,
            input.EndLineNumber);

        return new NormalizedLogEvent(
            eventRef,
            Attribution.Unknown<string>(),
            Attribution.Unknown<string>(),
            Attribution.Unknown<LogLevel>(),
            null,
            Attribution.Unknown<string>(),
            EventTimestamp.Unknown(),
            input.IsComplete ? EventParseStatus.Unsupported : EventParseStatus.Partial,
            input.RawText,
            input.RawText);
    }
}
