using System.Diagnostics;
using System.Text.Json;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentProbeMetricsTests
{
    [Fact]
    public void ElementStepIsOptInForEitherRenderer()
    {
        Assert.DoesNotContain("selector-elements", ResidentProbeOptions.ReadVariants([]));
        Assert.Contains("selector-elements", ResidentProbeOptions.ReadVariants(["--resident-variant", "selector-elements"]));
        var wpf = ResidentProbeOptions.ReadVariants(["--resident-variant", "selector-wpf,selector-elements"]);
        Assert.Contains("selector-elements", wpf);
        Assert.False(ResidentProbeOptions.UsesNativeSelector(wpf));
    }
    [Fact]
    public void ReleaseMeasurementsDoNotEnableDiagnosticWorkByDefault()
    {
        Assert.Empty(ResidentProbeOptions.ReadVariants(["--selftest-resident", "unused"]));
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ReadVariants(["--resident-variant", "unknown"]));
    }

    [Theory]
    [InlineData("legacy-capture-settle")]
    [InlineData("no-idle-gc")]
    [InlineData("legacy-flight-bounds")]
    [InlineData("no-private-working-set")]
    [InlineData("selector-breakdown")]
    [InlineData("receipt-breakdown")]
    [InlineData("no-key-panel")]
    [InlineData("selector-wpf")]
    public void AttributionVariantsAreExplicitAndIndependentlySelectable(string variant)
    {
        IReadOnlySet<string> selected = ResidentProbeOptions.ReadVariants(["--resident-variant", variant.ToUpperInvariant()]);
        Assert.Single(selected);
        Assert.Contains(variant, selected, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("no-flight,selector-native-dib")]
    [InlineData("no-flight,selector-wpf")]
    [InlineData("no-flight,selector-native-dib,selector-chrome-fresh")]
    [InlineData("no-flight,selector-native-dib,selector-chrome-parallel")]
    [InlineData("no-flight,selector-native-dib,selector-chrome-dib")]
    [InlineData("no-flight,selector-breakdown")]
    public void StageOneSelectorMeasurementVariantsAreAccepted(string variants)
    {
        IReadOnlySet<string> selected = ResidentProbeOptions.ReadVariants(["--resident-variant", variants]);
        Assert.Equal(variants.Split(',').Length, selected.Count);
        foreach (string variant in variants.Split(',')) Assert.Contains(variant, selected);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("selector-native-dib", true)]
    [InlineData("selector-wpf", false)]
    [InlineData("SELECTOR-WPF", false)]
    public void RendererVariantsSelectNativeByDefaultAndKeepWpfAvailable(string variants, bool native)
    {
        IReadOnlySet<string> selected = ResidentProbeOptions.ReadVariants(["--resident-variant", variants]);
        Assert.Equal(native, ResidentProbeOptions.UsesNativeSelector(selected));
        Assert.Equal(NativeSelectorChromeStrategy.Retained, ResidentProbeOptions.GetSelectorChromeStrategy(selected));
    }

    [Theory]
    [InlineData("selector-native-dib")]
    [InlineData("selector-chrome-fresh")]
    [InlineData("selector-chrome-parallel")]
    [InlineData("selector-chrome-dib")]
    [InlineData("selector-native-dib,selector-chrome-fresh")]
    public void WpfVariantRejectsContradictoryNativeRequests(string native)
    {
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ReadVariants(["--resident-variant", "selector-wpf," + native]));
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ReadVariants(["--resident-variant", native + ",SELECTOR-WPF"]));
    }

    [Fact]
    public void ReceiptMarksHaveTheirOwnSchemaAndCollectionDeltas()
    {
        var receipt = new ResidentProbeRecorder.ReceiptBreakdown("capture-30", 2, 71.5, 2, 1, 0,
            [new ResidentProbeRecorder.Mark("card-shown", 60.2, 1)]);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(receipt));
        JsonElement root = document.RootElement;
        Assert.Equal(71.5, root.GetProperty("CardMs").GetDouble());
        Assert.Equal(0, root.GetProperty("Gen2").GetInt32());
        Assert.False(root.TryGetProperty("SelectorMs", out _));
    }

    [Fact]
    public void PrivateWorkingSetCountsNonsharedPagesRatherThanCommitOrShareCount()
    {
        nuint[] entries = [0x12345004, 0x23456104, 0x34567024, 0x456781E4];
        Assert.Equal(8192, ResidentProbeWorkingSet.CountPrivateBytes(entries, 4096));
        Assert.Equal(0, ResidentProbeWorkingSet.CountPrivateBytes([], 4096));
    }

    [Fact]
    public void PrivateWorkingSetCanBeReadForThisProcessWithoutScreenAccess()
    {
        using Process process = Process.GetCurrentProcess();
        var reader = new ResidentProbeWorkingSet();
        (long? bytes, string? error) = reader.Read(process.Handle, process.WorkingSet64);
        Assert.Null(error);
        Assert.NotNull(bytes);
        Assert.True(bytes > 0);
    }

    [Fact]
    public void LibraryDeleteRunsLastInTheReleaseTableAndItsBudgetRejectsOneShellOperationPerCapture()
    {
        Assert.Equal(ResidentProbeLibraryDelete.Scenario, ResidentProbeOptions.AllScenarios[^1]);
        Assert.Equal(124, ResidentProbeLibraryDelete.Count(quick: false));
        Assert.Equal(12, ResidentProbeLibraryDelete.Count(quick: true));
        // 124 directories with the real Recycle Bin on the developer PC, 2026-11-06. One Shell
        // operation per capture kept the cards until the move ended (42.6 s) and restored for 396 s;
        // one operation for the selection moved them in 14.9 s and restored them in 8.1 s, with the
        // cards gone as in the slowest first delete measured (1.2 s).
        IReadOnlyList<string> before = ResidentProbeLibraryDelete.OverBudget(new(124, 42_560, 42_560, 396_370));
        Assert.Equal(3, before.Count);
        Assert.Contains("library-delete: 124 selected → cards gone: 42560 ms, budget 5000 ms", before);
        Assert.Empty(ResidentProbeLibraryDelete.OverBudget(new(124, 1_200, 14_920, 8_100)));
        // The quick probe of 12 on the same PC: the build before the batches (d28e9172) and the batched one.
        Assert.Equal(new[] { "library-delete: undo 12 → result on the status line: 28204 ms, budget 6800 ms" },
            ResidentProbeLibraryDelete.OverBudget(new(12, 1_652, 1_652, 28_204)));
        Assert.Empty(ResidentProbeLibraryDelete.OverBudget(new(12, 975, 1_097, 4_053)));
    }

    [Fact]
    public void RenderCompletionRemainsSeparateFromFirstRenderingAndUnknownIsNull()
    {
        var first = new ResidentProbeRecorder.Breakdown("capture", 1, 29, 800, []);
        string unavailable = JsonSerializer.Serialize(first with { RenderThreadCompleteError = "unavailable" });
        using JsonDocument missing = JsonDocument.Parse(unavailable);
        Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("RenderThreadCompleteMs").ValueKind);
        var complete = first with { RenderThreadCompleteMs = 51.2 };
        Assert.Equal(29, complete.SelectorMs);
        Assert.Equal(51.2, complete.RenderThreadCompleteMs);
    }
}
