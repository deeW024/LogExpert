using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Config;
using LogExpert.Core.Interfaces;

namespace LogExpert.UI.Workspace;

/// <summary>Persists workspace history and logical source policy in the existing settings file.</summary>
internal sealed class MinecraftWorkspaceSettingsStore (IConfigManager configManager)
{
    private const int MaximumRecentWorkspaces = 10;

    private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

    public IReadOnlyList<string> GetRecentWorkspaceRoots () => NormalizeRecentRoots(_configManager.Settings.RecentMinecraftWorkspaceRoots);

    public IReadOnlyList<string> GetDisabledSourceIds (string workspaceRoot)
    {
        string workspaceId = GetWorkspaceId(workspaceRoot);
        return GetDisabledSourceIdsForWorkspaceId(workspaceId);
    }

    public IReadOnlyList<string> GetDisabledSourceIdsForWorkspaceId (string workspaceId)
    {
        MinecraftWorkspaceSourcePolicySettings? policy = _configManager.Settings.MinecraftWorkspaceSourcePolicies
            .FirstOrDefault(candidate => string.Equals(candidate.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));
        return Array.AsReadOnly((policy?.DisabledSourceIds ?? []).Distinct(StringComparer.Ordinal).ToArray());
    }

    public void RecordSuccessfulOpen (string workspaceRoot)
    {
        string normalizedRoot = NormalizeRoot(workspaceRoot);
        List<string> previous = _configManager.Settings.RecentMinecraftWorkspaceRoots.ToList();
        List<string> updated = NormalizeRecentRoots(previous)
            .Where(root => !string.Equals(root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            .Prepend(normalizedRoot)
            .Take(MaximumRecentWorkspaces)
            .ToList();
        _configManager.Settings.RecentMinecraftWorkspaceRoots = updated;
        SaveOrRestore(() => _configManager.Settings.RecentMinecraftWorkspaceRoots = previous);
    }

    public void RemoveRecentWorkspace (string workspaceRoot)
    {
        string normalizedRoot = NormalizeRoot(workspaceRoot);
        List<string> previous = _configManager.Settings.RecentMinecraftWorkspaceRoots.ToList();
        _configManager.Settings.RecentMinecraftWorkspaceRoots = NormalizeRecentRoots(previous)
            .Where(root => !string.Equals(root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveOrRestore(() => _configManager.Settings.RecentMinecraftWorkspaceRoots = previous);
    }

    public void SetSourceEnabled (string workspaceRoot, string sourceId, bool enabled)
    {
        SetSourceEnabledForWorkspaceId(GetWorkspaceId(workspaceRoot), sourceId, enabled);
    }

    public void SetSourceEnabledForWorkspaceId (string workspaceId, string sourceId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        List<MinecraftWorkspaceSourcePolicySettings> previous = _configManager.Settings.MinecraftWorkspaceSourcePolicies
            .Select(ClonePolicy)
            .ToList();
        MinecraftWorkspaceSourcePolicySettings? policy = _configManager.Settings.MinecraftWorkspaceSourcePolicies
            .FirstOrDefault(candidate => string.Equals(candidate.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));
        if (enabled)
        {
            policy?.DisabledSourceIds.RemoveAll(id => string.Equals(id, sourceId, StringComparison.Ordinal));
            if (policy is not null && policy.DisabledSourceIds.Count == 0)
            {
                _configManager.Settings.MinecraftWorkspaceSourcePolicies.Remove(policy);
            }
        }
        else if (policy is null)
        {
            _configManager.Settings.MinecraftWorkspaceSourcePolicies.Add(new MinecraftWorkspaceSourcePolicySettings
            {
                WorkspaceId = workspaceId,
                DisabledSourceIds = [sourceId]
            });
        }
        else if (!policy.DisabledSourceIds.Contains(sourceId, StringComparer.Ordinal))
        {
            policy.DisabledSourceIds.Add(sourceId);
        }

        try
        {
            _configManager.Save(SettingsFlags.Settings);
        }
        catch
        {
            _configManager.Settings.MinecraftWorkspaceSourcePolicies = previous;
            throw;
        }
    }

    internal static string NormalizeRoot (string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
    }

    private static string GetWorkspaceId (string rootPath) => new MinecraftWorkspace(NormalizeRoot(rootPath)).WorkspaceId;

    private static List<string> NormalizeRecentRoots (IEnumerable<string>? roots)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in roots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = NormalizeRoot(root);
            }
            catch (ArgumentException)
            {
                normalizedRoot = root;
            }

            if (seen.Add(normalizedRoot))
            {
                normalized.Add(normalizedRoot);
                if (normalized.Count == MaximumRecentWorkspaces)
                {
                    break;
                }
            }
        }

        return normalized;
    }

    private void SaveOrRestore (Action restore)
    {
        try
        {
            _configManager.Save(SettingsFlags.Settings);
        }
        catch
        {
            restore();
            throw;
        }
    }

    private static MinecraftWorkspaceSourcePolicySettings ClonePolicy (MinecraftWorkspaceSourcePolicySettings policy) => new()
    {
        WorkspaceId = policy.WorkspaceId,
        DisabledSourceIds = policy.DisabledSourceIds.ToList()
    };
}
