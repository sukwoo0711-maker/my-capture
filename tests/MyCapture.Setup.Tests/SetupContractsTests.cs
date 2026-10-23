using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MyCapture.Setup.Tests;

public sealed class SetupContractsTests
{
    public static PackageManifest Manifest() => new PackageManifest
    {
        Version = "4.2.1", FileCount = 1, TotalExpandedBytes = 1,
        Files = new[] { new FileRecord { Path = "a", Bytes = 1, Sha256 = new string('a', 64) } },
        Payload = new FileRecord { Path = "payload.zip", Bytes = 1, Sha256 = new string('b', 64) }
    };
    internal static void Code(int expected, Action action) => Assert.Equal(expected, Assert.Throws<SetupException>(action).Code);

    [Theory]
    [InlineData("4.2.1")][InlineData("0.0.0")][InlineData("4.3.0-rc.1")][InlineData("4.3.0-alpha-beta")]
    public void ManifestAcceptsSemVer(string version) { var m = Manifest(); m.Version = version; m.Validate(); }
    [Theory]
    [InlineData("01.0.0")][InlineData("4.2")][InlineData("4.2.1+build")][InlineData("4.2.1-01")][InlineData("4.2.1\n")]
    public void ManifestRejectsInvalidVersions(string version) { var m = Manifest(); m.Version = version; Code(14, m.Validate); }
    [Theory]
    [InlineData("schema")][InlineData("product")][InlineData("runtime")][InlineData("architecture")][InlineData("offline")]
    [InlineData("self-contained")][InlineData("floor")][InlineData("empty")][InlineData("count")][InlineData("limit")]
    [InlineData("negative")][InlineData("large-file")][InlineData("hash")][InlineData("duplicate")][InlineData("total")]
    [InlineData("expanded-limit")][InlineData("bootstrap-duplicate")][InlineData("payload-hash")]
    public void ManifestChecksEveryFieldAndLimit(string fault)
    {
        var m = Manifest();
        switch (fault)
        {
            case "schema": m.SchemaVersion = 2; break; case "product": m.Product = "Other"; break;
            case "runtime": m.Runtime = "win-arm64"; break; case "architecture": m.Architecture = "arm64"; break;
            case "offline": m.Offline = false; break; case "self-contained": m.SelfContained = false; break;
            case "floor": m.MinimumWindowsBuild = 19044; break; case "empty": m.Files = Array.Empty<FileRecord>(); m.FileCount = 0; break;
            case "count": m.FileCount = 2; break; case "limit": m.Files = Enumerable.Range(0, 10001).Select(i => new FileRecord { Path = "f" + i, Bytes = 0, Sha256 = new string('a', 64) }).ToArray(); m.FileCount = m.Files.Length; break;
            case "negative": m.Files[0].Bytes = -1; break; case "large-file": m.Files[0].Bytes = 1073741825; break;
            case "hash": m.Files[0].Sha256 = "x"; break; case "payload-hash": m.Payload.Sha256 = "x"; break;
            case "duplicate": m.Files = new[] { m.Files[0], new FileRecord { Path = "A", Bytes = 1, Sha256 = new string('a', 64) } }; m.FileCount = 2; m.TotalExpandedBytes = 2; break;
            case "total": m.TotalExpandedBytes = 2; break;
            case "expanded-limit": m.Files = Enumerable.Range(0, 3).Select(i => new FileRecord { Path = "f" + i, Bytes = 1073741824, Sha256 = new string('a', 64) }).ToArray(); m.FileCount = 3; m.TotalExpandedBytes = 3221225472; break;
            case "bootstrap-duplicate": m.BootstrapFiles = new[] { m.Files[0], m.Files[0] }; break;
        }
        Code(14, m.Validate);
    }
    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("/a")][InlineData("C:/a")][InlineData("a:b")][InlineData("a\\b")]
    [InlineData("../a")][InlineData("a/../b")][InlineData("a/./b")][InlineData("a//b")][InlineData("a/")]
    [InlineData("a. ")][InlineData("a.")][InlineData("NUL.txt")][InlineData("COM1")][InlineData("a\0b")]
    public void PackagePathsRejectAllUnsafeForms(string path) => Code(14, () => PathSafety.PackagePath(path));
    [Theory][InlineData("Assets/a.ico")][InlineData("한글 폴더/file.txt")]
    public void PackagePathsAcceptRelativeUnicode(string path) => Assert.Equal(path, PathSafety.PackagePath(path));
    [Theory][InlineData(@"C:\")][InlineData(@"\\server\share\app")][InlineData(@"\\?\C:\app")][InlineData(@"\\.\C:\app")][InlineData(@"C:\app:stream")]
    public void InstallRootRejectsDriveUncDeviceAndAds(string path) => Code(12, () => PathSafety.InstallRoot(path, Manifest()));
    [Fact] public void InstallRootRejectsExcessiveDepth() => Code(12, () => PathSafety.InstallRoot(@"C:\" + new string('a', 240), Manifest()));
    [Fact] public void UpdateCannotTargetUserOrSystemRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) Code(12, () => PathSafety.UpdateBoundary(path));
        }
        Code(12, () => PathSafety.UpdateBoundary(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MyCapture")));
    }
    [Theory]
    [InlineData(true, 10, 19044, "Client", 0x8664, 10)]
    [InlineData(true, 10, 19045, "Server Core", 0x8664, 10)]
    [InlineData(true, 10, 19045, "Nano Server", 0x8664, 10)]
    [InlineData(false, 10, 19045, "Client", 0x8664, 10)]
    [InlineData(true, 6, 19045, "Client", 0x8664, 10)]
    [InlineData(true, 10, 19045, "Client", 0x14c, 11)]
    [InlineData(true, 10, 19045, "Client", 0xaa64, 11)]
    public void HostRejectsUnsupportedPlatform(bool windows, int major, int build, string type, int native, int code) =>
        Code(code, () => WindowsHost.Validate(windows, major, build, type, (ushort)native, 19045));
    [Theory][InlineData(19045, 0x8664)][InlineData(22000, 0xaa64)][InlineData(26100, 0x8664)]
    public void HostAcceptsWindowsFloorAndNativeArm64(int build, int native) => WindowsHost.Validate(true, 10, build, "Client", (ushort)native, 19045);
    [Fact] public void RuntimeProbeReportsComponentUnavailableBeforeCompression()
    {
        bool loaded = false; Code(19, () => WindowsHost.RequireRuntime(() => 528039, () => loaded = true)); Assert.False(loaded);
        Code(19, () => WindowsHost.RequireRuntime(() => 528040, () => throw new FileNotFoundException("Compression")));
        WindowsHost.RequireRuntime(() => 528040, () => loaded = true); Assert.True(loaded);
    }
    [Fact] public void FreeSpaceRetainsSafetyMarginAndOverride()
    {
        PathSafety.FreeSpace(67108865, 1, 0); Code(13, () => PathSafety.FreeSpace(67108864, 1, 0));
        Code(13, () => PathSafety.FreeSpace(100000000, 1, long.MaxValue));
    }
    [Fact] public void InstalledBootstrapDoesNotReducePayloadCountOrSizeLimits()
    {
        var m = Manifest();
        m.Files = Enumerable.Range(0, 10000).Select(i => new FileRecord { Path = "f" + i, Bytes = i < 2 ? 1073741824 : 0, Sha256 = new string('a', 64) }).ToArray();
        m.FileCount = 10000; m.TotalExpandedBytes = 2147483648L; m.Validate();
        m.Files = m.Files.Concat(new[] { "MyCaptureUninstall.exe", "MyCaptureUninstall.exe.config", "SETUP-HELP.txt" }.Select(n => new FileRecord { Path = n, Bytes = 100, Sha256 = new string('a', 64) })).ToArray();
        m.FileCount = 10003; m.TotalExpandedBytes += 300; m.ValidateInventory(true);
        Code(14, m.Validate); // A package may not claim the installed-only allowance.
    }
    [Fact] public void OptionDefaultsAreRelativeToExecutable()
    {
        var o = Options.Parse(Array.Empty<string>(), @"C:\package"); Assert.Equal(@"C:\package\payload.zip", o.Payload);
        Assert.Equal(@"C:\package\installer-manifest.json", o.Manifest); Assert.Equal("", o.InstallRoot);
        Assert.False(o.Quiet); Assert.False(o.NoShellIntegration); Assert.Null(o.TestRegistryKey); Assert.Null(o.TestStartMenu);
    }
    [Fact] public void OptionsRetainTheEntireParameterSet()
    {
        var o = Options.Parse(new[] { "/Q", "--install-root", @"C:\custom", "--payload", @"C:\p.zip", "--manifest", @"C:\m.json",
            "--log", @"C:\log.txt", "--verify-only", "--no-shell-integration", "--minimum-free-bytes", "9223372036854775807", "--test-fault", "AfterCommit" }, @"C:\package");
        Assert.True(o.Quiet && o.VerifyOnly && o.NoShellIntegration); Assert.Equal(long.MaxValue, o.MinimumFreeBytes);
        Assert.Equal("AfterCommit", o.TestFault); Assert.Equal(@"C:\custom", o.InstallRoot); Assert.Equal(@"C:\p.zip", o.Payload);
        Assert.Equal(@"C:\m.json", o.Manifest); Assert.Equal(@"C:\log.txt", o.Log);
    }
    [Theory][InlineData("--unknown", "x")][InlineData("--minimum-free-bytes", "-1")][InlineData("--test-fault", "BeforeCommit")]
    public void InvalidOptionsReturn21(string option, string value) => Code(21, () => Options.Parse(new[] { option, value }, @"C:\package"));
    [Fact] public void MissingDuplicateAndUninstallRootOptionsAreRejected()
    {
        Code(21, () => Options.Parse(new[] { "--payload" }, @"C:\package"));
        Code(21, () => Options.Parse(new[] { "/quiet", "/quiet" }, @"C:\package"));
        Code(21, () => Options.Parse(new[] { "/uninstall", "--install-root", @"C:\other" }, @"C:\package"));
    }
    [Fact] public void TestTargetsCannotReachRealShellLocations()
    {
        string key = @"Software\MyCapture.SetupTests\" + Guid.NewGuid().ToString("N");
        string temp = Path.Combine(Path.GetTempPath(), "MyCapture-test-menu-" + Guid.NewGuid().ToString("N"));
        Options.ValidateTestTargets(key, temp);
        Code(12, () => Options.ValidateTestTargets(ShellIntegration.DefaultKey, temp));
        Code(12, () => Options.ValidateTestTargets(key + @"\child", temp));
        Code(12, () => Options.ValidateTestTargets(key, Path.GetTempPath()));
        Code(12, () => Options.ValidateTestTargets(key, @"C:\outside"));
        Code(12, () => Options.ValidateTestTargets(key, null));
    }
    [Fact] public void ExactProcessPathIsTheOnlyTerminationCandidate()
    {
        var own = new FakeProcess(@"C:\installed\MyCapture.exe"); var outside = new FakeProcess(@"C:\portable\MyCapture.exe");
        Code(15, () => AppProcesses.Stop(own.Executable, false, false, _ => { }, new[] { own, outside }));
        Assert.False(own.Killed || outside.Killed); Assert.True(own.Disposed && outside.Disposed);
        own = new FakeProcess(@"C:\installed\MyCapture.exe"); outside = new FakeProcess(@"C:\installed-other\MyCapture.exe");
        AppProcesses.Stop(own.Executable, false, true, _ => { }, new[] { own, outside }); Assert.True(own.Killed); Assert.False(outside.Killed);
        own = new FakeProcess(@"C:\installed\MyCapture.exe");
        Code(15, () => AppProcesses.Stop(own.Executable, true, false, _ => { }, new[] { own })); Assert.False(own.Killed);
        AppProcesses.Stop(own.Executable, false, false, _ => { }, Array.Empty<IAppProcess>());
    }
    [Fact] public void ExitMappingKeepsInstallAndUninstallTaxonomy()
    {
        foreach (int code in Enumerable.Range(10, 12).Concat(Enumerable.Range(30, 6)).Append(0))
            Assert.Equal(code, ExitCodes.FromException(new SetupException(code, "test")));
        Assert.Equal(21, ExitCodes.FromException(new IOException())); Assert.Equal(35, ExitCodes.FromException(new IOException(), true));
        Assert.Equal(19, ExitCodes.ComponentUnavailable); Assert.Equal(18, ExitCodes.Concurrent); Assert.Equal(31, ExitCodes.UninstallConcurrent);
    }
    [Fact] public void InteractiveLaunchMatchesStartProcessWindowStyleHidden()
    {
        var info = Installer.LaunchInfo(@"C:\Install Root");
        Assert.Equal(@"C:\Install Root\MyCapture.exe", info.FileName); Assert.Equal(@"C:\Install Root", info.WorkingDirectory);
        Assert.Equal("", info.Arguments); Assert.True(info.UseShellExecute);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, info.WindowStyle);
    }
    [Fact] public void PermanentRemovalUsesTheLegacyRetrySchedules()
    {
        // install.ps1 Remove-DirectoryWithRetry and uninstall-cleanup.ps1 Remove-Target.
        Assert.Equal(new[] { 180, 360, 540, 720, 900, 1080 }, Enumerable.Range(1, 6).Select(FileRemoval.InstallRetryDelay));
        Assert.Equal(225, FileRemoval.UninstallRetryDelay(1)); Assert.Equal(975, FileRemoval.UninstallRetryDelay(11));
        Assert.Equal(1000, FileRemoval.UninstallRetryDelay(12)); Assert.Equal(1000, FileRemoval.UninstallRetryDelay(30));
        Assert.True(FileRemoval.IsWorkerName(".MyCapture-uninstall-cleanup-" + new string('a', 32)));
        foreach (string name in new[] { ".MyCapture-uninstall-cleanup-" + new string('A', 32), ".MyCapture-uninstall-cleanup-" + new string('a', 31), "MyCapture-uninstall-cleanup-" + new string('a', 32), ".MyCapture-uninstall-cleanup-" + new string('a', 32) + ".ps1" })
            Assert.False(FileRemoval.IsWorkerName(name));
    }
    [Theory]
    [InlineData("\"C:\\a b\\MyCapture.exe\" --background", "C:\\a b\\MyCapture.exe")]
    [InlineData("  \"C:\\a\\MyCapture.exe\"  ", "C:\\a\\MyCapture.exe")]
    [InlineData("C:\\a\\MyCapture.exe --background", "C:\\a\\MyCapture.exe")]
    [InlineData("C:\\a\\MyCapture.exe", "C:\\a\\MyCapture.exe")]
    [InlineData("\"C:\\a\\MyCapture.exe", "C:\\a\\MyCapture.exe")]
    [InlineData("\"C:\\a b\\MyCapture.exe", "\"C:\\a")]
    [InlineData("\"\"x", "x")]
    [InlineData(" ", null)][InlineData("", null)][InlineData(null, null)]
    public void StartupCommandExecutableMatchesTheLegacyParser(string? command, string? expected) =>
        Assert.Equal(expected, ShellIntegration.CommandExecutable(command));
    [Fact] public void ReceiptBytesMatchWindowsPowerShell51IncludingHtmlEscaping()
    {
        var receipt = new InstallReceipt { Token = "0123456789abcdef0123456789abcdef", Version = "4.2.1", ExitCode = 17,
            InstallRoot = @"C:\Temp\한글 설치 & (test)", LogPath = @"C:\Temp\MyCapture-Install.log" };
        // Captured from an ordered object serialized by Windows PowerShell 5.1, not by setup.
        const string expected = "{\r\n    \"Token\":  \"0123456789abcdef0123456789abcdef\",\r\n    \"Version\":  \"4.2.1\",\r\n    \"ExitCode\":  17,\r\n    \"InstallRoot\":  \"C:\\\\Temp\\\\한글 설치 \\u0026 (test)\",\r\n    \"LogPath\":  \"C:\\\\Temp\\\\MyCapture-Install.log\"\r\n}";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), receipt.ToBytes());
    }
    [Fact] public void BuiltExecutableEmbedsAsInvokerAndWindowsCompatibility()
    {
        IntPtr module = LoadLibraryEx(Program.Executable, IntPtr.Zero, 2);
        Assert.NotEqual(IntPtr.Zero, module);
        try
        {
            IntPtr resource = FindResource(module, new IntPtr(1), new IntPtr(24)); Assert.NotEqual(IntPtr.Zero, resource);
            int length = checked((int)SizeofResource(module, resource)); var bytes = new byte[length];
            Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, length);
            var document = XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\0'));
            var level = document.Descendants().Single(n => n.Name.LocalName == "requestedExecutionLevel");
            Assert.Equal("asInvoker", level.Attribute("level")!.Value); Assert.Equal("false", level.Attribute("uiAccess")!.Value);
            Assert.Contains(document.Descendants(), n => n.Name.LocalName == "supportedOS" && n.Attribute("Id")!.Value == "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
            Assert.Contains(document.Descendants(), n => n.Name.LocalName == "dpiAwareness" && n.Value == "PerMonitorV2");
            string config = File.ReadAllText(Path.Combine(Fixture.Repository, "src", "MyCapture.Setup", "App.config"));
            Assert.Contains("<supportedRuntime version=\"v4.0\" />", config); Assert.DoesNotContain("sku=", config);
        }
        finally { FreeLibrary(module); }
    }
    private sealed class FakeProcess : IAppProcess
    {
        public FakeProcess(string path) { Executable = path; }
        public int Id => 123; public string Executable { get; } public bool Killed, Disposed;
        public void Close() { } public bool Wait(int milliseconds) => Killed;
        public void Kill() => Killed = true; public void Dispose() => Disposed = true;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
}
