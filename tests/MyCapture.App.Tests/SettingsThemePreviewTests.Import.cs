using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.App.Updates;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task ImportedSettingsStayUnappliedAndReportResultsAcrossTabs() =>
        RunIsolated(nameof(ImportedSettingsStayUnappliedAndReportResultsAcrossTabs), VerifyImportedSettings);

    private static void VerifyImportedSettings()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                VerifyImportedSettingsForLanguage(locale);
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void VerifyImportedSettingsForLanguage(string locale)
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-settings-import-").FullName;
        AppPaths paths = AppPaths.CreateForRoot(root);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings saved = new() { General = { Theme = AppThemeNames.Glass }, GitHub = { Token = "" } };
        store.Save(saved);
        string persisted = File.ReadAllText(paths.SettingsFile);
        AppSettings imported = new() { General = { Theme = AppThemeNames.Daylight }, Queue = { MaxItems = 321 } };
        string importPath = Path.Combine(root, "imported-settings.json");
        store.ExportTo(imported, importPath);
        string invalidPath = Path.Combine(root, "invalid-settings.json");
        File.WriteAllText(invalidPath, "{ invalid json }");
        ThemeService.ApplyFromSettings(saved.General.Theme);
        using var lifetime = new SettingsWindowLifetime(new SettingsWindow(() => saved, candidate =>
        {
            store.Save(candidate);
            saved = candidate;
            ThemeService.ApplyFromSettings(saved.General.Theme);
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance, () => store, message => Assert.Fail(message))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 940, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
        });
        SettingsWindow window = lifetime.Window;
        FluidMotion.SetWindowEntrance(window, false);
        window.Show();
        Drain(window);
        Assert.False(IsDraftDirty(window));
        window.ImportSettingsFrom(importPath);
        Drain(window);
        Assert.Equal(AppThemeNames.Glass, saved.General.Theme);
        Assert.Equal(persisted, File.ReadAllText(paths.SettingsFile));
        Assert.Equal("321", Assert.IsType<SettingsDraft>(window.DataContext).MaxItems);
        AssertPalette(window, AppTheme.Daylight);

        // Capture both default and minimum client layouts before assertions that also
        // expose the original hidden-status / clean-import regression on the baseline.
        SaveEvidence(window, $"settings-import-{locale}-940x700.png");
        AssertCommandBarFits(window);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Drain(window);
        SaveEvidence(window, $"settings-import-{locale}-780x560.png");
        AssertCommandBarFits(window);

        Assert.True(IsDraftDirty(window), "An imported draft must block an update restart even when the saved GitHub token is empty.");
        TextBlock status = Assert.IsType<TextBlock>(window.FindName("SettingsStatus"));
        Assert.Equal(UiText.Get("Settings_ImportDone"), status.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        AssertStatusFits(window, status);
        AssertImportedDraftBlocksUpdate(window, root);
        var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
        tabs.SelectedIndex = 1;
        Drain(window);
        AssertStatusFits(window, status);
        tabs.SelectedItem = window.FindName("UpdateTab");
        Drain(window);
        AssertStatusFits(window, status);
        Assert.Equal(UpdateStrings.Busy, Assert.IsType<TextBlock>(window.FindName("UpdateStatus")).Text);
        tabs.SelectedIndex = 0;

        SettingsDraft pending = Assert.IsType<SettingsDraft>(window.DataContext);
        window.ImportSettingsFrom(invalidPath);
        Drain(window);
        Assert.Same(pending, window.DataContext);
        Assert.True(IsDraftDirty(window));
        Assert.Equal(UiText.Get("Settings_ImportFailed"), status.Text);
        AssertStatusFits(window, status);
        AssertCommandBarFits(window);
        Assert.Equal(persisted, File.ReadAllText(paths.SettingsFile));
        AssertPalette(window, AppTheme.Daylight);
        SaveEvidence(window, $"settings-import-error-{locale}-780x560.png");

        string missingPath = Path.Combine(root, new string('a', 200), new string('b', 200), "missing-settings.json");
        window.ImportSettingsFrom(missingPath);
        Drain(window);
        Assert.Same(pending, window.DataContext);
        Assert.True(IsDraftDirty(window));
        Assert.StartsWith(UiText.Get("Settings_ImportFailed"), status.Text, StringComparison.Ordinal);
        Assert.Contains("missing-settings.json", status.Text, StringComparison.Ordinal);
        AssertStatusFits(window, status);
        AssertCommandBarFits(window);

        SettingsWindow.CancelCommand.Execute(null, window);
        Assert.False(IsDraftDirty(window));
        AssertPalette(window, AppTheme.Glass);
        Assert.Equal(persisted, File.ReadAllText(paths.SettingsFile));
        window.ShowSettings();
        Drain(window);
        Assert.True(string.IsNullOrEmpty(status.Text));
        Assert.False(status.IsVisible);
        SettingsDraft clean = Assert.IsType<SettingsDraft>(window.DataContext);
        window.ImportSettingsFrom(invalidPath);
        Assert.Same(clean, window.DataContext);
        Assert.False(IsDraftDirty(window));
        AssertPalette(window, AppTheme.Glass);

        window.ImportSettingsFrom(importPath);
        Assert.True(IsDraftDirty(window));
        window.ShowSettings();
        Assert.False(IsDraftDirty(window));
        AssertPalette(window, AppTheme.Glass);
        Assert.True(string.IsNullOrEmpty(status.Text));

        window.ImportSettingsFrom(importPath);
        SettingsWindow.ApplyCommand.Execute(null, window);
        Assert.False(IsDraftDirty(window));
        Assert.False(window.IsVisible);
        Assert.Equal(AppThemeNames.Daylight, saved.General.Theme);
        Assert.Equal(AppThemeNames.Daylight, store.Load().General.Theme);
        Assert.Equal(321, store.Load().Queue.MaxItems);
        AssertPalette(window, AppTheme.Daylight);
    }

    private static bool IsDraftDirty(SettingsWindow window) =>
        (bool)typeof(SettingsWindow).GetField("_settingsEdited", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static void AssertImportedDraftBlocksUpdate(SettingsWindow window, string root)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo staged = typeof(SettingsWindow).GetField("_stagedUpdate", flags)!;
        FieldInfo target = typeof(SettingsWindow).GetField("_updateTarget", flags)!;
        object? originalTarget = target.GetValue(window);
        var package = new VerifiedUpdatePackage(new UpdateVersion(9, 0, 0), Path.Combine(root, "never-executed.exe"),
            "fixture", "fixture", 1, root, "Fixture", "", new Uri("https://example.invalid/release"), DateTimeOffset.UtcNow);
        int exitChecks = 0;
        window.CanExitForUpdate = () => { exitChecks++; return false; };
        window.ExitForUpdate = () => Assert.Fail("An imported draft must never exit for an update.");
        try
        {
            staged.SetValue(window, package);
            target.SetValue(window, new UpdateTarget(root, root, false));
            int generation = (int)typeof(SettingsWindow).GetField("_updateProgressGeneration", flags)!.GetValue(window)!;
            Task install = Assert.IsAssignableFrom<Task>(typeof(SettingsWindow)
                .GetMethod("InstallStagedUpdateAsync", flags)!.Invoke(window, [generation]));
            install.GetAwaiter().GetResult();
            Assert.Equal(0, exitChecks); // The dirty draft short-circuits before any handoff checks.
        }
        finally
        {
            // No package files exist or were executed. Do not invoke package cleanup on
            // the fixture root; retain all settings/evidence for inspection.
            staged.SetValue(window, null);
            target.SetValue(window, originalTarget);
            window.CanExitForUpdate = null;
            window.ExitForUpdate = null;
        }
    }

    private static void AssertCommandBarFits(SettingsWindow window)
    {
        var apply = Assert.IsType<Button>(window.FindName("ApplyButton"));
        var bar = Assert.IsType<Grid>(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(apply)));
        Button[] buttons = Descendants(bar).OfType<Button>().ToArray();
        Assert.Equal(5, buttons.Length);
        foreach (Button button in buttons)
        {
            AssertFits(window, button);
            foreach (ContentPresenter presenter in Descendants(button).OfType<ContentPresenter>())
            {
                AssertFits(window, presenter);
                Rect contentBounds = presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize));
                Assert.True(contentBounds.Left >= 0 && contentBounds.Right <= button.ActualWidth + 0.5,
                    $"{button.Content}: label {contentBounds} outside button {button.RenderSize}");
            }
        }
    }

    private static void AssertStatusFits(SettingsWindow window, TextBlock status)
    {
        Assert.True(status.IsVisible);
        Assert.True(status.ActualHeight > 0);
        AssertFits(window, Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(status)));
    }

    private static void AssertFits(SettingsWindow window, FrameworkElement element)
    {
        FrameworkElement content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= content.ActualWidth + 0.5
            && bounds.Bottom <= content.ActualHeight + 0.5, $"{element.Name}: {bounds} outside {content.RenderSize}");
        Rect clientBounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        Assert.True(GetClientRect(new WindowInteropHelper(window).Handle, out ImportClientRect client));
        Vector viewport = PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice
            .Transform(new Vector(client.Right - client.Left, client.Bottom - client.Top));
        Assert.True(clientBounds.Left >= -0.5 && clientBounds.Top >= -0.5 && clientBounds.Right <= viewport.X + 0.5
            && clientBounds.Bottom <= viewport.Y + 0.5, $"{element.Name}: {clientBounds} outside native client {viewport}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ImportClientRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out ImportClientRect rectangle);

    private sealed class SettingsWindowLifetime(SettingsWindow window) : IDisposable
    {
        internal SettingsWindow Window { get; } = window;
        public void Dispose() => Window.CloseForExit();
    }
}
