using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Every pin kind and state lays its hover bar out by one rule, measured with the app's real theme.
/// Set MYCAPTURE_PINBAR_EVIDENCE to a folder to keep a PNG of each bar and a table of its buttons.
/// </summary>
public sealed class PinHoverBarLayoutTests
{
    private const string EvidenceVariable = "MYCAPTURE_PINBAR_EVIDENCE";

    /// <summary>A bar state a pin kind can be in: its source and its translation command.</summary>
    internal sealed record BarState(string Name, CaptureProvenance? Source, string? TranslationKey, bool TranslationEnabled);

    internal static IReadOnlyList<BarState> States { get; } =
    [
        // Picture pins (capture, clipboard, OCR, lightweight native): translation is offered once text is read.
        new("image", null, "Pin_Translate", true),
        new("image-app-source", CaptureProvenance.Create(appName: "Notepad", appExecutablePath: @"C:\Windows\notepad.exe"), "Pin_Translate", true),
        new("image-url-source", CaptureProvenance.Create(url: "https://example.com/page"), "Pin_Translate", true),
        // Text, Markdown, code and table pins: the language decides whether the command is on, off or absent.
        new("text-no-translation", null, null, false),
        new("text-translate-off", null, "Pin_Translate", false),
        new("text-translated", null, "Pin_TranslationOriginal", true),
        new("text-translating", null, "Pin_TranslationCancel", true),
        new("text-translated-url-source", CaptureProvenance.Create(url: "https://example.com/page"), "Pin_TranslationOriginal", true),
    ];

    [Fact]
    public Task EveryPinStateLaysTheBarOutByOneRule() => SettingsThemePreviewTests.RunIsolated(
        nameof(EveryPinStateLaysTheBarOutByOneRule), VerifyLayouts, typeof(PinHoverBarLayoutTests));

    private static void VerifyLayouts()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        string? evidence = Environment.GetEnvironmentVariable(EvidenceVariable) is { Length: > 0 } requested
            ? DiagnosticOutputPaths.Create(requested) : null;
        var table = new StringBuilder("language\tstate\tmode\tbarWidth\tbarHeight\tlabeledWidth\tbudget\tnamesAt480\tbutton\tx\twidth\theight\tminWidth\ticonOffsetX\ticonCenterY\tlabel\n");
        var failures = new List<string>();
        try
        {
            ThemeService.ApplyFromSettings(AppThemeNames.ToSetting(AppTheme.Glass));
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var scope = UiText.UseLanguage(language);
                foreach (BarState state in States)
                foreach (bool compact in new[] { false, true })
                {
                    var bar = new PinHoverBar(state.Source) { Left = -10000, Top = -10000 };
                    try
                    {
                        if (state.TranslationKey is { } key)
                            bar.SetTranslationAction(true, state.TranslationEnabled, UiText.Get(key), UiText.Get(key));
                        double labeled = bar.MeasureLabeledWidth();
                        bool namesAt480 = !bar.NeedsCompact(480, labeled);
                        bar.Compact = compact;
                        bar.Show();
                        Settle(bar);
                        var surface = (Border)bar.Content;
                        string mode = compact ? "icons" : "names";
                        List<ButtonMetrics> buttons = Measure(surface);
                        foreach (ButtonMetrics button in buttons)
                            table.Append(CultureInfo.InvariantCulture, $"{language}\t{state.Name}\t{mode}\t{surface.ActualWidth:0.##}\t{surface.ActualHeight:0.##}\t{labeled:0.##}\t{bar.LabeledWidthBudget:0.##}\t{namesAt480}\t{button.Name}\t{button.X:0.##}\t{button.Width:0.##}\t{button.Height:0.##}\t{button.MinWidth:0.##}\t{button.IconOffsetX:0.##}\t{button.IconCenterY:0.##}\t{button.Label}\n");
                        if (evidence is not null)
                            Save(surface, Path.Combine(evidence, $"{language[..2]}-{state.Name}-{mode}.png"));
                        failures.AddRange(RuleViolations(bar, buttons, compact, labeled).Select(problem => $"{language} {state.Name} {mode}: {problem}"));
                    }
                    finally { bar.Close(); }
                }
            }
            if (evidence is not null) File.WriteAllText(Path.Combine(evidence, "buttons.tsv"), table.ToString(), new UTF8Encoding(false));
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            application.Shutdown();
        }
    }

    /// <summary>
    /// A large table pin with its hover bar drawn where <see cref="PinHoverBar.Place"/> puts it on three work
    /// areas: room above (the bar above), the pin at the top edge (below), and a work area no taller than the
    /// pin (inside its top-right corner). The pictures go to MYCAPTURE_PINBAR_EVIDENCE when it is set.
    /// </summary>
    [Fact]
    public Task ALargeTextPinKeepsItsBarOutsideAndInsideOnlyWithoutRoom() => SettingsThemePreviewTests.RunIsolated(
        nameof(ALargeTextPinKeepsItsBarOutsideAndInsideOnlyWithoutRoom), VerifyPlacementScenes, typeof(PinHoverBarLayoutTests));

    private static void VerifyPlacementScenes()
    {
        Application application = ThemedApplication();
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        string? evidence = Environment.GetEnvironmentVariable(EvidenceVariable) is { Length: > 0 } requested
            ? DiagnosticOutputPaths.Create(requested) : null;
        PinWindow? pin = null;
        try
        {
            ThemeService.ApplyFromSettings(AppThemeNames.ToSetting(AppTheme.Glass));
            using var scope = UiText.UseLanguage("ko-KR");
            string text = string.Join("\n", new[] { "항목\t담당\t상태\t마감\t비고" }.Concat(Enumerable.Range(1, 16).Select(row =>
                $"작업 {row:00}\t{(char)('가' + (row % 5))}팀\t{(row % 3 == 0 ? "검토" : "진행")}\t11-{row + 10:00}\t" +
                (row % 4 == 0 ? "자료 확인 필요" : "-"))));
            PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(text, true), text, true);
            pin = new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1),
                -10000, -10000, () => new PinSettings())
            {
                ShowActivated = false,
                ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
            };
            pin.Show();
            pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            PinHoverBar bar = Assert.IsType<PinHoverBar>(pin.HoverBarForTest);
            Settle(pin);
            Settle(bar);
            var pinContent = (FrameworkElement)pin.Content;
            var barContent = (FrameworkElement)bar.Content;
            double width = pinContent.ActualWidth, height = pinContent.ActualHeight;
            Assert.True(width >= 400 && height >= 300, $"The table pin is {width:0} x {height:0} DIP, not a large one.");
            // A large pin shows every name on its bar.
            Assert.False(bar.Compact);
            const double room = 96;
            var scenes = new (string Name, RectD Work, RectD Pin)[]
            {
                ("above", new(0, 0, width + (room * 2), height + (room * 2)), new(room, room, width, height)),
                ("below", new(0, 0, width + (room * 2), height + room), new(room, 0, width, height)),
                ("inside", new(0, 0, width + (room * 2), height), new(room, 0, width, height)),
            };
            var report = new StringBuilder("scene\twork\tpin\tbar\n");
            foreach (var scene in scenes)
            {
                RectD placed = PinHoverBar.Place(scene.Pin, barContent.ActualWidth, barContent.ActualHeight, scene.Work, 1);
                report.Append(CultureInfo.InvariantCulture, $"{scene.Name}\t{scene.Work}\t{scene.Pin}\t{placed}\n");
                // Right-aligned with the pin and outside it; one gap inside its corner only when it has to be inside.
                switch (scene.Name)
                {
                    case "above":
                        Assert.Equal(scene.Pin.Top - PinHoverBar.EdgeGap, placed.Bottom, 0.5);
                        Assert.Equal(scene.Pin.Right, placed.Right, 0.5);
                        break;
                    case "below":
                        Assert.Equal(scene.Pin.Bottom + PinHoverBar.EdgeGap, placed.Top, 0.5);
                        Assert.Equal(scene.Pin.Right, placed.Right, 0.5);
                        break;
                    default:
                        Assert.Equal(scene.Pin.Top + PinHoverBar.EdgeGap, placed.Top, 0.5);
                        Assert.Equal(scene.Pin.Right - PinHoverBar.EdgeGap, placed.Right, 0.5);
                        break;
                }
                if (evidence is not null)
                {
                    SettingsThemePreviewTests.SaveBitmap(
                        SettingsThemePreviewTests.RenderPinScene(pinContent, barContent, scene.Pin, placed, scene.Work, 2),
                        Path.Combine(evidence, $"placement-{scene.Name}.png"));
                }
            }
            if (evidence is not null) File.WriteAllText(Path.Combine(evidence, "placement.tsv"), report.ToString(), new UTF8Encoding(false));
        }
        finally
        {
            pin?.Close();
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            application.Shutdown();
        }
    }

    /// <summary>
    /// The pin menu's icon column follows the library card menu's rule: an icon only on the rows the hover bar
    /// also offers as buttons, with that button's icon, so no two rows share one.
    /// </summary>
    [Fact]
    public Task PinMenuIconsAreTheHoverBarsOwnAndNoTwoRowsShareOne() => SettingsThemePreviewTests.RunIsolated(
        nameof(PinMenuIconsAreTheHoverBarsOwnAndNoTwoRowsShareOne), VerifyMenuIcons, typeof(PinHoverBarLayoutTests));

    private static void VerifyMenuIcons()
    {
        Application application = ThemedApplication();
        try
        {
            using var scope = UiText.UseLanguage("ko-KR");
            Geometry Resource(string key) => Assert.IsAssignableFrom<Geometry>(application.FindResource(key));
            var barIcons = new Dictionary<Geometry, string>
            {
                [Resource("Icon.Copy")] = "copy",
                [Resource("Icon.Ocr")] = "text copy",
                [Resource("Icon.Save")] = "save",
                [Resource("Icon.Close")] = "close",
                [PinHoverBar.TranslationGlyph] = "translation",
            };
            const string text = "Quarterly notes\nSecond line";
            BitmapSource picture = BitmapSource.Create(160, 100, 96, 96, PixelFormats.Bgra32, null, new byte[160 * 100 * 4], 160 * 4);
            picture.Freeze();
            foreach ((PinContent content, string[] iconRows) in new[]
            {
                (PinContent.FromImage(picture), new[] { "Text_DFC084A111D0", "Pin_CopyImageName", "Text_03A1596F1681", "Dialog.Close" }),
                (PinContent.FromText(ClipboardTextRenderer.Render(text, false), text, false),
                    new[] { "Text_DFC084A111D0", "Pin_CopyTextImageName", "Text_976A6C92B870", "Dialog.Close" }),
            })
            {
                var pin = new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1),
                    -10000, -10000, () => new PinSettings()) { ShowActivated = false };
                try
                {
                    MenuItem[] rows = pin.ContextMenu.Items.OfType<MenuItem>().ToArray();
                    MenuItem[] withIcons = rows.Where(item => item.Icon is not null).ToArray();
                    Geometry[] glyphs = withIcons.Select(item => Assert.IsType<SymbolIcon>(item.Icon).Data!).ToArray();
                    Assert.All(glyphs, glyph => Assert.True(barIcons.ContainsKey(glyph), "A menu icon the hover bar does not show."));
                    Assert.Equal(glyphs.Length, glyphs.Distinct().Count());
                    // The translation command, when the pin offers it, is the only row besides these four.
                    Assert.Equal(iconRows.Select(UiText.Get),
                        withIcons.Where(item => barIcons[((SymbolIcon)item.Icon).Data!] != "translation").Select(item => (string)item.Header));
                    Assert.Equal(["save", "copy", "text copy", "close"],
                        glyphs.Select(glyph => barIcons[glyph]).Where(name => name != "translation"));
                    // Copy image only, save as, zoom and the other rows have no icon.
                    foreach (string key in new[] { "Pin_CopyImageOnly", "Text_A81F975C401F", "Onboard_PinZoomIn", "Onboard_PinZoomOut",
                                 "Onboard_PinAllowClickThrough", "Onboard_TrayReopenPin", "Text_E657405F6B2D" })
                    {
                        if (rows.FirstOrDefault(item => (string)item.Header == UiText.Get(key)) is { } row) Assert.Null(row.Icon);
                    }
                }
                finally { pin.Close(); }
            }
        }
        finally { application.Shutdown(); }
    }

    private static Application ThemedApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        }
        return application;
    }

    internal sealed record ButtonMetrics(Button Button, string Name, double X, double Width, double Height, double MinWidth,
        double IconOffsetX, double IconCenterY, bool Label);

    internal static List<ButtonMetrics> Measure(Border surface)
    {
        var row = (Panel)surface.Child;
        var result = new List<ButtonMetrics>();
        foreach (Button button in row.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible))
        {
            Point origin = button.TranslatePoint(new Point(0, 0), surface);
            var icon = Descendants(button).OfType<FrameworkElement>()
                .First(element => element is SymbolIcon || (element is Grid grid && grid.Children.OfType<SymbolIcon>().Any()));
            Point iconOrigin = icon.TranslatePoint(new Point(0, 0), surface);
            bool label = Descendants(button).OfType<TextBlock>().Any(text =>
                text.IsVisible && text.Text.Length > 0 && text.Parent is StackPanel);
            result.Add(new ButtonMetrics(button, AutomationProperties.GetName(button), origin.X, button.ActualWidth, button.ActualHeight,
                button.MinWidth, label ? double.NaN : iconOrigin.X + (icon.ActualWidth / 2) - (origin.X + (button.ActualWidth / 2)),
                iconOrigin.Y + (icon.ActualHeight / 2), label));
        }
        return result;
    }

    private static IEnumerable<string> RuleViolations(PinHoverBar bar, List<ButtonMetrics> buttons, bool compact, double labeled)
    {
        // One order: the shown buttons are the command order with the absent ones left out, and nothing else.
        Button[] expected = bar.CommandOrder.Where(button => button.Visibility == Visibility.Visible).ToArray();
        if (!buttons.Select(button => button.Button).SequenceEqual(expected))
            yield return "order " + string.Join(", ", buttons.Select(button => button.Name));
        if (!ReferenceEquals(buttons[0].Button, bar.CopyButton) || !ReferenceEquals(buttons[1].Button, bar.TextCopyButton)
            || !ReferenceEquals(buttons[2].Button, bar.SaveButton) || !ReferenceEquals(buttons[^1].Button, bar.CloseButton))
            yield return "copy, text copy and save lead and close ends the bar";
        string opacity = UiText.Get("Onboard_PinOpacity");
        if (buttons.Any(button => button.Name == opacity)) yield return "the bar still has an opacity button";
        // One size and one line: every button the same height, minimum width and icon line.
        if (buttons.Select(button => Math.Round(button.Height, 1)).Distinct().Count() != 1)
            yield return "heights " + string.Join("/", buttons.Select(button => button.Height));
        if (buttons.Select(button => button.MinWidth).Distinct().Count() != 1)
            yield return "minimum widths " + string.Join("/", buttons.Select(button => button.MinWidth));
        if (buttons.Max(button => button.IconCenterY) - buttons.Min(button => button.IconCenterY) > 0.5)
            yield return "icon lines " + string.Join("/", buttons.Select(button => button.IconCenterY));
        // An icon-only button keeps its icon in its middle.
        foreach (ButtonMetrics button in buttons.Where(button => !button.Label))
            if (Math.Abs(button.IconOffsetX) > 0.75) yield return $"{button.Name} icon off centre by {button.IconOffsetX:0.##}";
        // Even steps between neighbours.
        double[] gaps = buttons.Zip(buttons.Skip(1), (left, right) => right.X - (left.X + left.Width)).ToArray();
        if (gaps.Length > 0 && gaps.Max() - gaps.Min() > 0.5) yield return "gaps " + string.Join("/", gaps);
        if (compact)
        {
            // Icons only: every button the same narrow width, so the icons sit at even steps.
            if (buttons.Any(button => Math.Abs(button.Width - PinHoverBar.IconOnlyButtonWidth) > 0.5))
                yield return "icon widths " + string.Join("/", buttons.Select(button => button.Width));
            if (buttons.Any(button => button.Label)) yield return "a name shows in the icon-only bar";
        }
        else
        {
            // Every name or none: each named command shows its name; source and close are icons by design.
            foreach (ButtonMetrics button in buttons)
            {
                bool named = !ReferenceEquals(button.Button, bar.SourceButton) && !ReferenceEquals(button.Button, bar.CloseButton);
                if (button.Label != named) yield return $"{button.Name} name shown {button.Label}";
            }
            // A pin wide enough for the named bar shows the names, whatever its kind or translation state.
            if (bar.NeedsCompact(labeled + (PinHoverBar.EdgeGap * 4), labeled))
                yield return $"named width {labeled:0.##} exceeds budget {bar.LabeledWidthBudget:0.##}";
        }
    }

    internal static void Settle(Window window)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        window.UpdateLayout();
    }

    private static void Save(FrameworkElement element, string path)
    {
        const double scale = 2;
        const double margin = 8;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x7E)), null,
                new Rect(0, 0, element.ActualWidth + (margin * 2), element.ActualHeight + (margin * 2)));
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null,
                new Rect(margin, margin, element.ActualWidth, element.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((element.ActualWidth + (margin * 2)) * scale),
            (int)Math.Ceiling((element.ActualHeight + (margin * 2)) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Ocr410Fixture.Save(bitmap, path);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
