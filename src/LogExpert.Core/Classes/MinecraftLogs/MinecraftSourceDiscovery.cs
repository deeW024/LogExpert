namespace LogExpert.Core.Classes.MinecraftLogs;

public enum MinecraftSourceFamily
{
    Minecraft,
    Yeezus,
    ReCactus,
    CactusMonitor
}

public enum MinecraftSourceAdapterHint
{
    MinecraftLatestLog,
    YeezusTextLog,
    ReCactusSessionText,
    CactusMonitorSessionJsonl
}

public enum MinecraftSourceSegmentRole
{
    None,
    Primary,
    Rotated,
    Final,
    ActivePart
}

public enum MinecraftDiscoveryProvenance
{
    KnownPathRule
}

/// <summary>Means the path was observed in the filesystem; it makes no claim about readability or tail state.</summary>
public enum MinecraftDiscoveryStatus
{
    Observed
}

/// <summary>A physical file discovered by a known rule and its logical source grouping.</summary>
public sealed record DiscoveredSourceFile (
    string WorkspaceId,
    string SourceId,
    string FileId,
    string RelativePath,
    string FullPath,
    MinecraftSourceFamily Family,
    MinecraftSourceAdapterHint AdapterHint,
    MinecraftSourceSegmentRole SegmentRole,
    MinecraftDiscoveryProvenance Provenance,
    MinecraftDiscoveryStatus Status);

/// <summary>A point-in-time inventory and the physical files added or removed since the previous scan.</summary>
public sealed record MinecraftDiscoveryResult (
    string WorkspaceId,
    IReadOnlyList<DiscoveredSourceFile> Files,
    IReadOnlyList<DiscoveredSourceFile> Appeared,
    IReadOnlyList<DiscoveredSourceFile> Disappeared);

/// <summary>
/// Scans only known Minecraft log paths. It does not parse, watch, tail, or retain runtime state.
/// </summary>
public sealed class MinecraftSourceDiscovery
{
    private const string LatestLog = "logs/latest.log";
    private const string YeezusLog = "logs/yeezus.log";
    private const string YeezusRotatedLog = "logs/yeezus.log.1";
    private const string ReCactusDirectory = "logs/reccactus";
    private const string CactusMonitorDirectory = "cactusmonitor/sessions";
    private readonly Dictionary<string, DiscoveredSourceFile> _previousFiles = new(StringComparer.Ordinal);

    public MinecraftSourceDiscovery (MinecraftWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
    }

    public MinecraftWorkspace Workspace { get; }

    public MinecraftDiscoveryResult Rescan ()
    {
        DiscoveredSourceFile[] files = DiscoverFiles();
        var currentFiles = files.ToDictionary(file => file.FileId, StringComparer.Ordinal);

        DiscoveredSourceFile[] appeared = SortFiles(
            files.Where(file => !_previousFiles.ContainsKey(file.FileId)));
        DiscoveredSourceFile[] disappeared = SortFiles(
            _previousFiles.Values.Where(file => !currentFiles.ContainsKey(file.FileId)));

        _previousFiles.Clear();
        foreach ((string fileId, DiscoveredSourceFile file) in currentFiles)
        {
            _previousFiles.Add(fileId, file);
        }

        return new MinecraftDiscoveryResult(Workspace.WorkspaceId, files, appeared, disappeared);
    }

    private DiscoveredSourceFile[] DiscoverFiles ()
    {
        var files = new List<DiscoveredSourceFile>();
        string latestPath = GetWorkspacePath(LatestLog);
        AddIfObserved(
            files,
            latestPath,
            "minecraft",
            "latest",
            MinecraftSourceFamily.Minecraft,
            MinecraftSourceAdapterHint.MinecraftLatestLog,
            MinecraftSourceSegmentRole.Primary);

        AddIfObserved(
            files,
            GetWorkspacePath(YeezusLog),
            "yeezus",
            "yeezus",
            MinecraftSourceFamily.Yeezus,
            MinecraftSourceAdapterHint.YeezusTextLog,
            MinecraftSourceSegmentRole.Primary);
        AddIfObserved(
            files,
            GetWorkspacePath(YeezusRotatedLog),
            "yeezus",
            "yeezus",
            MinecraftSourceFamily.Yeezus,
            MinecraftSourceAdapterHint.YeezusTextLog,
            MinecraftSourceSegmentRole.Rotated);

        foreach (string path in EnumerateKnownDirectory(ReCactusDirectory))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith("reccactus-", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                Add(files, path, "reccactus", Path.GetFileNameWithoutExtension(fileName),
                    MinecraftSourceFamily.ReCactus,
                    MinecraftSourceAdapterHint.ReCactusSessionText,
                    MinecraftSourceSegmentRole.None);
            }
        }

        foreach (string path in EnumerateKnownDirectory(CactusMonitorDirectory))
        {
            string fileName = Path.GetFileName(path);
            if (!fileName.StartsWith("cfm-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fileName.EndsWith(".jsonl.part", StringComparison.OrdinalIgnoreCase))
            {
                Add(files, path, "cactusmonitor", fileName[..^".jsonl.part".Length],
                    MinecraftSourceFamily.CactusMonitor,
                    MinecraftSourceAdapterHint.CactusMonitorSessionJsonl,
                    MinecraftSourceSegmentRole.ActivePart);
            }
            else if (fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                Add(files, path, "cactusmonitor", fileName[..^".jsonl".Length],
                    MinecraftSourceFamily.CactusMonitor,
                    MinecraftSourceAdapterHint.CactusMonitorSessionJsonl,
                    MinecraftSourceSegmentRole.Final);
            }
        }

        return SortFiles(files);
    }

    private string GetWorkspacePath (string relativePath) =>
        Path.GetFullPath(Path.Combine(Workspace.RootPath, relativePath));

    private string[] EnumerateKnownDirectory (string relativePath)
    {
        string directoryPath = GetWorkspacePath(relativePath);
        try
        {
            return Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private void AddIfObserved (
        ICollection<DiscoveredSourceFile> files,
        string fullPath,
        string family,
        string sourceIdentity,
        MinecraftSourceFamily sourceFamily,
        MinecraftSourceAdapterHint adapterHint,
        MinecraftSourceSegmentRole segmentRole)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            Add(files, fullPath, family, sourceIdentity, sourceFamily, adapterHint, segmentRole);
        }
    }

    private void Add (
        ICollection<DiscoveredSourceFile> files,
        string fullPath,
        string family,
        string sourceIdentity,
        MinecraftSourceFamily sourceFamily,
        MinecraftSourceAdapterHint adapterHint,
        MinecraftSourceSegmentRole segmentRole)
    {
        string normalizedPath = Path.GetFullPath(fullPath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        files.Add(new DiscoveredSourceFile(
            Workspace.WorkspaceId,
            Workspace.GetSourceId(family, sourceIdentity),
            Workspace.GetFileId(normalizedPath),
            Workspace.GetRelativePath(normalizedPath),
            normalizedPath,
            sourceFamily,
            adapterHint,
            segmentRole,
            MinecraftDiscoveryProvenance.KnownPathRule,
            MinecraftDiscoveryStatus.Observed));
    }

    private static DiscoveredSourceFile[] SortFiles (IEnumerable<DiscoveredSourceFile> files) => files
        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
        .ToArray();
}
