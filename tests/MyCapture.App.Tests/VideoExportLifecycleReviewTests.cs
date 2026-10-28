using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoExportLifecycleReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnshownDialog_DoesNotStartPreviewAfterRejectedExport(bool gif) => RunSta(() =>
    {
        VideoExportDialog dialog = CreateDialog(gif, out string source);
        int renderCalls = 0;
        dialog.DestinationChooserForTest = () => source;
        dialog.SourcePreviewRendererForTest = _ =>
        {
            renderCalls++;
            return Task.FromException<BitmapSource>(new InvalidOperationException("Unexpected source decode."));
        };
        dialog.SampleRendererForTest = (_, _) =>
        {
            renderCalls++;
            return Task.FromException<GifPreviewSample>(new InvalidOperationException("Unexpected sample encode."));
        };
        try
        {
            Assert.False(dialog.IsLoaded);
            Pump(dialog.ExportAsync());
            Assert.Equal(UiText.Format("MediaExport_Error", UiText.Get("MediaExport_SourceProtected")), dialog.StatusText);
            Assert.Equal(0, renderCalls);
            Assert.True(dialog.SourcePreviewCompletion.IsCompleted);
            Assert.True(dialog.SampleCompletion.IsCompleted);
            Assert.True(dialog.CanExport);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Close_CancelsAndDrainsPreviewBeforeClosedAndRejectsNewWork(bool gif) => RunSta(() =>
    {
        VideoExportDialog dialog = CreateDialog(gif, out _);
        var render = new ControlledPreview(dialog);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool releasedWhenClosed = false;
        dialog.Closed += (_, _) =>
        {
            releasedWhenClosed = render.Released;
            closed.TrySetResult(true);
        };
        int chooserCalls = 0;
        dialog.DestinationChooserForTest = () => { chooserCalls++; return null; };
        try
        {
            render.Start(gif);
            Assert.Equal(1, render.Calls);
            dialog.Close();
            Assert.True(render.Token.IsCancellationRequested);
            Assert.False(render.Released);
            Assert.False(closed.Task.IsCompleted);
            Assert.False(dialog.CanExport);

            // Settings and explicit requests cannot resurrect a reader while close is draining.
            dialog.TargetReductionPercent = 40;
            render.Start(gif);
            Pump(dialog.ExportAsync());
            dialog.Close();
            Assert.Equal(1, render.Calls);
            Assert.Equal(0, chooserCalls);
            Assert.False(closed.Task.IsCompleted);

            render.Release();
            Pump(closed.Task);
            Assert.True(releasedWhenClosed);
            Assert.True(dialog.SourcePreviewCompletion.IsCompleted);
            Assert.True(dialog.SampleCompletion.IsCompleted);
        }
        finally
        {
            render.Release();
            if (!closed.Task.IsCompleted)
            {
                dialog.Close();
                Pump(closed.Task);
            }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelExport_WaitsForPreviewReleaseAndDoesNotRestartAnUnshownDialog(bool gif) => RunSta(() =>
    {
        VideoExportDialog dialog = CreateDialog(gif, out string source);
        var render = new ControlledPreview(dialog);
        dialog.DestinationChooserForTest = () => Path.ChangeExtension(source, gif ? ".export.gif" : ".export.mp4");
        Task? export = null;
        try
        {
            render.Start(gif);
            export = dialog.ExportAsync();
            Assert.True(render.Token.IsCancellationRequested);
            dialog.CancelExport();
            Assert.False(export.IsCompleted);
            Assert.False(render.Released);
            render.Release();
            Pump(export);
            Assert.True(render.Released);
            Assert.Equal(1, render.Calls);
            Assert.Equal(UiText.Get("MediaExport_Cancelled"), dialog.StatusText);
            Assert.True(dialog.CanExport);
            Assert.False(dialog.IsProgressVisible);
        }
        finally
        {
            render.Release();
            dialog.CancelExport();
            if (export is not null) Pump(export);
            dialog.Close();
        }
    });

    private sealed class ControlledPreview
    {
        private readonly VideoExportDialog _dialog;
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ControlledPreview(VideoExportDialog dialog)
        {
            _dialog = dialog;
            dialog.SourcePreviewRendererForTest = async token =>
            {
                await HoldReader(token);
                BitmapSource frame = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
                frame.Freeze();
                return frame;
            };
            dialog.SampleRendererForTest = async (_, token) =>
            {
                await HoldReader(token);
                throw new InvalidOperationException("The controlled sample must be cancelled before release.");
            };
        }

        internal int Calls { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal bool Released { get; private set; }

        private async Task HoldReader(CancellationToken token)
        {
            Calls++;
            Token = token;
            try
            {
                // Native decoder teardown can finish after cancellation has been requested.
                await _release.Task;
                token.ThrowIfCancellationRequested();
            }
            finally { Released = true; }
        }

        internal void Start(bool gif)
        {
            if (gif) _dialog.StartSample();
            else _dialog.RequestSourcePreview();
        }

        internal void Release() => _release.TrySetResult(true);
    }

    private static VideoExportDialog CreateDialog(bool gif, out string source)
    {
        source = Path.Combine(Path.GetTempPath(), "never-created-export-lifecycle-" + Guid.NewGuid().ToString("N") + ".mp4");
        var recording = new RecordingResult(source, 1000, 30, 30, 320, 180);
        return new VideoExportDialog(recording, VideoEditDocument.CreateFor(320, 180, 1000), NullLoggerFactory.Instance, gif);
    }

    private static Task RunSta(Action action) => StaThreadTask.RunAsync(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        action();
        return true;
    });

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        Assert.True(task.IsCompleted, "The cancelled media operation did not finish.");
        task.GetAwaiter().GetResult();
    }
}
