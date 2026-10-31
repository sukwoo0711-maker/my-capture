using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeJourneyEvidenceTests
{
    [Fact]
    public Task EveryStepInBothLanguagesAndAccessibleSizes() => SettingsThemePreviewTests.RunIsolated(
        nameof(EveryStepInBothLanguagesAndAccessibleSizes), Render, typeof(PracticeJourneyEvidenceTests));

    private static void Render()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work/tutorial-impl"));
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string dictionary in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{dictionary}.xaml") });
        try
        {
            foreach (string language in new[] { "ko", "en" })
            {
                using var culture = UiText.UseLanguage(language == "ko" ? "ko-KR" : "en-US");
                UiText.Configure(language);
                foreach (var (name, dpi, textScale, theme) in new[]
                {
                    ("default", 96u, 1d, AppTheme.Glass),
                    ("text150", 144u, 1.5, AppTheme.Glass),
                    ("contrast200", 144u, 2d, AppTheme.HighContrast),
                })
                {
                    ThemeService.Apply(theme);
                    var monitor = new MonitorInfo("practice", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), dpi, true);
                    var guide = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor, options: new WelcomeOptions
                    {
                        ReducedMotion = true, TextScale = textScale, MonitorProvider = () => monitor, MediaFactory = () => new FakeTutorialMedia(), CopyPracticePicture = () => 42,
                    }) { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                    guide.LimitToWorkArea(monitor);
                    using var tracking = new WindowTrackSizeScope(guide, new Size(1920, 1080));
                    try
                    {
                        guide.Show();
                        VisualTreeHelper.SetRootDpi(guide, new DpiScale(dpi / 96d, dpi / 96d));
                        foreach (UIElement element in FirstRunWelcomeTests.Descendants(guide).Prepend(guide).OfType<UIElement>())
                        { element.InvalidateMeasure(); element.InvalidateArrange(); }
                        foreach (PracticeStep step in PracticeSession.Steps)
                        {
                            Assert.Equal(step, guide.Session.Step);
                            guide.Session.ClearFeedback();
                            Save(guide, root, $"{name}-{language}-{step}-waiting", dpi);
                            PracticeJourneyDriver.Complete(guide.Session);
                            if (name == "default" && step is PracticeStep.B0 or PracticeStep.B1 or PracticeStep.B2 or PracticeStep.B4)
                            {
                                Assert.True(guide.SuccessShown);
                                Save(guide, root, $"{name}-{language}-{step}-success", dpi);
                            }
                        }
                        Save(guide, root, $"{name}-{language}-results", dpi);
                        guide.Session.StartGroup(PracticeStep.A1); guide.Session.Back();
                        Save(guide, root, $"{name}-{language}-groups", dpi);
                    }
                    finally { guide.Close(); }
                }
            }
        }
        finally { app.Shutdown(); }
    }

    private static void Save(WelcomeWindow guide, string directory, string name, uint dpi)
    {
        guide.Dispatcher.Invoke(guide.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)guide.Content;
        if (content is Panel panel) panel.Background = guide.Background;
        Assert.True(content.ActualWidth > 0 && content.ActualHeight > 0);
        Assert.True(guide.Scroller.ExtentWidth <= guide.Scroller.ViewportWidth + 1, name);
        foreach (Button button in new[] { guide.TryNowButton, guide.SkipButton, guide.BackButton, guide.SkipStepButton }.Where(button => button.IsVisible))
        {
            Rect bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= content.ActualHeight + 1, $"{name}: {button.Content} {bounds}");
        }
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}
