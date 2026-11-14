using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioSettingsAndEvidenceTests
{
    [Fact]
    public Task AudioSettingsSearchSaveImmediatelyAndRetainOtherPendingEdits() => Isolated(
        nameof(AudioSettingsSearchSaveImmediatelyAndRetainOtherPendingEdits), () =>
    {
        Application app = CreateApplication();
        string root = OwnedTestDirectory.Create("mc-r4-audio-settings-");
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
        AppSettings saved = new();
        int saves = 0;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            store.Save(candidate); saved = candidate; saves++;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance, settingsStore: () => store, defaultCapturesRoot: () => Path.Combine(root, "captures"))
        { ScheduleDelay = (_, _) => new NoopDelay() };
        try
        {
            window.NavigateToSection(SettingsSection.Recording);
            LayoutSettingsContent(window);
            Pump();
            var draft = Assert.IsType<SettingsDraft>(window.DataContext);
            CheckBox system = Assert.IsType<CheckBox>(window.FindSettingControl(nameof(SettingsDraft.RecordSystemAudio)));
            CheckBox microphone = Assert.IsType<CheckBox>(window.FindSettingControl(nameof(SettingsDraft.RecordMicrophone)));
            Assert.False(draft.RecordSystemAudio); Assert.False(draft.RecordMicrophone);
            foreach (CheckBox checkbox in new[] { system, microphone })
            {
                window.FilterSettings((string)checkbox.Content);
                Assert.Equal(Visibility.Visible, ((TabItem)window.FindName("RecordingTab")).Visibility);
                Assert.NotNull(window.FirstSearchMatch);
            }
            window.FilterSettings(string.Empty);
            draft.RecordingStartDelaySeconds = "7";
            ClickCheckBox(system);
            Pump();
            Assert.Equal(1, saves);
            Assert.True(store.Load().Recording.RecordSystemAudio);
            Assert.False(store.Load().Recording.RecordMicrophone);
            Assert.Equal(3, store.Load().Recording.StartDelaySeconds);
            ClickCheckBox(microphone);
            Pump();
            Assert.Equal(2, saves);
            Assert.True(store.Load().Recording.RecordMicrophone);
            saved.Recording.RecordSystemAudio = false;
            saved.Recording.RecordMicrophone = false;
            window.RefreshRecordingAudioPreferences();
            Assert.False(draft.RecordSystemAudio); Assert.False(draft.RecordMicrophone);
            Assert.Equal("7", draft.RecordingStartDelaySeconds);
            Assert.Equal(2, saves);
            var accepted = (Dictionary<string, object?>)typeof(SettingsWindow)
                .GetField("_acceptedValues", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Equal(false, accepted[nameof(SettingsDraft.RecordSystemAudio)]);
            Assert.Equal(false, accepted[nameof(SettingsDraft.RecordMicrophone)]);
            Assert.True(window.FlushPendingSettings());
            Assert.Equal(7, saved.Recording.StartDelaySeconds);
            Assert.False(saved.Recording.RecordSystemAudio); Assert.False(saved.Recording.RecordMicrophone);
        }
        finally { window.CloseForExit(); app.Shutdown(); FluidMotion.AnimationsEnabledOverrideForTest = null; }
    });

    [Fact]
    public Task RenderControlMenusLabelsAndRecordingSettings() => Isolated(nameof(RenderControlMenusLabelsAndRecordingSettings), () =>
    {
        string directory = OwnedTestDirectory.Create("mc-r4-audio-controls-evidence-");
        Application app = CreateApplication();
        try
        {
            foreach (string culture in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(culture);
                foreach (int sources in Enumerable.Range(0, 4))
                {
                    var controls = RecordingAudioControlsTests.Create();
                    try
                    {
                        controls.PreviewAudioSessionForTest(true, RecordingAudioControlsTests.Snapshot(sources));
                        Border strip = RecordingAudioEvidenceTests.Field<Border>(controls, "_controlStrip");
                        foreach (double scale in new[] { 1d, 1.5d, 2d })
                        {
                            RecordingAudioControlsTests.Layout(strip);
                            Save(directory, $"{culture}-controls-{sources}-{scale * 100:0}.png", strip, scale);
                        }
                    }
                    finally { controls.Close(); }
                }
                foreach (bool enabled in new[] { true, false })
                {
                    var controls = RecordingAudioControlsTests.Create();
                    try
                    {
                        controls.PreviewAudioSessionForTest(enabled, RecordingAudioControlsTests.Snapshot(enabled ? 3 : 0));
                        ContextMenu menu = controls.CreateAudioMenu();
                        menu.ApplyTemplate();
                        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
                        Assert.True(menu.ActualWidth > 0 && menu.ActualHeight > 0);
                        SaveControlAndMenu(directory, $"{culture}-audio-menu-{(enabled ? "enabled" : "disabled")}-100.png", controls, menu);
                    }
                    finally { controls.Close(); }
                }
                AppSettings saved = new() { General = { Theme = AppThemeNames.Daylight } };
                var settings = new SettingsWindow(() => saved, candidate =>
                { saved = candidate; return new SettingsApplyResult(true, true, true, false, []); }, NullLogger.Instance,
                    defaultCapturesRoot: () => Path.Combine(directory, "captures"))
                {
                    Width = 1000, Height = 720, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                    ShowActivated = false, Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual,
                };
                try
                {
                    settings.NavigateToSection(SettingsSection.Recording);
                    LayoutSettingsContent(settings);
                    Save(directory, $"{culture}-recording-settings-100.png", (FrameworkElement)settings.Content);
                }
                finally { settings.CloseForExit(); }
            }
            Assert.Equal(30, Directory.GetFiles(directory, "*.png").Length);
            TestDiagnosticArtifacts.WriteJson(directory, "rendering.json", new
            {
                Kind = "Synthetic WPF rendering; not a desktop capture",
                Menus = "Measured production control strip and ContextMenu rendered separately, then composed at the audio button's bottom placement with the menu's VerticalOffset.",
                Background = "SystemColors.WindowBrush", SettingsContentWidth = 1000, SettingsContentHeight = 720,
            });
            Console.WriteLine($"R4 recording controls/settings evidence: {directory}");
        }
        finally { app.Shutdown(); FluidMotion.AnimationsEnabledOverrideForTest = null; }
    });

    private static Application CreateApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        ThemeService.Apply(AppTheme.Daylight);
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        return app;
    }
    private static void Save(string directory, string filename, FrameworkElement element, double scale = 1)
    {
        TestDiagnosticArtifacts.SavePng(directory, filename, Render(element, scale));
    }
    private static BitmapSource Render(FrameworkElement element, double scale = 1)
    {
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0,
            $"{element.GetType().Name} must have a measured render area: {element.ActualWidth} × {element.ActualHeight}.");
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backing = new DrawingVisual();
        using (DrawingContext drawing = backing.RenderOpen())
            drawing.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        image.Render(backing);
        image.Render(element);
        return image;
    }
    private static void SaveControlAndMenu(string directory, string filename, RecordingControlWindow controls, ContextMenu menu)
    {
        Border strip = RecordingAudioEvidenceTests.Field<Border>(controls, "_controlStrip");
        RecordingAudioControlsTests.Layout(strip);
        Rect button = controls.AudioButtonForTest.TransformToAncestor(strip)
            .TransformBounds(new Rect(controls.AudioButtonForTest.RenderSize));
        const double padding = 20;
        double menuLeft = padding + button.Left, menuTop = padding + button.Bottom + menu.VerticalOffset;
        var canvas = new Canvas
        {
            Width = Math.Max(padding + strip.ActualWidth, menuLeft + menu.ActualWidth) + padding,
            Height = Math.Max(padding + strip.ActualHeight, menuTop + menu.ActualHeight) + padding,
            Background = SystemColors.WindowBrush,
        };
        var stripImage = new Image { Source = Render(strip), Width = strip.ActualWidth, Height = strip.ActualHeight };
        var menuImage = new Image { Source = Render(menu), Width = menu.ActualWidth, Height = menu.ActualHeight };
        Canvas.SetLeft(stripImage, padding); Canvas.SetTop(stripImage, padding);
        Canvas.SetLeft(menuImage, menuLeft); Canvas.SetTop(menuImage, menuTop);
        canvas.Children.Add(stripImage); canvas.Children.Add(menuImage);
        RecordingAudioControlsTests.Layout(canvas);
        Save(directory, filename, canvas);
    }
    private static void LayoutSettingsContent(SettingsWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1000, 720));
        content.Arrange(new Rect(0, 0, 1000, 720)); content.UpdateLayout();
        Pump();
    }
    private static void ClickCheckBox(CheckBox checkbox)
    {
        BindingExpression binding = Assert.IsType<BindingExpression>(checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty));
        Assert.Equal(BindingStatus.Active, binding.Status);
        // Run ToggleButton's actual click: it preserves the binding via SetCurrentValue and
        // raises the product's normal Checked/Click events, without real input or activation.
        typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(checkbox, null);
        Assert.Same(binding, checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty));
    }
    private static Task Isolated(string name, Action action) => SettingsThemePreviewTests.RunIsolated(name, action,
        typeof(RecordingAudioSettingsAndEvidenceTests));
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private sealed class NoopDelay : IDisposable { public void Dispose() { } }
}
