using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;

namespace MyCapture.App.Sources;

/// <summary>Source actions shared by the library, editor and pins, with injectable desktop boundaries.</summary>
internal sealed class CaptureSourceActions
{
    private readonly Action<ProcessStartInfo> _launch;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Action<string> _copy;
    private readonly string _explorerPath;
    private readonly string _textEditorPath;

    internal static CaptureSourceActions Default { get; } = new();

    internal CaptureSourceActions(Action<ProcessStartInfo>? launch = null, Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null, Action<string>? copy = null, string? windowsDirectory = null)
    {
        _launch = launch ?? (start => Process.Start(start)?.Dispose());
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        _copy = copy ?? ClipboardWriter.SetText;
        string windows = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _explorerPath = Path.Combine(windows, "explorer.exe");
        _textEditorPath = Path.Combine(windows, "System32", "notepad.exe");
    }

    internal async Task<string> OpenAsync(CaptureProvenance source)
    {
        if (!source.HasSource) return string.Empty;
        if (source.Kind == CaptureProvenanceKind.Web)
        {
            // The browser the page was captured in, while it is installed; otherwise the default browser.
            ProcessStartInfo? page = source.AppExecutablePath.Length == 0
                ? SourceAppLaunch.CreateUrl(source.Url, null)
                : await Task.Run(() => SourceAppLaunch.CreateUrl(source.Url, source.AppExecutablePath, _fileExists));
            return Launch(page, "Library.SourcePage.OpenFailed");
        }
        if (source.Kind == CaptureProvenanceKind.App)
        {
            if (!source.CanOpen) return UiText.Get("Source.AppMissing");
            // The executable, or a Store app by its ID once an update has replaced the executable's folder.
            ProcessStartInfo? app = await Task.Run(() => SourceAppLaunch.CreateApp(source.AppExecutablePath,
                source.AppUserModelId, _explorerPath, _fileExists));
            return app is null ? UiText.Get("Source.AppMissing") : Launch(app, "Source.AppOpenFailed");
        }

        return await Task.Run(() =>
        {
            string path = source.DocumentPath;
            if (!SourceDocument.IsFileSource(source.AppExecutablePath) && DirectoryExists(path))
                return Launch(SourceDocument.CreateOpenFolderStartInfo(path, _explorerPath, selectFile: false), "Library.Source.OpenFolderFailed");
            if (!FileExists(path)) return UiText.Get("Library.Source.FileMissing");
            // The captured application when it is a known document application; otherwise the safe plan.
            return Launch(SourceDocument.CreateOpenStartInfo(path, _textEditorPath, _fileExists, source.AppExecutablePath),
                "Library.Source.OpenFileFailed");
        });
    }

    internal Task<string> OpenLocationAsync(CaptureProvenance source, bool allowMissingParent = false) => Task.Run(() =>
    {
        string path = source.DocumentPath;
        if (path.Length == 0) return string.Empty;
        bool document = SourceDocument.IsFileSource(source.AppExecutablePath);
        bool file = FileExists(path);
        bool directory = !document && !file && DirectoryExists(path);
        if (!file && !directory && allowMissingParent)
        {
            string parent = CaptureSourceText.NormalizeLocalPath(Path.GetDirectoryName(path));
            if (parent.Length > 0 && DirectoryExists(parent))
            {
                path = parent;
                directory = true;
            }
        }
        if (!file && !directory)
            return UiText.Get(document ? "Library.Source.FileMissing" : "Library.Source.FolderMissing");
        return Launch(SourceDocument.CreateOpenFolderStartInfo(path, _explorerPath, selectFile: file), "Library.Source.OpenFolderFailed");
    });

    internal enum SourceFolderTargetKind { Missing, Folder, File }
    internal readonly record struct SourceFolderTarget(SourceFolderTargetKind Kind, string Path);

    internal SourceFolderTarget ResolveFolderTarget(string path)
    {
        string normalized = CaptureSourceText.NormalizeLocalPath(path);
        if (normalized.Length == 0) return new(SourceFolderTargetKind.Missing, string.Empty);
        if (DirectoryExists(normalized)) return new(SourceFolderTargetKind.Folder, normalized);
        if (FileExists(normalized)) return new(SourceFolderTargetKind.File, normalized);
        return new(SourceFolderTargetKind.Missing, normalized);
    }

    internal string CopyUrl(string url) => Copy(SourcePageUrl.Normalize(url), "Library.SourcePage.Copied", "Library.SourcePage.CopyFailed");
    internal string CopyPath(string path) => Copy(CaptureSourceText.NormalizeLocalPath(path), "Library.Source.PathCopied", "Library.Source.CopyPathFailed");

    private string Copy(string value, string success, string failure)
    {
        if (value.Length == 0) return string.Empty;
        try { _copy(value); return UiText.Get(success); }
        catch (ExternalException) { return UiText.Get(failure); }
    }

    private string Launch(ProcessStartInfo? start, string failure)
    {
        if (start is null) return UiText.Get(failure);
        try { _launch(start); return string.Empty; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return UiText.Get(failure); }
    }

    private bool FileExists(string path) => SourceDocument.FileExists(path, _fileExists);

    private bool DirectoryExists(string path)
    {
        try { return _directoryExists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }
}
