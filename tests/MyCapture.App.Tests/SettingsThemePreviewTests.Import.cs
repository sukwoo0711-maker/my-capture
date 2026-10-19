using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task ImportedSettingsSaveImmediatelyAndReportResultsAcrossTabs() =>
        RunIsolated(nameof(ImportedSettingsSaveImmediatelyAndReportResultsAcrossTabs), VerifyImportedSettings);

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
        AppSettings saved = new() { General = { Theme = AppThemeNames.Glass }, GitHub = { Token = "" },
            Hotkeys = { CoreShortcutsAsked = true } };
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
        Assert.NotEqual("321", DraftOf(window).MaxItems);
        window.ImportSettingsFrom(importPath);
        Drain(window);
        Assert.Equal(AppThemeNames.GlassLight, saved.General.Theme);
        Assert.NotEqual(persisted, File.ReadAllText(paths.SettingsFile));
        persisted = File.ReadAllText(paths.SettingsFile);
        Assert.Equal("321", Assert.IsType<SettingsDraft>(window.DataContext).MaxItems);
        Assert.True(DraftOf(window).ToAppSettings().Hotkeys.CoreShortcutsAsked);
        AssertPalette(window, AppTheme.GlassLight);

        // Capture both default and minimum client layouts before assertions that also
        // expose the original hidden-status / clean-import regression on the baseline.
        SaveEvidence(window, $"settings-import-{locale}-940x700.png");
        AssertCommandBarFits(window);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Drain(window);
        SaveEvidence(window, $"settings-import-{locale}-780x560.png");
        AssertCommandBarFits(window);

        Assert.Equal("321", DraftOf(window).MaxItems);
        TextBlock status = Assert.IsType<TextBlock>(window.FindName("SettingsStatus"));
        Assert.Equal(UiText.Get("Settings_Saved"), status.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        AssertStatusFits(window, status);
        var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
        tabs.SelectedIndex = 1;
        Drain(window);
        AssertStatusFits(window, status);
        tabs.SelectedItem = window.FindName("UpdateTab");
        Drain(window);
        AssertStatusFits(window, status);
        tabs.SelectedIndex = 0;

        SettingsDraft pending = Assert.IsType<SettingsDraft>(window.DataContext);
        window.ImportSettingsFrom(invalidPath);
        Drain(window);
        Assert.Same(pending, window.DataContext);
        Assert.Equal("321", pending.MaxItems);
        Assert.Equal(UiText.Get("Settings_ImportFailed"), status.Text);
        AssertStatusFits(window, status);
        AssertCommandBarFits(window);
        Assert.Equal(persisted, File.ReadAllText(paths.SettingsFile));
        AssertPalette(window, AppTheme.GlassLight);
        SaveEvidence(window, $"settings-import-error-{locale}-780x560.png");

        string missingPath = Path.Combine(root, new string('a', 200), new string('b', 200), "missing-settings.json");
        window.ImportSettingsFrom(missingPath);
        Drain(window);
        Assert.Same(pending, window.DataContext);
        Assert.Equal("321", pending.MaxItems);
        Assert.StartsWith(UiText.Get("Settings_ImportFailed"), status.Text, StringComparison.Ordinal);
        Assert.Contains("missing-settings.json", status.Text, StringComparison.Ordinal);
        AssertStatusFits(window, status);
        AssertCommandBarFits(window);

        SettingsWindow.CancelCommand.Execute(null, window);
        Assert.False(window.IsVisible);
        Assert.Equal("321", DraftOf(window).MaxItems);
        AssertPalette(window, AppTheme.GlassLight);
        Assert.Equal(persisted, File.ReadAllText(paths.SettingsFile));
        window.ShowSettings();
        Drain(window);
        Assert.True(string.IsNullOrEmpty(status.Text));
        Assert.False(status.IsVisible);
        SettingsDraft clean = Assert.IsType<SettingsDraft>(window.DataContext);
        window.ImportSettingsFrom(invalidPath);
        Assert.Same(clean, window.DataContext);
        Assert.Equal("321", clean.MaxItems);
        AssertPalette(window, AppTheme.GlassLight);

        window.ImportSettingsFrom(importPath);
        Drain(window);
        Assert.Equal("321", DraftOf(window).MaxItems);
        Assert.False(DraftOf(window).HasUnappliedChanges);
        window.ShowSettings();
        Assert.Equal("321", DraftOf(window).MaxItems);
        SettingsWindow.CancelCommand.Execute(null, window);
        window.ShowSettings();
        Assert.Equal("321", DraftOf(window).MaxItems);
        AssertPalette(window, AppTheme.GlassLight);
        Assert.True(string.IsNullOrEmpty(status.Text));

        window.ImportSettingsFrom(importPath);
        SettingsDraft applied = DraftOf(window);
        SettingsWindow.ApplyCommand.Execute(null, window);
        Assert.Same(applied, window.DataContext); // Saving preserves the editor and caret.
        Assert.Equal("321", DraftOf(window).MaxItems);
        Assert.True(window.IsVisible);
        Assert.Equal(AppThemeNames.GlassLight, saved.General.Theme);
        Assert.Equal(AppThemeNames.GlassLight, store.Load().General.Theme);
        Assert.Equal(321, store.Load().Queue.MaxItems);
        Assert.True(store.Load().Hotkeys.CoreShortcutsAsked);
        AssertPalette(window, AppTheme.GlassLight);
    }

    private static SettingsDraft DraftOf(SettingsWindow window) => Assert.IsType<SettingsDraft>(window.DataContext);

    private static void AssertCommandBarFits(SettingsWindow window)
    {
        var apply = Assert.IsType<Button>(window.FindName("CancelButton"));
        var bar = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(apply));
        Button[] buttons = Descendants(bar).OfType<Button>().ToArray();
        Assert.Single(buttons); // The footer has only Close; reset and backup live on About.
        Assert.Same(apply, buttons[0]);
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
