using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MyCapture.Core.Queue;
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

    public bool IsPinned => Record.IsPinned;

    public bool HasAnnotations => Record.HasAnnotations;

    public bool IsVideo => Record.IsVideo;

    public bool IsImage => Record.IsImage;

    public string ActionLabel => IsVideo ? "영상 편집" : "편집";

    public string ExportToolTip => IsVideo
        ? "폴더나 바탕화면으로 드래그해 MP4 파일로 내보내기"
        : "폴더나 바탕화면으로 드래그해 PNG 파일로 내보내기";

    public string PreviewUnavailableText => IsVideo
        ? "동영상 미리보기를 읽을 수 없음"
        : "이미지를 읽을 수 없음";

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
        : IsVideo ? $"동영상 {TimeCaption}" : $"캡처 {TimeCaption}";

    /// <summary>Accessible, human-readable label for the tile without an “untitled” phrase.</summary>
    public string AccessibleName
    {
        get
        {
            string time = Record.CreatedAt.DateTime.ToString("yyyy-MM-dd HH:mm");
            string pin = Record.IsPinned ? ", 고정됨" : string.Empty;
            string media = IsVideo ? $", 동영상 {DurationCaption}" : ", 이미지";
            return $"{ContextLabel}{media}, {Record.Width}×{Record.Height}, {time}{pin}";
        }
    }

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
            EnsureThumbnail();
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
        _thumbnailRequested = false;
        _thumbnail = null;
        _isBroken = false;
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
    }

    /// <summary>Notifies the view that pin/meta changed without re-decoding the image.</summary>
    public void RaiseMetaChanged()
    {
        Raise(nameof(IsPinned));
        Raise(nameof(AccessibleName));
    }

    private void EnsureThumbnail()
    {
        if (_thumbnailRequested)
        {
            return;
        }

        _thumbnailRequested = true;

        string path = _thumbnailPathResolver(Record);
        BitmapSource? decoded = SafeLoad(path);
        if (decoded is null)
        {
            _isBroken = true;
            return;
        }

        _thumbnail = decoded;
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
