using System.Runtime.Versioning;

using WeifenLuo.WinFormsUI.Docking;

namespace LogExpert.UI.Workspace;

[SupportedOSPlatform("windows")]
public sealed class MinecraftWorkspaceReadOnlyDocument : DockContent
{
    public MinecraftWorkspaceReadOnlyDocument (MinecraftWorkspaceReadOnlyViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Text = snapshot.WorkspaceDisplayName;
        TabText = snapshot.WorkspaceDisplayName;
        ShowHint = DockState.Document;
        WorkspaceControl = new MinecraftWorkspaceReadOnlyControl(snapshot);
        Controls.Add(WorkspaceControl);
    }

    public MinecraftWorkspaceReadOnlyControl WorkspaceControl { get; }
}
