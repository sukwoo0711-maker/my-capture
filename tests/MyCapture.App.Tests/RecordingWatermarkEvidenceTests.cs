using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingWatermarkEvidenceTests
{
    [Fact]
    public Task SettingsEntrySavesAndRejectsAnEditThatBecameLocked() => SettingsThemePreviewTests.RunIsolated(
        nameof(SettingsEntrySavesAndRejectsAnEditThatBecameLocked), () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        AppSettings saved = new();
        bool locked = false;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            saved = candidate;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance) { RecordingWatermarkLocked = () => locked };
        try
        {
            var section = Assert.IsType<StackPanel>(window.FindName("RecordingWatermarkSection"));
            Button settings = section.Children.OfType<Button>().Single();
            window.EditRecordingWatermarkSettings = current => current with
                { Enabled = true, Source = RecordingWatermarkSource.Text, Text = "Saved" };
            settings.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(window.FlushPendingSettings());
            Assert.Equal("Saved", saved.Recording.Watermark.Text);
            Assert.True(saved.Recording.Watermark.Enabled);
            window.EditRecordingWatermarkSettings = current => { locked = true; return current with { Text = "Rejected" }; };
            settings.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(window.FlushPendingSettings());
            Assert.Equal("Saved", saved.Recording.Watermark.Text);
            Assert.False(section.IsEnabled);
            var draft = Assert.IsType<SettingsDraft>(window.DataContext);
            draft.RecordingWatermarkEnabled = false;
            Assert.True(window.FlushPendingSettings());
            Assert.True(saved.Recording.Watermark.Enabled);
            Assert.True(draft.RecordingWatermarkEnabled);
        }
        finally { window.CloseForExit(); application.Shutdown(); }
    }, typeof(RecordingWatermarkEvidenceTests));

    [Fact]
    public Task PreparationBefore() => SettingsThemePreviewTests.RunIsolated(nameof(PreparationBefore), () =>
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_LIBVIDEO_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        if (File.Exists(Path.Combine(output, "recording-preparation-before.png"))) return;
        RenderPreparation(output, "before", null);
    }, typeof(RecordingWatermarkEvidenceTests));

    [Fact]
    public Task PreparationAfter() => SettingsThemePreviewTests.RunIsolated(nameof(PreparationAfter), () =>
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_LIBVIDEO_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        var snapshot = RecordingWatermarkSnapshot.Create(new RecordingWatermarkSettings
            { Enabled = true, Source = RecordingWatermarkSource.Text, Text = "ACME", TextColor = "#202C40" }, null);
        var watermark = new RecordingWatermarkWindow(new RectD(0, 0, 800, 450), 1, snapshot);
        try
        {
            Image image = watermark.ImageForTest;
            watermark.Content = null;
            var layer = new Canvas { Width = 800, Height = 450 };
            image.Width = watermark.PhysicalBounds.Width; image.Height = watermark.PhysicalBounds.Height;
            Canvas.SetLeft(image, watermark.PhysicalBounds.Left); Canvas.SetTop(image, watermark.PhysicalBounds.Top);
            layer.Children.Add(image);
            RenderPreparation(output, "after", layer);
        }
        finally { watermark.Close(); }
    }, typeof(RecordingWatermarkEvidenceTests));

    internal static void RenderPreparation(string output, string phase, FrameworkElement? watermark)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        using var language = UiText.UseLanguage("ko-KR");
        ThemeService.Apply(AppTheme.Daylight);
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var controls = new RecordingControlWindow(new RectD(100, 100, 800, 450), new RecordingSettings(),
            () => throw new InvalidOperationException("Evidence never records."), () => "unused.mp4", NullLogger.Instance);
        try
        {
            var strip = (Border)typeof(RecordingControlWindow).GetField("_controlStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controls)!;
            var canvas = new Canvas { Width = 860, Height = 560, Background = new SolidColorBrush(Color.FromRgb(231, 235, 242)) };
            var scene = new Grid { Width = 800, Height = 450, Background = Brushes.White };
            scene.Children.Add(new TextBlock { Text = "프로젝트 검토\n\n화면 녹화 예시", FontSize = 30, Foreground = Brushes.DimGray, Margin = new Thickness(48) });
            if (watermark is not null) scene.Children.Add(watermark);
            var outline = new Border { Width = 812, Height = 462, BorderThickness = new Thickness(6), BorderBrush = new SolidColorBrush(Color.FromRgb(67, 109, 233)), Child = scene };
            Canvas.SetLeft(outline, 24); Canvas.SetTop(outline, 16); canvas.Children.Add(outline);
            strip.Measure(new Size(560, 56)); strip.Arrange(new Rect(0, 0, 560, 56)); strip.UpdateLayout();
            var stripBitmap = new RenderTargetBitmap(560, 56, 96, 96, PixelFormats.Pbgra32);
            stripBitmap.Render(strip);
            var stripImage = new Image { Source = stripBitmap, Width = 560, Height = 56 };
            Canvas.SetLeft(stripImage, 150); Canvas.SetTop(stripImage, 492); canvas.Children.Add(stripImage);
            canvas.Measure(new Size(860, 560)); canvas.Arrange(new Rect(0, 0, 860, 560)); canvas.UpdateLayout();
            var image = new RenderTargetBitmap(860, 560, 96, 96, PixelFormats.Pbgra32); image.Render(canvas);
            Directory.CreateDirectory(output);
            string path = Path.Combine(output, $"recording-preparation-{phase}.png");
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
            Assert.True(stream.Length > 0);
        }
        finally { controls.Close(); application.Shutdown(); FluidMotion.AnimationsEnabledOverrideForTest = null; }
    }
}
