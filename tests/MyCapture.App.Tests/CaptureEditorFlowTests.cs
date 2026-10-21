using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
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
        ConstructorInfo[] constructors = typeof(CaptureOverlayView).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Contains(
            constructors,
            constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(FrozenFrame), typeof(bool), typeof(bool)]));
        ParameterInfo grid = constructors.Single(constructor =>
            constructor.GetParameters().First().ParameterType == typeof(FrozenFrame)).GetParameters()[2];
        Assert.Equal("showSelectionGrid", grid.Name);
        Assert.True(grid.IsOptional);
        Assert.Equal(true, grid.DefaultValue);
        Assert.DoesNotContain("Tab", CaptureOverlayView.InstructionText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("창 선택", CaptureOverlayView.InstructionText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            constructors.SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType.Name.Contains("WindowCandidate", StringComparison.Ordinal));
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

            // Ctrl+C copies the edit while keeping it available for further work.
            Assert.True(handled);
            Assert.NotNull(requested);
            Assert.Null(completed);
            Assert.True(editor.IsHitTestVisible);
            Assert.Equal(EditorCommitAction.CopyToClipboard, requested!.Action);
            Assert.Equal(frame.ScreenBounds, requested.Frame.ScreenBounds);
            Assert.Equal(frame.Monitor, requested.Frame.Monitor);
            Assert.Equal(frame.DpiScale, requested.Frame.DpiScale);
            Assert.Equal(frame.ElapsedMilliseconds, requested.Frame.ElapsedMilliseconds);
            Assert.Equal(sourceRegion, requested.BitmapRegion);
            Assert.Same(selectedBitmap, requested.SelectedBitmap);
            Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.Control));
            Assert.Same(requested, completed);
            Assert.Equal(EditorCommitAction.Done, completed!.Action);

            // The window opens on the pointer's monitor and is sized for it. The reported desktop has a
            // landscape primary and a 768 x 1366 portrait secondary; the pointer may be on either.
            var primary = new MonitorInfo(@"\\.\DISPLAY1", new RectD(0, 0, 1920, 1080),
                new RectD(0, 0, 1920, 1032), 96, true);
            var portrait = new MonitorInfo(@"\\.\DISPLAY2", new RectD(1920, 0, 768, 1366),
                new RectD(1920, 0, 768, 1318), 96, false);
            // 980 x 620 DIPs wanted; on the portrait monitor the width keeps 64 DIPs free: 768 - 64.
            AssertStandaloneWindow(primary, 980, 620);
            AssertStandaloneWindow(portrait, 704, 620);

            void AssertStandaloneWindow(MonitorInfo pointerMonitor, double width, double height)
            {
                var window = new AnnotationEditorWindow(frame, sourceRegion, selectedBitmap, pointerMonitor: pointerMonitor);
                try
                {
                    Assert.True(window.ShowInTaskbar);
                    Assert.True(window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip);
                    Assert.Same(window.Editor, window.Content);
                    Assert.Same(selectedBitmap, window.Editor.DisplayedBitmap);
                    Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);

                    Assert.Equal(AnnotationEditorWindow.MinimumSize.Width, window.MinWidth);
                    Assert.Equal(AnnotationEditorWindow.MinimumSize.Height, window.MinHeight);
                    Assert.Equal(width, window.Width);
                    Assert.Equal(height, window.Height);
                }
                finally
                {
                    window.Close();
                }
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

                Assert.True(editor.CapturePointer(),
                    $"Window active={host.IsActive}; viewport visible={editor.PointerInputElement.IsVisible}, enabled={editor.PointerInputElement.IsEnabled}, hit-test={editor.PointerInputElement.IsHitTestVisible}.");
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

    [Fact]
    public void OpenEditor_DoesNotKeepDesktopPixelsAlive() => RunSta(() =>
    {
        (AnnotationEditorControl editor, WeakReference<BitmapSource> desktop) = CreateDetachedEditor();
        // Collection is a test probe only; production does not force GC or trim working sets.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(desktop.TryGetTarget(out _));
        Assert.Equal(30, editor.DisplayedBitmap.PixelWidth);
        GC.KeepAlive(editor);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (AnnotationEditorControl Editor, WeakReference<BitmapSource> Desktop) CreateDetachedEditor()
    {
        BitmapSource desktop = Solid(800, 600, 0x22);
        var bounds = new RectD(-800, 100, 800, 600);
        var monitor = new MyCapture.Platform.Display.MonitorInfo("display", bounds, bounds, 144, false);
        var frame = new FrozenFrame(desktop, bounds, monitor, 12.5);
        var editor = new AnnotationEditorControl(frame, new RectD(14, 18, 30, 20), Solid(30, 20, 0x88));
        editor.CommitRequested = result =>
        {
            Assert.Equal(bounds, result.Frame.ScreenBounds);
            Assert.Same(monitor, result.Frame.Monitor);
            Assert.Equal(1.5, result.Frame.DpiScale);
            Assert.Equal(12.5, result.Frame.ElapsedMilliseconds);
            Assert.Equal(new RectD(14, 18, 30, 20), result.BitmapRegion);
            return Task.FromResult(false);
        };
        Assert.True(editor.HandleShortcut(Key.C, ModifierKeys.Control));
        return (editor, new WeakReference<BitmapSource>(desktop));
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
        var thread = WpfTestThread.Create(() =>
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
