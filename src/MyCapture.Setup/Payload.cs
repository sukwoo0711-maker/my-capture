using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace MyCapture.Setup;

public static class Payload
{
    public static void Extract(string zipPath, PackageManifest manifest, string destination, ISetupProgress? progress = null)
    {
        try
        {
            PathSafety.NotLink(destination);
            Directory.CreateDirectory(destination);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                    string name = PathSafety.PackagePath(directory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName);
                    if (!all.Add(name)) throw new SetupException(14, "Duplicate ZIP entry: " + name);
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new SetupException(14, "ZIP links are not allowed.");
                    if (directory)
                    {
                        if (entry.Length != 0) throw new SetupException(14, "ZIP directory has data.");
                        continue;
                    }
                    entries.Add(name, entry);
                }
                if (entries.Count != manifest.FileCount) throw new SetupException(14, "ZIP file count does not match manifest count.");
                string prefix = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
                int done = 0;
                progress?.Files(done, manifest.FileCount);
                foreach (var file in manifest.Files)
                {
                    if (!entries.TryGetValue(file.Path, out var entry)) throw new SetupException(14, "ZIP entry is missing: " + file.Path);
                    if (entry.Length != file.Bytes) throw new SetupException(14, "ZIP entry size mismatch: " + file.Path);
                    string target = Path.GetFullPath(Path.Combine(destination, PathSafety.PackagePath(file.Path).Replace('/', '\\')));
                    if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new SetupException(14, "ZIP entry escapes staging root.");
                    PathSafety.NoLinksBelow(destination, target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var input = entry.Open())
                    using (var output = File.Open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920]; long written = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            written += count;
                            if (written > file.Bytes) throw new SetupException(14, "ZIP entry exceeded declared size: " + file.Path);
                            output.Write(buffer, 0, count);
                        }
                        if (written != file.Bytes) throw new SetupException(14, "ZIP entry size mismatch: " + file.Path);
                    }
                    if (!file.Matches(target)) throw new SetupException(14, "Extracted file SHA-256 mismatch: " + file.Path);
                    progress?.Files(++done, manifest.FileCount);
                }
            }
        }
        catch (SetupException) { throw; }
        catch (Exception e) { throw new SetupException(16, "Payload extraction failed: " + e.Message); }
    }
    public static ushort PeMachine(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream))
        {
            if (stream.Length < 128 || reader.ReadUInt16() != 0x5A4D) return 0;
            stream.Position = 0x3C; int offset = reader.ReadInt32();
            if (offset < 0 || (long)offset + 6 > stream.Length) return 0;
            stream.Position = offset;
            return reader.ReadUInt32() == 0x4550 ? reader.ReadUInt16() : (ushort)0;
        }
    }
    public static void Application(string root, PackageManifest manifest)
    {
        string exe = Path.Combine(root, "MyCapture.exe"), dll = Path.Combine(root, "MyCapture.dll");
        if (!File.Exists(exe) || !File.Exists(dll)) throw new SetupException(14, "Payload is missing MyCapture.exe or MyCapture.dll.");
        if (PeMachine(exe) != (manifest.Architecture == "x86" ? 0x014c : 0x8664)) throw new SetupException(14, "MyCapture.exe architecture does not match its manifest.");
        if ((FileVersionInfo.GetVersionInfo(dll).ProductVersion ?? "").Split('+')[0] != manifest.Version)
            throw new SetupException(14, "Payload product version does not match manifest version.");
        foreach (string asset in new[] { "tray-idle.ico", "tray-capturing.ico", "tray-busy.ico", "tray-error.ico" })
            if (!File.Exists(Path.Combine(root, "Assets", asset))) throw new SetupException(14, "Required tray asset is missing: " + asset);
    }
    public static void VerifyInventory(string root, PackageManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(root, file.Path.Replace('/', '\\'));
            PathSafety.NoLinksBelow(root, path);
            if (!file.Matches(path)) throw new SetupException(17, "Installed payload verification failed: " + file.Path);
        }
    }
}

public interface IAppProcess : IDisposable
{
    int Id { get; }
    string Executable { get; }
    void Close();
    bool Wait(int milliseconds);
    void Kill();
}

public static class AppProcesses
{
    public static void Stop(string executable, bool update, bool uninstall, Action<string> log, IEnumerable<IAppProcess>? processes = null)
    {
        var matches = new List<IAppProcess>(); bool otherCopy = false;
        try
        {
            foreach (var process in processes ?? Process.GetProcessesByName("MyCapture").Select(p => new AppProcess(p)))
            {
                bool matched = false;
                try
                {
                    matched = PathSafety.Equal(process.Executable, executable);
                    if (matched) matches.Add(process); else otherCopy = true;
                }
                catch { /* An unreadable executable is never selected or terminated. */ }
                finally { if (!matched) process.Dispose(); }
            }
            if (!uninstall && otherCopy) throw new SetupException(15, "Another copy of MyCapture is running. Exit it from its tray menu, then run setup again. No installed files were replaced.");
            if (update && matches.Count != 0) throw new SetupException(15, "MyCapture is running again; update will not stop it or discard work.");
            foreach (var process in matches)
            {
                try
                {
                    log("Stopping running MyCapture process " + process.Id + ".");
                    try { process.Close(); } catch { }
                    if (!process.Wait(4000)) { process.Kill(); if (!process.Wait(6000)) throw new IOException("Process did not stop."); }
                }
                catch (Exception e) { throw new SetupException(uninstall ? 33 : 15, "Unable to stop MyCapture process " + process.Id + ": " + e.Message); }
            }
        }
        finally { foreach (var process in matches) process.Dispose(); }
    }
    private sealed class AppProcess : IAppProcess
    {
        private readonly Process process;
        public AppProcess(Process value) { process = value; }
        public int Id => process.Id;
        public string Executable => process.MainModule.FileName;
        public void Close() => process.CloseMainWindow();
        public bool Wait(int milliseconds) => process.WaitForExit(milliseconds);
        public void Kill() => process.Kill();
        public void Dispose() => process.Dispose();
    }
}
