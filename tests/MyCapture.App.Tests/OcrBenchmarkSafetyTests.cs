using System.IO;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using RapidOcrNet;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrBenchmarkSafetyTests
{
    [Theory]
    [InlineData("report")]
    [InlineData("../../report")]
    [InlineData(@"C:\external\report")]
    public void BenchmarkOutputs_UseOnlyFileNameUnderFixedTempRoot(string requested)
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "mycapture-ocr-merge-replay", "report"),
            OcrModelBenchmarkTests.ReplayOutputRoot(requested));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "mycapture-ocr-benchmark", "report"),
            OcrModelBenchmarkTests.SafeOutputRoot(requested));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("  ")]
    [InlineData("folder/")]
    public void BenchmarkOutputs_RejectMissingOrTraversalFolderName(string requested)
    {
        Assert.Throws<ArgumentException>(() => OcrModelBenchmarkTests.ReplayOutputRoot(requested));
        Assert.Throws<ArgumentException>(() => OcrModelBenchmarkTests.SafeOutputRoot(requested));
    }

    [Fact]
    public void Replay_RequiresExactlyTheSameNonemptyIdsInBothBaselines()
    {
        Assert.True(OcrModelBenchmarkTests.ReplayIdsMatch(["a", "b"], ["b", "a"], ["a", "b"]));
        Assert.False(OcrModelBenchmarkTests.ReplayIdsMatch(["a", "b"], ["a", "b"], ["a"]));
        Assert.False(OcrModelBenchmarkTests.ReplayIdsMatch(["a"], ["a", "b"], ["a"]));
        Assert.False(OcrModelBenchmarkTests.ReplayIdsMatch(["a", "b"], ["a"], ["a"]));
        Assert.False(OcrModelBenchmarkTests.ReplayIdsMatch(["a"], ["b"], ["a"]));
        Assert.False(OcrModelBenchmarkTests.ReplayIdsMatch([], [], []));
    }

    [Fact]
    public void BenchmarkNeuralRunner_RunsTelemetryGuardBeforeAnyModelLoads()
    {
        int calls = 0;
        var runtime = new OnnxRuntimeInitialization(() =>
        {
            calls++;
            throw new InvalidOperationException("Telemetry guard ran first.");
        });
        // Nonexistent model names: reaching InitModels would fail differently.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new OcrModelBenchmarkTests.NeuralRunner("missing-det.onnx", "missing-cls.onnx", "missing-rec.onnx",
                "missing-dict.txt", RapidOcrOptions.Default, OcrQuality.Accurate, runtime));
        Assert.Equal("Telemetry guard ran first.", error.Message);
        Assert.Equal(1, calls);
    }
}
