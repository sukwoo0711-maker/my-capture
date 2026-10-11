using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoExportDialogTests
{
    [Theory]
    [InlineData(12_345_678, "ko-KR", "12.3MB")]
    [InlineData(1_000_000, "ko-KR", "1.0MB")]
    [InlineData(999_500, "ko-KR", "1,000KB")]
    [InlineData(10, "ko-KR", "1KB")]
    [InlineData(12_345_678, "en-US", "12.3 MB")]
    [InlineData(4_200, "en-US", "4 KB")]
    public void FormatSize_UsesDecimalUnitsWithOneDecimalForMegabytes(long bytes, string language, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(expected, VideoExportDialog.FormatSize(bytes));
    }

    [Fact]
    public void FormatRange_CollapsesExactEstimatesAndJoinsRanges()
    {
        using var scope = UiText.UseLanguage("ko-KR");
        Assert.Equal("2.5MB", VideoExportDialog.FormatRange(GifSizeEstimate.Exact(2_500_000)));
        Assert.Equal("800KB~1.2MB", VideoExportDialog.FormatRange(new GifSizeEstimate(800_000, 1_000_000, 1_200_000, false)));
    }

    [Theory]
    [InlineData(6_000, "00:06.0")]
    [InlineData(65_250, "01:05.2")]
    [InlineData(0, "00:00.0")]
    public void FormatClock_ShowsMinutesSecondsAndTenths(double milliseconds, string expected) =>
        Assert.Equal(expected, VideoExportDialog.FormatClock(milliseconds));

    [Fact]
    public void EstimateMp4Bytes_IsTheStandardBitrateOverTheKeptRangeLessTheReduction()
    {
        // 1280×720 at 30 fps derives 2,764,800 bit/s; 6 s at the standard bitrate is 2,073,600 bytes.
        Assert.Equal(2_073_600, VideoExportDialog.EstimateMp4Bytes(1280, 720, 30, 6_000, 0));
        Assert.Equal(1_244_160, VideoExportDialog.EstimateMp4Bytes(1280, 720, 30, 6_000, 40));
        Assert.Equal(622_080, VideoExportDialog.EstimateMp4Bytes(1280, 720, 30, 6_000, 70));
        Assert.Equal(0, VideoExportDialog.EstimateMp4Bytes(1280, 720, 30, 0, 0));
    }

    [Theory]
    [InlineData("ko-KR", @"C:\Videos\MyCapture_20261011_101500.mp4", ".gif", "MyCapture_20261011_101500_편집.gif")]
    [InlineData("en-US", @"C:\Videos\clip.mp4", ".mp4", "clip_edited.mp4")]
    public void DefaultExportName_AppendsTheLocalizedEditedSuffix(string language, string source, string extension, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(expected, VideoExportDialog.DefaultExportName(source, extension));
    }

    [Fact]
    public async Task Dialog_SizePresetsDriveTheSliderAndTheSummaryLineNeedsNoEncode() => await StaThreadTask.RunAsync(() =>
    {
        using var scope = UiText.UseLanguage("ko-KR");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var recording = new RecordingResult(Path.Combine(Path.GetTempPath(), "never-read.mp4"), 6_000, 30, 180, 1280, 720);
        var document = VideoEditDocument.CreateFor(1280, 720, 6_000);
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: false);
        try
        {
            Assert.False(dialog.IsGif);
            Assert.Equal(0, dialog.SizePresetIndex);
            Assert.Equal(0, dialog.TargetReductionPercent);
            Assert.True(dialog.CanExport);
            Assert.False(dialog.IsProgressVisible);
            Assert.Equal(UiText.Get("MediaExport_Hint"), dialog.StatusText);
            Assert.Equal("예상 크기 약 2.1MB · 00:06.0 · 1280×720", dialog.EstimateText);

            dialog.SizePresetIndex = 2;
            Assert.Equal(70, dialog.TargetReductionPercent);
            Assert.Equal("예상 크기 약 622KB · 00:06.0 · 1280×720", dialog.EstimateText);

            // A custom slider value belongs to no preset; a preset value re-selects its button.
            dialog.TargetReductionPercent = 55;
            Assert.Equal(-1, dialog.SizePresetIndex);
            Assert.Contains("933KB", dialog.EstimateText, StringComparison.Ordinal);
            dialog.TargetReductionPercent = 40;
            Assert.Equal(1, dialog.SizePresetIndex);

            // Headless, no template is applied: walk the logical tree the dialog built in code.
            ToggleButton[] presets = Logical(dialog).OfType<ToggleButton>()
                .Where(toggle => AutomationProperties.GetName(toggle) is "원본 화질" or "작게" or "더 작게").ToArray();
            Assert.Equal(3, presets.Length);
            Assert.Equal(new[] { false, true, false }, presets.Select(toggle => toggle.IsChecked == true).ToArray());
            Assert.All(presets, toggle => Assert.False(string.IsNullOrWhiteSpace(toggle.ToolTip as string)));

            // Format toggles: GIF hides the MP4 size row and shows the GIF options.
            ToggleButton gif = Logical(dialog).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == "GIF");
            ToggleButton mp4 = Logical(dialog).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == "MP4");
            Assert.True(mp4.IsChecked == true && gif.IsChecked != true);
            gif.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(dialog.IsGif);
            Assert.True(gif.IsChecked == true && mp4.IsChecked != true);
            Assert.StartsWith("예상 크기 계산 중", dialog.EstimateText, StringComparison.Ordinal);
            // Clicking the checked toggle keeps the choice instead of clearing it.
            gif.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(dialog.IsGif && gif.IsChecked == true);

            // Footer order: 내보내기 (primary) before 닫기; the progress row stays hidden while idle.
            Button[] footer = Logical(dialog).OfType<Button>()
                .Where(button => AutomationProperties.GetName(button) is "내보내기" or "닫기").ToArray();
            Assert.Equal(new[] { "내보내기", "닫기" }, footer.Select(AutomationProperties.GetName).ToArray());
            ProgressBar bar = Logical(dialog).OfType<ProgressBar>().Single();
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)bar.Parent).Visibility);
            Assert.False(dialog.IsProgressVisible);

            // Declining the save dialog exports nothing and keeps the form usable.
            int chooserCalls = 0;
            dialog.DestinationChooserForTest = () => { chooserCalls++; return null; };
            dialog.ExportAsync().GetAwaiter().GetResult();
            Assert.Equal(1, chooserCalls);
            Assert.True(dialog.CanExport);
            Assert.False(dialog.IsProgressVisible);
        }
        finally { dialog.Close(); }
        return true;
    });

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Logical(element)) yield return descendant;
        }
    }
}
