using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

// A failure names the step it stopped in, in the pin and in the file log; no network, model or user files.
public sealed partial class PinTranslationIntegrationTests
{
    [Fact]
    public Task AModelThatCannotOpenSaysSoAndItsRetryInstallsTheModelAgain() => RunAsync(async () =>
    {
        using var model = new UnopenedModel();
        var log = new StageLog();
        PinManager manager = Manager(model.Service, log);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            int prompts = 0;
            pin.ConfirmTranslationModelDownload = _ => { prompts++; return true; };
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal(UiText.Get("Pin_TranslationFailedLoad"), pin.FeedbackForTest.Text);
            Assert.Equal(English, pin.DisplayedText);
            Assert.Equal(0, prompts);
            StageLog.Entry entry = Assert.Single(log.Entries);
            Assert.Equal("Pin translation failed while loading the model", entry.Template);
            Assert.IsType<InvalidDataException>(entry.Exception);
            Assert.Contains(" ex=System.IO.InvalidDataException ", entry.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(English, entry.Line, StringComparison.Ordinal);

            // The retry asks to install again, deletes the files the check accepted, downloads and translates.
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(1, prompts);
            Assert.Equal(1, model.Requests);
            Assert.True(model.DeletedBeforeDownload);
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            Assert.Equal(ReinstalledKorean, pin.DisplayedText);
            Assert.Single(log.Entries);

            // Once it opens, the model is used as installed: no further prompt or download.
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(1, prompts);
            Assert.Equal(1, model.Requests);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task APictureWhoseModelCannotOpenOffersTheInstallationOnItsRetryWithoutOpeningTheModel() => RunAsync(async () =>
    {
        using var model = new UnopenedModel();
        var log = new StageLog();
        PinManager manager = Manager(model.Service, log);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult<string?>(English));
        try
        {
            PinWindow pin = manager.PinImage(Image());
            int prompts = 0;
            pin.ConfirmTranslationModelDownload = _ => { prompts++; return false; };
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal(UiText.Get("Pin_TranslationFailedLoad"), pin.FeedbackForTest.Text);
            Assert.Equal("Pin translation failed while loading the model", Assert.Single(log.Entries).Template);
            int loads = model.Loads;

            // Declining the offer leaves the files where they are and opens nothing.
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(1, prompts);
            Assert.Equal(PinTranslationState.ModelNeeded, pin.TranslationState);
            Assert.Equal(loads, model.Loads);
            Assert.Equal(0, model.Requests);
            Assert.True(model.FilePresent);
        }
        finally { manager.CloseAll(); }
    });

    /// <summary>
    /// A one-file model that passes its check but does not open until it has been deleted and downloaded again,
    /// behind the product's own store and translator.
    /// </summary>
    private sealed class UnopenedModel : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("pin-translate-load-");
        private readonly HttpClient _http;
        private readonly TranslationModelStore _store;
        private readonly TranslationModelFile _file;
        private bool _replaced;

        internal UnopenedModel()
        {
            byte[] bytes = [1, 2, 3, 4];
            _file = new("model.bin", "https://models.invalid/model.bin", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
            _http = new HttpClient(new ServingHandler(bytes, () =>
            {
                Requests++;
                DeletedBeforeDownload = !File.Exists(_store!.PathFor(_file));
                _replaced = true;
            }));
            _store = new TranslationModelStore(AppPaths.CreateForRoot(_root), _http, NullLogger.Instance, [_file]);
            Directory.CreateDirectory(_store.ModelDirectory);
            File.WriteAllBytes(_store.PathFor(_file), bytes);
            Service = new TextTranslationService(_store, () =>
            {
                Interlocked.Increment(ref _loads);
                if (!_replaced) throw new InvalidDataException("Synthetic model that cannot open.");
                return new TextTranslationServiceTests.FakeEngine(_ => ReinstalledKorean);
            }, NullLogger.Instance, TimeSpan.FromMinutes(1));
        }

        private int _loads;

        internal TextTranslationService Service { get; }
        internal int Loads => Volatile.Read(ref _loads);
        internal int Requests { get; private set; }
        internal bool DeletedBeforeDownload { get; private set; }
        internal bool FilePresent => File.Exists(_store.PathFor(_file));

        public void Dispose()
        {
            Service.Dispose();
            _http.Dispose();
            OwnedTestDirectory.Delete(_root);
        }
    }

    private const string ReinstalledKorean = "창을 닫기 전에 문서를 저장하십시오.";

    private sealed class ServingHandler(byte[] bytes, Action requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    [Fact]
    public Task AnInferenceFailureAfterTheModelOpenedSaysTranslationStopped() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Fail = true };
        var log = new StageLog();
        PinManager manager = Manager(translator, log);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal("번역 도중 오류가 발생했습니다. 원문이 유지됩니다.", pin.FeedbackForTest.Text);
            Assert.Equal("Pin translation failed while translating", Assert.Single(log.Entries).Template);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task APictureThatCannotBeReadSaysTheReadingFailed() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        var log = new StageLog();
        PinManager manager = Manager(translator, log);
        manager.ConfigureImageTranslation((_, _) => Task.FromException<string?>(
            new COMException("Synthetic recognizer failure.", unchecked((int)0x80004005))));
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal("이미지의 글자를 읽지 못했습니다. 원문이 유지됩니다.", pin.FeedbackForTest.Text);
            StageLog.Entry entry = Assert.Single(log.Entries);
            Assert.Equal("Pin translation failed while reading the picture", entry.Template);
            Assert.Contains(" hresult=0x80004005", entry.Line, StringComparison.Ordinal);
            Assert.Equal(0, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task AModelCheckThatCannotReadTheFolderSaysTheCheckFailed() => RunAsync(async () =>
    {
        var translator = new UncheckableTranslator();
        var log = new StageLog();
        PinManager manager = Manager(translator, log);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal("번역 모델을 확인하지 못했습니다. 원문이 유지됩니다.", pin.FeedbackForTest.Text);
            Assert.Equal("Pin translation failed while checking the model", Assert.Single(log.Entries).Template);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ATranslationThatKeepsNoSentenceIsReportedAsTheOriginalKeptNotAsAFailure() => RunAsync(async () =>
    {
        const string source = "Save 3 files before you close the window.";
        // Every answer drops the number, so no sentence passes the value check.
        var translator = new FakeTranslator { Result = "창을 닫기 전에 파일을 저장하십시오." };
        var log = new StageLog();
        PinManager manager = Manager(translator, log);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), source, false);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Available, pin.TranslationState);
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal(source, pin.DisplayedText);
            Assert.Equal(UiText.Get("Pin_TranslationKeptOriginal"), pin.FeedbackForTest.Text);
            Assert.Empty(log.Entries);
            // The command still translates once the model keeps the values.
            translator.Result = "창을 닫기 전에 파일 3개를 저장하십시오.";
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public void EveryStageHasItsOwnSentenceAndLogTemplateInBothLanguages()
    {
        var failure = new InvalidOperationException("Synthetic.");
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            using var culture = UiText.UseLanguage(language);
            string general = UiText.Get("Pin_TranslationFailed");
            string[] messages = [.. Enum.GetValues<PinTranslationStage>().Select(stage => PinManager.TranslationFailureMessage(failure, 1, stage))];
            Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(general, messages);
            Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
            Assert.Equal(general, PinManager.TranslationFailureMessage(failure, 1));
            // A classified download failure keeps its own sentence whatever the step.
            Assert.Equal(UiText.Get("Pin_TranslationErrorNetwork"), PinManager.TranslationFailureMessage(
                new TranslationModelException(TranslationModelError.Network, "Synthetic."), 1, PinTranslationStage.Translating));
        }
    }

    private static PinManager Manager(ITextTranslator translator, ILogger log)
    {
        var manager = new PinManager(() => new PinSettings(), log)
        {
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
        };
        manager.ConfigureTranslation(translator);
        return manager;
    }

    private sealed class UncheckableTranslator : ITextTranslator
    {
        public long ModelDownloadBytes => 246_256_561;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) =>
            Task.FromException<bool>(new UnauthorizedAccessException("Synthetic."));
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslatedText>>([]);
    }

    /// <summary>Formats warnings with the file log's own safe formatter; the template is read without formatting arguments.</summary>
    private sealed class StageLog : ILogger
    {
        internal sealed record Entry(string? Template, Exception? Exception, string Line);

        internal List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string? template = SafeLogLine.TemplateOf(state);
            Entries.Add(new(template, exception, SafeLogLine.Format(DateTimeOffset.UnixEpoch, logLevel,
                typeof(PinManager).FullName!, eventId.Id, template, exception)));
        }
    }
}
