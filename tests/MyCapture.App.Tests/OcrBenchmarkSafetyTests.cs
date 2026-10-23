using System.IO;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using RapidOcrNet;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrBenchmarkSafetyTests
{
    [Fact]
    public void BenchmarkOutputs_UseTheRequestedLocalFolder()
    {
        string owner = OwnedTestDirectory.Create("ocr-bench-output-");
        try
        {
            string requested = Path.Combine(owner, "nested", "report");
            Assert.Equal(requested, OcrModelBenchmarkTests.SafeOutputRoot(requested));
            Assert.True(Directory.Exists(requested));
            Assert.Equal(requested, OcrModelBenchmarkTests.SafeOutputRoot(requested + Path.DirectorySeparatorChar));
        }
        finally { OwnedTestDirectory.Delete(owner); }
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.ApplicationData, "")]
    [InlineData(Environment.SpecialFolder.ApplicationData, "bench")]
    [InlineData(Environment.SpecialFolder.LocalApplicationData, "")]
    [InlineData(Environment.SpecialFolder.LocalApplicationData, @"ocr-models\bench")]
    public void BenchmarkOutputs_RejectTheRealProfileBeforeCreatingAnything(Environment.SpecialFolder folder, string suffix)
    {
        // The guard only compares paths, so nothing is created or read in the real profile.
        string requested = Path.Combine(Environment.GetFolderPath(folder), "MyCapture", suffix);
        Assert.Throws<InvalidOperationException>(() => OcrModelBenchmarkTests.SafeOutputRoot(requested));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BenchmarkOutputs_RequireAFolder(string requested) =>
        Assert.Throws<ArgumentException>(() => OcrModelBenchmarkTests.SafeOutputRoot(requested));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\reports")]
    public void BenchmarkOutputs_RejectDriveRootsAndNetworkPaths(string requested) =>
        Assert.Throws<IOException>(() => OcrModelBenchmarkTests.SafeOutputRoot(requested));

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
