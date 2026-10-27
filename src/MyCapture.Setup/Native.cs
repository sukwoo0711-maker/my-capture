using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MyCapture.Setup;

public static class WindowsHost
{
    public static void RequireRuntime(Func<int>? releaseProbe = null, Action? compressionProbe = null)
    {
        try
        {
            int release = (releaseProbe ?? ReadRelease)();
            if (release < 528040) throw new InvalidOperationException(".NET Framework 4.8 is missing or damaged.");
            (compressionProbe ?? LoadCompression)();
        }
        catch (Exception e) { throw new SetupException(19, "A required Windows component is unavailable: " + e.Message); }
    }
    private static int ReadRelease()
    {
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32))
        using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            return Convert.ToInt32(key?.GetValue("Release", 0));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadCompression()
    {
        Assembly.Load("System.IO.Compression, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        Assembly.Load("System.IO.Compression.FileSystem, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
    }
    public static void RequireSupported(PackageManifest manifest)
    {
        int build = Environment.OSVersion.Version.Build;
        string installation = "";
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (int.TryParse(Convert.ToString(key?.GetValue("CurrentBuildNumber")), out int registryBuild)) build = Math.Max(build, registryBuild);
                installation = Convert.ToString(key?.GetValue("InstallationType")) ?? "";
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        Validate(Environment.OSVersion.Platform == PlatformID.Win32NT, Environment.OSVersion.Version.Major,
            build, installation, 0x8664, manifest.MinimumWindowsBuild, manifest.Architecture);
        ushort native;
        try
        {
            using (var process = Process.GetCurrentProcess())
                if (!IsWow64Process2(process.Handle, out _, out native)) throw new SetupException(19, "Cannot detect the native Windows architecture.");
        }
        catch (EntryPointNotFoundException) { throw new SetupException(19, "The required Windows architecture API is unavailable."); }
        Validate(Environment.OSVersion.Platform == PlatformID.Win32NT, Environment.OSVersion.Version.Major,
            build, installation, native, manifest.MinimumWindowsBuild, manifest.Architecture);
    }
    public static void Validate(bool windows, int major, int build, string installation, ushort native, int minimum, string architecture = "x64")
    {
        if (!windows || major < 10 || build < Math.Max(19045, minimum) ||
            installation.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 || installation.IndexOf("Nano", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new SetupException(10, "Windows 10 version 22H2 (build 19045) or later with Desktop Experience is required; detected " + build + " " + installation + ".");
        if ((architecture != "x64" && architecture != "x86") ||
            (native != 0x8664 && native != 0xAA64 && !(architecture == "x86" && native == 0x014c)) ||
            (native == 0xAA64 && build < 22000))
            throw new SetupException(11, "The package architecture requires matching Windows or supported Windows 11 ARM64 emulation.");
    }
    public static void Popup(bool success, string message, bool uninstall = false) =>
        MessageBoxW(IntPtr.Zero, message, uninstall ? "MyCapture Uninstall" : "MyCapture Setup", success ? 0x40u : 0x10u);
    public static string RuntimeGuidance(string directory, string? log)
    {
        string help = Path.Combine(Path.GetTempPath(), "MyCapture-Setup-Help.txt");
        try { PathSafety.NotLink(help); File.Copy(Path.Combine(directory, "SETUP-HELP.txt"), help, true); } catch { }
        return "A required Windows component (.NET Framework 4.8 / System.IO.Compression) is unavailable.\r\n" +
            "Use MyCapture-<version>-win-" + (Environment.Is64BitProcess ? "x64" : "x86") + "-portable.zip or the Microsoft Store version.\r\n" +
            "https://apps.microsoft.com/detail/9PB33MC095VR\r\nHelp: " + help + "\r\nLog: " + log;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}

public static class ShellLink
{
    public static void Write(string path, string target, string arguments, string root, string icon, string description)
    {
        var instance = new ShellLinkObject();
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(target); link.SetArguments(arguments); link.SetWorkingDirectory(root);
            link.SetIconLocation(icon, 0); link.SetDescription(description);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    public static string Target(string path)
    {
        var instance = new ShellLinkObject();
        try
        {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Load(path, 0);
            var text = new StringBuilder(32768);
            ((IShellLinkW)instance).GetPath(text, text.Capacity, IntPtr.Zero, 4);
            return text.ToString();
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    public static string Arguments(string path)
    {
        var instance = new ShellLinkObject();
        try
        {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)instance).Load(path, 0);
            var text = new StringBuilder(32768);
            ((IShellLinkW)instance).GetArguments(text, text.Capacity);
            return text.ToString();
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLinkObject { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey);
        void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string text, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
