using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class TimelineFeedbackPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MarkerCachesLayoutAndFrozenPenUntilTheirInputsChange() => StaTestHost.Run(() =>
    {
        var marker = new TimelineVisuals.Marker();
        FormattedText text = marker.GetLabel("0:05.000", Brushes.White, 1, 388);
        Assert.Same(text, marker.GetLabel("0:05.000", Brushes.White, 1, 388));
        Assert.NotSame(text, text = marker.GetLabel("0:06.000", Brushes.White, 1, 388));
        Assert.NotSame(text, text = marker.GetLabel("0:06.000", Brushes.White, 1.5, 388));
        Assert.NotSame(text, text = marker.GetLabel("0:06.000", Brushes.White, 1.5, 288));
        Assert.NotSame(text, text = marker.GetLabel("0:06.000", Brushes.Black, 1.5, 288));
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = original.Name == "en-US" ? new("ko-KR") : new("en-US");
            Assert.NotSame(text, marker.GetLabel("0:06.000", Brushes.Black, 1.5, 288));
        }
        finally { CultureInfo.CurrentUICulture = original; }

        var owner = new FrameworkElement();
        var visual = new DrawingVisual();
        Draw(Brushes.Gray);
        Pen first = Field<Pen>(marker, "_pen");
        Assert.True(first.IsFrozen);
        Draw(Brushes.Gray);
        Assert.Same(first, Field<Pen>(marker, "_pen"));
        Draw(Brushes.White);
        Assert.NotSame(first, Field<Pen>(marker, "_pen"));
        Assert.True(Field<Pen>(marker, "_pen").IsFrozen);
        var mutableBrush = new SolidColorBrush(Colors.Gray);
        Draw(mutableBrush);
        Assert.False(mutableBrush.IsFrozen);
        Assert.True(Field<Pen>(marker, "_pen").IsFrozen);

        void Draw(Brush line)
        {
            using DrawingContext dc = visual.RenderOpen();
            marker.Draw(owner, dc, 200, 40, 0, 400, line, Brushes.Black, Brushes.White, "0:05.000");
        }
    });

    [Fact]
    public void StationaryHoverReusesTimecodeStrings() => StaTestHost.Run(() =>
    {
        using var timeline = new TwoLineTimeline();
        var layers = new VideoLayerTimeline();
        timeline.Initialize(10000, 30);
        layers.Initialize(10000);
        timeline.SetHover(5000);
        layers.SetHover(5000);
        string mainText = timeline.HoverText;
        string layerText = layers.HoverText;
        for (int i = 0; i < 400; i++)
        {
            timeline.SetHover(5000);
            layers.SetHover(5000);
            timeline.SetPlayhead(i * 20, ensureVisible: false);
            layers.SetPlayhead(i * 20);
            Assert.Same(mainText, timeline.HoverText);
            Assert.Same(layerText, layers.HoverText);
        }
        timeline.SetHover(null);
        layers.SetHover(null);
        Assert.Empty(timeline.HoverText);
        Assert.Empty(layers.HoverText);
    });

    [Fact]
    public void SnapCandidatesStayStableDuringTheGestureAndRefreshForTheNextOne() => StaTestHost.Run(() =>
    {
        var target = new TimedTextOverlay { StartMs = 1000, EndMs = 2000 };
        var otherText = new TimedTextOverlay { StartMs = 3000, EndMs = 4000 };
        var frame = new FrameEditLayer { StartMs = 5000, EndMs = 6000 };
        var timeline = new VideoLayerTimeline();
        timeline.Initialize(10000);
        timeline.SetKeptRange(500, 9500);
        timeline.SetLayers([target, otherText], [frame]);
        SetField(timeline, "_dragTargetId", target.Id);
        SetField(timeline, "_dragPlayheadMs", 4500d);
        Action prepare = Method(timeline, "PrepareSnapCandidates").CreateDelegate<Action>(timeline);
        prepare();
        List<VideoSnapCandidate> candidates = Field<List<VideoSnapCandidate>>(timeline, "_dragSnapCandidates");
        Assert.Equal(new VideoSnapCandidate[]
        {
            new(4500, VideoSnapKind.Playhead), new(500, VideoSnapKind.KeptStart), new(9500, VideoSnapKind.KeptEnd),
            new(3000, VideoSnapKind.LayerEdge), new(4000, VideoSnapKind.LayerEdge),
            new(5000, VideoSnapKind.LayerEdge), new(6000, VideoSnapKind.LayerEdge),
        }, candidates);
        target.StartMs = 1200;
        otherText.StartMs = 3200;
        Assert.Equal(3000, candidates[3].TimeMs);
        prepare();
        Assert.Same(candidates, Field<List<VideoSnapCandidate>>(timeline, "_dragSnapCandidates"));
        Assert.Equal(3200, candidates[3].TimeMs);
        Assert.DoesNotContain(candidates, item => item.TimeMs == 1200);
    });

    [Fact]
    public void RepeatedMarkerDrawsAllocateLessThanTheUncachedRenderer() => StaTestHost.Run(() =>
    {
        var owner = new FrameworkElement();
        var marker = new TimelineVisuals.Marker();
        var visual = new DrawingVisual();
        string label = VideoTimecode.Format(5000);
        Action before = () =>
        {
            using DrawingContext dc = visual.RenderOpen();
            DrawOriginalMarker(owner, dc, VideoTimecode.Format(5000));
        };
        Action after = () =>
        {
            using DrawingContext dc = visual.RenderOpen();
            marker.Draw(owner, dc, 200, 40, 0, 400, Brushes.Gray, Brushes.Black, Brushes.White, label);
        };
        Compare("stationary labeled marker, 400 DIP, 96 DPI", before, after);
    });

    [Fact]
    public void CachedDragCandidatesEliminatePerUpdateAllocations() => StaTestHost.Run(() =>
    {
        const int layerCount = 24;
        var textLayers = Enumerable.Range(0, layerCount).Select(i =>
            new TimedTextOverlay { StartMs = 1000 + i * 250, EndMs = 2000 + i * 250 }).ToArray();
        var timeline = new VideoLayerTimeline();
        timeline.Initialize(10000);
        timeline.SetLayers(textLayers, []);
        timeline.Measure(new Size(482, double.PositiveInfinity));
        timeline.Arrange(new Rect(0, 0, 482, timeline.DesiredSize.Height));
        SetField(timeline, "_dragTargetId", textLayers[0].Id);
        SetField(timeline, "_dragInitialStartMs", 1000d);
        SetField(timeline, "_dragInitialEndMs", 2000d);
        SetField(timeline, "_dragPlayheadMs", 5000d);
        Method(timeline, "PrepareSnapCandidates").CreateDelegate<Action>(timeline)();
        ApplyDrag apply = Method(timeline, "ApplyDrag").CreateDelegate<ApplyDrag>(timeline);
        double beforeStart = 0, afterStart = 0;
        Action before = () =>
        {
            // Original candidate construction, with the same keyboard query and invalidation as ApplyDrag.
            // Both paths share the indexed snap math, so the baseline omits its former enumerator allocation.
            var candidates = new List<VideoSnapCandidate>
            {
                new(5000, VideoSnapKind.Playhead), new(0, VideoSnapKind.KeptStart), new(10000, VideoSnapKind.KeptEnd),
            };
            foreach (TimedTextOverlay layer in textLayers)
            {
                if (layer.Id == textLayers[0].Id) continue;
                candidates.Add(new(layer.StartMs, VideoSnapKind.LayerEdge));
                candidates.Add(new(layer.EndMs, VideoSnapKind.LayerEdge));
            }
            beforeStart = VideoLayerSnapping.Apply(1000, 2000, 990, 10000, VideoLayerDrag.Move,
                timeline.Viewport, timeline.ActualWidth - VideoLayerTimeline.LabelWidth, candidates,
                (Keyboard.Modifiers & ModifierKeys.Alt) != 0).StartMs;
            timeline.InvalidateVisual();
        };
        Action after = () => apply(out afterStart, out _, 990);
        Compare("layer body drag, 24 text layers, 49 snap candidates, 400 DIP", before, after, zeroAfter: true);
        Assert.Equal(beforeStart, afterStart);
    });

    private void Compare(string workload, Action before, Action after, bool zeroAfter = false)
    {
        const int iterations = 400;
        const int repetitions = 7;
        for (int i = 0; i < 100; i++) { before(); after(); }
        var beforeSamples = new Measurement[repetitions];
        var afterSamples = new Measurement[repetitions];
        for (int repeat = 0; repeat < repetitions; repeat++)
        {
            beforeSamples[repeat] = Measure(before, iterations);
            afterSamples[repeat] = Measure(after, iterations);
        }
        output.WriteLine($"{workload}; warmup=100; iterations={iterations}; repetitions={repetitions}");
        Report("before", beforeSamples);
        Report("after", afterSamples);
        for (int repeat = 0; repeat < repetitions; repeat++)
        {
            Assert.True(afterSamples[repeat].Bytes < beforeSamples[repeat].Bytes,
                $"{workload}: before={beforeSamples[repeat].Bytes}, after={afterSamples[repeat].Bytes}");
            if (zeroAfter) Assert.Equal(0, afterSamples[repeat].Bytes);
        }

        void Report(string name, Measurement[] samples)
        {
            long[] bytes = samples.Select(sample => sample.Bytes).Order().ToArray();
            double[] ms = samples.Select(sample => sample.Milliseconds).Order().ToArray();
            output.WriteLine($"{name}: allocated bytes median={bytes[repetitions / 2]}, worst={bytes[^1]}; " +
                $"milliseconds median={ms[repetitions / 2]:F3}, worst={ms[^1]:F3}");
        }
    }

    private static Measurement Measure(Action action, int iterations)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) action();
        return new(GC.GetAllocatedBytesForCurrentThread() - bytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    // The marker renderer before the release-readiness fix, kept as an in-process allocation baseline.
    private static void DrawOriginalMarker(FrameworkElement owner, DrawingContext dc, string label)
    {
        dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(200, 0), new Point(200, 40));
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, Brushes.White, VisualTreeHelper.GetDpi(owner).PixelsPerDip)
        {
            MaxTextWidth = 388, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        double width = text.Width + 10;
        dc.DrawRoundedRectangle(Brushes.Black, new Pen(Brushes.Gray, 1), new Rect(205, 1, width, text.Height + 4), 3, 3);
        dc.DrawText(text, new Point(210, 3));
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    private static void SetField<T>(object owner, string name, T value) => owner.GetType().GetField(name, PrivateInstance)!.SetValue(owner, value);
    private static MethodInfo Method(object owner, string name) => owner.GetType().GetMethod(name, PrivateInstance)!;
    private delegate void ApplyDrag(out double startMs, out double endMs, double deltaMs);
    private readonly record struct Measurement(long Bytes, double Milliseconds);
}
