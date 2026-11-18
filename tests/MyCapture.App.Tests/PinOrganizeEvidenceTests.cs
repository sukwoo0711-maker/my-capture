using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinOrganizeEvidenceTests
{
    [Fact]
    public void RenderSyntheticPinsAndMenuAtOneHundredPercent() => StaTestHost.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_PIN_ORGANIZE_EVIDENCE") != "1") return;
        string root = OwnedTestDirectory.Create("pin-organize-evidence-");
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var input = new SyntheticPinDragInput();
        var first = new PinWindow(PinContent.FromImage(Picture()), new PinViewState(320, 180, 1, 1, .1), 100, 100,
            dragInput: input) { ShowActivated = false };
        var second = new PinWindow(PinContent.FromImage(Picture()), new PinViewState(320, 180, 1, 1, .1), 550, 100)
            { ShowActivated = false };
        try
        {
            first.Show(); second.Show();
            BitmapSource expanded = Render((FrameworkElement)first.Content, 320, 180);
            TestDiagnosticArtifacts.SavePng(root, "before-expanded.png", expanded);
            first.ToggleCollapsed();
            BitmapSource collapsed = Render((FrameworkElement)first.Content, first.Width, first.Height);
            TestDiagnosticArtifacts.SavePng(root, "after-collapsed.png", collapsed);
            TestDiagnosticArtifacts.SavePng(root, "expanded-and-collapsed.png", Compare(expanded, collapsed));
            first.ToggleCollapsed();
            var target = second.PhysicalBounds;
            first.ReadSnapDisplays = static () => [];
            first.ReadSnapTargets = () => [new(new RectD(target.Left, target.Top, target.Right - target.Left, target.Bottom - target.Top))];
            int width = first.PhysicalBounds.Right - first.PhysicalBounds.Left;
            input.Down(first);
            PinSnapTests.MoveTo(first, input, target.Left - width - 5, target.Top);
            input.Up(first);
            Assert.Equal(first.PhysicalBounds.Right, second.PhysicalBounds.Left);
            Assert.Equal(first.PhysicalBounds.Top, second.PhysicalBounds.Top);
            TestDiagnosticArtifacts.SavePng(root, "snapped-pins.png", Joined(
                Render((FrameworkElement)first.Content, first.Width, first.Height),
                Render((FrameworkElement)second.Content, second.Width, second.Height)));
            var menu = first.ContextMenu;
            menu.Resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Daylight, menu.Resources, systemHighContrast: false);
            menu.MinWidth = 320;
            PinTidyEvidenceTests.LayoutAndSave(menu, root, "pin-menu.png");
            Console.WriteLine("PIN_ORGANIZE_EVIDENCE=" + root);
        }
        finally
        {
            first.Close(); second.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            // Evidence is intentionally retained for the mailbox handoff.
        }
    });

    private static BitmapSource Picture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(237, 244, 249)), null, new Rect(0, 0, 320, 180));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(34, 76, 111)), null, new Rect(0, 0, 320, 46));
            dc.DrawText(Text("합성 자료 · 주간 계획", 18, Brushes.White), new Point(18, 12));
            for (int i = 0; i < 3; i++)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(209, 225, 237)), null, new Rect(18, 62 + i * 34, 54 + i * 40, 20));
                dc.DrawText(Text(new[] { "자료 확인", "화면 정리", "결과 기록" }[i], 14, Brushes.Black), new Point(184, 63 + i * 34));
            }
        }
        return Raster(visual, 320, 180);
    }

    private static FormattedText Text(string value, double size, Brush brush) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), size, brush, 1);

    private static BitmapSource Render(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        return Raster(element, (int)Math.Ceiling(width), (int)Math.Ceiling(height));
    }

    private static BitmapSource Compare(BitmapSource expanded, BitmapSource collapsed)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 520, 248));
            dc.DrawText(Text("펼침 · 기존 크기", 16, Brushes.Black), new Point(20, 14));
            dc.DrawText(Text("접힘", 16, Brushes.Black), new Point(380, 14));
            dc.DrawImage(expanded, new Rect(20, 48, 320, 180));
            dc.DrawImage(collapsed, new Rect(380, 48, collapsed.Width, collapsed.Height));
        }
        return Raster(visual, 520, 248);
    }

    private static BitmapSource Joined(BitmapSource first, BitmapSource second)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(first, new Rect(0, 0, first.Width, first.Height));
            dc.DrawImage(second, new Rect(first.Width, 0, second.Width, second.Height));
        }
        return Raster(visual, (int)(first.Width + second.Width), (int)Math.Max(first.Height, second.Height));
    }

    private static BitmapSource Raster(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }
}
