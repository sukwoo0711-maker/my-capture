using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Platform;
using MyCapture.Core.Storage;

namespace MyCapture.App.Diagnostics;

internal sealed record UnexpectedExitRecord(string Name, SafeErrorRecord Primary, IReadOnlyList<string> RecentLogLines,
    string Version, string Build, bool? Packaged, string WindowsVersion);

/// <summary>Only the terminating handler writes; reading and acknowledging run away from the UI thread.</summary>
internal sealed partial class UnexpectedExitLog
{
    internal const int MaximumRecordBytes = 8 * 1024;
    internal const string Header = "mycapture-unexpected-exit 1";
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string NameTimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Action<string> _deleteFile;

    internal UnexpectedExitLog(string folder, Action<string>? deleteFile = null)
    {
        Folder = Path.GetFullPath(folder);
        _deleteFile = deleteFile ?? (static path => global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(path));
    }

    internal string Folder { get; }
    internal static string FolderFor(AppPaths paths) =>
        Path.Combine(Path.GetDirectoryName(paths.OcrModelsRoot) ?? paths.DataRoot, "unexpected-exits");

    [GeneratedRegex(@"\Aexit-([0-9]{8}T[0-9]{9}Z)-[1-9][0-9]*\.(?:exit|seen)\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A0x[0-9A-F]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HResultPattern();

    internal bool TryWrite(SafeErrorRecord primary, IReadOnlyList<string>? recentLogLines = null,
        string? version = null, string? build = null, bool? packaged = null, string? windowsVersion = null)
    {
        try
        {
            if (Files().Length >= 20) return false;
            Assembly assembly = typeof(UnexpectedExitLog).Assembly;
            var record = new UnexpectedExitRecord("exit-" + primary.Utc.UtcDateTime.ToString(NameTimeFormat, CultureInfo.InvariantCulture)
                + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".exit", primary, recentLogLines ?? [],
                version ?? assembly.GetName().Version?.ToString() ?? SafeLogLine.Unknown,
                build ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? SafeLogLine.Unknown,
                packaged ?? AppPackage.IsPackaged, windowsVersion ?? Environment.OSVersion.Version.ToString());
            byte[] bytes = Utf8.GetBytes(Serialize(record));
            if (bytes.Length > MaximumRecordBytes) return false;
            Directory.CreateDirectory(Folder);
            using var file = new FileStream(Path.Combine(Folder, record.Name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(bytes);
            file.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    internal IReadOnlyList<UnexpectedExitRecord> ReadPending()
    {
        try
        {
            Prune();
            return Files().Where(file => file.Name.EndsWith(".exit", StringComparison.Ordinal))
                .OrderByDescending(file => file.Name, StringComparer.Ordinal).Select(Read)
                .OfType<UnexpectedExitRecord>().ToArray();
        }
        catch (Exception) { return []; }
    }

    internal void MarkHandled(IEnumerable<UnexpectedExitRecord> records)
    {
        try
        {
            foreach (UnexpectedExitRecord record in records)
            {
                try
                {
                    if (!SafeName(record.Name) || !record.Name.EndsWith(".exit", StringComparison.Ordinal)) continue;
                    File.Move(Path.Combine(Folder, record.Name), Path.Combine(Folder, Path.ChangeExtension(record.Name, ".seen")));
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    internal IEnumerable<(string Name, string Text)> SafeEntries()
    {
        try
        {
            return Files().OrderBy(file => file.Name, StringComparer.Ordinal).Select(Read)
                .OfType<UnexpectedExitRecord>().Select(record => (record.Name, Serialize(record))).ToArray();
        }
        catch (Exception) { return []; }
    }

    private FileInfo[] Files() => !Directory.Exists(Folder) ? [] : new DirectoryInfo(Folder).GetFiles()
        .Where(file => SafeName(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0).ToArray();

    private static bool SafeName(string name) => NamePattern().Match(name) is { Success: true } match
        && DateTimeOffset.TryParseExact(match.Groups[1].Value, NameTimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    private void Prune()
    {
        FileInfo[] files = Files().OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        long bytes = files.Sum(file => file.Length);
        int count = files.Length;
        foreach (FileInfo file in files)
        {
            if (count <= 10 && bytes <= 64 * 1024) break;
            try
            {
                long length = file.Length;
                _deleteFile(file.FullName);
                file.Refresh();
                if (!file.Exists) { count--; bytes -= length; }
            }
            catch (Exception) { }
        }
    }

    private static UnexpectedExitRecord? Read(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumRecordBytes) return null;
            // Also cap bytes read if another process grows the file after the length check.
            var buffer = new byte[MaximumRecordBytes + 1];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumRecordBytes) return null;
            string[] lines = Utf8.GetString(buffer, 0, length).Split('\n');
            if (lines.Length == 0 || lines[0].TrimEnd('\r') != Header) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var logs = new List<string>(8);
            foreach (string raw in lines.Skip(1))
            {
                string line = raw.TrimEnd('\r');
                int separator = line.IndexOf('=');
                if (separator < 1) continue;
                string key = line[..separator], value = line[(separator + 1)..];
                if (key == "log")
                {
                    if (logs.Count < 8 && SafeLogLine.IsWellFormed(value)) logs.Add(value);
                }
                else if (Valid(key, value)) fields.TryAdd(key, value);
            }
            if (!fields.TryGetValue("utc", out string? utcText) || !TryUtc(utcText, out DateTimeOffset utc)) return null;
            string Get(string key) => fields.GetValueOrDefault(key, SafeLogLine.Unknown);
            var primary = new SafeErrorRecord(utc,
                int.TryParse(Get("event"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id) ? id : 0,
                ParseHResult(Get("hresult")))
            {
                Level = Enum.TryParse(Get("level"), out LogLevel level) && level is >= LogLevel.Trace and <= LogLevel.Critical
                    ? level : LogLevel.Error,
                Category = Get("category"), Template = Get("msg"), ExceptionType = Get("ex"),
                BaseExceptionType = Get("base"), BaseHResult = ParseHResult(Get("base-hresult")),
                Site = Get("site"), AppFrame = Get("frame"), StackSignature = Get("stack"),
            };
            return new(file.Name, primary, logs.ToArray(), Get("version"), Get("build"),
                bool.TryParse(Get("packaged"), out bool packaged) ? packaged : null, Get("windows"));
        }
        catch (Exception) { return null; }
    }

    private static bool Valid(string key, string value) => key switch
    {
        "utc" => TryUtc(value, out _),
        "version" or "build" or "windows" or "category" or "ex" or "base" or "site" or "frame" => SafeLogLine.Token(value) == value,
        "packaged" => value is "true" or "false" or "-",
        "level" => Enum.TryParse(value, out LogLevel level) && level is >= LogLevel.Trace and <= LogLevel.Critical
            && level.ToString() == value,
        "event" => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id)
            && id.ToString(CultureInfo.InvariantCulture) == value,
        "msg" or "stack" => SafeErrorRecord.HashToken(value) == value,
        "hresult" or "base-hresult" => value == SafeLogLine.Unknown || HResultPattern().IsMatch(value),
        _ => false,
    };

    private static bool TryUtc(string value, out DateTimeOffset utc) => DateTimeOffset.TryParseExact(value, UtcFormat,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private static int? ParseHResult(string value) => HResultPattern().IsMatch(value)
        && uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code) ? unchecked((int)code) : null;

    private static string Serialize(UnexpectedExitRecord record)
    {
        SafeErrorRecord p = record.Primary;
        var text = new StringBuilder(Header).Append('\n');
        Add("utc", p.Utc.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture));
        Add("version", SafeLogLine.Token(record.Version)); Add("build", SafeLogLine.Token(record.Build));
        Add("packaged", record.Packaged is { } packaged ? packaged ? "true" : "false" : SafeLogLine.Unknown);
        Add("windows", SafeLogLine.Token(record.WindowsVersion));
        Add("level", p.Level is >= LogLevel.Trace and <= LogLevel.Critical ? p.Level.ToString() : "Error");
        Add("category", SafeLogLine.Token(p.Category)); Add("event", p.EventId.ToString(CultureInfo.InvariantCulture));
        Add("msg", SafeErrorRecord.HashToken(p.Template)); Add("ex", SafeLogLine.Token(p.ExceptionType));
        Add("hresult", ErrorReport.HResult(p.HResult)); Add("base", SafeLogLine.Token(p.BaseExceptionType));
        Add("base-hresult", ErrorReport.HResult(p.BaseHResult)); Add("site", SafeLogLine.Token(p.Site));
        Add("frame", SafeLogLine.Token(p.AppFrame)); Add("stack", SafeErrorRecord.HashToken(p.StackSignature));
        foreach (string line in record.RecentLogLines.Where(SafeLogLine.IsWellFormed).TakeLast(8))
        {
            if (Utf8.GetByteCount(text.ToString()) + Utf8.GetByteCount(line) + 5 > MaximumRecordBytes) break;
            Add("log", line);
        }
        return text.ToString();
        void Add(string key, string value) => text.Append(key).Append('=').Append(value).Append('\n');
    }
}
