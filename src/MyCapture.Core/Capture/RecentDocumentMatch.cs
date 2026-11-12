namespace MyCapture.Core.Capture;

/// <summary>One Recent Items shortcut: where it points and when Windows last wrote it.</summary>
/// <param name="Target">The shortcut's target path as stored, never resolved or searched for.</param>
/// <param name="WrittenUtc">The shortcut file's last write time.</param>
public readonly record struct RecentDocumentLink(string Target, DateTime WrittenUtc);

/// <summary>
/// Chooses a document application's file from the user's Recent Items when its window shows only a
/// file name. The rule is deliberately strict; anything doubtful gives no path rather than a guess.
/// </summary>
public static class RecentDocumentMatch
{
    /// <summary>
    /// The single existing file whose name is one of <paramref name="names"/> and whose shortcut was
    /// written after the application's process started (so this process can have opened it). Two
    /// different files with a matching name, a shortcut older than the process, a missing target or
    /// an unknown start time all give an empty result.
    /// </summary>
    public static string Choose(IReadOnlyList<string> names, IEnumerable<RecentDocumentLink> links,
        DateTime? processStartedUtc, Func<string, bool>? exists = null)
    {
        if (names.Count == 0 || processStartedUtc is not { } started) return string.Empty;
        var matches = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (RecentDocumentLink link in links)
        {
            string target = CaptureSourceText.NormalizeLocalPath(link.Target);
            if (target.Length == 0) continue;
            string name = Path.GetFileName(target);
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            // Every same-named file counts, however old its shortcut: two of them is ambiguous.
            matches[target] = matches.TryGetValue(target, out DateTime seen) && seen > link.WrittenUtc ? seen : link.WrittenUtc;
            if (matches.Count > 1) return string.Empty;
        }

        if (matches.Count != 1) return string.Empty;
        (string path, DateTime written) = matches.Single();
        // An older shortcut belongs to an earlier session; this process may not have opened the file.
        if (written < started) return string.Empty;
        return SourceDocument.FileExists(path, exists) ? path : string.Empty;
    }
}
