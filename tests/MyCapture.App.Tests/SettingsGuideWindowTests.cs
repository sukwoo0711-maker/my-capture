using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsGuideWindowTests
{
    [Fact]
    public Task OpensFromApiAndCloseReturnsOwnersLogicalFocus() => Isolated(nameof(OpensFromApiAndCloseReturnsOwnersLogicalFocus), () =>
    {
        var opener = new Button { Content = "Guide" };
        var owner = new Window { Content = opener, Width = 300, Height = 160 };
        Place(owner); owner.Show();
        FocusManager.SetFocusedElement(owner, opener);
        SettingsGuide.BeforeShowForTest = Place;
        try
        {
            var guide = SettingsGuide.Show("G02", owner);
            Assert.True(guide.IsVisible); Assert.Same(owner, guide.Owner); Assert.True(guide.HasBothImages);
            Assert.False(guide.IsPlaying);
            guide.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(guide.IsVisible);
            Assert.Same(opener, FocusManager.GetFocusedElement(owner));
        }
        finally { SettingsGuide.BeforeShowForTest = null; owner.Close(); }
    });

    [Fact]
    public Task EscapeClosesAndOwnerCloseStopsPlayback() => Isolated(nameof(EscapeClosesAndOwnerCloseStopsPlayback), () =>
    {
        var owner = new Window { Width = 300, Height = 160 }; Place(owner); owner.Show();
        var guide = new SettingsGuideWindow(SettingsGuideCatalog.Get("G04"), reducedMotion: () => false);
        guide.AttachOwner(owner); Place(guide); guide.Show();
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(guide)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        guide.RaiseEvent(key);
        Assert.True(key.Handled); Assert.False(guide.IsVisible);
        var second = new SettingsGuideWindow(SettingsGuideCatalog.Get("G05"), reducedMotion: () => false);
        second.AttachOwner(owner); Place(second); second.Show(); second.Replay();
        Assert.True(second.IsPlaying);
        owner.Close(); Assert.False(second.IsVisible); Assert.False(second.IsPlaying);
    });

    [Fact]
    public Task ReplayRunsOnceAndReducedMotionUsesTwoStillImages() => Isolated(nameof(ReplayRunsOnceAndReducedMotionUsesTwoStillImages), () =>
    {
        bool reduced = false;
        var guide = new SettingsGuideWindow(SettingsGuideCatalog.Get("G05"), reducedMotion: () => reduced);
        Place(guide); guide.Show();
        try
        {
            Assert.True(guide.HasBothImages); Assert.False(guide.IsPlaying);
            Assert.Equal(Visibility.Visible, guide.ReplayButton.Visibility);
            guide.Replay(); Assert.Equal(0, guide.Frame); Assert.True(guide.IsPlaying);
            for (int i = 0; i < 10; i++) guide.AdvanceFrame();
            Assert.False(guide.IsPlaying); Assert.Equal(3, guide.Frame);
            guide.Replay(); reduced = true; guide.RefreshMotionPreference();
            Assert.False(guide.IsPlaying); Assert.True(guide.HasBothImages);
            Assert.Equal(Visibility.Collapsed, guide.ReplayButton.Visibility);
            guide.Replay(); Assert.False(guide.IsPlaying);
            reduced = false; guide.ComparisonSelector.SelectedIndex = 1;
            Assert.Equal(Visibility.Collapsed, guide.ReplayButton.Visibility);
            Assert.Equal("G05.hidden", guide.Comparison.Id);
            Assert.True(guide.HasBothImages);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(guide));
            Assert.True(guide.CloseButton.IsCancel);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(guide.ComparisonSelector)));
        }
        finally { guide.Close(); }
    });

    [Fact]
    public Task MissingPicturesKeepCloseAvailableAndDisablePlayback() => Isolated(nameof(MissingPicturesKeepCloseAvailableAndDisablePlayback), () =>
    {
        var guide = new SettingsGuideWindow(SettingsGuideCatalog.Get("G04"), Path.Combine(AppContext.BaseDirectory, "absent-guide-fixture"), () => false);
        Place(guide); guide.Show();
        Assert.False(guide.HasBothImages); Assert.False(guide.ReplayButton.IsEnabled);
        guide.Replay(); Assert.False(guide.IsPlaying);
        guide.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(guide.IsVisible);
    });

    [Fact]
    public Task RenderAllGuidesInBothLanguagesAndSmallWindow() => Isolated(nameof(RenderAllGuidesInBothLanguagesAndSmallWindow), () =>
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_EVIDENCE");
        if (output is not null) output = DiagnosticOutputPaths.Create(output);
        foreach (string language in new[] { "ko", "en" })
        {
            using var culture = UiText.UseLanguage(language);
            foreach (SettingsGuideDefinition definition in SettingsGuideCatalog.All)
            {
                var guide = new SettingsGuideWindow(definition, reducedMotion: () => true);
                Place(guide); guide.Show();
                try
                {
                    Drain(guide);
                    Assert.Equal("MyCapture — " + definition.Title, guide.Title);
                    Assert.Equal(language == "ko" ? "닫기" : "Close", guide.CloseButton.Content);
                    Assert.Equal(Visibility.Collapsed, guide.ReplayButton.Visibility);
                    if (output is not null)
                        SettingsGuideTrayEvidenceTests.Save((FrameworkElement)guide.Content, output, $"guide-{definition.Id}-{language}.png");
                    foreach (var comparison in definition.Comparisons)
                    {
                        guide.ComparisonSelector.SelectedItem = comparison; Drain(guide);
                        if (definition.Comparisons.Count > 1)
                            Assert.Contains(Descendants(guide.ComparisonSelector).OfType<TextBlock>(), text => text.Text == comparison.Title);
                        if (definition.Id is "G06" or "G09")
                            foreach (var image in Descendants(guide.Scroller).OfType<Image>())
                            {
                                var source = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                                double scale = Math.Min(image.ActualWidth / source.PixelWidth, image.ActualHeight / source.PixelHeight);
                                Assert.True(17.5 * scale >= 13, $"{comparison.Id}: displayed body text is below 13 px.");
                            }
                        if (output is not null && definition.Comparisons.Count > 1)
                            SettingsGuideTrayEvidenceTests.Save((FrameworkElement)guide.Content, output, $"guide-{comparison.Id}-{language}.png");
                    }
                    guide.Width = 540; guide.Height = 400; Drain(guide);
                    Assert.True(guide.CloseButton.IsVisible);
                    Rect close = guide.CloseButton.TransformToAncestor(guide).TransformBounds(new Rect(guide.CloseButton.RenderSize));
                    Assert.True(close.Left >= 0 && close.Right <= guide.ActualWidth && close.Bottom <= guide.ActualHeight);
                    Assert.True(guide.Scroller.ExtentHeight > guide.Scroller.ViewportHeight);
                    // Content may be narrower than the viewport; only positive overflow can clip it.
                    Assert.True(guide.Scroller.ExtentWidth <= guide.Scroller.ViewportWidth + 1);
                    if (output is not null && definition.Id == "G02")
                        SettingsGuideTrayEvidenceTests.Save((FrameworkElement)guide.Content, output, $"guide-G02-small-{language}.png");
                }
                finally { guide.Close(); }
            }
        }
        if (output is not null) SaveContactSheet(output);
    });

    private static void SaveContactSheet(string output)
    {
        var cells = SettingsGuideCatalog.All.Select(guide => (guide.Id == "G09" ? "guide-G09.accurate-ko.png" : $"guide-{guide.Id}-ko.png", $"{guide.Id} · 한국어"))
            .Concat([("guide-G06-en.png", "G06 · English"), ("guide-G09.accurate-en.png", "G09 · English · Accurate")]).ToArray();
        const int columns = 3, cellWidth = 512, cellHeight = 306, gap = 16, labelHeight = 28;
        int width = gap + columns * (cellWidth + gap), height = gap + ((cells.Length + columns - 1) / columns) * (cellHeight + labelHeight + gap);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < cells.Length; i++)
            {
                double x = gap + i % columns * (cellWidth + gap), y = gap + i / columns * (cellHeight + labelHeight + gap);
                dc.DrawText(new FormattedText(cells[i].Item2, UiText.Culture, FlowDirection.LeftToRight,
                    new Typeface("Malgun Gothic"), 16, Brushes.Black, 1), new Point(x, y));
                using var stream = File.OpenRead(Path.Combine(output, cells[i].Item1));
                var source = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                dc.DrawImage(source, new Rect(x, y + labelHeight, cellWidth, cellHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        SettingsGuideAssetTests.Write(bitmap, Path.Combine(output, "guides-contact-sheet-r3.png"));
    }

    private static Task Isolated(string name, Action body) => SettingsThemePreviewTests.RunIsolated(name, () =>
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = SettingsGuideAssetTests.Resources() };
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest; FluidMotion.AnimationsEnabledOverrideForTest = false;
        using var language = UiText.UseLanguage("ko");
        try { body(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; app.Shutdown(); }
    }, typeof(SettingsGuideWindowTests));
    private static void Place(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000; window.ShowActivated = false; window.ShowInTaskbar = false;
        FluidMotion.SetWindowEntrance(window, false);
    }
    private static void Drain(FrameworkElement element) => element.Dispatcher.Invoke(element.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
