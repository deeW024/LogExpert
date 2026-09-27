#pragma warning disable CA1031 // FileOperationService failures become visible workspace navigation status.

using System.Security;
using System.Text;

using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.UI.Controls.LogWindow;
using LogExpert.UI.Services.FileOperationService;

namespace LogExpert.UI.Workspace;

internal enum MinecraftWorkspaceSourceNavigationStatus
{
    Success,
    SourceUnavailable,
    SourceChanged,
    InvalidLocation,
    OpenFailed
}

internal sealed record MinecraftWorkspaceSourceNavigationResult (
    MinecraftWorkspaceSourceNavigationStatus Status,
    string OriginalPath,
    string? ResolvedPath,
    string FileId,
    long Generation,
    int StartLineNumber,
    bool WasRelocated,
    string? Error = null);

internal interface IMinecraftWorkspaceSourceNavigator
{
    MinecraftWorkspaceSourceNavigationResult Validate (MinecraftWorkspaceTimelineEntry entry);

    MinecraftWorkspaceSourceNavigationResult Open (
        MinecraftWorkspaceTimelineEntry entry,
        MinecraftWorkspaceSourceNavigationResult validatedTarget);
}

internal sealed class MinecraftWorkspaceSourceNavigator (
    Func<FileTabRequest, LogWindow> addFileTab) : IMinecraftWorkspaceSourceNavigator
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Func<FileTabRequest, LogWindow> _addFileTab = addFileTab ?? throw new ArgumentNullException(nameof(addFileTab));

    public MinecraftWorkspaceSourceNavigationResult Validate (MinecraftWorkspaceTimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        NormalizedLogEvent logEvent = entry.IngressEvent.Event;
        FileRef file = logEvent.Ref.File;
        EventRef eventRef = logEvent.Ref;
        string originalPath = file.Path;
        MinecraftWorkspaceSourceNavigationResult invalid = new(
            MinecraftWorkspaceSourceNavigationStatus.InvalidLocation,
            originalPath,
            null,
            file.FileId,
            file.Generation,
            eventRef.StartLineNumber is >= 1 and <= int.MaxValue ? (int)eventRef.StartLineNumber : 0,
            false);

        if (string.IsNullOrWhiteSpace(file.FileId) ||
            string.IsNullOrWhiteSpace(originalPath) ||
            file.Generation < 1 ||
            eventRef.StartByteOffset < 0 ||
            eventRef.EndByteOffset < eventRef.StartByteOffset ||
            eventRef.StartLineNumber < 1 ||
            eventRef.StartLineNumber > int.MaxValue)
        {
            return invalid;
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Utf8.GetBytes(logEvent.RawText);
        }
        catch (EncoderFallbackException exception)
        {
            return invalid with { Error = exception.Message };
        }

        if ((long)expectedBytes.Length != eventRef.EndByteOffset - eventRef.StartByteOffset)
        {
            return invalid;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(originalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            return invalid with { Error = exception.Message };
        }

        CandidateValidation original = ValidateCandidate(normalizedPath, eventRef.StartByteOffset, eventRef.EndByteOffset, expectedBytes);
        if (original == CandidateValidation.Match)
        {
            return CreateSuccess(normalizedPath, normalizedPath, file, eventRef, wasRelocated: false);
        }

        string? successorPath = GetDeterministicSuccessor(normalizedPath);
        if (successorPath != null)
        {
            CandidateValidation successor = ValidateCandidate(successorPath, eventRef.StartByteOffset, eventRef.EndByteOffset, expectedBytes);
            if (successor == CandidateValidation.Match)
            {
                return CreateSuccess(normalizedPath, successorPath, file, eventRef, wasRelocated: true);
            }

            if (successor == CandidateValidation.Changed || original == CandidateValidation.Changed)
            {
                return invalid with
                {
                    Status = MinecraftWorkspaceSourceNavigationStatus.SourceChanged,
                    Error = "The captured byte range no longer matches the source record."
                };
            }
        }

        return invalid with
        {
            Status = original == CandidateValidation.Changed
                ? MinecraftWorkspaceSourceNavigationStatus.SourceChanged
                : MinecraftWorkspaceSourceNavigationStatus.SourceUnavailable,
            Error = original == CandidateValidation.Changed
                ? "The captured byte range no longer matches the source record."
                : null
        };
    }

    public MinecraftWorkspaceSourceNavigationResult Open (
        MinecraftWorkspaceTimelineEntry entry,
        MinecraftWorkspaceSourceNavigationResult validatedTarget)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(validatedTarget);
        if (validatedTarget.Status != MinecraftWorkspaceSourceNavigationStatus.Success || validatedTarget.ResolvedPath == null)
        {
            return validatedTarget;
        }

        MinecraftWorkspaceSourceNavigationResult freshTarget = Validate(entry);
        if (freshTarget.Status != MinecraftWorkspaceSourceNavigationStatus.Success || freshTarget.ResolvedPath == null)
        {
            return freshTarget;
        }

        try
        {
            _ = _addFileTab(new FileTabRequest
            {
                FileName = freshTarget.ResolvedPath,
                TargetLine = freshTarget.StartLineNumber,
                TargetLineBehavior = LogWindowTargetLineBehavior.WaitForExactTarget
            });
            return freshTarget;
        }
        catch (Exception exception)
        {
            return freshTarget with
            {
                Status = MinecraftWorkspaceSourceNavigationStatus.OpenFailed,
                Error = exception.Message
            };
        }
    }

    private static MinecraftWorkspaceSourceNavigationResult CreateSuccess (
        string originalPath,
        string resolvedPath,
        FileRef file,
        EventRef eventRef,
        bool wasRelocated) => new(
            MinecraftWorkspaceSourceNavigationStatus.Success,
            originalPath,
            resolvedPath,
            file.FileId,
            file.Generation,
            checked((int)eventRef.StartLineNumber),
            wasRelocated);

    private static string? GetDeterministicSuccessor (string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".jsonl.part", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^".part".Length];
        }

        if (string.Equals(fileName, "yeezus.log", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Path.GetDirectoryName(path)!, "yeezus.log.1");
        }

        return null;
    }

    private static CandidateValidation ValidateCandidate (
        string path,
        long startByteOffset,
        long endByteOffset,
        byte[] expectedBytes)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < endByteOffset)
            {
                return CandidateValidation.Changed;
            }

            stream.Position = startByteOffset;
            byte[] actualBytes = new byte[expectedBytes.Length];
            stream.ReadExactly(actualBytes);
            return actualBytes.AsSpan().SequenceEqual(expectedBytes)
                ? CandidateValidation.Match
                : CandidateValidation.Changed;
        }
        catch (FileNotFoundException)
        {
            return CandidateValidation.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return CandidateValidation.Missing;
        }
        catch (EndOfStreamException)
        {
            return CandidateValidation.Changed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return CandidateValidation.Missing;
        }
    }

    private enum CandidateValidation
    {
        Missing,
        Changed,
        Match
    }
}
