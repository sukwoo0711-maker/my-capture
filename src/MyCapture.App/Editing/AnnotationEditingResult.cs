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
        IReadOnlyDictionary<string, string> imageAssetSources)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        BitmapRegion = bitmapRegion;
        SelectedBitmap = selectedBitmap ?? throw new ArgumentNullException(nameof(selectedBitmap));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Action = action;
        ImageAssetBitmaps = imageAssetBitmaps ?? throw new ArgumentNullException(nameof(imageAssetBitmaps));
        ImageAssetSources = imageAssetSources ?? throw new ArgumentNullException(nameof(imageAssetSources));
    }

    /// <summary>The frozen monitor frame the selection was cropped from.</summary>
    internal FrozenFrame Frame { get; }

    /// <summary>The selection rectangle in frozen-frame physical pixels.</summary>
    internal RectD BitmapRegion { get; }

    /// <summary>The cropped selection pixels, frozen and safe to hand across threads.</summary>
    internal BitmapSource SelectedBitmap { get; }

    /// <summary>The live annotation layer, in selected-image pixel space.</summary>
    internal AnnotationDocument Document { get; }

    /// <summary>What the user asked the editor to do on commit.</summary>
    internal EditorCommitAction Action { get; }

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
}
