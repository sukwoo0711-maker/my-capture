using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class FirstPinLayoutTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ko-KR", AppTheme.Glass, 1)]
    [InlineData("ko-KR", AppTheme.GlassLight, 1)]
    [InlineData("en-US", AppTheme.Glass, 1)]
    [InlineData("en-US", AppTheme.GlassLight, 1)]
    [InlineData("ko-KR", AppTheme.Glass, 1.5)]
    [InlineData("ko-KR", AppTheme.GlassLight, 1.5)]
    [InlineData("en-US", AppTheme.Glass, 1.5)]
    [InlineData("en-US", AppTheme.GlassLight, 1.5)]
    public void ShortcutNoticeFitsItsContentWithReadableHierarchyAndOneWindowTitle(string language, AppTheme theme, double textScale) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        WelcomeWindow guide = Create(theme, new WelcomeOptions
        {
            FirstPinExperience = true, CoreShortcutsNotice = true, TextScale = textScale,
        });
        try
        {
            guide.Show();
            Drain(guide);
            Assert.Equal(WelcomeStep.Consent, guide.CurrentStep);
            Assert.Equal(SizeToContent.Height, guide.SizeToContent);
            Assert.Equal(0, guide.MinHeight);
            Assert.True(guide.ActualHeight <= guide.MaxHeight);
            if (textScale == 1) Assert.True(guide.ActualHeight < WelcomeWindow.DefaultHeight);
            Assert.Equal("MyCapture — " + UiText.Get("FirstRun.WindowTitle"), guide.Title);
            Assert.Equal(1, guide.Title.Split("MyCapture", StringSplitOptions.None).Length - 1);
            Assert.Equal(FontWeights.Normal, guide.InstructionBlock.FontWeight);
            Assert.Equal(FontWeights.Normal, guide.CoreShortcutParagraphs.FontWeight);
            TextBlock title = Assert.Single(FirstRunWelcomeTests.Descendants(guide).OfType<TextBlock>(),
                text => KoreanWordWrap.Strip(text.Text) == guide.TitleText);
            Assert.Equal(FontWeights.SemiBold, title.FontWeight);
            Assert.True(title.FontSize > guide.InstructionBlock.FontSize);
            AssertRenderedTypography(guide.InstructionBlock, FontWeights.Normal, TypeScale.Body * textScale);
            AssertRenderedTypography(guide.CoreShortcutParagraphs, FontWeights.Normal, TypeScale.Body * textScale);
            AssertRenderedTypography(title, FontWeights.SemiBold, TypeScale.Subtitle * textScale);
            foreach (TextBlock row in guide.CoreShortcutKeyList.Children.OfType<TextBlock>())
                AssertRenderedTypography(row, Grid.GetColumn(row) == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    TypeScale.BodyLarge * textScale);
            Assert.Same(guide.FindResource("Surface.Base"), guide.Background);
            Assert.False(guide.PracticeTableShown || guide.PreviewShown || guide.PreferencesShown);
            Assert.True(guide.TryNowButton.IsVisible && guide.TryNowButton.IsDefault);
            Assert.True(guide.SkipButton.IsVisible);
            AssertInside(guide, guide.TryNowButton);
            AssertInside(guide, guide.SkipButton);
            Assert.Equal(0, guide.Scroller.HorizontalOffset);
            Assert.True(guide.Scroller.ExtentWidth <= guide.Scroller.ViewportWidth + .5);
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Drain(guide);
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
            Assert.True(guide.IsFirstPinExperience);
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            Assert.Equal(SizeToContent.Height, guide.SizeToContent);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", 96u)]
    [InlineData("en-US", 96u)]
    [InlineData("ko-KR", 144u)]
    [InlineData("en-US", 144u)]
    public void ACompactFirstPinWindowKeepsItsActionsReachableOnNarrowWorkAreas(string language, uint dpi) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var monitor = new MonitorInfo("narrow-first-pin", new RectD(-10000, -10000, 900, 800),
            new RectD(-10000, -10000, 900, 760), dpi, true);
        WelcomeWindow guide = Create(AppTheme.GlassLight, new WelcomeOptions
        {
            FirstPinExperience = true, TextScale = 1.5, MonitorProvider = () => monitor,
        });
        using var tracking = new WindowTrackSizeScope(guide, new Size(900, 800));
        try
        {
            guide.LimitToWorkArea(monitor);
            guide.Show();
            Drain(guide);
            Assert.True(guide.ActualWidth <= monitor.WorkArea.Width / monitor.ScaleFactor);
            Assert.True(guide.ActualHeight <= monitor.WorkArea.Height / monitor.ScaleFactor);
            AssertInside(guide, guide.TryNowButton);
            AssertInside(guide, guide.SkipButton);
            Assert.True(guide.TryNowButton.IsTabStop && guide.SkipButton.IsTabStop && guide.SampleTextButton.IsTabStop);
            Assert.Equal(UiText.Get("FirstRun.PinClipboard"), AutomationProperties.GetName(guide.TryNowButton));
            Assert.Equal(UiText.Get("FirstRun.PinClipboard"), KoreanWordWrap.Strip(SurfaceShortcutHintTests.PrimaryCaption(guide.TryNowButton)));
            Assert.Equal(UiText.Get("FirstRun.CopySample"), AutomationProperties.GetName(guide.SampleTextButton));
            Assert.Equal(UiText.Get("FirstRun.CopySample"), KoreanWordWrap.Strip(SurfaceShortcutHintTests.PrimaryCaption(guide.SampleTextButton)));
            Assert.True(KeyboardNavigation.GetTabIndex(guide.Scroller)
                < KeyboardNavigation.GetTabIndex(LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(guide.TryNowButton))));
            Assert.True(guide.Scroller.ExtentWidth <= guide.Scroller.ViewportWidth + .5);
            PracticeJourneyDriver.Click(guide.SkipButton);
            Drain(guide);
            AssertInside(guide, guide.TryNowButton);
            AssertInside(guide, guide.FullPracticeButton);
            Assert.Equal(FirstPinState.Skipped, guide.FirstPin.State);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData(false, 96u)]
    [InlineData(true, 96u)]
    [InlineData(false, 144u)]
    [InlineData(true, 144u)]
    public void OpeningFullPracticeRecentersTheGrowingWindowInsideAShortWorkArea(bool notice, uint dpi) => StaTestHost.Run(() =>
    {
        var monitor = new MonitorInfo("short-first-pin", new RectD(-10000, -10000, 1366, 800),
            new RectD(-10000, -10000, 1366, 760), dpi, true);
        WelcomeWindow guide = Create(AppTheme.GlassLight, new WelcomeOptions
        {
            FirstPinExperience = true, CoreShortcutsNotice = notice, MonitorProvider = () => monitor,
        });
        guide.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using var tracking = new WindowTrackSizeScope(guide, new Size(1366, 800));
        try
        {
            guide.Show();
            Drain(guide);
            AssertFitsWorkArea();
            if (notice) PracticeJourneyDriver.Click(guide.TryNowButton);
            PracticeJourneyDriver.Click(guide.SkipButton);
            Drain(guide);
            double compactHeight = guide.ActualHeight;
            PracticeJourneyDriver.Click(guide.FullPracticeButton);
            Drain(guide);
            Assert.Equal(PracticeStep.Groups, guide.Session.Step);
            Assert.True(guide.ActualHeight > compactHeight);
            AssertFitsWorkArea();
            AssertInside(guide, guide.TryNowButton);
            AssertInside(guide, guide.SkipButton);
        }
        finally { guide.Close(); }

        void AssertFitsWorkArea()
        {
            var (left, top, right, bottom) = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(guide).Handle);
            Assert.InRange((double)left, monitor.WorkArea.Left, monitor.WorkArea.Right);
            Assert.InRange((double)right, monitor.WorkArea.Left, monitor.WorkArea.Right);
            Assert.InRange((double)top, monitor.WorkArea.Top, monitor.WorkArea.Bottom);
            Assert.InRange((double)bottom, monitor.WorkArea.Top, monitor.WorkArea.Bottom);
            Point primaryBottom = guide.TryNowButton.PointToScreen(new Point(guide.TryNowButton.ActualWidth, guide.TryNowButton.ActualHeight));
            Assert.InRange(primaryBottom.X, monitor.WorkArea.Left, monitor.WorkArea.Right);
            Assert.InRange(primaryBottom.Y, monitor.WorkArea.Top, monitor.WorkArea.Bottom);
        }
    });

    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void FullPracticeReadsMaterialAndInstructionBeforeAnExplicitlyRequestedVideo(AppTheme theme) => StaTestHost.Run(() =>
    {
        var media = new FakeTutorialMedia();
        WelcomeWindow guide = Create(theme, new WelcomeOptions { MediaFactory = () => media, ReducedMotion = false });
        try
        {
            guide.Show();
            Drain(guide);
            WelcomeTutorialPreview preview = Assert.Single(FirstRunWelcomeTests.Descendants(guide).OfType<WelcomeTutorialPreview>());
            Assert.True(preview.ShowingInstructions);
            Assert.Empty(media.OpenedPaths);
            Assert.Equal(Visibility.Collapsed, guide.PauseButton.Visibility);
            Assert.True(guide.ReplayButton.IsVisible);
            Assert.Equal(UiText.Get("FirstRun.WatchExample"), AutomationProperties.GetName(guide.ReplayButton));
            Assert.True(guide.InstructionBlock.TranslatePoint(new Point(), guide).Y
                < guide.PreviewFrame.TranslatePoint(new Point(), guide).Y);
            Image material = Assert.Single(guide.MotionParts.OfType<Image>());
            Assert.True(material.IsVisible);
            Assert.True(material.TranslatePoint(new Point(), guide).Y < guide.PreviewFrame.TranslatePoint(new Point(), guide).Y);
            Border surface = Assert.IsType<Border>(preview.Children[0]);
            Assert.Same(guide.FindResource("Surface.Raised"), surface.Background);
            PracticeJourneyDriver.Click(guide.ReplayButton);
            Drain(guide);
            Assert.False(preview.ShowingInstructions);
            Assert.Single(media.OpenedPaths);
            Assert.True(media.IsPlaying);
            Assert.Equal(Visibility.Visible, guide.PauseButton.Visibility);
            PracticeJourneyDriver.Click(guide.PauseButton);
            Assert.False(media.IsPlaying);
            guide.Session.StartGroup(PracticeStep.B0);
            Drain(guide);
            Assert.True(preview.ShowingInstructions);
            Assert.False(media.IsOpen);
            Assert.Equal(Visibility.Collapsed, guide.PauseButton.Visibility);
        }
        finally { guide.Close(); }
    });

    private static WelcomeWindow Create(AppTheme theme, WelcomeOptions options)
    {
        var guide = OnboardingGuideTests.Create(new(), options);
        guide.Resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, guide.Resources, systemHighContrast: false);
        StandardWindowTheme.Apply(guide);
        return guide;
    }

    private void AssertRenderedTypography(TextBlock text, FontWeight weight, double size)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(text.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(text.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(text);
        GlyphRun[] runs = Glyphs(VisualTreeHelper.GetDrawing(text)).ToArray();
        output.WriteLine($"Text={KoreanWordWrap.Strip(text.Text)}; effective font={text.FontFamily.Source}; weight={text.FontWeight}; size={text.FontSize}; formatting={TextOptions.GetTextFormattingMode(text)}; rendering={TextOptions.GetTextRenderingMode(text)}");
        foreach (GlyphRun run in runs)
            output.WriteLine($"Resolved font={run.GlyphTypeface.FontUri}; weight={run.GlyphTypeface.Weight}; simulation={run.GlyphTypeface.StyleSimulations}; em={run.FontRenderingEmSize}");
        Assert.Equal(ThemeFonts.Ui.Source, text.FontFamily.Source);
        Assert.Equal(weight, text.FontWeight);
        Assert.Equal(size, text.FontSize);
        Assert.Equal(ThemeFonts.Formatting, TextOptions.GetTextFormattingMode(text));
        Assert.Equal(ThemeFonts.Rendering, TextOptions.GetTextRenderingMode(text));
        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.Equal(weight, run.GlyphTypeface.Weight));

        static IEnumerable<GlyphRun> Glyphs(Drawing? drawing)
        {
            if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
            if (drawing is DrawingGroup group)
                foreach (Drawing child in group.Children)
                    foreach (GlyphRun run in Glyphs(child)) yield return run;
        }
    }

    private static void AssertInside(WelcomeWindow guide, FrameworkElement element)
    {
        Rect bounds = element.TransformToAncestor(guide).TransformBounds(new Rect(element.RenderSize));
        Assert.InRange(bounds.Left, 0, guide.ActualWidth);
        Assert.InRange(bounds.Right, 0, guide.ActualWidth);
        Assert.InRange(bounds.Top, 0, guide.ActualHeight);
        Assert.InRange(bounds.Bottom, 0, guide.ActualHeight);
    }

    private static void Drain(WelcomeWindow guide) => guide.Dispatcher.Invoke(guide.UpdateLayout, DispatcherPriority.ApplicationIdle);
}
