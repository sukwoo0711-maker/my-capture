using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
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
    [InlineData(6_000, "0:06.000")]
    [InlineData(65_250, "1:05.250")]
    [InlineData(0, "0:00.000")]
    public void FormatClock_UsesTheEditorsOneTimeFormat(double milliseconds, string expected) =>
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
            Assert.Equal("예상 크기 약 2.1MB · 0:06.000 · 1280 × 720", dialog.EstimateText);

            dialog.SizePresetIndex = 2;
            Assert.Equal(70, dialog.TargetReductionPercent);
            Assert.Equal("예상 크기 약 622KB · 0:06.000 · 1280 × 720", dialog.EstimateText);

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

    /// <summary>
    /// A failed export reads in State.Danger, as the editor's and the library's failures do; it
    /// used to look like the neutral hint. Neutral lines set no color and keep the dialog's.
    /// </summary>
    [Fact]
    public async Task Dialog_FailureReadsInTheDangerColorUntilTheNextNeutralLine() => await StaThreadTask.RunAsync(() =>
    {
        using var scope = UiText.UseLanguage("ko-KR");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var recording = new RecordingResult(Path.Combine(Path.GetTempPath(), "never-read.mp4"), 6_000, 30, 180, 1280, 720);
        var document = VideoEditDocument.CreateFor(1280, 720, 6_000);
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: false);
        try
        {
            var danger = new SolidColorBrush(Colors.Red);
            dialog.Resources["State.Danger"] = danger;
            var status = (TextBlock)typeof(VideoExportDialog)
                .GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Assert.Same(DependencyProperty.UnsetValue, status.ReadLocalValue(TextBlock.ForegroundProperty));

            // Choosing the recording itself fails before anything is read or written.
            dialog.DestinationChooserForTest = () => recording.OutputPath;
            dialog.ExportAsync().GetAwaiter().GetResult();
            Assert.Equal(UiText.Format("MediaExport_Error", UiText.Get("MediaExport_SourceProtected")), dialog.StatusText);
            Assert.Same(danger, status.Foreground);

            dialog.SizePresetIndex = 1;
            Assert.Equal(UiText.Get("MediaExport_Hint"), dialog.StatusText);
            Assert.Same(DependencyProperty.UnsetValue, status.ReadLocalValue(TextBlock.ForegroundProperty));
        }
        finally { dialog.Close(); }
        return true;
    });

    /// <summary>
    /// The MP4 frame sits in the card the GIF sample uses: the same style, heading, margin and
    /// padding, and a stage of fixed height that says the frame is being made until it arrives.
    /// </summary>
    [Fact]
    public async Task Mp4FrameSitsInTheGifSampleCard() => await StaThreadTask.RunAsync(() =>
    {
        using var scope = UiText.UseLanguage("ko-KR");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var recording = new RecordingResult(Path.Combine(Path.GetTempPath(), "never-read.mp4"), 6_000, 30, 180, 1280, 720);
        var dialog = new VideoExportDialog(recording, VideoEditDocument.CreateFor(1280, 720, 6_000), NullLoggerFactory.Instance, gif: false);
        try
        {
            VideoEditorUxTrimTests.LoadTheme(dialog);
            Image frame = Logical(dialog).OfType<Image>().Single(image => AutomationProperties.GetName(image) == UiText.Get("MediaExport_SourcePreview"));
            Grid stage = Assert.IsType<Grid>(LogicalTreeHelper.GetParent(frame));
            Assert.Equal(VideoExportDialog.SourcePreviewHeight, stage.Height);
            StackPanel body = Assert.IsType<StackPanel>(LogicalTreeHelper.GetParent(stage));
            Border card = Assert.IsType<Border>(LogicalTreeHelper.GetParent(body));
            GifPreviewPanel sample = dialog.SamplePanel;
            Assert.Same(dialog.FindResource("Card.Sunken"), card.Style);
            Assert.Same(sample.Style, card.Style);
            Assert.Equal(sample.Margin, card.Margin);
            Assert.Equal(sample.Padding, card.Padding);

            TextBlock heading = Assert.IsType<TextBlock>(body.Children[0]);
            TextBlock sampleHeading = Logical(sample).OfType<TextBlock>().First();
            Assert.Equal("미리보기 샘플", heading.Text);
            Assert.Equal(sampleHeading.Text, heading.Text);
            Assert.Equal(FontWeights.SemiBold, heading.FontWeight);
            Assert.Same(dialog.FindResource("Text.Primary"), heading.Foreground);
            Assert.Equal(UiText.Get("GifPreview_Heading"), AutomationProperties.GetName(card));

            // Until the frame arrives the stage keeps its height and says what it is waiting for.
            Assert.Equal(Visibility.Visible, card.Visibility);
            Assert.Equal(Visibility.Collapsed, frame.Visibility);
            TextBlock message = Assert.Single(stage.Children.OfType<TextBlock>());
            Assert.Equal(UiText.Get("GifPreview_Preparing"), KoreanWordWrap.Strip(message.Text));
            Assert.Same(dialog.FindResource("Text.Secondary"), message.Foreground);

            // The GIF form has its own card; the MP4 one leaves with the MP4 options.
            Format(dialog, "GIF").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Collapsed, card.Visibility);
            Format(dialog, "MP4").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, card.Visibility);
        }
        finally { dialog.Close(); }
        return true;
    });

    /// <summary>
    /// The window takes its form's height, up to the work area. While the form fits there is no
    /// band under it and nothing scrolls; a frame or a GIF measurement arriving later keeps the
    /// height; switching the format refits it both ways. With MYCAPTURE_TEST_SMALL_DESKTOP=1 the
    /// same checks run on a 1024×768 desktop, where the window stops at it and the form scrolls.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowTakesItsFormsHeightUpToTheWorkArea(bool gif) => await StaThreadTask.RunAsync(() =>
    {
        using var scope = UiText.UseLanguage("ko-KR");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var recording = new RecordingResult(Path.Combine(Path.GetTempPath(), "never-read-export-fit.mp4"), 6_000, 30, 180, 1280, 720);
        var dialog = new VideoExportDialog(recording, VideoEditDocument.CreateFor(1280, 720, 6_000), NullLoggerFactory.Instance, gif)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        var frame = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.SourcePreviewRendererForTest = token => frame.Task.WaitAsync(token);
        dialog.SampleRendererForTest = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The held sample never completes.");
        };
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        using var desktop = new WindowTrackSizeScope(dialog, new Size(0, 0));
        try
        {
            VideoEditorUxTrimTests.LoadTheme(dialog);
            FluidMotion.SetWindowEntrance(dialog, false);
            dialog.Show();
            Settle(dialog);
            Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
            double opened = AssertFitsItsForm(dialog, gif ? "GIF opened" : "MP4 opened");

            if (gif)
            {
                // The advice line has no measurement yet; its row keeps its place for it.
                var advice = (Grid)typeof(VideoExportDialog).GetField("_adviceRow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                Assert.Equal(Visibility.Hidden, advice.Visibility);
                Assert.True(advice.ActualHeight > 0);
            }
            else
            {
                BitmapSource bitmap = BitmapSource.Create(16, 9, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 9 * 4], 16 * 4);
                bitmap.Freeze();
                frame.SetResult(bitmap);
                PumpUntil(() => dialog.SourcePreviewCompletion.IsCompleted, "the MP4 frame");
                Settle(dialog);
                Image preview = Logical(dialog).OfType<Image>().Single(image => AutomationProperties.GetName(image) == UiText.Get("MediaExport_SourcePreview"));
                Assert.True(preview.IsVisible);
                Assert.Equal(opened, dialog.ActualHeight, 1);
            }

            Format(dialog, gif ? "MP4" : "GIF").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(dialog);
            double switched = AssertFitsItsForm(dialog, gif ? "switched to MP4" : "switched to GIF");
            // The GIF form carries the taller sample card and four options: about 200 DIP more.
            double mp4 = gif ? switched : opened, gifHeight = gif ? opened : switched;
            if (mp4 < WindowLimit(dialog) - 1) Assert.True(gifHeight > mp4 + 100, $"GIF {gifHeight:0.#} vs MP4 {mp4:0.#}");

            Format(dialog, gif ? "GIF" : "MP4").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(dialog);
            Assert.Equal(opened, AssertFitsItsForm(dialog, "switched back"), 1);
        }
        finally
        {
            dialog.Close();
            PumpUntil(() => closed, "the dialog to close");
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
        return true;
    });

    [Theory]
    [InlineData(100, 600, 0, 1040, 100)]
    [InlineData(300, 800, 0, 1040, 240)]
    [InlineData(100, 1200, 0, 1040, 0)]
    [InlineData(50, 700, 40, 740, 40)]
    [InlineData(-10000, 800, 0, 1040, -10000)]
    public void GrowingWindowMovesUpOnlyAsFarAsItMust(double top, double height, double workTop, double workBottom, double expected) =>
        Assert.Equal(expected, VideoExportDialog.TopWithinWorkArea(top, height, workTop, workBottom));

    /// <summary>
    /// The window is no taller than it may be, its footer is inside its client area, and either
    /// the form fits exactly (no band under it, no scrolling) or the window is as tall as it may
    /// be and the form scrolls. Returns the window's height.
    /// </summary>
    private static double AssertFitsItsForm(VideoExportDialog dialog, string when)
    {
        ScrollViewer form = Logical(dialog).OfType<ScrollViewer>().First();
        var settings = (FrameworkElement)form.Content;
        double limit = WindowLimit(dialog);
        double height = dialog.ActualHeight;
        string state = $"{when}: window {height:0.#} of {limit:0.#}, viewport {form.ViewportHeight:0.#}, form {settings.ActualHeight:0.#}";
        Assert.True(height <= limit + 0.5, state);
        if (form.ScrollableHeight <= 0.5)
            Assert.True(form.ViewportHeight - settings.ActualHeight <= 0.5, state + ": a band under the form");
        else
            Assert.True(height >= limit - 1, state + ": the form scrolls in a window that could grow");

        Assert.True(GetClientRect(new WindowInteropHelper(dialog).Handle, out NativeRect client));
        double clientHeight = (client.Bottom - client.Top) / VisualTreeHelper.GetDpi(dialog).DpiScaleY;
        foreach (Button button in Logical(dialog).OfType<Button>()
            .Where(button => AutomationProperties.GetName(button) is "내보내기" or "닫기"))
        {
            double bottom = button.TranslatePoint(new Point(0, button.ActualHeight), dialog).Y;
            Assert.True(button.IsVisible && bottom <= clientHeight + 0.5, $"{state}: {AutomationProperties.GetName(button)} ends at {bottom:0.#} of {clientHeight:0.#}");
        }
        return height;
    }

    /// <summary>The tallest the window may be: its MaxHeight, or the simulated small desktop.</summary>
    private static double WindowLimit(Window window)
    {
        bool small = Environment.GetEnvironmentVariable(WindowTrackSizeScope.SmallDesktopEnvironmentVariable) == "1";
        return small ? Math.Min(window.MaxHeight, WindowTrackSizeScope.SmallDesktopMaximum.Height / VisualTreeHelper.GetDpi(window).DpiScaleY) : window.MaxHeight;
    }

    private static ToggleButton Format(DependencyObject dialog, string name) =>
        Logical(dialog).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == name);

    private static void Settle(Window window)
    {
        for (int pass = 0; pass < 2; pass++)
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    }

    private static void PumpUntil(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Assert.True(done(), $"Timed out waiting for {what}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

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
