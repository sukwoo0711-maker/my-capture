using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MyCapture.App.Themes;

/// <summary>Why a file operation failed, as far as a person can act on it.</summary>
internal enum StorageFailure
{
    /// <summary>Not one of the cases below; the generic storage text applies.</summary>
    None,

    /// <summary>The drive has no room left (ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL).</summary>
    DiskFull,

    /// <summary>Another program holds the file open or locked (sharing or lock violation).</summary>
    InUse,

    /// <summary>Windows refused access to the file or folder.</summary>
    AccessDenied,
}

/// <summary>Marks only messages authored and localized by this application for display.</summary>
internal static partial class UserFacingError
{
    internal const int DiskFullHResult = unchecked((int)0x80070070);
    internal const int HandleDiskFullHResult = unchecked((int)0x80070027);
    internal const int SharingViolationHResult = unchecked((int)0x80070020);
    internal const int LockViolationHResult = unchecked((int)0x80070021);

    private sealed record Message(string Text);
    private static readonly ConditionalWeakTable<Exception, Message> Messages = new();
    internal static T Mark<T>(T error) where T : Exception
    {
        Messages.AddOrUpdate(error, new Message(error.Message));
        return error;
    }
    internal static string? GetMessage(Exception error) =>
        Messages.TryGetValue(error, out Message? value) ? value.Text : null;

    /// <summary>
    /// Reads the Windows error behind a failed file operation. The HRESULT is checked on the
    /// exception and its causes, so a full disk reported through an encoder's COM error or a
    /// wrapping task still reads as a full disk.
    /// </summary>
    internal static StorageFailure ClassifyStorage(Exception error)
    {
        for (Exception? current = error; current is not null; current = Cause(current))
        {
            switch (current.HResult)
            {
                case DiskFullHResult or HandleDiskFullHResult: return StorageFailure.DiskFull;
                case SharingViolationHResult or LockViolationHResult: return StorageFailure.InUse;
            }

            if (current is UnauthorizedAccessException) return StorageFailure.AccessDenied;
        }

        return StorageFailure.None;
    }

    /// <summary>
    /// The fully qualified file or folder path an I/O exception names, or <see langword="null"/>.
    /// .NET quotes the path in its file-system messages ("… 'C:\Captures\a.png' …").
    /// </summary>
    internal static string? FindPath(Exception error)
    {
        for (Exception? current = error; current is not null; current = Cause(current))
        {
            string? named = current switch
            {
                FileNotFoundException missing => missing.FileName,
                FileLoadException load => load.FileName,
                _ => null,
            };
            if (IsQualified(named)) return named;
            foreach (Match match in QuotedPath().Matches(current.Message))
            {
                string candidate = match.Groups["path"].Value;
                if (IsQualified(candidate)) return candidate;
            }
        }

        return null;
    }

    private static bool IsQualified(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { return Path.IsPathFullyQualified(path); }
        catch (ArgumentException) { return false; }
    }

    private static Exception? Cause(Exception error) => error switch
    {
        AggregateException { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
        _ => error.InnerException,
    };

    [GeneratedRegex(@"'(?<path>(?:[A-Za-z]:\\|\\\\)[^'\r\n]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPath();
}
