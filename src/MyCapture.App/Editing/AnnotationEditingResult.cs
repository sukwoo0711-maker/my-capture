using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Editing;

/// <summary>
/// Everything the annotation editor produces when the user commits an edit.
/// </summary>
/// <remarks>
/// <para>
/// This is the hand-off point between the in-place editor and the persistence /
/// clipboard / export layer. The editor never writes a file, touches the clipboard,
/// or re-captures the desktop itself: it hands over the frozen selected pixels, the
/// live annotation document, the in-memory decoded bitmaps for any inserted images,
/// the on-disk source paths of those images, and the <see cref="Action"/> the user
/// asked for, and lets the consumer decide what to persist.
/// </para>
/// <para>
/// Coordinates inside <see cref="Document"/> are in the selected image's own physical
/// pixels, with the origin at the top-left of the selection. The consumer can flatten
/// against <see cref="SelectedBitmap"/> without any DPI or offset arithmetic.
/// </para>
/// <para>
/// <see cref="Action"/> is an enum with no WPF type, so the intent flows into
/// <c>MyCapture.Core</c>-facing code without leaking a WPF dependency.
/// </para>
/// </remarks>
internal sealed class AnnotationEditingResult
{
    internal AnnotationEditingResult(
        FrozenFrame frame,
        RectD bitmapRegion,
        BitmapSource selectedBitmap,
        AnnotationDocument document,
        EditorCommitAction action,
        IReadOnlyDictionary<string, BitmapSource> imageAssetBitmaps,
        IReadOnlyDictionary<string, string> imageAssetSources,
        bool reduceExport = false,
        string? sourceWindowTitle = null,
        bool? showSourceWindowTitle = null,
        int rotationTurns = 0,
        PointD cropOrigin = default)
        : this(AnnotationSourceMetadata.FromFrame(frame), bitmapRegion, selectedBitmap, document,
            action, imageAssetBitmaps, imageAssetSources, reduceExport, sourceWindowTitle,
            showSourceWindowTitle, rotationTurns, cropOrigin)
    {
    }

    internal AnnotationEditingResult(
        AnnotationSourceMetadata frame,
        RectD bitmapRegion,
        BitmapSource selectedBitmap,
        AnnotationDocument document,
        EditorCommitAction action,
        IReadOnlyDictionary<string, BitmapSource> imageAssetBitmaps,
        IReadOnlyDictionary<string, string> imageAssetSources,
        bool reduceExport = false,
        string? sourceWindowTitle = null,
        bool? showSourceWindowTitle = null,
        int rotationTurns = 0,
        PointD cropOrigin = default)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        BitmapRegion = bitmapRegion;
        SelectedBitmap = selectedBitmap ?? throw new ArgumentNullException(nameof(selectedBitmap));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Action = action;
        ImageAssetBitmaps = imageAssetBitmaps ?? throw new ArgumentNullException(nameof(imageAssetBitmaps));
        ImageAssetSources = imageAssetSources ?? throw new ArgumentNullException(nameof(imageAssetSources));
        ReduceExport = reduceExport;
        SourceWindowTitle = sourceWindowTitle;
        ShowSourceWindowTitle = showSourceWindowTitle;
        RotationTurns = rotationTurns;
        CropOrigin = cropOrigin;
    }

    /// <summary>Source-frame provenance, without retaining the desktop bitmap.</summary>
    internal AnnotationSourceMetadata Frame { get; }

    /// <summary>The selection rectangle in frozen-frame physical pixels.</summary>
    internal RectD BitmapRegion { get; }

    /// <summary>The cropped selection pixels, frozen and safe to hand across threads.</summary>
    internal BitmapSource SelectedBitmap { get; }

    /// <summary>The live annotation layer, in selected-image pixel space.</summary>
    internal AnnotationDocument Document { get; }

    /// <summary>What the user asked the editor to do on commit.</summary>
    internal EditorCommitAction Action { get; }

    internal bool ReduceExport { get; }

    internal string? SourceWindowTitle { get; }

    /// <summary>The editor's displayed option; null for callers without a preview.</summary>
    internal bool? ShowSourceWindowTitle { get; }

    /// <summary>
    /// Clockwise quarter-turns the user applied to the document relative to the source
    /// capture. A 180° turn keeps the canvas dimensions, so consumers cannot detect it
    /// from sizes alone.
    /// </summary>
    internal int RotationTurns { get; }

    /// <summary>
    /// The live canvas origin's offset within the base bitmap at the current orientation.
    /// Non-zero once a crop has shrunk the canvas; consumers mapping document coordinates
    /// back onto the untransformed source need this offset.
    /// </summary>
    internal PointD CropOrigin { get; }

    /// <summary>
    /// The decoded, frozen bitmap for each <see cref="ImageAnnotation.AssetFileName"/> used
    /// in the document, so the flattened render and the persisted sidecars never depend on
    /// the inserted source file still existing.
    /// </summary>
    internal IReadOnlyDictionary<string, BitmapSource> ImageAssetBitmaps { get; }

    /// <summary>
    /// Maps each <see cref="ImageAnnotation.AssetFileName"/> used in the document to the
    /// absolute source path it was decoded from. Best-effort provenance only; persistence
    /// uses <see cref="ImageAssetBitmaps"/> so a deleted source cannot lose the asset.
    /// </summary>
    internal IReadOnlyDictionary<string, string> ImageAssetSources { get; }

    /// <summary>
    /// A file export or library save for this operation completed durably. This is
    /// separate from the commit's return value, which also includes clipboard success.
    /// </summary>
    internal bool SaveCompleted { get; set; }

    /// <summary>The copy failed after it was attempted, including a thrown clipboard error.</summary>
    internal bool ClipboardCopyFailed { get; set; }

    /// <summary>
    /// Called once by the Done commit, on the caller's context, as soon as the clipboard holds the
    /// result while the library save may still be running. The editor window sets it when it may
    /// leave the screen at that point; null otherwise.
    /// </summary>
    internal Action? CopiedToClipboard { get; set; }
}
