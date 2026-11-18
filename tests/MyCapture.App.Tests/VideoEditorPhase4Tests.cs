using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class VideoEditorPhase4Tests
{
    public static IEnumerable<object[]> PalettesAndScales()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        foreach (double scale in new[] { 1d, 1.5d })
            yield return [theme, false, false, scale];
        foreach (double scale in new[] { 1d, 1.5d })
        {
            yield return [AppTheme.System, true, false, scale];
            yield return [AppTheme.System, false, true, scale];
            yield return [AppTheme.System, true, true, scale];
        }
    }

    [Theory]
    [MemberData(nameof(PalettesAndScales))]
    public void DetailsUseNeutralDisclosureSelectionInEveryPalette(AppTheme theme, bool light, bool systemContrast, double scale) => Run(() =>
    {
        ThemeService.SystemLightThemeReaderForTest = () => light;
        ThemeService.SystemHighContrastOverrideForTest = systemContrast;
        var system = light ? SystemHighContrastThemeTests.Desert : SystemHighContrastThemeTests.NightSky;
        ThemeService.SystemColorsOverrideForTest = role => system[role];
        VideoEditorWindow editor = OpenLayout(1180, 760);
        try
        {
            WorkspaceTheme.Apply(editor.Resources, WorkspaceRole.VideoEditor, theme, systemHighContrast: systemContrast);
            VisualTreeHelper.SetRootDpi(editor, new DpiScale(scale, scale));
            ToggleButton toggle = Named<ToggleButton>(editor, "VideoDetailsToggle");
            Assert.Same(editor.FindResource("ToggleButton.Disclosure"), toggle.Style);
            editor.SetDetailsExpandedForTest(true);
            Settle(editor);
            Border selected = Part<Border>(toggle, "CheckedLayer");
            Assert.Equal(1d, selected.Opacity);
            Assert.Equal(ColorOf(editor.FindResource("Surface.Selected")), ColorOf(selected.Background));
            Assert.Equal(ColorOf(editor.FindResource("Selection.Stroke")), ColorOf(selected.BorderBrush));
            Assert.Equal((Thickness)editor.FindResource("Selection.Thickness"), selected.BorderThickness);
            Assert.Equal(ColorOf(editor.FindResource("Text.Primary")), ColorOf(toggle.Foreground));
            Assert.True(Contrast(ColorOf(toggle.Foreground), ColorOf(selected.Background)) >= 4.5);
            Assert.True(Contrast(ColorOf(selected.BorderBrush), ColorOf(selected.Background)) >= 3);
            Assert.True(Contrast(ColorOf(selected.BorderBrush), ColorOf(editor.FindResource("Surface.Base"))) >= 3);
            Assert.All(Descendants(toggle).OfType<TextBlock>().Where(text => !string.IsNullOrEmpty(text.Text)),
                text => Assert.Equal(ColorOf(toggle.Foreground), ColorOf(text.Foreground)));
            SymbolIcon chevron = Assert.Single(Descendants(toggle).OfType<SymbolIcon>());
            Assert.Equal(ColorOf(toggle.Foreground), ColorOf(chevron.Stroke));
            if (systemContrast)
            {
                Assert.Equal(system[SystemColorRole.Window], ColorOf(selected.Background));
                Assert.Equal(system[SystemColorRole.WindowText], ColorOf(toggle.Foreground));
                Assert.Equal(new Thickness(2), selected.BorderThickness);
            }
            editor.SetDetailsExpandedForTest(false);
            Settle(editor);
            Assert.Equal(0d, selected.Opacity);
            Assert.False(Named<Border>(editor, "VideoDetailsPanel").IsVisible);
        }
        finally { editor.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", 760, 555)]
    [InlineData("en-US", 760, 555)]
    [InlineData("ko-KR", 1180, 760)]
    [InlineData("en-US", 1180, 760)]
    public void InspectorReadsFrameNavigationThenTrimResultAndTimesThenLayers(string language, int width, int height) => Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        VideoEditorWindow editor = OpenLayout(width, height);
        try
        {
            Assert.False(editor.AreDetailsExpandedForTest);
            Border surface = Named<Border>(editor, "VideoPreviewSurface");
            Assert.Equal(new CornerRadius(0), surface.CornerRadius);
            Assert.Equal(new Thickness(0), surface.Padding);
            Assert.Equal(ColorOf(editor.FindResource("Surface.Canvas")), ColorOf(surface.Background));
            editor.SetDetailsExpandedForTest(true);
            Settle(editor);
            Border inspector = Named<Border>(editor, "VideoDetailsPanel");
            Assert.Equal(300d, inspector.Width);
            Assert.Equal(new Thickness(1, 0, 0, 0), inspector.BorderThickness);
            Assert.Equal(new CornerRadius(0), inspector.CornerRadius);
            var scroll = Named<ScrollViewer>(editor, "VideoDetailsScroll");
            var content = Assert.IsType<StackPanel>(scroll.Content);
            TextBlock frameHeading = Named<TextBlock>(editor, "VideoDetailsHeading");
            Assert.Equal(UiText.Get("VideoEditorUx_FrameNavigation"), frameHeading.Text);
            FrameworkElement[] order =
            [
                frameHeading, Named<StackPanel>(editor, "VideoPreciseTransport"),
                Named<TextBlock>(editor, "VideoTrimHeading"), Named<StackPanel>(editor, "VideoTrimSummary"),
                Field<TextBox>(editor, "_trimInInput"), Field<TextBox>(editor, "_trimOutInput"),
                Field<Button>(editor, "_resetTrimButton"), Named<Border>(editor, "VideoLayersPanel"),
            ];
            for (int i = 1; i < order.Length; i++)
            {
                double previousBottom = order[i - 1].TranslatePoint(new Point(0, order[i - 1].ActualHeight), content).Y;
                Assert.True(order[i].TranslatePoint(default, content).Y >= previousBottom - 0.5,
                    $"{order[i].Name} precedes or overlaps {order[i - 1].Name}");
            }
            foreach (FrameworkElement section in order)
                Assert.True(section.TranslatePoint(new Point(section.ActualWidth, 0), content).X <= content.ActualWidth + 0.5);
            double previewRight = surface.TranslatePoint(new Point(surface.ActualWidth, 0), (UIElement)editor.Content).X;
            double inspectorLeft = inspector.TranslatePoint(default, (UIElement)editor.Content).X;
            Assert.True(previewRight + 8 <= inspectorLeft + 0.5);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void EnterOpensInspectorAtItsFirstActionAndCollapsingReturnsFocusToDisclosure() => Run(() =>
    {
        VideoEditorWindow editor = OpenLayout(760, 555);
        try
        {
            ToggleButton toggle = Named<ToggleButton>(editor, "VideoDetailsToggle");
            KeyboardFocusCue.NotifyKeyboardInput(editor);
            Assert.Same(toggle, Keyboard.Focus(toggle));
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            toggle.RaiseEvent(enter);
            Settle(editor);
            Assert.True(enter.Handled);
            Assert.True(editor.AreDetailsExpandedForTest);
            Button first = Descendants(Named<StackPanel>(editor, "VideoPreciseTransport")).OfType<Button>().First();
            Assert.True(first.IsKeyboardFocused);
            TextBox start = Field<TextBox>(editor, "_trimInInput");
            TextBox end = Field<TextBox>(editor, "_trimOutInput");
            Button reset = Field<Button>(editor, "_resetTrimButton");
            Assert.False(reset.IsEnabled);
            Assert.True(editor.TimelineForTest.SetTrimRange(1_000, 9_000));
            Assert.Equal(1_000, editor.TimelineForTest.InMs);
            Assert.Equal(9_000, editor.TimelineForTest.OutMs);
            Assert.True(reset.IsEnabled);
            Assert.Same(start, Keyboard.Focus(start));
            Assert.True(start.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.True(end.IsKeyboardFocused);
            Assert.True(end.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.True(reset.IsKeyboardFocused);
            editor.SetDetailsExpandedForTest(false);
            Settle(editor);
            Assert.True(toggle.IsKeyboardFocused);
            Border ring = Part<Border>(toggle, "FocusRing");
            Assert.Equal(1d, ring.Opacity);
            Assert.Equal(new Thickness(2), ring.BorderThickness);
            Assert.Equal(ColorOf(editor.FindResource("Border.Focus")), ColorOf(ring.BorderBrush));
        }
        finally { editor.Close(); }
    });

    // The existing layout helper uses an unavailable media file; this test creates no folder or media.
    private static VideoEditorWindow OpenLayout(int width, int height) => OpenReadyEditor(
        Path.Combine(AppContext.BaseDirectory, "phase4-layout-in-memory"), 10_000, show: true, width, height);

    private static T Named<T>(VideoEditorWindow editor, string name) where T : FrameworkElement =>
        Assert.Single(Descendants((DependencyObject)editor.Content).OfType<T>(), item => item.Name == name);
    private static T Part<T>(Control control, string name) where T : DependencyObject =>
        Assert.IsType<T>(control.Template.FindName(name, control));
    private static Color ColorOf(object? brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static void Settle(VideoEditorWindow editor) =>
        editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void Run(Action action) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        bool? contrast = ThemeService.SystemHighContrastOverrideForTest;
        Func<SystemColorRole, Color>? colors = ThemeService.SystemColorsOverrideForTest;
        Func<bool>? light = ThemeService.SystemLightThemeReaderForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        ThemeService.SystemHighContrastOverrideForTest = false;
        try { action(); }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            ThemeService.SystemHighContrastOverrideForTest = contrast;
            ThemeService.SystemColorsOverrideForTest = colors;
            ThemeService.SystemLightThemeReaderForTest = light;
        }
    });
}
