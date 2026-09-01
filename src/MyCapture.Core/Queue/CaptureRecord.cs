using System.Text.Json.Serialization;

namespace MyCapture.Core.Queue;

/// <summary>The durable media carried by a gallery record.</summary>
public enum CaptureMediaKind
{
    /// <summary>A still image with an editable annotation document.</summary>
    Image = 0,

    /// <summary>An MP4 screen recording with an optional generated preview image.</summary>
    Video = 1,
}

/// <summary>
/// Metadata for one retained capture.
/// </summary>
/// <remarks>
/// <para>
/// Held in memory for the whole session and persisted as one entry in
/// <c>index.json</c>. Pixels live in files next to it; this record only describes
/// them.
/// </para>
/// <para>
/// <see cref="TotalBytes"/> is stored rather than computed from the filesystem on
/// demand. Enforcing a byte cap by stat-ing several hundred files on every capture
/// would put disk latency directly in the path of the capture hotkey, which is the
/// one operation that has to feel instant.
/// </para>
/// </remarks>
public sealed class CaptureRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Media stored by this record. The zero value deliberately remains <see cref="CaptureMediaKind.Image"/>
    /// so indexes written before video-gallery support deserialize without migration.
    /// </summary>
    public CaptureMediaKind MediaKind { get; set; } = CaptureMediaKind.Image;

    /// <summary>
    /// Monotonic revision of rendered pixels/layers/assets. Metadata-only changes such as pin,
    /// title, or cached OCR deliberately do not advance it, so they cannot invalidate an editor.
    /// </summary>
    public long ContentRevision { get; set; }

    /// <summary>Pixel width of the captured image.</summary>
    public int Width { get; set; }

    /// <summary>Pixel height of the captured image.</summary>
    public int Height { get; set; }

    /// <summary>Duration of a video record in milliseconds; zero for still images.</summary>
    public double DurationMs { get; set; }

    /// <summary>Encoded video frame rate; zero for still images.</summary>
    public int FrameRate { get; set; }

    /// <summary>Number of video frames written; zero for still images.</summary>
    public long FrameCount { get; set; }

    /// <summary>
    /// DPI scale of the monitor the capture came from, for example 1.5 at 150%.
    /// </summary>
    /// <remarks>
    /// Recorded so the editor can size annotation handles and default stroke widths
    /// in a way that looks the same to the user regardless of the source monitor's
    /// scaling. Without it, a 3px stroke drawn on a 200% display looks half as thick
    /// as the same stroke drawn on a 100% display.
    /// </remarks>
    public double DpiScale { get; set; } = 1.0;

    /// <summary>
    /// Device name of the source monitor, for diagnostics.
    /// </summary>
    public string SourceMonitor { get; set; } = string.Empty;

    /// <summary>
    /// Excluded from eviction regardless of age.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Total bytes of every file belonging to this capture.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Set when the annotation layer file exists and is non-empty.
    /// </summary>
    public bool HasAnnotations { get; set; }

    /// <summary>
    /// Cached OCR result, populated on first request.
    /// </summary>
    public string? OcrText { get; set; }

    /// <summary>
    /// Language actually used to produce <see cref="OcrText"/>.
    /// </summary>
    public string? OcrLanguage { get; set; }

    /// <summary>
    /// Content generation for which OCR completed, including a successful "no text" result.
    /// This prevents text-free captures from being reprocessed forever and makes the marker
    /// invalid automatically when rendered pixels advance to another generation.
    /// </summary>
    public long? OcrContentRevision { get; set; }

    /// <summary>
    /// Free-form user label, searchable in the gallery.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Title of the foreground window at capture time.
    /// </summary>
    /// <remarks>
    /// Captured automatically because it is the single most useful thing for finding
    /// a screenshot later: users remember which app they were in, not the timestamp.
    /// </remarks>
    public string SourceWindowTitle { get; set; } = string.Empty;

    /// <summary>
    /// Directory holding this capture's files, relative to the captures root.
    /// </summary>
    /// <remarks>
    /// Relative so the captures root can be relocated without rewriting the index.
    /// </remarks>
    public string RelativeDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public double AspectRatio => Height > 0 ? (double)Width / Height : 1.0;

    [JsonIgnore]
    public bool IsVideo => MediaKind == CaptureMediaKind.Video;

    [JsonIgnore]
    public bool IsImage => MediaKind == CaptureMediaKind.Image;

    [JsonIgnore]
    public bool HasOcrText => !string.IsNullOrWhiteSpace(OcrText);

    /// <summary>
    /// Whether OCR has completed for the current pixels. Legacy records that already carry
    /// text predate <see cref="OcrContentRevision"/> and are treated as indexed until edited.
    /// </summary>
    [JsonIgnore]
    public bool HasCurrentOcrIndex =>
        OcrContentRevision == ContentRevision || (OcrContentRevision is null && HasOcrText);

    /// <summary>
    /// Text used by gallery search.
    /// </summary>
    [JsonIgnore]
    public string SearchHaystack =>
        string.Join(
            ' ',
            Title,
            SourceWindowTitle,
            OcrText ?? string.Empty,
            IsVideo ? "동영상 비디오 video recording" : "이미지 스크린샷 image screenshot");
}

/// <summary>
/// Standard file names inside a capture directory.
/// </summary>
/// <remarks>
/// Fixed names rather than names stored per record: it keeps the index small, makes
/// a capture directory self-describing during manual inspection, and means a lost
/// index can be rebuilt by walking the tree.
/// </remarks>
public static class CaptureFileNames
{
    /// <summary>The unmodified capture.</summary>
    public const string Original = "original.png";

    /// <summary>The capture with annotations flattened onto it.</summary>
    public const string Rendered = "rendered.png";

    /// <summary>Editable annotation layer document.</summary>
    public const string Layers = "layers.json";

    /// <summary>Gallery thumbnail.</summary>
    public const string Thumbnail = "thumb.jpg";

    /// <summary>Per-capture metadata copy used to rebuild a lost index.</summary>
    public const string Meta = "meta.json";

    /// <summary>Immutable original MP4 payload for a video gallery record.</summary>
    public const string VideoSource = "source.mp4";

    /// <summary>Current trim/text render. Falls back to <see cref="VideoSource"/> when absent.</summary>
    public const string VideoRendered = "rendered.mp4";

    /// <summary>Non-destructive trim and timed-text document.</summary>
    public const string VideoEdits = "video-edits.json";

    /// <summary>Durable marker written before a recording begins.</summary>
    public const string VideoPending = ".video-pending.json";

    /// <summary>Encoder output kept private until the MP4 is complete.</summary>
    public const string VideoWriting = ".source-writing.mp4";

    /// <summary>
    /// Durable provisional record written before original pixels. It lets startup merge a
    /// capture that reached disk but was interrupted before the main queue index was updated.
    /// </summary>
    public const string OriginalPending = ".original-pending.json";

    /// <summary>Prefix for image-annotation assets.</summary>
    public const string AssetPrefix = "asset-";

    /// <summary>
    /// Returns whether a persisted image sidecar is one of MyCapture's canonical
    /// <c>asset-NN.png</c> names, never a rooted or traversing path.
    /// </summary>
    public static bool IsSafeAssetFileName(string? fileName)
    {
        const string extension = ".png";
        if (string.IsNullOrWhiteSpace(fileName)
            || !fileName.StartsWith(AssetPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = fileName.Substring(
            AssetPrefix.Length,
            fileName.Length - AssetPrefix.Length - extension.Length);
        return token.Length > 0 && token.All(char.IsAsciiDigit);
    }
}
