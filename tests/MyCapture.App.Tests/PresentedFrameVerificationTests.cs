using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PresentedFrameVerificationTests
{
    [Theory]
    [InlineData(0, 0.1)] [InlineData(0, 0.2)]
    [InlineData(1, 0.1)] [InlineData(1, 0.2)]
    [InlineData(2, 0.1)] [InlineData(2, 0.2)]
    public void ModelsSurviveNoiseAndOutliers(int model, double outliers)
    {
        var reference = Pattern();
        var random = new Random(8181);
        byte[] pixels = (byte[])reference.Pixels.Clone();
        // Corrupt whole patches of sampled points, rather than only independent colour channels.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            bool corrupt = random.NextDouble() < outliers;
            for (int c = 0; c < 3; c++)
            {
                double value = Transform(pixels[i + c], model);
                pixels[i + c] = (byte)Math.Clamp(Math.Round(corrupt ? random.Next(256) : value + random.Next(-2, 3)), 0, 255);
            }
        }
        var result = PresentedFrameRangeModel.Compare(reference with { Pixels = pixels }, reference);
        Assert.Equal((VideoRangePresentation)model, result.Model);
        Assert.True(result.Points >= 64);
        Assert.True(result.DynamicRange >= 96);
    }

    [Fact]
    public void LowRangeTooFewFlatPointsAndCloseModelsAreInconclusive()
    {
        var constant = Pattern();
        for (int i = 0; i < constant.Pixels.Length; i += 4)
            for (int c = 0; c < 3; c++) constant.Pixels[i + c] = 128;
        Assert.Equal("low-dynamic-range", PresentedFrameRangeModel.Compare(constant, constant).Status);
        var tiny = new RangeVerificationFrame(8, 8, new byte[8 * 8 * 4], 0);
        Assert.Equal("too-few-flat-points", PresentedFrameRangeModel.Compare(tiny, tiny).Status);
        var reference = Pattern();
        byte[] midpoint = (byte[])reference.Pixels.Clone();
        for (int i = 0; i < midpoint.Length; i += 4)
        for (int c = 0; c < 3; c++) midpoint[i + c] = (byte)Math.Round((midpoint[i + c] + Transform(midpoint[i + c], 2)) / 2);
        var result = PresentedFrameRangeModel.Compare(reference with { Pixels = midpoint }, reference);
        Assert.Null(result.Model);
        Assert.Equal("measured", result.Kind);
        Assert.Equal("verified", result.Status);
    }

    [Fact]
    public void EdgesAndTransparentPointsAreExcluded()
    {
        var reference = Pattern();
        for (int y = 0; y < reference.Height; y++)
        for (int x = 0; x < reference.Width; x++)
        for (int c = 0; c < 3; c++) reference.Pixels[(y * reference.Width + x) * 4 + c] = (byte)((x + y) % 2 * 255);
        Assert.Equal(0, PresentedFrameRangeModel.Compare(reference, reference).Points);
        reference = Pattern();
        var transparent = reference with { Pixels = new byte[reference.Pixels.Length] };
        Assert.Equal(0, PresentedFrameRangeModel.Compare(transparent, reference).Points);
    }

    [Fact]
    public void AreaDownscaleAccountsForFractionalCoverage()
    {
        byte[] pixels = [0, 0, 0, 0, 90, 90, 90, 0, 240, 240, 240, 0];
        var result = PresentedFrameRangeModel.AreaDownscale(pixels, 3, 1, 12, 2, 1, 123);
        Assert.Equal(new byte[] { 30, 30, 30, 255, 190, 190, 190, 255 }, result.Pixels);
        Assert.Equal(123, result.PositionMs);
    }

    [Fact]
    public async Task DefersUntilPresentedAndIdleAndDoesNoWorkForAnEmptyPlayer()
    {
        var fake = new Harness();
        using var session = fake.Create();
        fake.Now = 10000;
        await session.TickAsync();
        Assert.Equal(0, fake.SampleCalls); Assert.Equal(0, fake.DecodeCalls);
        fake.Presented = true;
        session.PositionRequested();
        fake.Now += 299; await session.TickAsync();
        Assert.Equal(0, fake.SampleCalls);
        session.PositionRequested();
        fake.Now += 299; await session.TickAsync();
        Assert.Equal(0, fake.SampleCalls);
        fake.Now++; await session.TickAsync();
        Assert.True(session.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, fake.SampleCalls); Assert.Equal(1, fake.DecodeCalls); Assert.Equal(1, fake.Logs);
        fake.Now += 10000; await session.TickAsync();
        Assert.Equal(1, fake.SampleCalls);
    }

    [Fact]
    public async Task FiveSecondsOfScrubRequestsStartNoSamplingOrDecode()
    {
        var fake = new Harness { Presented = true };
        using var session = fake.Create();
        for (int i = 0; i < 300; i++)
        {
            fake.Now = i * (5000.0 / 300);
            session.PositionRequested();
            await session.TickAsync();
        }
        Assert.Equal(0, session.Samples); Assert.Equal(0, session.Decodes);
        Assert.False(session.IsRunning); Assert.False(session.Completion.IsCompleted);
        fake.Now += 300; await session.TickAsync();
        Assert.Equal(1, session.Samples); Assert.Equal(1, session.Decodes);
        Assert.True(session.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RetriesAtGrowingDelaysThenRemovesUnverifiedCorrectionAndLogsOnce()
    {
        var fake = new Harness { Presented = true, Result = Inconclusive() };
        using var session = fake.Create();
        foreach (int delay in new[] { 300, 500, 2000, 5000 })
        {
            int calls = fake.SampleCalls;
            fake.Now += delay - 1; await session.TickAsync(); Assert.Equal(calls, fake.SampleCalls);
            fake.Now++; await session.TickAsync(); Assert.Equal(calls + 1, fake.SampleCalls);
        }
        Assert.Equal(4, (await session.Completion).Attempts);
        Assert.Null(session.LastResult!.Model);
        Assert.Equal(VideoRangeCorrection.None, fake.Correction);
        Assert.Equal("fallback", session.LastResult.Kind);
        Assert.Equal(1, fake.Logs);
        fake.Now += 10000; await session.TickAsync(); Assert.Equal(4, fake.SampleCalls);
        using var next = fake.Create(); Assert.Equal(VideoRangeCorrection.Expand, fake.Correction);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CloseOrReplacementDiscardsLateDecode(bool replacement)
    {
        var pending = new TaskCompletionSource<RangeVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Harness { Presented = true, Pending = pending.Task };
        using var old = fake.Create(); fake.Now = 300;
        Task tick = old.TickAsync();
        Assert.Equal(1, fake.DecodeCalls);
        old.Dispose();
        using var next = replacement ? fake.Create() : null;
        pending.SetResult(Verified(VideoRangePresentation.Correct));
        await tick;
        Assert.True(old.Completion.IsCanceled);
        Assert.Equal(VideoRangeCorrection.Expand, fake.Correction); Assert.Equal(0, fake.Logs);
    }

    [Fact]
    public async Task SeekCancelsInflightDecodeAndDefersNewVerification()
    {
        var pending = new TaskCompletionSource<RangeVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Harness { Presented = true, Pending = pending.Task };
        using var session = fake.Create(); fake.Now = 300;
        Task first = session.TickAsync(); session.PositionRequested();
        Assert.True(fake.Token.IsCancellationRequested);
        pending.SetResult(Verified(VideoRangePresentation.Correct)); await first;
        Assert.Null(session.LastResult); Assert.Equal(0, fake.Logs);
        fake.Pending = null; fake.Now += 299; await session.TickAsync(); Assert.Equal(1, fake.DecodeCalls);
        fake.Now++; await session.TickAsync(); Assert.Equal(2, fake.DecodeCalls); Assert.Equal(1, fake.Logs);
    }

    [Fact]
    public async Task CancelledDriverWorkStaysVisibleAndCannotStartAnotherDecode()
    {
        var pending = new TaskCompletionSource<RangeVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Harness { Presented = true, Pending = pending.Task };
        using var session = fake.Create(); fake.Now = 300;
        Task first = session.TickAsync(); session.PositionRequested();
        await first;
        Assert.True(session.IsRunning);
        fake.Now += 10000; await session.TickAsync();
        Assert.Equal(1, fake.DecodeCalls);
        pending.SetResult(Verified(VideoRangePresentation.Correct));
        Assert.False(session.IsRunning); Assert.Null(session.LastResult); Assert.Equal(0, fake.Logs);
    }

    [Fact]
    public async Task DeviceGalleryRetainsStudioButEditorIsIdentityAndCacheIsHostRangeScaleSpecific()
    {
        var cache = new RangeVerificationCache();
        var gallery = new Harness { Presented = true, PresenterModel = 2 };
        var editor = new Harness { Presented = true, PresenterModel = 0 };
        using var gs = gallery.Create(cache, VideoPlayerKind.Gallery);
        using var es = editor.Create(cache, VideoPlayerKind.Editor);
        gallery.Now = editor.Now = 300;
        await gs.TickAsync(); await es.TickAsync();
        Assert.Equal(VideoRangeCorrection.Expand, gallery.Correction);
        Assert.Equal(VideoRangeCorrection.None, editor.Correction);
        var later = new Harness(); using var next = later.Create(cache, VideoPlayerKind.Editor);
        Assert.Equal(VideoRangeCorrection.None, later.Correction); Assert.Equal(0, later.DecodeCalls);
        Assert.Equal(VideoRangeCorrection.Expand, cache.Guess(new(VideoPlayerKind.Editor, true, 2)));
        Assert.Equal(VideoRangeCorrection.None, cache.Guess(new(VideoPlayerKind.Gallery, false, 0)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SamplingRestoresEffectBeforeAnyCompositionEvenOnFailure(bool failure) => StaTestHost.Run(() =>
    {
        var effect = new StudioRangeVideoEffect();
        var media = new MediaElement { Effect = effect };
        bool composition = false;
        EventHandler handler = (_, _) => { composition = true; Assert.Same(effect, media.Effect); };
        CompositionTarget.Rendering += handler;
        try
        {
            bool queued = false;
            _ = media.Dispatcher.InvokeAsync(() => { queued = true; Assert.Same(effect, media.Effect); }, DispatcherPriority.Render);
            void Sample()
            {
                MediaElementRangeVerification.WithoutEffect(media, () =>
                {
                    Assert.Null(media.Effect); Assert.False(queued);
                    Assert.Throws<InvalidOperationException>(() => Dispatcher.PushFrame(new DispatcherFrame()));
                    if (failure) throw new InvalidOperationException("render failure");
                    return true;
                });
            }
            if (failure) Assert.Throws<InvalidOperationException>(Sample); else Sample();
            Assert.Same(effect, media.Effect);
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(80));
            Assert.True(queued);
            Assert.True(composition);
        }
        finally { CompositionTarget.Rendering -= handler; }
    });

    [Fact]
    public void NoHardwareDoesNotCreateVerification() => StaTestHost.Run(() =>
    {
        Func<bool> previous = StudioRangeVideoEffect.HardwareShaderAvailable;
        try
        {
            StudioRangeVideoEffect.HardwareShaderAvailable = () => false;
            var media = new MediaElement { Source = new Uri(@"C:\fixture.mp4") };
            StudioRangeVideoEffect.Apply(media, true);
            Assert.Null(media.Effect); Assert.Null(StudioRangeVideoEffect.VerificationFor(media));
            StudioRangeVideoEffect.Clear(media);
        }
        finally { StudioRangeVideoEffect.HardwareShaderAvailable = previous; }
    });

    private static RangeVerificationResult Verified(VideoRangePresentation model) =>
        new(model, new(0, 12, 10), [], 100, 255, 0, 123, "verified");
    private static RangeVerificationResult Inconclusive() => Verified(VideoRangePresentation.Correct) with { Model = null, Status = "model-mismatch" };
    private static double Transform(byte value, int model) => model switch
    {
        0 => value, 1 => Math.Clamp((value - 16) * 255.0 / 219, 0, 255), _ => 16 + value * 219.0 / 255,
    };
    private static RangeVerificationFrame Pattern()
    {
        const int width = 320, height = 180;
        byte[] pixels = new byte[width * height * 4];
        byte[] levels = [0, 16, 64, 96, 128, 192, 235, 255];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            for (int c = 0; c < 3; c++) pixels[i + c] = levels[(x / 40 + c) % levels.Length];
            pixels[i + 3] = 255;
        }
        return new(width, height, pixels, 123);
    }
    private sealed class Harness
    {
        internal double Now;
        internal bool Presented;
        internal int SampleCalls, DecodeCalls, Logs, PresenterModel;
        internal VideoRangeCorrection Correction;
        internal CancellationToken Token;
        internal RangeVerificationResult? Result;
        internal Task<RangeVerificationResult>? Pending;
        internal PresentedFrameVerification Create(RangeVerificationCache? cache = null, VideoPlayerKind kind = VideoPlayerKind.Editor) =>
            new(new(kind, true, 0), cache ?? new(), () => Now, () => Presented, () =>
            {
                SampleCalls++;
                var frame = Pattern();
                for (int i = 0; i < frame.Pixels.Length; i += 4)
                for (int c = 0; c < 3; c++) frame.Pixels[i + c] = (byte)Math.Round(Transform(frame.Pixels[i + c], PresenterModel));
                return frame;
            }, (sample, token) =>
            {
                DecodeCalls++; Token = token;
                return Pending ?? Task.FromResult(Result ?? PresentedFrameRangeModel.Compare(sample, Pattern()));
            }, decision => Correction = decision.Correction, _ => Logs++);
    }
}
