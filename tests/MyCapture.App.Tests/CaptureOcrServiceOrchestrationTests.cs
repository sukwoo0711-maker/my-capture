using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOcrServiceOrchestrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailabilityCheckIoFailure_UsesOriginalWindowsRequest(bool accessDenied)
    {
        OcrRequest request = NeuralRequest(enhanced: true);
        OcrResult expected = Success("original windows result");
        var windows = new FakeEngine(expected);
        int neuralCalls = 0;
        var service = new CaptureOcrService(windows, (_, _) =>
        {
            neuralCalls++;
            throw new InvalidOperationException("Must not invoke neural recognition.");
        }, () =>
        {
            if (accessDenied) throw new UnauthorizedAccessException("model hash");
            throw new IOException("model hash");
        },
            () => false, NullLogger<CaptureOcrService>.Instance);

        Assert.Same(expected, await service.RecognizeAsync(request));
        Assert.Same(request, windows.LastRequest);
        Assert.Equal(1, windows.CallCount);
        Assert.Equal(0, neuralCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AvailabilityCheckIoFailure_DoesNotEscapeIsAvailable(bool accessDenied)
    {
        var windows = new FakeEngine(Success("unused")) { IsAvailable = false };
        var service = new CaptureOcrService(windows, (_, _) => Task.FromResult(Success("unused")), () =>
        {
            if (accessDenied) throw new UnauthorizedAccessException("model hash");
            throw new IOException("model hash");
        }, () => false, NullLogger<CaptureOcrService>.Instance);

        Assert.False(service.IsAvailable);
    }

    [Theory]
    [InlineData(OcrStatus.Failed, "rotation")]
    [InlineData(OcrStatus.Unavailable, "rotation")]
    [InlineData(OcrStatus.Failed, "upscale")]
    [InlineData(OcrStatus.Unavailable, "upscale")]
    [InlineData(OcrStatus.Failed, "contrast")]
    [InlineData(OcrStatus.Unavailable, "contrast")]
    public async Task FailedNeural_RetriesWindowsWithOriginalPreprocessing(OcrStatus status, string option)
    {
        OcrRequest request = OcrRequest.FromBytes([1], upscaleFactor: option == "upscale" ? 4 : 1,
            searchRotatedOrientations: option == "rotation", enhanceContrast: option == "contrast", useNeuralModel: true);
        OcrResult expected = Success("original preprocessing only");
        var windows = new FakeEngine((supplied, _) => Task.FromResult(
            supplied.SearchRotatedOrientations == request.SearchRotatedOrientations &&
            supplied.UpscaleFactor == request.UpscaleFactor && supplied.EnhanceContrast == request.EnhanceContrast
                ? expected : OcrResult.NoText("ko-KR", TimeSpan.Zero)));
        var neural = new FakeEngine(status == OcrStatus.Failed ? OcrResult.Failed("failed") : OcrResult.Unavailable());

        Assert.Same(expected, await Create(windows, neural).RecognizeAsync(request));
        Assert.Same(request, windows.LastRequest);
        Assert.Equal(2, windows.CallCount);
        Assert.Equal(1, neural.CallCount);
    }

    [Theory]
    [InlineData(OcrStatus.Failed)]
    [InlineData(OcrStatus.Unavailable)]
    public async Task FailedNeural_KeepsFastTextWhenOriginalPreprocessingFindsNone(OcrStatus status)
    {
        OcrRequest request = OcrRequest.FromBytes([1], upscaleFactor: 4, searchRotatedOrientations: true,
            enhanceContrast: true, useNeuralModel: true);
        var windows = new FakeEngine((supplied, _) => Task.FromResult(supplied.UpscaleFactor == 1
            ? Success("fast only") : OcrResult.NoText("ko-KR", TimeSpan.Zero)));
        var neural = new FakeEngine(status == OcrStatus.Failed ? OcrResult.Failed("failed") : OcrResult.Unavailable());

        Assert.Equal("fast only", (await Create(windows, neural).RecognizeAsync(request)).Text);
        Assert.Same(request, windows.LastRequest);
        Assert.Equal(2, windows.CallCount);
    }

    [Theory]
    [InlineData(OcrStatus.Failed, false)]
    [InlineData(OcrStatus.Unavailable, false)]
    [InlineData(OcrStatus.NoText, true)]
    public async Task EquivalentFastRequestOrHealthyNoText_DoesNotRepeatWindows(OcrStatus status, bool preprocess)
    {
        OcrRequest request = OcrRequest.FromBytes([1], upscaleFactor: preprocess ? 4 : 1,
            searchRotatedOrientations: preprocess, enhanceContrast: preprocess, useNeuralModel: true);
        var windows = new FakeEngine(Success("windows"));
        var neural = new FakeEngine(status switch
        {
            OcrStatus.NoText => OcrResult.NoText("ko-KR", TimeSpan.Zero),
            OcrStatus.Unavailable => OcrResult.Unavailable(),
            _ => OcrResult.Failed("failed"),
        });
        Assert.Equal("windows", (await Create(windows, neural).RecognizeAsync(request)).Text);
        Assert.Equal(1, windows.CallCount);
    }

    [Fact]
    public async Task DualEngine_StartsBothBeforeEitherCompletes_AndUsesWallElapsed()
    {
        var windowsStarted = Signal();
        var neuralStarted = Signal();
        var release = Signal();
        using var cancellation = new CancellationTokenSource();
        var windows = new FakeEngine(async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            windowsStarted.SetResult();
            await release.Task.WaitAsync(token);
            return Success("shared text", TimeSpan.FromHours(1));
        });
        var neural = new FakeEngine(async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            neuralStarted.SetResult();
            await release.Task.WaitAsync(token);
            return Success("sharedtext", TimeSpan.FromHours(2));
        });
        var service = Create(windows, neural);

        Task<OcrResult> running = service.RecognizeAsync(NeuralRequest(), cancellation.Token);
        try
        {
            await Task.WhenAll(windowsStarted.Task, neuralStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        OcrResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.Equal("shared text", result.Text);
        Assert.InRange(result.Elapsed, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.Equal(1, windows.CallCount);
        Assert.Equal(1, neural.CallCount);
    }

    [Fact]
    public async Task SwitchDefaultsOn_RunsBothEnginesAndMergesTheirLines()
    {
        var windows = new FakeEngine(Success("shared text"));
        var neural = new FakeEngine(Success("sharedtext"));
        var service = new CaptureOcrService(
            windows, neural.RecognizeAsync, () => neural.IsAvailable, () => true,
            NullLogger<CaptureOcrService>.Instance);

        OcrResult result = await service.RecognizeAsync(NeuralRequest());

        Assert.Equal("shared text", result.Text);
        Assert.Equal(1, windows.CallCount);
        Assert.Equal(1, neural.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchOff_KeepsNeuralFirstBehavior(bool modelsInstalled)
    {
        var windows = new FakeEngine(Success("windows"));
        OcrResult expected = Success("neural");
        var neural = new FakeEngine(expected) { IsAvailable = modelsInstalled };
        var service = Create(windows, neural, enabled: false);
        OcrRequest request = NeuralRequest();

        OcrResult result = await service.RecognizeAsync(request);

        Assert.Same(expected, result);
        Assert.Same(request, neural.LastRequest);
        Assert.Equal(0, windows.CallCount);
        Assert.Equal(1, neural.CallCount);
    }

    [Fact]
    public async Task SwitchOff_NeuralFailureKeepsOriginalWindowsFallbackRequest()
    {
        OcrRequest request = NeuralRequest(enhanced: true);
        OcrResult expected = Success("windows fallback");
        var windows = new FakeEngine(expected);
        var neural = new FakeEngine(OcrResult.Failed("failed"));

        OcrResult result = await Create(windows, neural, enabled: false).RecognizeAsync(request);

        Assert.Same(expected, result);
        Assert.Same(request, windows.LastRequest);
        Assert.Equal(1, neural.CallCount);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task FastAndBalanced_UseOnlyWindowsAndKeepTheRequest(double scale)
    {
        OcrRequest request = OcrRequest.FromBytes([1], upscaleFactor: scale);
        OcrResult expected = Success("windows only");
        var windows = new FakeEngine(expected);
        var neural = new FakeEngine(Success("neural"));

        OcrResult result = await Create(windows, neural).RecognizeAsync(request);

        Assert.Same(expected, result);
        Assert.Same(request, windows.LastRequest);
        Assert.Equal(0, neural.CallCount);
    }

    [Fact]
    public async Task NeuralUnavailable_UsesOriginalWindowsFallbackWithoutCallingNeural()
    {
        OcrRequest request = NeuralRequest(enhanced: true);
        OcrResult expected = Success("windows fallback");
        var windows = new FakeEngine(expected);
        var neural = new FakeEngine(Success("neural")) { IsAvailable = false };

        OcrResult result = await Create(windows, neural).RecognizeAsync(request);

        Assert.Same(expected, result);
        Assert.Same(request, windows.LastRequest);
        Assert.Equal(0, neural.CallCount);
    }

    [Theory]
    [InlineData(false, "ko-KR")]
    [InlineData(true, "ja-JP")]
    public async Task WindowsUnavailableOrUnsupported_UsesOnlyNeural(bool windowsAvailable, string windowsLanguage)
    {
        OcrRequest request = NeuralRequest(enhanced: true);
        OcrResult expected = Success("neural only");
        var windows = new FakeEngine(Success("windows"))
        {
            IsAvailable = windowsAvailable,
            SupportedLanguages = [windowsLanguage],
        };
        var neural = new FakeEngine(expected);

        OcrResult result = await Create(windows, neural).RecognizeAsync(request);

        Assert.Same(expected, result);
        Assert.Same(request, neural.LastRequest);
        Assert.Equal(0, windows.CallCount);
    }

    [Theory]
    [InlineData("bytes", false)]
    [InlineData("bytes", true)]
    [InlineData("file", true)]
    [InlineData("bitmap", true)]
    public async Task DualEngine_PreservesNeuralPreprocessing_AndUsesFastWindowsClone(string sourceKind, bool enhanced)
    {
        OcrRequest request = NeuralRequest(enhanced, sourceKind);
        var windows = new FakeEngine(Success("same text"));
        var neural = new FakeEngine(Success("sametext"));

        await Create(windows, neural).RecognizeAsync(request);

        Assert.Same(request, neural.LastRequest);
        OcrRequest fast = Assert.IsType<OcrRequest>(windows.LastRequest);
        Assert.NotSame(request, fast);
        Assert.Same(request.EncodedImage, fast.EncodedImage);
        Assert.Equal(request.FilePath, fast.FilePath);
        Assert.Same(request.Bitmap, fast.Bitmap);
        Assert.Same(request.PreferredLanguages, fast.PreferredLanguages);
        Assert.False(fast.SearchRotatedOrientations);
        Assert.Equal(1.0, fast.UpscaleFactor);
        Assert.False(fast.EnhanceContrast);
        Assert.False(fast.LocalCorrection);
        Assert.False(fast.UseSuperResolution);
        Assert.False(fast.UseNeuralModel);
        Assert.True(request.EnhanceContrast);
        Assert.Equal(enhanced, request.LocalCorrection);
        Assert.Equal(enhanced, request.UseSuperResolution);
        Assert.Equal(enhanced ? 4.0 : 2.0, request.UpscaleFactor);
    }

    [Fact]
    public async Task DualEngine_DetachesDecodedJpegBeforeCrossThreadPreprocessing()
    {
        Task<OcrResult>? running = null;
        int sourceThread = 0, preprocessingThread = 0;
        StaTestHost.Run(() =>
        {
            sourceThread = Environment.CurrentManagedThreadId;
            BitmapFrame frame = DecodeJpegInMemory();
            Assert.True(frame.IsFrozen);
            Assert.NotEqual(PixelFormats.Bgra32, frame.Format);
            OcrRequest request = OcrRequest.FromBitmap(frame, 4, ["ko-KR", "en-US"],
                searchRotatedOrientations: true, enhanceContrast: true, useNeuralModel: true,
                localCorrection: true, useSuperResolution: true);
            var windows = new FakeEngine(OcrResult.NoText("ko-KR", TimeSpan.Zero));
            var neural = new FakeEngine((supplied, token) => Task.Run(() =>
            {
                preprocessingThread = Environment.CurrentManagedThreadId;
                // This exact conversion/CanFreeze path failed for the frozen JPEG frame
                // during the real dual-engine benchmark. Use the supplied source here.
                BitmapSource contrasted = ImageCodec.StretchContrastForRecognition(supplied.Bitmap!);
                return contrasted.PixelWidth > 0 ? Success("neural text") : OcrResult.Failed("empty image");
            }, token));

            running = Create(windows, neural).RecognizeAsync(request);

            OcrRequest prepared = Assert.IsType<OcrRequest>(neural.LastRequest);
            BitmapSource snapshot = Assert.IsAssignableFrom<BitmapSource>(prepared.Bitmap);
            Assert.NotSame(request, prepared);
            Assert.NotSame(frame, snapshot);
            Assert.False(snapshot is BitmapFrame);
            Assert.True(snapshot.IsFrozen);
            Assert.Same(snapshot, windows.LastRequest!.Bitmap);
            Assert.Equal(frame.PixelWidth, snapshot.PixelWidth);
            Assert.Equal(frame.PixelHeight, snapshot.PixelHeight);
            Assert.Equal(frame.DpiX, snapshot.DpiX);
            Assert.Equal(frame.DpiY, snapshot.DpiY);
            Assert.Equal(frame.Format, snapshot.Format);
            Assert.Equal(ReadPixels(frame), ReadPixels(snapshot));
            Assert.Same(request.PreferredLanguages, prepared.PreferredLanguages);
            Assert.Equal(request.UpscaleFactor, prepared.UpscaleFactor);
            Assert.Equal(request.SearchRotatedOrientations, prepared.SearchRotatedOrientations);
            Assert.Equal(request.EnhanceContrast, prepared.EnhanceContrast);
            Assert.Equal(request.UseNeuralModel, prepared.UseNeuralModel);
            Assert.Equal(request.LocalCorrection, prepared.LocalCorrection);
            Assert.Equal(request.UseSuperResolution, prepared.UseSuperResolution);
        });

        OcrResult result = await running!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(sourceThread, preprocessingThread);
        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.Equal("neural text", result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareDualRequest_KeepsExistingInMemoryBitmapIdentity(bool rendered) => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = rendered
            ? new RenderTargetBitmap(8, 6, 120, 144, PixelFormats.Pbgra32)
            : new WriteableBitmap(8, 6, 120, 144, PixelFormats.Bgra32, null);
        bitmap.Freeze();
        OcrRequest request = OcrRequest.FromBitmap(bitmap, useNeuralModel: true);

        OcrRequest prepared = CaptureOcrService.PrepareDualRequest(request);

        Assert.Same(request, prepared);
        Assert.Same(bitmap, prepared.Bitmap);
    });

    [Fact]
    public async Task SwitchOff_KeepsDecodedFrameAndOriginalRequest()
    {
        Task<OcrResult>? running = null;
        StaTestHost.Run(() =>
        {
            BitmapFrame frame = DecodeJpegInMemory();
            OcrRequest request = OcrRequest.FromBitmap(frame, useNeuralModel: true);
            var windows = new FakeEngine(Success("windows"));
            var neural = new FakeEngine(Success("neural"));

            running = Create(windows, neural, enabled: false).RecognizeAsync(request);

            Assert.Same(request, neural.LastRequest);
            Assert.Same(frame, neural.LastRequest!.Bitmap);
            Assert.Equal(0, windows.CallCount);
        });

        Assert.Equal(OcrStatus.Success, (await running!).Status);
    }

    [Fact]
    public async Task CancellationBeforeStart_DoesNotInvokeEitherEngine()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var windows = new FakeEngine(Success("windows"));
        var neural = new FakeEngine(Success("neural"));

        OcrResult result = await Create(windows, neural).RecognizeAsync(NeuralRequest(), cancellation.Token);

        Assert.Equal(OcrStatus.Cancelled, result.Status);
        Assert.Equal(0, windows.CallCount);
        Assert.Equal(0, neural.CallCount);
    }

    [Fact]
    public async Task CancellationDuringRecognition_ReachesBothEngines_AndReturnsCancelled()
    {
        var windowsStarted = Signal();
        var neuralStarted = Signal();
        using var cancellation = new CancellationTokenSource();
        var windows = new FakeEngine(async (_, token) =>
        {
            windowsStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Success("windows");
        });
        var neural = new FakeEngine(async (_, token) =>
        {
            neuralStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Success("neural");
        });

        Task<OcrResult> running = Create(windows, neural).RecognizeAsync(NeuralRequest(), cancellation.Token);
        try
        {
            await Task.WhenAll(windowsStarted.Task, neuralStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            cancellation.Cancel();
        }

        OcrResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OcrStatus.Cancelled, result.Status);
        Assert.Equal(cancellation.Token, windows.LastToken);
        Assert.Equal(cancellation.Token, neural.LastToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledEngineResult_IsNotReplacedByOtherEngineSuccess(bool windowsCancels)
    {
        var windows = new FakeEngine(windowsCancels ? OcrResult.Cancelled() : Success("windows"));
        var neural = new FakeEngine(windowsCancels ? Success("neural") : OcrResult.Cancelled());

        OcrResult result = await Create(windows, neural).RecognizeAsync(NeuralRequest());

        Assert.Equal(OcrStatus.Cancelled, result.Status);
    }

    [Theory]
    [InlineData(true, OcrStatus.Failed)]
    [InlineData(false, OcrStatus.Failed)]
    [InlineData(true, OcrStatus.Unavailable)]
    [InlineData(false, OcrStatus.Unavailable)]
    [InlineData(true, OcrStatus.NoText)]
    [InlineData(false, OcrStatus.NoText)]
    public async Task EngineFailureOrEmptyResult_PreservesOtherEngineText(bool windowsFails, OcrStatus failureStatus)
    {
        OcrResult failure = failureStatus switch
        {
            OcrStatus.Unavailable => OcrResult.Unavailable(),
            OcrStatus.NoText => OcrResult.NoText("ko-KR", TimeSpan.Zero),
            _ => OcrResult.Failed("failed"),
        };
        var windows = new FakeEngine(windowsFails ? failure : Success("healthy text"));
        var neural = new FakeEngine(windowsFails ? Success("healthy text") : failure);

        OcrResult result = await Create(windows, neural).RecognizeAsync(NeuralRequest());

        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.Equal("healthy text", result.Text);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task EngineException_PreservesOtherEngineText(bool windowsThrows, bool synchronous)
    {
        var broken = new FakeEngine((_, _) => synchronous
            ? throw new InvalidOperationException("synthetic failure")
            : Task.FromException<OcrResult>(new InvalidOperationException("synthetic failure")));
        var healthy = new FakeEngine(Success("healthy text"));
        var service = windowsThrows ? Create(broken, healthy) : Create(healthy, broken);

        OcrResult result = await service.RecognizeAsync(NeuralRequest());

        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.Equal("healthy text", result.Text);
    }

    [Fact]
    public async Task BothEnginesThrow_ReturnsTypedFailure()
    {
        var windows = new FakeEngine((_, _) => throw new InvalidOperationException("windows failure"));
        var neural = new FakeEngine((_, _) => throw new InvalidOperationException("neural failure"));

        OcrResult result = await Create(windows, neural).RecognizeAsync(NeuralRequest());

        Assert.Equal(OcrStatus.Failed, result.Status);
        Assert.False(result.HasText);
    }

    private static CaptureOcrService Create(FakeEngine windows, FakeEngine neural, bool enabled = true) =>
        new(windows, neural.RecognizeAsync, () => neural.IsAvailable, () => true,
            NullLogger<CaptureOcrService>.Instance, enableDualEngine: enabled);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static BitmapFrame DecodeJpegInMemory()
    {
        const int width = 16, height = 8, stride = width * 3;
        byte[] pixels = new byte[stride * height];
        for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(80 + index % 96);
        BitmapSource source = BitmapSource.Create(width, height, 120, 144, PixelFormats.Rgb24, null, pixels, stride);
        source.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        encoded.Position = 0;
        BitmapFrame decoded = BitmapDecoder.Create(encoded, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        decoded.Freeze();
        return decoded;
    }

    private static byte[] ReadPixels(BitmapSource bitmap)
    {
        int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static OcrRequest NeuralRequest(bool enhanced = false, string sourceKind = "bytes")
    {
        string[] languages = ["ko", "en-US"];
        double scale = enhanced ? 4.0 : 2.0;
        bool rotate = !enhanced;
        if (sourceKind == "file")
        {
            // The fake never opens this source; no profile or test directory is needed.
            return OcrRequest.FromFile("synthetic-image.png", scale, languages, rotate,
                enhanceContrast: true, useNeuralModel: true,
                localCorrection: enhanced, useSuperResolution: enhanced);
        }

        if (sourceKind == "bitmap")
        {
            BitmapSource bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            return OcrRequest.FromBitmap(bitmap, scale, languages, rotate,
                enhanceContrast: true, useNeuralModel: true,
                localCorrection: enhanced, useSuperResolution: enhanced);
        }

        return OcrRequest.FromBytes([1, 2, 3], scale, languages, rotate,
            enhanceContrast: true, useNeuralModel: true,
            localCorrection: enhanced, useSuperResolution: enhanced);
    }

    private static OcrResult Success(string text, TimeSpan elapsed = default)
    {
        var bounds = new OcrRect(10, 10, 120, 20);
        return OcrResult.Success(text, "ko-KR", [new OcrLine(text, bounds, [new OcrWord(text, bounds)])], elapsed);
    }

    private sealed class FakeEngine : IOcrService
    {
        private readonly Func<OcrRequest, CancellationToken, Task<OcrResult>> _recognize;

        internal FakeEngine(OcrResult result) : this((_, _) => Task.FromResult(result))
        {
        }

        internal FakeEngine(Func<OcrRequest, CancellationToken, Task<OcrResult>> recognize) => _recognize = recognize;

        public bool IsAvailable { get; init; } = true;
        public IReadOnlyList<string> SupportedLanguages { get; init; } = ["ko-KR", "en-US"];
        internal int CallCount { get; private set; }
        internal OcrRequest? LastRequest { get; private set; }
        internal CancellationToken LastToken { get; private set; }

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            LastToken = cancellationToken;
            return _recognize(request, cancellationToken);
        }
    }
}
