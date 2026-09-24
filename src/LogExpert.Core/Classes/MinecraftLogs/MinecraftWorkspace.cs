namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>A selected Minecraft instance root with a deterministic path-based identity.</summary>
public sealed record MinecraftWorkspace
{
    public MinecraftWorkspace (string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        RootPath = NormalizePath(Path.GetFullPath(rootPath));
        FileAttributes rootAttributes = File.GetAttributes(RootPath);
        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new ArgumentException(null, nameof(rootPath));
        }

        WorkspaceId = $"minecraft-workspace:{NormalizeIdentityPath(RootPath)}";
    }

    public string WorkspaceId { get; }

    public string RootPath { get; }

    internal string GetFileId (string fullPath) =>
        $"{WorkspaceId}/file:{NormalizeIdentityPath(fullPath)}";

    internal string GetSourceId (string family, string identity) =>
        $"{WorkspaceId}/source:{family}:{identity.ToUpperInvariant()}";

    internal string GetRelativePath (string fullPath) =>
        NormalizePath(Path.GetRelativePath(RootPath, fullPath));

    private static string NormalizePath (string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

    private static string NormalizeIdentityPath (string path) =>
        NormalizePath(path).ToUpperInvariant();
}
