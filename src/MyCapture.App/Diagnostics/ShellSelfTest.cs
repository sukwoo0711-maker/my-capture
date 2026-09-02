using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Diagnostics;

internal static class ShellSelfTest
{
    internal const string CommandLineSwitch = "--selftest-shell";

    internal static int Run(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var report = new StringBuilder();
        report.AppendLine("MyCapture shell self-test");
        report.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        report.AppendLine($"OS: {Environment.OSVersion}");
        report.AppendLine();

        bool hotkeyReceived = false;

        try
        {
            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddDebug();
            });

            using var window = new NativeMessageWindow();
            report.AppendLine($"Message-only HWND: 0x{window.Handle.ToInt64():X}");
            report.AppendLine($"TaskbarCreated message: 0x{window.TaskbarCreatedMessage:X}");

            string assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
            var assets = new TrayIconAssets(
                Path.Combine(assetsRoot, "tray-idle.ico"),
                Path.Combine(assetsRoot, "tray-capturing.ico"),
                Path.Combine(assetsRoot, "tray-busy.ico"),
                Path.Combine(assetsRoot, "tray-error.ico"));

            using var tray = new TrayIconService(
                window,
                assets,
                loggerFactory.CreateLogger<TrayIconService>());
            tray.Initialize();
            tray.SetCaptureCount(42);
            tray.SetState(TrayIconState.Capturing);
            tray.SetState(TrayIconState.Busy);
            tray.SetState(TrayIconState.Error);
            tray.SetState(TrayIconState.Idle);
            report.AppendLine("Tray add/modify/state/delete path: PASS");
            report.AppendLine($"Tray tooltip count: {tray.CaptureCount}");

            if (!tray.PostExplorerRestartDiagnostic())
            {
                throw new InvalidOperationException(
                    "Could not post the TaskbarCreated diagnostic message.");
            }

            PumpDispatcherOnce();
            report.AppendLine(
                $"TaskbarCreated recovery: {(tray.IsAdded ? "PASS" : "FAIL")}");
            if (!tray.IsAdded)
            {
                throw new InvalidOperationException(
                    "The tray icon was not restored after TaskbarCreated.");
            }

            using var hotkeys = new GlobalHotkeyService(
                window,
                loggerFactory.CreateLogger<GlobalHotkeyService>());
            hotkeys.Pressed += (_, args) =>
            {
                if (args.Command == GlobalHotkeyCommand.CaptureRegion)
                {
                    hotkeyReceived = true;
                }
            };

            hotkeys.Initialize(new HotkeySettings());
            report.AppendLine(
                $"Registered hotkeys: {string.Join(", ", hotkeys.RegisteredCommands.Order())}");

            foreach (HotkeyRegistrationFailure failure in hotkeys.Failures)
            {
                report.AppendLine(
                    $"HOTKEY FAILURE: {failure.Command} {failure.Hotkey} " +
                    $"Win32={failure.NativeErrorCode} {failure.NativeMessage}");
            }

            if (!hotkeys.PostDiagnosticCommand(GlobalHotkeyCommand.CaptureRegion))
            {
                throw new InvalidOperationException(
                    "Could not post the diagnostic capture hotkey message.");
            }

            PumpDispatcherOnce();
            report.AppendLine($"WM_HOTKEY dispatch: {(hotkeyReceived ? "PASS" : "FAIL")}");

            bool captureRegistered = hotkeys.RegisteredCommands.Contains(
                GlobalHotkeyCommand.CaptureRegion);
            if (!captureRegistered || !hotkeyReceived || hotkeys.Failures.Count != 0)
            {
                throw new InvalidOperationException(
                    "One or more production hotkeys could not be registered or dispatched.");
            }

            report.AppendLine();
            report.AppendLine("RESULT: PASS");
            File.WriteAllText(
                Path.Combine(outputDirectory, "shell-selftest-report.txt"),
                report.ToString(),
                Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine();
            report.AppendLine("RESULT: FAIL");
            report.AppendLine(ex.ToString());
            File.WriteAllText(
                Path.Combine(outputDirectory, "shell-selftest-report.txt"),
                report.ToString(),
                Encoding.UTF8);
            return 2;
        }
    }

    private static void PumpDispatcherOnce()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
