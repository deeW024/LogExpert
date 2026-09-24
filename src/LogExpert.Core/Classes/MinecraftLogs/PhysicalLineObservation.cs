namespace LogExpert.Core.Classes.MinecraftLogs;

public enum PhysicalLineTerminator
{
    None,
    Lf,
    CrLf,
    Cr
}

/// <summary>
/// One observed physical line or cumulative snapshot of an unfinished physical line.
/// Byte offsets and terminator byte length are supplied by the reader; they are never
/// inferred from the decoded string.
/// </summary>
public sealed record PhysicalLineObservation
{
    public PhysicalLineObservation (
        FileRef file,
        long lineNumber,
        long startByteOffset,
        long endByteOffset,
        string content,
        PhysicalLineTerminator terminator,
        long terminatorByteLength)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);

        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);

        if (startByteOffset < 0 || endByteOffset < startByteOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(endByteOffset));
        }

        if (!Enum.IsDefined(terminator))
        {
            throw new ArgumentOutOfRangeException(nameof(terminator));
        }

        if ((terminator == PhysicalLineTerminator.None && terminatorByteLength != 0) ||
            (terminator != PhysicalLineTerminator.None && terminatorByteLength <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(terminatorByteLength));
        }

        File = file;
        LineNumber = lineNumber;
        StartByteOffset = startByteOffset;
        EndByteOffset = endByteOffset;
        Content = content;
        Terminator = terminator;
        TerminatorByteLength = terminatorByteLength;
    }

    public FileRef File { get; }

    public long LineNumber { get; }

    public long StartByteOffset { get; }

    public long EndByteOffset { get; }

    public string Content { get; }

    public PhysicalLineTerminator Terminator { get; }

    /// <summary>Exact byte length observed for the physical delimiter.</summary>
    public long TerminatorByteLength { get; }

    public bool IsTerminated => Terminator != PhysicalLineTerminator.None;

    public bool IsComplete => IsTerminated;

    public string TerminatorText => Terminator switch
    {
        PhysicalLineTerminator.None => string.Empty,
        PhysicalLineTerminator.Lf => "\n",
        PhysicalLineTerminator.CrLf => "\r\n",
        PhysicalLineTerminator.Cr => "\r",
        _ => string.Empty
    };
}
