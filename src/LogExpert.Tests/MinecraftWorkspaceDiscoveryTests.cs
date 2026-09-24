using LogExpert.Core.Classes.MinecraftLogs;
using NUnit.Framework;

namespace LogExpert.Tests;

[TestFixture]
public class MinecraftWorkspaceDiscoveryTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp ()
    {
        _root = Path.Combine(Path.GetTempPath(), "LogExpertWorkspaceDiscoveryTests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown ()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void Empty_valid_workspace_has_an_empty_inventory ()
    {
        var discovery = new MinecraftSourceDiscovery(new MinecraftWorkspace(_root));

        MinecraftDiscoveryResult result = discovery.Rescan();

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Is.Empty);
            Assert.That(result.Appeared, Is.Empty);
            Assert.That(result.Disappeared, Is.Empty);
            Assert.That(result.WorkspaceId, Is.EqualTo(discovery.Workspace.WorkspaceId));
        });
    }

    [Test]
    public void Workspace_identity_normalizes_separators_trailing_dot_and_windows_case ()
    {
        string alternateSeparators = _root.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.AltDirectorySeparatorChar + ".";
        var normalized = new MinecraftWorkspace(alternateSeparators);
        var differentCase = new MinecraftWorkspace(_root.ToUpperInvariant());
        var original = new MinecraftWorkspace(_root);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.RootPath, Is.EqualTo(original.RootPath));
            Assert.That(normalized.WorkspaceId, Is.EqualTo(original.WorkspaceId));
            Assert.That(differentCase.WorkspaceId, Is.EqualTo(original.WorkspaceId));
        });
    }

    [Test]
    public void Known_source_families_have_stable_order_and_correct_identity_relationships ()
    {
        CreateFile("logs/latest.log");
        CreateFile("logs/yeezus.log");
        CreateFile("logs/yeezus.log.1");
        CreateFile("logs/reccactus/reccactus-one.txt");
        CreateFile("logs/reccactus/reccactus-two.txt");
        CreateFile("cactusmonitor/sessions/cfm-final.jsonl");
        CreateFile("cactusmonitor/sessions/cfm-active.jsonl.part");

        var discovery = new MinecraftSourceDiscovery(new MinecraftWorkspace(_root));
        MinecraftDiscoveryResult first = discovery.Rescan();
        MinecraftDiscoveryResult second = discovery.Rescan();

        string[] expectedOrder =
        [
            Path.Combine("cactusmonitor", "sessions", "cfm-active.jsonl.part"),
            Path.Combine("cactusmonitor", "sessions", "cfm-final.jsonl"),
            Path.Combine("logs", "latest.log"),
            Path.Combine("logs", "reccactus", "reccactus-one.txt"),
            Path.Combine("logs", "reccactus", "reccactus-two.txt"),
            Path.Combine("logs", "yeezus.log"),
            Path.Combine("logs", "yeezus.log.1")
        ];
        DiscoveredSourceFile currentYeezus = FindFile(first.Files, Path.Combine("logs", "yeezus.log"));
        DiscoveredSourceFile rotatedYeezus = FindFile(first.Files, Path.Combine("logs", "yeezus.log.1"));
        DiscoveredSourceFile[] reccactusSessions = first.Files
            .Where(file => file.Family == MinecraftSourceFamily.ReCactus)
            .ToArray();
        DiscoveredSourceFile[] cactusMonitorSessions = first.Files
            .Where(file => file.Family == MinecraftSourceFamily.CactusMonitor)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(first.Files.Select(file => file.RelativePath), Is.EqualTo(expectedOrder));
            Assert.That(first.Files, Has.Count.EqualTo(7));
            Assert.That(first.Files.Select(file => file.FileId).Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(7));
            Assert.That(second.Files, Is.EqualTo(first.Files));
            Assert.That(second.Appeared, Is.Empty);
            Assert.That(second.Disappeared, Is.Empty);
            Assert.That(currentYeezus.SourceId, Is.EqualTo(rotatedYeezus.SourceId));
            Assert.That(currentYeezus.FileId, Is.Not.EqualTo(rotatedYeezus.FileId));
            Assert.That(currentYeezus.SegmentRole, Is.EqualTo(MinecraftSourceSegmentRole.Primary));
            Assert.That(rotatedYeezus.SegmentRole, Is.EqualTo(MinecraftSourceSegmentRole.Rotated));
            Assert.That(reccactusSessions.Select(file => file.SourceId).Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(2));
            Assert.That(reccactusSessions.Select(file => file.FileId).Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(2));
            Assert.That(cactusMonitorSessions.Select(file => file.SourceId).Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(2));
            Assert.That(cactusMonitorSessions.All(file => file.Provenance == MinecraftDiscoveryProvenance.KnownPathRule), Is.True);
            Assert.That(cactusMonitorSessions.All(file => file.Status == MinecraftDiscoveryStatus.Observed), Is.True);
            Assert.That(FindFile(first.Files, Path.Combine("cactusmonitor", "sessions", "cfm-active.jsonl.part")).SegmentRole,
                Is.EqualTo(MinecraftSourceSegmentRole.ActivePart));
            Assert.That(FindFile(first.Files, Path.Combine("cactusmonitor", "sessions", "cfm-final.jsonl")).SegmentRole,
                Is.EqualTo(MinecraftSourceSegmentRole.Final));
        });
    }

    [Test]
    public void Rescan_reports_source_appearance_and_disappearance ()
    {
        var discovery = new MinecraftSourceDiscovery(new MinecraftWorkspace(_root));
        _ = discovery.Rescan();
        CreateFile("logs/latest.log");

        MinecraftDiscoveryResult appeared = discovery.Rescan();
        File.Delete(Path.Combine(_root, "logs", "latest.log"));
        MinecraftDiscoveryResult disappeared = discovery.Rescan();

        Assert.Multiple(() =>
        {
            Assert.That(appeared.Files, Has.Count.EqualTo(1));
            Assert.That(appeared.Appeared, Has.Count.EqualTo(1));
            Assert.That(appeared.Disappeared, Is.Empty);
            Assert.That(disappeared.Files, Is.Empty);
            Assert.That(disappeared.Appeared, Is.Empty);
            Assert.That(disappeared.Disappeared, Has.Count.EqualTo(1));
            Assert.That(disappeared.Disappeared[0].FileId, Is.EqualTo(appeared.Appeared[0].FileId));
        });
    }

    [Test]
    public void Cactus_monitor_part_publication_uses_a_new_physical_file_identity ()
    {
        string partPath = CreateFile("cactusmonitor/sessions/cfm-publish.jsonl.part");
        var discovery = new MinecraftSourceDiscovery(new MinecraftWorkspace(_root));
        MinecraftDiscoveryResult beforePublication = discovery.Rescan();
        string finalPath = Path.Combine(Path.GetDirectoryName(partPath)!, "cfm-publish.jsonl");
        File.Move(partPath, finalPath);

        MinecraftDiscoveryResult afterPublication = discovery.Rescan();

        Assert.Multiple(() =>
        {
            Assert.That(beforePublication.Files, Has.Count.EqualTo(1));
            Assert.That(afterPublication.Files, Has.Count.EqualTo(1));
            Assert.That(beforePublication.Files[0].SegmentRole, Is.EqualTo(MinecraftSourceSegmentRole.ActivePart));
            Assert.That(afterPublication.Files[0].SegmentRole, Is.EqualTo(MinecraftSourceSegmentRole.Final));
            Assert.That(afterPublication.Appeared, Has.Count.EqualTo(1));
            Assert.That(afterPublication.Disappeared, Has.Count.EqualTo(1));
            Assert.That(afterPublication.Appeared[0].FileId, Is.Not.EqualTo(afterPublication.Disappeared[0].FileId));
            Assert.That(afterPublication.Appeared[0].SourceId, Is.EqualTo(afterPublication.Disappeared[0].SourceId));
        });
    }

    [Test]
    public void Discovery_does_not_pick_up_arbitrary_or_nested_log_files ()
    {
        CreateFile("logs/application.log");
        CreateFile("logs/archive/old.log");
        CreateFile("logs/reccactus/other.txt");
        CreateFile("cactusmonitor/sessions/other-session.jsonl");
        CreateFile("other/cfm-outside.jsonl");

        var discovery = new MinecraftSourceDiscovery(new MinecraftWorkspace(_root));

        MinecraftDiscoveryResult result = discovery.Rescan();

        Assert.That(result.Files, Is.Empty);
    }

    private string CreateFile (string relativePath)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic fixture");
        return path;
    }

    private static DiscoveredSourceFile FindFile (IReadOnlyList<DiscoveredSourceFile> files, string relativePath) =>
        files.Single(file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
}
