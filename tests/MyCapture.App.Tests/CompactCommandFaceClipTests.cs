using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Renders the shipped compact consumers without opening windows or invoking their actions.</summary>
public sealed class CompactCommandFaceClipTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string language in new[] { "ko-KR", "en-US" })
        foreach (bool contrast in new[] { false, true })
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            yield return [language, contrast, scale];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RenderedGlyphsAndRevealedCaptionsFitEveryAncestorClip(string language, bool contrast, double scale) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previousHints = ShortcutPresentation.ReadEnabled;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        Func<SystemColorRole, Color>? previousColors = ThemeService.SystemColorsOverrideForTest;
        QuickAccessWindow? card = null;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            ThemeService.SystemHighContrastOverrideForTest = contrast;
            ThemeService.SystemColorsOverrideForTest = role => SystemHighContrastThemeTests.NightSky[role];
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: contrast);
            var bar = new CaptureModeBar { Resources = resources, HorizontalAlignment = HorizontalAlignment.Left };
            bar.Configure(detectWindows: true, textAvailable: true, colorAvailable: true, otherActions: true);
            bar.ApplyPalette(CaptureGuidePalette.Create(AppTheme.Midnight, null, contrast));

            var bitmap = BitmapSource.Create(240, 96, 96, 96, PixelFormats.Bgra32, null, new byte[240 * 96 * 4], 240 * 4);
            bitmap.Freeze();
            card = new QuickAccessWindow(bitmap, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
            Border cardContent = QuickAccessDetachedFixture.Layout(card, scale);
            ThemeService.ApplyResources(AppTheme.Midnight, cardContent.Resources, systemHighContrast: contrast);
            // The app's Application resources exist when its constructor resolves these icons.
            // This detached fixture loads the same resources afterward, without an Application.
            // Every action, including the library, shows an icon above its name.
            Assert.All(card.ActionButtonsForTest.Append(card.LibraryButtonForTest),
                action => Assert.IsType<SymbolIcon>(QuickAccessLabelFixture.IconOf(action)));
            QuickAccessLabelFixture.RealIcons(card, cardContent);
            Button close = Assert.Single(QuickAccessDetachedFixture.Descendants(cardContent).OfType<Button>(),
                button => AutomationProperties.GetAutomationId(button) == "Dialog.Close");
            Assert.IsType<SymbolIcon>(close.Content).Data = (Geometry)cardContent.Resources["Icon.Close"];
            Assert.IsType<SymbolIcon>(card.KeepToggleForTest.Content).Data = (Geometry)cardContent.Resources["Icon.Pause"];
            cardContent.Width = card.Width;
            cardContent.HorizontalAlignment = HorizontalAlignment.Left;

            var column = new StackPanel();
            column.Children.Add(bar);
            column.Children.Add(cardContent);
            var host = new Border { Child = column, Resources = resources, Padding = new Thickness(12), ClipToBounds = true };
            host.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
            VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
            Layout(host);

            ButtonBase[] buttons = QuickAccessDetachedFixture.Descendants(host).OfType<ButtonBase>().ToArray();
            Assert.Equal(20, buttons.Length); // Twelve capture commands and eight receipt commands.
            foreach (ButtonBase button in buttons) CommandButtonPresentation.RefreshHint(button);

            var failures = new List<string>();
            var measurements = new List<object>();
            string caseName = $"{language}-{(contrast ? "contrast" : "normal")}-{scale.ToString("0.0", CultureInfo.InvariantCulture)}";
            double? namedHeight = null;
            foreach (bool revealed in new[] { false, true, false })
            {
                ShortcutPresentation.SetAltRevealed(host, revealed);
                host.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Layout(host);
                RenderTargetBitmap image = Render(host, scale);
                SaveEvidence(image, $"{caseName}-{(revealed ? "alt" : "rest")}.png");
                foreach (ButtonBase button in buttons)
                {
                    var face = Assert.IsType<Grid>(button.Content);
                    Assert.Equal("CommandButtonFace", face.Name);
                    var caption = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
                    ShortcutMotionTestScope.AssertState(caption, revealed);
                    string name = $"{(bar.Holds(button) ? "capture" : "receipt")}:{AutomationProperties.GetName(button)}";
                    // Capture commands are 36 tall; a receipt action that shows its name is taller by the name,
                    // and every one of them keeps that height whether or not Alt shows its key.
                    if (button is Button action && QuickAccessActionPanel.LabelOf(action) is { Visibility: Visibility.Visible })
                    {
                        Assert.True(button.ActualHeight > 36, name);
                        Assert.Equal(namedHeight ??= button.ActualHeight, button.ActualHeight, 2);
                        Assert.Equal(QuickAccessLabelFixture.ExpectedActionHeight(action), button.ActualHeight, 2);
                    }
                    else Assert.Equal(36, button.ActualHeight, 2);
                    foreach (FrameworkElement ink in QuickAccessDetachedFixture.Descendants(face)
                                 .OfType<FrameworkElement>().Where(element => element is SymbolIcon or TextBlock))
                    {
                        // A resting caption is deliberately transparent and translated below its slot.
                        // Test its visible endpoint after Alt, and all visible primary content in both states.
                        if (ink.Visibility != Visibility.Visible || ink.Opacity == 0) continue;
                        Rect bounds = UnclippedDrawingBounds(VisualTreeHelper.GetDrawing(ink));
                        if (ink is TextBlock { Text.Length: 0 }) continue;
                        Assert.False(bounds.IsEmpty, $"Missing rendered content for {name}/{ink.GetType().Name}");
                        CheckClips(ink, bounds, host, $"{name}/{(ReferenceEquals(ink, caption) ? "caption" : "primary")}/{revealed}", failures);
                        measurements.Add(new
                        {
                            Button = name, Revealed = revealed, Element = ReferenceEquals(ink, caption) ? "caption" : "primary",
                            ButtonHeight = button.ActualHeight, FaceHeight = face.ActualHeight,
                            Padding = button.Padding.ToString(),
                            InkInButton = ink.TransformToAncestor(button).TransformBounds(bounds).ToString(CultureInfo.InvariantCulture),
                        });
                    }
                }
            }
            SaveEvidence(JsonSerializer.Serialize(new { Measurements = measurements, Failures = failures },
                new JsonSerializerOptions { WriteIndented = true }), $"{caseName}.json");
            Assert.False(card.IsVisible);
            Assert.Empty(failures);
        }
        finally
        {
            card?.Close();
            ShortcutPresentation.ReadEnabled = previousHints;
            ShortcutPresentation.Refresh();
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            ThemeService.SystemColorsOverrideForTest = previousColors;
        }
    });

    private static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(640, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, 640, host.DesiredSize.Height));
        host.UpdateLayout();
    }

    private static RenderTargetBitmap Render(FrameworkElement host, double scale)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * scale),
            (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(host);
        return image;
    }

    // DrawingGroup.Bounds already applies the group's clip. Walking its leaves preserves the
    // original ink (including pen width and glyph overhang) so a clipped drawing cannot pass.
    internal static Rect UnclippedDrawingBounds(Drawing? drawing)
    {
        if (drawing is not DrawingGroup group) return drawing?.Bounds ?? Rect.Empty;
        Rect bounds = Rect.Empty;
        foreach (Drawing child in group.Children) bounds.Union(UnclippedDrawingBounds(child));
        return bounds.IsEmpty ? bounds : group.Transform?.TransformBounds(bounds) ?? bounds;
    }

    internal static void CheckClips(FrameworkElement ink, Rect bounds, FrameworkElement host, string name, List<string> failures)
    {
        for (DependencyObject? current = ink; current is Visual ancestor; current = VisualTreeHelper.GetParent(current))
        {
            Rect inAncestor = ReferenceEquals(ink, ancestor) ? bounds : ink.TransformToAncestor(ancestor).TransformBounds(bounds);
            // GetClip includes WPF's automatic layout clip, not only an explicit ClipToBounds.
            if (VisualTreeHelper.GetClip(ancestor) is { } clip)
            {
                Rect visible = clip.Bounds;
                visible.Inflate(0.01, 0.01);
                if (!visible.Contains(inAncestor))
                    failures.Add($"{name}: ink {inAncestor} exceeds {ancestor.GetType().Name} clip {clip.Bounds}");
            }
            if (ReferenceEquals(ancestor, host)) break;
        }
    }

    private static void SaveEvidence(RenderTargetBitmap image, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("MYCAPTURE_COMPACT_FACE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    private static void SaveEvidence(string content, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("MYCAPTURE_COMPACT_FACE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), content);
    }
}
