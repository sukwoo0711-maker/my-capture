using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTextCopyServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recognition_CopiesOnlySuccessfulText(bool recognized)
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        string? copied = null;
        var ocr = new FakeOcr(recognized ? OcrResult.Success("exact\r\ntext", "en", [], TimeSpan.Zero) : OcrResult.Unavailable());
        bool success = await PinTextCopyService.CopyAsync(image, ocr,
            text => { copied = text; return Task.FromResult(true); }, 2, ["en"]);
        Assert.Equal(recognized, success);
        Assert.Equal(recognized ? "exact\r\ntext" : null, copied);
        Assert.Same(image, ocr.Request?.Bitmap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameImage_EquivalentLanguageLists_RecognizesOnceAndCopiesEachTime(bool useSettings)
    {
        var image = CreateImage();
        var ocr = new FakeOcr(Success("exact\r\ntext"));
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await CopyAsync(image, ocr, Copy, 2, ["en", "ko"], useSettings));
        Assert.True(await CopyAsync(image, ocr, Copy, 2, new List<string> { "en", "ko" }, useSettings));

        Assert.Equal(1, ocr.CallCount);
        Assert.Equal(new[] { "exact\r\ntext", "exact\r\ntext" }, copied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedLanguageContentsOrOrder_ReplacesRememberedText(bool reorder)
    {
        var image = CreateImage();
        var ocr = new FakeOcr((_, call) => Task.FromResult(Success($"text {call}")));
        var settings = new OcrSettings { PreferredLanguages = ["en", "ko"], UpscaleFactor = 2 };
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));
        settings.PreferredLanguages = reorder ? ["ko", "en"] : ["ja", "ko"];
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(new[] { "text 1", "text 2", "text 2" }, copied);
        Assert.Equal(settings.PreferredLanguages, ocr.Request!.PreferredLanguages);
    }

    [Theory]
    [InlineData(OcrQuality.Fast, OcrQuality.Balanced)]
    [InlineData(OcrQuality.Balanced, OcrQuality.Accurate)]
    public async Task ChangedQuality_WithSameUpscale_ReplacesRememberedText(OcrQuality initial, OcrQuality changed)
    {
        var image = CreateImage();
        var ocr = new FakeOcr((_, call) => Task.FromResult(Success($"text {call}")));
        var settings = new OcrSettings { PreferredLanguages = ["en"], UpscaleFactor = 2, Quality = initial };
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));
        settings.Quality = changed;
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(new[] { "text 1", "text 2", "text 2" }, copied);
        Assert.All(ocr.Requests, request => Assert.Equal(2, request.UpscaleFactor));
        Assert.Equal(OcrQualityProfile.SearchRotatedOrientations(changed), ocr.Request!.SearchRotatedOrientations);
        Assert.Equal(OcrQualityProfile.EnhanceContrast(changed), ocr.Request.EnhanceContrast);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedUpscale_ReplacesRememberedText(bool useSettings)
    {
        var image = CreateImage();
        var ocr = new FakeOcr((_, call) => Task.FromResult(Success($"text {call}")));
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await CopyAsync(image, ocr, Copy, 2, ["en"], useSettings));
        Assert.True(await CopyAsync(image, ocr, Copy, 4, ["en"], useSettings));
        Assert.True(await CopyAsync(image, ocr, Copy, 4, ["en"], useSettings));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(new[] { "text 1", "text 2", "text 2" }, copied);
        Assert.Equal(new[] { 2d, 4d }, ocr.Requests.Select(request => request.UpscaleFactor));
    }

    [Fact]
    public async Task EquivalentEffectiveQuality_ReusesRememberedText()
    {
        var image = CreateImage();
        var ocr = new FakeOcr(Success("remembered"));
        var settings = new OcrSettings { PreferredLanguages = ["en"], UpscaleFactor = 4, Quality = OcrQuality.Accurate };
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));
        settings.Quality = OcrQuality.Enhanced;
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));

        Assert.Equal(1, ocr.CallCount);
        Assert.Equal(new[] { "remembered", "remembered" }, copied);
    }

    [Fact]
    public async Task NonRecognitionSettings_ReusesRememberedText()
    {
        var image = CreateImage();
        var ocr = new FakeOcr(Success("remembered"));
        var settings = new OcrSettings { PreferredLanguages = ["en"] };
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));
        settings.AccuracySuggestionDismissed = true;
        settings.CacheResults = false;
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));

        Assert.Equal(1, ocr.CallCount);
        Assert.Equal(new[] { "remembered", "remembered" }, copied);
    }

    [Theory]
    [InlineData("no-text")]
    [InlineData("empty-success")]
    [InlineData("failed")]
    [InlineData("unavailable")]
    [InlineData("cancelled")]
    public async Task ResultWithoutText_IsNotRememberedAndRetries(string status)
    {
        var image = CreateImage();
        OcrResult first = status switch
        {
            "no-text" => OcrResult.NoText("en", TimeSpan.Zero),
            "empty-success" => Success(string.Empty),
            "failed" => OcrResult.Failed("recognition failed"),
            "unavailable" => OcrResult.Unavailable(),
            "cancelled" => OcrResult.Cancelled(),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        var ocr = new FakeOcr((_, call) => Task.FromResult(call == 1 ? first : Success("retry text")));
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.False(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        Assert.Empty(copied);
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(new[] { "retry text", "retry text" }, copied);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("faulted-task")]
    [InlineData("throws-cancellation")]
    [InlineData("cancelled-task")]
    public async Task RecognitionException_IsNotRememberedAndRetries(string failure)
    {
        var image = CreateImage();
        var ocr = new FakeOcr((_, call) => call != 1 ? Task.FromResult(Success("retry text")) : failure switch
        {
            "throws" => throw new InvalidOperationException("recognition failed"),
            "faulted-task" => Task.FromException<OcrResult>(new InvalidOperationException("recognition failed")),
            "throws-cancellation" => throw new OperationCanceledException(),
            "cancelled-task" => Task.FromCanceled<OcrResult>(new CancellationToken(true)),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        });
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        if (failure is "throws-cancellation" or "cancelled-task")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        }

        Assert.Empty(copied);
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(new[] { "retry text", "retry text" }, copied);
    }

    [Fact]
    public async Task DifferentImageInstances_WithIdenticalPixels_HaveSeparateMemory()
    {
        var firstImage = CreateImage();
        var secondImage = CreateImage();
        var ocr = new FakeOcr((request, _) => Task.FromResult(Success(ReferenceEquals(request.Bitmap, firstImage) ? "first" : "second")));
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await PinTextCopyService.CopyAsync(firstImage, ocr, Copy, 2, ["en"]));
        Assert.True(await PinTextCopyService.CopyAsync(secondImage, ocr, Copy, 2, ["en"]));
        Assert.True(await PinTextCopyService.CopyAsync(firstImage, ocr, Copy, 2, ["en"]));
        Assert.True(await PinTextCopyService.CopyAsync(secondImage, ocr, Copy, 2, ["en"]));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(new[] { "first", "second", "first", "second" }, copied);
        Assert.Equal(new[] { firstImage, secondImage }, ocr.Requests.Select(request => request.Bitmap));
    }

    [Fact]
    public async Task OverlappingCopies_ShareRecognitionAndEachCopyTheResult()
    {
        var image = CreateImage();
        var recognition = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new FakeOcr((_, _) => recognition.Task);
        var copied = new ConcurrentQueue<string>();
        Task<bool> Copy(string text) { copied.Enqueue(text); return Task.FromResult(true); }

        Task<bool> first = PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]);
        Task<bool> second = PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]);
        Assert.Equal(1, ocr.CallCount);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Empty(copied);

        recognition.SetResult(Success("shared text"));
        Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.True(result));

        Assert.Equal(1, ocr.CallCount);
        Assert.Equal(new[] { "shared text", "shared text" }, copied);
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        Assert.Equal(1, ocr.CallCount);
        Assert.Equal(3, copied.Count);
    }

    [Fact]
    public async Task FailedCopy_KeepsSuccessfulRecognitionForRetry()
    {
        var image = CreateImage();
        var ocr = new FakeOcr(Success("remembered"));
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(copied.Count > 1); }

        Assert.False(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));

        Assert.Equal(1, ocr.CallCount);
        Assert.Equal(new[] { "remembered", "remembered" }, copied);
    }

    [Fact]
    public async Task LanguagesMutatedDuringRecognition_DoNotChangeRequestOrRememberedKey()
    {
        var image = CreateImage();
        var recognition = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new FakeOcr((_, call) => call == 1 ? recognition.Task : Task.FromResult(Success("korean")));
        var settings = new OcrSettings { PreferredLanguages = ["en"], UpscaleFactor = 2, Quality = OcrQuality.Balanced };
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Task<bool> first = PinTextCopyService.CopyAsync(image, ocr, Copy, settings);
        settings.PreferredLanguages[0] = "ko";
        Assert.Equal(new[] { "en" }, ocr.Request!.PreferredLanguages);
        recognition.SetResult(Success("english"));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        Assert.Equal(1, ocr.CallCount);
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, settings));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(new[] { "english", "english", "korean", "korean" }, copied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderRecognitionCompletion_DoesNotReplaceOrClearNewerSettingsMemory(bool olderHasText)
    {
        var image = CreateImage();
        var older = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new FakeOcr((_, call) => call == 1 ? older.Task : newer.Task);
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Task<bool> first = PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]);
        Task<bool> second = PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["ko"]);
        Assert.Equal(2, ocr.CallCount);

        newer.SetResult(Success("newer text"));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        older.SetResult(olderHasText ? Success("older text") : OcrResult.Failed("older failed"));
        Assert.Equal(olderHasText, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["ko"]));

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(olderHasText ? new[] { "newer text", "older text", "newer text" } : new[] { "newer text", "newer text" }, copied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnToPendingSettings_ReusesOriginalRecognitionAndRemembersLastRequest(bool completeAFirst)
    {
        var image = CreateImage();
        var recognitionA = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognitionB = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new FakeOcr((request, _) => request.PreferredLanguages[0] == "en" ? recognitionA.Task : recognitionB.Task);
        var copied = new ConcurrentQueue<(string Caller, string Text)>();
        Task<bool> Copy(string caller, string text) { copied.Enqueue((caller, text)); return Task.FromResult(true); }

        Task<bool> firstA = PinTextCopyService.CopyAsync(image, ocr, text => Copy("first A", text), 2, ["en"]);
        Task<bool> copyB = PinTextCopyService.CopyAsync(image, ocr, text => Copy("B", text), 2, ["ko"]);
        Task<bool> repeatedA = PinTextCopyService.CopyAsync(image, ocr, text => Copy("repeated A", text), 2, ["en"]);

        Assert.Equal(2, ocr.CallCount);
        Assert.False(firstA.IsCompleted);
        Assert.False(copyB.IsCompleted);
        Assert.False(repeatedA.IsCompleted);
        Assert.Empty(copied);

        if (completeAFirst)
        {
            recognitionA.SetResult(Success("text A"));
            Assert.All(await Task.WhenAll(firstA, repeatedA).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.True(result));
            Assert.False(copyB.IsCompleted);
            recognitionB.SetResult(Success("text B"));
            Assert.True(await copyB.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            recognitionB.SetResult(Success("text B"));
            Assert.True(await copyB.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(firstA.IsCompleted);
            Assert.False(repeatedA.IsCompleted);
            recognitionA.SetResult(Success("text A"));
            Assert.All(await Task.WhenAll(firstA, repeatedA).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.True(result));
        }

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(3, copied.Count);
        Assert.Contains(("first A", "text A"), copied);
        Assert.Contains(("B", "text B"), copied);
        Assert.Contains(("repeated A", "text A"), copied);

        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, text => Copy("remembered A", text), 2, ["en"]));
        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(4, copied.Count);
        Assert.Contains(("remembered A", "text A"), copied);
    }

    [Fact]
    public async Task FailedRecognitionAfterSettingsChange_RetriesAndReplacesOldText()
    {
        var image = CreateImage();
        var ocr = new FakeOcr((_, call) => Task.FromResult(call switch
        {
            1 => Success("old text"),
            2 => OcrResult.Failed("new settings failed"),
            _ => Success("new text"),
        }));
        var copied = new List<string>();
        Task<bool> Copy(string text) { copied.Add(text); return Task.FromResult(true); }

        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["en"]));
        Assert.False(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["ko"]));
        Assert.Equal(new[] { "old text" }, copied);
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["ko"]));
        Assert.True(await PinTextCopyService.CopyAsync(image, ocr, Copy, 2, ["ko"]));

        Assert.Equal(3, ocr.CallCount);
        Assert.Equal(new[] { "old text", "new text", "new text" }, copied);
    }

    private static BitmapSource CreateImage()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }

    private static OcrResult Success(string text) => OcrResult.Success(text, "en", [], TimeSpan.Zero);

    private static Task<bool> CopyAsync(BitmapSource image, IOcrService ocr, Func<string, Task<bool>> copyText,
        double upscale, List<string> languages, bool useSettings) => useSettings
        ? PinTextCopyService.CopyAsync(image, ocr, copyText, new OcrSettings
        {
            PreferredLanguages = languages,
            UpscaleFactor = upscale,
            Quality = OcrQuality.Balanced,
        })
        : PinTextCopyService.CopyAsync(image, ocr, copyText, upscale, languages);

    private sealed class FakeOcr(Func<OcrRequest, int, Task<OcrResult>> recognize) : IOcrService
    {
        private int _callCount;

        public FakeOcr(OcrResult result) : this((_, _) => Task.FromResult(result)) { }

        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en"];
        internal OcrRequest? Request { get; private set; }
        internal ConcurrentQueue<OcrRequest> Requests { get; } = new();
        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Requests.Enqueue(request);
            return recognize(request, Interlocked.Increment(ref _callCount));
        }
    }
}
