using System.Globalization;
using System.Text;
using LogExpert.Core.Classes.MinecraftLogs;
using NUnit.Framework;

namespace LogExpert.Tests;

[TestFixture]
public class NormalizedLogEventContractTests
{
    private static readonly string[] RepresentativeFixtures =
    [
        "latest.log",
        "yeezus.log",
        "reccactus-session.txt",
        "cactusmonitor-session.jsonl"
    ];

    [TestCaseSource(nameof(RepresentativeFixtures))]
    public void Unknown_parser_preserves_representative_source_records (string fixtureName)
    {
        string rawText = File.ReadAllText(GetFixturePath(fixtureName));
        long endLineNumber = 4 + rawText.Count(character => character == '\n');
        if (rawText.EndsWith('\n'))
        {
            endLineNumber--;
        }

        var file = new FileRef("fixture-file", fixtureName, 2);
        var input = new LogParserInput(
            file,
            SourceLocalSequence: 3,
            StartByteOffset: 11,
            EndByteOffset: 11 + Encoding.UTF8.GetByteCount(rawText),
            StartLineNumber: 4,
            EndLineNumber: endLineNumber,
            rawText,
            IsComplete: true);

        NormalizedLogEvent result = new UnknownLogEventParser().Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.RawText, Is.EqualTo(rawText));
            Assert.That(result.Message, Is.EqualTo(rawText));
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Unsupported));
            Assert.That(result.Ref.File, Is.EqualTo(file));
            Assert.That(result.Ref.SourceLocalSequence, Is.EqualTo(3));
            Assert.That(result.Ref.StartByteOffset, Is.EqualTo(11));
            Assert.That(result.Ref.EndByteOffset, Is.EqualTo(input.EndByteOffset));
            Assert.That(result.Ref.StartLineNumber, Is.EqualTo(4));
            Assert.That(result.Ref.EndLineNumber, Is.EqualTo(input.EndLineNumber));
            Assert.That(result.Source.IsKnown, Is.False);
            Assert.That(result.Component.IsKnown, Is.False);
            Assert.That(result.Level.IsKnown, Is.False);
            Assert.That(result.RawLevel, Is.Null);
            Assert.That(result.Thread.IsKnown, Is.False);
            Assert.That(result.Timestamp.Provenance, Is.EqualTo(TimestampProvenance.Unknown));
        });
    }

    [Test]
    public void Incomplete_final_record_is_preserved_as_partial_unknown_event ()
    {
        string rawText = File.ReadAllText(GetFixturePath("cactusmonitor-incomplete.jsonl"));
        var input = new LogParserInput(
            new FileRef("cactus-monitor", "cactusmonitor-incomplete.jsonl", 1),
            SourceLocalSequence: 8,
            StartByteOffset: 512,
            EndByteOffset: 512 + Encoding.UTF8.GetByteCount(rawText),
            StartLineNumber: 9,
            EndLineNumber: 9,
            rawText,
            IsComplete: false);

        NormalizedLogEvent result = new UnknownLogEventParser().Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.ParseStatus, Is.EqualTo(EventParseStatus.Partial));
            Assert.That(result.RawText, Is.EqualTo(rawText));
            Assert.That(result.Ref.SourceLocalSequence, Is.EqualTo(8));
            Assert.That(result.Component.IsKnown, Is.False);
        });
    }

    [Test]
    public void Explicit_producer_metadata_wins_over_heuristic_attribution ()
    {
        var heuristic = Attribution.From(
            "OneBlock",
            AttributionProvenance.Heuristic,
            AttributionConfidence.Exact);
        var producerMetadata = Attribution.From(
            "ReCactus",
            AttributionProvenance.ExplicitMetadata,
            AttributionConfidence.Low);

        AttributedValue<string> selected = Attribution.Prefer(heuristic, producerMetadata);

        Assert.Multiple(() =>
        {
            Assert.That(selected.Value, Is.EqualTo("ReCactus"));
            Assert.That(selected.Provenance, Is.EqualTo(AttributionProvenance.ExplicitMetadata));
            Assert.That(selected.Confidence, Is.EqualTo(AttributionConfidence.Low));
        });
    }

    [Test]
    public void Prefer_uses_confidence_when_provenance_is_equal ()
    {
        var lowConfidence = Attribution.From(
            "parsed-low",
            AttributionProvenance.Parsed,
            AttributionConfidence.Low);
        var highConfidence = Attribution.From(
            "parsed-high",
            AttributionProvenance.Parsed,
            AttributionConfidence.High);

        AttributedValue<string> selected = Attribution.Prefer(lowConfidence, highConfidence);

        Assert.Multiple(() =>
        {
            Assert.That(selected.Value, Is.EqualTo("parsed-high"));
            Assert.That(selected.Provenance, Is.EqualTo(AttributionProvenance.Parsed));
            Assert.That(selected.Confidence, Is.EqualTo(AttributionConfidence.High));
        });
    }

    [Test]
    public void Unknown_value_type_does_not_expose_a_default_domain_value ()
    {
        AttributedValue<LogLevel> unknown = Attribution.Unknown<LogLevel>();
        var knownTrace = Attribution.From(LogLevel.Trace, AttributionProvenance.Parsed, AttributionConfidence.Exact);

        Assert.Multiple(() =>
        {
            Assert.That(unknown.IsKnown, Is.False);
            Assert.That(unknown.TryGetValue(out _), Is.False);
            Assert.That(() => unknown.Value, Throws.InvalidOperationException);
            Assert.That(knownTrace.TryGetValue(out LogLevel level), Is.True);
            Assert.That(level, Is.EqualTo(LogLevel.Trace));
        });
    }

    [Test]
    public void Timestamp_keeps_source_and_ingest_provenance_distinct ()
    {
        const string rawTimestamp = "2026-09-23T18:41:03.412+02:00";
        DateTimeOffset parsed = DateTimeOffset.Parse(rawTimestamp, CultureInfo.InvariantCulture);
        EventTimestamp sourceTime = EventTimestamp.FromSource(parsed, rawTimestamp);
        EventTimestamp ingestTime = EventTimestamp.FromIngest(parsed);

        Assert.Multiple(() =>
        {
            Assert.That(sourceTime.Provenance, Is.EqualTo(TimestampProvenance.Source));
            Assert.That(sourceTime.RawValue, Is.EqualTo(rawTimestamp));
            Assert.That(ingestTime.Provenance, Is.EqualTo(TimestampProvenance.Ingest));
            Assert.That(ingestTime.RawValue, Is.Null);
            Assert.That(EventTimestamp.Unknown().Value, Is.Null);
        });
    }

    [Test]
    public void Normalized_event_keeps_known_fields_unknowns_and_producer_sequence ()
    {
        string fixturePath = GetFixturePath("reccactus-session.txt");
        string[] fixtureLines = File.ReadAllLines(fixturePath);
        string rawText = fixtureLines[1];
        string[] fields = rawText.Split(',', 5);
        long startByteOffset = Array.IndexOf(File.ReadAllBytes(fixturePath), (byte)'\n') + 1;
        string rawTimestamp = fields[0];
        var result = new NormalizedLogEvent(
            new EventRef(
                new FileRef("reccactus", "reccactus-session.txt", 1),
                1,
                startByteOffset,
                startByteOffset + Encoding.UTF8.GetByteCount(rawText),
                2,
                2),
            Attribution.From("Yeezus", AttributionProvenance.Adapter, AttributionConfidence.High),
            Attribution.From("ReCactus", AttributionProvenance.Adapter, AttributionConfidence.High),
            Attribution.From(LogLevel.Info, AttributionProvenance.Parsed, AttributionConfidence.High),
            fields[1],
            Attribution.Unknown<string>(),
            EventTimestamp.FromSource(DateTimeOffset.Parse(rawTimestamp, CultureInfo.InvariantCulture), rawTimestamp),
            EventParseStatus.Parsed,
            fields[4],
            rawText,
            ProducerSequence: 7);

        Assert.Multiple(() =>
        {
            Assert.That(result.Source.Value, Is.EqualTo("Yeezus"));
            Assert.That(result.Component.Provenance, Is.EqualTo(AttributionProvenance.Adapter));
            Assert.That(result.Level.Value, Is.EqualTo(LogLevel.Info));
            Assert.That(result.RawLevel, Is.EqualTo("INFO"));
            Assert.That(result.Thread.IsKnown, Is.False);
            Assert.That(result.ProducerSequence, Is.EqualTo(7));
            Assert.That(result.Ref.SourceLocalSequence, Is.EqualTo(1));
            Assert.That(result.Timestamp.RawValue, Is.EqualTo(rawTimestamp));
        });
    }

    [Test]
    public void Multiline_record_remains_one_event_with_source_local_reference ()
    {
        string rawText = File.ReadAllText(GetFixturePath("yeezus.log"));
        var input = new LogParserInput(
            new FileRef("yeezus", "yeezus.log", 1),
            SourceLocalSequence: 2,
            StartByteOffset: 0,
            EndByteOffset: Encoding.UTF8.GetByteCount(rawText),
            StartLineNumber: 20,
            EndLineNumber: 22,
            rawText,
            IsComplete: true);

        NormalizedLogEvent result = new UnknownLogEventParser().Parse(input);

        Assert.Multiple(() =>
        {
            Assert.That(result.Message, Is.EqualTo(rawText));
            Assert.That(result.Ref.StartLineNumber, Is.EqualTo(20));
            Assert.That(result.Ref.EndLineNumber, Is.EqualTo(22));
            Assert.That(result.Ref.SourceLocalSequence, Is.EqualTo(2));
        });
    }

    private static string GetFixturePath (string fixtureName) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "MinecraftLogs", fixtureName);
}
