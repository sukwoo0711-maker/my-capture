namespace MyCapture.Core.Capture;

/// <summary>
/// The document of a code editor whose window title gives only a file name (VS Code). The full path is
/// read after the capture from the editor's selected tab; these rules decide whether what was read names
/// one existing file that agrees with the captured title.
/// </summary>
public static class EditorDocumentMatch
{
    /// <summary>
    /// Executables whose selected tab is read. Each was checked against a running instance; another editor
    /// of the same family is added here only once its tab has been checked the same way.
    /// </summary>
    public static readonly IReadOnlyList<string> Executables = ["Code.exe", "Code - Insiders.exe"];

    /// <summary>Texts longer than this are not tab metadata and are not searched.</summary>
    internal const int MaxTextLength = 2048;

    /// <summary>At most this many paths are tried in one text, so a long text cannot make many file checks.</summary>
    internal const int MaxPathsPerText = 4;

    /// <summary>At most this many shorter cuts of one path are checked.</summary>
    internal const int MaxCutsPerPath = 8;

    public static bool IsEditor(string? appPath)
    {
        string name = Path.GetFileName(appPath ?? string.Empty);
        return name.Length > 0 && Executables.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one existing file the selected tabs name. When the title carries file names, only paths with one
    /// of those names count; when it carries none (a changed title format), the tabs must name exactly one
    /// file. Anything ambiguous gives nothing.
    /// </summary>
    /// <param name="titleNames">File names from the captured window title (<see cref="SourceDocument.TitleFileNames"/>).</param>
    /// <param name="selectedTexts">Name, help text and status of each selected tab or active editor.</param>
    public static string Choose(IReadOnlyList<string> titleNames, IEnumerable<string?> selectedTexts,
        Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(titleNames);
        ArgumentNullException.ThrowIfNull(selectedTexts);
        ArgumentNullException.ThrowIfNull(exists);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? text in selectedTexts)
        foreach (string path in PathsIn(text, exists))
            if (titleNames.Count == 0 || titleNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        return paths.Count == 1 ? paths.Single() : string.Empty;
    }

    /// <summary>
    /// Existing local files written out in <paramref name="text"/>. A tab can add words after the path
    /// (<c>C:\work\a.cs • Modified</c>), so each drive-rooted run is cut back at its spaces until it
    /// names an existing file.
    /// </summary>
    internal static IEnumerable<string> PathsIn(string? text, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) yield break;
        int found = 0;
        for (int start = 0; start < text.Length - 2 && found < MaxPathsPerText; start++)
        {
            if (!char.IsAsciiLetter(text[start]) || text[start + 1] != ':' || text[start + 2] != '\\'
                || (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '\\'))) continue;
            found++;
            string run = text[start..];
            int stop = run.IndexOfAny(['\r', '\n', '\t', '"']);
            if (stop > 0) run = run[..stop];
            int cuts = 0;
            for (int end = run.Length; end > 3 && cuts++ < MaxCutsPerPath; end = run.LastIndexOf(' ', end - 1))
            {
                string path = CaptureSourceText.NormalizeLocalPath(run[..end].TrimEnd(',', ';', ' '));
                if (path.Length > 0 && Path.GetFileName(path).Length > 0 && exists(path))
                {
                    yield return path;
                    break;
                }
            }
        }
    }
}
