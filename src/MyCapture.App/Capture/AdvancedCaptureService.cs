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
    bool RecordForRepeat = false,
    string SourcePageUrl = "",
    CaptureSourceDetails? SourceDetails = null);

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

    /// <summary>Optional actual browser-document metadata; absence never prevents capture.</summary>
    BrowserDocumentSnapshot? ReadSourceDocument(WindowUnderCursor? window) => null;

    Task<BrowserDocumentSnapshot?> ReadSourceDocumentAsync(WindowUnderCursor? window) =>
        Task.FromResult(ReadSourceDocument(window));

    /// <summary>Optional application and Explorer folder of a window; absence never prevents capture.</summary>
    WindowSourceSnapshot? ReadWindowSource(WindowUnderCursor? window) => null;

    Task<WindowSourceSnapshot?> ReadWindowSourceAsync(WindowUnderCursor? window) =>
        Task.FromResult(ReadWindowSource(window));

    /// <summary>Resolves a stored region against the current display topology.</summary>
    RectD? ResolveRepeatRegion(RegionHistoryEntry entry);

    /// <summary>False when an overlay or editor is already occupying the capture session.</summary>
    bool CanOpenEditor { get; }

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

    /// <summary>
    /// Captures the whole monitor under the cursor without a source. Like the selector's full-screen
    /// button and a desktop click, a monitor image usually shows several windows, and the cursor's
    /// position when the shortcut was pressed says nothing about which of them the user meant. A
    /// window title, page link or folder attached by that guess would mislead later, so none is kept.
    /// </summary>
    public CaptureOutcome CaptureFullScreen()
    {
        if (BusyOutcome() is { } busy)
        {
            return busy;
        }

        try
        {
            FrozenFrame frame = _environment.CaptureMonitorUnderCursor();
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            return Open(new AdvancedSelection(frame, region, string.Empty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Full-screen capture failed");
            return CaptureOutcome.Failed(MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
        }
    }

    /// <summary>
    /// Captures the entire visible DWM frame under the cursor in one arbitrary-region freeze.
    /// This preserves a window that straddles monitors instead of clipping it to one display.
    /// </summary>
    public CaptureOutcome CaptureWindow()
    {
        if (BusyOutcome() is { } busy)
        {
            return busy;
        }

        try
        {
            WindowUnderCursor? window = _environment.WindowAt(_environment.CursorPosition);
            if (window is null || window.ScreenBounds.IsEmpty)
            {
                return CaptureOutcome.NothingToCapture(UiText.Get("Text_4B4B0D333A67"));
            }

            BrowserDocumentSnapshot? before = ReadDocument(window);
            WindowSourceSnapshot? sourceBefore = ReadSource(window);
            FrozenFrame frame = _environment.CaptureScreenRegion(window.ScreenBounds);
            BrowserDocumentSnapshot? after = ReadCurrentDocument(window, frame.ScreenBounds.Center,
                frame.ScreenBounds);
            WindowSourceSnapshot? sourceAfter = ReadCurrentSource(window, frame.ScreenBounds.Center);
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            return Open(new AdvancedSelection(frame, region, window.Title, SourcePageUrl: StableUrl(before, after),
                SourceDetails: StableSource(window, sourceBefore, sourceAfter)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Window capture failed");
            return CaptureOutcome.Failed(MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
        }
    }

    /// <summary>Captures a fixed physical-pixel box centred at the cursor.</summary>
    public CaptureOutcome CaptureFixedSize(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            return CaptureOutcome.NothingToCapture(UiText.Get("Text_24A509A94313"));
        }

        if (BusyOutcome() is { } busy)
        {
            return busy;
        }

        try
        {
            PointD cursor = _environment.CursorPosition;
            WindowUnderCursor? window = _environment.WindowAt(cursor);
            BrowserDocumentSnapshot? before = ReadDocument(window);
            WindowSourceSnapshot? sourceBefore = ReadSource(window);
            FrozenFrame frame = _environment.CaptureMonitorUnderCursor();
            RectD? placement = FixedRegionPlanner.PlaceAtCursor(width, height, cursor, frame.ScreenBounds);
            if (placement is null)
            {
                return CaptureOutcome.NothingToCapture(UiText.Get("Text_24A509A94313"));
            }

            RectD region = frame.ToBitmapSpace(placement.Value);
            BrowserDocumentSnapshot? after = ReadCurrentDocument(window, placement.Value.Center, placement.Value);
            WindowSourceSnapshot? sourceAfter = region.IsEmpty ? null : ReadCurrentSource(window, placement.Value.Center);
            return region.IsEmpty
                ? CaptureOutcome.NothingToCapture(UiText.Get("Text_E2B271067CCF"))
                : Open(new AdvancedSelection(frame, region, window?.Title ?? string.Empty,
                    SourcePageUrl: StableUrl(before, after),
                    SourceDetails: StableSource(window, sourceBefore, sourceAfter)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Fixed-size capture failed");
            return CaptureOutcome.Failed(MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
        }
    }

    /// <summary>Replays the last manual region, remapped to the same current monitor/DPI.</summary>
    public CaptureOutcome RepeatLastRegion()
    {
        RegionHistoryEntry? entry = _lastRegions.LastEntry;
        if (entry is null)
        {
            return CaptureOutcome.NothingToCapture(UiText.Get("Text_6F025107D313"));
        }

        if (BusyOutcome() is { } busy)
        {
            return busy;
        }

        try
        {
            RectD? resolved = _environment.ResolveRepeatRegion(entry);
            if (resolved is null || resolved.Value.IsEmpty)
            {
                return CaptureOutcome.NothingToCapture(
                    UiText.Get("Text_D5A4B6986227"));
            }

            WindowUnderCursor? window = _environment.WindowAt(resolved.Value.Center);
            BrowserDocumentSnapshot? before = ReadDocument(window);
            WindowSourceSnapshot? sourceBefore = ReadSource(window);
            FrozenFrame frame = _environment.CaptureScreenRegion(resolved.Value);
            BrowserDocumentSnapshot? after = ReadCurrentDocument(window, frame.ScreenBounds.Center,
                frame.ScreenBounds);
            WindowSourceSnapshot? sourceAfter = ReadCurrentSource(window, frame.ScreenBounds.Center);
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            return Open(new AdvancedSelection(frame, region, window?.Title ?? string.Empty,
                SourcePageUrl: StableUrl(before, after),
                SourceDetails: StableSource(window, sourceBefore, sourceAfter)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Repeat-last-region failed");
            return CaptureOutcome.Failed(MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
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
            return CaptureOutcome.NothingToCapture(UiText.Get("Text_A7937F29AF18"));
        }

        if (targetWindow == IntPtr.Zero)
        {
            return CaptureOutcome.NothingToCapture(UiText.Get("Text_A57954F341E2"));
        }

        if (maxFrames < 2)
        {
            return CaptureOutcome.NothingToCapture(UiText.Get("Text_23A30AD1434C"));
        }

        if (BusyOutcome() is { } busy)
        {
            return busy;
        }

        try
        {
            cancellation.ThrowIfCancellationRequested();

            int width = checked((int)region.Width);
            var stitcher = new ScrollStitcher(width, options);
            WindowUnderCursor? window;
            try { window = _environment.WindowAt(region.Center); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { window = null; }
            var source = new ScrollingSource(window?.Handle == targetWindow ? window : null);
            source.SourceBefore = await ReadSourceAsync(source.Window, region, cancellation);
            ScrollFrame seed = ScrollFrameBridge.ToScrollFrame(
                await CaptureScrollingFrameAsync(region, source, cancellation));
            ScrollAppendResult seeded = stitcher.Append(seed);
            if (seeded.Kind == ScrollAppendKind.LimitReached)
            {
                return CaptureOutcome.Failed(UiText.Get("Text_9E479FEB720B"));
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
                        ? CaptureOutcome.Failed(UiText.Get("Text_B3B047802E13"))
                        : await OpenVerifiedPartialAsync(
                            stitcher,
                            UiText.Get("Text_257BE22416F8"),
                            cancellation,
                            source,
                            region);
                }

                await _delay(DefaultSettleDelay, cancellation);
                cancellation.ThrowIfCancellationRequested();

                ScrollAppendResult appended = await AppendCurrentFrameAsync(stitcher, region, source, cancellation);
                if (appended.Kind == ScrollAppendKind.NoNewContent)
                {
                    // Slow applications can repaint after the normal settle interval. Retry
                    // once without another scroll before declaring end-of-content.
                    await _delay(SlowSettleRetry, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    appended = await AppendCurrentFrameAsync(stitcher, region, source, cancellation);
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
                                UiText.Get("Text_D613D66285DC"));
                        }

                        ended = true;
                        break;

                    case ScrollAppendKind.NoOverlap:
                        if (verifiedTransitions == 0)
                        {
                            return CaptureOutcome.Failed(
                                UiText.Get("Text_815E84CD61AB"));
                        }

                        completionMessage = UiText.Get("Text_39B10B03FD99");
                        ended = true;
                        break;

                    case ScrollAppendKind.LimitReached:
                        if (verifiedTransitions == 0)
                        {
                            return CaptureOutcome.Failed(UiText.Get("Text_E33E531B40C6"));
                        }

                        completionMessage = UiText.Get("Text_45E6B157C051");
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
                return CaptureOutcome.NothingToCapture(UiText.Get("Text_D613D66285DC"));
            }

            if (!ended)
            {
                completionMessage = UiText.Format("Text_8C99A8AB9C4C", maxFrames);
            }

            cancellation.ThrowIfCancellationRequested();
            return await OpenVerifiedPartialAsync(stitcher, completionMessage, cancellation, source, region);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CaptureOutcome.Cancelled(UiText.Get("Text_04B54A7BED01"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Scrolling capture failed");
            return CaptureOutcome.Failed(MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
        }
    }

    private async Task<ScrollAppendResult> AppendCurrentFrameAsync(ScrollStitcher stitcher, RectD region,
        ScrollingSource source, CancellationToken cancellation)
    {
        BitmapSource shot = await CaptureScrollingFrameAsync(region, source, cancellation);
        return stitcher.Append(ScrollFrameBridge.ToScrollFrame(shot));
    }

    private async Task<CaptureOutcome> OpenVerifiedPartialAsync(
        ScrollStitcher stitcher,
        string completionMessage,
        CancellationToken cancellation,
        ScrollingSource source,
        RectD region)
    {
        cancellation.ThrowIfCancellationRequested();
        // The application and folder seen before the first frame must still hold after the last one.
        WindowSourceSnapshot? sourceAfter = source.SourceBefore is null
            ? null : await ReadSourceAsync(source.Window, region, cancellation);
        CaptureSourceDetails sourceDetails = StableSource(source.Window, source.SourceBefore, sourceAfter);
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
        CaptureOutcome opened = Open(new AdvancedSelection(resultFrame, resultRegion, string.Empty,
            SourcePageUrl: source.Url, SourceDetails: sourceDetails));
        return opened.IsCompleted && !string.IsNullOrWhiteSpace(completionMessage)
            ? CaptureOutcome.Completed(completionMessage)
            : opened;
    }

    private WindowSourceSnapshot? ReadSource(WindowUnderCursor? window)
    {
        if (window is null || window.ProcessId == 0 || window.Handle == IntPtr.Zero) return null;
        try { return ValidateSource(window, _environment.ReadWindowSource(window)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// Application and folder follow the window the title names, so unlike the page link they do not
    /// require the captured region to stay inside that window; the window itself must not change.
    /// </summary>
    private WindowSourceSnapshot? ReadCurrentSource(WindowUnderCursor? expected, PointD point) =>
        WindowStillMatches(expected, point, containedRegion: null) ? ReadSource(expected) : null;

    private async Task<WindowSourceSnapshot?> ReadSourceAsync(WindowUnderCursor? expected, RectD region,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!WindowStillMatches(expected, region.Center, containedRegion: null)) return null;
        try
        {
            WindowSourceSnapshot? source = await _environment.ReadWindowSourceAsync(expected);
            cancellation.ThrowIfCancellationRequested();
            return ValidateSource(expected!, source);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static WindowSourceSnapshot? ValidateSource(WindowUnderCursor window, WindowSourceSnapshot? source) =>
        source is not null && source.ProcessId == window.ProcessId ? source : null;

    private static CaptureSourceDetails StableSource(WindowUnderCursor? window, WindowSourceSnapshot? before,
        WindowSourceSnapshot? after) =>
        window is null ? CaptureSourceDetails.Empty : WindowSourceService.StableDetails(before, after, window.ProcessId);

    private BrowserDocumentSnapshot? ReadDocument(WindowUnderCursor? window)
    {
        if (window is null || window.ProcessId == 0) return null;
        try { return ValidateDocument(window, _environment.ReadSourceDocument(window)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private BrowserDocumentSnapshot? ReadCurrentDocument(WindowUnderCursor? expected, PointD point,
        RectD? containedRegion = null) =>
        WindowStillMatches(expected, point, containedRegion) ? ReadDocument(expected) : null;

    private bool WindowStillMatches(WindowUnderCursor? expected, PointD point, RectD? containedRegion)
    {
        if (expected is null || expected.ProcessId == 0 || expected.Handle == IntPtr.Zero) return false;
        try
        {
            WindowUnderCursor? current = _environment.WindowAt(point);
            if (current is null || current.Handle != expected.Handle || current.ProcessId != expected.ProcessId
                || !string.Equals(current.Title, expected.Title, StringComparison.Ordinal)
                || current.ScreenBounds != expected.ScreenBounds) return false;
            return containedRegion is not { } region
                || (region.Left >= current.ScreenBounds.Left && region.Top >= current.ScreenBounds.Top
                    && region.Right <= current.ScreenBounds.Right && region.Bottom <= current.ScreenBounds.Bottom);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private async Task<BrowserDocumentSnapshot?> ReadCurrentDocumentAsync(WindowUnderCursor? expected,
        RectD region, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!WindowStillMatches(expected, region.Center, region)) return null;
        try
        {
            BrowserDocumentSnapshot? document = await _environment.ReadSourceDocumentAsync(expected);
            cancellation.ThrowIfCancellationRequested();
            return ValidateDocument(expected!, document);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static BrowserDocumentSnapshot? ValidateDocument(WindowUnderCursor window, BrowserDocumentSnapshot? document) =>
        document is not null && document.ProcessId == window.ProcessId
        && string.Equals(document.WindowTitle, window.Title, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(document.DocumentId)
        && !string.IsNullOrEmpty(SourcePageUrl.Normalize(document.Url)) ? document : null;

    private static string StableUrl(BrowserDocumentSnapshot? before, BrowserDocumentSnapshot? after) =>
        before is not null && before == after ? SourcePageUrl.Normalize(before.Url) : string.Empty;

    private async Task<BitmapSource> CaptureScrollingFrameAsync(RectD region, ScrollingSource source,
        CancellationToken cancellation)
    {
        BrowserDocumentSnapshot? before = source.IsStable
            ? await ReadCurrentDocumentAsync(source.Window, region, cancellation) : null;
        cancellation.ThrowIfCancellationRequested();
        BitmapSource frame = _environment.CaptureRegion(region);
        BrowserDocumentSnapshot? after = source.IsStable && before is not null
            ? await ReadCurrentDocumentAsync(source.Window, region, cancellation) : null;
        cancellation.ThrowIfCancellationRequested();
        source.Observe(before, after);
        return frame;
    }

    private sealed class ScrollingSource(WindowUnderCursor? window)
    {
        internal WindowUnderCursor? Window { get; } = window;
        internal WindowSourceSnapshot? SourceBefore { get; set; }
        internal bool IsStable { get; private set; } = window is { ProcessId: > 0 };
        private BrowserDocumentSnapshot? _document;
        internal string Url => IsStable ? SourcePageUrl.Normalize(_document?.Url) : string.Empty;

        internal void Observe(BrowserDocumentSnapshot? before, BrowserDocumentSnapshot? after)
        {
            // Once any frame is unattributed, later navigation back to the original page
            // cannot make a multi-frame image a reliable single-page capture again.
            IsStable = IsStable && before is not null && before == after
                && (_document is null || _document == before);
            _document = before;
        }
    }

    private CaptureOutcome Open(AdvancedSelection selection) =>
        _environment.OpenEditor(selection)
            ? CaptureOutcome.Completed()
            : CaptureOutcome.Cancelled(UiText.Get("Text_F87FEA248BDD"));

    private CaptureOutcome? BusyOutcome() =>
        _environment.CanOpenEditor
            ? null
            : CaptureOutcome.Cancelled(UiText.Get("Text_F87FEA248BDD"));
}
