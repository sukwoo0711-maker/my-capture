using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.App.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// A headless self-test for the settings pipeline: draft validation, model mapping, a
/// transactional hotkey reconfigure through a real message window, and launch-at-login
/// enable/disable against an <em>in-memory fake</em> Run key.
/// </summary>
/// <remarks>
/// This never mutates the real per-user Run key: the autostart portion runs entirely
/// against a fake store. The hotkey portion uses the real registration seam so it also
/// verifies the native path, but it registers and immediately releases benign chords.
/// </remarks>
internal static class SettingsSelfTest
{
    internal const string CommandLineSwitch = "--selftest-settings";

    internal static int Run(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var report = new StringBuilder();
        report.AppendLine("MyCapture settings self-test");
        report.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        report.AppendLine();

        try
        {
            // 1) Draft round-trip and validation.
            var settings = new AppSettings();
            var draft = new SettingsDraft(settings);
            Check(report, "Default draft is valid", !draft.HasErrors);

            draft.MaxItems = "5";               // below the floor of 10
            Check(report, "Out-of-range MaxItems flagged", draft.HasErrors);
            draft.MaxItems = "300";
            Check(report, "Corrected MaxItems clears error", !draft.HasErrors);

            draft.PasteToScreenHotkey = draft.CaptureHotkey; // duplicate
            Check(report, "Duplicate hotkey flagged", draft.HasErrors);
            draft.PasteToScreenHotkey = "F3";
            Check(report, "Distinct hotkey clears error", !draft.HasErrors);

            AppSettings mapped = draft.ToAppSettings();
            Check(report, "Mapping preserves capture hotkey",
                mapped.Hotkeys.Capture.ToString() == "Ctrl+Shift+C");

            // 2) Deep-clone isolation.
            AppSettings clone = settings.DeepClone();
            clone.Queue.MaxItems = 999;
            Check(report, "Deep clone isolates edits", settings.Queue.MaxItems != 999);

            // 3) Launch-at-login against a FAKE store (never the real Run key).
            var fake = new FakeRunKeyStore();
            var startup = new StartupRegistrationService(fake, @"C:\Apps\MyCapture\MyCapture.exe");
            Check(report, "Autostart starts disabled", !startup.IsEnabled());
            StartupApplyResult enabled = startup.Apply(desiredEnabled: true);
            Check(report, "Autostart enable succeeds", enabled.Succeeded && startup.IsEnabled());
            Check(report, "Autostart command is quoted",
                fake.Get(StartupRegistrationService.RunValueName) == "\"C:\\Apps\\MyCapture\\MyCapture.exe\"");
            StartupApplyResult disabled = startup.Apply(desiredEnabled: false);
            Check(report, "Autostart disable succeeds", disabled.Succeeded && !startup.IsEnabled());

            // 4) Transactional hotkey reconfigure through the real seam.
            using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
            using var window = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(window, loggerFactory.CreateLogger<GlobalHotkeyService>());
            hotkeys.Initialize(new HotkeySettings());
            PumpDispatcherOnce();

            HotkeyReconfigureResult result = TryApplyDiagnosticHotkey(hotkeys, out Hotkey appliedHotkey);
            if (result.Applied)
            {
                report.AppendLine($"INFO: Diagnostic hotkey {appliedHotkey}");
            }
            else
            {
                foreach (HotkeyRegistrationFailure failure in result.Failures)
                {
                    report.AppendLine($"INFO: Diagnostic hotkey rejected: {failure.Hotkey} ({failure.NativeErrorCode}: {failure.NativeMessage})");
                }
            }
            Check(report, "Reconfigure applied",
                result.Applied && hotkeys.RegisteredCommands.Contains(GlobalHotkeyCommand.CaptureRegion));

            // 5) Ctrl+S / Esc key bindings resolve to the window's real commands.
            //    Guards against the {Binding} regression: the DataContext is the draft (no
            //    commands), so the bindings must target the static RoutedUICommand fields.
            var settingsWindow = new SettingsWindow(
                () => new AppSettings(),
                _ => new SettingsApplyResult(
                    Saved: true,
                    HotkeysApplied: true,
                    StartupApplied: true,
                    RestartRequired: false,
                    Messages: Array.Empty<string>()),
                loggerFactory.CreateLogger<SettingsWindow>());
            try
            {
                var applyKey = FindKeyBinding(settingsWindow, Key.S, ModifierKeys.Control);
                Check(report, "Ctrl+S binding present", applyKey is not null);
                Check(report, "Ctrl+S invokes ApplyCommand",
                    ReferenceEquals(applyKey!.Command, SettingsWindow.ApplyCommand));

                var escKey = FindKeyBinding(settingsWindow, Key.Escape, ModifierKeys.None);
                Check(report, "Esc binding present", escKey is not null);
                Check(report, "Esc invokes CancelCommand",
                    ReferenceEquals(escKey!.Command, SettingsWindow.CancelCommand));
            }
            finally
            {
                settingsWindow.CloseForExit();
            }

            report.AppendLine();
            report.AppendLine("RESULT: PASS");
            File.WriteAllText(Path.Combine(outputDirectory, "settings-selftest-report.txt"), report.ToString(), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine();
            report.AppendLine("RESULT: FAIL");
            report.AppendLine(ex.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "settings-selftest-report.txt"), report.ToString(), Encoding.UTF8);
            return 2;
        }
    }

    internal static HotkeyReconfigureResult TryApplyDiagnosticHotkey(
        GlobalHotkeyService hotkeys,
        out Hotkey appliedHotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);

        HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        HotkeyReconfigureResult? lastResult = null;
        for (uint virtualKey = Hotkey.VkF1 + 23; virtualKey >= Hotkey.VkF1 + 19; virtualKey--)
        {
            var probe = new HotkeySettings
            {
                Capture = new Hotkey(modifiers, virtualKey),
                PasteToScreen = Hotkey.None,
                HideAllPins = Hotkey.None,
                ToggleClickThrough = Hotkey.None,
                RepeatLastRegion = Hotkey.None,
                CaptureWindow = Hotkey.None,
                CaptureFullScreen = Hotkey.None,
                RecordRegion = Hotkey.None,
            };

            lastResult = hotkeys.Reconfigure(probe);
            if (lastResult.Applied
                && hotkeys.RegisteredCommands.Contains(GlobalHotkeyCommand.CaptureRegion))
            {
                appliedHotkey = probe.Capture;
                return lastResult;
            }
        }

        appliedHotkey = Hotkey.None;
        return lastResult ?? HotkeyReconfigureResult.RolledBack([]);
    }

    private static void Check(StringBuilder report, string name, bool passed)
    {
        report.AppendLine($"{(passed ? "PASS" : "FAIL")}: {name}");
        if (!passed)
        {
            throw new InvalidOperationException($"Self-test assertion failed: {name}");
        }
    }

    private static KeyBinding? FindKeyBinding(SettingsWindow window, Key key, ModifierKeys modifiers)
    {
        foreach (InputBinding binding in window.InputBindings)
        {
            if (binding is KeyBinding kb && kb.Key == key && kb.Modifiers == modifiers)
            {
                return kb;
            }
        }

        return null;
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

    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;

        public string? GetValue(string name) => Get(name);

        public void SetValue(string name, string value) => _values[name] = value;

        public void DeleteValue(string name) => _values.TryRemove(name, out _);
    }
}
