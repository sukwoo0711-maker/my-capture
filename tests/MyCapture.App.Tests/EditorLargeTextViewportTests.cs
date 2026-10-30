using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorLargeTextViewportTests
{
    [Fact]
    public void LargeLiveTextScrollsWithinTheViewportAndKeepsTheCompleteDocument() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(800, 600, 240));
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        string content = string.Concat(Enumerable.Repeat("전체 내용 가나다😀 abcXYZ0123456789\n", 4000));
        Assert.True(content.Length >= 100_000);
        box.BeginChange();
        box.SelectedText = content;
        box.EndChange();
        host.Window.UpdateLayout();

        Canvas canvas = Field<Canvas>(host.Editor, "_overlayCanvas");
        var scroll = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        Assert.True(box.ActualHeight < 1000, "The input viewport must not grow to the complete document height.");
        Assert.True(box.ActualHeight <= canvas.ActualHeight - Canvas.GetTop(box) + 1);
        Assert.True(scroll.ScrollableHeight > 1000);
        Assert.Equal(content, box.Text);
        box.SelectAll();
        Assert.Equal(content, box.SelectedText);
        box.Select(0, 0);
        box.ScrollToHome();
        host.Window.UpdateLayout();
        Assert.Equal(0, scroll.VerticalOffset);
        box.ScrollToEnd();
        host.Window.UpdateLayout();
        Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight + 1);

        box.BeginChange();
        box.AppendText("추가😀");
        box.EndChange();
        Assert.Equal(content + "추가😀", box.Text);
        box.Undo();
        Assert.Equal(content, box.Text);
        box.Redo();
        Assert.Equal(content + "추가😀", box.Text);
        AnnotationDocument snapshot = host.Editor.CreateCurrentImageSnapshot();
        TextAnnotation snapshotText = Assert.Single(snapshot.Items.OfType<TextAnnotation>());
        Assert.Equal(content + "추가😀", snapshotText.Text);
        RectD complete = TextAnnotationLayout.Measure(snapshotText);
        Assert.Equal(complete, snapshotText.Rect);
        Assert.True(snapshotText.Rect.Height > 1000);

        Invoke(host.Editor, "CommitActiveText");
        AnnotationEditorController controller = Controller(host.Editor);
        TextAnnotation committed = Assert.Single(controller.Document.Items.OfType<TextAnnotation>());
        Assert.Equal(snapshotText.Text, committed.Text);
        Assert.Equal(snapshotText.Rect, committed.Rect);
        Assert.True(controller.PerformUndo());
        Assert.Empty(controller.Document.Items);
        Assert.True(controller.PerformRedo());
        Assert.Equal(snapshotText.Text, committed.Text);
        Assert.Equal(snapshotText.Rect, committed.Rect);
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ResizingTheViewportUpdatesTheLiveHeightWithoutChangingTextOrDocument(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(800, 600, 240));
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        box.Text = string.Concat(Enumerable.Repeat("가나다😀\n", 300));
        host.Window.UpdateLayout();
        Canvas canvas = Field<Canvas>(host.Editor, "_overlayCanvas");
        // Exercise WPF's logical available-height changes at equivalent scaled viewport sizes.
        // This does not inject a monitor DPI or claim to test WM_DPICHANGED.
        canvas.LayoutTransform = new ScaleTransform(scale, scale);
        host.Window.Height = 520;
        host.Window.UpdateLayout();
        string before = host.Editor.CreateCurrentImageSnapshot().ToJson();
        double small = box.MaxHeight;
        Assert.True(box.ActualHeight <= small + 1);
        host.Window.Height = 760;
        host.Window.UpdateLayout();
        Assert.True(box.MaxHeight > small);
        Assert.True(box.ActualHeight <= box.MaxHeight + 1);
        Assert.Equal(before, host.Editor.CreateCurrentImageSnapshot().ToJson());
    });

    [Theory]
    [InlineData(24.6, 152, 17, 632, "Right")]
    [InlineData(467, 172, 17, 632, "Left")]
    [InlineData(1, 638, 16, 632, "Overlay")]
    [InlineData(467, 172, 0, 632, "Right")]
    [InlineData(467, 172, 17, 0, "Right")]
    public void CaptionScrollBarTakesTheSideTheCanvasDoesNotClip(double left, double width, double bar, double visible, string side) =>
        Assert.Equal(Enum.Parse<AnnotationEditorControl.CaptionScrollBarSide>(side),
            AnnotationEditorControl.ChooseCaptionScrollBarSide(left, width, bar, visible));

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void FullWidthCaptionOverlaysItsBarInsideTheCanvasAndKeepsEditing(int paragraphLines) => StaTestHost.Run(() =>
    {
        // A wide image stays width-limited in the default 1000x640 host, even on a
        // small work area that would clamp a taller window and letterbox a 4:3 image.
        using EditorWindowHost host = EditorWindowHost.Create(Solid(900, 300, 240));
        // The 16-DIP image margin is narrower than the bar and text chrome on either side,
        // so a full-width caption still overlays the bar at its right edge.
        Invoke(host.Editor, "PlaceTextBox", new PointD(0, 40));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        // Paragraphs wider than the image, so the caption wraps at the image's right edge.
        string paragraph = string.Concat(Enumerable.Repeat("한글 조합과 줄바꿈을 유지합니다 abcXYZ😀 ", 6));
        string text = string.Join("\n", Enumerable.Repeat(paragraph, 120 / paragraphLines));
        box.Text = text;
        box.CaretIndex = text.Length;
        box.ScrollToEnd();
        host.Window.UpdateLayout();
        var scroll = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        Canvas overlay = Field<Canvas>(host.Editor, "_overlayCanvas");
        AssertBarInside(bar, overlay);
        TextAnnotation caption = Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>());
        Assert.False(caption.AutoSize);
        AnnotationEditorSurface surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        Assert.Equal(16, surface.FrameRectDip.Left, 6);
        Assert.Equal(overlay.ActualWidth - 16, surface.FrameRectDip.Right, 6);
        var inset = Field<Point>(host.Editor, "_liveTextInset");
        // Overlaying never narrows the text viewport, so the live lines match the committed ones.
        Assert.Equal(surface.ToSurfaceRect(caption.Rect).Width, box.ActualWidth - 2 * inset.X, 1);
        var singleLine = (TextAnnotation)caption.Clone();
        singleLine.Text = "가";
        singleLine.AutoSize = true;
        Assert.Equal((int)Math.Round(TextAnnotationLayout.Format(caption, 1).Height / TextAnnotationLayout.Format(singleLine, 1).Height), box.LineCount);
        // The caret stays inside the visible canvas, and typing and undo still edit the same box.
        Rect caret = box.GetRectFromCharacterIndex(box.CaretIndex);
        Point caretOnCanvas = box.TranslatePoint(caret.TopLeft, overlay);
        Assert.InRange(caretOnCanvas.X, 0, overlay.ActualWidth);
        Assert.InRange(caretOnCanvas.Y, 0, overlay.ActualHeight);
        Assert.True(InputMethod.GetIsInputMethodEnabled(box));
        box.BeginChange();
        box.SelectedText = "추가";
        box.EndChange();
        Assert.Equal(text + "추가", box.Text);
        box.Undo();
        Assert.Equal(text, box.Text);
    });

    private static void AssertBarInside(System.Windows.Controls.Primitives.ScrollBar bar, Canvas overlay)
    {
        Assert.True(bar.ActualWidth > 0, "the vertical bar is laid out");
        Rect bounds = bar.TransformToAncestor(overlay).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= overlay.ActualWidth + 0.5,
            $"the vertical bar {bounds.Left:0.0}-{bounds.Right:0.0} must stay inside the visible canvas 0-{overlay.ActualWidth:0.0}");
        Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= overlay.ActualHeight + 0.5,
            $"the vertical bar {bounds.Top:0.0}-{bounds.Bottom:0.0} must stay inside the visible canvas height {overlay.ActualHeight:0.0}");
    }

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(1000, 600)]
    [InlineData(850, 0)]
    [InlineData(850, 600)]
    public void ReopenedCaptionKeepsFractionalWrappingWidthInRoundedWindows(int windowWidth, int left) => StaTestHost.Run(() =>
    {
        var caption = new TextAnnotation
        {
            Text = string.Concat(Enumerable.Repeat("한글 줄바꿈과 소수 폭을 유지합니다 abcXYZ😀 ", 100)),
            // A controlled fractional width is chosen after the actual viewport is measured.
            Rect = new RectD(left, 40, 800 - left - 1.4, 40),
            FontSize = 18,
            AutoSize = false,
        };
        caption.Rect = TextAnnotationLayout.Measure(caption);
        var document = AnnotationDocument.CreateFor(800, 600);
        document.Add(caption);
        using EditorWindowHost host = EditorWindowHost.Create(Solid(800, 600, 240), document);
        host.Window.Width = windowWidth;
        host.Window.UpdateLayout();
        Assert.True(host.Window.UseLayoutRounding);
        AnnotationEditorSurface surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        double dpi = VisualTreeHelper.GetDpi(surface).DpiScaleX;
        double dipPerImagePixel = surface.ToSurfaceRect(new RectD(0, 0, 1, 1)).Width;
        // Footer commands reserve their compact height, which can make this 4:3 image
        // height-limited. Do not rely on one particular monitor/window fit scale.
        double fractionalDipWidth = (Math.Floor(surface.ToSurfaceRect(caption.Rect).Width * dpi) - .35) / dpi;
        caption.Rect = new RectD(caption.Rect.Left, caption.Rect.Top, fractionalDipWidth / dipPerImagePixel, caption.Rect.Height);
        caption.Rect = TextAnnotationLayout.Measure(caption);
        AnnotationEditorController controller = Controller(host.Editor);
        controller.SetSelected(caption);
        string before = controller.Document.ToJson();
        host.Editor.ShortcutActions["EditText"]();
        host.Window.UpdateLayout();

        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        var scroll = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        AssertBarInside(bar, Field<Canvas>(host.Editor, "_overlayCanvas"));
        double expectedWidth = Field<AnnotationEditorSurface>(host.Editor, "_surface").ToSurfaceRect(caption.Rect).Width;
        double scale = VisualTreeHelper.GetDpi(box).DpiScaleX;
        Assert.True(Math.Abs(expectedWidth - Math.Round(expectedWidth * scale) / scale) > 0.05,
            $"The fixture must exercise a fractional wrapping width: {expectedWidth}.");
        var inset = Field<Point>(host.Editor, "_liveTextInset");
        double barSlot = Grid.GetColumn(bar) == 1 ? 0 : bar.ActualWidth;
        Assert.Equal(expectedWidth, box.ActualWidth - 2 * inset.X - barSlot, 1);
        var singleLine = (TextAnnotation)caption.Clone();
        singleLine.Text = "가";
        singleLine.AutoSize = true;
        Assert.Equal((int)Math.Round(TextAnnotationLayout.Format(caption, 1).Height / TextAnnotationLayout.Format(singleLine, 1).Height), box.LineCount);

        Invoke(host.Editor, "CommitActiveText");
        Assert.Equal(before, controller.Document.ToJson());
        Assert.False(controller.CanUndo);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverflowScrollbarKeepsTheCommittedWrappingWidth(bool paragraphs) => StaTestHost.Run(() =>
    {
        // Keep the image width-limited, with only the 16-DIP inset outside its right edge.
        using EditorWindowHost host = EditorWindowHost.Create(Solid(900, 300, 240));
        Invoke(host.Editor, "PlaceTextBox", new PointD(600, 40));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        string line = "고정 폭에서 같은 줄바꿈을 유지합니다 abcXYZ😀 ";
        box.Text = string.Join(paragraphs ? "\n" : "", Enumerable.Repeat(line, 100));
        host.Window.UpdateLayout();
        AnnotationDocument snapshot = host.Editor.CreateCurrentImageSnapshot();
        TextAnnotation caption = Assert.Single(snapshot.Items.OfType<TextAnnotation>());
        Assert.False(caption.AutoSize);
        var scroll = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        AnnotationEditorSurface surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        double dipWidth = surface.ToSurfaceRect(caption.Rect).Width;
        var inset = Field<Point>(host.Editor, "_liveTextInset");
        var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        Assert.Equal(dipWidth, box.ActualWidth - 2 * inset.X - bar.ActualWidth, 1);
        // The caption wraps at the image's right edge; its margin cannot fit the bar and text chrome.
        // The bar moves left, wholly inside the canvas, while the first glyph stays at the committed position.
        Canvas overlay = Field<Canvas>(host.Editor, "_overlayCanvas");
        Assert.Equal(16, surface.FrameRectDip.Left, 6);
        Assert.Equal(overlay.ActualWidth - 16, surface.FrameRectDip.Right, 6);
        AssertBarInside(bar, overlay);
        Assert.True(bar.TranslatePoint(new Point(), overlay).X < box.TranslatePoint(new Point(inset.X, 0), overlay).X,
            "the bar sits left of the text");
        box.ScrollToHome();
        host.Window.UpdateLayout();
        Point glyph = box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, overlay);
        Rect committed = surface.ToSurfaceRect(caption.Rect);
        Assert.InRange(glyph.X - committed.Left, -0.5, 0.5);
        Assert.InRange(glyph.Y - committed.Top, -0.5, 0.5);
        var singleLine = (TextAnnotation)caption.Clone();
        singleLine.Text = "가";
        singleLine.AutoSize = true;
        double lineHeight = TextAnnotationLayout.Format(singleLine, 1).Height;
        Assert.Equal((int)Math.Round(TextAnnotationLayout.Format(caption, 1).Height / lineHeight), box.LineCount);
        // Hiding ends the focused edit through the existing LostKeyboardFocus contract.
        // A new edit after reopening still tracks the current canvas height.
        host.Window.Hide();
        host.Window.Show();
        Assert.Equal(caption.Text, Controller(host.Editor).Document.Items.OfType<TextAnnotation>().Single().Text);
        Invoke(host.Editor, "PlaceTextBox", new PointD(600, 40));
        box = Field<TextBox>(host.Editor, "_activeTextBox");
        box.Text = caption.Text;
        host.Window.Height = 520;
        host.Window.UpdateLayout();
        Canvas canvas = Field<Canvas>(host.Editor, "_overlayCanvas");
        Assert.True(box.ActualHeight <= canvas.ActualHeight - Canvas.GetTop(box) + 1);
        Assert.Equal(caption.Text, box.Text);
    });
}
