using System.Globalization;
using System.Text;
using LogExpert.Core.Classes.MinecraftLogs;
using NUnit.Framework;

namespace LogExpert.Tests;

[TestFixture]
public class MinecraftLogEventParserTests
{
    private static readonly (MinecraftSourceAdapterHint Hint, string ParserId)[] ParserMappings =
    [
        (MinecraftSourceAdapterHint.MinecraftLatestLog, "minecraft.latest-log"),
        (MinecraftSourceAdapterHint.YeezusTextLog, "yeezus.text-log"),
        (MinecraftSourceAdapterHint.ReCactusSessionText, "yeezus.recactus-session-text"),
        (MinecraftSourceAdapterHint.CactusMonitorSessionJsonl, "yeezus.cactus-monitor-session-jsonl")
    ];

    [Test]
    public void Resolver_maps_every_discovery_hint_to_its_parser ()
    {
        foreach ((MinecraftSourceAdapterHint hint, string parserId) in ParserMappings)
        {
            Assert.That(MinecraftSourceParserResolver.Resolve(hint).ParserId, Is.EqualTo(parserId));
        }
    }

    [Test]
    public void Minecraft_latest_log_parses_time_thread_level_and_continuations_without_inventing_a_date ()
    {
        string rawText = ReadFixture("latest.log");
        LogParserInput input = CreateInput(rawText);

        NormalizedLogEvent result = MinecraftSourceParserResolver
            .Resolve(MinecraftSourceAdapterHint.MinecraftLatestLog)
            .Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.RawLevel, Is.EqualTo("ERROR"));
            Assert.That(result.Level.Value, Is.EqualTo(LogLevel.Error));
            Assert.That(result.Thread.Value, Is.EqualTo("Render thread"));
            Assert.That(result.Timestamp.Value, Is.Null);
            Assert.That(result.Timestamp.RawValue, Is.EqualTo("18:41:03"));
            Assert.That(result.Timestamp.Provenance, Is.EqualTo(TimestampProvenance.Unknown));
            Assert.That(result.Source.IsKnown, Is.False);
            Assert.That(result.Component.IsKnown, Is.False);
            Assert.That(result.Message, Does.StartWith("Synthetic failure"));
            Assert.That(result.Message, Does.Contain("java.lang.IllegalStateException: synthetic fixture"));
            Assert.That(result.Message, Does.Contain("at example.client.Tick.run(Tick.java:1)"));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            AssertInputLocation(result, input);
        });
    }

    [Test]
    public void Minecraft_latest_log_accepts_verified_parenthetical_logger_without_attributing_component ()
    {
        const string rawText = "[18:41:03] [Render thread/INFO] (FabricLoader/GameProvider) Loading Minecraft";
        NormalizedLogEvent result = new MinecraftLatestLogParser().Parse(CreateInput(rawText));

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.RawLevel, Is.EqualTo("INFO"));
            Assert.That(result.Level.Value, Is.EqualTo(LogLevel.Info));
            Assert.That(result.Thread.Value, Is.EqualTo("Render thread"));
            Assert.That(result.Message, Is.EqualTo("Loading Minecraft"));
            Assert.That(result.Source.IsKnown, Is.False);
            Assert.That(result.Component.IsKnown, Is.False);
            Assert.That(result.RawText, Is.EqualTo(rawText));
        });
    }

    [Test]
    public void Yeezus_core_context_is_parsed_but_message_tags_do_not_replace_it ()
    {
        string rawText = ReadFixture("yeezus.log");
        LogParserInput input = CreateInput(rawText);

        NormalizedLogEvent result = new YeezusTextLogParser().Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.Source.Value, Is.EqualTo("Yeezus"));
            Assert.That(result.Source.Provenance, Is.EqualTo(AttributionProvenance.Adapter));
            Assert.That(result.Component.Value, Is.EqualTo("Core"));
            Assert.That(result.Component.Provenance, Is.EqualTo(AttributionProvenance.Parsed));
            Assert.That(result.RawLevel, Is.EqualTo("ERROR"));
            Assert.That(result.Level.Value, Is.EqualTo(LogLevel.Error));
            Assert.That(result.Thread.Value, Is.EqualTo("Client thread"));
            Assert.That(result.Timestamp.Value, Is.EqualTo(DateTimeOffset.Parse(
                "2026-09-23T18:41:03.412+02:00", CultureInfo.InvariantCulture)));
            Assert.That(result.Message, Does.StartWith("Synthetic failure [AutoMiner]"));
            Assert.That(result.Message, Does.Contain("java.lang.IllegalStateException: synthetic fixture"));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            AssertInputLocation(result, input);
        });
    }

    [Test]
    public void Yeezus_context_without_component_identity_and_component_like_message_tag_stay_unknown ()
    {
        string rawText = "2026-09-23T18:41:03.412+02:00 [INFO] [yeezus] [Client thread] [ReCactus] ready";
        NormalizedLogEvent result = new YeezusTextLogParser().Parse(CreateInput(rawText));

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.Source.Value, Is.EqualTo("Yeezus"));
            Assert.That(result.Component.IsKnown, Is.False);
            Assert.That(result.Component.Provenance, Is.EqualTo(AttributionProvenance.Unknown));
            Assert.That(result.Message, Is.EqualTo("[ReCactus] ready"));
        });
    }

    [Test]
    public void ReCactus_current_pipe_record_unescapes_only_producer_escapes_and_has_no_producer_sequence ()
    {
        string rawText = File.ReadAllLines(GetFixturePath("reccactus-session.txt"))
            .Single(line => !line.StartsWith('#'));
        LogParserInput input = CreateInput(rawText);

        NormalizedLogEvent result = new ReCactusSessionTextParser().Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.Source.Value, Is.EqualTo("Yeezus"));
            Assert.That(result.Component.Value, Is.EqualTo("ReCactus"));
            Assert.That(result.RawLevel, Is.EqualTo("INFO"));
            Assert.That(result.Level.Value, Is.EqualTo(LogLevel.Info));
            Assert.That(result.Thread.IsKnown, Is.False);
            Assert.That(result.Timestamp.Value, Is.EqualTo(DateTimeOffset.Parse(
                "2026-09-23T16:41:03.412Z", CultureInfo.InvariantCulture)));
            Assert.That(result.Message, Is.EqualTo("first line\nsecond line\\path"));
            Assert.That(result.ProducerSequence, Is.Null);
            Assert.That(result.RawText, Is.EqualTo(rawText));
            AssertInputLocation(result, input);
        });
    }

    [Test]
    public void ReCactus_lifecycle_level_remains_raw_while_normalized_level_is_unknown ()
    {
        const string rawText = "2026-09-23T16:41:03Z | LIFECYCLE | fixture-session | COMPACT | SESSION_END";
        NormalizedLogEvent result = new ReCactusSessionTextParser().Parse(CreateInput(rawText));

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.RawLevel, Is.EqualTo("LIFECYCLE"));
            Assert.That(result.Level.IsKnown, Is.False);
            Assert.That(result.Message, Is.EqualTo("SESSION_END"));
        });
    }

    [Test]
    public void ReCactus_timestamp_without_Instant_utc_suffix_is_malformed ()
    {
        const string rawText = "2026-09-23T16:41:03 | INFO | fixture-session | COMPACT | event";
        NormalizedLogEvent result = new ReCactusSessionTextParser().Parse(CreateInput(rawText));

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Malformed));
            Assert.That(result.Timestamp.Value, Is.Null);
            Assert.That(result.Timestamp.Provenance, Is.EqualTo(TimestampProvenance.Unknown));
            Assert.That(result.RawText, Is.EqualTo(rawText));
        });
    }

    [Test]
    public void ReCactus_metadata_header_is_preserved_as_unsupported ()
    {
        string rawText = File.ReadLines(GetFixturePath("reccactus-session.txt"))
            .First(line => line.StartsWith('#'));
        NormalizedLogEvent result = new ReCactusSessionTextParser().Parse(CreateInput(rawText));

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Unsupported));
            Assert.That(result.Message, Is.EqualTo(rawText));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            Assert.That(result.Timestamp.Value, Is.Null);
            Assert.That(result.Level.IsKnown, Is.False);
            Assert.That(result.Source.Value, Is.EqualTo("Yeezus"));
            Assert.That(result.Component.Value, Is.EqualTo("ReCactus"));
        });
    }

    [Test]
    public void Cactus_monitor_parses_flattened_event_fields_timestamp_and_sequence ()
    {
        string rawText = ReadFixture("cactusmonitor-session.jsonl");
        LogParserInput input = CreateInput(rawText);

        NormalizedLogEvent result = new CactusMonitorSessionJsonlParser().Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.Source.Value, Is.EqualTo("Yeezus"));
            Assert.That(result.Component.Value, Is.EqualTo("CactusMonitor"));
            Assert.That(result.Message, Is.EqualTo("CYCLE"));
            Assert.That(result.Timestamp.Value, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1790181663412)));
            Assert.That(result.Timestamp.Provenance, Is.EqualTo(TimestampProvenance.Source));
            Assert.That(result.ProducerSequence, Is.EqualTo(7));
            Assert.That(result.Level.IsKnown, Is.False);
            Assert.That(result.RawLevel, Is.Null);
            Assert.That(result.Thread.IsKnown, Is.False);
            Assert.That(result.RawText, Is.EqualTo(rawText));
            AssertInputLocation(result, input);
        });
    }

    [Test]
    public void Cactus_monitor_mode_and_log_level_fields_are_not_event_severity ()
    {
        const string rawText = "{\"type\":\"SESSION_SUMMARY\",\"mode\":\"DIAGNOSTIC\",\"logLevel\":\"ERROR\",\"extraEventField\":{\"future\":true}}";
        NormalizedLogEvent result = new CactusMonitorSessionJsonlParser().Parse(CreateInput(rawText));

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(result.Level.IsKnown, Is.False);
            Assert.That(result.RawLevel, Is.Null);
            Assert.That(result.Thread.IsKnown, Is.False);
            Assert.That(result.Message, Is.EqualTo("SESSION_SUMMARY"));
        });
    }

    [Test]
    public void Cactus_monitor_incomplete_fixture_is_partial_and_keeps_full_raw_json ()
    {
        string rawText = ReadFixture("cactusmonitor-incomplete.jsonl");
        LogParserInput input = CreateInput(rawText, isComplete: false);

        NormalizedLogEvent result = new CactusMonitorSessionJsonlParser().Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Partial));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            Assert.That(result.Ref.File, Is.SameAs(input.File));
            Assert.That(result.Ref.SourceLocalSequence, Is.EqualTo(input.SourceLocalSequence));
            Assert.That(result.Ref.StartByteOffset, Is.EqualTo(input.StartByteOffset));
            Assert.That(result.Ref.EndByteOffset, Is.EqualTo(input.EndByteOffset));
        });
    }

    [TestCase(MinecraftSourceAdapterHint.MinecraftLatestLog, "not a Minecraft log")]
    [TestCase(MinecraftSourceAdapterHint.YeezusTextLog, "not a Yeezus log")]
    [TestCase(MinecraftSourceAdapterHint.ReCactusSessionText, "not a ReCactus record")]
    [TestCase(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl, "{not JSON")]
    public void Complete_malformed_records_are_preserved_without_throwing (
        MinecraftSourceAdapterHint hint,
        string rawText)
    {
        LogParserInput input = CreateInput(rawText);
        NormalizedLogEvent? result = null;

        Assert.DoesNotThrow(() => result = MinecraftSourceParserResolver.Resolve(hint).Parse(input));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ParseStatus, Is.EqualTo(EventParseStatus.Malformed));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            AssertInputLocation(result, input);
        });
    }

    [TestCase(MinecraftSourceAdapterHint.MinecraftLatestLog, "unfinished Minecraft record")]
    [TestCase(MinecraftSourceAdapterHint.YeezusTextLog, "unfinished Yeezus record")]
    [TestCase(MinecraftSourceAdapterHint.ReCactusSessionText, "unfinished ReCactus record")]
    [TestCase(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl, "{\"type\":\"CYCLE\"")]
    public void Incomplete_records_take_partial_precedence_and_preserve_source_location (
        MinecraftSourceAdapterHint hint,
        string rawText)
    {
        LogParserInput input = CreateInput(rawText, isComplete: false);
        NormalizedLogEvent result = MinecraftSourceParserResolver.Resolve(hint).Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Partial));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            AssertInputLocation(result, input);
        });
    }

    [Test]
    public void Cactus_monitor_invalid_complete_field_types_are_malformed ()
    {
        const string rawText = "{\"type\":\"CYCLE\",\"sequence\":\"eight\"}";
        NormalizedLogEvent result = new CactusMonitorSessionJsonlParser().Parse(CreateInput(rawText));

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Malformed));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            Assert.That(result.ProducerSequence, Is.Null);
        });
    }

    private static LogParserInput CreateInput (string rawText, bool isComplete = true)
    {
        var file = new FileRef("synthetic-file", "synthetic/session.log", 4);
        return new LogParserInput(
            file,
            SourceLocalSequence: 23,
            StartByteOffset: 128,
            EndByteOffset: 128 + Encoding.UTF8.GetByteCount(rawText),
            StartLineNumber: 17,
            EndLineNumber: 17 + rawText.Count(character => character == '\n'),
            rawText,
            isComplete);
    }

    private static string ReadFixture (string fileName) => File.ReadAllText(GetFixturePath(fileName));

    private static string GetFixturePath (string fileName) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "MinecraftLogs", fileName);

    private static void AssertInputLocation (NormalizedLogEvent result, LogParserInput input)
    {
        Assert.That(result.Ref.File, Is.SameAs(input.File));
        Assert.That(result.Ref.SourceLocalSequence, Is.EqualTo(input.SourceLocalSequence));
        Assert.That(result.Ref.StartByteOffset, Is.EqualTo(input.StartByteOffset));
        Assert.That(result.Ref.EndByteOffset, Is.EqualTo(input.EndByteOffset));
        Assert.That(result.Ref.StartLineNumber, Is.EqualTo(input.StartLineNumber));
        Assert.That(result.Ref.EndLineNumber, Is.EqualTo(input.EndLineNumber));
    }
}
