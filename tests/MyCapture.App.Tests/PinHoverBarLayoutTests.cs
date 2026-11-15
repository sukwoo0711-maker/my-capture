using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
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
