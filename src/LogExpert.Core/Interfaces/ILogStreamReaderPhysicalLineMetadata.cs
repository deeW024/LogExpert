using LogExpert.Core.Classes.Log.Streamreaders;

namespace LogExpert.Core.Interfaces;

/// <summary>
/// Optional exact physical-line capability. The display slice may remain capped while
/// <paramref name="fullContentMemory"/> contains the complete decoded source line.
/// </summary>
public interface ILogStreamReaderPhysicalLineMetadata : ILogStreamReaderMemory
{
    bool TryReadLineWithMetadata (
        out ReadOnlyMemory<char> displayLineMemory,
        out ReadOnlyMemory<char> fullContentMemory,
        out PhysicalLineReadMetadata metadata);
}
