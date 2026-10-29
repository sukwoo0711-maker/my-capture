using System.IO;
using System.Reflection;
using System.Text.Json;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class PinTranslationContentLossCorpusR7Tests(ITestOutputHelper output)
{
    [PinOverlayContentComparisonFact]
    public void ReportDetectedLossAndAcceptedRecoveryForEachDirection()
    {
        string before = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.ComparisonVariable)!;
        string after = Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)!;
        MethodInfo preserves = typeof(PinTranslationService).GetMethod("PreservesProtectedValues", BindingFlags.NonPublic | BindingFlags.Static)!;
        var records = new List<object>();
        foreach (string direction in new[] { "en-ko", "ko-en" })
        {
            PinTranslationDirection value = direction == "en-ko" ? PinTranslationDirection.EnglishToKorean : PinTranslationDirection.KoreanToEnglish;
            var baseline = Directory.EnumerateFiles(before, "*." + direction + "-translations.json")
                .SelectMany(file => JsonSerializer.Deserialize<TranslatedText[]>(File.ReadAllText(file))!
                    .Select(item => new { File = Path.GetFileName(file), Item = item,
                        Issue = PinTranslationService.ContentLossReason(item.Source, item.Text ?? string.Empty, value) }))
                .Where(item => item.Issue.Length > 0).Select(item => new
                {
                    item.File, item.Item.Source, item.Item.Text, item.Issue, item.Item.NeedsReview,
                    AcceptedByOldChecks = !item.Item.NeedsReview && !string.IsNullOrWhiteSpace(item.Item.Text)
                        && (bool)preserves.Invoke(null, [item.Item.Source, item.Item.Text])!,
                }).ToArray();
            var current = Directory.EnumerateFiles(after, "*." + direction + "-recovery.json")
                .SelectMany(file => JsonSerializer.Deserialize<PinTranslationRecoveryDecision[]>(File.ReadAllText(file))!
                    .Select(item => new { File = Path.GetFileName(file), Item = item })).ToArray();
            Assert.NotEmpty(current);
            var acceptedLoss = current.Where(item => item.Item.AcceptedText is { } text &&
                PinTranslationService.ContentLossReason(item.Item.Source, text, value).Length > 0).ToArray();
            Assert.Empty(acceptedLoss);
            var detected = current.Where(item => item.Item.Issue.Length > 0).ToArray();
            var record = new
            {
                Direction = direction, BaselineRawDetected = baseline.Length,
                BaselineAcceptedDetected = baseline.Count(item => item.AcceptedByOldChecks),
                CurrentAttemptedSentences = current.Length, CurrentInitialDetected = detected.Length,
                Recovered = detected.Count(item => item.Item.Recovered),
                SourceFallback = detected.Count(item => item.Item.SourceFallback),
                AcceptedDetectedAfter = acceptedLoss.Length, Baseline = baseline, CurrentDetected = detected,
            };
            records.Add(record);
            output.WriteLine($"{direction}: baseline raw={record.BaselineRawDetected}, accepted by old checks={record.BaselineAcceptedDetected}; "
                + $"current initial={record.CurrentInitialDetected}, recovered={record.Recovered}, source fallback={record.SourceFallback}, accepted loss after=0.");
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(after, "content-loss-summary.json"),
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class PinOverlayContentComparisonFactAttribute : FactAttribute
{
    public PinOverlayContentComparisonFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.ComparisonVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)))
            Skip = "Set the previous corpus and new evidence directories to compare recorded content decisions.";
    }
}
