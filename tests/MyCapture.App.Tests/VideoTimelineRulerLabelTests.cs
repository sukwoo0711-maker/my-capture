using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoTimelineRulerLabelTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>
    /// The start/end chips keep the gap labels keep between themselves: a label under a chip
    /// or closer to it than 6 DIP is omitted, every other label stays, and so does every tick.
    /// A label 1 to 2 DIP beside a chip read as part of it (2026-11-06 aesthetic review).
    /// </summary>
    [Theory]
    [InlineData(360)]
    [InlineData(1200)]
    public void RulerOmitsOnlyLabelsOverOrBesideBothChipsAndRetainsEveryTick(double width) => WithTimeline(width, timeline =>
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
        AssertChipGapRule(withoutChips, withChips, start, end, $"{width} DIP");
        Assert.Equal(staticBefore, overview.StaticDrawCount);
        GeometryDrawing[] afterTicks = Drawings(StaticDrawing(overview)).OfType<GeometryDrawing>()
            .Where(drawing => drawing.Geometry is LineGeometry).ToArray();
        Assert.Equal(ticks.Select(tick => tick.Bounds), afterTicks.Select(tick => tick.Bounds));
    });

    /// <summary>
    /// Moving the start chip across the labels in small steps: wherever its edge stops, a
    /// label it does not cover but comes within 6 DIP of disappears and comes back once the
    /// chip moves on. The sweep must meet such a beside-only case, or it proves nothing.
    /// </summary>
    [Fact]
    public void LabelsKeepTheLabelGapBesideAMovingStartChip() => WithTimeline(1200, timeline =>
    {
        TimelineRenderSurface overview = Surface(timeline);
        timeline.SetTrimMode(false);
        timeline.FlushRenderForTest();
        Dictionary<string, Rect> all = Labels(overview);
        timeline.SetTrimMode(true);
        int besideOnly = 0;
        for (double inMs = 500; inMs <= 4500; inMs += 20)
        {
            timeline.SetTrimRange(inMs, 10000);
            timeline.FlushRenderForTest();
            (Rect start, Rect end) = ChipBounds(timeline, overview.ActualWidth);
            besideOnly += AssertChipGapRule(all, Labels(overview), start, end, $"start {inMs} ms");
        }
        Assert.True(besideOnly > 0, "No step put the start chip within 6 DIP of a label it does not cover.");
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

    /// <summary>
    /// The labels sit on one even interval from 0:00, so whole seconds are never dropped for
    /// half seconds and no two labels crowd each other. A greedy skip of colliding labels
    /// gave strips about 700 DIP wide the run 0:00 0:00.5 0:01.5 0:02.5 ...
    /// </summary>
    [Theory]
    [InlineData(360)]
    [InlineData(520)]
    [InlineData(640)]
    [InlineData(720)]
    [InlineData(860)]
    [InlineData(1200)]
    public void RulerLabelsKeepOneEvenIntervalFromZero(double width) => WithTimeline(width, timeline =>
    {
        TimelineRenderSurface overview = Surface(timeline);
        timeline.SetTrimMode(false);
        timeline.FlushRenderForTest();
        KeyValuePair<string, Rect>[] labels = Labels(overview).OrderBy(label => label.Value.X).ToArray();
        Assert.True(labels.Length >= 3, $"{width} DIP: {labels.Length} labels");
        double[] times = labels.Select(label => TickMs(label.Key)).ToArray();
        double interval = times[1] - times[0];
        Assert.Equal(0d, times[0]);
        Assert.Contains(interval, new[] { 500d, 1_000, 2_000, 5_000, 10_000 });
        for (int index = 0; index < times.Length; index++)
            Assert.True(Math.Abs(times[index] - (index * interval)) < 0.5,
                $"{width} DIP: {string.Join(" ", labels.Select(label => label.Key))}");
        for (int index = 1; index < labels.Length; index++)
            Assert.True(labels[index].Value.Left >= labels[index - 1].Value.Right + 6 - 0.05,
                $"{width} DIP: {labels[index - 1].Key} and {labels[index].Key} are closer than 6 DIP");
    });

    /// <summary>
    /// A strip too narrow for a label on every major tick takes the next interval that fits:
    /// 40 s clips step from 2 s to 4 s labels and 200 s clips from 10 s to 20 s, not to the
    /// 10 s and 30 s that leave a narrow strip with four or five labels.
    /// </summary>
    [Theory]
    [InlineData(10_000, 1200, 500)]
    [InlineData(40_000, 1200, 2_000)]
    [InlineData(40_000, 360, 4_000)]
    [InlineData(200_000, 360, 20_000)]
    public void NarrowRulerTakesTheNextIntervalThatFits(double durationMs, double width, double expectedMs) => WithTimeline(width, timeline =>
    {
        TimelineRenderSurface overview = Surface(timeline);
        timeline.SetTrimMode(false);
        timeline.FlushRenderForTest();
        string[] labels = Labels(overview).OrderBy(label => label.Value.X).Select(label => label.Key).ToArray();
        double[] times = labels.Select(TickMs).ToArray();
        string shown = string.Join(" ", labels);
        Assert.True(times.Length >= 3, $"{durationMs} ms in {width} DIP: {shown}");
        Assert.Equal(expectedMs, times[1] - times[0]);
        for (int index = 0; index < times.Length; index++)
            Assert.True(Math.Abs(times[index] - (index * expectedMs)) < 0.5, $"{durationMs} ms in {width} DIP: {shown}");
    }, durationMs);

    /// <summary>
    /// The pointer's time and the snap caption are drawn in Font.Ui, the font of the chips and
    /// labels beside them. A fixed Segoe UI drew their digits in another face and their Korean
    /// in Malgun Gothic.
    /// </summary>
    [Fact]
    public void HoverTimeAndSnapCaptionUseTheUiFont() => WithTimeline(1200, timeline =>
    {
        TimelineRenderSurface overview = Surface(timeline);
        timeline.SetTrimMode(false);
        timeline.HoverForTest(overview.ActualWidth * 0.4, 40);
        timeline.FlushRenderForTest();
        Assert.False(string.IsNullOrEmpty(timeline.HoverText));
        GlyphRun[] hover = TransientRuns(overview);
        Assert.Equal(timeline.HoverText, string.Concat(hover.Select(Text)));
        Assert.All(hover, run => Assert.Equal(UiFontUri(Text(run)), run.GlyphTypeface.FontUri));

        timeline.ClearHoverForTest();
        timeline.SetSnapGuide(new VideoSnapCandidate(3_000, VideoSnapKind.Playhead));
        timeline.FlushRenderForTest();
        GlyphRun[] snap = TransientRuns(overview);
        Assert.Equal(UiText.Get("VideoEditorUx_SnapPlayhead").Replace(" ", string.Empty, StringComparison.Ordinal),
            string.Concat(snap.Select(Text)).Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.All(snap, run => Assert.Equal(UiFontUri(Text(run)), run.GlyphTypeface.FontUri));
    });

    /// <summary>
    /// Zoomed out to the whole clip, the detail strip labels one interval from 0:00 and its
    /// label chips keep 6 DIP apart; a label on every major tick ran 21 chips into each other on
    /// a 10 s clip. The frame-tick hint sits under the label chips instead of over them.
    /// </summary>
    [Theory]
    [InlineData(10_000, 360)]
    [InlineData(10_000, 720)]
    [InlineData(10_000, 1200)]
    [InlineData(60_000, 360)]
    [InlineData(200_000, 520)]
    public void ZoomedOutDetailStripKeepsLabelChipsApartAndTheHintBelowThem(double durationMs, double width) => WithTimeline(width, timeline =>
    {
        TimelineRenderSurface detail = DetailSurface(timeline);
        timeline.FitAll();
        timeline.FlushRenderForTest();
        Assert.True(timeline.IsFitAll);
        double stripWidth = detail.ActualWidth;
        var scrim = (Brush)typeof(TwoLineTimeline).GetField("_surfaceScrim", Private)!.GetValue(timeline)!;
        Drawing[] drawn = Drawings(StaticDrawing(detail)).ToArray();
        string[] labels = drawn.OfType<GlyphRunDrawing>().Where(run => run.GlyphRun.FontRenderingEmSize == TypeScale.Micro)
            .OrderBy(run => run.GlyphRun.BaselineOrigin.X).Select(run => Text(run.GlyphRun)).ToArray();
        Rect[] chips = drawn.OfType<GeometryDrawing>().Where(drawing => ReferenceEquals(drawing.Brush, scrim))
            .Select(drawing => drawing.Bounds).OrderBy(bounds => bounds.Left).ToArray();
        Rect[] labelChips = chips.Where(bounds => Math.Abs(bounds.Top - 2) < 0.05).ToArray();
        string shown = $"{durationMs} ms in {stripWidth} DIP: {string.Join(" ", labels)}";

        Assert.True(labels.Length >= 3, shown);
        Assert.Equal(labels.Length, labelChips.Length);
        double[] times = labels.Select(TickMs).ToArray();
        double interval = times[1] - times[0];
        Assert.Equal(0d, times[0]);
        Assert.True(interval % timeline.CoarseIntervalMs < 0.5, shown);
        for (int index = 0; index < times.Length; index++)
            Assert.True(Math.Abs(times[index] - (index * interval)) < 0.5, shown);
        for (int index = 1; index < labelChips.Length; index++)
            Assert.True(labelChips[index].Left >= labelChips[index - 1].Right + 6 - 0.05,
                $"{shown}: {labels[index - 1]} and {labels[index]} are closer than 6 DIP");
        Assert.All(labelChips, bounds => Assert.True(bounds.Left >= 3 - 0.05 && bounds.Right <= stripWidth - 3 + 0.05, shown));

        // No line drawn after a chip crosses it: the unlabelled ticks inside a label's width used
        // to cut through its text, and every major tick crossed the zoom hint.
        for (int index = 0; index < drawn.Length; index++)
        {
            if (drawn[index] is not GeometryDrawing chip || !ReferenceEquals(chip.Brush, scrim)) continue;
            Rect box = chip.Bounds;
            foreach (GeometryDrawing line in drawn.Skip(index + 1).OfType<GeometryDrawing>().Where(drawing => drawing.Geometry is LineGeometry))
            {
                Rect span = line.Geometry.Bounds;
                Assert.False(span.Left > box.Left + 0.5 && span.Left < box.Right - 0.5 && span.Top < box.Bottom && span.Bottom > box.Top,
                    $"{shown}: a line at x {span.Left:0.##} crosses the chip {box.Left:0.##}–{box.Right:0.##} drawn before it");
            }
        }

        // 30 fps frames closer than 2 px leave the frame ticks to the zoom hint.
        Rect[] hintChips = chips.Where(bounds => bounds.Top > 2.5).ToArray();
        string hint = string.Concat(drawn.OfType<GlyphRunDrawing>().Where(run => run.GlyphRun.FontRenderingEmSize == TypeScale.Caption)
            .Select(run => Text(run.GlyphRun))).Replace(" ", string.Empty, StringComparison.Ordinal);
        if ((1000.0 / 30) / timeline.VisibleSpanMs * stripWidth < 2.0)
        {
            Assert.Equal(UiText.Get("Text_D9483E35CD83").Replace(" ", string.Empty, StringComparison.Ordinal), hint);
            Rect hintChip = Assert.Single(hintChips);
            Assert.True(hintChip.Top >= labelChips.Max(bounds => bounds.Bottom) - 0.05,
                $"{shown}: hint top {hintChip.Top:0.##} over label bottom {labelChips.Max(bounds => bounds.Bottom):0.##}");
            Assert.True(hintChip.Bottom <= detail.ActualHeight + 0.05, shown);
        }
        else
        {
            Assert.Equal(string.Empty, hint);
            Assert.Empty(hintChips);
        }
    }, durationMs, detail: true);

    /// <summary>
    /// A range caption that does not fit ends in an ellipsis; a narrow window cut it mid-word.
    /// </summary>
    [Fact]
    public void RangeCaptionsEndInAnEllipsisWhenTheyDoNotFit() => WithTimeline(360, timeline =>
    {
        foreach (string name in new[] { "_overviewCaption", "_detailCaption" })
        {
            var caption = (TextBlock)typeof(TwoLineTimeline).GetField(name, Private)!.GetValue(timeline)!;
            Assert.Equal(TextTrimming.CharacterEllipsis, caption.TextTrimming);
        }
    }, detail: true);

    private static double TickMs(string label)
    {
        string[] parts = label.Split(':');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture) * 60_000)
            + (double.Parse(parts[1], CultureInfo.InvariantCulture) * 1_000);
    }

    /// <summary>The text of the transient layer (playhead, pointer and snap feedback).</summary>
    private static GlyphRun[] TransientRuns(TimelineRenderSurface surface) =>
        Drawings(VisualTreeHelper.GetDrawing((Visual)VisualTreeHelper.GetChild(surface, 2)))
            .OfType<GlyphRunDrawing>().Select(drawing => drawing.GlyphRun).ToArray();

    /// <summary>The font file that Font.Ui Regular draws <paramref name="text"/> from.</summary>
    private static Uri UiFontUri(string text)
    {
        var probe = new DrawingGroup();
        using (DrawingContext dc = probe.Open())
        {
            dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(ThemeFonts.Ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                TypeScale.Caption, Brushes.Black, 1), new Point());
        }
        return Drawings(probe).OfType<GlyphRunDrawing>().First().GlyphRun.GlyphTypeface.FontUri;
    }

    private static string Text(GlyphRun run) => run.Characters is { } characters ? new string(characters.ToArray()) : string.Empty;

    private static void WithTimeline(double width, Action<TwoLineTimeline> action, double durationMs = 10000, bool detail = false) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using var timeline = new TwoLineTimeline { Width = width };
        timeline.Initialize(durationMs, 30);
        timeline.SetDetailVisible(detail);
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

    private static TimelineRenderSurface DetailSurface(TwoLineTimeline timeline) =>
        (TimelineRenderSurface)typeof(TwoLineTimeline).GetField("_detail", Private)!.GetValue(timeline)!;

    private const double LabelGap = 6;

    /// <summary>
    /// A label is drawn exactly when it keeps 6 DIP from both chips. Labels within 0.05 DIP
    /// of that boundary are left out of the check: the glyph run's advance width and the
    /// renderer's FormattedText width may differ in their last digits. Returns how many
    /// labels were omitted only for the gap, beside a chip they do not touch.
    /// </summary>
    private static int AssertChipGapRule(Dictionary<string, Rect> all, Dictionary<string, Rect> shown,
        Rect start, Rect end, string scene)
    {
        int besideOnly = 0;
        foreach ((string text, Rect bounds) in all)
        {
            double gap = Math.Min(ChipGap(bounds, start), ChipGap(bounds, end));
            if (Math.Abs(gap - LabelGap) < 0.05) continue;
            bool kept = gap >= LabelGap;
            Assert.True(kept == shown.ContainsKey(text),
                $"{scene}: {text} is {gap:0.##} DIP from a chip and should {(kept ? "stay" : "be omitted")}");
            if (gap > 0 && !kept) besideOnly++;
        }
        Assert.All(shown, label => Assert.True(Math.Min(ChipGap(label.Value, start), ChipGap(label.Value, end)) > LabelGap - 0.05,
            $"{scene}: {label.Key} is drawn closer than 6 DIP to a chip"));
        return besideOnly;
    }

    /// <summary>The horizontal gap between a label and a chip; zero or less when they touch or overlap.</summary>
    private static double ChipGap(Rect label, Rect chip) =>
        label.Left >= chip.Right ? label.Left - chip.Right
        : chip.Left >= label.Right ? chip.Left - label.Right
        : -Math.Min(label.Right, chip.Right) + Math.Max(label.Left, chip.Left);

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
