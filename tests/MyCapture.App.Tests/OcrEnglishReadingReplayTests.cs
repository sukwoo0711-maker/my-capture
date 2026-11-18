using MyCapture.App.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in: translates a recorded set of corpus readings (<c>&lt;name&gt;-ocr.json</c> and <c>&lt;name&gt;-source.png</c>)
/// through the picture-translation service, so two readings of the same pictures can be compared by what the
/// translator keeps as source. Cached translations are reused and changed text goes to the local model.
/// </summary>
public sealed class OcrEnglishReadingReplayTests(ITestOutputHelper output)
{
    internal const string ReplayVariable = "MYCAPTURE_OCR_ENGLISH_READING_REPLAY";

    [OcrEnglishReadingReplayFact]
    public Task TranslateRecordedReadings() => new PinTranslationOverlayCorpusTests(output)
        .RunCorpusAsync(DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(ReplayVariable)!));
}

public sealed class OcrEnglishReadingReplayFactAttribute : FactAttribute
{
    public OcrEnglishReadingReplayFactAttribute()
    {
        string[] variables = [OcrEnglishReadingReplayTests.ReplayVariable, PinTranslationOverlayCorpusTests.CorpusVariable,
            PinTranslationOverlayEvidenceTests.OutputVariable, PinTranslationOverlayEvidenceTests.ModelsVariable];
        if (variables.Any(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))))
            Skip = "Set the replay, corpus, evidence and local model folders to translate recorded readings.";
    }
}
