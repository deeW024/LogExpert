using LogExpert.Core.Classes.Log.Streamreaders;

namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>Raw physical-line content and exact metadata from the existing LogExpert read path.</summary>
public sealed record MinecraftPhysicalLineRead (string Content, PhysicalLineReadMetadata Metadata);

/// <summary>Optional sink attached to a single-file LogfileReader for a Minecraft source session.</summary>
public interface IMinecraftPhysicalLineObserver
{
    void OnPhysicalLine (MinecraftPhysicalLineRead line);

    void OnSourceTruncated ();

    void OnSourceDeleted ();

    void OnSourceRecreated ();
}
