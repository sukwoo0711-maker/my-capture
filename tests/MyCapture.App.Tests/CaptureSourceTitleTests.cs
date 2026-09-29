using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureSourceTitleTests
{
    [Fact]
    public void RegionTitle_PrefersFrontmostWindowAtSelectionCenter()
    {
        WindowCandidate[] windows =
        [
            new(new IntPtr(1), new RectD(-100, 0, 200, 100), "report.xlsx - Excel"),
            new(new IntPtr(2), new RectD(-1000, -100, 2000, 1000), "background - Visual Studio Code"),
        ];
        Assert.Equal("report.xlsx - Excel", WindowCandidateService.ResolveSourceTitle(windows,
            new RectD(-50, 20, 100, 50)));
    }

    [Fact]
    public void RegionTitle_CenterOverDesktopFallsBackToLargestIntersection_AndSkipsEmptyTitles()
    {
        WindowCandidate[] windows =
        [
            new(new IntPtr(1), new RectD(0, 0, 100, 100)),
            new(new IntPtr(2), new RectD(0, 0, 40, 100), "larger overlap"),
            new(new IntPtr(3), new RectD(90, 0, 10, 100), "smaller overlap"),
        ];
        Assert.Equal("larger overlap", WindowCandidateService.ResolveSourceTitle(windows, new RectD(0, 0, 100, 100)));
        Assert.Equal(string.Empty, WindowCandidateService.ResolveSourceTitle(windows, new RectD(200, 200, 50, 50)));
        Assert.Equal(string.Empty, WindowCandidateService.ResolveSourceTitle(windows, RectD.Empty));
    }

    [Fact]
    public void ActualWin32Title_IsFrozenBeforeOverlay_AndSurvivesSourceTitleChange() => StaTestHost.Run(() =>
    {
        var source = new Window { Title = "매출.xlsx - Excel", ShowInTaskbar = false };
        CaptureOverlayWindow? overlay = null;
        try
        {
            IntPtr handle = new WindowInteropHelper(source).EnsureHandle();
            string snapshot = new WindowTitleService().ReadTitle(handle);
            Assert.Equal("매출.xlsx - Excel", snapshot);
            var screenBounds = new RectD(-200, 100, 64, 48);
            BitmapSource bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32,
                null, new byte[64 * 48 * 4], 64 * 4);
            bitmap.Freeze();
            overlay = new CaptureOverlayWindow(new FrozenFrame(bitmap, screenBounds, null, 0), false, false)
            {
                SourceWindows = [new WindowCandidate(handle, screenBounds, snapshot)],
            };
            source.Title = "다른 문서.xlsx - Excel";
            Assert.Equal(source.Title, new WindowTitleService().ReadTitle(handle));
            CaptureSelectionCompletedEventArgs? selected = null;
            overlay.SelectionCompleted += (_, selection) => selected = selection;
            overlay.CompleteSelection(new RectD(4, 4, 32, 24));
            Assert.NotNull(selected);
            Assert.Equal(snapshot, selected.SourceTitle);
            Assert.Equal(32, selected.SelectedBitmap.PixelWidth);
        }
        finally
        {
            overlay?.Close();
            source.Close();
        }
    });
}
