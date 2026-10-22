using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class WelcomeOcrChoiceTests
{
    [Theory]
    [InlineData(true, false, 4, 8192, true)]
    [InlineData(true, false, 8, 16384, true)]
    [InlineData(false, false, 4, 8192, false)]
    [InlineData(null, false, 4, 8192, false)]
    [InlineData(true, true, 4, 8192, false)]
    [InlineData(true, null, 4, 8192, false)]
    [InlineData(true, false, 3, 8192, false)]
    [InlineData(true, false, null, 8192, false)]
    [InlineData(true, false, 4, 8191, false)]
    [InlineData(true, false, 4, 0, false)]
    [InlineData(true, false, 4, null, false)]
    public void PreselectionRequiresEveryKnownCondition(bool? unmetered, bool? batterySaver,
        int? cores, int? memoryMiB, bool expected)
    {
        var environment = new WelcomeOcrTestEnvironment
        {
            Unmetered = unmetered, IsBatterySaverOn = batterySaver, ProcessorCount = cores,
            InstalledMemoryBytes = memoryMiB is int memory ? (ulong)memory * 1024 * 1024 : null,
        };
        Assert.Equal(expected, FirstRunOcrChoice.ShouldPreselect(environment));
    }

    [Fact]
    public void PreselectionFailureRemainsUnchecked() =>
        Assert.False(FirstRunOcrChoice.ShouldPreselect(new FailingEnvironment()));

    [Theory]
    [InlineData("ko-KR", "정확한 한국어 글자 인식")]
    [InlineData("en-US", "Accurate Korean text recognition")]
    public void CheckboxProvidesLocalizedNameSizeAndHelp(string locale, string label) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(locale);
        var environment = new WelcomeOcrTestEnvironment();
        var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor, ocrEnvironment: environment);
        try
        {
            Assert.True(welcome.UseAccurateOcr);
            Assert.Equal(label, welcome.AccurateOcrCheck.Content);
            Assert.Equal(label, AutomationProperties.GetName(welcome.AccurateOcrCheck));
            long bytes = OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional).Sum(file => file.ExpectedBytes!.Value);
            Assert.Equal(101_654_967, bytes);
            string expected = UiText.Format("Welcome_AccurateOcrHelp", (int)Math.Ceiling(bytes / 1_000_000d));
            Assert.Equal(expected, AutomationProperties.GetHelpText(welcome.AccurateOcrCheck));
            Assert.Equal(expected, welcome.AccurateOcrCheck.ToolTip);
            Assert.Equal(expected, welcome.AccurateOcrHelp.Text);
            Assert.Contains("102 MB", expected, StringComparison.Ordinal);
            Assert.Equal(32, welcome.AccurateOcrCheck.MinHeight);
            Assert.Equal(12, welcome.AccurateOcrCheck.FontSize);
            Assert.Equal(560, welcome.Width);
            welcome.AccurateOcrCheck.IsChecked = false;
            Assert.False(welcome.UseAccurateOcr);
        }
        finally { welcome.Close(); }
        var uncheckedWelcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor,
            ocrEnvironment: new WelcomeOcrTestEnvironment { Unmetered = false });
        try { Assert.False(uncheckedWelcome.UseAccurateOcr); }
        finally { uncheckedWelcome.Close(); }
    });

    [Theory]
    [InlineData(OcrQuality.Fast, true, OcrQuality.Accurate, 1)]
    [InlineData(OcrQuality.Fast, false, OcrQuality.Fast, 0)]
    [InlineData(OcrQuality.Balanced, true, OcrQuality.Balanced, 0)]
    [InlineData(OcrQuality.Balanced, false, OcrQuality.Balanced, 0)]
    [InlineData(OcrQuality.Accurate, true, OcrQuality.Accurate, 1)]
    [InlineData(OcrQuality.Accurate, false, OcrQuality.Accurate, 0)]
    [InlineData(OcrQuality.Enhanced, true, OcrQuality.Enhanced, 1)]
    [InlineData(OcrQuality.Enhanced, false, OcrQuality.Enhanced, 0)]
    public void ChoiceOnlyChangesFastAndSavesBeforeDownloading(OcrQuality previous, bool selected,
        OcrQuality expected, int expectedDownloads)
    {
        var settings = new AppSettings();
        settings.Ocr.Quality = previous;
        int saves = 0, downloads = 0;
        FirstRunOcrChoice.Complete(settings, selected, saved =>
        {
            Assert.Same(settings, saved);
            Assert.Equal(expected, saved.Ocr.Quality);
            saves++;
        }, () => { Assert.Equal(1, saves); downloads++; });
        Assert.Equal(expected, settings.Ocr.Quality);
        Assert.Equal(1, saves);
        Assert.Equal(expectedDownloads, downloads);
    }

    [Fact]
    public void FailedSaveRestoresQualityAndDoesNotStartDownload()
    {
        var settings = new AppSettings();
        int downloads = 0;
        Assert.Throws<IOException>(() => FirstRunOcrChoice.Complete(settings, true,
            _ => throw new IOException("Injected save failure"), () => downloads++));
        Assert.Equal(OcrQuality.Fast, settings.Ocr.Quality);
        Assert.Equal(0, downloads);
    }

    public static IEnumerable<object[]> CloseCases =>
        from locale in new[] { "ko-KR", "en-US" }
        from outcome in Enum.GetValues<WelcomeOutcome>()
        from selected in new[] { false, true }
        select new object[] { locale, (int)outcome, selected };

    [Theory]
    [MemberData(nameof(CloseCases))]
    public void EveryCloseOutcomeAppliesCheckboxIncludingCaptureDismissal(string locale, int outcomeValue,
        bool selected) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(locale);
        WelcomeOutcome outcome = (WelcomeOutcome)outcomeValue;
        var settings = new AppSettings();
        var window = new WelcomeWindow(settings.Hotkeys, settings.General.AfterCapture,
            ocrEnvironment: new WelcomeOcrTestEnvironment())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        int saves = 0, downloads = 0;
        window.Closed += (_, _) => FirstRunOcrChoice.Complete(settings, window.UseAccurateOcr,
            _ => saves++, () => downloads++);
        try
        {
            window.AccurateOcrCheck.IsChecked = selected;
            window.Show();
            switch (outcome)
            {
                case WelcomeOutcome.TryCapture:
                    window.TryNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                case WelcomeOutcome.OpenSettings:
                    window.ShortcutsLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); break;
                case WelcomeOutcome.DismissedForCapture:
                    window.DismissForCapture(); break;
                default: window.Close(); break;
            }
            Assert.Equal(outcome, window.Outcome);
            Assert.Equal(1, saves);
            Assert.Equal(selected ? 1 : 0, downloads);
            Assert.Equal(selected ? OcrQuality.Accurate : OcrQuality.Fast, settings.Ocr.Quality);
        }
        finally { if (window.IsVisible) window.Close(); }
    });

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(null, 0)]
    public async Task ExplicitWelcomeDownloadAcceptsMeteredButRechecksBatterySaver(bool? batterySaver, int expectedRequests)
    {
        var environment = new WelcomeOcrTestEnvironment();
        Assert.True(FirstRunOcrChoice.ShouldPreselect(environment));
        // Conditions change after the checkbox was first presented.
        environment.Unmetered = false;
        environment.IsBatterySaverOn = batterySaver;
        var handler = new FakeDownloader();
        using var http = new HttpClient(handler);
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(), "welcome-ocr-" + Guid.NewGuid().ToString("N")));
        var store = new OcrModelStore(paths, http, NullLogger.Instance);
        var coordinator = new OcrModelDownloadCoordinator(store, environment);
        var settings = new AppSettings();
        Task<bool>? download = null;
        FirstRunOcrChoice.Complete(settings, true, _ => { }, () => download = coordinator.EnsureAsync(
            settings.Ocr.Quality, isBackground: false, CancellationToken.None, deferForBatterySaver: true));
        Assert.NotNull(download);
        Assert.False(await download);
        Assert.Equal(expectedRequests, handler.Requests);
        Assert.Equal(OcrQuality.Accurate, settings.Ocr.Quality);
        // Synthetic directories are retained; the legacy helper uses permanent deletion.
    }

    private sealed class FakeDownloader : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class FailingEnvironment : IOcrDownloadEnvironment
    {
        public bool ShouldDeferBackgroundDownload() => throw new InvalidOperationException("Unavailable OS status");
    }
}

internal sealed class WelcomeOcrTestEnvironment : IOcrDownloadEnvironment
{
    internal bool? Unmetered { get; set; } = true;
    public bool? IsBatterySaverOn { get; set; } = false;
    public int? ProcessorCount { get; set; } = 4;
    public ulong? InstalledMemoryBytes { get; set; } = 8UL * 1024 * 1024 * 1024;
    public bool ShouldDeferBackgroundDownload() => Unmetered != true || IsBatterySaverOn != false;
}
