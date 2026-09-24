using System.Text;
using LogExpert.Core.Classes.MinecraftLogs;
using NUnit.Framework;

namespace LogExpert.Tests;

[TestFixture]
public sealed class MinecraftLogRecordFramerTests
{
    [Test]
    public void Framing_resolver_selects_the_strategy_for_all_adapter_hints ()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MinecraftSourceFramingResolver.Resolve(MinecraftSourceAdapterHint.ReCactusSessionText),
                Is.EqualTo(LogicalRecordFramingStrategy.LineDelimited));
            Assert.That(MinecraftSourceFramingResolver.Resolve(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl),
                Is.EqualTo(LogicalRecordFramingStrategy.LineDelimited));
            Assert.That(MinecraftSourceFramingResolver.Resolve(MinecraftSourceAdapterHint.MinecraftLatestLog),
                Is.EqualTo(LogicalRecordFramingStrategy.HeaderDelimited));
            Assert.That(MinecraftSourceFramingResolver.Resolve(MinecraftSourceAdapterHint.YeezusTextLog),
                Is.EqualTo(LogicalRecordFramingStrategy.HeaderDelimited));
        });
    }

    [TestCase(MinecraftSourceAdapterHint.ReCactusSessionText,
        "2026-09-24T10:00:00Z | INFO | synthetic-session | COMPACT | ready")]
    [TestCase(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl,
        "{\"type\":\"CYCLE\",\"sequence\":4,\"timestampEpochMillis\":1790244000000}")]
    public void Line_delimited_adapters_emit_one_terminated_line_without_its_delimiter (
        MinecraftSourceAdapterHint adapterHint,
        string content)
    {
        FileRef file = CreateFile();
        var framer = MinecraftSourceFramingResolver.CreateFramer(adapterHint);
        framer.BeginGeneration(file);
        PhysicalLineObservation line = CreateLine(file, 1, 50, content, PhysicalLineTerminator.CrLf);

        IReadOnlyList<LogParserInput> inputs = framer.Accept(line);
        NormalizedLogEvent parsed = MinecraftSourceEventParser.Parse(adapterHint, inputs).Single();

        Assert.Multiple(() =>
        {
            Assert.That(inputs, Has.Count.EqualTo(1));
            Assert.That(inputs[0].RawText, Is.EqualTo(content));
            Assert.That(inputs[0].IsComplete, Is.True);
            Assert.That(inputs[0].StartByteOffset, Is.EqualTo(50));
            Assert.That(inputs[0].EndByteOffset, Is.EqualTo(line.EndByteOffset));
            Assert.That(inputs[0].StartLineNumber, Is.EqualTo(1));
            Assert.That(inputs[0].EndLineNumber, Is.EqualTo(1));
            Assert.That(parsed.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(parsed.Ref.File, Is.SameAs(file));
            Assert.That(parsed.Ref.SourceLocalSequence, Is.EqualTo(inputs[0].SourceLocalSequence));
            Assert.That(parsed.Ref.StartByteOffset, Is.EqualTo(inputs[0].StartByteOffset));
            Assert.That(parsed.Ref.EndByteOffset, Is.EqualTo(inputs[0].EndByteOffset));
        });
    }

    [Test]
    public void Incomplete_line_is_held_across_snapshots_then_emitted_once_when_completed ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl);
        framer.BeginGeneration(file);
        const string firstSnapshot = "{\"type\":\"CYCLE\"";
        const string fullContent = "{\"type\":\"CYCLE\",\"sequence\":5}";
        PhysicalLineObservation pending = CreateLine(file, 9, 80, firstSnapshot, PhysicalLineTerminator.None);
        PhysicalLineObservation completed = CreateLine(file, 9, 80, fullContent, PhysicalLineTerminator.Lf);

        IReadOnlyList<LogParserInput> first = framer.Accept(pending);
        IReadOnlyList<LogParserInput> duplicateSnapshot = framer.Accept(pending);
        IReadOnlyList<LogParserInput> output = framer.Accept(completed);
        IReadOnlyList<LogParserInput> duplicateCompleted = framer.Accept(completed);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Empty);
            Assert.That(duplicateSnapshot, Is.Empty);
            Assert.That(output, Has.Count.EqualTo(1));
            Assert.That(output[0].RawText, Is.EqualTo(fullContent));
            Assert.That(output[0].IsComplete, Is.True);
            Assert.That(output[0].StartLineNumber, Is.EqualTo(9));
            Assert.That(output[0].EndLineNumber, Is.EqualTo(9));
            Assert.That(duplicateCompleted, Is.Empty);
        });
    }

    [Test]
    public void Line_delimited_replay_checkpoint_starts_at_pending_partial_line_and_is_read_only ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl);
        framer.BeginGeneration(file);
        const string partial = "{\"type\":\"CYCLE\",\"sequence\":";
        const string complete = partial + "8}";
        PhysicalLineObservation pending = CreateLine(file, 4, 90, partial, PhysicalLineTerminator.None);
        framer.Accept(pending);

        LogicalRecordReplayStart checkpoint = framer.GetReplayStart(pending.EndByteOffset, nextPhysicalLineNumber: 4);
        LogicalRecordReplayStart repeated = framer.GetReplayStart(pending.EndByteOffset, nextPhysicalLineNumber: 4);
        IReadOnlyList<LogParserInput> completed = framer.Accept(
            CreateLine(file, 4, 90, complete, PhysicalLineTerminator.Lf));

        Assert.Multiple(() =>
        {
            Assert.That(checkpoint, Is.EqualTo(repeated));
            Assert.That(checkpoint.ByteOffset, Is.EqualTo(90));
            Assert.That(checkpoint.PhysicalLineNumber, Is.EqualTo(4));
            Assert.That(checkpoint.HasUnemittedState, Is.True);
            Assert.That(completed, Has.Count.EqualTo(1));
            Assert.That(completed[0].RawText, Is.EqualTo(complete));
            Assert.That(completed[0].SourceLocalSequence, Is.EqualTo(1));
        });
    }

    [Test]
    public void Line_delimited_replay_checkpoint_at_consumed_frontier_is_exact_and_read_only ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl);
        framer.BeginGeneration(file);
        const string first = "{\"type\":\"CYCLE\",\"sequence\":1}";
        const string next = "{\"type\":\"END\",\"sequence\":2}";
        PhysicalLineObservation completed = CreateLine(file, 7, 120, first, PhysicalLineTerminator.Lf);
        Assert.That(framer.Accept(completed), Has.Count.EqualTo(1));

        long consumedFrontier = completed.EndByteOffset + completed.TerminatorByteLength;
        const long nextLine = 8;
        LogicalRecordReplayStart checkpoint = framer.GetReplayStart(consumedFrontier, nextLine);
        LogicalRecordReplayStart repeated = framer.GetReplayStart(consumedFrontier, nextLine);
        IReadOnlyList<LogParserInput> afterCheckpoint = framer.Accept(
            CreateLine(file, nextLine, consumedFrontier, next, PhysicalLineTerminator.Lf));

        Assert.Multiple(() =>
        {
            Assert.That(checkpoint, Is.EqualTo(repeated));
            Assert.That(checkpoint.ByteOffset, Is.EqualTo(consumedFrontier));
            Assert.That(checkpoint.PhysicalLineNumber, Is.EqualTo(nextLine));
            Assert.That(checkpoint.HasUnemittedState, Is.False);
            Assert.That(afterCheckpoint, Has.Count.EqualTo(1));
            Assert.That(afterCheckpoint[0].RawText, Is.EqualTo(next));
            Assert.That(afterCheckpoint[0].SourceLocalSequence, Is.EqualTo(2));
        });
    }

    [Test]
    public void Header_delimited_replay_checkpoint_starts_at_pending_event_header_without_finalizing_it ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.YeezusTextLog);
        framer.BeginGeneration(file);
        const string header = "2026-09-24T10:00:00+02:00 [ERROR] [yeezus-core] [Client thread] failed";
        const string stack = "    at example.Client.tick(Client.java:1)";
        const string nextHeader = "2026-09-24T10:00:01+02:00 [INFO] [yeezus] [Client thread] recovered";
        PhysicalLineObservation first = CreateLine(file, 20, 100, header, PhysicalLineTerminator.CrLf);
        PhysicalLineObservation second = CreateLine(file, 21, NextStart(first), stack, PhysicalLineTerminator.Lf);
        PhysicalLineObservation third = CreateLine(file, 22, NextStart(second), nextHeader, PhysicalLineTerminator.Lf);
        framer.Accept(first);
        framer.Accept(second);

        LogicalRecordReplayStart checkpoint = framer.GetReplayStart(NextStart(second), nextPhysicalLineNumber: 22);
        IReadOnlyList<LogParserInput> closed = framer.Accept(third);

        Assert.Multiple(() =>
        {
            Assert.That(checkpoint.ByteOffset, Is.EqualTo(first.StartByteOffset));
            Assert.That(checkpoint.PhysicalLineNumber, Is.EqualTo(first.LineNumber));
            Assert.That(checkpoint.HasUnemittedState, Is.True);
            Assert.That(closed, Has.Count.EqualTo(1));
            Assert.That(closed[0].RawText, Is.EqualTo(header + "\r\n" + stack));
            Assert.That(closed[0].SourceLocalSequence, Is.EqualTo(1));
        });
    }

    [Test]
    public void Finalizing_an_unterminated_line_emits_one_partial_record_for_the_parser ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.ReCactusSessionText);
        framer.BeginGeneration(file);
        const string content = "2026-09-24T10:00:00Z | INFO | synthetic-session | COMPACT | unfinished";
        framer.Accept(CreateLine(file, 4, 300, content, PhysicalLineTerminator.None));

        IReadOnlyList<LogParserInput> flushed = framer.FinalizeGeneration();
        IReadOnlyList<LogParserInput> secondFinalize = framer.FinalizeGeneration();
        NormalizedLogEvent parsed = MinecraftSourceEventParser.Parse(
            MinecraftSourceAdapterHint.ReCactusSessionText,
            flushed).Single();

        Assert.Multiple(() =>
        {
            Assert.That(flushed, Has.Count.EqualTo(1));
            Assert.That(flushed[0].RawText, Is.EqualTo(content));
            Assert.That(flushed[0].IsComplete, Is.False);
            Assert.That(flushed[0].StartByteOffset, Is.EqualTo(300));
            Assert.That(flushed[0].EndByteOffset, Is.EqualTo(300 + Encoding.UTF8.GetByteCount(content)));
            Assert.That(parsed.ParseStatus, Is.EqualTo(EventParseStatus.Partial));
            Assert.That(parsed.Ref.File, Is.SameAs(file));
            Assert.That(parsed.Ref.SourceLocalSequence, Is.EqualTo(flushed[0].SourceLocalSequence));
            Assert.That(secondFinalize, Is.Empty);
        });
    }

    [Test]
    public void Yeezus_multiline_record_preserves_each_internal_terminator_and_closes_at_next_header ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.YeezusTextLog);
        framer.BeginGeneration(file);
        const string header = "2026-09-24T10:00:00+02:00 [ERROR] [yeezus-core] [Client thread] failed";
        const string stack = "java.lang.IllegalStateException: synthetic";
        const string stackLine = "    at example.Client.tick(Client.java:1)";
        const string nextHeader = "2026-09-24T10:00:01+02:00 [INFO] [yeezus] [Client thread] recovered";
        PhysicalLineObservation first = CreateLine(file, 10, 100, header, PhysicalLineTerminator.Lf);
        PhysicalLineObservation second = CreateLine(file, 11, NextStart(first), stack, PhysicalLineTerminator.CrLf);
        PhysicalLineObservation third = CreateLine(file, 12, NextStart(second), stackLine, PhysicalLineTerminator.Lf);
        PhysicalLineObservation fourth = CreateLine(file, 13, NextStart(third), nextHeader, PhysicalLineTerminator.Lf);

        Assert.That(framer.Accept(first), Is.Empty);
        Assert.That(framer.Accept(second), Is.Empty);
        Assert.That(framer.Accept(third), Is.Empty);
        IReadOnlyList<LogParserInput> closed = framer.Accept(fourth);
        NormalizedLogEvent parsed = MinecraftSourceEventParser.Parse(MinecraftSourceAdapterHint.YeezusTextLog, closed)
            .Single();
        IReadOnlyList<LogParserInput> final = framer.FinalizeGeneration();

        Assert.Multiple(() =>
        {
            Assert.That(closed, Has.Count.EqualTo(1));
            Assert.That(closed[0].RawText, Is.EqualTo(header + "\n" + stack + "\r\n" + stackLine));
            Assert.That(closed[0].StartByteOffset, Is.EqualTo(first.StartByteOffset));
            Assert.That(closed[0].EndByteOffset, Is.EqualTo(third.EndByteOffset));
            Assert.That(closed[0].StartLineNumber, Is.EqualTo(10));
            Assert.That(closed[0].EndLineNumber, Is.EqualTo(12));
            Assert.That(closed[0].IsComplete, Is.True);
            Assert.That(parsed.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(parsed.RawText, Is.EqualTo(closed[0].RawText));
            Assert.That(parsed.Ref.File, Is.SameAs(file));
            Assert.That(parsed.Ref.SourceLocalSequence, Is.EqualTo(1));
            Assert.That(final, Has.Count.EqualTo(1));
            Assert.That(final[0].RawText, Is.EqualTo(nextHeader));
            Assert.That(final[0].SourceLocalSequence, Is.EqualTo(2));
        });
    }

    [Test]
    public void Minecraft_multiline_record_preserves_crlf_and_lf_and_keeps_exact_utf8_offsets ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.MinecraftLatestLog);
        framer.BeginGeneration(file);
        const string header = "[10:00:00] [Render thread/ERROR]: café 🐈";
        const string continuation = "    at example.Client.tick(Client.java:1) café";
        const string nextHeader = "[10:00:01] [Render thread/INFO]: recovered";
        PhysicalLineObservation first = CreateLine(file, 30, 500, header, PhysicalLineTerminator.CrLf);
        PhysicalLineObservation second = CreateLine(file, 31, NextStart(first), continuation, PhysicalLineTerminator.Lf);
        PhysicalLineObservation third = CreateLine(file, 32, NextStart(second), nextHeader, PhysicalLineTerminator.Lf);

        framer.Accept(first);
        framer.Accept(second);
        IReadOnlyList<LogParserInput> closed = framer.Accept(third);
        NormalizedLogEvent parsed = MinecraftSourceEventParser.Parse(
            MinecraftSourceAdapterHint.MinecraftLatestLog,
            closed).Single();

        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetByteCount(header), Is.GreaterThan(header.Length));
            Assert.That(first.EndByteOffset, Is.EqualTo(500 + Encoding.UTF8.GetByteCount(header)));
            Assert.That(second.StartByteOffset, Is.EqualTo(first.EndByteOffset + 2));
            Assert.That(closed[0].RawText, Is.EqualTo(header + "\r\n" + continuation));
            Assert.That(closed[0].StartByteOffset, Is.EqualTo(500));
            Assert.That(closed[0].EndByteOffset, Is.EqualTo(second.EndByteOffset));
            Assert.That(closed[0].StartLineNumber, Is.EqualTo(30));
            Assert.That(closed[0].EndLineNumber, Is.EqualTo(31));
            Assert.That(parsed.ParseStatus, Is.EqualTo(EventParseStatus.Parsed));
            Assert.That(parsed.Ref.StartByteOffset, Is.EqualTo(500));
            Assert.That(parsed.Ref.EndByteOffset, Is.EqualTo(second.EndByteOffset));
            Assert.That(parsed.RawText, Is.EqualTo(closed[0].RawText));
        });
    }

    [Test]
    public void Finalizing_a_pending_multiline_record_uses_terminated_state_for_completeness ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.MinecraftLatestLog);
        framer.BeginGeneration(file);
        const string header = "[10:00:00] [Render thread/INFO]: started";
        PhysicalLineObservation line = CreateLine(file, 1, 0, header, PhysicalLineTerminator.Cr);
        framer.Accept(line);

        IReadOnlyList<LogParserInput> flushed = framer.FinalizeGeneration();

        Assert.Multiple(() =>
        {
            Assert.That(flushed, Has.Count.EqualTo(1));
            Assert.That(flushed[0].RawText, Is.EqualTo(header));
            Assert.That(flushed[0].IsComplete, Is.True);
            Assert.That(flushed[0].StartLineNumber, Is.EqualTo(1));
            Assert.That(flushed[0].EndLineNumber, Is.EqualTo(1));
        });
    }

    [Test]
    public void Unterminated_multiline_continuation_is_preserved_and_parsed_as_partial ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.MinecraftLatestLog);
        framer.BeginGeneration(file);
        const string header = "[10:00:00] [Render thread/ERROR]: failed";
        const string continuation = "    at example.Client.tick(Client.java:1)";
        PhysicalLineObservation first = CreateLine(file, 20, 1000, header, PhysicalLineTerminator.CrLf);
        PhysicalLineObservation pending = CreateLine(file, 21, NextStart(first), continuation, PhysicalLineTerminator.None);

        Assert.That(framer.Accept(first), Is.Empty);
        Assert.That(framer.Accept(pending), Is.Empty);
        IReadOnlyList<LogParserInput> flushed = framer.FinalizeGeneration();
        NormalizedLogEvent parsed = MinecraftSourceEventParser.Parse(
            MinecraftSourceAdapterHint.MinecraftLatestLog,
            flushed).Single();

        Assert.Multiple(() =>
        {
            Assert.That(flushed, Has.Count.EqualTo(1));
            Assert.That(flushed[0].RawText, Is.EqualTo(header + "\r\n" + continuation));
            Assert.That(flushed[0].IsComplete, Is.False);
            Assert.That(flushed[0].StartLineNumber, Is.EqualTo(20));
            Assert.That(flushed[0].EndLineNumber, Is.EqualTo(21));
            Assert.That(parsed.ParseStatus, Is.EqualTo(EventParseStatus.Partial));
            Assert.That(parsed.Ref.File, Is.SameAs(file));
            Assert.That(parsed.Ref.StartLineNumber, Is.EqualTo(20));
            Assert.That(parsed.Ref.EndLineNumber, Is.EqualTo(21));
        });
    }

    [Test]
    public void Orphan_continuation_is_emitted_instead_of_disappearing ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.YeezusTextLog);
        framer.BeginGeneration(file);
        const string orphan = "    at example.Client.tick(Client.java:1)";

        IReadOnlyList<LogParserInput> emitted = framer.Accept(
            CreateLine(file, 6, 700, orphan, PhysicalLineTerminator.Lf));
        NormalizedLogEvent parsed = MinecraftSourceEventParser.Parse(MinecraftSourceAdapterHint.YeezusTextLog, emitted)
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Count.EqualTo(1));
            Assert.That(emitted[0].RawText, Is.EqualTo(orphan));
            Assert.That(emitted[0].IsComplete, Is.True);
            Assert.That(emitted[0].StartLineNumber, Is.EqualTo(6));
            Assert.That(emitted[0].EndLineNumber, Is.EqualTo(6));
            Assert.That(parsed.ParseStatus, Is.EqualTo(EventParseStatus.Malformed));
            Assert.That(parsed.RawText, Is.EqualTo(orphan));
        });
    }

    [Test]
    public void New_generation_finalizes_old_record_and_keeps_sequence_monotonic_without_crossing_generations ()
    {
        FileRef generationOne = CreateFile(generation: 1);
        FileRef generationTwo = CreateFile(generation: 2);
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.YeezusTextLog);
        framer.BeginGeneration(generationOne);
        const string oldHeader = "2026-09-24T10:00:00Z [INFO] [yeezus] [Client thread] old";
        const string newHeader = "2026-09-24T10:00:01Z [INFO] [yeezus] [Client thread] new";
        framer.Accept(CreateLine(generationOne, 40, 900, oldHeader, PhysicalLineTerminator.Lf));

        Assert.Throws<InvalidOperationException>(() => framer.BeginGeneration(CreateFile(generation: 0)));

        IReadOnlyList<LogParserInput> oldGeneration = framer.BeginGeneration(generationTwo);
        framer.Accept(CreateLine(generationTwo, 1, 0, newHeader, PhysicalLineTerminator.Lf));
        IReadOnlyList<LogParserInput> newGeneration = framer.FinalizeGeneration();

        Assert.Multiple(() =>
        {
            Assert.That(oldGeneration, Has.Count.EqualTo(1));
            Assert.That(oldGeneration[0].File, Is.SameAs(generationOne));
            Assert.That(oldGeneration[0].File.Generation, Is.EqualTo(1));
            Assert.That(oldGeneration[0].RawText, Is.EqualTo(oldHeader));
            Assert.That(newGeneration, Has.Count.EqualTo(1));
            Assert.That(newGeneration[0].File, Is.SameAs(generationTwo));
            Assert.That(newGeneration[0].File.Generation, Is.EqualTo(2));
            Assert.That(newGeneration[0].RawText, Is.EqualTo(newHeader));
            Assert.That(newGeneration[0].SourceLocalSequence, Is.EqualTo(oldGeneration[0].SourceLocalSequence + 1));
            Assert.That(oldGeneration[0].StartByteOffset, Is.EqualTo(900));
            Assert.That(newGeneration[0].StartByteOffset, Is.EqualTo(0));
        });
    }

    [Test]
    public void Starting_a_new_generation_flushes_an_old_unterminated_line_once ()
    {
        FileRef generationOne = CreateFile(generation: 1);
        FileRef generationTwo = CreateFile(generation: 2);
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.ReCactusSessionText);
        framer.BeginGeneration(generationOne);
        const string unfinished = "2026-09-24T10:00:00Z | INFO | synthetic-session | COMPACT | partial";
        framer.Accept(CreateLine(generationOne, 7, 200, unfinished, PhysicalLineTerminator.None));

        IReadOnlyList<LogParserInput> oldGeneration = framer.BeginGeneration(generationTwo);
        IReadOnlyList<LogParserInput> secondFinalize = framer.FinalizeGeneration();

        Assert.Multiple(() =>
        {
            Assert.That(oldGeneration, Has.Count.EqualTo(1));
            Assert.That(oldGeneration[0].File.Generation, Is.EqualTo(1));
            Assert.That(oldGeneration[0].IsComplete, Is.False);
            Assert.That(oldGeneration[0].RawText, Is.EqualTo(unfinished));
            Assert.That(secondFinalize, Is.Empty);
        });
    }

    [Test]
    public void Duplicate_delivery_is_idempotent_and_conflicting_or_out_of_order_lines_are_rejected ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.CactusMonitorSessionJsonl);
        framer.BeginGeneration(file);
        const string firstJson = "{\"type\":\"CYCLE\"}";
        PhysicalLineObservation first = CreateLine(file, 1, 20, firstJson, PhysicalLineTerminator.Lf);

        IReadOnlyList<LogParserInput> emitted = framer.Accept(first);
        IReadOnlyList<LogParserInput> duplicate = framer.Accept(first);
        PhysicalLineObservation second = CreateLine(file, 2, NextStart(first), "{\"type\":\"END\"}",
            PhysicalLineTerminator.CrLf);
        framer.Accept(second);

        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Count.EqualTo(1));
            Assert.That(duplicate, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => framer.Accept(first));
            Assert.Throws<InvalidOperationException>(() => framer.Accept(
                CreateLine(file, 2, second.StartByteOffset, "{\"type\":\"CHANGED\"}", PhysicalLineTerminator.Lf)));
            Assert.Throws<InvalidOperationException>(() => framer.Accept(
                CreateLine(file, 4, NextStart(second), "{\"type\":\"GAP\"}", PhysicalLineTerminator.Lf)));
        });
    }

    [Test]
    public void A_new_line_cannot_follow_an_unterminated_line_snapshot ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.ReCactusSessionText);
        framer.BeginGeneration(file);
        PhysicalLineObservation pending = CreateLine(file, 1, 10, "partial", PhysicalLineTerminator.None);
        framer.Accept(pending);

        Assert.Throws<InvalidOperationException>(() => framer.Accept(
            CreateLine(file, 2, pending.EndByteOffset, "next", PhysicalLineTerminator.Lf)));
    }

    [Test]
    public void Empty_physical_line_remains_representable ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.ReCactusSessionText);
        framer.BeginGeneration(file);

        IReadOnlyList<LogParserInput> emitted = framer.Accept(
            CreateLine(file, 1, 0, string.Empty, PhysicalLineTerminator.Lf));

        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Count.EqualTo(1));
            Assert.That(emitted[0].RawText, Is.Empty);
            Assert.That(emitted[0].StartByteOffset, Is.EqualTo(0));
            Assert.That(emitted[0].EndByteOffset, Is.EqualTo(0));
            Assert.That(emitted[0].StartLineNumber, Is.EqualTo(1));
            Assert.That(emitted[0].EndLineNumber, Is.EqualTo(1));
        });
    }

    [Test]
    public void Framer_requires_an_external_generation_and_rejects_mismatched_file_refs ()
    {
        FileRef file = CreateFile();
        var framer = new GenerationAwareLogicalRecordFramer(MinecraftSourceAdapterHint.ReCactusSessionText);
        Assert.Throws<InvalidOperationException>(() => framer.Accept(
            CreateLine(file, 1, 0, "x", PhysicalLineTerminator.Lf)));

        framer.BeginGeneration(file);
        Assert.Throws<InvalidOperationException>(() => framer.Accept(
            CreateLine(CreateFile(generation: 2), 1, 0, "x", PhysicalLineTerminator.Lf)));
    }

    private static FileRef CreateFile (long generation = 1) =>
        new("synthetic-source", "logs/synthetic.log", generation);

    private static PhysicalLineObservation CreateLine (
        FileRef file,
        long lineNumber,
        long startByteOffset,
        string content,
        PhysicalLineTerminator terminator)
    {
        long endByteOffset = startByteOffset + Encoding.UTF8.GetByteCount(content);
        long terminatorByteLength = terminator switch
        {
            PhysicalLineTerminator.None => 0,
            PhysicalLineTerminator.Lf or PhysicalLineTerminator.Cr => 1,
            PhysicalLineTerminator.CrLf => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(terminator))
        };

        return new PhysicalLineObservation(
            file,
            lineNumber,
            startByteOffset,
            endByteOffset,
            content,
            terminator,
            terminatorByteLength);
    }

    private static long NextStart (PhysicalLineObservation line) =>
        line.EndByteOffset + line.TerminatorByteLength;
}
