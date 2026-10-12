using System.Windows;
using System.Windows.Controls;
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
        Assert.Equal(string.Empty, committed.Text);
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
    [InlineData(false)]
    [InlineData(true)]
    public void OverflowScrollbarKeepsTheCommittedWrappingWidth(bool paragraphs) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(800, 600, 240));
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
