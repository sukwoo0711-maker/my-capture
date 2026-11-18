#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property ImportDirectoryBuildProps=false
#:property ImportDirectoryBuildTargets=false
#:property NuGetAudit=false
#:property RestoreSources=$(MSBuildThisFileDirectory)
#:property UseSharedCompilation=false
#:property TreatWarningsAsErrors=true
#:property PublishAot=false

// One implementation shared by the .cmd and optional .ps1 launchers. No package references.
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
try
{
    return SizeReport.Run(args);
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"size-report: {error.Message}");
    return 2;
}

internal static class SizeReport
{
    private const string Projection = "Microsoft.Windows.SDK.NET.dll";
    private const string CompressionDescription =
        "Raw Deflate per-file payload, .NET CompressionLevel.SmallestSize. " +
        "Excludes ZIP headers, MSIX block boundaries/signature and installer container overhead. " +
        "This is a payload estimate; artifact bytes are measured separately.";

    internal static int Run(string[] args)
    {
        var folders = new List<string>();
        var artifacts = new List<string>();
        bool json = false, verifyIdentical = false, verifyProjection = false, allowDebuggers = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--json": json = true; break;
                case "--verify-identical": verifyIdentical = true; break;
                case "--verify-only-projection": verifyProjection = true; break;
                case "--allow-missing-debuggers": allowDebuggers = true; break;
                case "--artifact":
                    if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--artifact requires a file path.");
                    artifacts.Add(Path.GetFullPath(args[index]));
                    break;
                case "--help":
                    Console.WriteLine("Usage: size-report.cmd PUBLISH [CANDIDATE] [--json] [--artifact FILE] " +
                        "[--verify-identical | --verify-only-projection [--allow-missing-debuggers]]");
                    return 0;
                default:
                    if (args[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option: {args[index]}");
                    folders.Add(Path.GetFullPath(args[index]));
                    break;
            }
        }

        if (folders.Count is < 1 or > 2 ||
            ((verifyIdentical || verifyProjection) && folders.Count != 2) ||
            (verifyIdentical && verifyProjection) || (allowDebuggers && !verifyProjection))
            throw new ArgumentException("Supply one or two publish folders; verification requires two. " +
                "Choose one verification mode. --allow-missing-debuggers requires --verify-only-projection.");

        var report = new Report
        {
            Folders = folders.Select(Inventory).ToArray(),
            Artifacts = artifacts.Order(StringComparer.Ordinal).Select(MeasureArtifact).ToArray(),
        };
        if (folders.Count == 2)
        {
            Dictionary<string, FileRecord> before = report.Folders[0].Inventory.ToDictionary(f => f.Path, StringComparer.Ordinal);
            Dictionary<string, FileRecord> after = report.Folders[1].Inventory.ToDictionary(f => f.Path, StringComparer.Ordinal);
            report.Differences = before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(path =>
                {
                    before.TryGetValue(path, out FileRecord? a);
                    after.TryGetValue(path, out FileRecord? b);
                    string status = a is null ? "added" : b is null ? "removed" : a.Sha256 == b.Sha256 ? "identical" : "changed";
                    return new Difference(path, status, (b?.Bytes ?? 0) - (a?.Bytes ?? 0),
                        (b?.DeflateBytes ?? 0) - (a?.DeflateBytes ?? 0));
                }).ToArray();
            if (verifyIdentical || verifyProjection)
            {
                report.VerificationMode = verifyIdentical ? "identical" : "only-projection";
                report.UnexpectedChanges = report.Differences.Where(f => f.Status != "identical" &&
                    !(verifyProjection && f.Path == Projection && f.Status == "changed") &&
                    !(allowDebuggers && f.Status == "removed" && IsDebuggerFile(f.Path))).ToArray();
                report.AllowedDebuggerOmissions = report.Differences.Where(f =>
                    allowDebuggers && f.Status == "removed" && IsDebuggerFile(f.Path)).Select(f => f.Path).ToArray();
                report.Verification = report.UnexpectedChanges.Length == 0 ? "PASS" : "FAIL";
            }
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }));
        else
            Print(report);
        return report.Verification == "FAIL" ? 1 : 0;
    }

    private static bool IsDebuggerFile(string path) => Regex.IsMatch(path,
        @"\A(?:mscordaccore(?:_(?:amd64_amd64|x86_x86|arm64_arm64)_[0-9]+(?:\.[0-9]+){3})?|mscordbi|Microsoft\.DiaSymReader\.Native\.(?:amd64|x86|arm64))\.dll\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static FolderReport Inventory(string root)
    {
        RequireRegular(root, directory: true);
        var files = new List<FileRecord>();
        Walk(root);
        FileRecord[] ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        return new FolderReport(root, ordered.Length, ordered.Sum(f => f.Bytes), ordered.Sum(f => f.DeflateBytes),
            ordered.OrderByDescending(f => f.Bytes).ThenBy(f => f.Path, StringComparer.Ordinal).Take(40).ToArray(),
            Group(ordered, f => f.Path.Contains('/') ? f.Path[..f.Path.IndexOf('/')] : "(root)"),
            Group(ordered, f => Path.GetExtension(f.Path).ToLowerInvariant() is { Length: > 0 } extension ? extension : "(none)"),
            ordered);

        void Walk(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Refusing a reparse point: {path}");
                if ((attributes & FileAttributes.Directory) != 0)
                    Walk(path);
                else
                {
                    // A single read handle denies concurrent writers throughout hashing and compression.
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    long bytes = input.Length;
                    string sha256 = Convert.ToHexStringLower(SHA256.HashData(input));
                    input.Position = 0;
                    using var count = new CountingStream();
                    using (var deflate = new DeflateStream(count, CompressionLevel.SmallestSize, leaveOpen: true))
                        input.CopyTo(deflate);
                    files.Add(new FileRecord(Path.GetRelativePath(root, path).Replace('\\', '/'), bytes, count.Length, sha256));
                }
            }
        }
    }

    private static ArtifactRecord MeasureArtifact(string path)
    {
        RequireRegular(path, directory: false);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new ArtifactRecord(path, input.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
    }

    private static void RequireRegular(string path, bool directory)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing a reparse point: {path}");
        if (((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException($"Expected {(directory ? "directory" : "file")}: {path}");
    }

    private static GroupRecord[] Group(FileRecord[] files, Func<FileRecord, string> key) => files
        .GroupBy(key, StringComparer.Ordinal)
        .Select(g => new GroupRecord(g.Key, g.Count(), g.Sum(f => f.Bytes), g.Sum(f => f.DeflateBytes)))
        .OrderByDescending(g => g.Bytes).ThenBy(g => g.Name, StringComparer.Ordinal).ToArray();

    private static void Print(Report report)
    {
        Console.WriteLine(CompressionDescription);
        foreach (FolderReport folder in report.Folders)
        {
            Console.WriteLine($"\n{folder.Root}\nFILES\t{folder.Files}\nBYTES\t{folder.Bytes}\nDEFLATE_BYTES\t{folder.DeflateBytes}");
            Console.WriteLine("\nLARGEST 40: path / bytes / deflate / sha256");
            foreach (FileRecord file in folder.Largest) PrintFile(file);
            Console.WriteLine("\nTOP-LEVEL: name / count / bytes / deflate");
            foreach (GroupRecord group in folder.Folders) PrintGroup(group);
            Console.WriteLine("\nEXTENSION: name / count / bytes / deflate");
            foreach (GroupRecord group in folder.Extensions) PrintGroup(group);
            Console.WriteLine("\nALL FILES: path / bytes / deflate / sha256");
            foreach (FileRecord file in folder.Inventory) PrintFile(file);
        }
        if (report.Differences is not null)
        {
            Console.WriteLine("\nDIFFERENCES: path / status / bytes delta / deflate delta (candidate minus baseline)");
            foreach (Difference difference in report.Differences)
                Console.WriteLine(FormattableString.Invariant($"{difference.Path}\t{difference.Status}\t{difference.BytesDelta}\t{difference.DeflateDelta}"));
        }
        if (report.Artifacts.Length > 0)
        {
            Console.WriteLine("\nACTUAL ARTIFACTS: path / bytes / sha256");
            foreach (ArtifactRecord artifact in report.Artifacts)
                Console.WriteLine(FormattableString.Invariant($"{artifact.Path}\t{artifact.Bytes}\t{artifact.Sha256}"));
        }
        if (report.Verification is not null)
            Console.WriteLine($"\nVERIFY_{report.VerificationMode}={report.Verification}");
    }

    private static void PrintFile(FileRecord file) => Console.WriteLine(FormattableString.Invariant(
        $"{file.Path}\t{file.Bytes}\t{file.DeflateBytes}\t{file.Sha256}"));
    private static void PrintGroup(GroupRecord group) => Console.WriteLine(FormattableString.Invariant(
        $"{group.Name}\t{group.Files}\t{group.Bytes}\t{group.DeflateBytes}"));

    private sealed class Report
    {
        public int SchemaVersion => 1;
        public string Compression => CompressionDescription;
        public required FolderReport[] Folders { get; init; }
        public required ArtifactRecord[] Artifacts { get; init; }
        public Difference[]? Differences { get; set; }
        public string? VerificationMode { get; set; }
        public string? Verification { get; set; }
        public Difference[]? UnexpectedChanges { get; set; }
        public string[]? AllowedDebuggerOmissions { get; set; }
    }
    private sealed record FileRecord(string Path, long Bytes, long DeflateBytes, string Sha256);
    private sealed record GroupRecord(string Name, int Files, long Bytes, long DeflateBytes);
    private sealed record FolderReport(string Root, int Files, long Bytes, long DeflateBytes,
        FileRecord[] Largest, GroupRecord[] Folders, GroupRecord[] Extensions, FileRecord[] Inventory);
    private sealed record ArtifactRecord(string Path, long Bytes, string Sha256);
    private sealed record Difference(string Path, string Status, long BytesDelta, long DeflateDelta);

    // No compressed payload is held in memory or written to disk.
    private sealed class CountingStream : Stream
    {
        private long count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => count;
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int length) => count = checked(count + length);
        public override void Write(ReadOnlySpan<byte> buffer) => count = checked(count + buffer.Length);
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
