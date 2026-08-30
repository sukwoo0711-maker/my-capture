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

    /// <summary>Primary caption line. Blank captures intentionally have no visible title.</summary>
    public string Caption =>
        !string.IsNullOrWhiteSpace(Record.Title) ? Record.Title
        : !string.IsNullOrWhiteSpace(Record.SourceWindowTitle) ? Record.SourceWindowTitle
        : string.Empty;

    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

    /// <summary>Non-empty label for confirmations, OCR windows and other contextual UI.</summary>
    public string ContextLabel => HasCaption ? Caption : $"캡처 {TimeCaption}";

    /// <summary>Accessible, human-readable label for the tile without an “untitled” phrase.</summary>
    public string AccessibleName
    {
        get
        {
            string time = Record.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            string pin = Record.IsPinned ? ", 고정됨" : string.Empty;
            return $"{ContextLabel}, {Record.Width}×{Record.Height}, {time}{pin}";
        }
    }

    /// <summary>Secondary caption line: capture time.</summary>
    public string TimeCaption => Record.CreatedAt.LocalDateTime.ToString("HH:mm");

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
}
