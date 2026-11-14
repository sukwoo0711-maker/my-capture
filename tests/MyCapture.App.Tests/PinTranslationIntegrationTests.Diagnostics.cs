using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class PinTranslationIntegrationTests
{
    [Fact]
    public Task FailedPictureTranslationLogsExceptionMetadataWithoutPrivateTextAndKeepsOriginal() => RunAsync(async () =>
    {
        const string source = "Save the private project document before closing the window.";
        const string output = "비공개 프로젝트 문서를 저장합니다.";
        const string privateDetail = "PRIVATE_MODEL_FAILURE_DETAIL";
        var failure = new InvalidOperationException($"{privateDetail}: source={source}; output={output}");
        var translator = new FakeTranslator { Failure = failure };
        var log = new RecordingSafeTranslationLog();
        PinManager manager = Manager(translator, log);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult<string?>(source));
        try
        {
            var image = Image();
            PinWindow pin = manager.PinImage(image);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;

            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.False(pin.IsShowingTranslation);
            Assert.Same(image, pin.PinnedContent.Image); // The frozen source keeps every original pixel.
            Assert.Null(pin.DisplayedText);
            Assert.Equal((UiText.Get("Pin_Translate"), UiText.Get("Pin_TranslationRetry"), true), pin.TranslationActionForTest);

            const string template = "Pin translation failed ({ErrorType})";
            var entry = Assert.Single(log.Entries, entry => entry.Template == template);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Same(failure, entry.Exception);
            Assert.True(SafeLogLine.IsWellFormed(entry.Line), entry.Line);
            Assert.Contains(" msg=" + SafeLogLine.TemplateId(template), entry.Line, StringComparison.Ordinal);
            Assert.Contains(" ex=System.InvalidOperationException hresult=0x80131509", entry.Line, StringComparison.Ordinal);
            Assert.Contains(" site=MyCapture.App.Tests.PinTranslationIntegrationTests+FakeTranslator+", entry.Line, StringComparison.Ordinal);
            foreach (string line in log.Entries.Select(entry => entry.Line))
            {
                Assert.DoesNotContain(source, line, StringComparison.Ordinal);
                Assert.DoesNotContain(output, line, StringComparison.Ordinal);
                Assert.DoesNotContain(privateDetail, line, StringComparison.Ordinal);
                Assert.DoesNotContain(failure.Message, line, StringComparison.Ordinal);
            }
        }
        finally { manager.CloseAll(); }
    });

    // Use the file logger's safe formatter without creating a file or rendering message arguments.
    private sealed class RecordingSafeTranslationLog : ILogger
    {
        internal List<(LogLevel Level, string? Template, Exception? Exception, string Line)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string? template = SafeLogLine.TemplateOf(state);
            Entries.Add((logLevel, template, exception, SafeLogLine.Format(DateTimeOffset.UnixEpoch,
                logLevel, typeof(PinManager).FullName!, eventId.Id, template, exception)));
        }
    }
}
