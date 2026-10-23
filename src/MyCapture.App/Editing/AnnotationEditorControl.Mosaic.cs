using System.Collections.Specialized;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;

namespace MyCapture.App.Editing;

/// <summary>A non-rendering, non-selectable document slot reserved by an unfinished gesture.</summary>
internal sealed class PendingMosaicAnnotation : AnnotationItem
{
    public override string DisplayName => string.Empty;
    public override RectD Bounds => default;
    public override bool SupportsResize => false;
    public override void Translate(double dx, double dy) { }
    public override void SetBounds(RectD bounds) { }
    public override double DistanceTo(PointD point) => double.PositiveInfinity;
    public override AnnotationItem Clone()
    {
        var clone = new PendingMosaicAnnotation();
        CopyBaseTo(clone);
        return clone;
    }
}

internal sealed partial class AnnotationEditorControl
{
    private readonly List<MosaicRequest> _mosaicRequests = [];
    private CancellationTokenSource? _mosaicLifetime;
    private Task _mosaicTail = Task.CompletedTask;
    private bool _mosaicClosed;
    private bool _mosaicPermanentlyClosed;
    private bool _mosaicAbandoned;
    private bool _mosaicObservingDocument;
    private bool _replacingMosaicSlot;

    internal bool HasMosaicWork => _mosaicRequests.Any(request => request.Active);
    internal bool HasUnsettledMosaicWork => _mosaicRequests.Any(request => request.Active && !request.Settled.Task.IsCompleted);
    internal bool MosaicsReadyForExport => !_mosaicAbandoned && !HasMosaicWork;

    /// <summary>Immutable input; pixel copying and averaging both happen on the worker.</summary>
    internal sealed record MosaicInput(BitmapSource Bitmap, RectD Region, int BlockSize);

    private sealed class MosaicRequest(AnnotationEditorControl owner, MosaicInput input, BitmapSource displayedBase,
        int turns, PointD cropOrigin, CancellationTokenSource cancellation) : IUndoableCommand
    {
        internal MosaicInput? Input { get; private set; } = input;
        internal BitmapSource? DisplayedBase { get; private set; } = displayedBase;
        internal RectD Region { get; } = input.Region;
        internal int Turns { get; } = turns;
        internal PointD CropOrigin { get; } = cropOrigin;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal CancellationToken Token { get; } = cancellation.Token;
        internal TaskCompletionSource Settled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ImageAnnotation Image { get; } = new()
        {
            AssetFileName = string.Empty,
            SourceWidth = (int)input.Region.Width,
            SourceHeight = (int)input.Region.Height,
            Rect = input.Region,
        };
        internal PendingMosaicAnnotation Placeholder { get; } = new();
        internal int InsertIndex { get; set; } = -1;
        internal bool Active { get; private set; }
        internal bool Computed { get; set; }
        internal bool Removed { get; set; }
        internal bool Invalidated { get; set; }

        public string Description => UiText.Format("Text_47E781C43156", Image.DisplayName);
        public IEnumerable<string> ReferencedImageAssets => string.IsNullOrEmpty(Image.AssetFileName) ? [] : [Image.AssetFileName];

        internal void ReleaseSourceReferences()
        {
            Input = null;
            DisplayedBase = null;
        }

        public void Execute()
        {
            if (Removed || owner._mosaicPermanentlyClosed) return;
            Active = true;
            if (!Computed && Settled.Task.IsCompleted)
                Settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AnnotationDocument document = owner._controller.Document;
            AnnotationItem item = Computed && !Invalidated ? Image : Placeholder;
            if (InsertIndex < 0)
            {
                document.Add(item);
                InsertIndex = document.Items.IndexOf(item);
            }
            else document.Insert(InsertIndex, item);
            owner.RefreshMosaicDraft();
        }

        public void Undo()
        {
            Active = false;
            owner._controller.Document.Remove(Image);
            owner._controller.Document.Remove(Placeholder);
            // The private worker may finish for a later Redo; an explicitly undone gesture
            // no longer holds a save/render that was waiting for its visible result.
            Settled.TrySetResult();
            owner.RefreshMosaicDraft();
        }
    }

    /// <summary>Tests gate the real worker without relying on a processing duration.</summary>
    internal Func<MosaicInput, CancellationToken, Task<BitmapSource>> ComputeMosaic { get; set; } =
        static (input, token) => Task.FromResult(BuildMosaicBitmap(input, token));

    private void QueueMosaic(RectD region, int blockSize)
    {
        Dispatcher.VerifyAccess();
        if (_completed || _mosaicClosed || _commitInProgress) return;
        RectD normalized = region.Normalized().ClampTo(new RectD(0, 0, _canvasWidth, _canvasHeight));
        var pixels = new RectD(Math.Floor(normalized.X), Math.Floor(normalized.Y),
            Math.Ceiling(normalized.Width), Math.Ceiling(normalized.Height));
        if (pixels.Width < 2 || pixels.Height < 2) return;

        BitmapSource displayedBase = _surface.Frame.Bitmap;
        // Most bases are already frozen. Mutable inputs require one detached UI-thread copy,
        // otherwise their later mutation would change what this completed gesture sampled.
        BitmapSource frozen = displayedBase.IsFrozen ? displayedBase : CopyToIndependentBitmap(displayedBase);
        var input = new MosaicInput(frozen, pixels,
            Math.Clamp(blockSize, SettingsRanges.MosaicBlockSize.Min, SettingsRanges.MosaicBlockSize.Max));
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            (_mosaicLifetime ??= new CancellationTokenSource()).Token);
        // Reserve both history and a real document slot now. Commands added while the worker
        // runs then record the same insertion indices that they will need after Undo/Redo.
        var request = new MosaicRequest(this, input, displayedBase, _controller.RotationTurns,
            _controller.CropOrigin, cancellation);
        _mosaicRequests.Add(request);
        if (!_mosaicObservingDocument)
        {
            _controller.Document.ItemsChanged += OnMosaicItemsChanged;
            _mosaicObservingDocument = true;
        }
        SelectTool(EditorTool.Select);
        _controller.Undo.Execute(request);
        RefreshMosaicDraft();
        RefreshHistoryButtons();
        DraftChanged?.Invoke(this, EventArgs.Empty);
        // The predecessor includes its UI insertion. Completion speed cannot reorder patches.
        Task previous = _mosaicTail;
        _mosaicTail = RunMosaicAsync(previous, request, ComputeMosaic);
    }

    private async Task RunMosaicAsync(Task previous, MosaicRequest request,
        Func<MosaicInput, CancellationToken, Task<BitmapSource>> compute)
    {
        BitmapSource? bitmap = null;
        Exception? failure = null;
        try
        {
            await previous.WaitAsync(request.Token).ConfigureAwait(false);
            MosaicInput input = request.Input!;
            bitmap = await Task.Run(() => compute(input, request.Token), request.Token)
                .WaitAsync(request.Token).ConfigureAwait(false);
            if (!bitmap.IsFrozen) throw new InvalidOperationException("Mosaic worker returned an unfrozen bitmap.");
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }

        try
        {
            await Dispatcher.InvokeAsync(() => CompleteMosaic(request, bitmap, failure));
        }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted)
        {
            request.Settled.TrySetResult();
        }
        catch (Exception ex)
        {
            // A view or draft subscriber may fail after the result was handled. It must not
            // strand this request or prevent a later queued gesture from being computed.
            System.Diagnostics.Trace.TraceWarning("Mosaic completion notification failed: {0}", ex);
        }
        finally
        {
            request.Settled.TrySetResult();
            request.ReleaseSourceReferences();
            request.Cancellation.Dispose();
        }
    }

    private bool MosaicBaseMatches(MosaicRequest request) =>
        ReferenceEquals(request.DisplayedBase, _surface.Frame.Bitmap)
        && request.Input is { } input && input.Bitmap.PixelWidth == _canvasWidth && input.Bitmap.PixelHeight == _canvasHeight
        && request.Turns == _controller.RotationTurns && request.CropOrigin == _controller.CropOrigin;

    private void CompleteMosaic(MosaicRequest request, BitmapSource? bitmap, Exception? failure)
    {
        Dispatcher.VerifyAccess();
        request.Computed = true;
        try
        {
            if (request.Removed || _mosaicClosed || _completed) return;
            if (failure is not null)
                System.Diagnostics.Trace.TraceWarning("Mosaic processing failed: {0}", failure);
            if (bitmap is null || request.Invalidated || !MosaicBaseMatches(request))
            {
                request.Invalidated = true;
                SetStatus(UiText.Get("Text_06F58809C083"));
                return;
            }

            (BitmapSource Bitmap, string AssetFileName) asset = _imageStore.RegisterInMemory(bitmap);
            request.Image.AssetFileName = asset.AssetFileName;
            request.Image.ZIndex = request.Placeholder.ZIndex;
            if (request.Active)
            {
                ReplaceMosaicSlot(request);
                _documentRevision++;
                _autoTrimCts?.Cancel();
            }
            if (request.Active && _controller.Tool == EditorTool.Select && _controller.Selected is null
                && !_mosaicDragging && !_viewport.IsMouseCaptured && _activeTextBox is null)
                _controller.SetSelected(request.Image);
            if (request.Active && !_commitInProgress) SetStatus(UiText.Get("Text_3F8046019B34"));
            _mosaicRequests.Remove(request);
        }
        catch (Exception ex)
        {
            // A failed insertion must be just as fail-closed as a failed pixel computation.
            request.Invalidated = true;
            System.Diagnostics.Trace.TraceWarning("Mosaic insertion failed: {0}", ex);
            SetStatus(UiText.Get("Text_06F58809C083"));
        }
        finally
        {
            try
            {
                RefreshMosaicDraft();
                RefreshHistoryButtons();
                DraftChanged?.Invoke(this, EventArgs.Empty);
            }
            finally { request.Settled.TrySetResult(); }
        }
    }

    private void ReplaceMosaicSlot(MosaicRequest request)
    {
        AnnotationDocument document = _controller.Document;
        int index = document.Items.IndexOf(request.Placeholder);
        if (index < 0) throw new InvalidOperationException("The reserved mosaic slot is missing.");
        request.Image.ZIndex = request.Placeholder.ZIndex;
        _replacingMosaicSlot = true;
        try
        {
            document.Items[index] = request.Image;
        }
        finally { _replacingMosaicSlot = false; }
    }

    private void OnMosaicItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_replacingMosaicSlot || _mosaicRequests.Count == 0
            || e.Action is not (NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Reset)) return;
        // Moving existing slots preserves paint order. Replacing or resetting them breaks
        // their identity, so an unfinished gesture must remain blocked until explicit Undo.
        foreach (MosaicRequest request in _mosaicRequests)
        {
            request.Invalidated = true;
            if (!request.Computed) request.Cancellation.Cancel();
        }
        RefreshMosaicDraft();
    }

    private void ForgetDiscardedMosaics()
    {
        if (_mosaicRequests.Count == 0 || _controller.Undo.RedoCount != 0) return;
        bool changed = false;
        foreach (MosaicRequest request in _mosaicRequests.Where(request => !request.Active).ToArray())
        {
            // A new edit discarded the redo branch containing this unfinished/failed command.
            request.Removed = true;
            _mosaicRequests.Remove(request);
            changed = true;
            if (!request.Computed) request.Cancellation.Cancel();
        }
        if (changed) RefreshMosaicDraft();
    }

    private bool TryUndoUnretainedMosaic()
    {
        if (_controller.CanUndo || _commitInProgress) return false;
        MosaicRequest? request = _mosaicRequests.LastOrDefault(request => request.Active);
        if (request is null) return false;
        // The history depth limit may have evicted this command while its worker was pending.
        // Only after every retained command is undone may Undo explicitly remove that intent.
        // Its evicted command cannot be redone; the newer, retained redo branch remains intact.
        request.Undo();
        request.Removed = true;
        _mosaicRequests.Remove(request);
        if (!request.Computed) request.Cancellation.Cancel();
        _documentRevision++;
        _autoTrimCts?.Cancel();
        RefreshMosaicDraft();
        RefreshHistoryButtons();
        DraftChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Called after the displayed base is replaced, including geometry restoration.</summary>
    private void InvalidateIncompatibleMosaics()
    {
        foreach (MosaicRequest request in _mosaicRequests)
        {
            if (request.Invalidated || MosaicBaseMatches(request)) continue;
            request.Invalidated = true;
            if (!request.Computed) request.Cancellation.Cancel();
        }
        RefreshMosaicDraft();
    }

    internal Task MosaicOperationsSettledAsync()
    {
        Dispatcher.VerifyAccess();
        return Task.WhenAll(_mosaicRequests.Where(request => request.Active).Select(request => request.Settled.Task));
    }

    private void EnsureMosaicsReadyForSnapshot()
    {
        if (!MosaicsReadyForExport)
            throw UserFacingError.Mark(new InvalidOperationException(UiText.Get("Text_06F58809C083")));
    }

    private async Task<BitmapSource> RenderCurrentImageAfterMosaicsAsync()
    {
        await MosaicOperationsSettledAsync().ConfigureAwait(false);
        return await Dispatcher.InvokeAsync(RenderCurrentImageAsync).Task.Unwrap().ConfigureAwait(false);
    }

    private async Task CommitAfterMosaicsAsync(EditorCommitAction action, bool reduceExport)
    {
        CancelFrozenPointerGesture();
        if (_activeTextBox is not null) CommitActiveText();
        _commitInProgress = true;
        IsHitTestVisible = false;
        RectD? framed = _crop?.Region;
        await MosaicOperationsSettledAsync().ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _commitInProgress = false;
                IsHitTestVisible = true;
                if (_completed || _mosaicClosed) return;
                if (!MosaicsReadyForExport)
                {
                    SetStatus(UiText.Get("Text_06F58809C083"));
                    return;
                }
                if (framed is RectD region && _crop is { } crop && crop.Region != region)
                {
                    crop.SetRegion(region);
                    RefreshCrop();
                }
                Commit(action, reduceExport);
            });
        }
        catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted) { }
    }

    private void ResetMosaicAfterLoad()
    {
        if (!_mosaicPermanentlyClosed) _mosaicClosed = false;
    }

    internal void CancelPendingMosaics(bool closing = false)
    {
        Dispatcher.VerifyAccess();
        _mosaicClosed = true;
        _mosaicPermanentlyClosed |= closing;
        if (_mosaicPermanentlyClosed)
        {
            _mosaicAbandoned |= HasMosaicWork;
            foreach (MosaicRequest request in _mosaicRequests) request.Removed = true;
            _mosaicRequests.Clear();
            if (_mosaicObservingDocument)
            {
                _controller.Document.ItemsChanged -= OnMosaicItemsChanged;
                _mosaicObservingDocument = false;
            }
        }
        else
        {
            // A hidden/reparented editor may load again. Keep canceled gestures available
            // for explicit Undo then, instead of silently allowing an uncovered export.
            foreach (MosaicRequest request in _mosaicRequests) request.Invalidated = true;
        }
        _mosaicLifetime?.Cancel();
        _mosaicLifetime?.Dispose();
        _mosaicLifetime = null;
        _surface.MosaicGestureRegion = null;
        RefreshMosaicDraft();
    }

    private void RefreshMosaicDraft()
    {
        _surface.PendingMosaicRegions = _mosaicRequests.Where(request => request.Active && !request.Invalidated)
            .Select(request => request.Region).ToArray();
        _surface.InvalidateVisual();
    }

    private void UpdateMosaicGesture(PointD end)
    {
        _surface.MosaicGestureRegion = _mosaicDragging
            ? new RectD(_mosaicStart.X, _mosaicStart.Y, end.X - _mosaicStart.X, end.Y - _mosaicStart.Y)
                .Normalized().ClampTo(new RectD(0, 0, _canvasWidth, _canvasHeight))
            : null;
        _surface.InvalidateVisual();
    }

    private static BitmapSource BuildMosaicBitmap(MosaicInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int x = (int)input.Region.X, y = (int)input.Region.Y;
        int width = (int)input.Region.Width, height = (int)input.Region.Height, step = input.BlockSize;
        byte[] source = ReadMosaicSource(input.Bitmap, x, y, width, height, out int stride, out int originX, out int originY);
        int patchStride = width * 4;
        byte[] patch = new byte[patchStride * height];
        for (int by = 0; by < height; by += step)
        {
            token.ThrowIfCancellationRequested();
            int blockH = Math.Min(step, height - by);
            for (int bx = 0; bx < width; bx += step)
            {
                int blockW = Math.Min(step, width - bx);
                long sumB = 0, sumG = 0, sumR = 0, count = 0;
                for (int py = 0; py < blockH; py++)
                {
                    int row = (y + by + py - originY) * stride;
                    for (int px = 0; px < blockW; px++)
                    {
                        int i = row + (x + bx + px - originX) * 4;
                        if (source[i + 3] == 0) continue;
                        sumB += source[i]; sumG += source[i + 1]; sumR += source[i + 2]; count++;
                    }
                }
                if (count == 0) continue;
                byte b = (byte)(sumB / count), g = (byte)(sumG / count), r = (byte)(sumR / count);
                for (int py = 0; py < blockH; py++)
                    for (int px = 0; px < blockW; px++)
                    {
                        int i = (by + py) * patchStride + (bx + px) * 4;
                        patch[i] = b; patch[i + 1] = g; patch[i + 2] = r; patch[i + 3] = 255;
                    }
            }
        }
        token.ThrowIfCancellationRequested();
        BitmapSource decoded = BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, patch, patchStride);
        decoded.Freeze();
        return decoded;
    }
}
