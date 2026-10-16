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

internal enum VideoRangeCorrection { None, Expand, Compress }
internal enum VideoRangePresentation { Correct, ExpandedAsStudio, StudioRetained }

/// <summary>Both input ranges measured against the original RGB, before any preview effect.</summary>
internal readonly record struct VideoRangeCalibrationResult(
    VideoRangePresentation? Studio, VideoRangePresentation? Full)
{
    internal VideoRangeCorrection CorrectionFor(bool studioRange) => (studioRange ? Studio : Full) switch
    {
        VideoRangePresentation.Correct => VideoRangeCorrection.None,
        VideoRangePresentation.ExpandedAsStudio => VideoRangeCorrection.Compress,
        VideoRangePresentation.StudioRetained => VideoRangeCorrection.Expand,
        _ => studioRange ? VideoRangeCorrection.Expand : VideoRangeCorrection.None,
    };
}

/// <summary>A lazy, process-wide measurement of both input ranges with one bounded, cached fallback.</summary>
internal sealed class StudioRangePresentationCalibration
{
    private readonly Lazy<Task<VideoRangeCalibrationResult>> _result;

    internal StudioRangePresentationCalibration(
        Func<CancellationToken, Task<VideoRangeCalibrationResult>> measure,
        TimeSpan? timeout = null,
        Action<Exception>? logFailure = null)
    {
        _result = new Lazy<Task<VideoRangeCalibrationResult>>(() => MeasureOnceAsync(
            measure, timeout ?? TimeSpan.FromSeconds(8), logFailure ?? (ex =>
                Trace.TraceWarning("MediaElement range calibration failed; retaining studio Expand / full None: {0}", ex))));
    }

    // Reading a decision does not start work. Unknown/failed measurements retain the old policy.
    internal VideoRangeCorrection CorrectionFor(bool studioRange) =>
        (_result.IsValueCreated && _result.Value.IsCompletedSuccessfully ? _result.Value.Result : default)
            .CorrectionFor(studioRange);
    internal bool IsStarted => _result.IsValueCreated;
    internal Task<VideoRangeCalibrationResult> GetResultAsync() => _result.Value;
    internal Exception? Failure { get; private set; }

    private async Task<VideoRangeCalibrationResult> MeasureOnceAsync(
        Func<CancellationToken, Task<VideoRangeCalibrationResult>> measure, TimeSpan timeout, Action<Exception> logFailure)
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        try
        {
            // One budget covers both encodes and both presentations. Even a synchronously blocking
            // encoder/driver must not block Apply or its UI thread.
            VideoRangeCalibrationResult result = await Task.Run(() => measure(token))
                .WaitAsync(timeout).ConfigureAwait(false);
            Trace.TraceInformation("MediaElement range calibration: studio={0}, full={1}", result.Studio, result.Full);
            return result;
        }
        catch (Exception ex)
        {
            Failure = ex;
            logFailure(ex);
            return default;
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
    private static readonly byte[] ExpandedLevels = [0, 56, 205, 255];

    internal static Task<VideoRangeCalibrationResult> MeasureAsync(CancellationToken cancellationToken)
    {
        // The caller runs this on the pool. Keep MF encoding off every WPF dispatcher. The tiny
        // clips live in the app's OS-temp diagnostic root, never in the user's capture library,
        // and are removed once the presenter has been measured.
        string directory = ValidatedFilePaths.CreateTemporaryDirectory();
        string studioPath = Path.Combine(directory, "studio-range-calibration.mp4");
        string fullPath = Path.Combine(directory, "full-range-calibration.mp4");
        try
        {
            Encode(studioPath, fullRange: false, cancellationToken);
            Encode(fullPath, fullRange: true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            _ = DeleteClip(directory);
            throw;
        }

        var result = new TaskCompletionSource<VideoRangeCalibrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { result.TrySetResult(await MeasureWindowAsync(studioPath, fullPath, cancellationToken)); }
                catch (Exception ex) { result.TrySetException(ex); }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    // Media Foundation releases the files asynchronously after Close.
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

    internal static void Encode(string path, bool fullRange, CancellationToken cancellationToken,
        int width = Width, int height = Height)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = SourceLevels[x * 4 / width];
            pixels[offset + 3] = 255;
        }
        var options = new VideoEncoderOptions(path, width, height, 30,
            VideoEncoderOptions.DeriveBitrate(width, height, 30)) { FullRange = fullRange };
        using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
        for (int i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, i * 1000.0 / 30));
        }
        encoder.Complete();
    }

    internal static Window CreateWindow(int width = Width, int height = Height) =>
        new Window
        {
            // This window lives on its own UI thread. The app's implicit Window style carries
            // resources (the icon) owned by the main UI thread, so it must not apply here.
            Style = null,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = width, Height = height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false,
        };

    private static async Task<VideoRangeCalibrationResult> MeasureWindowAsync(
        string studioPath, string fullPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = CreateWindow();
        try
        {
            window.Show();
            VideoRangePresentation studio = await MeasureClipAsync(window, studioPath, cancellationToken);
            VideoRangePresentation full = await MeasureClipAsync(window, fullPath, cancellationToken);
            return new(studio, full);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<VideoRangePresentation> MeasureClipAsync(
        Window window, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A fresh element in the same window avoids sampling the previous clip's retained frame
        // while the next clip is opening. Both clips deliberately use the same source pattern.
        var media = CreateMediaElement();
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
            window.Content = media;
            media.Source = new Uri(path);
            media.Play();
            await opened.Task.WaitAsync(cancellationToken);
            var samples = new ConsistentSamples();
            while (true)
            {
                await Task.Delay(80, cancellationToken);
                if (failure is not null) throw failure;
                // MediaOpened is not proof that a video frame has reached composition.
                if (samples.Add(Sample(media)) is { } measured) return measured;
            }
        }
        finally
        {
            media.Close();
            media.Source = null;
            window.Content = null;
        }
    }

    internal static MediaElement CreateMediaElement() => new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
        Stretch = Stretch.Fill, IsMuted = true, Volume = 0, ScrubbingEnabled = true,
    };

    // Shared by calibration and the device diagnostic. This samples the visual presenter only;
    // it must never supply saved frames, layers, posters, or export inputs.
    internal static byte[] Sample(MediaElement media)
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

    /// <returns>The presented pattern relative to source RGB, or null for an ambiguous/incomplete frame.</returns>
    internal static VideoRangePresentation? Classify(ReadOnlySpan<byte> patches)
    {
        if (patches.Length != 16) return null;
        bool studio = true, correct = true, expanded = true;
        for (int i = 0; i < 4; i++)
        {
            if (patches[i * 4 + 3] != 255) return null;
            for (int channel = 0; channel < 3; channel++)
            {
                int value = patches[i * 4 + channel];
                studio &= Math.Abs(value - StudioLevels[i]) <= 5;
                correct &= Math.Abs(value - SourceLevels[i]) <= 5;
                expanded &= Math.Abs(value - ExpandedLevels[i]) <= 5;
            }
        }
        // The 192 -> 205 patch separates correct/expanded even where the 64 -> 56 tolerances
        // overlap. Require the whole pattern and exactly one match, never a nearest guess.
        if ((studio ? 1 : 0) + (correct ? 1 : 0) + (expanded ? 1 : 0) != 1) return null;
        return studio ? VideoRangePresentation.StudioRetained
            : expanded ? VideoRangePresentation.ExpandedAsStudio : VideoRangePresentation.Correct;
    }

    internal sealed class ConsistentSamples
    {
        private VideoRangePresentation? _previous;

        internal VideoRangePresentation? Add(ReadOnlySpan<byte> patches)
        {
            VideoRangePresentation? measured = Classify(patches);
            VideoRangePresentation? confirmed = measured is not null && measured == _previous ? measured : null;
            _previous = measured; // An incomplete/ambiguous frame breaks the consecutive pair.
            return confirmed;
        }
    }
}
