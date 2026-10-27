using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Every quick access action names itself under its icon, or none does: a card too narrow for all
/// six names shows only icons, never some names and never a clipped or ellipsized one.
/// </summary>
public sealed class QuickAccessActionLabelTests
{
    private static readonly string[] KoreanNames = ["편집", "핀", "저장", "텍스트", "복사", "라이브러리"];
    private static readonly string[] EnglishNames = ["Edit", "Pin", "Save", "Text", "Copy", "Library"];

    public static IEnumerable<object[]> Cases()
    {
        foreach (string language in new[] { "ko", "en" })
        foreach (double dpi in new[] { 1.0, 1.5 })
        foreach (double textScale in new[] { 1.0, 1.5 })
            yield return [language, dpi, textScale];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryActionShowsItsNameWithoutClippingOrEveryActionShowsOnlyItsIcon(string language, double dpi, double textScale) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var motion = new ShortcutMotionTestScope();
        QuickAccessWindow card = QuickAccessLabelFixture.NewCard();
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card, dpi);
            QuickAccessLabelFixture.ApplyTextScale(host, textScale);
            QuickAccessLabelFixture.RealIcons(card, host);
            QuickAccessLabelFixture.Relayout(host, card.Width, dpi);
            // The card keeps its width; only the actions inside it change.
            Assert.Equal(card.AllowsTransparency ? 344 : 328, host.ActualWidth);

            var panel = Assert.Single(QuickAccessDetachedFixture.Descendants(host).OfType<QuickAccessActionPanel>());
            double available = ((FrameworkElement)panel.Parent).ActualWidth;
            Button[] buttons = [.. card.ActionButtonsForTest, card.LibraryButtonForTest];
            TextBlock[] labels = [.. buttons.Select(button => Assert.IsType<TextBlock>(QuickAccessActionPanel.LabelOf(button)))];
            Assert.Equal(language == "ko" ? KoreanNames : EnglishNames, labels.Select(label => label.Text));
            Assert.Equal(language == "ko"
                ? ["편집", "핀", "저장", "텍스트 복사", "복사", "라이브러리"]
                : ["Edit", "Pin", "Save", "Copy text", "Copy", "Library"], buttons.Select(AutomationProperties.GetName));
            Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(button.ToolTip as string)));

            // All or none, never a partial set.
            bool shown = panel.NamesShown;
            Assert.All(labels, label => Assert.Equal(shown ? Visibility.Visible : Visibility.Collapsed, label.Visibility));
            // The names are dropped only when the full set does not fit the card's action row.
            double labeled = LabeledWidth(panel, host, card.Width, dpi);
            Assert.True(labeled <= available == shown, $"{language}: names need {labeled:0.###} of {available:0.###} DIP, shown={shown}");
            if (textScale == 1) Assert.True(shown, $"{language}: the names need {labeled:0.#} of {available:0.#} DIP at normal text size");
            Assert.Same(host.FindResource("Button.Primary"), buttons[0].Style);
            Assert.All(buttons.Skip(1), button => Assert.Same(host.FindResource("Button.Ghost"), button.Style));

            RenderTargetBitmap image = QuickAccessLabelFixture.Render(host, dpi);
            var measurements = new List<object>();
            Rect[] bounds = [.. buttons.Select(button => Bounds(button, host))];
            Rect row = Bounds((FrameworkElement)panel.Parent, host);
            for (int index = 0; index < buttons.Length; index++)
            {
                Button button = buttons[index];
                // Inside the card's content area, and not on top of its neighbour.
                Assert.True(bounds[index].Left >= row.Left - 0.5 && bounds[index].Right <= row.Right + 0.5,
                    $"{language}: {labels[index].Text} leaves the card's content: {bounds[index]} in {row}");
                if (index > 0) Assert.True(bounds[index - 1].Right <= bounds[index].Left + 0.01, "Adjacent actions overlap");
                SymbolIcon icon = Assert.IsType<SymbolIcon>(QuickAccessLabelFixture.IconOf(button));
                Assert.True(button.ActualWidth >= 28 && button.ActualHeight >= 28);
                Assert.Equal(shown ? buttons[0].ActualHeight : 36, button.ActualHeight, 2);

                var failures = new List<string>();
                foreach (FrameworkElement ink in shown ? new FrameworkElement[] { icon, labels[index] } : [icon])
                {
                    Rect drawn = CompactCommandFaceClipTests.UnclippedDrawingBounds(VisualTreeHelper.GetDrawing(ink));
                    Assert.False(drawn.IsEmpty, $"{language}: nothing is drawn for {ink.GetType().Name} of {AutomationProperties.GetName(button)}");
                    CompactCommandFaceClipTests.CheckClips(ink, drawn, host, ink is TextBlock ? "name" : "icon", failures);
                    Rect inButton = ink.TransformToAncestor(button).TransformBounds(drawn);
                    if (inButton.Left < -0.01 || inButton.Top < -0.01 || inButton.Right > button.ActualWidth + 0.01
                        || inButton.Bottom > button.ActualHeight + 0.01)
                        failures.Add($"{ink.GetType().Name} ink {inButton} leaves its {button.RenderSize} button");
                }
                Assert.Empty(failures);

                if (!shown) continue;
                // The name is whole: one line as wide as its text, with no trimming.
                TextBlock label = labels[index];
                var probe = new TextBlock
                {
                    Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight,
                    FontStyle = label.FontStyle, FontStretch = label.FontStretch,
                };
                TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(label));
                VisualTreeHelper.SetRootDpi(probe, new DpiScale(dpi, dpi));
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.Equal(TextTrimming.None, label.TextTrimming);
                Assert.Equal(TextWrapping.NoWrap, label.TextWrapping);
                Assert.True(label.ActualWidth + 0.5 >= probe.DesiredSize.Width,
                    $"{language}: '{label.Text}' needs {probe.DesiredSize.Width:0.#} DIP but has {label.ActualWidth:0.#} DIP");
                Assert.True(label.ActualHeight <= probe.DesiredSize.Height + 0.5, $"'{label.Text}' wrapped");
            }

            foreach ((Button button, Rect rect) in buttons.Zip(bounds))
                measurements.Add(new
                {
                    Name = AutomationProperties.GetName(button), Label = QuickAccessActionPanel.LabelOf(button)!.Text,
                    LabelVisible = shown, ButtonWidth = Math.Round(rect.Width, 2), ButtonHeight = Math.Round(rect.Height, 2),
                    LabelWidth = Math.Round(QuickAccessActionPanel.LabelOf(button)!.ActualWidth, 2),
                    Left = Math.Round(rect.Left, 2), Right = Math.Round(rect.Right, 2),
                });
            SaveMeasurements($"quick-card-measure-{language}-dpi{dpi * 96:0}-text{textScale:0.0}.json", new
            {
                Language = language, Dpi = dpi * 96, TextScale = textScale, CardWidth = host.ActualWidth,
                ActionRowAvailable = available, NamesNeed = Math.Round(labeled, 2), NamesShown = shown,
                CardHeight = Math.Round(host.ActualHeight, 2), Actions = measurements,
            });
            Assert.True(image.PixelWidth > 0);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void NamesDropTogetherAtEveryWidthAndNeverPartially(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var motion = new ShortcutMotionTestScope();
        QuickAccessWindow card = QuickAccessLabelFixture.NewCard();
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card);
            QuickAccessLabelFixture.RealIcons(card, host);
            QuickAccessLabelFixture.Relayout(host, card.Width, 1);
            var panel = Assert.Single(QuickAccessDetachedFixture.Descendants(host).OfType<QuickAccessActionPanel>());
            Button[] buttons = [.. card.ActionButtonsForTest, card.LibraryButtonForTest];
            double labeled = LabeledWidth(panel, host, card.Width, 1);
            double iconsOnly = MeasuredWidth(panel, labeled - 0.01);
            Assert.True(iconsOnly < labeled);

            var widths = new List<double> { labeled, labeled - 0.01, labeled + 0.01, iconsOnly, labeled * 2, 40 };
            for (double width = 100; width <= 400; width += 7) widths.Add(width);
            foreach (double width in widths)
            {
                double desired = MeasuredWidth(panel, width);
                Visibility[] states = [.. buttons.Select(button => QuickAccessActionPanel.LabelOf(button)!.Visibility)];
                Assert.True(states.All(state => state == states[0]), $"{width}: some names are shown and some are not");
                // The labelled set fits whole, or the row is the icon row.
                Assert.Equal(width >= labeled, states[0] == Visibility.Visible);
                // A measure never reports more than the width it was offered.
                Assert.Equal(Math.Min(width, width >= labeled ? labeled : iconsOnly), desired, 3);
            }
        }
        finally { card.Close(); }
    });

    [Fact]
    public void ActionNamesAreCapitalizedNounsFromTheCardsResources()
    {
        using (UiText.UseLanguage("ko"))
            Assert.Equal(KoreanNames, new[] { "QuickAccess.Edit", "QuickAccess.Pin", "QuickAccess.Save", "QuickAccess.TextLabel", "QuickAccess.Copy", "QuickAccess.Library" }
                .Select(UiText.Get));
        using (UiText.UseLanguage("en"))
            Assert.Equal(EnglishNames, new[] { "QuickAccess.Edit", "QuickAccess.Pin", "QuickAccess.Save", "QuickAccess.TextLabel", "QuickAccess.Copy", "QuickAccess.Library" }
                .Select(UiText.Get));
    }

    /// <summary>The width the six actions take with every name shown, at the current text size.</summary>
    private static double LabeledWidth(QuickAccessActionPanel panel, Border host, double width, double dpi)
    {
        double labeled = MeasuredWidth(panel, double.PositiveInfinity);
        QuickAccessLabelFixture.Relayout(host, width, dpi);
        return labeled;
    }

    private static double MeasuredWidth(QuickAccessActionPanel panel, double width)
    {
        panel.InvalidateMeasure();
        panel.Measure(new Size(width, double.PositiveInfinity));
        return panel.DesiredSize.Width;
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void SaveMeasurements(string name, object measurements)
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_UXPOLISH_EVIDENCE") is not { Length: > 0 } requested) return;
        string folder = DiagnosticOutputPaths.Create(requested);
        File.WriteAllText(DiagnosticOutputPaths.Child(folder, name),
            JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }).Replace(",\r\n", ",\n"));
    }
}

/// <summary>Builds and lays out the quick access card for the action name checks and their evidence.</summary>
internal static class QuickAccessLabelFixture
{
    internal static QuickAccessWindow NewCard() =>
        new(Sample(), () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true),
            () => { }, () => Task.FromResult("unused.png"));

    internal static BitmapSource Sample()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xE8, 0xEC, 0xF4), Color.FromRgb(0xC9, 0xD3, 0xE8), 90),
                null, new Rect(0, 0, 480, 270));
            for (int bar = 0; bar < 6; bar++)
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x6F, 0xA3)), null, new Rect(80 + bar * 56, 150 - bar * 12, 34, 70 + bar * 12));
        }
        var bitmap = new RenderTargetBitmap(480, 270, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A larger text size, as the type tokens the card reads would give it.</summary>
    internal static void ApplyTextScale(Border host, double textScale)
    {
        host.Resources["FontSize.Body"] = 14 * textScale;
        host.Resources["FontSize.Control"] = 14 * textScale;
        host.Resources["FontSize.Caption"] = 12 * textScale;
        host.Resources["CommandButton.FontSize"] = 14 * textScale;
    }

    /// <summary>
    /// A detached card has no application resources when it builds its icons, so each icon still
    /// holds a placeholder square. Give every icon the geometry the shipped card shows.
    /// </summary>
    internal static void RealIcons(QuickAccessWindow card, Border host)
    {
        foreach (Button button in card.ActionButtonsForTest.Append(card.LibraryButtonForTest))
        {
            if (IconOf(button) is not SymbolIcon icon) continue;
            string command = AutomationProperties.GetAutomationId(button)["QuickAccess.".Length..];
            string key = command switch { "CopyText" => "Icon.Ocr", "Library" => host.TryFindResource("Icon.Library") is null ? "Icon.Storage" : "Icon.Library", _ => "Icon." + command };
            icon.Data = (Geometry)host.FindResource(key);
        }
        foreach (Button close in QuickAccessDetachedFixture.Descendants(host).OfType<Button>()
                     .Where(button => AutomationProperties.GetAutomationId(button) == "Dialog.Close"))
            if (close.Content is SymbolIcon closeIcon) closeIcon.Data = (Geometry)host.FindResource("Icon.Close");
        if (card.KeepToggleForTest.Content is SymbolIcon keep) keep.Data = (Geometry)host.FindResource("Icon.Pause");
    }

    internal static void Relayout(Border host, double width, double dpi)
    {
        VisualTreeHelper.SetRootDpi(host, new DpiScale(dpi, dpi));
        host.InvalidateMeasure();
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
        host.UpdateLayout();
    }

    internal static void SavePng(FrameworkElement host, double dpi, string path)
    {
        BitmapSource image = Render(host, dpi);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static RenderTargetBitmap Render(FrameworkElement host, double dpi)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * dpi), (int)Math.Ceiling(host.ActualHeight * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        image.Render(host);
        return image;
    }

    /// <summary>
    /// The height an action that shows its name takes: its icon, the name and its gap, the reserved
    /// shortcut line, and the button's own border and padding. Without a name it is the 36 DIP command height.
    /// </summary>
    internal static double ExpectedActionHeight(Button button)
    {
        if (QuickAccessActionPanel.LabelOf(button) is not { Visibility: Visibility.Visible } label) return 36;
        return CommandButtonPresentation.IconSize + label.Margin.Top + label.ActualHeight + label.Margin.Bottom
            + CommandButtonPresentation.HintHeight + button.BorderThickness.Top + button.BorderThickness.Bottom
            + button.Padding.Top + button.Padding.Bottom;
    }

    /// <summary>The first symbol in a button's content, wherever the face keeps it.</summary>
    internal static SymbolIcon? IconOf(Button button) => button.Content is DependencyObject root ? FindIcon(root) : null;

    private static SymbolIcon? FindIcon(DependencyObject node)
    {
        if (node is SymbolIcon icon) return icon;
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            if (FindIcon(child) is { } found) return found;
        return null;
    }
}
