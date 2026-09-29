using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsThemePreviewTests
{
    private const string ChildFlag = "MYCAPTURE_SETTINGS_THEME_TEST_CHILD";
    private const string ReceiptRoot = "MYCAPTURE_SETTINGS_THEME_TEST_RECEIPT";

    [Fact]
    public async Task ThemeSelectionPreviewsAndAllDismissalPathsRestoreSavedPalette()
    {
        if (Environment.GetEnvironmentVariable(ChildFlag) == "1")
        {
            StaTestHost.Run(VerifyPreviewLifecycle);
            string receipt = DiagnosticOutputPaths.Child(
                Environment.GetEnvironmentVariable(ReceiptRoot) ?? throw new IOException("Missing parent test receipt."), "completed.txt");
            File.WriteAllText(receipt, "PASS");
            return;
        }

        // SettingsWindow's real BAML uses Application resources. Run it in a fresh test
        // process so neither its Application singleton nor its brushes/dispatcher leak
        // into the other multi-STA WPF tests in this assembly.
        string runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        string dotnet = Path.Combine(runtimeRoot, "dotnet.exe");
        Assert.True(File.Exists(dotnet), "The test runtime must provide its dotnet host.");
        string receiptRoot = TestRecycleBin.CreateTempSubdirectory("mc-theme-preview-process-").FullName;
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(SettingsThemePreviewTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(SettingsThemePreviewTests).FullName
            + "." + nameof(ThemeSelectionPreviewsAndAllDismissalPathsRestoreSavedPalette));
        start.ArgumentList.Add("--Logger:console;verbosity=normal");
        start.Environment[ChildFlag] = "1";
        start.Environment[ReceiptRoot] = receiptRoot;
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using Process child = Process.Start(start) ?? throw new IOException("Could not start the isolated WPF test.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                Assert.Fail("The isolated WPF test timed out.\n" + await output + Environment.NewLine + await error);
            }
            Assert.True(child.ExitCode == 0, await output + Environment.NewLine + await error);
            // VSTest may exit successfully when a filter matches no cases. Require proof
            // that this child's complete WPF assertion body actually finished, too.
            Assert.Equal("PASS", File.ReadAllText(Path.Combine(receiptRoot, "completed.txt")));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    private static void VerifyPreviewLifecycle()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = TestRecycleBin.CreateTempSubdirectory("mc-theme-preview-").FullName;
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
        AppSettings saved = new() { General = { Theme = AppThemeNames.Glass } };
        store.Save(saved);
        ThemeService.ApplyFromSettings(saved.General.Theme);
        bool failApply = false;
        int applyCount = 0;
        int saveFailureWarnings = 0;
        var survivingSurface = new Border();
        survivingSurface.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        var survivingWindow = new Window
        {
            Content = survivingSurface,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 160, Height = 120,
            ShowActivated = false, ShowInTaskbar = false,
        };
        survivingWindow.Show();
        SettingsWindow? window = null;
        try
        {
            window = new SettingsWindow(() => saved, candidate =>
            {
                applyCount++;
                if (failApply) return SettingsApplyResult.NotSaved(["Synthetic settings save failure"]);
                store.Save(candidate);
                saved = candidate;
                ThemeService.ApplyFromSettings(saved.General.Theme);
                return new SettingsApplyResult(true, true, true, false, []);
            }, NullLogger.Instance, showSaveFailureMessage: message =>
            {
                Assert.Equal("Synthetic settings save failure", message);
                saveFailureWarnings++;
            })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Width = 940, Height = 700,
                ShowActivated = false, ShowInTaskbar = false,
            };
            // Compare settled UI states without sampling the entrance animation.
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            Drain(window);
            var selector = Assert.IsType<ComboBox>(window.FindName("ThemeSelector"));
            AssertPalette(window, AppTheme.Glass);
            SaveEvidence(window, "theme-before-selection.png");

            SettingsDraft abandoned = Assert.IsType<SettingsDraft>(window.DataContext);
            SelectTheme(window, selector, AppThemeNames.Daylight);
            AssertPalette(window, AppTheme.Daylight);
            Assert.Equal(AppThemeNames.Glass, saved.General.Theme);
            Assert.Equal(AppThemeNames.Glass, store.Load().General.Theme);
            Assert.Equal(0, applyCount);
            SaveEvidence(window, "theme-preview-before-apply.png");

            Assert.IsType<Button>(window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.IsVisible);
            AssertPalette(window, AppTheme.Glass);
            abandoned.Theme = AppThemeNames.HighContrast;
            AssertPalette(window, AppTheme.Glass); // The discarded draft has no live event handlers.

            window.ShowSettings();
            Drain(window);
            Assert.Equal(AppThemeNames.Glass, selector.SelectedValue);
            SaveEvidence(window, "theme-after-cancel.png");
            SelectTheme(window, selector, AppThemeNames.Daylight);
            var escape = Assert.Single(window.InputBindings.OfType<KeyBinding>(), key => key.Key == Key.Escape);
            Assert.Same(SettingsWindow.CancelCommand, escape.Command);
            Assert.IsType<RoutedUICommand>(escape.Command).Execute(null, window);
            Assert.False(window.IsVisible);
            AssertPalette(window, AppTheme.Glass);

            window.ShowSettings();
            SelectTheme(window, selector, AppThemeNames.Daylight);
            window.Close(); // Same Closing path as native X.
            Assert.False(window.IsVisible);
            AssertPalette(window, AppTheme.Glass);

            window.ShowSettings();
            SelectTheme(window, selector, AppThemeNames.Daylight);
            window.ShowSettings(); // Reopening discards any prior visible draft, too.
            Drain(window);
            AssertPalette(window, AppTheme.Glass);
            Assert.Equal(AppThemeNames.Glass, selector.SelectedValue);

            SelectTheme(window, selector, AppThemeNames.Daylight);
            Console.WriteLine("Theme lifecycle: applying a valid draft.");
            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.False(window.IsVisible);
            Assert.Equal(1, applyCount);
            Assert.Equal(AppThemeNames.Daylight, store.Load().General.Theme);
            AssertPalette(window, AppTheme.Daylight);

            window.ShowSettings();
            SelectTheme(window, selector, AppThemeNames.HighContrast);
            failApply = true;
            Console.WriteLine("Theme lifecycle: applying a draft with simulated persistence failure.");
            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.Equal(1, saveFailureWarnings);
            Assert.True(window.IsVisible);
            Assert.Equal(AppThemeNames.HighContrast, Assert.IsType<SettingsDraft>(window.DataContext).Theme);
            AssertPalette(window, AppTheme.HighContrast);
            Assert.Equal(AppThemeNames.Daylight, saved.General.Theme);
            Assert.Equal(AppThemeNames.Daylight, store.Load().General.Theme);
            SettingsWindow.CancelCommand.Execute(null, window);
            AssertPalette(window, AppTheme.Daylight);

            window.ShowSettings();
            SettingsDraft reset = Assert.IsType<SettingsDraft>(window.DataContext);
            reset.ResetToDefaults(); // PropertyChanged(null) must preview the default palette.
            AssertPalette(window, AppTheme.Glass);
            Assert.Equal(AppThemeNames.Daylight, store.Load().General.Theme);
            SettingsWindow.CancelCommand.Execute(null, window);
            AssertPalette(window, AppTheme.Daylight);

            window.ShowSettings();
            SelectTheme(window, selector, AppThemeNames.HighContrast);
            int attemptsBeforeValidation = applyCount;
            SettingsDraft finalDraft = Assert.IsType<SettingsDraft>(window.DataContext);
            finalDraft.MaxItems = "5";
            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.Equal(attemptsBeforeValidation, applyCount);
            Assert.True(window.IsVisible);
            AssertPalette(window, AppTheme.HighContrast);
            window.CloseForExit();
            Console.WriteLine("Theme lifecycle: final window closed.");
            // Closed controls are detached from the resource tree. Verify that an
            // actual surviving WPF surface receives the restored saved palette.
            Assert.Equal(AppTheme.Daylight, ThemeService.Current);
            AssertBrush(survivingSurface.Background, ThemeCatalog.ColorsFor(AppTheme.Daylight)["Surface.Raised"]);
            finalDraft.Theme = AppThemeNames.Midnight;
            Assert.Equal(AppTheme.Daylight, ThemeService.Current);
            AssertBrush(survivingSurface.Background, ThemeCatalog.ColorsFor(AppTheme.Daylight)["Surface.Raised"]);
            window = null;
        }
        finally
        {
            window?.CloseForExit();
            survivingWindow.Close();
            application.Shutdown();
            // Keep the fixture/evidence available for inspection; never permanently delete it.
        }
    }

    private static void SelectTheme(SettingsWindow window, ComboBox selector, string theme)
    {
        selector.SelectedValue = theme;
        Drain(window);
        Assert.Equal(theme, Assert.IsType<SettingsDraft>(window.DataContext).Theme);
    }

    private static void AssertPalette(SettingsWindow window, AppTheme theme)
    {
        Assert.Equal(theme, ThemeService.Current);
        IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);

        // Glass uses the native Mica backdrop, so Window.Background is deliberately
        // transparent. Assert the actual visible page and selector chrome instead of
        // only checking resource entries that might no longer be bound to the UI.
        var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
        var selectedContent = Assert.IsType<ContentPresenter>(tabs.Template.FindName("PART_SelectedContentHost", tabs));
        var page = Assert.IsType<Border>(VisualTreeHelper.GetParent(selectedContent));
        AssertBrush(page.Background, colors["Surface.Raised"]);
        var selector = Assert.IsType<ComboBox>(window.FindName("ThemeSelector"));
        var chrome = Assert.IsType<Border>(selector.Template.FindName("ComboChrome", selector));
        AssertBrush(chrome.Background, colors["Surface.Overlay"]);
        AssertBrush(selector.Foreground, colors["Text.Primary"]);
        Style labelStyle = Assert.IsType<Style>(window.FindResource("Settings.Label"));
        Style descriptionStyle = Assert.IsType<Style>(window.FindResource("Settings.Description"));
        TextBlock[] text = Descendants(page).OfType<TextBlock>().ToArray();
        Assert.Contains(text, block => ReferenceEquals(block.Style, labelStyle));
        Assert.Contains(text, block => ReferenceEquals(block.Style, descriptionStyle));
        foreach (TextBlock label in text.Where(block => ReferenceEquals(block.Style, labelStyle)))
            AssertBrush(label.Foreground, colors["Text.Primary"]);
        foreach (TextBlock description in text.Where(block => ReferenceEquals(block.Style, descriptionStyle)))
            AssertBrush(description.Foreground, colors["Text.Muted"]);
        foreach (TabItem tab in tabs.Items.OfType<TabItem>().Where(tab => !tab.IsSelected))
            AssertBrush(tab.Foreground, colors["Text.Secondary"]);
        AssertBrush(Assert.IsType<Button>(window.FindName("CancelButton")).Foreground, colors["Text.Primary"]);

        if (AppThemeNames.UsesBackdrop(theme))
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(window.Background).Color);
        else
            AssertBrush(window.Background, colors["Surface.Base"]);
    }

    private static void AssertBrush(Brush brush, ThemeColor expected) =>
        Assert.Equal(Color.FromArgb(expected.A, expected.R, expected.G, expected.B),
            Assert.IsType<SolidColorBrush>(brush).Color);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Drain(Window window) =>
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

    private static void SaveEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_THEME_PREVIEW_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }

}
