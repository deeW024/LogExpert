using LogExpert.Core.Classes.MinecraftLogs;

namespace LogExpert.Core.Classes.Log.Streamreaders;

/// <summary>Byte-exact metadata for one physical line returned by a stream reader.</summary>
public readonly record struct PhysicalLineReadMetadata (
    long StartByteOffset,
    long EndByteOffset,
    PhysicalLineTerminator Terminator,
    long TerminatorByteLength)
{
    public bool IsTerminated => Terminator != PhysicalLineTerminator.None;
}
