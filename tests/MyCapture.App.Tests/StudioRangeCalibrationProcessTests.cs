using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StudioRangeCalibrationProcessTests
{
    [Fact]
    public void FindsSiblingAppBeforeTestHostAndOnlyFallsBackToMyCapture()
    {
        const string app = @"C:\test output\MyCapture.exe";
        Assert.Equal(app, StudioRangeCalibrationProcess.ResolveExecutable(@"C:\test output", @"C:\dotnet\testhost.exe", p => p == app));
        Assert.Equal(@"C:\install\MyCapture.exe", StudioRangeCalibrationProcess.ResolveExecutable(@"C:\missing",
            @"C:\install\MyCapture.exe", p => p == @"C:\install\MyCapture.exe"));
        Assert.Throws<FileNotFoundException>(() => StudioRangeCalibrationProcess.ResolveExecutable(@"C:\missing",
            @"C:\dotnet\testhost.exe", _ => false));
        Assert.Throws<FileNotFoundException>(() => StudioRangeCalibrationProcess.ResolveExecutable(@"C:\missing",
            @"C:\dotnet\testhost.exe", p => p.EndsWith("testhost.exe", StringComparison.Ordinal)));
    }

    [Fact]
    public void ChildIsHiddenAndIsolatedWithAnUnquotedArgumentList()
    {
        string root = OwnedTestDirectory.Create("mc-calibration-start-");
        try
        {
            string output = Path.Combine(root, "result with spaces.json");
            string data = Path.Combine(root, "child data");
            ProcessStartInfo start = StudioRangeCalibrationProcess.BuildStartInfo(@"C:\program files\MyCapture.exe", output, data);
            Assert.Equal(output, StudioRangeCalibrationProcess.ParseArguments(start.ArgumentList.ToArray()));
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
            Assert.Equal(root, start.WorkingDirectory);
            Assert.Empty(start.Arguments);
            Assert.Equal(data, start.Environment["MYCAPTURE_DATA_ROOT"]);
            Assert.Equal(data, start.Environment["TEMP"]);
            Assert.Equal(data, start.Environment["TMP"]);
            Assert.False(Directory.Exists(data));
            Assert.Equal(TimeSpan.FromSeconds(10), StudioRangeCalibrationProcess.Timeout);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData("result.json")]
    [InlineData(@"C:result.json")]
    [InlineData(@"C:\result.json")]
    [InlineData(@"\\server\share\result.json")]
    [InlineData(@"C:\Temp\..\result.json")]
    [InlineData(@"C:\Temp\result.json:stream")]
    [InlineData(@"C:\Temp\result.txt")]
    public void RejectsAmbiguousOutputPaths(string path) =>
        Assert.Throws<IOException>(() => StudioRangeCalibrationProcess.ValidateOutputPath(path));

    [Theory]
    [InlineData()]
    [InlineData("--measure-video-range")]
    [InlineData("--measure-video-range", @"C:\Temp\result.json", "extra")]
    [InlineData("--other", @"C:\Temp\result.json")]
    public void RejectsInvalidInternalArguments(params string[] args) =>
        Assert.Throws<ArgumentException>(() => StudioRangeCalibrationProcess.ParseArguments(args));

    [Fact]
    public void OutputRejectsJunctionBeforeWriting()
    {
        string owner = OwnedTestDirectory.Create("mc-calibration-link-");
        string target = OwnedTestDirectory.Create("mc-calibration-target-");
        string link = Path.Combine(owner, "link");
        try
        {
            UpdatePathsTests.CreateJunction(link, target);
            Assert.Throws<IOException>(() => StudioRangeCalibrationProcess.ValidateOutputPath(Path.Combine(link, "result.json")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            if (Directory.Exists(link)) TestRecycleBin.DeleteDirectory(link);
            OwnedTestDirectory.Delete(owner);
            OwnedTestDirectory.Delete(target);
        }
    }

    [Theory]
    [InlineData(1, 42, 0, null, true)]
    [InlineData(2, 42, 0, null, false)]
    [InlineData(1, 43, 0, null, false)]
    [InlineData(1, 42, 1, "probe failed", false)]
    [InlineData(1, 42, 0, "probe failed", false)]
    public void ProtocolRequiresBothMeasurementsAndOwnedProcessIdentity(int version, int pid, int exit, string? failure, bool valid)
    {
        var expected = new VideoRangeCalibrationResult(VideoRangePresentation.StudioRetained, VideoRangePresentation.Correct);
        string json = JsonSerializer.Serialize(new VideoRangeMeasurement(version, pid, expected, failure), StudioRangeCalibrationProcess.JsonOptions);
        if (valid) Assert.Equal(expected, StudioRangeCalibrationProcess.ReadResult(json, 42, exit));
        else Assert.Throws<IOException>(() => StudioRangeCalibrationProcess.ReadResult(json, 42, exit));
        json = JsonSerializer.Serialize(new VideoRangeMeasurement(1, 42, default, null), StudioRangeCalibrationProcess.JsonOptions);
        Assert.Throws<IOException>(() => StudioRangeCalibrationProcess.ReadResult(json, 42, 0));
        Assert.Throws<JsonException>(() => StudioRangeCalibrationProcess.ReadResult("{broken", 42, 0));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("oversized")]
    [InlineData("invalid")]
    [InlineData("exit")]
    [InlineData("cancel")]
    public async Task TransportReadsResultsAndStopsOnlyItsOwnedChildOnCancellation(string scenario)
    {
        string root = OwnedTestDirectory.Create("mc-calibration-transport-");
        var expected = new VideoRangeCalibrationResult(VideoRangePresentation.Correct, VideoRangePresentation.ExpandedAsStudio);
        using var cancellation = new CancellationTokenSource();
        FakeChild? child = null;
        try
        {
            string directory = Path.Combine(root, "measurement");
            Directory.CreateDirectory(directory);
            Task<VideoRangeCalibrationResult> task = StudioRangeCalibrationProcess.MeasureAsync(@"C:\app\MyCapture.exe", directory,
                cancellation.Token, start =>
                {
                    string json = JsonSerializer.Serialize(new VideoRangeMeasurement(1, 42, expected, null), StudioRangeCalibrationProcess.JsonOptions);
                    if (scenario == "invalid") json = "{broken";
                    if (scenario == "oversized") json = new string('x', 16 * 1024 + 1);
                    if (scenario != "missing") File.WriteAllText(start.ArgumentList[1], json);
                    child = new FakeChild(scenario == "cancel", scenario == "exit" ? 1 : 0);
                    return child;
                });
            if (scenario == "cancel") cancellation.Cancel();
            if (scenario == "success") Assert.Equal(expected, await task);
            else if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
            else if (scenario == "invalid") await Assert.ThrowsAsync<JsonException>(async () => await task);
            else await Assert.ThrowsAsync<IOException>(async () => await task);
            Assert.NotNull(child);
            Assert.Equal(scenario == "cancel", child.Killed);
            Assert.True(child.Disposed);
            // The existing retry cleanup is asynchronous and must remove the owned child root.
            for (int i = 0; i < 60 && Directory.Exists(directory); i++) await Task.Delay(100);
            Assert.False(Directory.Exists(directory));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public async Task LaunchFailureKeepsTheFallbackLogsOnceAndCleansTheOwnedDirectory()
    {
        string root = OwnedTestDirectory.Create("mc-calibration-launch-");
        string directory = Path.Combine(root, "measurement");
        Directory.CreateDirectory(directory);
        int starts = 0, logs = 0;
        var calibration = new StudioRangePresentationCalibration(token =>
            StudioRangeCalibrationProcess.MeasureAsync(@"C:\app\MyCapture.exe", directory, token, _ =>
            {
                Interlocked.Increment(ref starts);
                throw new IOException("fixture start failure");
            }), logFailure: _ => Interlocked.Increment(ref logs));
        try
        {
            Assert.Equal(default, await calibration.GetResultAsync());
            Assert.Equal(default, await calibration.GetResultAsync());
            Assert.Equal(1, starts);
            Assert.Equal(1, logs);
            Assert.Equal(VideoRangeCorrection.Expand, calibration.CorrectionFor(true));
            Assert.Equal(VideoRangeCorrection.None, calibration.CorrectionFor(false));
            Assert.IsType<IOException>(calibration.Failure);
            for (int i = 0; i < 60 && Directory.Exists(directory); i++) await Task.Delay(100);
            Assert.False(Directory.Exists(directory));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    private sealed class FakeChild(bool waitForCancellation, int exitCode) : StudioRangeCalibrationProcess.ICalibrationChild
    {
        public int Id => 42;
        public int ExitCode => exitCode;
        public bool HasExited { get; private set; } = !waitForCancellation;
        internal bool Killed { get; private set; }
        internal bool Disposed { get; private set; }
        public async Task WaitForExitAsync(CancellationToken token)
        {
            if (!HasExited) await Task.Delay(System.Threading.Timeout.Infinite, token);
        }
        public void Kill() { Killed = true; HasExited = true; }
        public void Dispose() => Disposed = true;
    }
}
