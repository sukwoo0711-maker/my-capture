using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MyCapture.App.Diagnostics;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

internal sealed record PinOverlayCorpusDecision(string Text, OcrRect Bounds, string Reason, double LineArea);

/// <summary>Coverage is source line area, not the enclosing paragraph rectangle or translated ink.</summary>
internal sealed class PinOverlayCorpusCoverage
{
    internal const string ManifestVariable = "MYCAPTURE_PIN_OVERLAY_COVERAGE_MANIFEST";
    private readonly HashSet<(string Name, string Direction)> expected;
    private readonly List<Row> rows = [];

    internal PinOverlayCorpusCoverage(string manifest)
    {
        string fullPath = Path.GetFullPath(manifest);
        string directory = DiagnosticOutputPaths.Create(Path.GetDirectoryName(fullPath)!);
        expected = File.ReadLines(DiagnosticOutputPaths.Child(directory, Path.GetFileName(fullPath))).Skip(1).Where(line => line.Length > 0)
            .Select(line => line.Split('\t')).Select(fields => (fields[0], fields[4])).ToHashSet();
        Assert.Equal(37, expected.Count);
        Assert.All(expected, item => Assert.Contains(item.Direction, new[] { "en-ko", "ko-en" }));
    }

    internal IEnumerable<string> Directions(string name) => expected.Where(item => item.Name == name).Select(item => item.Direction);

    internal void Add(string name, string direction, IReadOnlyList<PinOverlayCorpusDecision> before,
        IReadOnlyList<PinOverlayCorpusDecision> after)
    {
        if (expected.Contains((name, direction))) rows.Add(new(name, direction, before, after));
    }

    internal void WriteAndAssert(string directory, ITestOutputHelper output)
    {
        var table = new StringBuilder("name\tdirection\tbeforeEligibleArea\tbeforeRenderedArea\tbeforeSharePct\tafterEligibleArea\tafterRenderedArea\tafterSharePct\tdeltaPp\tfit\tallRejected\tdropReasons\n");
        foreach (Row row in rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Direction, StringComparer.Ordinal))
        {
            Metrics before = Measure(row.Before), after = Measure(row.After);
            double? delta = after.Share - before.Share;
            string reasons = after.Share is null
                ? row.After.Any(item => item.Reason == "protected") ? "no eligible source after code protection" : "no eligible overlay source"
                : before.Share is null ? "no eligible baseline source"
                : delta < -1e-10 ? string.Join("; ", row.After
                .Where(item => Eligible(item) && item.Reason != "rendered")
                .GroupBy(item => item.Reason).OrderByDescending(group => group.Sum(item => item.LineArea))
                .Select(group => $"{group.Key}: {group.Count()} blocks, {group.Sum(item => item.LineArea):F3} px2")) : "";
            table.Append(CultureInfo.InvariantCulture,
                $"{row.Name}\t{row.Direction}\t{before.Eligible:F3}\t{before.Rendered:F3}\t{Percent(before.Share)}\t{after.Eligible:F3}\t{after.Rendered:F3}\t{Percent(after.Share)}\t{Percent(delta)}\t{after.Fit}\t{after.Rejected}\t{reasons}\n");
            output.WriteLine($"COVERAGE {row.Name} {row.Direction}: {Percent(before.Share)}% -> {Percent(after.Share)}%, delta {Percent(delta)} pp; {reasons}");
        }
        Metrics totalBefore = Measure(rows.SelectMany(row => row.Before)), totalAfter = Measure(rows.SelectMany(row => row.After));
        table.Append(CultureInfo.InvariantCulture,
            $"TOTAL\t-\t{totalBefore.Eligible:F3}\t{totalBefore.Rendered:F3}\t{Percent(totalBefore.Share)}\t{totalAfter.Eligible:F3}\t{totalAfter.Rendered:F3}\t{Percent(totalAfter.Share)}\t{Percent(totalAfter.Share - totalBefore.Share)}\t{totalAfter.Fit}\t{totalAfter.Rejected}\t\n");
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "coverage.tsv"), table.ToString(), Encoding.UTF8);
        output.WriteLine($"COVERAGE TOTAL: {Percent(totalBefore.Share)}% -> {Percent(totalAfter.Share)}%; fit={totalAfter.Fit}, all-rejected={totalAfter.Rejected}");
        var failures = new List<string>();
        if (!expected.SetEquals(rows.Select(row => (row.Name, row.Direction))) || rows.Count != expected.Count)
            failures.Add("Coverage did not observe each of the same 37 c1 directions exactly once.");
        if (totalAfter.Share + 1e-10 < totalBefore.Share) failures.Add("Total rendered source-line area share decreased.");
        failures.AddRange(rows.Where(row => Measure(row.After).Share + .03 + 1e-10 < Measure(row.Before).Share)
            .Select(row => row.Name + "." + row.Direction + " decreased more than 3 percentage points."));
        if (totalAfter.Fit + totalAfter.Rejected > 96) failures.Add($"fit + all-rejected = {totalAfter.Fit + totalAfter.Rejected}, above 96.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string Percent(double? ratio) => ratio.HasValue
        ? (ratio.Value * 100).ToString("F6", CultureInfo.InvariantCulture) : "N/A";

    private static bool Eligible(PinOverlayCorpusDecision item) => item.Reason is not "not-eligible" and not "protected";

    private static Metrics Measure(IEnumerable<PinOverlayCorpusDecision> source)
    {
        PinOverlayCorpusDecision[] items = source.ToArray();
        return new(items.Where(Eligible).Sum(item => item.LineArea), items.Where(item => item.Reason == "rendered").Sum(item => item.LineArea),
            items.Count(item => item.Reason == "fit"), items.Count(item => item.Reason == "all-rejected"));
    }

    private sealed record Row(string Name, string Direction, IReadOnlyList<PinOverlayCorpusDecision> Before, IReadOnlyList<PinOverlayCorpusDecision> After);
    private readonly record struct Metrics(double Eligible, double Rendered, int Fit, int Rejected)
    {
        // Zero eligible area contributes no mass to the weighted total and has no direction ratio.
        internal double? Share => Eligible > 0 ? Rendered / Eligible : null;
    }
}

/// <summary>
/// Saves exact model results, including rejected values. Changed block boundaries never guess a
/// translation from a neighbouring block: only a missing exact source unit invokes the local model.
/// </summary>
internal sealed class PinOverlayCorpusTranslations : ITextTranslator
{
    internal const string CacheVariable = "MYCAPTURE_PIN_OVERLAY_TRANSLATIONS";
    private readonly ITextTranslator fallback;
    private readonly string outputPath;
    private readonly Dictionary<string, TranslatedText> values;

    internal PinOverlayCorpusTranslations(ITextTranslator fallback, string outputDirectory, string stem, string? inputDirectory)
    {
        this.fallback = fallback;
        outputPath = DiagnosticOutputPaths.Child(outputDirectory, stem + "-translations.json");
        string? inputPath = inputDirectory is null ? null
            : DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(inputDirectory), stem + "-translations.json");
        values = inputPath is not null && File.Exists(inputPath)
            ? JsonSerializer.Deserialize<TranslatedText[]>(File.ReadAllText(inputPath))!.ToDictionary(item => item.Source, StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
    }

    internal int ModelUnits { get; private set; }
    internal int CachedUnits { get; private set; }
    public long ModelDownloadBytes => fallback.ModelDownloadBytes;
    public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => fallback.IsModelInstalledAsync(cancellationToken);
    public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The corpus only uses the explicitly supplied local models.");

    public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
        IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
    {
        string[] missing = sources.Where(source => !values.ContainsKey(source)).Distinct(StringComparer.Ordinal).ToArray();
        CachedUnits += sources.Count(source => values.ContainsKey(source));
        if (missing.Length > 0)
        {
            IReadOnlyList<TranslatedText> results = await fallback.TranslateAsync(missing, null, cancellationToken);
            Assert.Equal(missing.Length, results.Count);
            for (int index = 0; index < missing.Length; index++)
            {
                Assert.Equal(missing[index], results[index].Source);
                values.Add(missing[index], results[index]);
            }
            ModelUnits += missing.Length;
        }
        Save();
        progress?.Report(new(sources.Count, sources.Count));
        return sources.Select(source => values[source]).ToArray();
    }

    internal void Save() => File.WriteAllText(outputPath,
        JsonSerializer.Serialize(values.Values.OrderBy(item => item.Source, StringComparer.Ordinal), new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
}
