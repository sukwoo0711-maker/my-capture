using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrStartupLifetimeTests
{
    [Fact]
    public void ResidentPresenterDoesNotCreateAccuracyControlsOrReadModelAndNetworkState() => Ocr410Fixture.RunSta(() =>
    {
        var model = new CountingModel();
        using var presenter = CreatePresenter(model);
        OcrResultWindow window = presenter.Window;
        Ocr410Fixture.Pump();
        window.RefreshAccuracy();
        Assert.False(window.AccuracyControlsCreated);
        Assert.Equal(0, model.Reads);
        Assert.True(window.AccuracyModelCheckTask.IsCompletedSuccessfully);
    });

    [Fact]
    public void InitialResultIsReadyBeforeAccuracyControlsAndVerificationAreCreated() => Ocr410Fixture.RunSta(() =>
    {
        var model = new CountingModel();
        using var presenter = CreatePresenter(model);
        OcrResultWindow window = presenter.Window;
        ShowResult(window);
        Assert.Equal("ready text", window.ResultTextBox.Text);
        Assert.False(window.AccuracyControlsCreated);
        Assert.Equal(0, model.Reads);

        window.CompleteAccuracyPresentation();
        CompleteModelCheck(window);
        Assert.True(window.AccuracyControlsCreated);
        Assert.Equal(OcrQuality.Fast, window.AccuracySelector.SelectedValue);
        Assert.Equal(Visibility.Visible, window.AccuracySuggestion.Visibility);
        Assert.True(model.ReadinessReads > 0);
        Assert.True(model.MeteredReads > 0);
        ComboBox selector = window.AccuracySelector;
        int reads = model.Reads;
        window.CompleteAccuracyPresentation();
        Assert.Same(selector, window.AccuracySelector);
        Assert.Equal(reads, model.Reads);
    });

    [Fact]
    public void CloseBeforeFirstRenderCancelsDeferredWorkAndReopenCreatesItOnce() => Ocr410Fixture.RunSta(() =>
    {
        var model = new CountingModel();
        using var presenter = CreatePresenter(model);
        OcrResultWindow window = presenter.Window;
        ShowResult(window);
        window.Close();
        window.CompleteAccuracyPresentation();
        Ocr410Fixture.Pump();
        Assert.False(window.AccuracyControlsCreated);
        Assert.Equal(0, model.Reads);
        ShowResult(window);
        Assert.False(window.AccuracyControlsCreated);
        window.CompleteAccuracyPresentation();
        CompleteModelCheck(window);
        ComboBox selector = window.AccuracySelector;
        window.Close();
        int reads = model.Reads;
        window.RefreshAccuracy();
        window.CompleteAccuracyPresentation();
        Assert.Equal(reads, model.Reads);
        Assert.False(selector.IsEnabled);
        Assert.Equal(Visibility.Collapsed, window.AccuracySuggestion.Visibility);
        ShowResult(window);
        Assert.Equal(reads, model.Reads);
        window.CompleteAccuracyPresentation();
        CompleteModelCheck(window);
        Assert.Same(selector, window.AccuracySelector);
        Assert.True(selector.IsEnabled);
    });

    [Fact]
    public void RealOffscreenRenderInitializesAccuracyThroughTheWindowEvent() => Ocr410Fixture.RunSta(() =>
    {
        var model = new CountingModel();
        using var presenter = CreatePresenter(model);
        OcrResultWindow window = presenter.Window;
        window.SuppressPresentation = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -30000;
        window.ShowActivated = false;
        ShowResult(window);
        Assert.False(window.AccuracyControlsCreated);
        Assert.Equal(0, model.Reads);
        OcrTranslationWindowTests.PumpUntil(() => window.AccuracyControlsCreated);
        CompleteModelCheck(window);
        Assert.True(window.IsVisible);
        Assert.Equal(Visibility.Visible, window.AccuracySuggestion.Visibility);
    });

    private static OcrResultPresenter CreatePresenter(CountingModel model) => new(new UnusedOcr(),
        Dispatcher.CurrentDispatcher, _ => { }, NullLogger<OcrResultPresenter>.Instance,
        accuracy: new OcrAccuracyOptions(() => new OcrSettings(), () => { }, model),
        window: new OcrResultWindow { SuppressPresentation = true, ShowInTaskbar = false });

    private static void ShowResult(OcrResultWindow window) => window.ShowResult(
        OcrResult.Success("ready text", "en-US", [], TimeSpan.Zero).WithQuality(OcrQuality.Fast), "fixture");

    private static void CompleteModelCheck(OcrResultWindow window)
    {
        OcrTranslationWindowTests.PumpUntil(() => window.AccuracyModelCheckTask.IsCompleted);
        window.AccuracyModelCheckTask.GetAwaiter().GetResult();
    }

    private sealed class CountingModel : IOcrAccuracyModel
    {
        private int _supportedReads, _readinessReads, _meteredReads;
        internal int ReadinessReads => Volatile.Read(ref _readinessReads);
        internal int MeteredReads => Volatile.Read(ref _meteredReads);
        internal int Reads => Volatile.Read(ref _supportedReads) + ReadinessReads + MeteredReads;
        public bool IsSupported { get { Interlocked.Increment(ref _supportedReads); return true; } }
        public bool IsReady { get { Interlocked.Increment(ref _readinessReads); return false; } }
        public bool IsMetered { get { Interlocked.Increment(ref _meteredReads); return false; } }
        public long DownloadBytes => 1_000_000;
        public Task<bool> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Startup must not download models.");
    }

    private sealed class UnusedOcr : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No recognition in the lifetime fixture.");
    }
}
