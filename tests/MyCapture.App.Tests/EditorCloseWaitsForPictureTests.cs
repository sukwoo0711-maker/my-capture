using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A close that arrives while a picked picture is still being read waits for the picture and
/// then goes ahead, as it did while the picture was decoded on the UI thread.
/// </summary>
public sealed class EditorCloseWaitsForPictureTests
{
    [Fact]
    public void CodeCloseDuringAPictureReadClosesOnceThePictureIsIn() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-close-picture-");
        try
        {
            (AnnotationEditorWindow window, TaskCompletionSource gate, Task insert) = OpenWithPendingPicture(root);
            bool cancelled = false;
            int picturesWhenClosed = -1;
            window.Closing += (_, e) => { if (!e.Cancel) picturesWhenClosed = Pictures(window).Count; };
            window.Cancelled += (_, _) => cancelled = true;

            window.Close();
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.True(window.IsVisible);

            gate.SetResult();
            PumpUntil(() => !window.IsVisible);
            Assert.True(insert.IsCompletedSuccessfully);
            Assert.Equal(1, picturesWhenClosed);
            Assert.True(cancelled);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void UserCloseDuringAPictureReadAsksOnceWithThePictureIn() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-close-picture-ask-");
        try
        {
            (AnnotationEditorWindow window, TaskCompletionSource gate, Task insert) = OpenWithPendingPicture(root);
            int prompts = 0;
            int picturesWhenAsked = -1;
            window.ConfirmClose = () =>
            {
                prompts++;
                picturesWhenAsked = Pictures(window).Count;
                return EditorCloseChoice.DontSave;
            };

            window.RequestClose();
            window.RequestClose();
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.True(window.IsVisible);
            Assert.Equal(0, prompts);

            gate.SetResult();
            PumpUntil(() => !window.IsVisible);
            Assert.True(insert.IsCompletedSuccessfully);
            Assert.Equal(1, prompts);
            Assert.Equal(1, picturesWhenAsked);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    private static (AnnotationEditorWindow Window, TaskCompletionSource Gate, Task Insert) OpenWithPendingPicture(string root)
    {
        string path = Path.Combine(root, "picture.png");
        ImageCodec.SavePng(EditorKeysTests.Solid(320, 200, 0x80), path);
        BitmapSource bitmap = EditorKeysTests.Solid(640, 360, 0x30);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 1000, Height = 640,
            ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, Task<AnnotationImageStore.DecodedFile?>> decode = window.Editor.DecodeImageForInsert;
        window.Editor.DecodeImageForInsert = async file =>
        {
            await gate.Task.ConfigureAwait(false);
            return await decode(file).ConfigureAwait(false);
        };
        Task insert = window.Editor.InsertImageFileAsync(path, new PointD(200, 120));
        Assert.True(window.Editor.IsOnlyReadingPictures);
        return (window, gate, insert);
    }

    private static IReadOnlyList<ImageAnnotation> Pictures(AnnotationEditorWindow window)
    {
        var controller = (AnnotationEditorController)typeof(AnnotationEditorControl)
            .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        return [.. controller.Document.Items.OfType<ImageAnnotation>()];
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(done(), "Timed out waiting for the editor.");
    }

    /// <summary>Runs async continuations on this test thread's dispatcher, as in the app.</summary>
    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        internal DispatcherContextScope() =>
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
