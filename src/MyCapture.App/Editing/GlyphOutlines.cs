using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Editing;

/// <summary>
/// <see cref="FormattedText.BuildGeometry"/> for a caption whose layout changes a little at a
/// time. BuildGeometry traces every glyph again for each new layout, and a caption being typed
/// has a new layout after every keystroke although most of its glyphs stay where they were. Each
/// glyph is traced as <see cref="GlyphRun.BuildGeometry"/> traces it, at its place in the layout,
/// and kept under that place for the next layout. The outline holds the figures BuildGeometry
/// gives, in the same order, and a keystroke traces only the glyphs it added or moved.
/// </summary>
internal static class GlyphOutlines
{
    /// <summary>One glyph's outline at its place in a layout.</summary>
    internal readonly record struct Key(GlyphTypeface Typeface, ushort Glyph, double EmSize, float PixelsPerDip, double X, double Y);

    /// <summary>
    /// The outline of the glyph runs <paramref name="glyphs"/> recorded from
    /// <paramref name="formatted"/> at <paramref name="origin"/>.
    /// </summary>
    /// <param name="kept">
    /// The glyph outlines of the caption's previous layout, replaced by this layout's; set to
    /// <see langword="null"/> when the layout holds anything but left-to-right glyph runs, which
    /// <paramref name="formatted"/> then outlines whole.
    /// </param>
    internal static Geometry Build(DrawingGroup glyphs, FormattedText formatted, Point origin, ref Dictionary<Key, Geometry>? kept)
    {
        var used = new Dictionary<Key, Geometry>(kept?.Count ?? 0);
        GeometryGroup? outline = null;
        if (!Collect(glyphs, kept, used, ref outline))
        {
            kept = null;
            return formatted.BuildGeometry(origin);
        }

        kept = used;
        return outline is null || outline.IsEmpty() ? Geometry.Empty : outline;
    }

    private static bool Collect(Drawing drawing, Dictionary<Key, Geometry>? kept, Dictionary<Key, Geometry> used, ref GeometryGroup? outline)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                if (group.Transform is { } transform && !transform.Value.IsIdentity) return false;
                foreach (Drawing child in group.Children)
                {
                    if (!Collect(child, kept, used, ref outline)) return false;
                }

                return true;
            case GlyphRunDrawing { GlyphRun: null }:
                return true;
            case GlyphRunDrawing { GlyphRun: { } run }:
                if (!TryOutline(run, kept, used, out GeometryGroup? runOutline)) return false;
                if (runOutline is not null)
                {
                    outline ??= new GeometryGroup { FillRule = FillRule.Nonzero };
                    outline.Children.Add(runOutline);
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>The run's outline as <see cref="GlyphRun.BuildGeometry"/> builds it, one glyph at a time.</summary>
    private static bool TryOutline(GlyphRun run, Dictionary<Key, Geometry>? kept, Dictionary<Key, Geometry> used, out GeometryGroup? outline)
    {
        outline = null;
        // Right-to-left and sideways runs place their glyphs differently; they keep the run's own geometry.
        if (run.IsSideways || (run.BidiLevel & 1) != 0) return false;
        if (run.GlyphOffsets is { } offsets)
        {
            foreach (Point offset in offsets)
            {
                if (offset.X != 0 || offset.Y != 0) return false;
            }
        }

        IList<ushort> indices = run.GlyphIndices;
        IList<double> advances = run.AdvanceWidths;
        Point baseline = run.BaselineOrigin;
        double advance = 0;
        for (int i = 0; i < indices.Count; i++)
        {
            double x = baseline.X + advance;
            advance += advances[i];
            var key = new Key(run.GlyphTypeface, indices[i], run.FontRenderingEmSize, run.PixelsPerDip, x, baseline.Y);
            if (!used.TryGetValue(key, out Geometry? glyph))
            {
                if (kept is null || !kept.TryGetValue(key, out glyph)) glyph = Trace(run, indices[i], advances[i], new Point(x, baseline.Y));
                used[key] = glyph;
            }

            if (glyph.IsEmpty()) continue;
            outline ??= new GeometryGroup { FillRule = FillRule.Nonzero };
            outline.Children.Add(glyph);
        }

        return true;
    }

    /// <summary>One glyph's outline, traced by a run of that glyph alone at its place.</summary>
    private static Geometry Trace(GlyphRun run, ushort index, double advance, Point origin)
    {
        var alone = new GlyphRun(run.GlyphTypeface, 0, false, run.FontRenderingEmSize, run.PixelsPerDip,
            new[] { index }, origin, new[] { advance }, null, null, null, null, null, null);
        Geometry traced = alone.BuildGeometry();
        if (traced.CanFreeze) traced.Freeze();
        // The run's own geometry holds each glyph's outline directly, not in a group of one.
        return traced is GeometryGroup { Children.Count: 1 } group ? group.Children[0] : traced;
    }
}
