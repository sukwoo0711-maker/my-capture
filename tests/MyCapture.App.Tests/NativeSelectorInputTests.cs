using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorInputTests
{
    private static readonly RectD Bounds = new(-500, -200, 640, 360);
    private static readonly MonitorInfo Monitor = new("test", Bounds, Bounds, 96, true);

    [Fact]
    public void NativeKeyMessagesPreserveMinimumSizeAndInterruptedDragsCannotConfirm() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        int confirmed = 0;
        overlay.SelectionCompleted += (_, _) => confirmed++;
        overlay.BeginSelection(new PointD(10, 10));
        overlay.MoveSelection(new PointD(11, 11));
        _ = SendMessage(overlay.Handle, 0x0100, new IntPtr(13), IntPtr.Zero);
        Assert.Equal(0, confirmed);
        Assert.False(overlay.IsClosed);
        overlay.MoveSelection(new PointD(100, 80));
        _ = SendMessage(overlay.Handle, 0x0215, IntPtr.Zero, IntPtr.Zero);
        Assert.Null(overlay.Selection);
        overlay.EndSelection(new PointD(100, 80));
        _ = SendMessage(overlay.Handle, 0x0100, new IntPtr(13), IntPtr.Zero);
        Assert.Equal(0, confirmed);
        _ = SendMessage(overlay.Handle, 0x0100, new IntPtr(27), IntPtr.Zero);
        Assert.True(overlay.IsClosed);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
    });

    [Fact]
    public void RatioKeysLeaveControlAndAltChordsAloneAndShiftRCycles() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        Assert.False(overlay.HandleKey(Key.R, ModifierKeys.Control));
        Assert.False(overlay.HandleKey(Key.R, ModifierKeys.Alt));
        Assert.True(overlay.HandleKey(Key.R, ModifierKeys.Shift));
        Assert.Equal(MyCapture.Core.Capture.SelectionRatio.Ratio16x9, overlay.Ratio);
    });

    [Theory]
    [InlineData(ColorFormat.Hex, "#604020")]
    [InlineData(ColorFormat.Rgb, "rgb(96, 64, 32)")]
    [InlineData(ColorFormat.Hsl, "hsl(30, 50%, 25%)")]
    public void CKeyCopiesFrozenPhysicalPixelAndClosesBeforeFeedback(ColorFormat format, string expected) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        var copied = new List<string>();
        overlay.CopyColorText = text => { copied.Add(text); return Task.FromResult(true); };
        overlay.ReadColorFormat = () => format;
        int closed = 0, confirmed = 0;
        ColorCopyFeedback? feedback = null;
        overlay.Closed += (_, _) => closed++;
        overlay.SelectionCompleted += (_, _) => confirmed++;
        overlay.ColorCopied = result => { Assert.Equal(1, closed); feedback = result; };
        Assert.True(overlay.HandleKey(Key.C, ModifierKeys.Shift));
        Assert.Equal([expected], copied);
        Assert.NotNull(feedback);
        Assert.Equal(Color.FromRgb(96, 64, 32), feedback.ChipColor);
        Assert.Equal("#604020", feedback.HexValue);
        Assert.Equal(0, confirmed);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
        Assert.Null(overlay.CopyColorAtPointerAsync().Result);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingCopyBlocksConfirmationUntilFailureIsHandledThenAllowsRetry(bool fullScreen) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        overlay.CopyColorText = _ => pending.Task;
        overlay.BeginSelection(new PointD(100, 100));
        overlay.MoveSelection(new PointD(200, 200));
        int confirmed = 0;
        overlay.SelectionCompleted += (_, _) => confirmed++;
        Task<string?> copy = overlay.CopyColorAtPointerAsync();
        overlay.EndSelection(new PointD(200, 200));
        overlay.ConfirmCurrent();
        overlay.CompleteMonitor();
        Assert.Equal(0, confirmed);
        Assert.Null(overlay.CopyColorAtPointerAsync().Result);
        Task.Run(() => pending.SetResult(false)).GetAwaiter().GetResult();
        Assert.False(copy.IsCompleted);
        overlay.ConfirmCurrent();
        Assert.Equal(0, confirmed);
        PumpUntil(() => copy.IsCompleted);
        Assert.Null(copy.Result);
        Assert.Equal(UiText.Get("ColorPick_CopyFailed"), overlay.ToastMessage);
        if (fullScreen) overlay.CompleteMonitor(); else overlay.ConfirmCurrent();
        Assert.Equal(1, confirmed);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
    });

    [Fact]
    public void LateSuccessfulColorCopyCannotNotifyOrReopenAfterCancel() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        overlay.CopyColorText = _ => pending.Task;
        int notifications = 0;
        overlay.ColorCopied = _ => notifications++;
        Task<string?> copy = overlay.CopyColorAtPointerAsync();
        overlay.Close();
        pending.SetResult(true);
        PumpUntil(() => copy.IsCompleted);
        Assert.Equal(0, notifications);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
        Assert.Null(overlay.Frame);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TToggleAndDirectTextEntryCarryTextContractWithoutPendingSource(bool direct) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.TextCaptureAvailable = true;
        if (direct) overlay.TextCaptureMode = true;
        else
        {
            Assert.False(overlay.HandleKey(Key.T, ModifierKeys.Control));
            Assert.True(overlay.HandleKey(Key.T, ModifierKeys.Shift));
        }
        Assert.True(overlay.TextCaptureMode);
        Assert.Equal(CaptureSelectorMode.Text, overlay.Mode);
        overlay.AwaitSourceDetails(new TaskCompletionSource<IReadOnlyList<WindowCandidate>>().Task);
        CaptureSelectionCompletedEventArgs? completed = null;
        overlay.SelectionCompleted += (_, e) => completed = e;
        overlay.BeginSelection(new PointD(10, 20));
        overlay.EndSelection(new PointD(100, 90));
        Assert.NotNull(completed);
        Assert.True(completed.TextCapture);
        Assert.Equal(new RectD(10, 20, 90, 70), completed.BitmapRegion);
        Assert.Null(completed.PendingSource);
        Assert.True(completed.RecordForRepeat);
        Assert.True(completed.SelectedBitmap.IsFrozen);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
    });

    [Fact]
    public void WindowClickKeepsZOrderAndBackfillsTheClickedWindowSource() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        var chosen = new WindowCandidate(new IntPtr(11), new RectD(-460, -170, 300, 200), "Editor");
        var other = new WindowCandidate(new IntPtr(12), Bounds, "Behind");
        overlay.SourceWindows = [chosen, other];
        overlay.DetectWindows = true;
        overlay.RefreshHover();
        Assert.Equal(new RectD(40, 30, 300, 200), overlay.HoverRegion);
        var pending = new TaskCompletionSource<IReadOnlyList<WindowCandidate>>();
        overlay.AwaitSourceDetails(pending.Task);
        CaptureSelectionCompletedEventArgs? completed = null;
        overlay.SelectionCompleted += (_, e) => completed = e;
        overlay.BeginSelection(new PointD(100, 100));
        overlay.MoveSelection(new PointD(102, 101));
        Assert.Null(overlay.Selection);
        overlay.EndSelection(new PointD(102, 101));
        Assert.NotNull(completed);
        Assert.Equal(new RectD(40, 30, 300, 200), completed.BitmapRegion);
        Assert.Equal("Editor", completed.SourceTitle);
        Assert.NotNull(completed.PendingSource);
        pending.SetResult([chosen with { SourcePageUrl = "https://example.com/editor" }, other]);
        Assert.Equal("https://example.com/editor", completed.PendingSource.GetAwaiter().GetResult().PageUrl);
    });

    [Fact]
    public void DragLeavesWindowClickThresholdAndUsesTheRatio() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.SourceWindows = [new(new IntPtr(11), Bounds, "Editor")];
        overlay.DetectWindows = true;
        overlay.SelectRatio(MyCapture.Core.Capture.SelectionRatio.Ratio16x9);
        overlay.BeginSelection(new PointD(100, 100));
        overlay.MoveSelection(new PointD(260, 180));
        Assert.Null(overlay.HoverRegion);
        Assert.Equal(new RectD(100, 100, 160, 90), overlay.Selection);
        CaptureSelectionCompletedEventArgs? completed = null;
        overlay.SelectionCompleted += (_, e) => completed = e;
        overlay.EndSelection(new PointD(260, 180));
        Assert.Equal(new RectD(100, 100, 160, 90), completed!.BitmapRegion);
    });

    [Fact]
    public void DesktopAndFullScreenUseTheMonitorWithoutAWindowSource() => StaTestHost.Run(() =>
    {
        foreach (bool button in new[] { false, true })
        {
            using var overlay = Create();
            overlay.SourceWindows = [new(new IntPtr(11), Bounds, "Desktop", ClassName: "Progman")];
            overlay.DetectWindows = true;
            CaptureSelectionCompletedEventArgs? completed = null;
            overlay.SelectionCompleted += (_, e) => completed = e;
            if (button) overlay.CompleteMonitor();
            else { overlay.BeginSelection(new PointD(100, 100)); overlay.EndSelection(new PointD(101, 101)); }
            Assert.Equal(new RectD(0, 0, 640, 360), completed!.BitmapRegion);
            Assert.Equal(string.Empty, completed.SourceTitle);
            Assert.Equal(string.Empty, completed.SourcePageUrl);
            Assert.True(completed.RecordForRepeat); // Same result contract as CaptureOverlayWindow.
        }
    });

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(0)]
    public void ModeBarHandsDelayedOrScrollingActionToOwnerBeforeClosing(int seconds) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.OffersOtherActions = true;
        CaptureSelectorRequest? action = null;
        int selections = 0;
        overlay.SelectionCompleted += (_, _) => selections++;
        overlay.ActionRequested += (_, request) => { Assert.False(overlay.IsClosed); action = request; };
        overlay.Closed += (_, _) => Assert.NotNull(action);
        if (seconds == 0) overlay.ModeBar.ActivateButton(overlay.ModeBar.ScrollButton);
        else
        {
            var item = overlay.ModeBar.CreateDelayMenu().Items.OfType<MenuItem>().ElementAt(seconds == 3 ? 0 : seconds == 5 ? 1 : 2);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        Assert.NotNull(action);
        Assert.Equal(seconds == 0 ? CaptureSelectorAction.ScrollingCapture : CaptureSelectorAction.DelayedCapture, action.Action);
        Assert.Equal(seconds, action.DelaySeconds);
        Assert.Equal(0, selections);
        Assert.True(overlay.IsClosed);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
    });

    [Theory]
    [InlineData(0x007E)] // WM_DISPLAYCHANGE
    [InlineData(0x02E0)] // WM_DPICHANGED
    [InlineData(0x0204)] // WM_RBUTTONDOWN
    [InlineData(0x0010)] // WM_CLOSE
    public void NativeCancellationMessagesReleaseEveryOwnedResource(int message) => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        IntPtr hwnd = overlay.Handle;
        NativeSelectorDib dib = overlay.DisplayDib!;
        int confirmed = 0, closed = 0;
        overlay.SelectionCompleted += (_, _) => confirmed++;
        overlay.Closed += (_, _) => closed++;
        _ = SendMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(0, confirmed);
        Assert.Equal(1, closed);
        Assert.False(NativeSelectorInterop.IsWindow(hwnd));
        Assert.True(dib.IsDisposed);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
        Assert.False(overlay.IsReady);
        Assert.Null(overlay.Frame);
    });

    [Fact]
    public void HiddenChromeCreatesBoundedWindowsAndDisposesTheirActualHandles() => StaTestHost.Run(() =>
    {
        using var overlay = Create();
        overlay.Prepare();
        using var chrome = new NativeSelectorChrome(overlay, true, false);
        IntPtr[] handles = chrome.Windows.Select(window => new WindowInteropHelper(window).Handle).ToArray();
        Assert.Equal(4, handles.Length);
        Assert.All(handles, hwnd => Assert.True(NativeSelectorInterop.IsWindow(hwnd)));
        Assert.All(chrome.Windows, window => { Assert.False(window.IsVisible); Assert.True(window.Width < 640); Assert.True(window.Height < 200); });
        chrome.Dispose();
        Assert.All(handles, hwnd => Assert.False(NativeSelectorInterop.IsWindow(hwnd)));
        Assert.Empty(chrome.Windows);
        overlay.Close();
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
    });

    private static NativeCaptureOverlay Create()
    {
        byte[] pixels = new byte[640 * 360 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        { pixels[offset] = 32; pixels[offset + 1] = 64; pixels[offset + 2] = 96; pixels[offset + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, pixels, 640 * 4);
        bitmap.Freeze();
        var overlay = new NativeCaptureOverlay(Bounds, false, false)
        {
            ReadCursor = () => new PointD(-400, -100), ReadMonitors = () => [Monitor],
        };
        overlay.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0));
        return overlay;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
