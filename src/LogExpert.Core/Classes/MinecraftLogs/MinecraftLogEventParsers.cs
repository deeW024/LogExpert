using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogExpert.Core.Classes.MinecraftLogs;

public sealed class MinecraftLatestLogParser : ILogEventParser
{
    private static readonly Regex HeaderPattern = new(
        @"^\[(?<timestamp>\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\] \[(?<thread>[^/\]\r\n]+)/(?<level>[^\]\r\n]+)\](?:: ?(?<plainMessage>[^\r\n]*)| \((?<category>[^()\r\n]+)\)(?:: ?| +)(?<categoryMessage>[^\r\n]*))$",
        RegexOptions.CultureInvariant);

    public string ParserId => "minecraft.latest-log";

    public NormalizedLogEvent Parse (LogParserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        string rawText = LogEventParserSupport.GetRawText(input);
        string header = LogEventParserSupport.GetHeaderLine(rawText, out string continuation);
        Match match = HeaderPattern.Match(header);
        if (!match.Success)
        {
            return LogEventParserSupport.Create(
                input,
                Attribution.Unknown<string>(),
                Attribution.Unknown<string>(),
                Attribution.Unknown<LogLevel>(),
                null,
                Attribution.Unknown<string>(),
                EventTimestamp.Unknown(),
                EventParseStatus.Malformed,
                rawText,
                rawText);
        }

        string rawTimestamp = match.Groups["timestamp"].Value;
        string rawLevel = match.Groups["level"].Value;
        string thread = match.Groups["thread"].Value;
        string headerMessage = match.Groups["plainMessage"].Success
            ? match.Groups["plainMessage"].Value
            : match.Groups["categoryMessage"].Value;
        string message = headerMessage + continuation;
        bool validTime = TimeOnly.TryParse(
            rawTimestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

        return LogEventParserSupport.Create(
            input,
            Attribution.Unknown<string>(),
            Attribution.Unknown<string>(),
            LogEventParserSupport.NormalizeLevel(rawLevel),
            rawLevel,
            Attribution.From(thread, AttributionProvenance.Parsed, AttributionConfidence.High),
            EventTimestamp.Unknown(rawTimestamp),
            validTime ? EventParseStatus.Parsed : EventParseStatus.Malformed,
            message,
            rawText);
    }
}

public sealed class YeezusTextLogParser : ILogEventParser
{
    private static readonly Regex HeaderPattern = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})) \[(?<level>[^\]\r\n]+)\] \[(?<context>[^\]\r\n]+)\] \[(?<thread>[^\]\r\n]+)\] (?<message>[^\r\n]*)$",
        RegexOptions.CultureInvariant);

    private static readonly AttributedValue<string> YeezusSource =
        Attribution.From("Yeezus", AttributionProvenance.Adapter, AttributionConfidence.High);

    public string ParserId => "yeezus.text-log";

    public NormalizedLogEvent Parse (LogParserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        string rawText = LogEventParserSupport.GetRawText(input);
        string header = LogEventParserSupport.GetHeaderLine(rawText, out string continuation);
        Match match = HeaderPattern.Match(header);
        if (!match.Success)
        {
            return LogEventParserSupport.Create(
                input,
                YeezusSource,
                Attribution.Unknown<string>(),
                Attribution.Unknown<LogLevel>(),
                null,
                Attribution.Unknown<string>(),
                EventTimestamp.Unknown(),
                EventParseStatus.Malformed,
                rawText,
                rawText);
        }

        string rawTimestamp = match.Groups["timestamp"].Value;
        string rawLevel = match.Groups["level"].Value;
        string context = match.Groups["context"].Value;
        string thread = match.Groups["thread"].Value;
        string message = match.Groups["message"].Value + continuation;
        bool validTimestamp = DateTimeOffset.TryParse(
            rawTimestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTimeOffset timestamp);
        AttributedValue<string> component = string.Equals(context, "yeezus-core", StringComparison.Ordinal)
            ? Attribution.From("Core", AttributionProvenance.Parsed, AttributionConfidence.High)
            : Attribution.Unknown<string>();

        return LogEventParserSupport.Create(
            input,
            YeezusSource,
            component,
            LogEventParserSupport.NormalizeLevel(rawLevel),
            rawLevel,
            Attribution.From(thread, AttributionProvenance.Parsed, AttributionConfidence.High),
            validTimestamp ? EventTimestamp.FromSource(timestamp, rawTimestamp) : EventTimestamp.Unknown(rawTimestamp),
            validTimestamp ? EventParseStatus.Parsed : EventParseStatus.Malformed,
            message,
            rawText);
    }
}

public sealed class ReCactusSessionTextParser : ILogEventParser
{
    private const string FieldSeparator = " | ";

    private static readonly Regex InstantPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z$",
        RegexOptions.CultureInvariant);

    private static readonly AttributedValue<string> YeezusSource =
        Attribution.From("Yeezus", AttributionProvenance.Adapter, AttributionConfidence.High);

    private static readonly AttributedValue<string> ReCactusComponent =
        Attribution.From("ReCactus", AttributionProvenance.Adapter, AttributionConfidence.High);

    public string ParserId => "yeezus.recactus-session-text";

    public NormalizedLogEvent Parse (LogParserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        string rawText = LogEventParserSupport.GetRawText(input);
        if (rawText.StartsWith('#'))
        {
            return LogEventParserSupport.Create(
                input,
                YeezusSource,
                ReCactusComponent,
                Attribution.Unknown<LogLevel>(),
                null,
                Attribution.Unknown<string>(),
                EventTimestamp.Unknown(),
                EventParseStatus.Unsupported,
                rawText,
                rawText);
        }

        if (!TrySplitFields(rawText, out string[] fields))
        {
            return Malformed(input, rawText);
        }

        string rawTimestamp = fields[0].Trim();
        if (!InstantPattern.IsMatch(rawTimestamp) ||
            !DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset timestamp) ||
            string.IsNullOrWhiteSpace(fields[1]) ||
            string.IsNullOrWhiteSpace(fields[2]) ||
            string.IsNullOrWhiteSpace(fields[3]))
        {
            return Malformed(input, rawText);
        }

        string rawLevel = fields[1].Trim();
        bool validEscapes = TryUnescapeContent(fields[4], out string message);

        return LogEventParserSupport.Create(
            input,
            YeezusSource,
            ReCactusComponent,
            LogEventParserSupport.NormalizeLevel(rawLevel),
            rawLevel,
            Attribution.Unknown<string>(),
            EventTimestamp.FromSource(timestamp, rawTimestamp),
            validEscapes ? EventParseStatus.Parsed : EventParseStatus.Malformed,
            message,
            rawText);
    }

    private static bool TrySplitFields (string rawText, out string[] fields)
    {
        fields = new string[5];
        int start = 0;
        for (int fieldIndex = 0; fieldIndex < fields.Length - 1; fieldIndex++)
        {
            int separatorIndex = rawText.IndexOf(FieldSeparator, start, StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                return false;
            }

            fields[fieldIndex] = rawText[start..separatorIndex];
            start = separatorIndex + FieldSeparator.Length;
        }

        fields[^1] = rawText[start..];
        return true;
    }

    private static NormalizedLogEvent Malformed (LogParserInput input, string rawText) =>
        LogEventParserSupport.Create(
            input,
            YeezusSource,
            ReCactusComponent,
            Attribution.Unknown<LogLevel>(),
            null,
            Attribution.Unknown<string>(),
            EventTimestamp.Unknown(),
            EventParseStatus.Malformed,
            rawText,
            rawText);

    private static bool TryUnescapeContent (string value, out string unescaped)
    {
        var result = new System.Text.StringBuilder(value.Length);
        bool valid = true;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\')
            {
                result.Append(current);
                continue;
            }

            if (index + 1 >= value.Length)
            {
                result.Append(current);
                valid = false;
                continue;
            }

            char escaped = value[++index];
            switch (escaped)
            {
                case '\\':
                    result.Append('\\');
                    break;
                case 'r':
                    result.Append('\r');
                    break;
                case 'n':
                    result.Append('\n');
                    break;
                default:
                    result.Append('\\').Append(escaped);
                    valid = false;
                    break;
            }
        }

        unescaped = result.ToString();
        return valid;
    }
}

public sealed class CactusMonitorSessionJsonlParser : ILogEventParser
{
    private static readonly AttributedValue<string> YeezusSource =
        Attribution.From("Yeezus", AttributionProvenance.Adapter, AttributionConfidence.High);

    private static readonly AttributedValue<string> CactusMonitorComponent =
        Attribution.From("CactusMonitor", AttributionProvenance.Adapter, AttributionConfidence.High);

    public string ParserId => "yeezus.cactus-monitor-session-jsonl";

    public NormalizedLogEvent Parse (LogParserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        string rawText = LogEventParserSupport.GetRawText(input);
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawText);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                return Malformed(input, rawText);
            }

            if (!TryReadOptionalInt64(root, "sequence", out long? sequence) ||
                sequence is < 0 ||
                !TryReadOptionalInt64(root, "timestampEpochMillis", out long? epochMilliseconds))
            {
                return Malformed(input, rawText);
            }

            EventTimestamp timestamp = EventTimestamp.Unknown(
                epochMilliseconds?.ToString(CultureInfo.InvariantCulture));
            if (epochMilliseconds.HasValue)
            {
                try
                {
                    timestamp = EventTimestamp.FromSource(
                        DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds.Value),
                        epochMilliseconds.Value.ToString(CultureInfo.InvariantCulture));
                }
                catch (ArgumentOutOfRangeException)
                {
                    return Malformed(input, rawText);
                }
            }

            return LogEventParserSupport.Create(
                input,
                YeezusSource,
                CactusMonitorComponent,
                Attribution.Unknown<LogLevel>(),
                null,
                Attribution.Unknown<string>(),
                timestamp,
                EventParseStatus.Parsed,
                typeElement.GetString()!,
                rawText,
                sequence);
        }
        catch (JsonException)
        {
            return Malformed(input, rawText);
        }
        catch (InvalidOperationException)
        {
            return Malformed(input, rawText);
        }
    }

    private static bool TryReadOptionalInt64 (JsonElement root, string propertyName, out long? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static NormalizedLogEvent Malformed (LogParserInput input, string rawText) =>
        LogEventParserSupport.Create(
            input,
            YeezusSource,
            CactusMonitorComponent,
            Attribution.Unknown<LogLevel>(),
            null,
            Attribution.Unknown<string>(),
            EventTimestamp.Unknown(),
            EventParseStatus.Malformed,
            rawText,
            rawText);
}

internal static class LogEventParserSupport
{
    public static string GetRawText (LogParserInput input) => input.RawText ?? string.Empty;

    public static string GetHeaderLine (string rawText, out string continuation)
    {
        int lineEnd = rawText.IndexOfAny(['\r', '\n']);
        if (lineEnd < 0)
        {
            continuation = string.Empty;
            return rawText;
        }

        continuation = rawText[lineEnd..];
        return rawText[..lineEnd];
    }

    public static AttributedValue<LogLevel> NormalizeLevel (string rawLevel) =>
        rawLevel.Trim().ToUpperInvariant() switch
        {
            "TRACE" => Attribution.From(LogLevel.Trace, AttributionProvenance.Parsed, AttributionConfidence.High),
            "DEBUG" => Attribution.From(LogLevel.Debug, AttributionProvenance.Parsed, AttributionConfidence.High),
            "INFO" => Attribution.From(LogLevel.Info, AttributionProvenance.Parsed, AttributionConfidence.High),
            "WARN" => Attribution.From(LogLevel.Warn, AttributionProvenance.Parsed, AttributionConfidence.High),
            "ERROR" => Attribution.From(LogLevel.Error, AttributionProvenance.Parsed, AttributionConfidence.High),
            "FATAL" => Attribution.From(LogLevel.Fatal, AttributionProvenance.Parsed, AttributionConfidence.High),
            _ => Attribution.Unknown<LogLevel>()
        };

    public static NormalizedLogEvent Create (
        LogParserInput input,
        AttributedValue<string> source,
        AttributedValue<string> component,
        AttributedValue<LogLevel> level,
        string? rawLevel,
        AttributedValue<string> thread,
        EventTimestamp timestamp,
        EventParseStatus parseStatus,
        string message,
        string rawText,
        long? producerSequence = null) =>
        new(
            new EventRef(
                input.File,
                input.SourceLocalSequence,
                input.StartByteOffset,
                input.EndByteOffset,
                input.StartLineNumber,
                input.EndLineNumber),
            source,
            component,
            level,
            rawLevel,
            thread,
            timestamp,
            input.IsComplete ? parseStatus : EventParseStatus.Partial,
            message,
            rawText,
            producerSequence);
}
