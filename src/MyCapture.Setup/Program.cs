using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace MyCapture.Setup;

public static class Program
{
    public static string Executable => System.Reflection.Assembly.GetExecutingAssembly().Location;
    [STAThread]
    public static int Main(string[] args) => Run(args);
    public static int Run(string[] args, Func<int>? releaseProbe = null, Action? compressionProbe = null,
        string? directoryOverride = null, Action<bool, string, bool>? popup = null)
    {
        string directory = directoryOverride ?? Path.GetDirectoryName(Executable)!;
        bool quiet = args.Any(a => string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/q", StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION")) || args.Contains("--cleanup-request");
        bool uninstall = args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)) || args.Contains("--cleanup-request");
        // Only CLR 4 APIs run before this probe. No Compression types appear in Main's JIT graph.
        try { WindowsHost.RequireRuntime(releaseProbe, compressionProbe); }
        catch (SetupException e)
        {
            var earlyLog = new SetupLog("", directory, quiet, uninstall);
            earlyLog.Write("ERROR", "Setup failed (exit 19): " + e.Message);
            string guidance = WindowsHost.RuntimeGuidance(directory, earlyLog.Path);
            earlyLog.Write("ERROR", guidance);
            if (!quiet) (popup ?? WindowsHost.Popup)(false, guidance, uninstall);
            return 19;
        }
        try { return Execute(args, directory, quiet); }
        catch (Exception e)
        {
            var log = new SetupLog("", directory, quiet, uninstall);
            int code = ExitCodes.FromException(e);
            log.Write("ERROR", "Setup failed (exit " + code + "): " + e.Message);
            if (!quiet) WindowsHost.Popup(false, e.Message + "\r\nLog: " + log.Path, uninstall);
            return code;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Execute(string[] args, string directory, bool quiet)
    {
        var options = Options.Parse(args, directory); options.Quiet |= quiet;
        var log = new SetupLog(options.Log, directory, options.Quiet, options.Uninstall);
        return options.Uninstall ? Uninstaller.Run(options, directory, log) : new Installer(options, directory, log).Run();
    }
    public static string Quote(string value)
    {
        // CommandLineToArgvW quoting, including trailing backslashes and literal quotes.
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2); return result.Append('"').ToString();
    }
}
