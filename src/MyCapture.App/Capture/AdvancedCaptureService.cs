using System.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>A pre-decided selection that will enter the shared editor/persistence pipeline.</summary>
/// <param name="PendingSource">
/// Page and application of the captured window while their reads are still running; null when
/// <paramref name="SourcePageUrl"/> and <paramref name="SourceDetails"/> are final. It resolves within the
/// readers' deadlines and never faults. The capture is saved and opened without waiting for it, and the
/// details are added to the saved capture when it resolves (CaptureSelectionCompletedEventArgs.PendingSource).
/// </param>
internal sealed record AdvancedSelection(
    FrozenFrame Frame,
    RectD Region,
    string SourceTitle,
    bool RecordForRepeat = false,
    string SourcePageUrl = "",
    CaptureSourceDetails? SourceDetails = null,
    Task<CaptureSelectionSource>? PendingSource = null);

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

    /// <summary>
    /// The read the service uses. No capture waits for it: it runs beside the pixel grab and its result
    /// reaches the capture later.
    /// </summary>
    Task<BrowserDocumentSnapshot?> ReadSourceDocumentAsync(WindowUnderCursor? window) =>
        Task.FromResult(ReadSourceDocument(window));

    /// <summary>Optional application and Explorer folder of a window; absence never prevents capture.</summary>
    WindowSourceSnapshot? ReadWindowSource(WindowUnderCursor? window) => null;

    /// <summary>The read the service uses; like the page read, nothing waits for it.</summary>
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
/// <remarks>
/// The captured window, its rectangle and title come from a cheap window snapshot taken when the
/// command runs. Its page and application are read the way the region selector reads them
/// (<see cref="CapturePageContext"/>): a first read starts before the grab and runs beside it, a
/// confirming read starts after both the pixels and the first read, and only what both agree on for
/// that same window is kept. No capture, save or editor waits for those reads.
/// </remarks>
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
            // The rectangle comes from the window snapshot, never from the page or application reads.
            WindowUnderCursor? window = _environment.WindowAt(_environment.CursorPosition);
            if (window is null || window.ScreenBounds.IsEmpty)
            {
                return CaptureOutcome.NothingToCapture(UiText.Get("Text_4B4B0D333A67"));
            }

            StillSourceReads? reads = StartSourceReads(window);
            FrozenFrame frame = _environment.CaptureScreenRegion(window.ScreenBounds);
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            return Open(new AdvancedSelection(frame, region, window.Title),
                reads?.ConfirmAfterPixels(frame.ScreenBounds.Center, frame.ScreenBounds));
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
            StillSourceReads? reads = StartSourceReads(window);
            FrozenFrame frame = _environment.CaptureMonitorUnderCursor();
            RectD? placement = FixedRegionPlanner.PlaceAtCursor(width, height, cursor, frame.ScreenBounds);
            if (placement is null)
            {
                return CaptureOutcome.NothingToCapture(UiText.Get("Text_24A509A94313"));
            }

            RectD region = frame.ToBitmapSpace(placement.Value);
            return region.IsEmpty
                ? CaptureOutcome.NothingToCapture(UiText.Get("Text_E2B271067CCF"))
                : Open(new AdvancedSelection(frame, region, window?.Title ?? string.Empty),
                    reads?.ConfirmAfterPixels(placement.Value.Center, placement.Value));
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
            StillSourceReads? reads = StartSourceReads(window);
            FrozenFrame frame = _environment.CaptureScreenRegion(resolved.Value);
            var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
            return Open(new AdvancedSelection(frame, region, window?.Title ?? string.Empty),
                reads?.ConfirmAfterPixels(frame.ScreenBounds.Center, frame.ScreenBounds));
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
            var source = new ScrollingSource(this, window?.Handle == targetWindow ? window : null, region);
            source.Start();
            ScrollFrame seed = ScrollFrameBridge.ToScrollFrame(CaptureScrollingFrame(region, source, cancellation));
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
                        : OpenVerifiedPartial(
                            stitcher,
                            UiText.Get("Text_257BE22416F8"),
                            cancellation,
                            source);
                }

                await _delay(DefaultSettleDelay, cancellation);
                cancellation.ThrowIfCancellationRequested();

                ScrollAppendResult appended = AppendCurrentFrame(stitcher, region, source, cancellation);
                if (appended.Kind == ScrollAppendKind.NoNewContent)
                {
                    // Slow applications can repaint after the normal settle interval. Retry
                    // once without another scroll before declaring end-of-content.
                    await _delay(SlowSettleRetry, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    appended = AppendCurrentFrame(stitcher, region, source, cancellation);
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
            return OpenVerifiedPartial(stitcher, completionMessage, cancellation, source);
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

    private ScrollAppendResult AppendCurrentFrame(ScrollStitcher stitcher, RectD region,
        ScrollingSource source, CancellationToken cancellation)
    {
        BitmapSource shot = CaptureScrollingFrame(region, source, cancellation);
        return stitcher.Append(ScrollFrameBridge.ToScrollFrame(shot));
    }

    private CaptureOutcome OpenVerifiedPartial(
        ScrollStitcher stitcher,
        string completionMessage,
        CancellationToken cancellation,
        ScrollingSource source)
    {
        cancellation.ThrowIfCancellationRequested();
        // The reads confirming the last frame start now and run beside the stitching.
        Task<CaptureSelectionSource>? lateSource = source.Finish();
        ScrollFrame image = stitcher.ToImage();
        BitmapSource stitched = ScrollFrameBridge.ToBitmap(image);
        cancellation.ThrowIfCancellationRequested();

        var resultFrame = new FrozenFrame(
            stitched,
            new RectD(0, 0, stitched.PixelWidth, stitched.PixelHeight),
            null,
            0);
        var resultRegion = new RectD(0, 0, stitched.PixelWidth, stitched.PixelHeight);
        CaptureOutcome opened = Open(new AdvancedSelection(resultFrame, resultRegion, string.Empty), lateSource);
        return opened.IsCompleted && !string.IsNullOrWhiteSpace(completionMessage)
            ? CaptureOutcome.Completed(completionMessage)
            : opened;
    }

    private static readonly Task<BrowserDocumentSnapshot?> NoDocument = Task.FromResult<BrowserDocumentSnapshot?>(null);
    private static readonly Task<WindowSourceSnapshot?> NoSource = Task.FromResult<WindowSourceSnapshot?>(null);

    /// <summary>
    /// Starts the first page and application reads of <paramref name="window"/>, before its pixels are
    /// taken. Null when the window has nothing to read.
    /// </summary>
    private StillSourceReads? StartSourceReads(WindowUnderCursor? window) =>
        window is null || window.ProcessId == 0 || window.Handle == IntPtr.Zero
            ? null
            : new StillSourceReads(this, window, ReadDocumentAsync(window), ReadSourceAsync(window));

    /// <summary>A validated page read of <paramref name="window"/>; null when there is none. Never faults.</summary>
    private async Task<BrowserDocumentSnapshot?> ReadDocumentAsync(WindowUnderCursor window)
    {
        try { return ValidateDocument(window, await _environment.ReadSourceDocumentAsync(window).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>A validated application read of <paramref name="window"/>; null when there is none. Never faults.</summary>
    private async Task<WindowSourceSnapshot?> ReadSourceAsync(WindowUnderCursor window)
    {
        try { return ValidateSource(window, await _environment.ReadWindowSourceAsync(window).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static WindowSourceSnapshot? ValidateSource(WindowUnderCursor window, WindowSourceSnapshot? source) =>
        source is not null && source.ProcessId == window.ProcessId ? source : null;

    private static CaptureSourceDetails StableSource(WindowUnderCursor window, WindowSourceSnapshot? before,
        WindowSourceSnapshot? after) =>
        WindowSourceService.StableDetails(before, after, window.ProcessId);

    /// <summary>
    /// The window under <paramref name="point"/> is still <paramref name="expected"/>, unchanged. The page
    /// link also needs <paramref name="containedRegion"/> inside it; application and folder follow the
    /// window the title names, so they are checked without a region.
    /// </summary>
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

    private static BrowserDocumentSnapshot? ValidateDocument(WindowUnderCursor window, BrowserDocumentSnapshot? document) =>
        document is not null && document.ProcessId == window.ProcessId
        && string.Equals(document.WindowTitle, window.Title, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(document.DocumentId)
        && !string.IsNullOrEmpty(SourcePageUrl.Normalize(document.Url)) ? document : null;

    private static string StableUrl(BrowserDocumentSnapshot? before, BrowserDocumentSnapshot? after) =>
        before is not null && before == after ? SourcePageUrl.Normalize(before.Url) : string.Empty;

    private BitmapSource CaptureScrollingFrame(RectD region, ScrollingSource source, CancellationToken cancellation)
    {
        // The page read runs beside the frame; the frame never waits for it.
        source.Sample();
        cancellation.ThrowIfCancellationRequested();
        BitmapSource frame = _environment.CaptureRegion(region);
        source.FrameCaptured();
        cancellation.ThrowIfCancellationRequested();
        return frame;
    }

    /// <summary>
    /// The page and application reads of one still capture. The first reads start before the grab and run
    /// beside it; <see cref="ConfirmAfterPixels"/> checks the window right after the pixels, and the
    /// confirming reads start once both the pixels and the first reads are done.
    /// </summary>
    private sealed class StillSourceReads(
        AdvancedCaptureService owner,
        WindowUnderCursor window,
        Task<BrowserDocumentSnapshot?> pageBefore,
        Task<WindowSourceSnapshot?> sourceBefore)
    {
        /// <summary>
        /// Called right after the pixels. The result resolves to the window's title with the page and
        /// application both reads agree on; it never faults.
        /// </summary>
        internal Task<CaptureSelectionSource> ConfirmAfterPixels(PointD point, RectD capturedRegion)
        {
            bool pageHeld = owner.WindowStillMatches(window, point, capturedRegion);
            bool sourceHeld = owner.WindowStillMatches(window, point, containedRegion: null);
            return ConfirmAsync(point, capturedRegion, pageHeld, sourceHeld);
        }

        private async Task<CaptureSelectionSource> ConfirmAsync(PointD point, RectD capturedRegion, bool pageHeld,
            bool sourceHeld)
        {
            // Each reader runs one native probe at a time, and a read overlapping the first could not
            // show that nothing changed after it, so the confirming reads start after the first ones.
            BrowserDocumentSnapshot? page = await pageBefore.ConfigureAwait(false);
            WindowSourceSnapshot? source = await sourceBefore.ConfigureAwait(false);
            // Only a value the first read saw can be confirmed; the window is checked again as each starts.
            Task<BrowserDocumentSnapshot?> pageAfter = pageHeld && page is not null
                && owner.WindowStillMatches(window, point, capturedRegion)
                ? owner.ReadDocumentAsync(window) : NoDocument;
            Task<WindowSourceSnapshot?> sourceAfter = sourceHeld && source is not null
                && owner.WindowStillMatches(window, point, containedRegion: null)
                ? owner.ReadSourceAsync(window) : NoSource;
            BrowserDocumentSnapshot? pageConfirmed = await pageAfter.ConfigureAwait(false);
            WindowSourceSnapshot? sourceConfirmed = await sourceAfter.ConfigureAwait(false);
            return new CaptureSelectionSource(window.Title, StableUrl(page, pageConfirmed),
                StableSource(window, source, sourceConfirmed));
        }
    }

    /// <summary>
    /// Page and application of a scrolling capture's target window. The frames never wait for them. The
    /// application is read before the first frame and again after the last. Page reads run one at a time
    /// beside the frames, the first starting before the first frame and the last after the last frame,
    /// and the page is kept only when every read agrees; the window itself is checked beside every frame.
    /// With a responsive reader that is still a page read before and after every frame.
    /// </summary>
    private sealed class ScrollingSource
    {
        private readonly AdvancedCaptureService _owner;
        private readonly RectD _region;
        private Task<WindowSourceSnapshot?>? _sourceBefore;
        private Task<BrowserDocumentSnapshot?>? _read;
        private int _readStartedAfterFrames;
        private int _frames;
        private BrowserDocumentSnapshot? _document;
        private bool _pageStable;

        internal ScrollingSource(AdvancedCaptureService owner, WindowUnderCursor? window, RectD region)
        {
            _owner = owner;
            _region = region;
            Window = window is { ProcessId: > 0 } && window.Handle != IntPtr.Zero ? window : null;
            _pageStable = Window is not null;
        }

        private WindowUnderCursor? Window { get; }

        /// <summary>Before the first frame: starts the application read.</summary>
        internal void Start()
        {
            if (Window is not null && _owner.WindowStillMatches(Window, _region.Center, containedRegion: null))
                _sourceBefore = _owner.ReadSourceAsync(Window);
        }

        /// <summary>
        /// Right before and right after each frame: checks the window and, once the previous page read
        /// has finished, records it and starts the next.
        /// </summary>
        internal void Sample()
        {
            if (!_pageStable) return;
            if (!_owner.WindowStillMatches(Window, _region.Center, _region))
            {
                _pageStable = false;
                return;
            }

            if (_read is { IsCompleted: false }) return;
            if (_read is { } finished && !Observe(finished.Result)) return;
            _read = _owner.ReadDocumentAsync(Window!);
            _readStartedAfterFrames = _frames;
        }

        internal void FrameCaptured()
        {
            _frames++;
            Sample();
        }

        /// <summary>After the last frame. Null when the window has nothing to read.</summary>
        internal Task<CaptureSelectionSource>? Finish()
        {
            if (Window is null) return null;
            bool pageHeld = _pageStable && _read is not null
                && _owner.WindowStillMatches(Window, _region.Center, _region);
            bool sourceHeld = _sourceBefore is not null
                && _owner.WindowStillMatches(Window, _region.Center, containedRegion: null);
            return FinishAsync(Window, pageHeld, sourceHeld);
        }

        private async Task<CaptureSelectionSource> FinishAsync(WindowUnderCursor window, bool pageHeld, bool sourceHeld)
        {
            // Once the frames have ended, nothing else touches this state.
            bool page = pageHeld && Observe(await _read!.ConfigureAwait(false));
            WindowSourceSnapshot? sourceFirst = _sourceBefore is null ? null : await _sourceBefore.ConfigureAwait(false);
            // The page read that confirms the last frame must have started after it.
            bool confirmPage = page && _readStartedAfterFrames < _frames;
            Task<BrowserDocumentSnapshot?> pageAfter = confirmPage
                && _owner.WindowStillMatches(window, _region.Center, _region)
                ? _owner.ReadDocumentAsync(window) : NoDocument;
            Task<WindowSourceSnapshot?> sourceAfter = sourceHeld && sourceFirst is not null
                && _owner.WindowStillMatches(window, _region.Center, containedRegion: null)
                ? _owner.ReadSourceAsync(window) : NoSource;
            BrowserDocumentSnapshot? pageConfirmed = await pageAfter.ConfigureAwait(false);
            WindowSourceSnapshot? sourceConfirmed = await sourceAfter.ConfigureAwait(false);
            if (confirmPage) page = Observe(pageConfirmed);
            return new CaptureSelectionSource(string.Empty,
                page ? SourcePageUrl.Normalize(_document?.Url) : string.Empty,
                StableSource(window, sourceFirst, sourceConfirmed));
        }

        /// <summary>
        /// Once any read disagrees, later navigation back to the original page cannot make a multi-frame
        /// image a reliable single-page capture again.
        /// </summary>
        private bool Observe(BrowserDocumentSnapshot? read)
        {
            _pageStable = _pageStable && read is not null && (_document is null || _document == read);
            if (_pageStable) _document = read;
            return _pageStable;
        }
    }

    /// <summary>
    /// Opens the capture. Reads that have already finished go in directly; otherwise the capture opens at
    /// once and its page and application follow when the reads end.
    /// </summary>
    private CaptureOutcome Open(AdvancedSelection selection, Task<CaptureSelectionSource>? source = null)
    {
        if (source is { IsCompletedSuccessfully: true } read)
        {
            selection = selection with { SourcePageUrl = read.Result.PageUrl, SourceDetails = read.Result.Details };
        }
        else if (source is not null)
        {
            selection = selection with { PendingSource = source };
        }

        return _environment.OpenEditor(selection)
            ? CaptureOutcome.Completed()
            : CaptureOutcome.Cancelled(UiText.Get("Text_F87FEA248BDD"));
    }

    private CaptureOutcome? BusyOutcome() =>
        _environment.CanOpenEditor
            ? null
            : CaptureOutcome.Cancelled(UiText.Get("Text_F87FEA248BDD"));
}
