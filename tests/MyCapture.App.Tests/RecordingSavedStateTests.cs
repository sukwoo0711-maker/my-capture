using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// After a recording stops, the bar says it is saving, then Saved with a check until the editor is
/// on screen, and leaves with the shared exit; no recorder, input or live capture runs here.
/// </summary>
public sealed class RecordingSavedStateTests
{
    [Theory]
    [InlineData("ko-KR", "저장됨")]
    [InlineData("en-US", "Saved")]
    public void SavedReplacesTheProgressWithACheckAndLetsTheBarClose(string culture, string saved) => Run(culture, motion: false, window =>
    {
        int closed = 0;
        window.Closed += (_, _) => closed++;
        window.ShowCompletionStatus(UiText.Get("Text_528544494302"));
        ProgressBar progress = Field<ProgressBar>(window, "_completionProgress");
        Assert.Equal(Visibility.Visible, progress.Visibility);
        // Saving cannot be dismissed.
        window.Close();
        Assert.Equal(0, closed);

        window.ShowSaved();

        Assert.True(window.IsSaved);
        TextBlock status = Field<TextBlock>(window, "_statusText");
        SymbolIcon indicator = Field<SymbolIcon>(window, "_recordingIndicator");
        Assert.Equal(saved, status.Text);
        Assert.Equal(saved, System.Windows.Automation.AutomationProperties.GetName(status));
        Assert.Equal(Visibility.Visible, status.Visibility);
        Assert.Equal(Visibility.Collapsed, progress.Visibility);
        Assert.Equal(Visibility.Collapsed, window.PrimaryButtonForTest.Visibility);
        Assert.Equal(Visibility.Visible, indicator.Visibility);
        Assert.Same(typeof(RecordingControlWindow).GetField("CheckGlyph", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null), indicator.Data);
        Assert.Equal(Success(), Assert.IsType<SolidColorBrush>(status.Foreground).Color);
        Assert.Equal(Success(), Assert.IsType<SolidColorBrush>(indicator.Stroke).Color);
        Assert.Null(indicator.Fill);

        // Without motion (and for a hidden bar) it leaves at once.
        window.LeaveAfterSaved();
        Assert.Equal(1, closed);
    });

    [Fact]
    public void SavedPreviewIsTheStoppedBarWithItsRecordedTimeAndStays() => Run("ko-KR", motion: false, window =>
    {
        int closed = 0;
        window.Closed += (_, _) => closed++;
        window.SavedHoldLimit = TimeSpan.FromMilliseconds(30);

        window.PreviewSavedForTest(TimeSpan.FromSeconds(3));

        // The full bar of a finished session, not the compact recording pill: Saved is spelled out.
        Assert.True(window.IsSaved);
        TextBlock status = Field<TextBlock>(window, "_statusText");
        SymbolIcon indicator = Field<SymbolIcon>(window, "_recordingIndicator");
        Assert.Equal("저장됨", status.Text);
        Assert.Equal(Visibility.Visible, status.Visibility);
        Assert.Equal(Visibility.Visible, indicator.Visibility);
        Assert.Same(typeof(RecordingControlWindow).GetField("CheckGlyph", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null), indicator.Data);
        Assert.Equal("00:03", Field<TextBlock>(window, "_timerText").Text);
        Assert.Equal(Visibility.Collapsed, window.PrimaryButtonForTest.Visibility);
        Assert.Equal(Visibility.Collapsed, Field<ProgressBar>(window, "_completionProgress").Visibility);
        Assert.Equal(Visibility.Visible, window.PauseButtonForTest.Visibility);
        Assert.False(window.PauseButtonForTest.IsEnabled);
        Assert.Equal(Visibility.Collapsed, window.CancelButtonForTest.Visibility);
        Assert.False(window.MoreButtonForTest.IsEnabled);

        // A preview stays until its owner closes it.
        Pump(window.Dispatcher, 200);
        Assert.Equal(0, closed);
    });

    [Fact]
    public void SavedLeavesOnItsOwnWhenNoEditorComes() => Run("ko-KR", motion: false, window =>
    {
        int closed = 0;
        window.Closed += (_, _) => closed++;
        window.SavedHoldLimit = TimeSpan.FromMilliseconds(30);
        window.ShowCompletionStatus(UiText.Get("Text_528544494302"));
        window.ShowSaved();

        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (closed == 0 && DateTime.UtcNow < deadline) Pump(window.Dispatcher, 20);

        Assert.Equal(1, closed);
    });

    [Fact]
    public void ShownSavedBarLeavesWithTheSharedFadeDown() => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(true);
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        RectD work = MonitorEnumerator.GetFromPoint(new PointD(100, 100)).WorkArea;
        var window = new RecordingControlWindow(new RectD(work.Left + 40, work.Top + 40, 160, 100).ToPixelBounds(),
            new RecordingSettings(), () => throw new InvalidOperationException("No recorder in this presentation test."),
            () => "unused.mp4", NullLogger.Instance) { ShowActivated = false };
        int closed = 0;
        window.Closed += (_, _) => closed++;
        try
        {
            window.Show();
            ControlStateMotionTests.Drain(window);
            window.ShowCompletionStatus(UiText.Get("Text_528544494302"));
            window.ShowSaved();

            window.LeaveAfterSaved();

            Assert.True(FluidMotion.IsExiting(window));
            Assert.False(window.IsHitTestVisible);
            Assert.IsType<TranslateTransform>(((FrameworkElement)window.Content).RenderTransform);
            Assert.Equal(0, closed);
            FluidMotion.CompleteExit(window);
            Assert.Equal(1, closed);
            Assert.False(FluidMotion.IsExiting(window));
        }
        finally
        {
            window.CompleteAndClose();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void SavedGetsAFrameBeforeTheEditorIsBuiltWithoutALongWait() => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = OwnedTestDirectory.Create("mc-saved-frame-");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var coordinator = new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
                paths, () => new RecordingSettings(), NullLoggerFactory.Instance,
                new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance));
            var drawn = (Task)typeof(RegionRecordingCoordinator)
                .GetMethod("DrawSavedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, null)!;

            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!drawn.IsCompleted && DateTime.UtcNow < deadline) Pump(Dispatcher.CurrentDispatcher, 10);

            Assert.True(drawn.IsCompletedSuccessfully);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });

    private static void Run(string culture, bool motion, Action<RecordingControlWindow> body) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        var window = new RecordingControlWindow(new RectD(100, 100, 640, 360), new RecordingSettings(),
            () => throw new InvalidOperationException("No recorder in this presentation test."), () => "unused.mp4", NullLogger.Instance);
        try
        {
            body(window);
        }
        finally
        {
            window.CompleteAndClose();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    // The bar follows the theme token, or its own emerald when the test thread has no theme.
    private static Color Success() =>
        ThemeBrushes.Follow("State.Success", Color.FromRgb(0x45, 0xD6, 0xA2)) is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0x45, 0xD6, 0xA2);

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void Pump(Dispatcher dispatcher, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, dispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
