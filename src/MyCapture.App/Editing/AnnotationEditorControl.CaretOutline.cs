using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

/// <summary>
/// The live caption's caret. The box is clear, so a caret in one colour vanishes over a picture of
/// that colour: a white caption typed over a white page. The caret is white or black, whichever the
/// caption's outline colour is not, and a bar in the outline colour, <see cref="CaretOutlineDip"/>
/// larger than the caret on every side, lies under the glyphs at the caret and blinks with it. One
/// of the two colours stands out from any picture, and the caret is outlined as the glyphs are.
/// The box keeps keyboard focus while it is open (it commits when focus leaves), so the outline
/// does not follow focus.
/// </summary>
internal sealed partial class AnnotationEditorControl
{
    /// <summary>How far the caret's outline reaches past the caret on each side, in DIP.</summary>
    internal const double CaretOutlineDip = 1;

    /// <summary>The live box's layer, under its text, that holds the caret outline.</summary>
    internal const string CaretOutlineLayerName = "CaretOutlineLayer";

    private Rectangle? _caretOutline;
    private Rect _caretOutlineBounds = Rect.Empty;
    private bool _caretOutlineStale;

    private void AttachCaretOutline(ScrollViewer host)
    {
        if (host.Template?.FindName(CaretOutlineLayerName, host) is not Canvas layer) return;
        if (layer.Children.Count == 0)
        {
            layer.Children.Add(new Rectangle
            {
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                Visibility = Visibility.Collapsed,
            });
        }

        _caretOutline = (Rectangle)layer.Children[0];
        _caretOutlineBounds = Rect.Empty;
        _caretOutlineStale = true;
        SyncCaretInk();
    }

    /// <summary>Stops and forgets the outline of the box that closes.</summary>
    private void DetachCaretOutline()
    {
        if (_caretOutline is not { } outline) return;
        // A blink that repeats forever would keep the animation clock running after the box is gone.
        outline.BeginAnimation(UIElement.OpacityProperty, null);
        outline.Visibility = Visibility.Collapsed;
        _caretOutline = null;
        _caretOutlineBounds = Rect.Empty;
    }

    /// <summary>
    /// The caret's colours for <paramref name="text"/>: the core, which the box draws, and the ring
    /// in the caption's outline colour around it. Both are opaque.
    /// </summary>
    internal static (SolidColorBrush Core, SolidColorBrush Ring) CaretBrushes(TextAnnotation text)
    {
        ColorRgba outline = AnnotationRenderer.OutlineColor(text);
        bool darkRing = (0.299 * outline.R) + (0.587 * outline.G) + (0.114 * outline.B) < 128;
        return darkRing ? (Brushes.White, Brushes.Black) : (Brushes.Black, Brushes.White);
    }

    /// <summary>Gives the box's caret and its ring the colours of the caption being edited.</summary>
    private void SyncCaretInk()
    {
        if (_activeTextBox is not { } box || _editingText is not { } editing) return;
        (SolidColorBrush core, SolidColorBrush ring) = CaretBrushes(editing);
        if (!ReferenceEquals(box.CaretBrush, core)) box.CaretBrush = core;
        if (_caretOutline is { } outline && !ReferenceEquals(outline.Fill, ring)) outline.Fill = ring;
    }

    /// <summary>Places the outline after a layout pass that followed a caret, text or scroll change.</summary>
    private void UpdateCaretOutline(TextBox box)
    {
        if (!_caretOutlineStale || _caretOutline is not { } outline || !ReferenceEquals(_activeTextBox, box)) return;
        if (VisualTreeHelper.GetParent(outline) is not UIElement layer) return;
        // Until the text view has laid out the caret's line, try again after the next layout pass.
        if (CaretOutlineBounds(box, layer) is not { } bounds) return;
        _caretOutlineStale = false;
        if (bounds.IsEmpty)
        {
            outline.Visibility = Visibility.Collapsed;
            _caretOutlineBounds = Rect.Empty;
            return;
        }

        if (outline.Visibility == Visibility.Visible && bounds == _caretOutlineBounds) return;
        _caretOutlineBounds = bounds;
        Canvas.SetLeft(outline, bounds.X);
        Canvas.SetTop(outline, bounds.Y);
        outline.Width = bounds.Width;
        outline.Height = bounds.Height;
        outline.Visibility = Visibility.Visible;
        // Like the caret, the outline shows at once wherever the caret moves to.
        RestartCaretOutlineBlink(outline);
    }

    /// <summary>
    /// The caret's outline in <paramref name="layer"/>'s coordinates, kept within
    /// <see cref="CaretOutlineDip"/> of the text view; <see cref="Rect.Empty"/> while the box shows
    /// no plain caret; <see langword="null"/> while the text view has not laid out the caret's line.
    /// A selection hides the caret, and an IME's interim block (a Korean syllable being composed)
    /// selects the syllable. At a soft line break the caret may stand at the end of the line above
    /// or at the start of the next one, and the box does not say which, so there the outline is
    /// left out rather than drawn on the wrong line.
    /// </summary>
    internal static Rect? CaretOutlineBounds(TextBox box, UIElement layer)
    {
        if (box.SelectionLength != 0) return Rect.Empty;
        int index = box.CaretIndex;
        Rect caret = box.GetRectFromCharacterIndex(index);
        if (caret.IsEmpty || !double.IsFinite(caret.X) || !double.IsFinite(caret.Y) || !(caret.Height > 0)) return null;
        string text = box.Text;
        if (index > 0 && index <= text.Length && text[index - 1] is not ('\n' or '\r'))
        {
            Rect before = box.GetRectFromCharacterIndex(index - 1, trailingEdge: true);
            if (before.IsEmpty) return null;
            if (Math.Abs(before.Top - caret.Top) > 0.5) return Rect.Empty;
        }

        // The caret WPF draws there: the system caret width centred on the insertion point, as tall
        // as the font's line spacing and standing on the bottom of its line.
        double height = Math.Min(caret.Height, box.FontFamily.LineSpacing * box.FontSize);
        double width = SystemParameters.CaretWidth;
        Point topLeft = box.TranslatePoint(
            new Point(caret.X - (width / 2) - CaretOutlineDip, caret.Bottom - height - CaretOutlineDip), layer);
        var outline = new Rect(topLeft, new Size(width + (2 * CaretOutlineDip), height + (2 * CaretOutlineDip)));
        // The layer has the text view's bounds. The outline may reach CaretOutlineDip past them, into
        // the box's padding, so a caret as tall as the view stays outlined at both ends; a caret on a
        // line the box has scrolled partly out of view keeps only that much of its outline outside.
        var shown = new Rect(-CaretOutlineDip, -CaretOutlineDip,
            layer.RenderSize.Width + (2 * CaretOutlineDip), layer.RenderSize.Height + (2 * CaretOutlineDip));
        outline.Intersect(shown);
        return outline.IsEmpty || !(outline.Width > 0) || !(outline.Height > 0) ? Rect.Empty : outline;
    }

    /// <summary>
    /// The system caret blink, run as WPF runs its caret's: shown for the blink time, hidden as long,
    /// at ten frames a second, and steady when Windows turns blinking off.
    /// </summary>
    private static void RestartCaretOutlineBlink(UIElement outline)
    {
        // 0 and INFINITE (-1 here) both mean a steady caret.
        int blink = unchecked((int)GetCaretBlinkTime());
        if (blink <= 0)
        {
            outline.BeginAnimation(UIElement.OpacityProperty, null);
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            Duration = new Duration(TimeSpan.FromMilliseconds(blink * 2.0)),
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
        Timeline.SetDesiredFrameRate(animation, 10);
        animation.Freeze();
        outline.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();
}
