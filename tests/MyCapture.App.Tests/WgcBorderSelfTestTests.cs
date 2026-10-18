using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class WgcBorderSelfTestTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040)] // Bottom taskbar.
    [InlineData(0, 40, 1920, 1040)] // Top taskbar.
    [InlineData(40, 0, 1880, 1080)] // Left taskbar.
    [InlineData(0, 0, 1880, 1080)] // Right taskbar.
    public void FixtureRegion_ExcludesAppbarsOnEveryEdge(int x, int y, int width, int height)
    {
        var work = new RectD(x, y, width, height);
        var monitor = new MonitorInfo("primary", new RectD(0, 0, 1920, 1080), work, 96, true);
        Assert.Equal(work, WgcBorderSelfTest.ComputeFixtureRegion(monitor));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void FixtureRegion_UsesSelectedMonitorPhysicalPixelsInMixedDpiLayout(int dpi)
    {
        MonitorInfo[] layout =
        [
            new("primary", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), 96, true),
            new("left-above", new RectD(-2560, -720, 2560, 1440),
                new RectD(-2500, -680, 2500, 1400), (uint)dpi, false),
        ];
        Assert.Equal(new RectD(-2500, -680, 2500, 1400), WgcBorderSelfTest.ComputeFixtureRegion(layout[1]));
    }

    [Fact]
    public void FixtureRegion_RoundsInwardAndAlignsEncoderDimensionsInsideWorkArea()
    {
        var monitor = new MonitorInfo("fractional", new RectD(-1600, -900, 1600, 900),
            new RectD(-1599.5, -899.5, 1579, 859), 144, false);
        Assert.Equal(new RectD(-1599, -899, 1578, 858), WgcBorderSelfTest.ComputeFixtureRegion(monitor));
    }

    [Fact]
    public void FixtureRegion_RejectsWorkAreaWithoutEnoughPixelsForEdgeInspection()
    {
        var monitor = new MonitorInfo("tiny", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 20, 20), 96, true);
        Assert.Throws<InvalidOperationException>(() => WgcBorderSelfTest.ComputeFixtureRegion(monitor));
    }

    [Fact]
    public void YellowEdgeScan_CoversEveryEdgeAndIgnoresTheInteriorAndRowPadding()
    {
        const int Width = 40, Height = 30, Stride = Width * 4 + 16;
        var pixels = new byte[Stride * Height];
        void Yellow(int x, int y)
        {
            int offset = y * Stride + x * 4;
            pixels[offset + 1] = 200;
            pixels[offset + 2] = 255;
        }
        Yellow(0, 0);
        Yellow(Width - 1, 0);
        Yellow(0, Height - 1);
        Yellow(Width - 1, Height - 1);
        Yellow(Width / 2, 9);
        Yellow(Width / 2, Height - 10);
        Yellow(9, Height / 2);
        Yellow(Width - 10, Height / 2);
        Yellow(Width / 2, Height / 2);
        Yellow(Width, 0);
        Assert.Equal(8, WgcBorderSelfTest.CountYellowEdgePixels(pixels, Width, Height, Stride));
    }

    [Fact]
    public void YellowEdgeScan_RejectsIncompleteFrames()
    {
        Assert.Throws<ArgumentException>(() => WgcBorderSelfTest.CountYellowEdgePixels(new byte[4], 20, 20, 80));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\0")]
    [InlineData(@"C:\")]
    public void MissingOrInvalidOutput_ReturnsFailureWithoutInvokingDiagnostic(string output)
    {
        bool called = false;
        int code = App.ExecuteSelfTest(output, WgcBorderSelfTest.ReportName, _ => { called = true; return 0; });
        Assert.Equal(2, code);
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JunctionOutput_DoesNotWriteAFallbackReport(bool missingChild)
    {
        string root = NewOutputDirectory();
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(root, "link");
        UpdatePathsTests.CreateJunction(link, target);
        bool called = false;
        string output = missingChild ? Path.Combine(link, "must-not-exist") : link;
        int code = App.ExecuteSelfTest(output, WgcBorderSelfTest.ReportName, _ => { called = true; return 0; });
        Assert.Equal(2, code);
        Assert.False(called);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void SharedCatch_RevalidatesTheReportArtifactAndPreservesItsTarget()
    {
        string root = NewOutputDirectory();
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        string keep = Path.Combine(target, "keep.txt");
        File.WriteAllText(keep, "unchanged");
        UpdatePathsTests.CreateJunction(Path.Combine(root, WgcBorderSelfTest.ReportName), target);

        Assert.Equal(2, App.ExecuteSelfTest(root, WgcBorderSelfTest.ReportName,
            _ => throw new IOException("diagnostic failed")));
        Assert.Equal("unchanged", File.ReadAllText(keep));
        Assert.Single(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void SharedCatch_WritesFailureOnlyWithinTheValidatedRoot()
    {
        string root = NewOutputDirectory();
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, root);
        Assert.Equal(2, App.ExecuteSelfTest(relative, WgcBorderSelfTest.ReportName, validated =>
        {
            Assert.Equal(root, validated);
            throw new InvalidOperationException("expected diagnostic failure");
        }));
        string report = File.ReadAllText(DiagnosticOutputPaths.Child(root, WgcBorderSelfTest.ReportName));
        Assert.Contains("RESULT: FAIL", report, StringComparison.Ordinal);
        Assert.Contains("expected diagnostic failure", report, StringComparison.Ordinal);

        Assert.Equal(2, App.ExecuteSelfTest(root, "../escaped.txt", _ => throw new IOException("failure")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escaped.txt")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void SharedRunner_PreservesDiagnosticExitCodes(int expected)
    {
        string root = NewOutputDirectory();
        Assert.Equal(expected, App.ExecuteSelfTest(root, WgcBorderSelfTest.ReportName, _ => expected));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void Timeout_ClosesFixtureAndTimerAndReportsFailureWithoutWaitingForWorker() => StaTestHost.Run(() =>
    {
        string root = NewOutputDirectory();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Width = 100, Height = 100, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        bool closed = false, completed = false;
        CancellationToken token = default;
        window.Closed += (_, _) => closed = true;
        var elapsed = Stopwatch.StartNew();
        int code = WgcBorderSelfTest.RunDiagnostic(root, (_, cancellation) =>
        {
            token = cancellation;
            window.Show();
            timer.Start();
            return pending.Task;
        }, () =>
        {
            timer.Stop();
            window.Close();
        }, (_, _) => completed = true, TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, code);
        Assert.True(closed);
        Assert.False(timer.IsEnabled);
        Assert.False(completed);
        Assert.False(pending.Task.IsCompleted);
        Assert.True(token.IsCancellationRequested);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
        string report = File.ReadAllText(DiagnosticOutputPaths.Child(root, WgcBorderSelfTest.ReportName));
        Assert.Contains("RESULT: FAIL", report, StringComparison.Ordinal);
        Assert.Contains("timed out", report, StringComparison.Ordinal);
        // Even a worker fault arriving after timeout cannot replace the report or run completion.
        pending.SetException(new InvalidOperationException("late native failure"));
        Assert.False(completed);
        Assert.Equal(report, File.ReadAllText(DiagnosticOutputPaths.Child(root, WgcBorderSelfTest.ReportName)));
    });

    [Theory]
    [InlineData(24)]
    [InlineData(36)]
    public void FrozenFixtureFrames_FailEvenWhenFortyFramesWereCopied(int tolerance)
    {
        var check = new WgcBorderSelfTest.FixtureFrameValidator(tolerance);
        byte[] frozen = FixtureFrame(white: true);
        for (int index = 0; index < 40; index++) check.Observe(frozen, 40, 30, 160);
        Assert.Equal(40, check.Frames);
        Assert.Equal(0, check.UnexpectedEdgePixels);
        Assert.Equal(0, check.InvalidMarkerFrames);
        Assert.Equal(0, check.MarkerTransitions);
        Assert.False(check.IsValid);
    }

    [Fact]
    public void BlackDecodedFrames_FailWithoutAnyYellowPixels()
    {
        var decoded = new WgcBorderSelfTest.FixtureFrameValidator(compressionTolerance: 36);
        var black = new byte[40 * 30 * 4];
        decoded.Observe(black, 40, 30, 160);
        decoded.Observe(black, 40, 30, 160);
        Assert.Equal(0, decoded.YellowPixels);
        Assert.True(decoded.UnexpectedEdgePixels > 0);
        Assert.False(decoded.IsValid);
    }

    [Fact]
    public void DecodedFrames_WithChangingMarkerAndCompressionNoise_Pass()
    {
        var decoded = new WgcBorderSelfTest.FixtureFrameValidator(compressionTolerance: 36);
        decoded.Observe(FixtureFrame(white: true, noise: 20), 40, 30, 160);
        decoded.Observe(FixtureFrame(white: false, noise: 20), 40, 30, 160);
        Assert.Equal(1, decoded.MarkerTransitions);
        Assert.True(decoded.IsValid);
    }

    [Fact]
    public void FixtureWithoutACentralMarker_Fails()
    {
        var check = new WgcBorderSelfTest.FixtureFrameValidator(compressionTolerance: 36);
        byte[] pixels = FixtureFrame(white: true);
        for (int y = 13; y <= 17; y++)
        for (int x = 18; x <= 22; x++)
        {
            int offset = y * 160 + x * 4;
            pixels[offset] = 166;
            pixels[offset + 1] = 90;
            pixels[offset + 2] = 18;
        }
        check.Observe(pixels, 40, 30, 160);
        Assert.Equal(1, check.InvalidMarkerFrames);
        Assert.False(check.IsValid);
    }

    private static byte[] FixtureFrame(bool white, int noise = 0)
    {
        var pixels = new byte[40 * 30 * 4];
        for (int y = 0; y < 30; y++)
        for (int x = 0; x < 40; x++)
        {
            int offset = y * 160 + x * 4;
            pixels[offset] = (byte)(166 + noise);
            pixels[offset + 1] = (byte)(90 + noise);
            pixels[offset + 2] = (byte)(18 + noise);
            pixels[offset + 3] = 255;
            if (x is >= 18 and <= 22 && y is >= 13 and <= 17)
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)(white ? 255 - noise : noise);
        }
        return pixels;
    }

    // Preserve diagnostic fixtures for inspection; the caller's isolated TEMP root owns them.
    private static string NewOutputDirectory()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ?? Path.GetTempPath(),
            "wgc-border-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
