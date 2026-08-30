using System.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>A pre-decided selection that will enter the shared editor/persistence pipeline.</summary>
internal sealed record AdvancedSelection(
    FrozenFrame Frame,
    RectD Region,
    string SourceTitle,
    bool RecordForRepeat = false);

/// <summary>Platform and WPF seams used by the deterministic advanced-mode orchestration.</summary>
internal interface IAdvancedCaptureEnvironment
{
    FrozenFrame CaptureMonitorUnderCursor();

    /// <summary>Freezes an arbitrary visible virtual-desktop rectangle as a self-contained frame.</summary>
    FrozenFrame CaptureScreenRegion(RectD screenBounds);

    /// <summary>Captures one scrolling viewport in physical pixels.</summary>
    BitmapSource CaptureRegion(RectD screenBounds);

    PointD CursorPosition { get; }

    WindowUnderCursor? WindowAt(PointD screenPoint);

    /// <summary>Resolves a stored region against the current display topology.</summary>
    RectD? ResolveRepeatRegion(RegionHistoryEntry entry);

    bool OpenEditor(AdvancedSelection selection);
}

/// <summary>
/// Advanced capture modes. Every successful mode opens the same editor-first overlay, which
/// raises the same SelectionCompleted event and therefore uses immediate queue persistence.
/// </summary>
internal sealed class AdvancedCaptureService
{
    private static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan SlowSettleRetry = TimeSpan.FromMilliseconds(220);

    private readonly IAdvancedCaptureEnvironment _environment;
    private readonly LastRegionStore _lastRegions;
    private readonly IScrollInputSink _scrollInput;
    private readonly ILogger _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal AdvancedCaptureService(
        IAdvancedCaptureEnvironment environment,
        LastRegionStore lastRegions,
        IScrollInputSink scrollInput,
        ILogger log,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _lastRegions = lastRegions ?? throw new ArgumentNullException(nameof(lastRegions));
        _scrollInput = scrollInput ?? throw new ArgumentNullException(nameof(scrollInput));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _delay = delay ?? Task.Delay;
    }

    public CaptureOutcome CaptureFullScreen()
    {
        try
        {
            FrozenFrame frame = _environment.CaptureMonitorUnderCursor();
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            string title = frame.Monitor?.DeviceName ?? string.Empty;
            return Open(new AdvancedSelection(frame, region, title));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Full-screen capture failed");
            return CaptureOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Captures the entire visible DWM frame under the cursor in one arbitrary-region freeze.
    /// This preserves a window that straddles monitors instead of clipping it to one display.
    /// </summary>
    public CaptureOutcome CaptureWindow()
    {
        try
        {
            WindowUnderCursor? window = _environment.WindowAt(_environment.CursorPosition);
            if (window is null || window.ScreenBounds.IsEmpty)
            {
                return CaptureOutcome.NothingToCapture("커서 아래에 캡처할 창이 없습니다.");
            }

            FrozenFrame frame = _environment.CaptureScreenRegion(window.ScreenBounds);
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            return Open(new AdvancedSelection(frame, region, window.Title));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Window capture failed");
            return CaptureOutcome.Failed(ex.Message);
        }
    }

    /// <summary>Captures a fixed physical-pixel box centred at the cursor.</summary>
    public CaptureOutcome CaptureFixedSize(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            return CaptureOutcome.NothingToCapture("고정 크기 캡처의 크기가 올바르지 않습니다.");
        }

        try
        {
            PointD cursor = _environment.CursorPosition;
            FrozenFrame frame = _environment.CaptureMonitorUnderCursor();
            RectD? placement = FixedRegionPlanner.PlaceAtCursor(width, height, cursor, frame.ScreenBounds);
            if (placement is null)
            {
                return CaptureOutcome.NothingToCapture("고정 크기 캡처의 크기가 올바르지 않습니다.");
            }

            RectD region = frame.ToBitmapSpace(placement.Value);
            return region.IsEmpty
                ? CaptureOutcome.NothingToCapture("고정 크기 영역이 이 모니터 밖에 있습니다.")
                : Open(new AdvancedSelection(frame, region, string.Empty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Fixed-size capture failed");
            return CaptureOutcome.Failed(ex.Message);
        }
    }

    /// <summary>Replays the last manual region, remapped to the same current monitor/DPI.</summary>
    public CaptureOutcome RepeatLastRegion()
    {
        RegionHistoryEntry? entry = _lastRegions.LastEntry;
        if (entry is null)
        {
            return CaptureOutcome.NothingToCapture("반복할 이전 영역이 없습니다.");
        }

        try
        {
            RectD? resolved = _environment.ResolveRepeatRegion(entry);
            if (resolved is null || resolved.Value.IsEmpty)
            {
                return CaptureOutcome.NothingToCapture(
                    "이전 영역의 모니터가 연결되어 있지 않습니다. 새 영역을 선택해 주세요.");
            }

            FrozenFrame frame = _environment.CaptureScreenRegion(resolved.Value);
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            return Open(new AdvancedSelection(frame, region, string.Empty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Repeat-last-region failed");
            return CaptureOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Captures and verifies a scrolling client area asynchronously. The delay seam lets the
    /// target repaint and keeps the WPF dispatcher responsive so the tray command can cancel.
    /// </summary>
    public async Task<CaptureOutcome> CaptureScrollingAsync(
        IntPtr targetWindow,
        RectD screenRegion,
        ScrollStitchOptions options,
        int maxFrames = 40,
        CancellationToken cancellation = default)
    {
        RectD region = screenRegion.Normalized().ToPixelBounds();
        if (region.IsEmpty)
        {
            return CaptureOutcome.NothingToCapture("스크롤 캡처할 영역이 없습니다.");
        }

        if (targetWindow == IntPtr.Zero)
        {
            return CaptureOutcome.NothingToCapture("스크롤 캡처할 창이 없습니다.");
        }

        if (maxFrames < 2)
        {
            return CaptureOutcome.NothingToCapture("스크롤 캡처에는 두 프레임 이상이 필요합니다.");
        }

        try
        {
            cancellation.ThrowIfCancellationRequested();

            int width = checked((int)region.Width);
            var stitcher = new ScrollStitcher(width, options);
            ScrollFrame seed = ScrollFrameBridge.ToScrollFrame(_environment.CaptureRegion(region));
            ScrollAppendResult seeded = stitcher.Append(seed);
            if (seeded.Kind == ScrollAppendKind.LimitReached)
            {
                return CaptureOutcome.Failed("첫 화면이 스크롤 캡처 안전 한도를 초과합니다.");
            }

            int verifiedTransitions = 0;
            string completionMessage = string.Empty;
            bool ended = false;

            for (int transition = 1; transition < maxFrames; transition++)
            {
                cancellation.ThrowIfCancellationRequested();

                if (!_scrollInput.ScrollDown(targetWindow, region.Center, notches: 3))
                {
                    return verifiedTransitions == 0
                        ? CaptureOutcome.Failed("대상 창에 스크롤 입력을 보낼 수 없습니다.")
                        : await OpenVerifiedPartialAsync(
                            stitcher,
                            "스크롤 입력이 거부되어 검증된 부분까지만 열었습니다.",
                            cancellation);
                }

                await _delay(DefaultSettleDelay, cancellation);
                cancellation.ThrowIfCancellationRequested();

                ScrollAppendResult appended = AppendCurrentFrame(stitcher, region);
                if (appended.Kind == ScrollAppendKind.NoNewContent)
                {
                    // Slow applications can repaint after the normal settle interval. Retry
                    // once without another scroll before declaring end-of-content.
                    await _delay(SlowSettleRetry, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    appended = AppendCurrentFrame(stitcher, region);
                }

                switch (appended.Kind)
                {
                    case ScrollAppendKind.Appended:
                        verifiedTransitions++;
                        break;

                    case ScrollAppendKind.NoNewContent:
                        if (verifiedTransitions == 0)
                        {
                            return CaptureOutcome.NothingToCapture(
                                "추가로 스크롤할 콘텐츠를 찾지 못했습니다.");
                        }

                        ended = true;
                        break;

                    case ScrollAppendKind.NoOverlap:
                        if (verifiedTransitions == 0)
                        {
                            return CaptureOutcome.Failed(
                                "스크롤 전후 화면의 겹치는 영역을 확인할 수 없어 캡처를 중단했습니다.");
                        }

                        completionMessage = "화면 정합을 확인할 수 없어 검증된 부분까지만 열었습니다.";
                        ended = true;
                        break;

                    case ScrollAppendKind.LimitReached:
                        if (verifiedTransitions == 0)
                        {
                            return CaptureOutcome.Failed("스크롤 결과가 안전 크기 한도를 초과합니다.");
                        }

                        completionMessage = "안전 크기 한도에 도달해 검증된 부분까지만 열었습니다.";
                        ended = true;
                        break;

                    case ScrollAppendKind.Seeded:
                        throw new InvalidOperationException("A seeded stitcher cannot be seeded twice.");
                }

                if (ended)
                {
                    break;
                }
            }

            if (verifiedTransitions == 0)
            {
                return CaptureOutcome.NothingToCapture("추가로 스크롤할 콘텐츠를 찾지 못했습니다.");
            }

            if (!ended)
            {
                completionMessage = $"최대 {maxFrames}프레임 안전 한도에 도달해 검증된 부분까지만 열었습니다.";
            }

            cancellation.ThrowIfCancellationRequested();
            return await OpenVerifiedPartialAsync(stitcher, completionMessage, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CaptureOutcome.Cancelled("스크롤 캡처가 취소되었습니다.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Scrolling capture failed");
            return CaptureOutcome.Failed(ex.Message);
        }
    }

    private ScrollAppendResult AppendCurrentFrame(ScrollStitcher stitcher, RectD region)
    {
        BitmapSource shot = _environment.CaptureRegion(region);
        return stitcher.Append(ScrollFrameBridge.ToScrollFrame(shot));
    }

    private Task<CaptureOutcome> OpenVerifiedPartialAsync(
        ScrollStitcher stitcher,
        string completionMessage,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ScrollFrame image = stitcher.ToImage();
        BitmapSource stitched = ScrollFrameBridge.ToBitmap(image);
        cancellation.ThrowIfCancellationRequested();

        var resultFrame = new FrozenFrame(
            stitched,
            new RectD(0, 0, stitched.PixelWidth, stitched.PixelHeight),
            null,
            0);
        var resultRegion = new RectD(0, 0, stitched.PixelWidth, stitched.PixelHeight);
        CaptureOutcome opened = Open(new AdvancedSelection(resultFrame, resultRegion, string.Empty));
        return Task.FromResult(opened.IsCompleted && !string.IsNullOrWhiteSpace(completionMessage)
            ? CaptureOutcome.Completed(completionMessage)
            : opened);
    }

    private CaptureOutcome Open(AdvancedSelection selection) =>
        _environment.OpenEditor(selection)
            ? CaptureOutcome.Completed()
            : CaptureOutcome.Cancelled("이미 캡처가 진행 중입니다.");
}
