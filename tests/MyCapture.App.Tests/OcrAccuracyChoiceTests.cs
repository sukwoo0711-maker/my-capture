using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrAccuracyChoiceTests
{
    [Fact]
    public void CaptureCardResultKeepsRequestedQualityWhenSettingsChangeDuringRecognition() => Ocr410Fixture.RunSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var settings = new OcrSettings { Quality = OcrQuality.Accurate };
        OcrQualityProfile.Synchronize(settings);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognizer = new FakeOcr { Recognize = (_, _) => pending.Task };
        string? copied = null;
        var service = new TextCaptureService(recognizer, () => settings,
            text => { copied = text; return Task.FromResult(true); }, NullLogger.Instance);
        BitmapSource image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
        image.Freeze();
        Task<TextCaptureOutcome> recognition = service.RecognizeAndCopyAsync(image, CancellationToken.None);
        OcrRequest request = Assert.Single(recognizer.Requests);
        Assert.Equal(AppArchitecture.SupportsNeuralOcr, request.UseNeuralModel);
        Assert.Equal(4.0, request.UpscaleFactor);
        settings.Quality = OcrQuality.Fast;
        OcrQualityProfile.Synchronize(settings);
        pending.SetResult(Text("capture card result"));
        Complete(recognition);

        Assert.Equal(TextCaptureStatus.Copied, recognition.Result.Status);
        Assert.Equal(OcrQuality.Accurate, recognition.Result.Result!.Quality);
        Assert.Equal("capture card result", recognition.Result.Result.Text);
        Assert.Equal("capture card result", copied);
        Assert.Equal(OcrQuality.Fast, settings.Quality);
        Assert.Same(image, request.Bitmap);
    });

    [Fact]
    public async Task ExplicitAccuracyAdapterBypassesBackgroundAndBatteryDeferral()
    {
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(),
            "ocr-accuracy-adapter-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        using var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var environment = new DeferredEnvironment();
        var model = new OcrAccuracyModel(new OcrModelStore(paths, http, NullLogger.Instance), environment);
        Assert.Equal(AppArchitecture.SupportsNeuralOcr, model.IsSupported);
        Assert.False(await Task.Run(() => model.IsReady));
        Assert.True(model.IsMetered);
        Assert.Equal(OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate), model.DownloadBytes);

        Assert.False(await model.DownloadAsync(new Progress<double>(), CancellationToken.None));

        Assert.Equal(model.IsSupported ? 1 : 0, handler.Calls);
        Assert.Equal(0, environment.DeferralChecks);
        // The fake server rejects the request before any model bytes are transferred.
    }

    [Theory]
    [InlineData(OcrQuality.Fast, true)]
    [InlineData(OcrQuality.Balanced, true)]
    [InlineData(OcrQuality.Accurate, false)]
    public void SelectorAndSuggestionDescribeResultInsteadOfLatestSavedQuality(OcrQuality quality, bool suggested)
        => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture(new OcrSettings { Quality = quality == OcrQuality.Accurate ? OcrQuality.Fast : OcrQuality.Accurate });
        fixture.ShowCached(quality);
        Assert.Equal(quality, fixture.Window.ResultQuality);
        Assert.Equal((int)quality, fixture.Window.AccuracySelector.SelectedIndex);
        Assert.Equal(suggested ? Visibility.Visible : Visibility.Collapsed, fixture.Window.AccuracySuggestion.Visibility);
        Assert.Empty(fixture.Service.Requests);
        Assert.Equal(0, fixture.SaveCount);
        Assert.False(fixture.Window.IsVisible);
    });

    [Fact]
    public void ChoosingDifferentQualityRecognizesOriginalImageOnceAndSavesSameSettings() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        fixture.Service.Result = Text("balanced result");

        fixture.Window.AccuracySelector.SelectedIndex = (int)OcrQuality.Balanced;
        WaitUntil(() => fixture.Window.ResultQuality == OcrQuality.Balanced);

        OcrRequest request = Assert.Single(fixture.Service.Requests);
        Assert.Same(fixture.Image, request.Bitmap);
        Assert.Equal(2.0, request.UpscaleFactor);
        Assert.True(request.SearchRotatedOrientations);
        Assert.False(request.UseNeuralModel);
        Assert.Equal(OcrQuality.Balanced, fixture.Settings.Quality);
        Assert.Equal(2.0, fixture.Settings.UpscaleFactor);
        Assert.Equal(1, fixture.SaveCount);
        Assert.Equal("balanced result", fixture.Window.ExtractionText);
        Assert.Equal(0, fixture.Model.DownloadCount);
    });

    [Fact]
    public void ChoosingCurrentQualityDoesNotRecognizeOrSaveAgain() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Fast));
        Assert.Empty(fixture.Service.Requests);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void MissingModelShowsManifestSizeAndExplicitClickDownloadsThenRecognizes(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var model = new FakeModel { IsReady = false, WaitForDownload = true };
        using var fixture = new Fixture(model: model);
        fixture.ShowCached(OcrQuality.Fast);
        Assert.Contains(OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Accurate).ToString(CultureInfo.CurrentCulture),
            fixture.Window.AccuracySuggestionText.Text);
        Assert.Contains("MB", fixture.Window.AccuracySuggestionText.Text, StringComparison.Ordinal);
        Assert.Equal(UiText.Get("Ocr_DownloadAccurateAgain"), fixture.Window.AccuracyAction.Content);

        Click(fixture.Window.AccuracyAction);
        WaitUntil(() => model.DownloadCount == 1);
        Assert.Empty(fixture.Service.Requests);
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(Visibility.Visible, fixture.Window.AccuracyCancel.Visibility);
        model.Progress!.Report(0.45);
        Ocr410Fixture.Pump();
        Assert.Contains("45", fixture.Window.AccuracySuggestionText.Text);

        model.CompleteDownload(true);
        WaitUntil(() => fixture.Window.ResultQuality == OcrQuality.Accurate);

        OcrRequest request = Assert.Single(fixture.Service.Requests);
        Assert.Same(fixture.Image, request.Bitmap);
        Assert.True(request.UseNeuralModel);
        Assert.Equal(4.0, request.UpscaleFactor);
        Assert.False(request.UseSuperResolution);
        Assert.Equal(OcrQuality.Accurate, fixture.Settings.Quality);
        Assert.True(fixture.Settings.AccuracySuggestionDismissed);
        Assert.Equal(1, fixture.SaveCount);
        Assert.Equal(Visibility.Collapsed, fixture.Window.AccuracySuggestion.Visibility);
    });

    [Fact]
    public void InstalledModelRecognizesWithoutDownload() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Balanced);
        Assert.Equal(UiText.Get("Ocr_AccurateAgain"), fixture.Window.AccuracyAction.Content);
        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate));
        Assert.Equal(0, fixture.Model.DownloadCount);
        Assert.True(Assert.Single(fixture.Service.Requests).UseNeuralModel);
        Assert.Equal(OcrQuality.Accurate, fixture.Settings.Quality);
        Assert.Equal(Visibility.Collapsed, fixture.Window.AccuracySuggestion.Visibility);
    });

    [Fact]
    public void MeteredConnectionIsShownBeforeExplicitDownloadAndDoesNotDeferIt() => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage("en");
        var model = new FakeModel { IsReady = false, IsMetered = true, WaitForDownload = true };
        using var fixture = new Fixture(model: model);
        fixture.ShowCached(OcrQuality.Fast);
        Assert.Contains(UiText.Get("Ocr_AccuracyMetered"), fixture.Window.AccuracySuggestionText.Text, StringComparison.Ordinal);
        Assert.Equal(0, model.DownloadCount);

        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate);
        WaitUntil(() => model.DownloadCount == 1);
        model.CompleteDownload(true);
        Complete(change);
        Assert.Equal(OcrQuality.Accurate, fixture.Settings.Quality);
        Assert.Single(fixture.Service.Requests);
    });

    [Fact]
    public void CancellingDownloadPreservesResultAndSavedQuality() => Ocr410Fixture.RunSta(() =>
    {
        var model = new FakeModel { IsReady = false, WaitForDownload = true };
        using var fixture = new Fixture(model: model);
        fixture.ShowCached(OcrQuality.Fast);
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate);
        WaitUntil(() => model.DownloadCount == 1);

        Click(fixture.Window.AccuracyCancel);
        Complete(change);

        Assert.True(model.Token.IsCancellationRequested);
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Window.ResultQuality);
        Assert.Equal((int)OcrQuality.Fast, fixture.Window.AccuracySelector.SelectedIndex);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
        Assert.Empty(fixture.Service.Requests);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedDownloadPreservesResultAndAllowsRetry(bool throws) => Ocr410Fixture.RunSta(() =>
    {
        var model = new FakeModel { IsReady = false, WaitForDownload = true };
        using var fixture = new Fixture(model: model);
        fixture.ShowCached(OcrQuality.Fast);
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate);
        WaitUntil(() => model.DownloadCount == 1);
        if (throws) model.FailDownload();
        else model.CompleteDownload(false);
        Complete(change);

        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Window.ResultQuality);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Empty(fixture.Service.Requests);
        Assert.Equal(0, fixture.SaveCount);
        Assert.True(fixture.Window.AccuracyAction.IsEnabled);
        Assert.Equal(UiText.Get("Translate_DownloadFailed"), fixture.Window.AccuracySuggestionText.Text);
    });

    [Fact]
    public void FailedRecognitionKeepsExistingResultAndQuality() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        fixture.Service.Result = OcrResult.Failed("synthetic recognition failure");
        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Balanced));
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Window.ResultQuality);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Fact]
    public void DismissalSavesFlagAndSuppressesSuggestionInNewWindow() => Ocr410Fixture.RunSta(() =>
    {
        var settings = new OcrSettings();
        using (var fixture = new Fixture(settings))
        {
            fixture.ShowCached(OcrQuality.Fast);
            Click(fixture.Window.AccuracyDismiss);
            Assert.True(settings.AccuracySuggestionDismissed);
            Assert.Equal(1, fixture.SaveCount);
            Assert.Equal(Visibility.Collapsed, fixture.Window.AccuracySuggestion.Visibility);
            Assert.Empty(fixture.Service.Requests);
        }
        using var reopened = new Fixture(settings);
        reopened.ShowCached(OcrQuality.Balanced);
        Assert.Equal(Visibility.Collapsed, reopened.Window.AccuracySuggestion.Visibility);
        Assert.Equal(0, reopened.SaveCount);
        Assert.Equal(OcrQuality.Fast, settings.Quality);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedResultRequiresConsentBeforeRecognitionCanReplaceIt(bool consent) => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        fixture.Window.ResultTextBox.Text = "user edited text";
        int prompts = 0;
        fixture.Window.ConfirmReplaceEditedText = () => { prompts++; return consent; };
        fixture.Service.Result = Text("replacement result");
        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Balanced));
        Assert.Equal(1, prompts);
        Assert.Equal(consent ? "replacement result" : "user edited text", fixture.Window.ExtractionText);
        Assert.Equal(consent ? 1 : 0, fixture.Service.Requests.Count);
        Assert.Equal(consent ? 1 : 0, fixture.SaveCount);
        Assert.Equal(consent ? OcrQuality.Balanced : OcrQuality.Fast, fixture.Settings.Quality);
    });

    [Fact]
    public void CurrentResultRemainsVisibleUntilReplacementIsReady() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task;
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Balanced);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Same(fixture.Image, fixture.Window.ImageOverlay.SourceImage);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        pending.SetResult(Text("new result"));
        Complete(change);
        Assert.Equal("new result", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Balanced, fixture.Settings.Quality);
    });

    [Fact]
    public void TextEditedDuringRecognitionRequiresConsentAtCompletion() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task;
        int prompts = 0;
        fixture.Window.ConfirmReplaceEditedText = () => { prompts++; return false; };
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Balanced);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        Assert.Equal(0, prompts);
        fixture.Window.ResultTextBox.Text = "edited while waiting";
        pending.SetResult(Text("replacement"));
        Complete(change);
        Assert.Equal(1, prompts);
        Assert.Equal("edited while waiting", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Fact]
    public void SaveFailureRollsBackQualityAndKeepsOriginalResult() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture { FailSave = true };
        fixture.ShowCached(OcrQuality.Fast);
        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate));
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Window.ResultQuality);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(1.0, fixture.Settings.UpscaleFactor);
        Assert.False(fixture.Settings.AccuracySuggestionDismissed);
        Assert.Equal(UiText.Get("Ocr_AccuracySaveFailed"), fixture.Window.AccuracySuggestionText.Text);
    });

    [Fact]
    public void RegularRerunKeepsCurrentTextAndImageUntilNewResultArrives() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task;

        ClickRerun(fixture.Window);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        Assert.True(fixture.IsBusy);
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Same(fixture.Image, fixture.Window.ImageOverlay.SourceImage);
        pending.SetResult(Text("regular rerun result"));
        WaitUntil(() => !fixture.IsBusy);
        Assert.Equal("regular rerun result", fixture.Window.ExtractionText);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingRegularRerunRejectsAccuracyChangesAndRestoresCommandsAfterFailureOrCancellation(bool cancelled)
        => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task;
        ClickRerun(fixture.Window);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        string busyStatus = Status(fixture.Window);
        Assert.True(fixture.IsBusy);
        Assert.False(Field<Button>(fixture.Window, "_copyButton").IsEnabled);
        Assert.False(Field<Button>(fixture.Window, "_plainExportButton").IsEnabled);
        Assert.False(fixture.Window.AccuracySelector.IsEnabled);
        Assert.False(fixture.Window.AccuracyAction.IsEnabled);
        Assert.False(fixture.Window.AccuracyDismiss.IsEnabled);

        // Programmatic events exercise the guard as well as the disabled UI contract.
        fixture.Window.AccuracySelector.SelectedIndex = (int)OcrQuality.Accurate;
        Click(fixture.Window.AccuracyAction);
        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Balanced));
        fixture.Presenter.CancelQualityChange();
        Assert.Single(fixture.Service.Requests);
        Assert.False(Assert.Single(fixture.Service.Tokens).IsCancellationRequested);
        Assert.Equal((int)OcrQuality.Fast, fixture.Window.AccuracySelector.SelectedIndex);
        Assert.Equal(0, fixture.Model.DownloadCount);
        Assert.Equal(0, fixture.SaveCount);

        pending.SetResult(cancelled ? OcrResult.Cancelled() : OcrResult.Failed("synthetic ordinary failure"));
        WaitUntil(() => !fixture.IsBusy);
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Window.ResultQuality);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(1d, fixture.Settings.UpscaleFactor);
        Assert.Equal(0, fixture.SaveCount);
        Assert.NotEqual(busyStatus, Status(fixture.Window));
        Assert.Equal(cancelled ? UiText.Format("Text_5626DD9C2F6A", "isolated fixture")
            : "synthetic ordinary failure", Status(fixture.Window));
        Assert.True(Field<Button>(fixture.Window, "_copyButton").IsEnabled);
        Assert.True(Field<Button>(fixture.Window, "_plainExportButton").IsEnabled);
        Assert.True(fixture.Window.AccuracySelector.IsEnabled);
        Assert.True(fixture.Window.AccuracyAction.IsEnabled);
        Assert.True(fixture.Window.AccuracyDismiss.IsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledOrdinaryRerunDiscardsLateResultAfterAttemptedAccuracyChange(bool lateFailure)
        => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task; // Deliberately ignores cancellation.
        ClickRerun(fixture.Window);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        var obsoleteCancellation = (CancellationTokenSource)typeof(OcrResultPresenter)
            .GetField("_inFlight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Presenter)!;
        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate));
        fixture.ShowCached(OcrQuality.Balanced, "new presentation");
        Assert.True(Assert.Single(fixture.Service.Tokens).IsCancellationRequested);
        string newStatus = Status(fixture.Window);

        pending.SetResult(lateFailure ? OcrResult.Failed("obsolete failure") : Text("obsolete success"));
        WaitUntil(() =>
        {
            try { _ = obsoleteCancellation.Token; return false; }
            catch (ObjectDisposedException) { return true; }
        });

        Assert.Equal("new presentation", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Balanced, fixture.Window.ResultQuality);
        Assert.Equal(newStatus, Status(fixture.Window));
        Assert.False(fixture.IsBusy);
        Assert.True(Field<Button>(fixture.Window, "_copyButton").IsEnabled);
        Assert.True(Field<Button>(fixture.Window, "_plainExportButton").IsEnabled);
        Assert.True(fixture.Window.AccuracySelector.IsEnabled);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
        Assert.Equal(0, fixture.Model.DownloadCount);
        Assert.Single(fixture.Service.Requests);
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void UnsupportedNeuralOcrUsesWindowsAccurateWithoutCheckingOrDownloadingModel(string language)
        => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var model = new FakeModel
        {
            IsSupported = false,
            IsReady = false,
            Readiness = () => throw new InvalidOperationException("Unsupported models must not be checked."),
        };
        using var fixture = new Fixture(model: model);
        fixture.ShowCached(OcrQuality.Fast);
        Assert.Equal(Visibility.Collapsed, fixture.Window.AccuracySuggestion.Visibility);
        Assert.DoesNotContain(UiText.Get("Ocr_DownloadAccurateAgain"), fixture.Window.AccuracySuggestionText.Text);

        Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate));

        OcrRequest request = Assert.Single(fixture.Service.Requests);
        Assert.Same(fixture.Image, request.Bitmap);
        Assert.Equal(4d, request.UpscaleFactor);
        Assert.True(request.SearchRotatedOrientations);
        Assert.True(request.EnhanceContrast);
        Assert.False(request.UseNeuralModel);
        Assert.False(request.UseSuperResolution);
        Assert.Equal(0, model.ReadinessCalls);
        Assert.Equal(0, model.DownloadCount);
        Assert.Equal(OcrQuality.Accurate, fixture.Settings.Quality);
        Assert.Equal(4d, fixture.Settings.UpscaleFactor);
        Assert.Equal(OcrQuality.Accurate, fixture.Window.ResultQuality);
        Assert.Equal("recognized result", fixture.Window.ExtractionText);
        Assert.Equal(1, fixture.SaveCount);
        Assert.Equal(Visibility.Collapsed, fixture.Window.AccuracySuggestion.Visibility);
    });

    [Fact]
    public void SlowModelVerificationDoesNotBlockOpeningOrDispatcherAndUpdatesSuggestionWhenReady()
        => Ocr410Fixture.RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int dispatcherThread = Environment.CurrentManagedThreadId;
        int verificationThread = 0;
        var model = new FakeModel { Readiness = () =>
        {
            verificationThread = Environment.CurrentManagedThreadId;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Readiness gate was not released.");
            return true;
        } };
        using var fixture = new Fixture(model: model);
        try
        {
            fixture.ShowCached(OcrQuality.Fast, waitForModel: false);
            WaitUntil(() => entered.IsSet);
            Assert.NotEqual(dispatcherThread, verificationThread);
            Assert.Equal("original text", fixture.Window.ExtractionText);
            Assert.True(Field<Button>(fixture.Window, "_copyButton").IsEnabled);
            Assert.False(fixture.Window.AccuracyModelCheckTask.IsCompleted);
            bool responded = false;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => responded = true));
            WaitUntil(() => responded);
            Assert.False(release.IsSet);
            Assert.False(fixture.Window.AccuracyModelCheckTask.IsCompleted);
        }
        finally { release.Set(); }
        Complete(fixture.Window.AccuracyModelCheckTask);
        Assert.Equal(Visibility.Visible, fixture.Window.AccuracySuggestion.Visibility);
        Assert.Equal(UiText.Get("Ocr_AccurateAgain"), fixture.Window.AccuracyAction.Content);
        Assert.Equal(0, fixture.Model.DownloadCount);
    });

    [Fact]
    public void SlowModelVerificationDuringAccuracyChangeIsResponsiveAndCancellationKeepsResult()
        => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int dispatcherThread = Environment.CurrentManagedThreadId;
        int verificationThread = 0;
        fixture.Model.Readiness = () =>
        {
            verificationThread = Environment.CurrentManagedThreadId;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Readiness gate was not released.");
            return false;
        };
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate);
        try
        {
            WaitUntil(() => entered.IsSet);
            Assert.NotEqual(dispatcherThread, verificationThread);
            Assert.False(change.IsCompleted);
            bool responded = false;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => responded = true));
            WaitUntil(() => responded);
            Assert.False(release.IsSet);
            fixture.Presenter.CancelQualityChange();
            Assert.False(fixture.IsBusy);
            Assert.Equal("original text", fixture.Window.ExtractionText);
        }
        finally { release.Set(); }
        Complete(change);
        Complete(fixture.Window.AccuracyModelCheckTask);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(OcrQuality.Fast, fixture.Window.ResultQuality);
        Assert.Equal(0, fixture.SaveCount);
        Assert.Equal(0, fixture.Model.DownloadCount);
        Assert.Empty(fixture.Service.Requests);
        Assert.True(fixture.Window.AccuracySelector.IsEnabled);
        Assert.True(Field<Button>(fixture.Window, "_copyButton").IsEnabled);
        Assert.True(Field<Button>(fixture.Window, "_plainExportButton").IsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateModelVerificationCannotReplaceCurrentProgressOrFailure(bool failed)
        => Ocr410Fixture.RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var model = new FakeModel { Readiness = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Readiness gate was not released.");
            return true;
        } };
        using var fixture = new Fixture(model: model);
        Task obsoleteCheck = Task.CompletedTask;
        try
        {
            fixture.ShowCached(OcrQuality.Fast, waitForModel: false);
            WaitUntil(() => entered.IsSet);
            obsoleteCheck = fixture.Window.AccuracyModelCheckTask;
            fixture.Window.BeginQualityOperation(downloading: true);
            fixture.Window.ReportQualityProgress(0.45);
            if (failed) fixture.Window.ShowAccuracyFailure("synthetic current failure");
        }
        finally { release.Set(); }
        Complete(obsoleteCheck);
        Complete(fixture.Window.AccuracyModelCheckTask);
        Assert.Equal(failed ? "synthetic current failure" : UiText.Format("Ocr_ModelDownloadProgress", 45d),
            fixture.Window.AccuracySuggestionText.Text);
        Assert.Equal(failed ? Visibility.Collapsed : Visibility.Visible, fixture.Window.AccuracyProgress.Visibility);
        Assert.Equal("original text", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Fact]
    public void RegularRerunDoesNotStartWhenUserKeepsEditedText() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        fixture.Window.ResultTextBox.Text = "retained edits";
        int prompts = 0;
        fixture.Window.ConfirmReplaceEditedText = () => { prompts++; return false; };
        ClickRerun(fixture.Window);
        Ocr410Fixture.Pump();
        Assert.Equal(1, prompts);
        Assert.Empty(fixture.Service.Requests);
        Assert.Equal("retained edits", fixture.Window.ExtractionText);
        Assert.False(fixture.IsBusy);
    });

    [Fact]
    public void RegularRerunConfirmsEditsMadeWhileRecognitionWasPending() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task;
        int prompts = 0;
        fixture.Window.ConfirmReplaceEditedText = () => { prompts++; return false; };
        ClickRerun(fixture.Window);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        Assert.Equal(0, prompts);
        fixture.Window.ResultTextBox.Text = "edits made during rerun";
        pending.SetResult(Text("unapproved replacement"));
        WaitUntil(() => !fixture.IsBusy);
        Assert.Equal(1, prompts);
        Assert.Equal("edits made during rerun", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Fast, fixture.Window.ResultQuality);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewPresentationDuringInitialConfirmationCancelsOldIntent(bool qualityChange) => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        fixture.Window.ResultTextBox.Text = "old capture edits";
        int prompts = 0;
        fixture.Window.ConfirmReplaceEditedText = () =>
        {
            prompts++;
            fixture.ShowCached(OcrQuality.Balanced, "new capture during prompt");
            return true;
        };
        if (qualityChange) Complete(fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate));
        else ClickRerun(fixture.Window);
        Ocr410Fixture.Pump();
        Assert.Equal(1, prompts);
        Assert.Equal("new capture during prompt", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Balanced, fixture.Window.ResultQuality);
        Assert.Empty(fixture.Service.Requests);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Fact]
    public void NewPresentationDuringCompletionConfirmationRejectsStaleResult() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task;
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        fixture.Window.ResultTextBox.Text = "edits during recognition";
        int prompts = 0;
        fixture.Window.ConfirmReplaceEditedText = () =>
        {
            prompts++;
            fixture.ShowCached(OcrQuality.Balanced, "new capture during completion prompt");
            return true;
        };
        pending.SetResult(Text("old accurate result"));
        Complete(change);
        Assert.Equal(1, prompts);
        Assert.Equal("new capture during completion prompt", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Balanced, fixture.Window.ResultQuality);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
    });

    [Fact]
    public void LateDownloadCannotOverwriteNewPresentationOrSavedQuality() => Ocr410Fixture.RunSta(() =>
    {
        var model = new FakeModel { IsReady = false, WaitForDownload = true, HonorCancellation = false };
        using var fixture = new Fixture(model: model);
        fixture.ShowCached(OcrQuality.Fast);
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate);
        WaitUntil(() => model.DownloadCount == 1);
        fixture.ShowCached(OcrQuality.Balanced, "new capture");
        model.CompleteDownload(true);
        Complete(change);
        Assert.True(model.Token.IsCancellationRequested);
        Assert.Equal("new capture", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Balanced, fixture.Window.ResultQuality);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
        Assert.Empty(fixture.Service.Requests);
    });

    [Fact]
    public void LateRecognitionCannotOverwriteNewPresentationOrPersistItsSetting() => Ocr410Fixture.RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.ShowCached(OcrQuality.Fast);
        var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Recognize = (_, _) => pending.Task;
        Task change = fixture.Presenter.ChangeQualityAsync(OcrQuality.Accurate);
        WaitUntil(() => fixture.Service.Requests.Count == 1);
        fixture.ShowCached(OcrQuality.Balanced, "new capture");
        pending.SetResult(Text("stale accurate result"));
        Complete(change);
        Assert.True(Assert.Single(fixture.Service.Tokens).IsCancellationRequested);
        Assert.Equal("new capture", fixture.Window.ExtractionText);
        Assert.Equal(OcrQuality.Balanced, fixture.Window.ResultQuality);
        Assert.Equal(OcrQuality.Fast, fixture.Settings.Quality);
        Assert.Equal(0, fixture.SaveCount);
    });

    private static OcrResult Text(string text) => OcrResult.Success(text, "en-US", [], TimeSpan.FromMilliseconds(1));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void ClickRerun(OcrResultWindow window) => Click((Button)typeof(OcrResultWindow)
        .GetField("_rerunButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);

    private static T Field<T>(OcrResultWindow window, string name) =>
        (T)typeof(OcrResultWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static string Status(OcrResultWindow window) => Field<TextBlock>(window, "_status").Text;

    private static void Complete(Task task)
    {
        WaitUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) Ocr410Fixture.Pump();
        Assert.True(predicate(), "The isolated accuracy operation did not finish within ten seconds.");
    }

    private sealed class Fixture : IDisposable
    {
        internal OcrSettings Settings { get; }
        internal FakeModel Model { get; }
        internal FakeOcr Service { get; } = new();
        internal OcrResultWindow Window { get; }
        internal OcrResultPresenter Presenter { get; }
        internal BitmapSource Image { get; }
        internal int SaveCount { get; private set; }
        internal bool FailSave { get; init; }
        internal bool IsBusy { get; private set; }

        internal Fixture(OcrSettings? settings = null, FakeModel? model = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Settings = settings ?? new OcrSettings();
            OcrQualityProfile.Synchronize(Settings);
            Model = model ?? new FakeModel();
            Image = BitmapSource.Create(20, 20, 96, 96, PixelFormats.Bgra32, null, new byte[20 * 20 * 4], 20 * 4);
            Image.Freeze();
            Window = new OcrResultWindow { SuppressPresentation = true, ShowInTaskbar = false };
            Presenter = new OcrResultPresenter(Service, Dispatcher.CurrentDispatcher, busy => IsBusy = busy,
                NullLogger<OcrResultPresenter>.Instance,
                accuracy: new OcrAccuracyOptions(() => Settings, () =>
                {
                    SaveCount++;
                    if (FailSave) throw new IOException("synthetic save failure");
                }, Model), window: Window);
        }

        internal void ShowCached(OcrQuality quality, string text = "original text", bool waitForModel = true)
        {
            Presenter.ShowCached(Text(text).WithQuality(quality), "isolated fixture",
                () => OcrRequestFactory.FromBitmap(Image, Settings));
            WaitUntil(() => ReferenceEquals(Window.ImageOverlay.SourceImage, Image));
            if (waitForModel) Complete(Window.AccuracyModelCheckTask);
        }

        public void Dispose() => Presenter.Dispose();
    }

    private sealed class FakeOcr : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        internal OcrResult Result { get; set; } = Text("recognized result");
        internal Func<OcrRequest, CancellationToken, Task<OcrResult>>? Recognize { get; set; }
        internal List<OcrRequest> Requests { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            return Recognize?.Invoke(request, cancellationToken) ?? Task.FromResult(Result);
        }
    }

    private sealed class FakeModel : IOcrAccuracyModel
    {
        private readonly TaskCompletionSource<bool> _download = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _isReady = true;
        private int _readinessCalls;
        public bool IsSupported { get; set; } = true;
        public bool IsReady
        {
            get { Interlocked.Increment(ref _readinessCalls); return Readiness?.Invoke() ?? _isReady; }
            set => _isReady = value;
        }
        internal Func<bool>? Readiness { get; set; }
        internal int ReadinessCalls => Volatile.Read(ref _readinessCalls);
        public bool IsMetered { get; set; }
        public long DownloadBytes => OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate);
        internal bool WaitForDownload { get; init; }
        internal bool HonorCancellation { get; init; } = true;
        internal int DownloadCount { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal IProgress<double>? Progress { get; private set; }

        public Task<bool> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            DownloadCount++;
            Token = cancellationToken;
            Progress = progress;
            if (!WaitForDownload) { IsReady = true; return Task.FromResult(true); }
            if (HonorCancellation) cancellationToken.Register(() => _download.TrySetCanceled(cancellationToken));
            return _download.Task;
        }

        internal void CompleteDownload(bool success)
        {
            IsReady = success;
            _download.TrySetResult(success);
        }

        internal void FailDownload() => _download.TrySetException(new IOException("synthetic download failure"));
    }

    private sealed class DeferredEnvironment : IOcrDownloadEnvironment
    {
        internal int DeferralChecks { get; private set; }
        public bool IsMetered => true;
        public bool? IsBatterySaverOn => true;
        public bool ShouldDeferBackgroundDownload() { DeferralChecks++; return true; }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
