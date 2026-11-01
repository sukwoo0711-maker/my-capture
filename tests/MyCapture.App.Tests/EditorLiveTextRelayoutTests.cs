using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorLiveTextRelayoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void ResizingRelaysOutLiveTextWithoutChangingContentSelectionOrHistory(int rotation) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(900, 520, 240));
        host.Editor.HandleShortcut(Key.T, ModifierKeys.None);
        host.Editor.SimulatePointerDown(host.Editor.ViewportPointOf(new PointD(420, 180)));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        TextAnnotation text = Field<TextAnnotation>(host.Editor, "_editingText");
        text.RotationDegrees = rotation;
        box.Text = "첫 줄 abc\n둘째 줄 입력 내용";
        box.Select(3, 4);
        host.Window.UpdateLayout();
        double beforeSize = box.FontSize;
        var undo = Controller(host.Editor).Undo;
        long state = undo.CurrentStateId;

        host.Window.Width = 680;
        host.Window.Height = 520;
        host.Window.UpdateLayout();

        Assert.NotEqual(beforeSize, box.FontSize);
        AssertAligned(host.Editor, box, text);
        Assert.Equal("첫 줄 abc\n둘째 줄 입력 내용", box.Text);
        Assert.Equal(3, box.SelectionStart);
        Assert.Equal(4, box.SelectionLength);
        Assert.Equal(state, undo.CurrentStateId);
        Assert.Same(box, Field<TextBox>(host.Editor, "_activeTextBox"));
    });

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DpiNotificationRelaysOutLiveTextAtUnchangedWindowSize(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(900, 520, 240));
        host.Editor.HandleShortcut(Key.T, ModifierKeys.None);
        host.Editor.SimulatePointerDown(host.Editor.ViewportPointOf(new PointD(200, 100)));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        TextAnnotation text = Field<TextAnnotation>(host.Editor, "_editingText");
        box.Text = "배율 변경 / DPI";
        host.Window.UpdateLayout();
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        Size before = surface.RenderSize;
        // Synthetic WPF DPI notification; no physical monitor or input manipulation.
        Invoke(surface, "OnDpiChanged", new DpiScale(1, 1), new DpiScale(scale, scale));
        host.Window.UpdateLayout();
        Assert.Equal(before, surface.RenderSize);
        AssertAligned(host.Editor, box, text);
        Assert.Equal("배율 변경 / DPI", box.Text);
        Assert.Equal(0, Controller(host.Editor).Undo.UndoCount);
    });

    private static void AssertAligned(AnnotationEditorControl editor, TextBox box, TextAnnotation text)
    {
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        TextAnnotation snapshot = Assert.Single(editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>());
        Rect target = surface.ToSurfaceRect(snapshot.Rect);
        Point inset = Field<Point>(editor, "_liveTextInset");
        Assert.Equal(Math.Max(1, text.FontSize * surface.DipPerPixel), box.FontSize, 6);
        Assert.Equal(target.Left, Canvas.GetLeft(box) + inset.X, 6);
        Assert.Equal(target.Top, Canvas.GetTop(box) + inset.Y, 6);
        var rotate = Assert.IsType<System.Windows.Media.RotateTransform>(box.RenderTransform);
        Assert.Equal(text.RotationDegrees, rotate.Angle);
        Assert.Equal(target.Width / 2 + inset.X, rotate.CenterX, 6);
        Assert.Equal(target.Height / 2 + inset.Y, rotate.CenterY, 6);
    }
}
