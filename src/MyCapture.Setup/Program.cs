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
        bool quiet = !SetupInteraction.IsInteractive(args, Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION"));
        bool uninstall = args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--cleanup-request", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--uninstall-ui", StringComparison.OrdinalIgnoreCase));
        // Only CLR 4 APIs run before this probe. WinForms and Compression stay behind the no-inline boundary.
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
        return Execute(args, directory, quiet, uninstall);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Execute(string[] args, string directory, bool quiet, bool uninstall)
    {
        SetupLog? log = null;
        try
        {
            var options = Options.Parse(args, directory);
            bool updateRequested = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION"));
            options.Quiet |= !SetupInteraction.IsInteractive(args, null);
            log = new SetupLog(options.Log, directory, options.Quiet, options.Uninstall, options.UninstallUiRequest != null);
            if (!options.Uninstall && updateRequested)
            {
                var updater = new Installer(options, directory, log);
                var session = updater.PrepareUpdate();
                bool showProgress = SetupInteraction.ShowUpdateProgress(options, session);
                options.Quiet = !showProgress; log.Quiet = !showProgress;
                if (!showProgress) return updater.Run();
                return SetupWindow.RunUpdate(session!.Version, session.WaitForParent, log, progress =>
                    { updater.SetProgress(progress); return updater.Run(); }, updater.OpenUpdatedApplication, () => updater.CanOpenUpdatedApplication);
            }
            options.Quiet |= quiet;
            if (options.Quiet)
                return options.Uninstall ? Uninstaller.Run(options, directory, log) : new Installer(options, directory, log).Run();
            if (options.Uninstall)
                return options.UninstallUiRequest == null ? SetupWindow.LaunchUninstall(options, directory, log) :
                    SetupWindow.Run(true, log, progress => Uninstaller.Run(options, directory, log, progress));
            Installer? installer = null;
            return SetupWindow.Run(false, log, progress => (installer = new Installer(options, directory, log, progress)).Run(),
                () => installer?.Launch());
        }
        catch (Exception e)
        {
            log = log ?? new SetupLog("", directory, quiet, uninstall);
            int code = ExitCodes.FromException(e);
            log.Write("ERROR", "Setup failed (exit " + code + "): " + e.Message);
            return quiet ? code : SetupWindow.Run(uninstall, log, _ => code);
        }
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
