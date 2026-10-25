using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MyCapture.Setup;

public static class ExitCodes
{
    public const int Success = 0, UnsupportedOs = 10, Architecture = 11, UnsafePath = 12,
        Disk = 13, Integrity = 14, ProcessStop = 15, Staging = 16, Commit = 17,
        Concurrent = 18, ComponentUnavailable = 19, ExistingInstall = 20, Unexpected = 21;
    // 22 (script bootstrap did not start) is retired. Uninstall's existing range is retained.
    public const int InvalidUninstall = 30, UninstallConcurrent = 31, UninstallComponent = 32,
        UninstallProcess = 33, CleanupLaunch = 34, UninstallUnexpected = 35;
    public static int FromException(Exception error, bool uninstall = false) =>
        error is SetupException known ? known.Code : uninstall ? UninstallUnexpected : Unexpected;
}

public sealed class SetupException : Exception
{
    public int Code { get; }
    public SetupException(int code, string message) : base(message) { Code = code; }
}

[DataContract]
public sealed class FileRecord
{
    [DataMember(IsRequired = true)] public string Path { get; set; } = "";
    [DataMember(IsRequired = true)] public long Bytes { get; set; }
    [DataMember(IsRequired = true)] public string Sha256 { get; set; } = "";
    public static FileRecord Create(string path, string relative) => new FileRecord
    { Path = relative, Bytes = new FileInfo(path).Length, Sha256 = Hash(path) };
    public static string Hash(string path)
    {
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
    public bool Matches(string path) => File.Exists(path) && new FileInfo(path).Length == Bytes &&
        string.Equals(Hash(path), Sha256, StringComparison.OrdinalIgnoreCase);
}

[DataContract]
public sealed class PackageManifest
{
    public const string SemVerPattern = @"\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?\z";
    public static readonly string[] BootstrapNames = { "MyCaptureSetup.exe", "MyCaptureSetup.exe.config", "SETUP-HELP.txt" };
    [DataMember(IsRequired = true)] public int SchemaVersion { get; set; } = 1;
    [DataMember(IsRequired = true)] public string Product { get; set; } = "MyCapture";
    [DataMember(IsRequired = true)] public string Version { get; set; } = "";
    [DataMember(IsRequired = true)] public string Runtime { get; set; } = "win-x64";
    [DataMember(IsRequired = true)] public string Architecture { get; set; } = "x64";
    [DataMember(IsRequired = true)] public int MinimumWindowsBuild { get; set; } = 19045;
    [DataMember(IsRequired = true)] public bool SelfContained { get; set; } = true;
    [DataMember(IsRequired = true)] public bool Offline { get; set; } = true;
    [DataMember(IsRequired = true)] public FileRecord Payload { get; set; } = new FileRecord();
    [DataMember(IsRequired = true)] public int FileCount { get; set; }
    [DataMember(IsRequired = true)] public long TotalExpandedBytes { get; set; }
    [DataMember(IsRequired = true)] public FileRecord[] Files { get; set; } = Array.Empty<FileRecord>();
    [DataMember(IsRequired = true)] public FileRecord[] BootstrapFiles { get; set; } = Array.Empty<FileRecord>();
    // Additional installed-only fields are ignored by the previous installer (schema remains 1).
    [DataMember(EmitDefaultValue = false)] public bool? ShellIntegration { get; set; }
    [DataMember(EmitDefaultValue = false)] public string? TestRegistryKey { get; set; }
    [DataMember(EmitDefaultValue = false)] public string? TestStartMenu { get; set; }
    [DataMember(EmitDefaultValue = false)] public string? InstallationId { get; set; }

    public static PackageManifest Read(string path, bool installed = false)
    {
        try { var value = Json.Read<PackageManifest>(path); value.ValidateInventory(installed); return value; }
        catch (SetupException) { throw; }
        catch (Exception e) { throw new SetupException(14, "Installer manifest is invalid JSON: " + e.Message); }
    }
    public void Validate() => ValidateInventory(false);
    public void ValidateInventory(bool installed)
    {
        if (SchemaVersion != 1 || Product != "MyCapture") Fail("Installer manifest schema or product is invalid.");
        if (Version == null || !Regex.IsMatch(Version, SemVerPattern)) Fail("Installer manifest version is invalid.");
        if ((Architecture != "x64" && Architecture != "x86") || Runtime != "win-" + Architecture || !SelfContained || !Offline || MinimumWindowsBuild < 19045)
            Fail("Manifest must describe an offline self-contained win-x64 or win-x86 payload for build 19045 or later.");
        if (Files == null || Files.Length != FileCount || FileCount < 1 || FileCount > (installed ? 10003 : 10000)) Fail("Payload file count is invalid.");
        long total = 0;
        long installedBootstrapBytes = 0; int installedBootstrapCount = 0;
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files!)
        {
            ValidateRecord(file);
            if (!seen.Add(file.Path)) Fail("Duplicate payload path: " + file.Path);
            total += file.Bytes;
            if (installed && (file.Path == "MyCaptureUninstall.exe" || file.Path == "MyCaptureUninstall.exe.config" || file.Path == "SETUP-HELP.txt"))
            { installedBootstrapBytes += file.Bytes; installedBootstrapCount++; }
        }
        if (FileCount - installedBootstrapCount > 10000) Fail("Payload file count is invalid.");
        if (total - installedBootstrapBytes > 2147483648L) Fail("Payload expanded size exceeds the 2 GiB safety limit.");
        if (total != TotalExpandedBytes) Fail("Payload expanded byte total does not match the manifest.");
        ValidateRecord(Payload, 2147483648L);
        if (BootstrapFiles == null) Fail("Bootstrap inventory is missing.");
        seen.Clear();
        foreach (var file in BootstrapFiles!)
        {
            ValidateRecord(file);
            if (!seen.Add(file.Path)) Fail("Duplicate bootstrap path: " + file.Path);
        }
    }
    public static void ValidateRecord(FileRecord file, long limit = 1073741824L)
    {
        if (file == null) Fail("Null inventory entry.");
        PathSafety.PackagePath(file!.Path);
        if (file.Bytes < 0 || file.Bytes > limit || file.Sha256 == null || !Regex.IsMatch(file.Sha256, @"\A[A-Fa-f0-9]{64}\z"))
            Fail("Invalid size or SHA-256: " + file.Path);
    }
    public void VerifyBootstrap(string directory)
    {
        if (BootstrapFiles.Length != BootstrapNames.Length) Fail("Unexpected bootstrap inventory.");
        foreach (string name in BootstrapNames)
        {
            var entries = BootstrapFiles.Where(f => f.Path == name).ToArray();
            string path = System.IO.Path.Combine(directory, name);
            PathSafety.NotLink(path);
            if (entries.Length != 1 || !entries[0].Matches(path)) Fail("Bootstrap integrity check failed: " + name);
        }
    }
    private static void Fail(string message) => throw new SetupException(14, message);
}

[DataContract]
public sealed class UpdaterSession
{
    [DataMember(IsRequired = true)] public string Token { get; set; } = "";
    [DataMember(IsRequired = true)] public string Version { get; set; } = "";
    [DataMember(IsRequired = true)] public string InstallRoot { get; set; } = "";
    [DataMember(IsRequired = true)] public int ParentId { get; set; }
    [DataMember] public string? SourceRoot { get; set; }
    [DataMember] public string? TargetMode { get; set; }
    [DataMember] public long ParentStartedUtc { get; set; }
    [DataMember] public bool WaitForParent { get; set; }
    [DataMember] public bool RestartAfterInstall { get; set; }
    [DataMember] public bool ShowProgress { get; set; }
}

[DataContract]
public sealed class InstallReceipt
{
    [DataMember(Order = 0)] public string Token { get; set; } = "";
    [DataMember(Order = 1)] public string Version { get; set; } = "";
    [DataMember(Order = 2)] public int ExitCode { get; set; }
    [DataMember(Order = 3)] public string? InstallRoot { get; set; }
    [DataMember(Order = 4)] public string? LogPath { get; set; }
    public void Write(string path)
    {
        PathSafety.NoLinks(path);
        string temporary = path + ".tmp";
        using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = ToBytes();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        File.Move(temporary, path);
    }
    public byte[] ToBytes()
    {
        // Windows PowerShell 5.1 ConvertTo-Json: CRLF, two spaces after ':', HTML escaping,
        // no trailing newline and no BOM. Keep this exact wire format for released updaters.
        string text = "{\r\n    \"Token\":  " + Quote(Token) + ",\r\n    \"Version\":  " + Quote(Version) +
            ",\r\n    \"ExitCode\":  " + ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\r\n    \"InstallRoot\":  " + Quote(InstallRoot) + ",\r\n    \"LogPath\":  " + Quote(LogPath) + "\r\n}";
        return new UTF8Encoding(false, true).GetBytes(text);
    }
    private static string Quote(string? value)
    {
        if (value == null) return "null";
        var text = new StringBuilder("\"");
        foreach (char c in value)
            switch (c)
            {
                case '"': text.Append("\\\""); break; case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break; case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break; case '\r': text.Append("\\r"); break; case '\t': text.Append("\\t"); break;
                default:
                    if (c < 32 || c == '&' || c == '<' || c == '>' || c == '\'' || c == '\u0085' || c == '\u2028' || c == '\u2029')
                        text.Append("\\u").Append(((int)c).ToString("x4"));
                    else text.Append(c);
                    break;
            }
        return text.Append('"').ToString();
    }
}

public static class Json
{
    public static T Read<T>(string path)
    {
        // StreamReader accepts the BOM emitted by Windows PowerShell 5.1's Set-Content.
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new SerializationException("JSON exceeds 8 MiB.");
        using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true))
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(reader.ReadToEnd())))
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
    }
    public static byte[] Bytes<T>(T value)
    {
        using (var stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
            return stream.ToArray();
        }
    }
    public static void Write<T>(string path, T value) => File.WriteAllBytes(path, Bytes(value));
}
