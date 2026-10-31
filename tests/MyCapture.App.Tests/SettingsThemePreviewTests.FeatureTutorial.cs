using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task FeatureTutorial_FitsA1080pScreenAt150PercentWithA16By9PreviewReadableStepsAndClose() =>
        RunIsolated(nameof(FeatureTutorial_FitsA1080pScreenAt150PercentWithA16By9PreviewReadableStepsAndClose), VerifyFeatureTutorialLayout);

    [Fact]
    public Task FeatureTutorial_EnglishDefaultAt125PercentHasFullPreviewAndSmallWorkAreaClamps() =>
        RunIsolated(nameof(FeatureTutorial_EnglishDefaultAt125PercentHasFullPreviewAndSmallWorkAreaClamps),
            () => VerifyFeatureTutorialLayout(englishDefault: true));

    private static void VerifyFeatureTutorialLayout() => VerifyFeatureTutorialLayout(englishDefault: false);

    private static void VerifyFeatureTutorialLayout(bool englishDefault)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        ThemeService.Apply(AppTheme.Glass);
        // The direct text-copy key adds the third instruction paragraph: the tallest tutorial.
        var keys = new HotkeySettings { CaptureText = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54) };
        try
        {
            if (englishDefault)
            {
                using var locale = UiText.UseLanguage("en-US");
                // 1920x1040 physical work area at the device's 125% scale.
                VerifyTutorialFits(keys, "text-copy", new Size(1536, 832), fullPreview: true, "tutorial-en-default-125.png");
                VerifyTutorialFits(keys, "text-copy", new Size(1280, 688), fullPreview: false, "tutorial-en-small-125.png");
                var small = new FeatureTutorialWindow(keys, mediaFactory: () => new FakeTutorialMedia());
                try
                {
                    small.FitToWorkArea(new Size(500, 440));
                    Assert.Equal(500, small.Width); Assert.Equal(440, small.Height);
                    Assert.Equal(500, small.MinWidth); Assert.Equal(440, small.MinHeight);
                }
                finally { small.Close(); }
                return;
            }
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var locale = UiText.UseLanguage(language);
                foreach (string feature in new[] { "text-copy", "pin-click-through" })
                {
                    // 1920×1080 at 150% leaves about 1280×688 DIPs above the taskbar; 1920×1040 is 100%.
                    VerifyTutorialFits(keys, feature, new Size(1280, 688), fullPreview: false, $"tutorial-{language}-{feature}-1280x688-150.png");
                    VerifyTutorialFits(keys, feature, new Size(1920, 1040), fullPreview: true, $"tutorial-{language}-{feature}-1920x1040-150.png");
                }
            }
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyTutorialFits(HotkeySettings keys, string feature, Size workArea, bool fullPreview, string evidence)
    {
        var media = new FakeTutorialMedia();
        var window = new FeatureTutorialWindow(keys, feature, () => media)
        {
            AssetDirectory = TutorialTestAssets.Create(feature), WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        window.FitToWorkArea(workArea);
        Assert.Equal(720, window.Width);
        Assert.Equal(Math.Min(800, workArea.Height), window.Height);
        Assert.Equal(520, window.MinWidth); Assert.Equal(480, window.MinHeight);
        try
        {
            window.Show();
            Drain(window);
            Assert.True(window.HasFirstFrame);
            Assert.InRange(window.ActualWidth, 0, workArea.Width + 0.5);
            Assert.InRange(window.ActualHeight, 0, workArea.Height + 0.5);

            var frame = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(media.View));
            Assert.InRange(frame.ActualWidth / frame.ActualHeight, 16.0 / 9 - 0.01, 16.0 / 9 + 0.01);
            Assert.InRange(frame.ActualWidth, 0, FeatureTutorialWindow.PreviewMaxWidth + 0.5);
            Assert.InRange(frame.ActualHeight, 160, FeatureTutorialWindow.PreviewMaxHeight + 0.5);
            if (fullPreview)
            {
                Assert.True(frame.ActualWidth >= 640, $"Preview width: {frame.ActualWidth} DIP");
                Assert.True(frame.ActualHeight >= 360, $"Preview height: {frame.ActualHeight} DIP");
                Assert.Equal(FeatureTutorialWindow.PreviewMaxWidth, frame.ActualWidth, 0.5);
                Assert.Equal(FeatureTutorialWindow.PreviewMaxHeight, frame.ActualHeight, 0.5);
            }

            var root = (FrameworkElement)window.Content;
            Button close = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, UiText.Get("Dialog.Close")));
            Rect closeBounds = close.TransformToAncestor(root).TransformBounds(new Rect(close.RenderSize));
            Assert.True(close.IsVisible);
            Assert.InRange(closeBounds.Bottom, closeBounds.Height, root.ActualHeight + 0.5);
            Assert.InRange(closeBounds.Right, closeBounds.Width, root.ActualWidth + 0.5);

            // Every step is fully on screen, above the Close button, without scrolling.
            TextBlock[] steps = TutorialCatalog.Get(feature).StepsFor(keys)
                .Select(step => Descendants(window).OfType<TextBlock>().Single(text => KoreanWordWrap.Strip(text.Text) == step)).ToArray();
            var scroller = (ScrollViewer)Descendants(window).OfType<ScrollViewer>().Single(viewer => viewer.Content is StackPanel panel
                && panel.Children.Contains(steps[0]));
            Assert.InRange(scroller.ExtentHeight, 0, scroller.ViewportHeight + 0.5);
            foreach (TextBlock step in steps)
            {
                Rect bounds = step.TransformToAncestor(root).TransformBounds(new Rect(step.RenderSize));
                Assert.True(step.IsVisible && step.ActualHeight > 0);
                Assert.InRange(bounds.Bottom, 0, closeBounds.Top + 0.5);
            }
            SaveEvidenceAt150Percent(window, evidence);
        }
        finally { window.Close(); }
    }

    private static void SaveEvidenceAt150Percent(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_THEME_PREVIEW_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        // The window's visual is its client area; the frame and title bar belong to Windows.
        var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth * 1.5), (int)Math.Ceiling(client.ActualHeight * 1.5),
            144, 144, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
