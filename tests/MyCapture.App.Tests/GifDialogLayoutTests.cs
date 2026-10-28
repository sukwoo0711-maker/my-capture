using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifDialogLayoutTests
{
    [Theory]
    [InlineData("ko-KR", "GIF 크기", "프레임 속도", "자동 · 10fps", "자동 · 5fps")]
    [InlineData("en-US", "GIF size", "Frame rate", "Auto · 10 fps", "Auto · 5 fps")]
    public Task Options_UseTwoByTwoGridAndKeepAutomaticRateSeparateFromSize(
        string language, string sizeLabel, string rateLabel, string automaticTen, string automaticFive) => RunSta(() =>
    {
        using var scope = UiText.UseLanguage(language);
        VideoExportDialog dialog = NewDialog(60);
        try
        {
            ComboBox size = Combo(dialog, sizeLabel);
            ComboBox rate = Combo(dialog, rateLabel);
            ComboBox speed = Combo(dialog, UiText.Get("MediaExport_Speed"));
            ComboBox target = Combo(dialog, UiText.Get("GifPreview_Target"));
            StackPanel[] cells = [Cell(size), Cell(rate), Cell(speed), Cell(target)];
            Grid grid = Assert.IsType<Grid>(LogicalTreeHelper.GetParent(cells[0]));
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            Assert.Equal(2, grid.RowDefinitions.Count);
            Assert.Equal(4, grid.Children.Count);
            Assert.All(cells, cell => Assert.Same(grid, LogicalTreeHelper.GetParent(cell)));
            Assert.Equal(new[] { (0, 0), (0, 1), (1, 0), (1, 1) },
                cells.Select(cell => (Grid.GetRow(cell), Grid.GetColumn(cell))).ToArray());
            Assert.All(new[] { size, rate, speed, target }, combo => Assert.Equal(36, combo.MinHeight));
            Assert.Equal(sizeLabel, Assert.IsType<TextBlock>(cells[0].Children[0]).Text);
            Assert.Equal(rateLabel, Assert.IsType<TextBlock>(cells[1].Children[0]).Text);
            Assert.Equal(language == "ko-KR" ? "표준 · 960px" : "Standard · 960 px", size.Items[0].ToString());
            Assert.All(size.Items.Cast<object>(), item => Assert.DoesNotContain("fps", item.ToString()!, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(language == "ko-KR" ? "640px" : "640 px", size.Items[2].ToString(), StringComparison.Ordinal);
            Assert.Contains(language == "ko-KR" ? "480px" : "480 px", size.Items[3].ToString(), StringComparison.Ordinal);
            string suffix = language == "ko-KR" ? "fps" : " fps";
            Assert.Equal(new[] { 5, 10, 15, 20, 25, 30, 50 }.Select(fps => fps + suffix),
                rate.Items.Cast<object>().Skip(1).Select(item => item.ToString()));
            string[] targetLabels = language == "ko-KR"
                ? ["5MB · Notion 무료", "10MB · GitHub 첨부", "15MB · X(350프레임 이하)", "20MB · Discord 무료", "제한 없음"]
                : ["5 MB · Notion free", "10 MB · GitHub attachments", "15 MB · X (up to 350 frames)", "20 MB · Discord free", "No limit"];
            Assert.Equal(targetLabels, target.Items.Cast<object>().Select(item => item.ToString()));
            for (int index = 0; index < targetLabels.Length; index++)
            {
                dialog.ShareTargetIndex = index;
                Assert.Equal(targetLabels[index], target.ToolTip);
                Assert.Equal(targetLabels[index], AutomationProperties.GetHelpText(target));
            }
            dialog.ShareTargetIndex = 2;
            Assert.Contains("X", Assert.IsType<string>(target.ToolTip), StringComparison.Ordinal);
            Assert.Contains("350", Assert.IsType<string>(target.ToolTip), StringComparison.Ordinal);
            Assert.Equal(350, GifSharePolicy.Targets[dialog.ShareTargetIndex].MaximumFrames);
            dialog.ShareTargetIndex = GifSharePolicy.DefaultTargetIndex;

            Assert.Equal(automaticTen, rate.Items[0].ToString());
            Assert.Equal(0, dialog.SelectedFrameRate);
            Assert.Equal(10, dialog.EffectiveFrameRate);
            dialog.QualityIndex = 3;
            Assert.Equal(automaticFive, rate.Items[0].ToString());
            Assert.Equal(0, dialog.SelectedFrameRate);
            Assert.Equal(5, dialog.EffectiveFrameRate);

            dialog.SelectedFrameRate = 30;
            object explicitSelection = rate.SelectedItem;
            dialog.QualityIndex = 0;
            Assert.Equal(automaticTen, rate.Items[0].ToString());
            Assert.Same(explicitSelection, rate.SelectedItem);
            Assert.Equal(30, dialog.SelectedFrameRate);
            Assert.Equal(30, dialog.EffectiveFrameRate);
            dialog.QualityIndex = 3;
            Assert.Equal(automaticFive, rate.Items[0].ToString());
            Assert.Same(explicitSelection, rate.SelectedItem);
            Assert.Equal(30, dialog.EffectiveFrameRate);
            dialog.SelectedFrameRate = 0;
            Assert.Equal(5, dialog.EffectiveFrameRate);
            Assert.False(dialog.IsLoaded);
            Assert.True(dialog.SampleCompletion.IsCompleted);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", 15, 1, 30, 15, "원본이 15fps라 15fps로 만듭니다.")]
    [InlineData("en-US", 15, 1, 30, 15, "Made at 15 fps because the source is 15 fps.")]
    [InlineData("ko-KR", 60, 2, 50, 25, "재생 속도 때문에 25fps로 만듭니다.")]
    [InlineData("en-US", 60, 2, 50, 25, "Made at 25 fps because of the playback speed.")]
    [InlineData("ko-KR", 15, 4, 50, 12.5, "재생 속도 때문에 12.5fps로 만듭니다.")]
    [InlineData("en-US", 15, 4, 50, 12.5, "Made at 12.5 fps because of the playback speed.")]
    public Task CapNotice_NamesTheLimitingReasonAndDisappearsWhenUncapped(
        string language, int sourceRate, double playbackSpeed, int selectedRate, double expectedRate, string expectedNotice) => RunSta(() =>
    {
        using var scope = UiText.UseLanguage(language);
        VideoExportDialog dialog = NewDialog(sourceRate);
        try
        {
            dialog.SelectedFrameRate = selectedRate;
            ComboBox speed = Combo(dialog, UiText.Get("MediaExport_Speed"));
            speed.SelectedIndex = GifSharePolicy.Speeds.ToList().IndexOf(playbackSpeed);
            Assert.Equal(expectedRate, dialog.EffectiveFrameRate);
            TextBlock notice = Logical(dialog).OfType<TextBlock>().Single(text => text.Text == expectedNotice);
            Assert.Equal(Visibility.Visible, notice.Visibility);
            Assert.DoesNotContain('\n', notice.Text);
            if (language == "ko-KR")
            {
                Assert.DoesNotContain(" fps", notice.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(" fps", dialog.EstimateText, StringComparison.Ordinal);
            }
            else Assert.Contains(" fps", dialog.EstimateText, StringComparison.Ordinal);

            dialog.SelectedFrameRate = 5;
            Assert.Equal(5, dialog.EffectiveFrameRate);
            Assert.Equal(string.Empty, notice.Text);
            Assert.Equal(Visibility.Collapsed, notice.Visibility);
        }
        finally { dialog.Close(); }
    });

    internal static ComboBox Combo(DependencyObject root, string name) =>
        Logical(root).OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == name);

    private static StackPanel Cell(ComboBox combo) => Assert.IsType<StackPanel>(LogicalTreeHelper.GetParent(combo));

    private static VideoExportDialog NewDialog(int sourceRate)
    {
        var recording = new RecordingResult(Path.Combine(Path.GetTempPath(), "never-opened-gif-layout.mp4"),
            1_000, sourceRate, sourceRate, 960, 540);
        return new VideoExportDialog(recording, VideoEditDocument.CreateFor(960, 540, 1_000), NullLoggerFactory.Instance, gif: true);
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Logical(element)) yield return descendant;
        }
    }

    private static Task RunSta(Action action) => StaThreadTask.RunAsync(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        action();
        return true;
    });
}
