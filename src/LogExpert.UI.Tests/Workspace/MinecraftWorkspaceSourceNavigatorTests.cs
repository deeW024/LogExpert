#pragma warning disable CA1303 // Synthetic paths and log records are assertion-visible test inputs.

using System.Globalization;
using System.Text;

using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.UI.Controls.LogWindow;
using LogExpert.UI.Services.FileOperationService;
using LogExpert.UI.Workspace;

using NUnit.Framework;

namespace LogExpert.UI.Tests.Workspace;

[TestFixture]
[NonParallelizable]
public sealed class MinecraftWorkspaceSourceNavigatorTests
{
    private string _testDirectory = null!;

    [SetUp]
    public void SetUp ()
    {
        _testDirectory = Path.Join(Path.GetTempPath(), "LogExpertWorkspaceSourceNavigationTests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_testDirectory);
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
    public void Exact_record_at_original_path_is_valid_and_append_after_record_is_allowed ()
    {
        string path = Path.Join(_testDirectory, "latest.log");
        const string prefix = "first line\n";
        const string rawText = "[18:00:00] [Render thread/INFO] exact λ record";
        long start = Encoding.UTF8.GetByteCount(prefix);
        File.WriteAllText(path, prefix + rawText + "\n", new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(path, rawText, start, line: 2);

        File.AppendAllText(path, "appended later\n", new UTF8Encoding(false));

        MinecraftWorkspaceSourceNavigationResult result = CreateNavigator().Validate(entry);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.Success));
            Assert.That(result.OriginalPath, Is.EqualTo(path));
            Assert.That(result.ResolvedPath, Is.EqualTo(path));
            Assert.That(result.WasRelocated, Is.False);
            Assert.That(result.StartLineNumber, Is.EqualTo(2));
            Assert.That(result.FileId, Is.EqualTo("source-file"));
            Assert.That(result.Generation, Is.EqualTo(1));
            Assert.That(entry.IngressEvent.Event.Ref.File.Path, Is.EqualTo(path));
        });
    }

    [Test]
    public void Rewritten_or_truncated_record_is_reported_as_changed ()
    {
        string path = Path.Join(_testDirectory, "latest.log");
        const string rawText = "captured record";
        File.WriteAllText(path, rawText + "\n", new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(path, rawText, 0);
        MinecraftWorkspaceSourceNavigator navigator = CreateNavigator();

        File.WriteAllText(path, "rewritten record\n", new UTF8Encoding(false));
        MinecraftWorkspaceSourceNavigationResult rewritten = navigator.Validate(entry);
        File.WriteAllText(path, "short", new UTF8Encoding(false));
        MinecraftWorkspaceSourceNavigationResult truncated = navigator.Validate(entry);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.SourceChanged));
            Assert.That(truncated.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.SourceChanged));
        });
    }

    [Test]
    public void Missing_source_without_deterministic_successor_is_unavailable ()
    {
        string path = Path.Join(_testDirectory, "latest.log");
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(path, "captured record", 0);

        MinecraftWorkspaceSourceNavigationResult result = CreateNavigator().Validate(entry);

        Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.SourceUnavailable));
    }

    [TestCase(-1, 10, 1)]
    [TestCase(10, 9, 1)]
    [TestCase(0, 1, 0)]
    [TestCase(0, 1, 2147483648)]
    public void Invalid_offsets_or_line_numbers_are_rejected (long start, long end, long line)
    {
        string path = Path.Join(_testDirectory, "latest.log");
        File.WriteAllText(path, "anything", new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(path, "anything", start, line: line, end: end);

        MinecraftWorkspaceSourceNavigationResult result = CreateNavigator().Validate(entry);

        Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.InvalidLocation));
    }

    [Test]
    public void Multiline_record_validates_exact_internal_CRLF_bytes ()
    {
        string path = Path.Join(_testDirectory, "yeezus.log");
        const string rawText = "2099-01-01T10:00:00Z [INFO] header\r\n    at synthetic.Stack.run(Stack.java:4)";
        File.WriteAllText(path, rawText + "\r\nnext", new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(path, rawText, 0, line: 1, endLine: 2);

        MinecraftWorkspaceSourceNavigationResult result = CreateNavigator().Validate(entry);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.Success));
            Assert.That(result.StartLineNumber, Is.EqualTo(1));
        });
    }

    [TestCase("cfm-session.jsonl.part", "cfm-session.jsonl")]
    [TestCase("yeezus.log", "yeezus.log.1")]
    public void Deterministic_rotation_relocation_requires_the_original_exact_byte_range (
        string originalName,
        string relocatedName)
    {
        string originalPath = Path.Join(_testDirectory, originalName);
        string relocatedPath = Path.Join(_testDirectory, relocatedName);
        const string prefix = "prefix\n";
        const string rawText = "synthetic relocated event";
        long start = Encoding.UTF8.GetByteCount(prefix);
        File.WriteAllText(relocatedPath, prefix + rawText + "\n", new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(originalPath, rawText, start, line: 2);

        MinecraftWorkspaceSourceNavigationResult result = CreateNavigator().Validate(entry);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.Success));
            Assert.That(result.OriginalPath, Is.EqualTo(originalPath));
            Assert.That(result.ResolvedPath, Is.EqualTo(relocatedPath));
            Assert.That(result.WasRelocated, Is.True);
            Assert.That(entry.IngressEvent.Event.Ref.File.Path, Is.EqualTo(originalPath));
        });
    }

    [Test]
    public void Mismatched_rotated_file_and_arbitrary_sibling_content_are_not_searched ()
    {
        string yeezusPath = Path.Join(_testDirectory, "yeezus.log");
        string rotatedPath = Path.Join(_testDirectory, "yeezus.log.1");
        string arbitrarySiblingPath = Path.Join(_testDirectory, "archive.log");
        string unrelatedSourcePath = Path.Join(_testDirectory, "other.log");
        const string rawText = "captured exact record";
        File.WriteAllText(rotatedPath, "different record\n", new UTF8Encoding(false));
        File.WriteAllText(arbitrarySiblingPath, rawText + "\n", new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(yeezusPath, rawText, 0);

        MinecraftWorkspaceSourceNavigationResult relocatedMismatch = CreateNavigator().Validate(entry);
        MinecraftWorkspaceTimelineEntry arbitraryEntry = CreateEntry(unrelatedSourcePath, rawText, 0);
        MinecraftWorkspaceSourceNavigationResult arbitraryContent = CreateNavigator().Validate(arbitraryEntry);

        Assert.Multiple(() =>
        {
            Assert.That(relocatedMismatch.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.SourceChanged));
            Assert.That(arbitraryContent.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.SourceUnavailable));
            Assert.That(relocatedMismatch.ResolvedPath, Is.Null);
            Assert.That(arbitraryContent.ResolvedPath, Is.Null);
        });
    }

    [Test]
    public void Open_uses_resolved_file_and_exact_start_line_with_wait_behavior ()
    {
        string path = Path.Join(_testDirectory, "yeezus.log");
        const string rawText = "header\n    at synthetic.Stack.run(Stack.java:4)";
        File.WriteAllText(path, rawText, new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(path, rawText, 0, line: 1, endLine: 2);
        FileTabRequest? capturedRequest = null;
        MinecraftWorkspaceSourceNavigator navigator = new(request =>
        {
            capturedRequest = request;
            return null!;
        });

        MinecraftWorkspaceSourceNavigationResult validated = navigator.Validate(entry);
        MinecraftWorkspaceSourceNavigationResult opened = navigator.Open(entry, validated);

        Assert.Multiple(() =>
        {
            Assert.That(opened.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.Success));
            Assert.That(capturedRequest, Is.Not.Null);
            Assert.That(capturedRequest!.FileName, Is.EqualTo(path));
            Assert.That(capturedRequest.TargetLine, Is.EqualTo(1));
            Assert.That(capturedRequest.TargetLineBehavior, Is.EqualTo(LogWindowTargetLineBehavior.WaitForExactTarget));
            Assert.That(capturedRequest.IsTempFile, Is.False);
        });
    }

    [Test]
    public void Open_does_not_call_file_operations_for_an_unverified_target_and_reports_open_failure ()
    {
        string path = Path.Join(_testDirectory, "latest.log");
        const string rawText = "captured record";
        File.WriteAllText(path, rawText, new UTF8Encoding(false));
        MinecraftWorkspaceTimelineEntry entry = CreateEntry(path, rawText, 0);
        int openCalls = 0;
        MinecraftWorkspaceSourceNavigator navigator = new(_ =>
        {
            openCalls++;
            throw new IOException("synthetic source tab failure");
        });

        File.WriteAllText(path, "rewritten bytes", new UTF8Encoding(false));
        MinecraftWorkspaceSourceNavigationResult stale = navigator.Validate(entry);
        MinecraftWorkspaceSourceNavigationResult refused = navigator.Open(entry, stale);
        File.WriteAllText(path, rawText, new UTF8Encoding(false));
        MinecraftWorkspaceSourceNavigationResult verified = navigator.Validate(entry);
        MinecraftWorkspaceSourceNavigationResult openFailed = navigator.Open(entry, verified);

        Assert.Multiple(() =>
        {
            Assert.That(refused.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.SourceChanged));
            Assert.That(openFailed.Status, Is.EqualTo(MinecraftWorkspaceSourceNavigationStatus.OpenFailed));
            Assert.That(openFailed.Error, Is.EqualTo("synthetic source tab failure"));
            Assert.That(openCalls, Is.EqualTo(1), "An unverified event must never reach FileOperationService.");
        });
    }

    private static MinecraftWorkspaceSourceNavigator CreateNavigator () => new(_ => null!);

    private static MinecraftWorkspaceTimelineEntry CreateEntry (
        string path,
        string rawText,
        long start,
        long? end = null,
        long line = 1,
        long? endLine = null)
    {
        DateTimeOffset timestamp = DateTimeOffset.Parse("2030-01-01T10:00:00Z", CultureInfo.InvariantCulture);
        FileRef file = new("source-file", path, 1);
        EventRef eventRef = new(file, 1, start, end ?? start + Encoding.UTF8.GetByteCount(rawText), line, endLine ?? line);
        NormalizedLogEvent logEvent = new(
            eventRef,
            Attribution.Unknown<string>(),
            Attribution.Unknown<string>(),
            Attribution.Unknown<LogLevel>(),
            null,
            Attribution.Unknown<string>(),
            EventTimestamp.Unknown(),
            EventParseStatus.Parsed,
            rawText,
            rawText);
        MinecraftWorkspaceIngressEvent ingress = new(
            1,
            "source-navigation-test",
            "source-file",
            file.FileId,
            MinecraftSourceSegmentRole.Primary,
            logEvent,
            timestamp);
        return new MinecraftWorkspaceTimelineEntry(
            ingress,
            timestamp,
            timestamp,
            MinecraftWorkspaceTimelineTimestampBasis.WorkspaceIngressFallback,
            false,
            false);
    }
}
