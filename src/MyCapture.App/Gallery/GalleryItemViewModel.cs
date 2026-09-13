using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

/// <summary>
/// One gallery tile: a capture record plus the lazily-decoded thumbnail bound to its image.
/// </summary>
/// <remarks>
/// <para>
/// The thumbnail is decoded on demand (when the tile is realised by the virtualizing panel)
/// rather than eagerly for every record, and at a bounded pixel width via
/// <see cref="ImageCodec.TryLoadScaled"/>, so scrolling a 300-item queue never materialises
/// 300 full-resolution frames. The decoded bitmap is frozen so it is safe to reuse and cheap
/// to hand around.
/// </para>
/// <para>
/// A missing or corrupt <c>thumb.jpg</c> sets <see cref="IsBroken"/> instead of throwing, so
/// the tile shows a "손상됨" placeholder rather than crashing the gallery — the queue can
/// outlive hand-editing of its folder.
/// </para>
/// </remarks>
public sealed class GalleryItemViewModel : INotifyPropertyChanged
{
    private readonly Func<CaptureRecord, string> _thumbnailPathResolver;
    private readonly int _decodePixelWidth;

    private BitmapSource? _thumbnail;
    private bool _isBroken;
    private bool _thumbnailRequested;
    private bool _thumbnailLoadingEnabled = true;
    private GalleryThumbnailLoader? _loader;
    private Dispatcher? _dispatcher;
    private CancellationTokenSource? _loadCancellation;
    private int _generation;
    internal Task PendingThumbnailLoad { get; private set; } = Task.CompletedTask;

    internal void ConfigureAsyncLoading(GalleryThumbnailLoader loader, Dispatcher dispatcher)
    {
        ReleaseThumbnail();
        _loader = loader;
        _dispatcher = dispatcher;
    }
    internal Action<GalleryItemViewModel>? ThumbnailAccessed { get; set; }
    internal long CachedThumbnailBytes => _thumbnail is null ? 0
        : (long)_thumbnail.PixelWidth * _thumbnail.PixelHeight * ((_thumbnail.Format.BitsPerPixel + 7) / 8);

    internal void ReleaseThumbnail()
    {
        _generation++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _thumbnail = null;
        _thumbnailRequested = false;
        _isBroken = false;
    }

    internal void SetThumbnailLoadingEnabled(bool enabled)
    {
        _thumbnailLoadingEnabled = enabled;
        if (!enabled) ReleaseThumbnail();
        Raise(nameof(Thumbnail));
        Raise(nameof(IsBroken));
    }

    public GalleryItemViewModel(
        CaptureRecord record,
        Func<CaptureRecord, string> thumbnailPathResolver,
        int decodePixelWidth)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        _thumbnailPathResolver = thumbnailPathResolver ?? throw new ArgumentNullException(nameof(thumbnailPathResolver));
        _decodePixelWidth = Math.Max(1, decodePixelWidth);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureRecord Record { get; }

    public Guid Id => Record.Id;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        internal set { if (_isSelected != value) { _isSelected = value; Raise(); } }
    }

    public bool IsPinned => Record.IsPinned;

    public bool HasAnnotations => Record.HasAnnotations;

    public bool IsVideo => Record.IsVideo;

    public bool IsImage => Record.IsImage;

    public string ActionLabel => IsVideo ? UiText.Get("Text_35383149904D") : UiText.Get("Text_87B0ACEF85A7");

    public string ExportToolTip => IsVideo
        ? UiText.Get("Text_E11F2F71F169")
        : UiText.Get("Text_1A8F33AE9F00");

    public string PreviewUnavailableText => IsVideo
        ? UiText.Get("Text_FACB0016A977")
        : UiText.Get("Text_0D3A337CA053");

    public string DurationCaption => IsVideo ? FormatDuration(Record.DurationMs) : string.Empty;

    /// <summary>Primary caption line. Blank captures intentionally have no visible title.</summary>
    public string Caption =>
        !string.IsNullOrWhiteSpace(Record.Title) ? Record.Title
        : !string.IsNullOrWhiteSpace(Record.SourceWindowTitle) ? Record.SourceWindowTitle
        : string.Empty;

    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

    /// <summary>Non-empty label for confirmations, OCR windows and other contextual UI.</summary>
    public string ContextLabel => HasCaption
        ? Caption
        : IsVideo ? UiText.Format("Text_32512F9EA32E", TimeCaption) : UiText.Format("Text_16DBAB47AC29", TimeCaption);

    /// <summary>Accessible, human-readable label for the tile without an “untitled” phrase.</summary>
    public string AccessibleName
    {
        get
        {
            string time = Record.CreatedAt.DateTime.ToString("yyyy-MM-dd HH:mm");
            string pin = Record.IsPinned ? UiText.Get("Text_E485D788C2BE") : string.Empty;
            string media = IsVideo ? UiText.Format("Text_DFB3664A9A08", DurationCaption) : UiText.Get("Text_AA3D7105289A");
            return $"{ContextLabel}{media}, {Record.Width}×{Record.Height}, {time}{pin}";
        }
    }

    internal Func<int> RetentionHours { get; set; } = static () => CaptureRetention.DefaultImageRetentionHours;

    internal Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.Now;

    /// <summary>TTL countdown matching the configured gallery retention.</summary>
    public string RetentionCaption => CaptureRetention.FormatCountdown(
        Record.CreatedAt,
        CaptureRetention.TimeToLive(new QueueSettings { ImageRetentionHours = RetentionHours() }),
        Clock(),
        IsImage,
        IsPinned);

    /// <summary>Secondary caption line: capture time.</summary>
    // Keep the wall-clock time captured with the record's stored offset. LocalDateTime
    // would reinterpret it through the current machine time zone and could change the
    // label after travel, restore, or execution on a UTC host.
    public string TimeCaption => Record.CreatedAt.DateTime.ToString("HH:mm");

    /// <summary>The decoded thumbnail, or <see langword="null"/> until requested / when broken.</summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            bool wasCached = _thumbnail is not null;
            EnsureThumbnail();
            if (wasCached && _thumbnail is not null) ThumbnailAccessed?.Invoke(this);
            return _thumbnail;
        }
    }

    public bool IsBroken
    {
        get
        {
            EnsureThumbnail();
            return _isBroken;
        }
    }

    /// <summary>
    /// Re-decodes the thumbnail from disk, used after a re-edit commit regenerates it.
    /// </summary>
    public void RefreshThumbnail()
    {
        ReleaseThumbnail();
        EnsureThumbnail();
        Raise(nameof(Thumbnail));
        Raise(nameof(IsBroken));
        Raise(nameof(IsPinned));
        Raise(nameof(HasAnnotations));
        Raise(nameof(IsVideo));
        Raise(nameof(IsImage));
        Raise(nameof(ActionLabel));
        Raise(nameof(ExportToolTip));
        Raise(nameof(PreviewUnavailableText));
        Raise(nameof(DurationCaption));
        Raise(nameof(Caption));
        Raise(nameof(HasCaption));
        Raise(nameof(ContextLabel));
        Raise(nameof(AccessibleName));
        Raise(nameof(RetentionCaption));
    }

    /// <summary>Notifies the view that pin/meta changed without re-decoding the image.</summary>
    public void RaiseMetaChanged()
    {
        Raise(nameof(IsPinned));
        Raise(nameof(AccessibleName));
        Raise(nameof(RetentionCaption));
    }

    private void EnsureThumbnail()
    {
        if (!_thumbnailLoadingEnabled || _thumbnailRequested)
        {
            return;
        }

        _thumbnailRequested = true;

        string path = _thumbnailPathResolver(Record);
        if (_loader is not null && _dispatcher is not null)
        {
            var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            PendingThumbnailLoad = LoadAndPublishAsync(path, _generation, cancellation.Token);
            return;
        }
        BitmapSource? decoded = SafeLoad(path);
        if (decoded is null)
        {
            _isBroken = true;
            return;
        }

        _thumbnail = decoded;
        ThumbnailAccessed?.Invoke(this);
    }

    private async Task LoadAndPublishAsync(string path, int generation, CancellationToken cancellationToken)
    {
        BitmapSource? decoded;
        try
        {
            decoded = await _loader!.LoadAsync(path, _decodePixelWidth, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            decoded = null;
        }
        Dispatcher dispatcher = _dispatcher!;
        if (dispatcher.HasShutdownStarted || cancellationToken.IsCancellationRequested) return;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation || !_thumbnailLoadingEnabled || cancellationToken.IsCancellationRequested) return;
                _thumbnail = decoded;
                _isBroken = decoded is null;
                if (decoded is not null) ThumbnailAccessed?.Invoke(this);
                Raise(nameof(Thumbnail));
                Raise(nameof(IsBroken));
            }, DispatcherPriority.Background);
        }
        catch (TaskCanceledException) { }
    }

    private BitmapSource? SafeLoad(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            // OnLoad + DecodePixelWidth via ImageCodec: the file is never left locked and the
            // full frame is never materialised for a tile.
            return ImageCodec.TryLoadScaled(path, _decodePixelWidth);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatDuration(double durationMs)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(0, durationMs));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
    }
}
