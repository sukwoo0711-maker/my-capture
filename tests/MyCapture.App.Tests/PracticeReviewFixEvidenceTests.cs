using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeReviewFixEvidenceTests
{
    [Fact]
    public Task ChangedStepsInKoreanAndEnglish() => SettingsThemePreviewTests.RunIsolated(
        nameof(ChangedStepsInKoreanAndEnglish), Render, typeof(PracticeReviewFixEvidenceTests));

    private static void Render()
    {
        string root = DiagnosticOutputPaths.Create(Path.Combine(AppContext.BaseDirectory, "../../../../../work/tutorial-fixes"));
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_REVIEW_PHASE") ?? "after";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string dictionary in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{dictionary}.xaml") });
        try
        {
            ThemeService.Apply(AppTheme.Glass);
            foreach (string language in new[] { "ko", "en" })
            {
                using var culture = UiText.UseLanguage(language == "ko" ? "ko-KR" : "en-US");
                UiText.Configure(language);
                var guide = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor, options: new WelcomeOptions
                { ReducedMotion = true, MediaFactory = () => new FakeTutorialMedia(), CopyPracticePicture = () => 42 })
                { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    guide.Show();
                    foreach (PracticeStep step in PracticeSession.Steps)
                    {
                        guide.Session.ClearFeedback();
                        if (step is PracticeStep.A1 or PracticeStep.A4 or PracticeStep.A5 or PracticeStep.A6
                            or PracticeStep.B2 or PracticeStep.B3 or PracticeStep.B4 or PracticeStep.B5)
                            Save(guide, root, $"{phase}-{language}-{step}");
                        PracticeJourneyDriver.Complete(guide.Session);
                        if (step == PracticeStep.B6)
                        {
                            guide.Session.Back();
                            Save(guide, root, $"{phase}-{language}-B6-restored-back");
                            guide.Session.Skip();
                        }
                    }
                }
                finally { guide.Close(); }
            }
        }
        finally { app.Shutdown(); }
    }

    private static void Save(WelcomeWindow guide, string root, string name)
    {
        guide.Dispatcher.Invoke(guide.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)guide.Content;
        if (content is Panel panel) panel.Background = guide.Background;
        Assert.True(guide.Scroller.ExtentWidth <= guide.Scroller.ViewportWidth + 1, name);
        foreach (Button button in new[] { guide.TryNowButton, guide.BackButton, guide.SkipButton, guide.SkipStepButton }.Where(b => b.IsVisible))
        {
            Rect bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= content.ActualHeight + 1, name);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(DiagnosticOutputPaths.Child(root, name + ".png")); encoder.Save(stream);
    }
}
