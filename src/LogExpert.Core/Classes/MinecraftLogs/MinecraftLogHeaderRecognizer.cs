using System.Text.RegularExpressions;

namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>Shared header patterns used by both framing and the concrete log parsers.</summary>
internal static class MinecraftLogHeaderRecognizer
{
    private static readonly Regex MinecraftLatestLogPattern = new(
        @"^\[(?<timestamp>\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\] \[(?<thread>[^/\]\r\n]+)/(?<level>[^\]\r\n]+)\](?:: ?(?<plainMessage>[^\r\n]*)| \((?<category>[^()\r\n]+)\)(?:: ?| +)(?<categoryMessage>[^\r\n]*))$",
        RegexOptions.CultureInvariant);

    private static readonly Regex YeezusTextLogPattern = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})) \[(?<level>[^\]\r\n]+)\] \[(?<context>[^\]\r\n]+)\] \[(?<thread>[^\]\r\n]+)\] (?<message>[^\r\n]*)$",
        RegexOptions.CultureInvariant);

    public static Match MatchMinecraftLatestLog (string line) => MinecraftLatestLogPattern.Match(line);

    public static Match MatchYeezusTextLog (string line) => YeezusTextLogPattern.Match(line);

    public static bool IsMinecraftLatestLogHeader (string line) =>
        MatchMinecraftLatestLog(line).Success;

    public static bool IsYeezusTextLogHeader (string line) =>
        MatchYeezusTextLog(line).Success;
}
