using System.IO;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoExportCalculationTests
{
    [Theory]
    [InlineData(500, true, 50)]
    [InlineData(700, false, 30)]
    [InlineData(1200, false, 0)]
    public void MeasuresSameIntervalBaselineAndKeepsSmallerOutput(int candidateBytes, bool reached, double actual)
    {
        var requests = new List<(int Bitrate, bool Baseline)>();
        string? directory = null;
        using (var result = VideoExportCalculation.Calculate(50, 1000, 1_000_000, (path, bitrate, baseline, token) =>
        {
            directory = Path.GetDirectoryName(path);
            requests.Add((bitrate, baseline));
            File.WriteAllBytes(path, new byte[baseline ? 1000 : candidateBytes]);
        }, CancellationToken.None))
        {
            Assert.Equal(2, requests.Count);
            Assert.Equal((1_000_000, true), requests[0]);
            Assert.False(requests[1].Baseline);
            Assert.InRange(requests[1].Bitrate, 64_000, requests[0].Bitrate);
            Assert.Equal(1000, result.BaselineBytes);
            Assert.Equal(Math.Min(1000, candidateBytes), result.ResultBytes);
            Assert.Equal(actual, result.ActualReduction, 6);
            Assert.Equal(reached, result.TargetReached);
            Assert.Equal(result.ResultBytes, new FileInfo(result.ResultPath).Length);
        }
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void ZeroTargetRendersOnlyStandardEditedOutput()
    {
        int calls = 0;
        using var result = VideoExportCalculation.Calculate(0, 1000, 1_000_000, (path, bitrate, baseline, token) =>
        {
            calls++;
            Assert.True(baseline);
            File.WriteAllBytes(path, [1, 2, 3]);
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(result.TargetReached);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationOrEncoderFailureCleansOnlyOwnedRenders(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        string? directory = null;
        int calls = 0;
        Exception? error = Record.Exception(() => VideoExportCalculation.Calculate(50, 1000, 1_000_000, (path, bitrate, baseline, token) =>
        {
            directory = Path.GetDirectoryName(path);
            calls++;
            File.WriteAllBytes(path, [1, 2, 3]);
            if (cancel) cancellation.Cancel();
            else throw new IOException("Synthetic encoder failure");
        }, cancellation.Token));
        if (cancel) Assert.IsType<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(-1, 1000, 1_000_000)]
    [InlineData(91, 1000, 1_000_000)]
    [InlineData(0, 0, 1_000_000)]
    [InlineData(0, 1000, 1)]
    public void RejectsInvalidInputsBeforeRendering(int reduction, double duration, int bitrate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoExportCalculation.Calculate(reduction, duration, bitrate,
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("Encoder must not run"), CancellationToken.None));
    }

    [Fact]
    public void SaveAsPreservesSourceAndExistingDestinationOnCancellation()
    {
        string root = OwnedTestDirectory.Create("mc-export-copy-");
        try
        {
            string original = Path.Combine(root, "source.mp4");
            string output = Path.Combine(root, "output.mp4");
            File.WriteAllBytes(original, [7, 8]);
            File.WriteAllBytes(output, [9, 10]);
            using var result = VideoExportCalculation.Calculate(0, 1000, 1_000_000,
                (path, _, _, _) => File.WriteAllBytes(path, [1, 2, 3]), CancellationToken.None);
            Assert.Throws<IOException>(() => result.SaveCopy(original, original, CancellationToken.None));
            Assert.Throws<IOException>(() => result.SaveCopy(Path.Combine(root, "wrong.gif"), original, CancellationToken.None));
            Assert.Throws<OperationCanceledException>(() => result.SaveCopy(output, original, new CancellationToken(true)));
            Assert.Equal(new byte[] { 9, 10 }, File.ReadAllBytes(output));
            Assert.Equal(2, Directory.GetFiles(root).Length);
            result.SaveCopy(output, original, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
            Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(original));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void GifUsesOwnedResultAndDoesNotPretendToHaveReductionTarget()
    {
        using var result = VideoExportCalculation.CalculateGif(path => File.WriteAllBytes(path, [71, 73, 70]), CancellationToken.None);
        Assert.Equal(".gif", result.Extension);
        Assert.Equal(3, result.ResultBytes);
        Assert.Equal(0, result.TargetReduction);
    }

    [Fact]
    public void SaveAsCannotReplaceSourceThroughDirectoryJunction()
    {
        string root = OwnedTestDirectory.Create("mc-export-alias-");
        string link = Path.Combine(root, "alias");
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            string source = Path.Combine(folder, "source.mp4");
            File.WriteAllBytes(source, [7, 8, 9]);
            UpdatePathsTests.CreateJunction(link, folder);
            using var result = VideoExportCalculation.Calculate(0, 1000, 1_000_000,
                (path, _, _, _) => File.WriteAllBytes(path, [1, 2, 3, 4]), CancellationToken.None);

            Assert.ThrowsAny<IOException>(() => result.SaveCopy(Path.Combine(link, "source.mp4"), source, CancellationToken.None));
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(source));
            Assert.Single(Directory.GetFiles(folder));
            // The chosen parent may legitimately be a junction; a distinct new export
            // is still supported without putting the immutable source at risk.
            result.SaveCopy(Path.Combine(link, "export.mp4"), source, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(folder, "export.mp4")));
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(source));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // Remove only the owned junction.
            OwnedTestDirectory.Delete(root);
        }
    }
}
