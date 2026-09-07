using System.IO;

namespace MyCapture.App.Diagnostics;

/// <summary>Confines CLI-selected UX evidence to dedicated diagnostic output roots.</summary>
internal static class UxReviewOutputDirectory
{
    internal static string Resolve(string requestedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedDirectory);
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory))
            + Path.DirectorySeparatorChar;
        string workspaceRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "validation"))
            + Path.DirectorySeparatorChar;
        string temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mycapture-ux-review"))
            + Path.DirectorySeparatorChar;

        // The trailing separator makes the root itself valid while excluding lookalike
        // siblings such as validation-other. Normalize before testing containment.
        if (candidate.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            RejectReparsePoints(candidate);
            return candidate;
        }
        if (candidate.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            RejectReparsePoints(candidate);
            return candidate;
        }

        throw new ArgumentException(
            "UX review output must stay within artifacts/validation under the current working directory, "
            + "or the mycapture-ux-review directory under the system temporary directory.",
            nameof(requestedDirectory));
    }

    private static void RejectReparsePoints(string directory)
    {
        // Lexical containment alone cannot prevent an existing junction/symlink from
        // redirecting the write outside the allowed tree. Check existing ancestors too.
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArgumentException("UX review output cannot traverse a directory reparse point.", nameof(directory));
            }
        }
    }
}
