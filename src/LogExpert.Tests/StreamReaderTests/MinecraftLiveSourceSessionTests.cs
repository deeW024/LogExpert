using System.Collections.Concurrent;
using System.Text;

using LogExpert.Core.Classes.Log;
using LogExpert.Core.Classes.Log.Streamreaders;
using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Entities;
using LogExpert.Core.Enums;
using LogExpert.Core.Interfaces;

using NUnit.Framework;

namespace LogExpert.Tests.StreamReaderTests;

[TestFixture]
internal sealed class MinecraftLiveSourceSessionTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private string _testDirectory = null!;

    [SetUp]
    public void SetUp ()
    {
        _testDirectory = Path.Join(Path.GetTempPath(), "LogExpertTests", Guid.NewGuid().ToString());
        _ = Directory.CreateDirectory(_testDirectory);
        _ = PluginRegistry.PluginRegistry.Create(_testDirectory, 500);
    }

    [TearDown]
    public void TearDown ()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Test]
    public void Direct_reader_reports_exact_mixed_terminators_and_full_content_offsets_after_preamble ()
    {
        const string source = "é\n魚\r\nx\rtail";
        byte[] contentBytes = Utf8.GetBytes(source);
        byte[] preamble = [0xEF, 0xBB, 0xBF];
        byte[] bytes = [.. preamble, .. contentBytes];
        using var stream = new MemoryStream(bytes);
        using var reader = new PositionAwareStreamReaderDirect(
            stream,
            new EncodingOptions { Encoding = Utf8 },
            maximumLineLength: 2);

        var lines = new List<(string Display, string Full, PhysicalLineReadMetadata Metadata)>();
        while (reader.TryReadLineWithMetadata(out var display, out var full, out var metadata))
        {
            lines.Add((display.ToString(), full.ToString(), metadata));
        }

        Assert.Multiple(() =>
        {
            Assert.That(lines.Select(line => line.Full), Is.EqualTo(new[] { "é", "魚", "x", "tail" }));
            Assert.That(lines.Select(line => line.Display), Is.EqualTo(new[] { "é", "魚", "x", "ta" }));
            Assert.That(lines.Select(line => line.Metadata.Terminator), Is.EqualTo(new[]
            {
                PhysicalLineTerminator.Lf,
                PhysicalLineTerminator.CrLf,
                PhysicalLineTerminator.Cr,
                PhysicalLineTerminator.None
            }));
            Assert.That(lines.Select(line => line.Metadata.StartByteOffset), Is.EqualTo(new long[] { 0, 3, 8, 10 }));
            Assert.That(lines.Select(line => line.Metadata.EndByteOffset), Is.EqualTo(new long[] { 2, 6, 9, 14 }));
            Assert.That(lines.Select(line => line.Metadata.TerminatorByteLength), Is.EqualTo(new long[] { 1, 2, 1, 0 }));
            Assert.That(lines[^1].Metadata.IsTerminated, Is.False);
            Assert.That(reader.Position, Is.EqualTo(contentBytes.Length));
        });
    }

    [TestCase(ReaderType.System)]
    [TestCase(ReaderType.Legacy)]
    public void Minecraft_observer_rejects_readers_without_exact_metadata (ReaderType readerType)
    {
        SourceFixture fixture = CreateSource(MinecraftSourceAdapterHint.MinecraftLatestLog, string.Empty);
        using var reader = CreateReader(fixture.Path, readerType);

        Assert.Throws<NotSupportedException>(() => reader.MinecraftPhysicalLineObserver = new NoOpObserver());
    }

    [Test]
    public async Task Line_delimited_sources_emit_initial_load_and_append_once ( )
    {
        await AssertLineDelimitedInitialAndAppend(MinecraftSourceAdapterHint.ReCactusSessionText).ConfigureAwait(false);
        await AssertLineDelimitedInitialAndAppend(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl).ConfigureAwait(false);
    }

    [Test]
    public async Task Appended_suffix_completes_one_cumulative_multibyte_physical_line ()
    {
        string firstRecord = "{\"type\":\"CYCLE\",\"sessionId\":\"live\",\"sequence\":1,\"timestampEpochMillis\":1790181663412,\"message\":\"é\"}";
        int splitAt = firstRecord.IndexOf("é", StringComparison.Ordinal);
        string prefix = firstRecord[..splitAt];
        string suffix = firstRecord[splitAt..];
        const string secondRecord = "{\"type\":\"CYCLE\",\"sessionId\":\"live\",\"sequence\":2}";
        SourceFixture fixture = CreateSource(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl, prefix);
        using var reader = CreateReader(fixture.Path, maximumLineLength: 24);
        using var session = new MinecraftLiveSourceSession(fixture.Source, reader);
        var events = new ConcurrentQueue<NormalizedLogEvent>();
        session.EventsProduced += (_, args) =>
        {
            foreach (NormalizedLogEvent parsedEvent in args.Events)
            {
                events.Enqueue(parsedEvent);
            }
        };

        session.StartMonitoring();
        await WaitUntil(() => reader.FileSize == Utf8.GetByteCount(prefix), "Initial unterminated line was not read").ConfigureAwait(false);
        Assert.That(events, Is.Empty, "The pending source line must not be parsed as a separate event");

        await AppendTextAsync(fixture.Path, suffix + "\n" + secondRecord + "\n").ConfigureAwait(false);
        await WaitUntil(() => events.Count >= 2, "Appended records were not emitted").ConfigureAwait(false);

        NormalizedLogEvent[] emitted = events.ToArray();
        long firstRecordBytes = Utf8.GetByteCount(firstRecord);
        long secondStart = firstRecordBytes + 1;
        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Length.EqualTo(2));
            Assert.That(emitted[0].RawText, Is.EqualTo(firstRecord));
            Assert.That(emitted[0].ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(emitted[0].Ref.StartByteOffset, Is.Zero);
            Assert.That(emitted[0].Ref.EndByteOffset, Is.EqualTo(firstRecordBytes));
            Assert.That(emitted[0].Ref.StartLineNumber, Is.EqualTo(1));
            Assert.That(emitted[0].Ref.EndLineNumber, Is.EqualTo(1));
            Assert.That(emitted[1].RawText, Is.EqualTo(secondRecord));
            Assert.That(emitted[1].Ref.StartByteOffset, Is.EqualTo(secondStart));
            Assert.That(emitted[1].Ref.EndByteOffset, Is.EqualTo(secondStart + Utf8.GetByteCount(secondRecord)));
            Assert.That(emitted[1].Ref.StartLineNumber, Is.EqualTo(2));
            Assert.That(emitted[1].Ref.SourceLocalSequence, Is.EqualTo(2));
            Assert.That(emitted.All(item => item.Ref.File.Generation == 1), Is.True);
        });

        reader.StopMonitoring();
        reader.ReadFiles();
        Assert.That(events, Has.Count.EqualTo(2), "A repeated initial read must not re-emit source events");
    }

    [Test]
    public async Task Live_source_uses_reader_offsets_with_a_detected_preamble ()
    {
        const string json = "{\"type\":\"CYCLE\",\"sessionId\":\"bom\",\"sequence\":1}";
        string path = Path.Combine(_testDirectory, "cactusmonitor", "sessions", "cfm-bom.jsonl");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).ConfigureAwait(false);
        var workspace = new MinecraftWorkspace(_testDirectory);
        DiscoveredSourceFile source = new MinecraftSourceDiscovery(workspace)
            .Rescan()
            .Files
            .Single(file => file.AdapterHint == MinecraftSourceAdapterHint.CactusMonitorSessionJsonl);
        using var reader = CreateReader(path, maximumLineLength: 24);
        using var session = new MinecraftLiveSourceSession(source, reader);
        var events = new ConcurrentQueue<NormalizedLogEvent>();
        session.EventsProduced += (_, args) =>
        {
            foreach (NormalizedLogEvent parsedEvent in args.Events)
            {
                events.Enqueue(parsedEvent);
            }
        };

        session.StartMonitoring();
        await WaitUntil(() => events.Count == 1, "BOM-prefixed CFM event was not emitted").ConfigureAwait(false);

        NormalizedLogEvent parsed = events.Single();
        Assert.Multiple(() =>
        {
            Assert.That(parsed.RawText, Is.EqualTo(json));
            Assert.That(parsed.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(parsed.Ref.StartByteOffset, Is.Zero);
            Assert.That(parsed.Ref.EndByteOffset, Is.EqualTo(Utf8.GetByteCount(json)));
        });
    }

    [Test]
    public async Task Header_delimited_sources_emit_initial_multiline_records_and_keep_trailing_record_pending ()
    {
        await AssertHeaderDelimitedInitialAndAppend(MinecraftSourceAdapterHint.MinecraftLatestLog).ConfigureAwait(false);
        await AssertHeaderDelimitedInitialAndAppend(MinecraftSourceAdapterHint.YeezusTextLog).ConfigureAwait(false);
    }

    [Test]
    public async Task Truncate_finalizes_old_generation_before_reading_replacement ()
    {
        string oldPrefix = "{\"type\":\"CYCLE\",\"sessionId\":\"old\",\"description\":\"" + new string('x', 160);
        const string replacement = "{\"type\":\"CYCLE\",\"sessionId\":\"new\",\"sequence\":9}";
        SourceFixture fixture = CreateSource(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl, oldPrefix);
        using var reader = CreateReader(fixture.Path);
        using var session = new MinecraftLiveSourceSession(fixture.Source, reader);
        var events = new ConcurrentQueue<NormalizedLogEvent>();
        session.EventsProduced += (_, args) =>
        {
            foreach (NormalizedLogEvent parsedEvent in args.Events)
            {
                events.Enqueue(parsedEvent);
            }
        };

        session.StartMonitoring();
        await WaitUntil(() => reader.FileSize == Utf8.GetByteCount(oldPrefix), "Initial pending line was not read").ConfigureAwait(false);
        Assert.That(events, Is.Empty);

        Assert.That(Utf8.GetByteCount(replacement + "\n"), Is.LessThan(Utf8.GetByteCount(oldPrefix)));
        await File.WriteAllTextAsync(fixture.Path, replacement + "\n", Utf8).ConfigureAwait(false);
        await WaitUntil(() => events.Count >= 2, "Truncate/rewrite did not flush and reload both generations").ConfigureAwait(false);

        NormalizedLogEvent[] emitted = events.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Length.EqualTo(2));
            Assert.That(emitted[0].ParseStatus, Is.EqualTo(EventParseStatus.Partial));
            Assert.That(emitted[0].RawText, Is.EqualTo(oldPrefix));
            Assert.That(emitted[0].Ref.File.Generation, Is.EqualTo(1));
            Assert.That(emitted[0].Ref.SourceLocalSequence, Is.EqualTo(1));
            Assert.That(emitted[1].RawText, Is.EqualTo(replacement));
            Assert.That(emitted[1].Ref.File.Generation, Is.EqualTo(2));
            Assert.That(emitted[1].Ref.SourceLocalSequence, Is.EqualTo(2));
            Assert.That(session.CurrentFile.Generation, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Delete_finalizes_once_and_recreate_starts_exactly_one_new_generation ()
    {
        string pending = "{\"type\":\"CYCLE\",\"sessionId\":\"before-delete\",\"payload\":\"" + new string('x', 90);
        const string recreated = "{\"type\":\"CYCLE\",\"sessionId\":\"after-recreate\",\"sequence\":4}";
        SourceFixture fixture = CreateSource(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl, pending);
        using var reader = CreateReader(fixture.Path);
        using var session = new MinecraftLiveSourceSession(fixture.Source, reader);
        var events = new ConcurrentQueue<NormalizedLogEvent>();
        session.EventsProduced += (_, args) =>
        {
            foreach (NormalizedLogEvent parsedEvent in args.Events)
            {
                events.Enqueue(parsedEvent);
            }
        };

        session.StartMonitoring();
        await WaitUntil(() => reader.FileSize == Utf8.GetByteCount(pending), "Initial pending line was not read").ConfigureAwait(false);
        File.Delete(fixture.Path);
        await WaitUntil(() => session.IsDeleted && events.Count == 1, "Delete did not finalize the active generation").ConfigureAwait(false);

        session.OnSourceDeleted();
        session.OnSourceDeleted();
        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(session.CurrentFile.Generation, Is.EqualTo(1));
        });

        await File.WriteAllTextAsync(fixture.Path, recreated + "\n", Utf8).ConfigureAwait(false);
        await WaitUntil(() => events.Count == 2 && !session.IsDeleted, "Recreated file was not read in a new generation").ConfigureAwait(false);

        NormalizedLogEvent[] emitted = events.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Length.EqualTo(2));
            Assert.That(emitted[0].ParseStatus, Is.EqualTo(EventParseStatus.Partial));
            Assert.That(emitted[0].Ref.File.Generation, Is.EqualTo(1));
            Assert.That(emitted[1].Ref.File.Generation, Is.EqualTo(2));
            Assert.That(emitted[1].Ref.StartLineNumber, Is.EqualTo(1));
            Assert.That(emitted[1].Ref.StartByteOffset, Is.Zero);
            Assert.That(emitted[0].Ref.SourceLocalSequence, Is.EqualTo(1));
            Assert.That(emitted[1].Ref.SourceLocalSequence, Is.EqualTo(2));
            Assert.That(session.CurrentFile.Generation, Is.EqualTo(2));
        });
    }

    private async Task AssertLineDelimitedInitialAndAppend (MinecraftSourceAdapterHint adapterHint)
    {
        string first = adapterHint == MinecraftSourceAdapterHint.ReCactusSessionText
            ? "2026-09-23T16:41:03.412Z | INFO | live-session | COMPACT | first"
            : "{\"type\":\"CYCLE\",\"sessionId\":\"live\",\"sequence\":1,\"timestampEpochMillis\":1790181663412}";
        string second = adapterHint == MinecraftSourceAdapterHint.ReCactusSessionText
            ? "2026-09-23T16:41:04.412Z | INFO | live-session | COMPACT | second"
            : "{\"type\":\"CYCLE\",\"sessionId\":\"live\",\"sequence\":2}";
        SourceFixture fixture = CreateSource(adapterHint, first + "\n");
        using var reader = CreateReader(fixture.Path);
        using var session = new MinecraftLiveSourceSession(fixture.Source, reader);
        var events = new ConcurrentQueue<NormalizedLogEvent>();
        session.EventsProduced += (_, args) =>
        {
            foreach (NormalizedLogEvent parsedEvent in args.Events)
            {
                events.Enqueue(parsedEvent);
            }
        };

        session.StartMonitoring();
        await WaitUntil(() => events.Count == 1, $"Initial {adapterHint} event was not emitted").ConfigureAwait(false);
        await AppendTextAsync(fixture.Path, second + "\n").ConfigureAwait(false);
        await WaitUntil(() => events.Count == 2, $"Appended {adapterHint} event was not emitted").ConfigureAwait(false);

        NormalizedLogEvent[] emitted = events.ToArray();
        long expectedSecondStart = Utf8.GetByteCount(first + "\n");
        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Length.EqualTo(2));
            Assert.That(emitted[0].RawText, Is.EqualTo(first));
            Assert.That(emitted[1].RawText, Is.EqualTo(second));
            Assert.That(emitted[0].Ref.SourceLocalSequence, Is.EqualTo(1));
            Assert.That(emitted[1].Ref.SourceLocalSequence, Is.EqualTo(2));
            Assert.That(emitted[1].Ref.File.Generation, Is.EqualTo(1));
            Assert.That(emitted[1].Ref.StartByteOffset, Is.EqualTo(expectedSecondStart));
            Assert.That(emitted[1].Ref.StartLineNumber, Is.EqualTo(2));
        });
    }

    private async Task AssertHeaderDelimitedInitialAndAppend (MinecraftSourceAdapterHint adapterHint)
    {
        string firstHeader = adapterHint == MinecraftSourceAdapterHint.MinecraftLatestLog
            ? "[18:41:03] [Render thread/INFO]: first"
            : "2026-09-23T18:41:03.412+02:00 [INFO] [yeezus] [Client thread] first";
        string secondHeader = adapterHint == MinecraftSourceAdapterHint.MinecraftLatestLog
            ? "[18:41:04] [Render thread/WARN]: second"
            : "2026-09-23T18:41:04.412+02:00 [WARN] [yeezus] [Client thread] second";
        string thirdHeader = adapterHint == MinecraftSourceAdapterHint.MinecraftLatestLog
            ? "[18:41:05] [Render thread/INFO]: third"
            : "2026-09-23T18:41:05.412+02:00 [INFO] [yeezus] [Client thread] third";
        const string continuation = "    at synthetic.Test.call(Test.java:1)";
        SourceFixture fixture = CreateSource(adapterHint, $"{firstHeader}\n{continuation}\n{secondHeader}\n");
        using var reader = CreateReader(fixture.Path);
        using var session = new MinecraftLiveSourceSession(fixture.Source, reader);
        var events = new ConcurrentQueue<NormalizedLogEvent>();
        session.EventsProduced += (_, args) =>
        {
            foreach (NormalizedLogEvent parsedEvent in args.Events)
            {
                events.Enqueue(parsedEvent);
            }
        };

        session.StartMonitoring();
        await WaitUntil(() => reader.FileSize == Utf8.GetByteCount($"{firstHeader}\n{continuation}\n{secondHeader}\n"), "Initial multiline source was not read").ConfigureAwait(false);
        Assert.That(events, Has.Count.EqualTo(1), "The trailing header-delimited record must remain pending");

        await AppendTextAsync(fixture.Path, thirdHeader + "\n").ConfigureAwait(false);
        await WaitUntil(() => events.Count == 2, "The pending multiline record was not closed by the next header").ConfigureAwait(false);

        NormalizedLogEvent[] emitted = events.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(emitted[0].RawText, Is.EqualTo(firstHeader + "\n" + continuation));
            Assert.That(emitted[0].Ref.StartLineNumber, Is.EqualTo(1));
            Assert.That(emitted[0].Ref.EndLineNumber, Is.EqualTo(2));
            Assert.That(emitted[0].Ref.EndByteOffset, Is.EqualTo(Utf8.GetByteCount(firstHeader + "\n" + continuation)));
            Assert.That(emitted[1].RawText, Is.EqualTo(secondHeader));
            Assert.That(emitted[1].Ref.StartLineNumber, Is.EqualTo(3));
            Assert.That(emitted[1].Ref.EndLineNumber, Is.EqualTo(3));
            Assert.That(emitted[1].Ref.SourceLocalSequence, Is.EqualTo(2));
        });
    }

    private SourceFixture CreateSource (MinecraftSourceAdapterHint adapterHint, string initialContent)
    {
        string relativePath = adapterHint switch
        {
            MinecraftSourceAdapterHint.MinecraftLatestLog => Path.Combine("logs", "latest.log"),
            MinecraftSourceAdapterHint.YeezusTextLog => Path.Combine("logs", "yeezus.log"),
            MinecraftSourceAdapterHint.ReCactusSessionText => Path.Combine("logs", "reccactus", "reccactus-live.txt"),
            MinecraftSourceAdapterHint.CactusMonitorSessionJsonl => Path.Combine("cactusmonitor", "sessions", "cfm-live.jsonl"),
            _ => throw new ArgumentOutOfRangeException(nameof(adapterHint), adapterHint, null)
        };

        string path = Path.Combine(_testDirectory, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, initialContent, Utf8);

        var workspace = new MinecraftWorkspace(_testDirectory);
        DiscoveredSourceFile source = new MinecraftSourceDiscovery(workspace)
            .Rescan()
            .Files
            .Single(file => file.AdapterHint == adapterHint);

        return new SourceFixture(path, source);
    }

    private static LogfileReader CreateReader (string path, ReaderType readerType = ReaderType.SystemDirect, int maximumLineLength = 500) =>
        new(
            path,
            new EncodingOptions { Encoding = Utf8 },
            multiFile: false,
            bufferCount: 40,
            linesPerBuffer: 50,
            new MultiFileOptions(),
            readerType,
            PluginRegistry.PluginRegistry.Instance,
            maximumLineLength);

    private static Task AppendTextAsync (string path, string text) => File.AppendAllTextAsync(path, text, Utf8);

    private static async Task WaitUntil (Func<bool> condition, string failureMessage)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail(failureMessage);
    }

    private sealed record SourceFixture (string Path, DiscoveredSourceFile Source);

    private sealed class NoOpObserver : IMinecraftPhysicalLineObserver
    {
        public void OnPhysicalLine (MinecraftPhysicalLineRead line) { }

        public void OnSourceTruncated () { }

        public void OnSourceDeleted () { }

        public void OnSourceRecreated () { }
    }
}
