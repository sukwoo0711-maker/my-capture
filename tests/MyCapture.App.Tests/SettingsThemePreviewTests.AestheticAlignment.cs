using System.IO;
using System.Windows;
using System.Windows.Automation;
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
/// finish step's shortcut link sits on the start-at-sign-in label's line; the settings guide's
/// before/after panels take the card radius; every guide button centres on the row it explains; and
/// a shortcut help search result keeps its category under its name beside a tutorial button.
/// </summary>
/// <remarks>
/// With <c>MYCAPTURE_AES_SO_FIX_EVIDENCE</c> set to a folder, each check first writes the area it
/// measures as PNG at 96 and 144 dpi (and the guide rows' offsets as TSV), prefixed by
/// <c>MYCAPTURE_AES_SO_FIX_PHASE</c>.
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

    [Fact]
    public Task AestheticAlignment_GuideButtonsSitOnTheirRowLine() =>
        RunIsolated(nameof(AestheticAlignment_GuideButtonsSitOnTheirRowLine), () =>
        {
            // Both languages are measured before failing, so one run names every row that is off.
            var misaligned = new List<string>();
            InSettingsWindows((window, tabs, locale) => misaligned.AddRange(MisalignedGuideRows(window, tabs, locale)));
            if (misaligned.Count > 0)
                Assert.Fail($"Guide buttons off their row's line (DIP below): {string.Join("; ", misaligned)}");
        });

    /// <summary>
    /// The same rows where layout rounds to the pixels of a 125% and a 150% display. Pinned to its row's
    /// top, the storage tab's guide button sat above the middle of its two-line English checkbox by half
    /// the difference of their heights: 0.5 DIP at 100%, inside the limit, and 0.8 DIP at 125% (issue 280).
    /// </summary>
    [Fact]
    public Task AestheticAlignment_GuideButtonsSitOnTheirRowLineAtOtherScales() =>
        RunIsolated(nameof(AestheticAlignment_GuideButtonsSitOnTheirRowLineAtOtherScales), () =>
        {
            var misaligned = new List<string>();
            InSettingsWindows((window, tabs, locale) =>
            {
                foreach (double scale in new[] { 1.25, 1.5 })
                    misaligned.AddRange(MisalignedGuideRows(window, tabs, locale, scale));
            });
            if (misaligned.Count > 0)
                Assert.Fail($"Guide buttons off their row's line (DIP below): {string.Join("; ", misaligned)}");
        });

    [Fact]
    public Task AestheticAlignment_HelpResultsKeepTheCategoryUnderTheName() =>
        RunIsolated(nameof(AestheticAlignment_HelpResultsKeepTheCategoryUnderTheName), VerifyHelpResultRows);

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

    // A help search result keeps its category 1 under its name whether or not it has a tutorial
    // button: the 36-high button stands beside both lines instead of on the name's line.
    private static void VerifyHelpResultRows()
    {
        Application application = CreateThemedApplication();
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings();
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            ThemeService.Apply(AppTheme.Glass);
            var help = new ShortcutHelpWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            };
            FluidMotion.SetWindowEntrance(help, false);
            try
            {
                help.Show();
                Drain(help);
                ShortcutHelpSearchTests.HelpView view = ShortcutHelpSearchTests.View(help);
                int withTutorial = 0, withoutTutorial = 0;
                foreach (string query in new[] { "F2", "Ctrl+C", UiText.Get("Help.Video") })
                {
                    view.Search.Text = query;
                    Drain(help);
                    var gaps = new List<double>();
                    var tutorials = new List<(Rect Button, double TextRight)>();
                    foreach (Border row in view.VisibleResults())
                    {
                        TextBlock[] texts = Descendants(row).OfType<TextBlock>().ToArray();
                        Rect name = BoundsIn(texts[0], help), category = BoundsIn(texts[1], help);
                        gaps.Add(category.Top - name.Bottom);
                        Button? tutorial = Descendants(row).OfType<Button>().SingleOrDefault();
                        if (tutorial is null) { withoutTutorial++; continue; }
                        withTutorial++;
                        tutorials.Add((BoundsIn(tutorial, help), Math.Max(name.Right, category.Right)));
                    }
                    SaveAlignmentEvidence(help, "help-results-" + (query == "F2" ? "f2" : query == "Ctrl+C" ? "ctrl-c" : "video"),
                        BoundsIn(view.ResultsView, help));
                    Assert.True(gaps.Count > 1, $"{query}: expected several results.");
                    // Every row keeps the category's 1 DIP margin under the name, not merely the same gap.
                    Assert.True(gaps.All(gap => Math.Abs(gap - 1) <= 0.5),
                        $"{query}: the gaps between a result's name and its category are {string.Join(", ", gaps.Select(gap => gap.ToString("0.##")))}; each must be 1.");
                    // The tutorial button stands beside the name and the category, over neither.
                    foreach ((Rect button, double textRight) in tutorials)
                        Assert.True(button.Left >= textRight - 0.5,
                            $"{query}: the tutorial button at {button.Left:0.#} starts before the result's text ends at {textRight:0.#}.");
                }
                Assert.True(withTutorial > 0 && withoutTutorial > 0,
                    $"The queries must cover rows with ({withTutorial}) and without ({withoutTutorial}) a tutorial button.");
            }
            finally
            {
                help.Close();
            }
        }
        finally
        {
            ShortcutHelpWindow.ReadHotkeys = previous;
            application.Shutdown();
        }
    }

    // A guide or compare button at the end of a row centres on the checkbox, label, list or field
    // it explains, not on that row's spacing below. Every tab is measured with its folds open.
    // With layoutScale, each tab is laid out again as a display at that scale would lay it out.
    private static List<string> MisalignedGuideRows(SettingsWindow window, TabControl tabs, string locale, double? layoutScale = null)
    {
        var guideStyle = (Style)window.FindResource("Settings.GuideButton");
        string evidenceSuffix = layoutScale is { } percent ? $"-{percent * 100:0}" : string.Empty;
        var table = new List<string> { "tab\tguide\tpartner\ttext\tline\tbutton\toffset\tpartnerHeight\tbuttonHeight\tscale" };
        var misaligned = new List<string>();
        var recordingTags = new List<string>();
        foreach (TabItem tab in tabs.Items.OfType<TabItem>())
        {
            tabs.SelectedItem = tab;
            Drain(window);
            foreach (Expander fold in Descendants(window).OfType<Expander>()) fold.IsExpanded = true;
            Drain(window);
            if (layoutScale is { } rounding) LayOutAtScale(window, rounding);
            Button[] buttons = Descendants(window).OfType<Button>()
                .Where(button => button.IsVisible && IsBasedOn(button.Style, guideStyle)).ToArray();
            if (buttons.Length == 0) continue;
            buttons[0].BringIntoView();
            Drain(window);
            Rect viewport = tab.Content is ScrollViewer scroll ? BoundsIn(scroll, window) : new Rect(window.RenderSize);
            Rect evidence = Rect.Empty;
            foreach (Button button in buttons)
            {
                var row = Assert.IsType<Grid>(button.Parent);
                FrameworkElement partner = row.Children.OfType<FrameworkElement>().Single(child =>
                    !ReferenceEquals(child, button) && Grid.GetColumn(child) == 0 && Grid.GetRow(child) == Grid.GetRow(button));
                Rect buttonBounds = BoundsIn(button, window);
                double line = LineCentre(partner, window), centre = VerticalCentre(buttonBounds), offset = centre - line;
                string text = RowText(partner);
                // The scale the row is laid out at, and both heights: a button pinned to its row's top is
                // off its partner's centre by half their difference.
                double scale = VisualTreeHelper.GetDpi(button).DpiScaleY;
                table.Add($"{tab.Name}\t{button.Tag}\t{partner.GetType().Name}\t{text}\t{line:0.##}\t{centre:0.##}\t{offset:0.##}"
                    + $"\t{partner.ActualHeight:0.##}\t{button.ActualHeight:0.##}\t{scale:0.##}");
                if (Math.Abs(offset) > 0.501)
                    misaligned.Add($"{locale} {tab.Name} {button.Tag} '{text}' {offset:0.##}"
                        + $" (partner {partner.ActualHeight:0.##} high, button {button.ActualHeight:0.##} high, at {scale:0.##}x)");
                if (tab.Name == "RecordingTab" && button.Tag is string tag) recordingTags.Add(tag);
                Rect area = Rect.Union(buttonBounds, BoundsIn(partner, window));
                if (viewport.Contains(area)) evidence.Union(area);
            }
            if (!evidence.IsEmpty) SaveAlignmentEvidence(window, $"guide-rows-{tab.Name}-{locale}{evidenceSuffix}", evidence);
        }
        SaveAlignmentTable($"guide-rows-{locale}{evidenceSuffix}", table);
        // The rows the 11-06 review found: both pointer checkboxes and the recording size label.
        Assert.Equal(new[] { "G05", "G05", "G04" }, recordingTags.Where(tag => tag is "G05" or "G04").ToArray());
        return misaligned;
    }

    // Lays the window out as a display at this scale lays it out: sizes and offsets round to that
    // display's pixels. The window stays on its own display; only WPF's layout scale changes. Every
    // element is laid out again, because one that kept its size would keep an offset rounded at the
    // previous scale (see CaptureFlowPolishTests.AtScale).
    private static void LayOutAtScale(Window window, double scale)
    {
        VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
        foreach (UIElement element in Descendants(window).OfType<UIElement>().Prepend(window))
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
        }
        Drain(window);
        Assert.Equal(scale, VisualTreeHelper.GetDpi(window).DpiScaleY);
    }

    // The middle of the line a row button belongs to: a checkbox or list by its box, a label or a
    // field by its text. A label stretched over a taller row draws its text at the row's top.
    private static double LineCentre(FrameworkElement partner, Window window)
    {
        if (partner is SettingsField field) partner = Assert.IsType<TextBlock>(field.Template.FindName("FieldLabel", field));
        Rect bounds = BoundsIn(partner, window);
        return partner is TextBlock text
            ? bounds.Top + (text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom) / 2
            : VerticalCentre(bounds);
    }

    private static string RowText(FrameworkElement partner) => KoreanWordWrap.Strip(partner switch
    {
        SettingsField field => field.Header as string ?? string.Empty,
        TextBlock text => text.Text,
        CheckBox box => box.Content as string ?? string.Empty,
        _ => AutomationProperties.GetName(partner),
    });

    private static bool IsBasedOn(Style? style, Style basis)
    {
        for (; style is not null; style = style.BasedOn)
            if (ReferenceEquals(style, basis)) return true;
        return false;
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

    private static void SaveAlignmentTable(string name, IEnumerable<string> lines)
    {
        if (EvidencePath($"{name}.tsv") is { } path) File.WriteAllLines(path, lines);
    }

    private static string? EvidencePath(string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_AES_SO_FIX_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return null;
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_AES_SO_FIX_PHASE") is { Length: > 0 } label ? label : "after";
        return DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(requested), $"{phase}-{name}");
    }

    private static void SaveAlignmentEvidence(Window window, string name, Rect area)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_AES_SO_FIX_EVIDENCE"))) return;
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
            using FileStream stream = File.Create(EvidencePath($"{name}-{dpi}dpi.png")!);
            encoder.Save(stream);
        }
    }
}
