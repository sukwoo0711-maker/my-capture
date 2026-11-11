using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class Arm64PackageTests
{
    [Fact]
    public void StagedApplicationMustHaveTheManifestArchitecture()
    {
        using (var f = new Fixture())
        {
            string stage = Path.Combine(f.Root, "publish");
            f.Manifest.Architecture = "arm64"; f.Manifest.Runtime = "win-arm64";
            // The stand-in application is this run's setup executable, which is never built for ARM64.
            SetupContractsTests.Code(14, () => Payload.Application(stage, f.Manifest));
            SetMachine(Path.Combine(stage, "MyCapture.exe"), 0xAA64);
            Payload.Application(stage, f.Manifest);
            f.Manifest.Architecture = "x64"; f.Manifest.Runtime = "win-x64";
            SetupContractsTests.Code(14, () => Payload.Application(stage, f.Manifest));
        }
    }

    [Fact]
    public void ThisComputerAcceptsTheArm64PackageOnlyWhenItIsArm64()
    {
        var manifest = SetupContractsTests.Manifest();
        manifest.Architecture = "arm64"; manifest.Runtime = "win-arm64";
        manifest.Validate();
        ushort native;
        using (var process = Process.GetCurrentProcess())
            Assert.True(IsWow64Process2(process.Handle, out _, out native));
        // An x64 or x86 test host on ARM64 Windows is emulated, as the shipped setup is.
        if (native == 0xAA64) WindowsHost.RequireSupported(manifest);
        else SetupContractsTests.Code(11, () => WindowsHost.RequireSupported(manifest));
    }

    [Fact]
    public void SetupReadsTheExecutablePathOfANativeProcess()
    {
        // Setup finds a running MyCapture by its main module path. In the arm64 package setup is
        // an emulated x64 process and the application is native, so the read must cross that
        // boundary: cmd.exe started from the system folder by a 64-bit process is native.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c set /p unused=")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        using (var child = Process.Start(start))
        {
            try
            {
                string? path = null; Exception? last = null;
                // The read is about a process that is running, as the application is when setup looks
                // for it. One that has only just started has no settled module list yet: .NET Framework
                // then throws Win32Exception, returns no main module, or returns the first module the
                // loader has listed so far (ntdll.dll). All three were seen on the x64 runner, so wait
                // until the image itself is listed. Setup treats an unreadable or other executable as
                // "not this application" (AppProcesses.Stop).
                bool IsCommandProcessor() => path != null && string.Equals(Path.GetFileName(path), "cmd.exe", StringComparison.OrdinalIgnoreCase);
                for (int attempt = 0; attempt < 50 && !IsCommandProcessor(); attempt++)
                {
                    try
                    {
                        child.Refresh();
                        ProcessModule? module = child.MainModule;
                        path = module?.FileName;
                    }
                    catch (System.ComponentModel.Win32Exception e) { last = e; }
                    catch (InvalidOperationException e) { last = e; }
                    if (!IsCommandProcessor()) Thread.Sleep(100);
                }
                Assert.True(path != null, "The main module of a system process could not be read: " + last);
                Assert.Equal("cmd.exe", Path.GetFileName(path), ignoreCase: true);
            }
            finally
            {
                child.StandardInput.Close();
                if (!child.WaitForExit(5000)) child.Kill();
            }
        }
    }

    private static void SetMachine(string path, ushort machine)
    {
        using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        using (var reader = new BinaryReader(file))
        {
            file.Position = 0x3c;
            int header = reader.ReadInt32();
            file.Position = header + 4;
            file.WriteByte((byte)(machine & 0xFF));
            file.WriteByte((byte)(machine >> 8));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
