using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

/// <summary>
/// One placed block of a picture translation as separate frozen layers in the source's native pixels: the surface that
/// covers the source text, and the translated text over it. Drawn in index order over the source they look exactly like
/// the composition's single layer. <see cref="Signature"/> names everything the placement chose, so a block placed
/// before the earlier blocks were known can be checked against the final placement.
/// </summary>
internal sealed record PinOverlayBlockLayer(int Index, Drawing Surface, Drawing? Text, System.Windows.Rect Bounds, string Signature);

internal static partial class PinTranslationOverlay
{
    /// <summary>What <see cref="Compose(PinOverlayPreparation, IReadOnlyList{PinOverlayTranslation}, bool, Action{PinOverlayBlock, PinOverlayFit, bool}?, Action{PinOverlayBlock, string}?, Action{PinOverlayBlockLayer}?)"/> draws for one block.</summary>
    private sealed record ComposedDraw(PinOverlayFit Fit, PinOverlayBackground[] Backgrounds, Color Color, PinOverlayBlock Block,
        double InkTop, double PitchScale, TextAlignment Alignment, Geometry? Clip, BackgroundSurface Surface)
    {
        internal int Index { get; init; }
        internal string? Signature { get; init; }
    }

    /// <summary>
    /// Draws one block: its surface, its text or both. Both parts in one call are the composition's own drawing; the parts
    /// alone are the same commands split at the text, each inside the block's clip.
    /// </summary>
    private static void DrawComposed(DrawingContext drawing, ComposedDraw draw, int width, int height, bool drawText,
        Action<PinOverlayBlock, PinOverlayFit, bool>? observeFit,
        Dictionary<(string Text, double Size, Color Color, double Pitch, TextAlignment Alignment, double Width, bool Underline), FormattedText> renderedText,
        bool surfacePart, bool textPart)
    {
        if (draw.Clip is not null) drawing.PushClip(draw.Clip);
        Color background = MedianColor(draw.Backgrounds.Select(value => value.Color).ToArray());
        Color foreground = draw.Color;
        if (surfacePart)
        {
            // Erase the complete source ink before putting any translucent surface over it.
            // Snap outwards to physical pixels so fractional OCR edges cannot leave antialiased remnants.
            for (int i = 0; i < draw.Block.Lines.Count; i++)
            {
                OcrRect erase = EraseBounds(draw.Block.Lines[i].Bounds, width, height);
                drawing.DrawRectangle(draw.Surface.Brush(erase, draw.Backgrounds[i].Color), null, Rect(erase));
            }
            foreach (OcrRect extra in draw.Surface.ExtraInk)
                drawing.DrawRectangle(draw.Surface.Brush(extra, background), null, Rect(extra));
        }
        bool needsPlate = draw.Backgrounds.Any(value => value.NeedsPlate || Contrast(value.Color, background) > 1.5
            || Contrast(foreground, value.Color) < 4.5);
        observeFit?.Invoke(draw.Block, draw.Fit, needsPlate);
        if (needsPlate)
        {
            (foreground, double opacity) = PlateStyle(background, foreground);
            if (surfacePart)
            {
                OcrRect outer = Inflate(draw.Fit.Bounds, Padding, width, height);
                drawing.DrawRoundedRectangle(draw.Surface.Brush(outer, background, opacity * .2), null, Rect(outer), 3, 3);
                OcrRect inner = Inflate(draw.Fit.Bounds, Padding - 1, width, height);
                drawing.DrawRoundedRectangle(draw.Surface.Brush(inner, background, opacity), null, Rect(inner), 2, 2);
            }
        }
        if (drawText && textPart)
        {
            var key = (draw.Fit.RenderText, draw.Fit.FontSize, foreground, draw.PitchScale, draw.Alignment,
                draw.Alignment == TextAlignment.Left ? 0 : draw.Fit.Bounds.Width, draw.Surface.Underline);
            if (!renderedText.TryGetValue(key, out FormattedText? text))
            {
                text = Format(draw.Fit.RenderText, draw.Fit.FontSize, new SolidColorBrush(foreground),
                    draw.PitchScale * draw.Fit.FontSize);
                if (draw.Surface.Underline) text.SetTextDecorations(TextDecorations.Underline);
                if (draw.Alignment != TextAlignment.Left)
                {
                    text.TextAlignment = draw.Alignment;
                    // Wrapping was already selected at the legal break points by Fit.
                    text.MaxTextWidth = draw.Fit.Bounds.Width + .02;
                }
                renderedText.Add(key, text);
            }
            Point origin = new(draw.Fit.Bounds.X, draw.Fit.Bounds.Y - draw.InkTop);
            if (draw.Fit.LineOffsets.Count > 0)
            {
                string[] lines = draw.Fit.RenderText.Split('\n');
                double pitch = draw.PitchScale * draw.Fit.FontSize;
                if (pitch <= 0) pitch = Format("Ag", draw.Fit.FontSize, Brushes.Black).Height;
                for (int i = 0; i < lines.Length; i++)
                {
                    // PlacementBounds includes the source line pitch in the first
                    // baseline. Keep it when rendering separate exclusion-aware lines,
                    // especially when the source ink cap makes the em much smaller.
                    var line = Format(lines[i], draw.Fit.FontSize, new SolidColorBrush(foreground), pitch);
                    if (draw.Surface.Underline) line.SetTextDecorations(TextDecorations.Underline);
                    drawing.DrawText(line, new Point(origin.X + draw.Fit.LineOffsets[i], origin.Y + i * pitch));
                }
            }
            else if (draw.Surface.LeadingInset > 0 && draw.Alignment == TextAlignment.Left)
            {
                int newline = draw.Fit.RenderText.IndexOf('\n');
                string first = newline < 0 ? draw.Fit.RenderText : draw.Fit.RenderText[..newline];
                double pitch = draw.PitchScale * draw.Fit.FontSize;
                var firstLine = Format(first, draw.Fit.FontSize, new SolidColorBrush(foreground), pitch);
                if (draw.Surface.Underline) firstLine.SetTextDecorations(TextDecorations.Underline);
                drawing.DrawText(firstLine, new Point(origin.X + draw.Surface.LeadingInset, origin.Y));
                if (newline >= 0)
                {
                    var rest = Format(draw.Fit.RenderText[(newline + 1)..], draw.Fit.FontSize, new SolidColorBrush(foreground), pitch);
                    if (draw.Surface.Underline) rest.SetTextDecorations(TextDecorations.Underline);
                    drawing.DrawText(rest, new Point(origin.X, origin.Y + (pitch > 0 ? pitch : firstLine.Height)));
                }
            }
            else drawing.DrawText(text, origin);
        }
        if (draw.Clip is not null) drawing.Pop();
    }

    /// <summary>Records each placed block's surface and text as their own frozen layers, after the composition's layer.</summary>
    private static void ObserveBlockLayers(List<ComposedDraw> draws, int width, int height, bool drawText,
        Dictionary<(string Text, double Size, Color Color, double Pitch, TextAlignment Alignment, double Width, bool Underline), FormattedText> renderedText,
        Action<PinOverlayBlockLayer> observe)
    {
        foreach (ComposedDraw draw in draws)
        {
            var surface = new DrawingGroup();
            using (DrawingContext drawing = surface.Open())
                DrawComposed(drawing, draw, width, height, drawText, null, renderedText, surfacePart: true, textPart: false);
            surface.Freeze();
            DrawingGroup? text = null;
            if (drawText)
            {
                text = new DrawingGroup();
                using (DrawingContext drawing = text.Open())
                    DrawComposed(drawing, draw, width, height, drawText, null, renderedText, surfacePart: false, textPart: true);
                text.Freeze();
            }
            System.Windows.Rect bounds = surface.Bounds;
            if (text is not null && !text.Bounds.IsEmpty) bounds = bounds.IsEmpty ? text.Bounds : System.Windows.Rect.Union(bounds, text.Bounds);
            observe(new PinOverlayBlockLayer(draw.Index, surface, text, bounds, draw.Signature ?? string.Empty));
        }
    }

    /// <summary>Everything a block's drawing follows from; <paramref name="obstacles"/> only when its surface was sampled again.</summary>
    private static string DrawSignature(PinOverlayFit fit, Color foreground, double inkTop, double pitchScale, TextAlignment alignment,
        OcrRect clipBounds, OcrRect[]? obstacles)
    {
        var signature = new StringBuilder();
        void Add(double value) => signature.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        void AddRect(OcrRect rect)
        {
            Add(rect.X);
            Add(rect.Y);
            Add(rect.Width);
            Add(rect.Height);
        }
        AddRect(fit.Bounds);
        Add(fit.FontSize);
        signature.Append(fit.Grew).Append('|').Append(fit.RenderText).Append('|');
        foreach (double offset in fit.LineOffsets) Add(offset);
        signature.Append('|').Append(foreground.ToString(CultureInfo.InvariantCulture)).Append('|');
        Add(inkTop);
        Add(pitchScale);
        signature.Append(alignment).Append('|');
        AddRect(clipBounds);
        if (obstacles is not null)
        {
            signature.Append("|s:");
            foreach (OcrRect obstacle in obstacles) AddRect(obstacle);
        }
        return signature.ToString();
    }
}
