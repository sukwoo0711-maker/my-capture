using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoTimelineRulerLabelTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(360)]
    [InlineData(1200)]
    public void RulerOmitsOnlyLabelsOverBothChipsAndRetainsEveryTick(double width) => WithTimeline(width, timeline =>
    {
        TimelineRenderSurface overview = Surface(timeline);
        timeline.SetTrimMode(false);
        timeline.FlushRenderForTest();
        Dictionary<string, Rect> withoutChips = Labels(overview);
        GeometryDrawing[] ticks = Drawings(StaticDrawing(overview)).OfType<GeometryDrawing>()
            .Where(drawing => drawing.Geometry is LineGeometry).ToArray();
        Assert.NotEmpty(ticks);
        long staticBefore = overview.StaticDrawCount;

        timeline.SetTrimMode(true);
        timeline.FlushRenderForTest();
        Dictionary<string, Rect> withChips = Labels(overview);
        (Rect start, Rect end) = ChipBounds(timeline, overview.ActualWidth);
        Assert.Contains(withoutChips, label => label.Value.IntersectsWith(start));
        Assert.Contains(withoutChips, label => label.Value.IntersectsWith(end));
        Assert.NotEmpty(withChips);
        foreach ((string text, Rect bounds) in withoutChips)
        {
            if (bounds.IntersectsWith(start) || bounds.IntersectsWith(end)) Assert.DoesNotContain(text, withChips.Keys);
            else Assert.Contains(text, withChips.Keys);
        }
        Assert.All(withChips.Values, bounds =>
        {
            Assert.False(bounds.IntersectsWith(start));
            Assert.False(bounds.IntersectsWith(end));
        });
        Assert.Equal(staticBefore, overview.StaticDrawCount);
        GeometryDrawing[] afterTicks = Drawings(StaticDrawing(overview)).OfType<GeometryDrawing>()
            .Where(drawing => drawing.Geometry is LineGeometry).ToArray();
        Assert.Equal(ticks.Select(tick => tick.Bounds), afterTicks.Select(tick => tick.Bounds));
    });

    [Theory]
    [InlineData(360)]
    [InlineData(1200)]
    public void MovingChipsRefreshesRulerLabelsWithoutRedrawingStaticTicks(double width) => WithTimeline(width, timeline =>
    {
        TimelineRenderSurface overview = Surface(timeline);
        timeline.FlushRenderForTest();
        Assert.DoesNotContain("0:00", Labels(overview).Keys);
        long staticBefore = overview.StaticDrawCount;
        long rangeBefore = overview.RangeDrawCount;
        Assert.True(timeline.SetTrimRange(4000, 6000));
        timeline.FlushRenderForTest();
        Dictionary<string, Rect> changed = Labels(overview);
        Assert.Contains("0:00", changed.Keys);
        (Rect start, Rect end) = ChipBounds(timeline, overview.ActualWidth);
        Assert.All(changed.Values, bounds =>
        {
            Assert.False(bounds.IntersectsWith(start));
            Assert.False(bounds.IntersectsWith(end));
        });
        Assert.Equal(staticBefore, overview.StaticDrawCount);
        Assert.Equal(rangeBefore + 1, overview.RangeDrawCount);
        Assert.True(timeline.SetTrimRange(0, 10000));
        timeline.FlushRenderForTest();
        Assert.DoesNotContain("0:00", Labels(overview).Keys);
    });

    private static void WithTimeline(double width, Action<TwoLineTimeline> action) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using var timeline = new TwoLineTimeline { Width = width };
        timeline.Initialize(10000, 30);
        timeline.SetDetailVisible(false);
        var host = new Window
        {
            Content = timeline, Width = width + 40, Height = 260,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            host.Show();
            host.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(host.UpdateLayout));
            timeline.FlushRenderForTest();
            action(timeline);
        }
        finally { host.Close(); }
    });

    private static TimelineRenderSurface Surface(TwoLineTimeline timeline) =>
        (TimelineRenderSurface)typeof(TwoLineTimeline).GetField("_overview", Private)!.GetValue(timeline)!;

    private static (Rect Start, Rect End) ChipBounds(TwoLineTimeline timeline, double width)
    {
        object chips = typeof(TwoLineTimeline).GetMethod("LayoutChips", Private)!.Invoke(timeline, [width])!;
        return ((Rect)chips.GetType().GetProperty("In")!.GetValue(chips)!, (Rect)chips.GetType().GetProperty("Out")!.GetValue(chips)!);
    }

    private static Dictionary<string, Rect> Labels(TimelineRenderSurface surface) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(surface))
            .SelectMany(index => Drawings(VisualTreeHelper.GetDrawing((Visual)VisualTreeHelper.GetChild(surface, index))))
            .OfType<GlyphRunDrawing>().Where(drawing => drawing.GlyphRun.FontRenderingEmSize == TypeScale.Micro)
            // The renderer excludes the full label width, including glyph side bearings.
            // Read that advance width from the actual run instead of its smaller ink bounds.
            .ToDictionary(drawing => new string(drawing.GlyphRun.Characters!.ToArray()), drawing =>
                new Rect(drawing.GlyphRun.BaselineOrigin.X, 2,
                    drawing.GlyphRun.AdvanceWidths.Sum(), drawing.GlyphRun.FontRenderingEmSize));

    private static DrawingGroup StaticDrawing(TimelineRenderSurface surface) =>
        VisualTreeHelper.GetDrawing((Visual)VisualTreeHelper.GetChild(surface, 0));

    private static IEnumerable<Drawing> Drawings(Drawing? drawing)
    {
        if (drawing is null) yield break;
        if (drawing is DrawingGroup group)
        {
            foreach (Drawing child in group.Children)
                foreach (Drawing nested in Drawings(child)) yield return nested;
        }
        else yield return drawing;
    }
}
