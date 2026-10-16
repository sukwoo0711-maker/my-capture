using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>A lazy, process-wide presentation measurement with a bounded, cached fallback.</summary>
internal sealed class StudioRangePresentationCalibration
{
    private readonly Lazy<Task<bool>> _result;

    internal StudioRangePresentationCalibration(
        Func<CancellationToken, Task<bool>> measure,
        TimeSpan? timeout = null,
        Action<Exception>? logFailure = null)
    {
        _result = new Lazy<Task<bool>>(() => MeasureOnceAsync(
            measure, timeout ?? TimeSpan.FromSeconds(5), logFailure ?? (ex =>
                Trace.TraceWarning("MediaElement range calibration failed; retaining studio-range correction: {0}", ex))));
    }

    // Reading the decision does not start work. Unknown and failed measurements retain the old policy.
    internal bool NeedsExpansion => !_result.IsValueCreated || !_result.Value.IsCompletedSuccessfully || _result.Value.Result;
    internal Task<bool> GetResultAsync() => _result.Value;
    internal Exception? Failure { get; private set; }

    private async Task<bool> MeasureOnceAsync(
        Func<CancellationToken, Task<bool>> measure, TimeSpan timeout, Action<Exception> logFailure)
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        try
        {
            // Even a synchronously blocking encoder/driver must not block Apply or its UI thread.
            return await Task.Run(() => measure(token))
                .WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failure = ex;
            logFailure(ex);
            return true;
        }
        finally
        {
            cancellation.Cancel();
        }
    }
}

/// <summary>Measures WPF's actual presenter, without a shader, using the recording encoder.</summary>
internal static class StudioRangeCalibrationProbe
{
    private const int Width = 320;
    private const int Height = 180;
    private static readonly byte[] SourceLevels = [0, 64, 192, 255];
    private static readonly byte[] StudioLevels = [16, 71, 181, 235];

    internal static Task<bool> MeasureAsync(CancellationToken cancellationToken)
    {
        // The caller runs this on the pool. Keep MF encoding off every WPF dispatcher. The tiny
        // clip lives in the app's OS-temp diagnostic root, never in the user's capture library,
        // and is removed once the presenter has been measured.
        string directory = ValidatedFilePaths.CreateTemporaryDirectory();
        string path = Path.Combine(directory, "studio-range-calibration.mp4");
        try
        {
            Encode(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            _ = DeleteClip(directory);
            throw;
        }

        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { result.TrySetResult(await MeasureWindowAsync(path, cancellationToken)); }
                catch (Exception ex) { result.TrySetException(ex); }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    // Media Foundation releases the file asynchronously after Close.
                    _ = Task.Run(() => DeleteClipWithRetriesAsync(directory));
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "MyCapture media range calibration" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    // Best effort: a clip still held by Media Foundation stays in the OS temp root.
    private static bool DeleteClip(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task DeleteClipWithRetriesAsync(string directory)
    {
        for (int attempt = 0; attempt < 10 && !DeleteClip(directory); attempt++)
            await Task.Delay(500).ConfigureAwait(false);
    }

    private static void Encode(string path, CancellationToken cancellationToken)
    {
        int stride = Width * 4;
        byte[] pixels = new byte[stride * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = y * stride + x * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = SourceLevels[x * 4 / Width];
            pixels[offset + 3] = 255;
        }
        var options = new VideoEncoderOptions(path, Width, Height, 30,
            VideoEncoderOptions.DeriveBitrate(Width, Height, 30)) { FullRange = false };
        using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
        for (int i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoder.WriteFrame(new EncoderFrame(pixels, Width, Height, stride, i * 1000.0 / 30));
        }
        encoder.Complete();
    }

    private static async Task<bool> MeasureWindowAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Fill, IsMuted = true, Volume = 0, ScrubbingEnabled = true,
        };
        var window = new Window
        {
            // This window lives on its own UI thread. The app's implicit Window style carries
            // resources (the icon) owned by the main UI thread, so it must not apply here.
            Style = null,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = Width, Height = Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Content = media,
        };
        Exception? failure = null;
        media.MediaOpened += (_, _) => opened.TrySetResult();
        media.MediaFailed += (_, e) =>
        {
            failure = e.ErrorException;
            opened.TrySetException(e.ErrorException);
        };
        media.MediaEnded += (_, _) => { media.Position = TimeSpan.Zero; media.Play(); };
        try
        {
            window.Show();
            media.Source = new Uri(path);
            media.Play();
            await opened.Task.WaitAsync(cancellationToken);
            bool? previous = null;
            while (true)
            {
                await Task.Delay(80, cancellationToken);
                if (failure is not null) throw failure;
                // MediaOpened is not proof that a video frame has reached composition. Require
                // two consistent, complete patterns; blank/transparent/ambiguous frames retry.
                bool? measured = Classify(Sample(media));
                if (measured is not null && measured == previous) return measured.Value;
                previous = measured;
            }
        }
        finally
        {
            media.Close();
            media.Source = null;
            window.Close();
        }
    }

    private static byte[] Sample(MediaElement media)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(media) { Stretch = Stretch.Fill }, null, new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] patches = new byte[16];
        for (int i = 0; i < 4; i++)
            bitmap.CopyPixels(new Int32Rect((2 * i + 1) * Width / 8, Height / 2, 1, 1), patches, 4, i * 4);
        return patches;
    }

    /// <returns>True for studio RGB, false for full RGB, null for an unrecognized frame.</returns>
    internal static bool? Classify(ReadOnlySpan<byte> patches)
    {
        if (patches.Length != 16) return null;
        bool studio = true, full = true;
        for (int i = 0; i < 4; i++)
        {
            if (patches[i * 4 + 3] != 255) return null;
            for (int channel = 0; channel < 3; channel++)
            {
                int value = patches[i * 4 + channel];
                studio &= Math.Abs(value - StudioLevels[i]) <= 5;
                full &= Math.Abs(value - SourceLevels[i]) <= 5;
            }
        }
        return studio == full ? null : studio;
    }
}
