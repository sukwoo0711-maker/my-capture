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

    public bool IsPinned => Record.IsManuallyPinned;
    public string SearchMatchText { get; private set; } = string.Empty;
    public bool HasSearchMatch => SearchMatchText.Length > 0;

    internal void SetSearchMatch(bool active, CaptureMatchField fields)
    {
        string text = active ? GallerySearchMatchText.Describe(fields) : string.Empty;
        if (SearchMatchText == text) return;
        SearchMatchText = text;
        Raise(nameof(SearchMatchText));
        Raise(nameof(HasSearchMatch));
    }

    internal Func<CaptureRecord, string>? FolderFileName { get; set; }

    private string _folderBadge = string.Empty;
    private string _folderPath = string.Empty;

    /// <summary>
    /// The folder this capture lives in, shown on its card only where a view lists it outside
    /// that folder: a search or the image, video and kept views. Empty while browsing.
    /// </summary>
    public string FolderBadge => _folderBadge;

    public bool HasFolderBadge => _folderBadge.Length > 0;

    /// <summary>Sets or clears the folder badge for the view being shown.</summary>
    internal void SetFolderBadge((string Name, string Path)? folder)
    {
        string name = folder?.Name ?? string.Empty;
        string path = folder?.Path ?? string.Empty;
        if (_folderBadge == name && _folderPath == path) return;
        _folderBadge = name;
        _folderPath = path;
        Raise(nameof(FolderBadge));
        Raise(nameof(HasFolderBadge));
        Raise(nameof(AccessibleName));
    }

    public bool HasAnnotations => Record.HasAnnotations;

    public bool IsVideo => Record.IsVideo;

    public bool IsImage => Record.IsImage;

    public string ActionLabel => IsVideo ? UiText.Get("Text_35383149904D") : UiText.Get("Text_87B0ACEF85A7");

    /// <summary>Dragging moves the capture onto a library folder, or exports a file outside the app.</summary>
    public string ExportToolTip => IsVideo
        ? UiText.Get("LibraryUx_TileDragVideo")
        : UiText.Get("LibraryUx_TileDragImage");

    public string PreviewUnavailableText => IsVideo
        ? UiText.Get("Text_FACB0016A977")
        : UiText.Get("Text_0D3A337CA053");

    public string DurationCaption => IsVideo ? FormatDuration(Record.DurationMs) : string.Empty;

    /// <summary>Primary caption line. Blank captures intentionally have no visible title.</summary>
    public string Caption =>
        !string.IsNullOrWhiteSpace(Record.Title) ? Record.Title
        : Record.FolderId is not null ? Path.GetFileNameWithoutExtension(FolderFileName?.Invoke(Record)
            ?? CaptureFolderNaming.SequenceName(CaptureFolderNaming.DefaultPrefix, Record.FolderSequence) + (IsVideo ? ".mp4" : ".png"))
        : !string.IsNullOrWhiteSpace(Record.SourceWindowTitle) ? Record.SourceWindowTitle
        : string.Empty;

    public string DetailDimensions => $"{Record.Width} \u00d7 {Record.Height}";

    public string TagsCaption => Record.Tags ?? string.Empty;

    public bool HasTags => !string.IsNullOrWhiteSpace(Record.Tags);

    public string SourcePageUrl => MyCapture.Core.Capture.SourcePageUrl.Normalize(Record.SourcePageUrl);

    public bool HasSourcePageUrl => SourcePageUrl.Length > 0;

    /// <summary>The whole address with its label, read by screen readers whatever the summary shows.</summary>
    public string SourcePageUrlAutomationName =>
        HasSourcePageUrl ? UiText.Format("Library.SourcePage.Accessible", SourcePageUrl) : string.Empty;

    /// <summary>
    /// Whether the details panel shows a source section. Videos have no capture-time source; their
    /// stored window title is the generic recording label, not a window.
    /// </summary>
    public bool HasSource => Record.ImportedFileName is not null
        || IsImage && (HasSourceApp || HasSourceWindowTitle || HasSourcePageUrl || HasSourceDocumentPath);

    public string SourceAppName => Record.ImportedFileName is not null ? UiText.Get("Library.Import.Source")
        : IsImage ? Record.SourceDetails.AppName : string.Empty;

    public bool HasSourceApp => SourceAppName.Length > 0;

    /// <summary>Executable path, offered as the application line's tooltip.</summary>
    public string SourceAppPath => IsImage ? Record.SourceDetails.AppPath : string.Empty;

    /// <summary>Null rather than empty, so a record without a path shows no empty tooltip.</summary>
    public string? SourceAppToolTip => SourceAppPath.Length > 0 ? SourceAppPath : null;

    public string SourceAppAutomationName => HasSourceApp ? UiText.Format("Library.Source.AppAccessible", SourceAppName) : string.Empty;

    public string SourceWindowTitle => Record.ImportedFileName
        ?? (IsImage ? (Record.SourceWindowTitle ?? string.Empty).Trim() : string.Empty);

    public bool HasSourceWindowTitle => SourceWindowTitle.Length > 0;

    public string SourceWindowTitleAutomationName =>
        HasSourceWindowTitle ? UiText.Format("Library.Source.WindowAccessible", SourceWindowTitle) : string.Empty;

    /// <summary>The folder or file the window showed. A page link, when present, is the location shown instead.</summary>
    public string SourceDocumentPath => IsImage && !HasSourcePageUrl ? Record.SourceDetails.DocumentPath : string.Empty;

    public bool HasSourceDocumentPath => SourceDocumentPath.Length > 0;

    /// <summary>The whole path with its label, read by screen readers whatever the summary shows.</summary>
    public string SourceDocumentPathAutomationName =>
        HasSourceDocumentPath ? UiText.Format("Library.Source.LocationAccessible", SourceDocumentPath) : string.Empty;

    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

    /// <summary>The single line under a compact card: its caption, else the capture time.</summary>
    public string CompactCaption => HasCaption ? Caption : TimeCaption;

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
            string pin = IsPinned ? UiText.Get("LibraryUx_KeptAccessible") : string.Empty;
            string media = IsVideo ? UiText.Format("Text_DFB3664A9A08", DurationCaption) : UiText.Get("Text_AA3D7105289A");
            string folder = HasFolderBadge ? UiText.Format("LibraryUx_InFolderAccessible", _folderPath) : string.Empty;
            return $"{ContextLabel}{media}, {Record.Width}×{Record.Height}, {time}{pin}{folder}";
        }
    }

    internal Func<int> RetentionHours { get; set; } = static () => CaptureRetention.DefaultImageRetentionHours;

    internal Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.Now;

    public bool IsExpiringSoon => IsImage && !IsPinned && Record.FolderId is null && CaptureRetention.Remaining(
        Record.RetentionReference, CaptureRetention.TimeToLive(new QueueSettings { ImageRetentionHours = RetentionHours() }),
        Clock()) < TimeSpan.FromHours(24);

    /// <summary>TTL countdown matching the configured gallery retention.</summary>
    public string RetentionCaption => CaptureRetention.FormatCountdown(
        Record.RetentionReference,
        CaptureRetention.TimeToLive(new QueueSettings { ImageRetentionHours = RetentionHours() }),
        Clock(),
        IsImage,
        IsPinned,
        Record.FolderId is not null);

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
        Raise(nameof(CompactCaption));
        Raise(nameof(ContextLabel));
        Raise(nameof(AccessibleName));
        Raise(nameof(RetentionCaption));
        Raise(nameof(IsExpiringSoon));
    }

    /// <summary>Notifies the view that pin/meta changed without re-decoding the image.</summary>
    public void RaiseMetaChanged()
    {
        Raise(nameof(Caption));
        Raise(nameof(HasCaption));
        Raise(nameof(CompactCaption));
        Raise(nameof(ContextLabel));
        Raise(nameof(TagsCaption));
        Raise(nameof(HasTags));
        Raise(nameof(SourcePageUrl));
        Raise(nameof(HasSourcePageUrl));
        Raise(nameof(SourcePageUrlAutomationName));
        Raise(nameof(HasSource));
        Raise(nameof(SourceAppName));
        Raise(nameof(HasSourceApp));
        Raise(nameof(SourceAppPath));
        Raise(nameof(SourceAppToolTip));
        Raise(nameof(SourceAppAutomationName));
        Raise(nameof(SourceWindowTitle));
        Raise(nameof(HasSourceWindowTitle));
        Raise(nameof(SourceWindowTitleAutomationName));
        Raise(nameof(SourceDocumentPath));
        Raise(nameof(HasSourceDocumentPath));
        Raise(nameof(SourceDocumentPathAutomationName));
        Raise(nameof(IsPinned));
        Raise(nameof(AccessibleName));
        Raise(nameof(RetentionCaption));
        Raise(nameof(IsExpiringSoon));
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
