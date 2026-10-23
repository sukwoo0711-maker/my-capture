using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MyCapture.Ocr;
using Xunit;
using ProductResult = MyCapture.Ocr.OcrResult;

namespace MyCapture.App.Tests;

public sealed partial class OcrModelBenchmarkTests
{
    /// <summary>
    /// Opt-in, offline replay of the product line merger over engine results retained by a
    /// previous dual benchmark run. No engine, model, image, profile or clipboard is used:
    /// only results.json files named by MYCAPTURE_OCR_MERGE_REPLAY are read, and the report
    /// goes below the temp directory using the MYCAPTURE_OCR_MERGE_REPLAY_OUT folder name.
    /// Wall time still requires the full benchmark.
    /// </summary>
    [Fact]
    public void ReplayOcrMergerOnRetainedResults_WhenExplicitlyEnabled()
    {
        string? inputs = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_MERGE_REPLAY");
        if (string.IsNullOrWhiteSpace(inputs)) return;

        string output = ReplayOutputRoot(Environment.GetEnvironmentVariable("MYCAPTURE_OCR_MERGE_REPLAY_OUT")
            ?? throw new InvalidOperationException("Set MYCAPTURE_OCR_MERGE_REPLAY_OUT to a report folder name."));
        string dualName = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_MERGE_REPLAY_DUAL") ?? "A-dual";
        string neuralName = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_MERGE_REPLAY_NEURAL") ?? "A-cur";
        Directory.CreateDirectory(output);

        var markdown = new StringBuilder("# OCR merger replay\n\n");
        markdown.AppendLine("Stored W and neural results from the dual pipeline are merged again by the current product merger. " +
            "Gate: each category CER <= min(W, neural) + 0.5 pp and overall CER strictly below both single-engine pipelines. " +
            "Single-engine CER comes from the W and neural pipelines of the same run. Latency is not part of a replay.\n");
        bool allPassed = true;
        foreach (string path in inputs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement[] pipelines = document.RootElement.GetProperty("pipelines").EnumerateArray().ToArray();
            Dictionary<string, JsonElement> W = Images(pipelines, "W"), N = Images(pipelines, neuralName);
            Dictionary<string, JsonElement> D = Images(pipelines, dualName);
            Assert.True(ReplayIdsMatch(W.Keys, N.Keys, D.Keys),
                $"{path}: W, {neuralName} and {dualName} must cover the same images.");

            var rows = new List<ReplayRow>();
            var timer = new Stopwatch();
            var mergeTimes = new List<double>();
            foreach ((string id, JsonElement image) in D.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string truth = image.GetProperty("truth").GetString() ?? "";
                ProductResult windows = ReadResult(image.GetProperty("windowsResult"));
                ProductResult neural = ReadResult(image.GetProperty("neuralResult"));
                timer.Restart();
                ProductResult merged = OcrResultMerger.Merge(windows, neural, TimeSpan.Zero);
                mergeTimes.Add(timer.Elapsed.TotalMilliseconds);
                rows.Add(new ReplayRow(id, image.GetProperty("category").GetString() ?? "", truth,
                    W[id].GetProperty("output").GetString() ?? "", N[id].GetProperty("output").GetString() ?? "",
                    image.GetProperty("output").GetString() ?? "", merged.Text,
                    windows.Text, neural.Text));
            }

            markdown.AppendLine($"## {path}\n");
            markdown.AppendLine("| Category | Images | W CER | Neural CER | Stored dual CER | Replayed CER | Maximum | Passed | Replayed exact lines |\n|---|---:|---:|---:|---:|---:|---:|---|---:|");
            bool passed = true;
            foreach (IGrouping<string, ReplayRow> category in rows.GroupBy(row => row.Category).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                double w = Cer(category, row => row.WindowsPipeline), n = Cer(category, row => row.NeuralPipeline);
                double stored = Cer(category, row => row.StoredDual), replayed = Cer(category, row => row.Replayed);
                double maximum = Math.Min(w, n) + 0.005;
                bool ok = replayed <= maximum;
                passed &= ok;
                markdown.AppendLine($"| {category.Key} | {category.Count()} | {Pct(w)} | {Pct(n)} | {Pct(stored)} | {Pct(replayed)} | {Pct(maximum)} | {ok} | {Pct(ExactLines(category))} |");
            }
            double overallW = Cer(rows, row => row.WindowsPipeline), overallN = Cer(rows, row => row.NeuralPipeline);
            double overallStored = Cer(rows, row => row.StoredDual), overallReplayed = Cer(rows, row => row.Replayed);
            bool overallOk = overallReplayed < overallW && overallReplayed < overallN;
            passed &= overallOk;
            allPassed &= passed;
            mergeTimes.Sort();
            markdown.AppendLine($"| **overall** | {rows.Count} | {Pct(overallW)} | {Pct(overallN)} | {Pct(overallStored)} | {Pct(overallReplayed)} | < both | {overallOk} | {Pct(ExactLines(rows))} |\n");
            markdown.AppendLine($"Replay accuracy gate: **{(passed ? "passed" : "failed")}**. Merge median {Percentile(mergeTimes.ToArray(), .5).ToString("0.000", CultureInfo.InvariantCulture)} ms, " +
                $"maximum {mergeTimes[^1].ToString("0.000", CultureInfo.InvariantCulture)} ms per image.\n");

            // Every image where the replayed merge is worse than the better single engine, or
            // differs from the stored merge, so a rule change can be inspected line by line.
            foreach (ReplayRow row in rows)
            {
                int w = Measure(row.Truth, row.WindowsPipeline).Edits, n = Measure(row.Truth, row.NeuralPipeline).Edits;
                int stored = Measure(row.Truth, row.StoredDual).Edits, replayed = Measure(row.Truth, row.Replayed).Edits;
                if (replayed <= Math.Min(w, n) && row.Replayed == row.StoredDual) continue;
                markdown.AppendLine($"### {row.Id} — edits W {w}, neural {n}, stored dual {stored}, replayed {replayed}\n");
                markdown.AppendLine("```text\nTRUTH\n" + row.Truth + "\n\nWINDOWS (dual run)\n" + row.DualWindows +
                    "\n\nNEURAL (dual run)\n" + row.DualNeural + "\n\nSTORED DUAL\n" + row.StoredDual +
                    "\n\nREPLAYED\n" + row.Replayed + "\n```\n");
            }
        }
        File.WriteAllText(Path.Combine(output, "replay.md"), markdown.ToString(), new UTF8Encoding(false));
        Assert.True(allPassed || Environment.GetEnvironmentVariable("MYCAPTURE_OCR_MERGE_REPLAY_REPORT_ONLY") == "1",
            "Replay accuracy gate failed; see " + Path.Combine(output, "replay.md"));

        static Dictionary<string, JsonElement> Images(JsonElement[] pipelines, string name) =>
            pipelines.Where(p => p.GetProperty("name").GetString() == name && p.GetProperty("status").GetString() == "completed")
                .SelectMany(p => p.GetProperty("images").EnumerateArray())
                .ToDictionary(i => i.GetProperty("id").GetString()!, i => i, StringComparer.Ordinal);

        static double Cer(IEnumerable<ReplayRow> rows, Func<ReplayRow, string> output)
        {
            int edits = 0, characters = 0;
            foreach (ReplayRow row in rows)
            {
                TextMetrics metrics = Measure(row.Truth, output(row));
                edits += metrics.Edits;
                characters += metrics.TruthCharacters;
            }
            return edits / (double)Math.Max(1, characters);
        }

        static double ExactLines(IEnumerable<ReplayRow> rows)
        {
            int matched = 0, lines = 0;
            foreach (ReplayRow row in rows)
            {
                TextMetrics metrics = Measure(row.Truth, row.Replayed);
                matched += metrics.MatchedLines;
                lines += metrics.TruthLines;
            }
            return matched / (double)Math.Max(1, lines);
        }
    }

    internal static string ReplayOutputRoot(string requested)
    {
        string name = Path.GetFileName(requested);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            throw new ArgumentException("A report folder name is required.", nameof(requested));
        return Path.Combine(Path.GetTempPath(), "mycapture-ocr-merge-replay", name);
    }

    internal static bool ReplayIdsMatch(IEnumerable<string> windows, IEnumerable<string> neural, IEnumerable<string> dual)
    {
        var ids = new HashSet<string>(dual, StringComparer.Ordinal);
        return ids.Count > 0 && ids.SetEquals(windows) && ids.SetEquals(neural);
    }

    private static ProductResult ReadResult(JsonElement result)
    {
        var status = (OcrStatus)result.GetProperty("status").GetInt32();
        string language = result.TryGetProperty("languageTag", out JsonElement tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString()! : string.Empty;
        string message = result.TryGetProperty("message", out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()! : string.Empty;
        OcrLine[] lines = result.GetProperty("lines").EnumerateArray().Select(line => new OcrLine(
            line.GetProperty("text").GetString() ?? "", ReadRect(line.GetProperty("bounds")),
            line.GetProperty("words").EnumerateArray().Select(word => new OcrWord(
                word.GetProperty("text").GetString() ?? "", ReadRect(word.GetProperty("bounds")))).ToArray())).ToArray();
        return status switch
        {
            OcrStatus.Success => ProductResult.Success(result.GetProperty("text").GetString() ?? "", language, lines, TimeSpan.Zero),
            OcrStatus.NoText => ProductResult.NoText(language, TimeSpan.Zero),
            OcrStatus.Unavailable => ProductResult.Unavailable(message),
            OcrStatus.Cancelled => ProductResult.Cancelled(),
            _ => ProductResult.Failed(message),
        };

        static OcrRect ReadRect(JsonElement bounds) => new(bounds.GetProperty("x").GetDouble(),
            bounds.GetProperty("y").GetDouble(), bounds.GetProperty("width").GetDouble(), bounds.GetProperty("height").GetDouble());
    }

    private sealed record ReplayRow(string Id, string Category, string Truth, string WindowsPipeline,
        string NeuralPipeline, string StoredDual, string Replayed, string DualWindows, string DualNeural);
}
