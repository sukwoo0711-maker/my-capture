using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureEditorFlowTests
{
    [Fact]
    public void ResolveCompletedDrag_NormalizesClampsAndRejectsClicks()
    {
        var bounds = new RectD(0, 0, 100, 80);

        RectD? forward = CaptureOverlayView.ResolveCompletedDrag(
            new PointD(10.4, 12.6),
            new PointD(50.1, 42.2),
            bounds);
        RectD? reverse = CaptureOverlayView.ResolveCompletedDrag(
            new PointD(50.1, 42.2),
            new PointD(10.4, 12.6),
            bounds);
        RectD? clipped = CaptureOverlayView.ResolveCompletedDrag(
            new PointD(-20, -10),
            new PointD(120, 90),
            bounds);
        RectD? click = CaptureOverlayView.ResolveCompletedDrag(
            new PointD(10, 10),
            new PointD(10.9, 10.9),
            bounds);

        Assert.Equal(forward, reverse);
        Assert.Equal(new RectD(10, 12, 41, 31), forward);
        Assert.Equal(bounds, clipped);
        Assert.Null(click);
    }

    [Fact]
    public void ManualSelector_HasNoWindowCandidateConstructorOrTabInstructions()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(CaptureOverlayView).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Type[] parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal([typeof(FrozenFrame), typeof(bool)], parameterTypes);
        Assert.DoesNotContain("Tab", CaptureOverlayView.InstructionText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("창 선택", CaptureOverlayView.InstructionText, StringComparison.Ordinal);
        Assert.DoesNotContain(parameterTypes, type => type.Name.Contains("WindowCandidate", StringComparison.Ordinal));
    }

    [Fact]
    public void StandaloneEditor_DisplaysOnlySelectionAndCtrlCRequestsClipboardCommit()
    {
        RunSta(() =>
        {
            BitmapSource sourceBitmap = Solid(100, 80, 0x22);
            BitmapSource selectedBitmap = Solid(30, 20, 0x88);
            var frame = new FrozenFrame(sourceBitmap, new RectD(400, 250, 100, 80), null, 7.5);
            var sourceRegion = new RectD(14, 18, 30, 20);
            var editor = new AnnotationEditorControl(frame, sourceRegion, selectedBitmap);

            AnnotationEditingResult? requested = null;
            AnnotationEditingResult? completed = null;
            editor.CommitRequested = result =>
            {
                requested = result;
                return Task.FromResult(true);
            };
            editor.EditingCompleted += (_, result) => completed = result;

            Assert.Same(selectedBitmap, editor.DisplayedBitmap);
            Assert.Equal(new RectD(0, 0, 30, 20), editor.DisplayedRegion);

            bool handled = editor.HandleShortcut(Key.C, ModifierKeys.Control);

            Assert.True(handled);
            Assert.NotNull(requested);
            Assert.Same(requested, completed);
            Assert.Equal(EditorCommitAction.CopyToClipboard, requested!.Action);
            Assert.Same(frame, requested.Frame);
            Assert.Equal(sourceRegion, requested.BitmapRegion);
            Assert.Same(selectedBitmap, requested.SelectedBitmap);

            var window = new AnnotationEditorWindow(frame, sourceRegion, selectedBitmap);
            try
            {
                Assert.True(window.ShowInTaskbar);
                Assert.True(window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip);
                Assert.Same(window.Editor, window.Content);
                Assert.Same(selectedBitmap, window.Editor.DisplayedBitmap);

                Rect work = SystemParameters.WorkArea;
                Assert.Equal(Math.Min(980, Math.Max(window.MinWidth, work.Width - 64)), window.Width);
                Assert.Equal(Math.Min(620, Math.Max(window.MinHeight, work.Height - 64)), window.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EditorPointerCapture_IsOwnedByViewportEventElement()
    {
        RunSta(() =>
        {
            BitmapSource bitmap = Solid(320, 180, 0x44);
            var frame = new FrozenFrame(bitmap, new RectD(0, 0, 320, 180), null, 1);
            var editor = new AnnotationEditorControl(frame, new RectD(0, 0, 320, 180), bitmap);
            var host = new Window
            {
                Content = editor,
                Width = 640,
                Height = 420,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };

            try
            {
                host.Show();
                _ = host.Activate();
                host.UpdateLayout();

                Assert.True(editor.CapturePointer());
                Assert.True(editor.IsPointerCaptured);
                Assert.Same(editor.PointerInputElement, Mouse.Captured);
                Assert.False(editor.IsMouseCaptured);
            }
            finally
            {
                editor.ReleasePointer();
                host.Close();
            }
        });
    }

    private static BitmapSource Solid(int width, int height, byte value)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }
}
