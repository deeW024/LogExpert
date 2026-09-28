using LogExpert.Core.Config;
using LogExpert.Core.Interfaces;
using LogExpert.UI.Workspace;

using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace LogExpert.UI.Tests.Workspace;

[TestFixture]
public sealed class MinecraftWorkspaceSettingsStoreTests
{
    private string _testDirectory = null!;

    [SetUp]
    public void SetUp ()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"YEE80-settings-{Guid.NewGuid():N}");
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
    public void Recent_roots_are_normalized_case_insensitive_deduplicated_and_limited_to_ten ()
    {
        Settings settings = new();
        Mock<IConfigManager> config = CreateConfig(settings);
        MinecraftWorkspaceSettingsStore store = new(config.Object);
        string[] roots = Enumerable.Range(0, 11)
            .Select(index => Path.Combine(Path.GetTempPath(), $"YEE80-history-{index}"))
            .ToArray();

        foreach (string root in roots)
        {
            store.RecordSuccessfulOpen(root);
        }

        store.RecordSuccessfulOpen(roots[10].ToUpperInvariant());

        Assert.Multiple(() =>
        {
            Assert.That(store.GetRecentWorkspaceRoots(), Has.Count.EqualTo(10));
            Assert.That(store.GetRecentWorkspaceRoots()[0], Is.EqualTo(MinecraftWorkspaceSettingsStore.NormalizeRoot(roots[10])).IgnoreCase);
            Assert.That(store.GetRecentWorkspaceRoots(), Does.Contain(MinecraftWorkspaceSettingsStore.NormalizeRoot(roots[1])));
            Assert.That(store.GetRecentWorkspaceRoots(), Does.Not.Contain(MinecraftWorkspaceSettingsStore.NormalizeRoot(roots[0])));
            Assert.That(settings.FileHistoryList, Is.Empty);
            config.Verify(manager => manager.Save(SettingsFlags.Settings), Times.Exactly(12));
        });
    }

    [Test]
    public void Source_policy_is_workspace_scoped_retains_stale_ids_and_round_trips ()
    {
        Settings settings = new();
        Mock<IConfigManager> config = CreateConfig(settings);
        MinecraftWorkspaceSettingsStore store = new(config.Object);
        string firstRoot = Path.Combine(_testDirectory, "workspace-a");
        string secondRoot = Path.Combine(_testDirectory, "workspace-b");
        _ = Directory.CreateDirectory(firstRoot);
        _ = Directory.CreateDirectory(secondRoot);

        store.SetSourceEnabled(firstRoot, "logical:yeezus", enabled: false);
        store.SetSourceEnabled(firstRoot, "logical:stale", enabled: false);
        string persistedJson = JsonConvert.SerializeObject(settings);
        Settings reloadedSettings = JsonConvert.DeserializeObject<Settings>(persistedJson)!;
        MinecraftWorkspaceSettingsStore reloaded = new(CreateConfig(reloadedSettings).Object);

        Assert.Multiple(() =>
        {
            Assert.That(store.GetDisabledSourceIds(firstRoot), Is.EquivalentTo(new[] { "logical:yeezus", "logical:stale" }));
            Assert.That(store.GetDisabledSourceIds(secondRoot), Is.Empty);
            Assert.That(reloaded.GetDisabledSourceIds(firstRoot), Is.EquivalentTo(new[] { "logical:yeezus", "logical:stale" }));
            Assert.That(reloaded.GetDisabledSourceIds(secondRoot), Is.Empty);
            Assert.That(settings.FileHistoryList, Is.Empty);
        });
    }

    [Test]
    public void Explicit_remove_persists_history_without_deleting_workspace_directory ()
    {
        Settings settings = new();
        MinecraftWorkspaceSettingsStore store = new(CreateConfig(settings).Object);
        string absentRoot = Path.Combine(Path.GetTempPath(), $"YEE80-missing-{Guid.NewGuid():N}");
        store.RecordSuccessfulOpen(absentRoot);

        store.RemoveRecentWorkspace(absentRoot);

        Assert.Multiple(() =>
        {
            Assert.That(store.GetRecentWorkspaceRoots(), Is.Empty);
            Assert.That(Directory.Exists(absentRoot), Is.False);
            Assert.That(settings.FileHistoryList, Is.Empty);
        });
    }

    private static Mock<IConfigManager> CreateConfig (Settings settings)
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(manager => manager.Settings).Returns(settings);
        config.Setup(manager => manager.Save(It.IsAny<SettingsFlags>()));
        return config;
    }
}
