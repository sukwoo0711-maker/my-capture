using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Caption box rules in the editor controller, with a deterministic stand-in for text measurement.
/// </summary>
/// <remarks>
/// The fake lays out every character half an em wide and every line 1.25 em tall, wraps a
/// fixed-width box at whole characters, and adds a one-pixel margin to auto-size boxes, which is
/// enough to check sizes exactly without WPF.
/// </remarks>
public sealed class EditorTextFitControllerTests
{
    private const double CharEm = 0.5;
    private const double LineEm = 1.25;

    private static RectD FakeMeasure(TextAnnotation text)
    {
        double size = text.FontSize;
        string[] paragraphs = text.Text.Split('\n');
        if (text.AutoSize)
        {
            int longest = paragraphs.Max(p => p.Length);
            return new RectD(text.Rect.X, text.Rect.Y, (longest * size * CharEm) + 1, (paragraphs.Length * size * LineEm) + 1);
        }

        double wrap = Math.Max(text.Rect.Width, size);
        int perLine = Math.Max(1, (int)Math.Floor(wrap / (size * CharEm)));
        int lines = paragraphs.Sum(p => Math.Max(1, (int)Math.Ceiling(p.Length / (double)perLine)));
        return new RectD(text.Rect.X, text.Rect.Y, wrap, (lines * size * LineEm) + 1);
    }

    private static AnnotationEditorController NewController(out AnnotationDocument document, out UndoStack undo)
    {
        document = AnnotationDocument.CreateFor(800, 600);
        undo = new UndoStack(mergeWindow: TimeSpan.Zero);
        return new AnnotationEditorController(document, undo) { TextFitter = FakeMeasure, HitTolerance = 4 };
    }

    private static TextAnnotation Caption(AnnotationEditorController c, PointD at, string text)
    {
        TextAnnotation caption = c.BeginTextAnnotation(at, 180, 40);
        c.CommitTextEdit(caption, text);
        c.SetSelected(caption);
        return caption;
    }

    [Fact]
    public void NewCaptionFollowsItsText()
    {
        AnnotationEditorController c = NewController(out _, out _);

        TextAnnotation caption = Caption(c, new PointD(10, 20), "hello");

        Assert.True(caption.AutoSize);
        Assert.Equal(new RectD(10, 20, (5 * 9) + 1, 22.5 + 1), caption.Rect);
    }

    [Fact]
    public void LargerFontRefitsTheBoxAndOneUndoRestoresSizeAndBox()
    {
        AnnotationEditorController c = NewController(out _, out UndoStack undo);
        TextAnnotation caption = Caption(c, new PointD(10, 20), "hello");
        RectD before = caption.Rect;
        int steps = undo.UndoCount;

        c.ApplyFontSize(96);

        Assert.Equal(96, caption.FontSize);
        Assert.Equal(new RectD(10, 20, (5 * 48) + 1, 120 + 1), caption.Rect);
        Assert.Equal(steps + 1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(18, caption.FontSize);
        Assert.Equal(before, caption.Rect);
        Assert.True(c.PerformRedo());
        Assert.Equal(96, caption.FontSize);
        Assert.Equal(new RectD(10, 20, (5 * 48) + 1, 120 + 1), caption.Rect);
    }

    [Fact]
    public void AutoSizeCaptionReachingTheImageEdgeWrapsAtTheEdge()
    {
        AnnotationEditorController c = NewController(out _, out _);

        TextAnnotation fits = Caption(c, new PointD(700, 10), "abcdefghij");
        TextAnnotation wraps = Caption(c, new PointD(700, 100), "abcdefghijklmnop");

        Assert.True(fits.AutoSize);
        Assert.Equal(791, fits.Rect.Right, 6);
        Assert.False(wraps.AutoSize);
        Assert.Equal(new RectD(700, 100, 100, (2 * 22.5) + 1), wraps.Rect);
    }

    [Fact]
    public void FontSizeChangeFollowsTheSameEdgeRule()
    {
        AnnotationEditorController c = NewController(out _, out _);
        TextAnnotation caption = Caption(c, new PointD(700, 10), "abcdefghij");

        c.ApplyFontSize(36);

        // 10 characters at 18 px each no longer fit in the 100 px left before the edge.
        Assert.False(caption.AutoSize);
        Assert.Equal(new RectD(700, 10, 100, (2 * 45) + 1), caption.Rect);
    }

    [Fact]
    public void CaptionStartedTooCloseToTheEdgeStillGetsOneEm()
    {
        AnnotationEditorController c = NewController(out _, out _);

        TextAnnotation caption = Caption(c, new PointD(795, 10), "abc");

        Assert.False(caption.AutoSize);
        Assert.Equal(18, caption.Rect.Width, 6);
    }

    [Fact]
    public void SizeAndColourChangesWhileTypingLeaveOnlyTheAdd()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument document, out UndoStack undo);
        TextAnnotation caption = c.BeginTextAnnotation(new PointD(30, 30), 180, 40);

        c.ApplyFontSize(40);
        c.ApplyStrokeColor(ColorRgba.FromRgb(0x3B, 0x82, 0xF6));

        Assert.Equal(40, caption.FontSize);
        Assert.Equal(ColorRgba.FromRgb(0x3B, 0x82, 0xF6), caption.Foreground);
        Assert.Equal(1, undo.UndoCount);

        c.CommitTextEdit(caption, string.Empty);

        Assert.Empty(document.Items);
        Assert.Equal(0, undo.UndoCount);
        Assert.Null(c.Selected);
    }

    [Fact]
    public void TextTypedAfterASizeChangeCommitsAtThatSizeAndUndoesAsTextThenAdd()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument document, out _);
        TextAnnotation caption = c.BeginTextAnnotation(new PointD(30, 30), 180, 40);
        c.ApplyFontSize(40);

        c.CommitTextEdit(caption, "ab");

        Assert.Equal(new RectD(30, 30, 41, 51), caption.Rect);
        Assert.True(c.PerformUndo());
        Assert.Equal(string.Empty, caption.Text);
        Assert.True(c.PerformUndo());
        Assert.Empty(document.Items);
    }

    [Fact]
    public void RightEdgeHandleSetsAFixedWrappingWidthAndHeightFollows()
    {
        AnnotationEditorController c = NewController(out _, out UndoStack undo);
        TextAnnotation caption = Caption(c, new PointD(100, 100), "aaaabbbbcccc");
        TextLayout before = TextLayout.Of(caption);
        c.Tool = EditorTool.Select;
        c.SetSelected(caption);
        int steps = undo.UndoCount;

        c.PointerDown(new PointD(caption.Rect.Right, caption.Rect.Center.Y));
        c.PointerMove(new PointD(150, 400));
        c.PointerMove(new PointD(145, 400));
        c.PointerUp(new PointD(145, 400));

        Assert.False(caption.AutoSize);
        Assert.Equal(18, caption.FontSize);
        // 45 px holds 5 characters per line: 12 characters take 3 lines.
        Assert.Equal(new RectD(100, 100, 45, (3 * 22.5) + 1), caption.Rect);
        Assert.Equal(steps + 1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(before, TextLayout.Of(caption));
    }

    [Fact]
    public void LeftEdgeHandleKeepsTheRightEdgeAndStopsAtOneEm()
    {
        AnnotationEditorController c = NewController(out _, out _);
        TextAnnotation caption = Caption(c, new PointD(100, 100), "aaaabbbb");
        double right = caption.Rect.Right;
        c.Tool = EditorTool.Select;
        c.SetSelected(caption);

        c.PointerDown(new PointD(caption.Rect.Left, caption.Rect.Center.Y));
        c.PointerMove(new PointD(right + 50, 100));
        c.PointerUp(new PointD(right + 50, 100));

        Assert.False(caption.AutoSize);
        Assert.Equal(18, caption.Rect.Width, 6);
        Assert.Equal(right, caption.Rect.Right, 6);
        Assert.Equal(100, caption.Rect.Y, 6);
    }

    [Fact]
    public void CornerDragScalesTheFontOnceAndRedoRepeatsTheSameState()
    {
        AnnotationEditorController c = NewController(out _, out UndoStack undo);
        TextAnnotation caption = Caption(c, new PointD(100, 100), "hello");
        TextLayout before = TextLayout.Of(caption);
        c.Tool = EditorTool.Select;
        c.SetSelected(caption);

        // Doubling the box height doubles the font: 18 -> 36, not 72.
        c.PointerDown(new PointD(caption.Rect.Right, caption.Rect.Bottom));
        c.PointerMove(new PointD(caption.Rect.Right + 10, caption.Rect.Bottom + 5));
        c.PointerMove(new PointD(before.Rect.Right + 30, before.Rect.Top + (2 * before.Rect.Height)));
        c.PointerUp(new PointD(before.Rect.Right + 30, before.Rect.Top + (2 * before.Rect.Height)));

        Assert.Equal(36, caption.FontSize, 6);
        Assert.True(caption.AutoSize);
        TextLayout after = TextLayout.Of(caption);
        Assert.Equal(new RectD(100, 100, (5 * 18) + 1, 45 + 1), after.Rect);

        Assert.True(c.PerformUndo());
        Assert.Equal(before, TextLayout.Of(caption));
        Assert.True(c.PerformRedo());
        Assert.Equal(after, TextLayout.Of(caption));
        Assert.Equal(36, caption.FontSize, 6);
        Assert.Equal(0, undo.RedoCount);
    }

    [Fact]
    public void TopLeftCornerDragKeepsTheBottomRightCorner()
    {
        AnnotationEditorController c = NewController(out _, out _);
        TextAnnotation caption = Caption(c, new PointD(100, 100), "hello");
        RectD before = caption.Rect;
        c.Tool = EditorTool.Select;
        c.SetSelected(caption);

        var to = new PointD(before.Left - 20, before.Bottom - (2 * before.Height));
        c.PointerDown(new PointD(before.Left, before.Top));
        c.PointerMove(to);
        c.PointerUp(to);

        Assert.Equal(36, caption.FontSize, 6);
        Assert.Equal(before.Right, caption.Rect.Right, 6);
        Assert.Equal(before.Bottom, caption.Rect.Bottom, 6);
    }

    [Fact]
    public void FixedWidthCornerDragScalesTheWrappingWidthByTheSameRatio()
    {
        AnnotationEditorController c = NewController(out _, out _);
        TextAnnotation caption = Caption(c, new PointD(100, 100), "aaaabbbbcccc");
        c.Tool = EditorTool.Select;
        c.SetSelected(caption);
        c.PointerDown(new PointD(caption.Rect.Right, caption.Rect.Center.Y));
        c.PointerMove(new PointD(145, 0));
        c.PointerUp(new PointD(145, 0));
        RectD fixedBox = caption.Rect;

        var to = new PointD(fixedBox.Right, fixedBox.Top + (2 * fixedBox.Height));
        c.PointerDown(new PointD(fixedBox.Right, fixedBox.Bottom));
        c.PointerMove(to);
        c.PointerUp(to);

        Assert.False(caption.AutoSize);
        Assert.Equal(36, caption.FontSize, 6);
        Assert.Equal(90, caption.Rect.Width, 6);
        Assert.Equal((3 * 45) + 1, caption.Rect.Height, 6);
    }

    [Fact]
    public void TopAndBottomHandlesAreNotOfferedForCaptionsButStayForBadges()
    {
        AnnotationEditorController c = NewController(out _, out _);
        TextAnnotation caption = Caption(c, new PointD(100, 100), "hello");
        c.Tool = EditorTool.Select;
        c.SetSelected(caption);
        RectD box = caption.Rect;

        Assert.Equal(ResizeHandle.None, c.HandleAt(new PointD(box.Center.X, box.Top), 4));
        Assert.Equal(ResizeHandle.None, c.HandleAt(new PointD(box.Center.X, box.Bottom), 4));
        Assert.Equal(ResizeHandle.MiddleRight, c.HandleAt(new PointD(box.Right, box.Center.Y), 4));

        // Pressing where the top handle would be moves the caption instead of resizing it.
        c.PointerDown(new PointD(box.Center.X, box.Top + 1));
        c.PointerMove(new PointD(box.Center.X + 10, box.Top + 21));
        c.PointerUp(new PointD(box.Center.X + 10, box.Top + 21));
        Assert.Equal(new RectD(box.X + 10, box.Y + 20, box.Width, box.Height), caption.Rect);
        Assert.Equal(18, caption.FontSize);

        TextAnnotation badge = c.AddStepNumber(new PointD(400, 400));
        Assert.Equal(ResizeHandle.TopCenter, c.HandleAt(new PointD(badge.Rect.Center.X, badge.Rect.Top), 4));
    }

    [Fact]
    public void MovingAWrappedCaptionKeepsItsWrappingWidth()
    {
        AnnotationEditorController c = NewController(out _, out _);
        TextAnnotation caption = Caption(c, new PointD(700, 100), "abcdefghijklmnop");
        RectD wrapped = caption.Rect;
        c.Tool = EditorTool.Select;
        c.SetSelected(caption);

        c.PointerDown(new PointD(wrapped.Center.X, wrapped.Center.Y));
        c.PointerMove(new PointD(wrapped.Center.X - 500, wrapped.Center.Y));
        c.PointerUp(new PointD(wrapped.Center.X - 500, wrapped.Center.Y));

        Assert.False(caption.AutoSize);
        Assert.Equal(new RectD(200, 100, wrapped.Width, wrapped.Height), caption.Rect);
        Assert.True(c.PerformUndo());
        Assert.Equal(wrapped, caption.Rect);
        Assert.Equal(18, caption.FontSize);
    }

    [Fact]
    public void BadgeFontSizeScalesItsSquare()
    {
        AnnotationEditorController c = NewController(out _, out _);
        TextAnnotation badge = c.AddStepNumber(new PointD(10, 10));
        RectD square = badge.Rect;

        c.ApplyFontSize(badge.FontSize * 2);

        Assert.Equal(square.Width * 2, badge.Rect.Width, 6);
        Assert.Equal(square.Height * 2, badge.Rect.Height, 6);
        Assert.Equal(square.TopLeft, badge.Rect.TopLeft);
    }
}
