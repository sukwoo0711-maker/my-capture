using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Settings and first-use surfaces line up what sits together (2026-11-06 aesthetic review): the
/// capture guide colour swatch takes the combo box's corner radius and the 8 spacing step beside
/// it; the font label sits on the font list's line, not between the list and its reset button; the
/// finish step's shortcut link sits on the start-at-sign-in label's line; and the settings guide's
/// before/after panels take the card radius.
/// </summary>
/// <remarks>
/// With <c>MYCAPTURE_AES_SO_FIX_EVIDENCE</c> set to a folder, each check first writes the area it
/// measures as PNG at 96 and 144 dpi, prefixed by <c>MYCAPTURE_AES_SO_FIX_PHASE</c>.
/// </remarks>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task AestheticAlignment_GuideSwatchFollowsTheColourChoice() =>
        RunIsolated(nameof(AestheticAlignment_GuideSwatchFollowsTheColourChoice), () => InSettingsWindows(VerifyGuideSwatch));

    [Fact]
    public Task AestheticAlignment_FontLabelSitsOnTheFontListLine() =>
        RunIsolated(nameof(AestheticAlignment_FontLabelSitsOnTheFontListLine), () => InSettingsWindows(VerifyFontField));

    [Fact]
    public Task AestheticAlignment_FinishLinkSitsOnTheCheckBoxLabelLine() =>
        RunIsolated(nameof(AestheticAlignment_FinishLinkSitsOnTheCheckBoxLabelLine), VerifyFinishLinkLine);

    [Fact]
    public Task AestheticAlignment_SettingsGuidePanelsTakeTheCardRadius() =>
        RunIsolated(nameof(AestheticAlignment_SettingsGuidePanelsTakeTheCardRadius), VerifyGuidePanelRadius);

    private static void InSettingsWindows(Action<SettingsWindow, TabControl, string> verify)
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                SettingsWindow window = CreateSettingsUxWindow(new AppSettings { General = { Theme = AppThemeNames.Glass } }).Window;
                try
                {
                    window.Show();
                    verify(window, Assert.IsType<TabControl>(window.FindName("CategoryTabs")), locale);
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    // The swatch stands beside the colour choice like a second field of one row.
    private static void VerifyGuideSwatch(SettingsWindow window, TabControl tabs, string locale)
    {
        tabs.SelectedItem = window.FindName("CaptureTab");
        Drain(window);
        var swatch = Assert.IsType<Border>(window.FindName("CaptureGuideColorSwatch"));
        var mode = Assert.IsType<ComboBox>(window.FindName("CaptureGuideColorModeSelector"));
        swatch.BringIntoView();
        Drain(window);
        Rect swatchBounds = BoundsIn(swatch, window), modeBounds = BoundsIn(mode, window);
        SaveAlignmentEvidence(window, $"guide-swatch-{locale}", Rect.Union(modeBounds, swatchBounds));
        Assert.Equal((CornerRadius)window.FindResource("Radius.Sm"), swatch.CornerRadius);
        Assert.Equal(8, swatchBounds.Left - modeBounds.Right, 0.5);
        Assert.Equal(VerticalCentre(modeBounds), VerticalCentre(swatchBounds), 1.0);
    }

    // The font label names the font list, so it sits on the list's line; the reset button stays in
    // the input column under the list, and the note under both.
    private static void VerifyFontField(SettingsWindow window, TabControl tabs, string locale)
    {
        tabs.SelectedItem = window.FindName("EditingTab");
        Drain(window);
        var fonts = Assert.IsType<ComboBox>(window.FindName("FontFamilyBox"));
        var reset = Assert.IsType<Button>(window.FindName("DefaultFontButton"));
        // Korean word wrapping puts joiners into the header and note text.
        SettingsField field = Descendants(window).OfType<SettingsField>()
            .Single(item => item.Header is string header && KoreanWordWrap.Strip(header) == UiText.Get("Settings_FontName"));
        var label = Assert.IsType<TextBlock>(field.Template.FindName("FieldLabel", field));
        TextBlock note = Descendants(window).OfType<TextBlock>()
            .Single(text => text.IsVisible && KoreanWordWrap.Strip(text.Text) == UiText.Get("Settings_FontScope"));
        fonts.BringIntoView();
        Drain(window);
        Rect labelBounds = BoundsIn(label, window), fontsBounds = BoundsIn(fonts, window);
        Rect resetBounds = BoundsIn(reset, window), noteBounds = BoundsIn(note, window);
        SaveAlignmentEvidence(window, $"font-field-{locale}", Rect.Union(Rect.Union(labelBounds, resetBounds), noteBounds));
        Assert.True(Math.Abs(VerticalCentre(labelBounds) - VerticalCentre(fontsBounds)) <= 1,
            $"{locale}: the font label's centre is {VerticalCentre(labelBounds) - VerticalCentre(fontsBounds):0.#} from the font list's.");
        Assert.Equal(fontsBounds.Left, resetBounds.Left, 0.5);
        Assert.Equal(6, resetBounds.Top - fontsBounds.Bottom, 0.5);
        Assert.Equal(labelBounds.Left, noteBounds.Left, 0.5);
        Assert.True(noteBounds.Top >= resetBounds.Bottom, $"{locale}: the font note starts above the reset button's bottom.");
    }

    private static void VerifyFinishLinkLine()
    {
        Application application = CreateThemedApplication();
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            ThemeService.Apply(AppTheme.Glass);
            WelcomeWindow guide = OnboardingGuideTests.Create(new OnboardingGuideTests.FakeClipboard(),
                new WelcomeOptions { ReducedMotion = true, TextScale = 1, HintDelay = TimeSpan.FromMinutes(5) });
            FluidMotion.SetWindowEntrance(guide, false);
            try
            {
                guide.Show();
                PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.Results);
                Drain(guide);
                guide.StartAtSignInCheck.BringIntoView();
                Drain(guide);
                TextBlock link = Descendants(guide).OfType<TextBlock>().Single(text => text.Inlines.Contains(guide.ShortcutsLink));
                TextBlock label = Descendants(guide.StartAtSignInCheck).OfType<TextBlock>()
                    .Single(text => KoreanWordWrap.Strip(text.Text) == UiText.Get("Onboard_StartAtSignIn"));
                SaveAlignmentEvidence(guide, "finish-link", Rect.Union(BoundsIn(label, guide), BoundsIn(link, guide)));
                // The 32-high line stays the link's click target.
                Assert.True(link.LineHeight >= 32 && link.ActualHeight >= 32, $"The link's line is {link.ActualHeight:0.#} high.");
                // Measure the glyphs, not the underline under the link.
                guide.ShortcutsLink.TextDecorations = null;
                Drain(guide);
                Rect labelBounds = BoundsIn(label, guide), linkBounds = BoundsIn(link, guide);
                // The label ends in Hangul (실행); its Latin descenders would lower its ink centre.
                var labelTail = new Rect(labelBounds.Right - 24, labelBounds.Top, 24, labelBounds.Height);
                foreach (int dpi in new[] { 96, 144 })
                {
                    double scale = dpi / 96d;
                    BitmapSource bitmap = RenderAtDpi(guide, dpi);
                    double labelCentre = InkCentre(bitmap, labelTail, scale), linkCentre = InkCentre(bitmap, linkBounds, scale);
                    Assert.True(Math.Abs(linkCentre - labelCentre) <= scale,
                        $"{dpi} dpi: the link's text centre is {linkCentre - labelCentre:0.#} px from the label's.");
                }
            }
            finally
            {
                guide.Close();
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void VerifyGuidePanelRadius()
    {
        Application application = CreateThemedApplication();
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            ThemeService.Apply(AppTheme.Glass);
            var guide = new SettingsGuideWindow(SettingsGuideCatalog.Get("G03"), reducedMotion: () => true)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            };
            FluidMotion.SetWindowEntrance(guide, false);
            try
            {
                guide.Show();
                Drain(guide);
                Border[] panels = Descendants(guide.Scroller).OfType<Image>().Select(PanelOf).Distinct().ToArray();
                Assert.Equal(2, panels.Length);
                SaveAlignmentEvidence(guide, "settings-guide-panels", Rect.Union(BoundsIn(panels[0], guide), BoundsIn(panels[1], guide)));
                Assert.All(panels, panel => Assert.Equal((CornerRadius)guide.FindResource("Radius.Lg"), panel.CornerRadius));
            }
            finally
            {
                guide.Close();
            }
        }
        finally
        {
            application.Shutdown();
        }

        static Border PanelOf(Image image)
        {
            DependencyObject? node = image;
            while ((node = VisualTreeHelper.GetParent(node)) is not null and not Border) { }
            return Assert.IsType<Border>(node);
        }
    }

    private static double VerticalCentre(Rect bounds) => bounds.Top + bounds.Height / 2;

    /// <summary>
    /// The middle of the rows that hold text ink inside <paramref name="area"/> (in DIP), in
    /// pixels of <paramref name="bitmap"/>. The commonest colour in the area is its background.
    /// </summary>
    private static double InkCentre(BitmapSource bitmap, Rect area, double scale)
    {
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int left = Math.Max(0, (int)Math.Floor(area.Left * scale)), top = Math.Max(0, (int)Math.Floor(area.Top * scale));
        int width = Math.Min(pixels.PixelWidth - left, (int)Math.Ceiling(area.Width * scale));
        int height = Math.Min(pixels.PixelHeight - top, (int)Math.Ceiling(area.Height * scale));
        var data = new byte[width * height * 4];
        pixels.CopyPixels(new Int32Rect(left, top, width, height), data, width * 4, 0);
        int background = Enumerable.Range(0, width * height).Select(index => BitConverter.ToInt32(data, index * 4))
            .GroupBy(color => color).OrderByDescending(group => group.Count()).First().Key;
        byte[] backdrop = BitConverter.GetBytes(background);
        int first = -1, last = -1;
        for (int y = 0; y < height; y++)
        {
            int ink = 0;
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                int difference = Math.Abs(data[offset] - backdrop[0]) + Math.Abs(data[offset + 1] - backdrop[1]) + Math.Abs(data[offset + 2] - backdrop[2]);
                if (difference > 60) ink++;
            }
            if (ink < 2) continue;
            if (first < 0) first = y;
            last = y;
        }
        Assert.True(first >= 0, $"No text ink in {area}.");
        return top + (first + last) / 2d;
    }

    private static void SaveAlignmentEvidence(Window window, string name, Rect area)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_AES_SO_FIX_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_AES_SO_FIX_PHASE") is { Length: > 0 } label ? label : "after";
        string directory = DiagnosticOutputPaths.Create(requested);
        area.Inflate(16, 16);
        area.Intersect(new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        foreach (int dpi in new[] { 96, 144 })
        {
            double scale = dpi / 96d;
            BitmapSource full = RenderAtDpi(window, dpi);
            int left = (int)Math.Floor(area.Left * scale), top = (int)Math.Floor(area.Top * scale);
            var crop = new CroppedBitmap(full, new Int32Rect(left, top,
                Math.Min(full.PixelWidth - left, (int)Math.Ceiling(area.Width * scale)),
                Math.Min(full.PixelHeight - top, (int)Math.Ceiling(area.Height * scale))));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(crop));
            using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, $"{phase}-{name}-{dpi}dpi.png"));
            encoder.Save(stream);
        }
    }
}
