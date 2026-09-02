namespace MyCapture.Core.Recording;

/// <summary>Vertical anchor used by a timed text note in a video.</summary>
public enum VideoTextPlacement
{
    Bottom = 0,
    Center = 1,
    Top = 2,
}

/// <summary>
/// One text note that is visible over the half-open source-time interval
/// <c>[StartMs, EndMs)</c>.
/// </summary>
public sealed class TimedTextOverlay
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public double StartMs { get; set; }

    public double EndMs { get; set; }

    public string Text { get; set; } = string.Empty;

    public VideoTextPlacement Placement { get; set; } = VideoTextPlacement.Bottom;

    public bool IsActiveAt(double sourceTimeMs) =>
        double.IsFinite(sourceTimeMs)
        && sourceTimeMs >= StartMs
        && sourceTimeMs < EndMs;

    public TimedTextOverlay Clone() => new()
    {
        Id = Id,
        StartMs = StartMs,
        EndMs = EndMs,
        Text = Text,
        Placement = Placement,
    };
}

/// <summary>
/// A transparent PNG annotation layer shown over a source-time interval. The encoded bitmap
/// contains only the user's drawing/edit marks; the immutable source video remains untouched.
/// </summary>
public sealed class FrameEditLayer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public double StartMs { get; set; }

    public double EndMs { get; set; }

    public string Name { get; set; } = "프레임 편집";

    public string OverlayPngBase64 { get; set; } = string.Empty;

    public bool IsActiveAt(double sourceTimeMs) =>
        double.IsFinite(sourceTimeMs)
        && sourceTimeMs >= StartMs
        && sourceTimeMs < EndMs;

    public FrameEditLayer Clone() => new()
    {
        Id = Id,
        StartMs = StartMs,
        EndMs = EndMs,
        Name = Name,
        OverlayPngBase64 = OverlayPngBase64,
    };
}

/// <summary>
/// Non-destructive editing instructions for an immutable source recording.
/// Times are always expressed on the source timeline so changing the trim does not move notes.
/// </summary>
public sealed class VideoEditDocument
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumOverlayCount = 100;
    public const int MaximumFrameLayerCount = 100;
    public const int MaximumTextLength = 500;
    public const int MaximumFrameLayerNameLength = 80;
    public const int MaximumFrameLayerEncodedLength = 32 * 1024 * 1024;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int CanvasWidth { get; set; }

    public int CanvasHeight { get; set; }

    public double SourceDurationMs { get; set; }

    public double TrimInMs { get; set; }

    public double TrimOutMs { get; set; }

    public List<TimedTextOverlay> TextOverlays { get; set; } = [];

    public List<FrameEditLayer> FrameEditLayers { get; set; } = [];

    public bool HasEdits =>
        TrimInMs > 0.5
        || TrimOutMs < SourceDurationMs - 0.5
        || TextOverlays.Count > 0
        || FrameEditLayers.Count > 0;

    public static VideoEditDocument CreateFor(
        int width,
        int height,
        double sourceDurationMs) => new()
        {
            CanvasWidth = Math.Max(1, width),
            CanvasHeight = Math.Max(1, height),
            SourceDurationMs = Math.Max(1, sourceDurationMs),
            TrimInMs = 0,
            TrimOutMs = Math.Max(1, sourceDurationMs),
        };

    /// <summary>
    /// Returns a validated detached copy. Invalid or hostile overlay entries are dropped rather
    /// than reaching WPF text layout, while an unknown future schema is rejected explicitly.
    /// </summary>
    public VideoEditDocument NormalizeFor(int width, int height, double sourceDurationMs)
    {
        if (SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Unsupported video edit schema {SchemaVersion}.");
        }

        double duration = double.IsFinite(sourceDurationMs)
            ? Math.Max(1, sourceDurationMs)
            : 1;
        var normalized = CreateFor(width, height, duration);

        double trimIn = double.IsFinite(TrimInMs) ? Math.Clamp(TrimInMs, 0, duration) : 0;
        double trimOut = double.IsFinite(TrimOutMs) ? Math.Clamp(TrimOutMs, 0, duration) : duration;
        if (trimOut > trimIn)
        {
            normalized.TrimInMs = trimIn;
            normalized.TrimOutMs = trimOut;
        }

        var ids = new HashSet<Guid>();
        foreach (TimedTextOverlay overlay in TextOverlays ?? [])
        {
            if (normalized.TextOverlays.Count >= MaximumOverlayCount
                || overlay is null
                || !double.IsFinite(overlay.StartMs)
                || !double.IsFinite(overlay.EndMs))
            {
                continue;
            }

            string text = (overlay.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (text.Length > MaximumTextLength)
            {
                text = text[..MaximumTextLength];
            }

            double start = Math.Clamp(overlay.StartMs, 0, duration);
            double end = Math.Clamp(overlay.EndMs, 0, duration);
            if (end <= start)
            {
                continue;
            }

            Guid id = overlay.Id == Guid.Empty || !ids.Add(overlay.Id)
                ? Guid.NewGuid()
                : overlay.Id;
            _ = ids.Add(id);

            normalized.TextOverlays.Add(new TimedTextOverlay
            {
                Id = id,
                StartMs = start,
                EndMs = end,
                Text = text,
                Placement = Enum.IsDefined(overlay.Placement)
                    ? overlay.Placement
                    : VideoTextPlacement.Bottom,
            });
        }

        var frameLayerIds = new HashSet<Guid>();
        foreach (FrameEditLayer layer in FrameEditLayers ?? [])
        {
            if (normalized.FrameEditLayers.Count >= MaximumFrameLayerCount
                || layer is null
                || !double.IsFinite(layer.StartMs)
                || !double.IsFinite(layer.EndMs)
                || string.IsNullOrWhiteSpace(layer.OverlayPngBase64)
                || layer.OverlayPngBase64.Length > MaximumFrameLayerEncodedLength)
            {
                continue;
            }

            double start = Math.Clamp(layer.StartMs, 0, duration);
            double end = Math.Clamp(layer.EndMs, 0, duration);
            if (end <= start)
            {
                continue;
            }

            Guid id = layer.Id == Guid.Empty || !frameLayerIds.Add(layer.Id)
                ? Guid.NewGuid()
                : layer.Id;
            _ = frameLayerIds.Add(id);
            string name = string.IsNullOrWhiteSpace(layer.Name)
                ? "프레임 편집"
                : layer.Name.Trim();
            if (name.Length > MaximumFrameLayerNameLength)
            {
                name = name[..MaximumFrameLayerNameLength];
            }

            normalized.FrameEditLayers.Add(new FrameEditLayer
            {
                Id = id,
                StartMs = start,
                EndMs = end,
                Name = name,
                OverlayPngBase64 = layer.OverlayPngBase64,
            });
        }

        return normalized;
    }

    public VideoEditDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        CanvasWidth = CanvasWidth,
        CanvasHeight = CanvasHeight,
        SourceDurationMs = SourceDurationMs,
        TrimInMs = TrimInMs,
        TrimOutMs = TrimOutMs,
        TextOverlays = TextOverlays.Select(overlay => overlay.Clone()).ToList(),
        FrameEditLayers = FrameEditLayers.Select(layer => layer.Clone()).ToList(),
    };
}
