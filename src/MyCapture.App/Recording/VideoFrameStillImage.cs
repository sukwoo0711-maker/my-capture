using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// The timed text belonging to one extracted frame, independent of the moving playhead.
/// The annotation editor deliberately shows only the editable pixels (no timed text).
/// Video layers keep that text-free result; still outputs are self-contained snapshots:
/// the base already has the editor rotation/crop, then upright text is baked once at its
/// normalized output position. Annotations remain editable above that base in the same
/// pixel coordinates. Re-editing therefore starts with exactly the saved base and layers;
/// subsequent still-image rotations rotate that base (including its baked subtitle).
/// </summary>
internal sealed record VideoFrameStillImage(double SourceTimeMs, IReadOnlyList<TimedTextOverlay> TextOverlays)
{
    internal static VideoFrameStillImage Capture(VideoEditDocument document, double sourceTimeMs) =>
        new(sourceTimeMs, document.TextOverlays
            .Where(overlay => overlay.IsActiveAt(sourceTimeMs) && !string.IsNullOrWhiteSpace(overlay.Text))
            .Select(overlay => overlay.Clone()).ToArray());

    internal Task<AnnotationEditingResult> CreateResultAsync(AnnotationEditingResult result)
    {
        if (TextOverlays.Count == 0) return Task.FromResult(result);
        AnnotationDocument document = CaptureCommitService.CreatePersistenceSnapshot(result.Document);
        return StaThreadTask.RunAsync(() =>
        {
            BitmapSource edited = result.SelectedBitmap;
            int width = edited.PixelWidth, height = edited.PixelHeight;
            BitmapSource still = VideoFrameRenderPipeline.RenderFrame(edited, width, height, width, height,
                TextOverlays, [], new Dictionary<Guid, BitmapSource>(), SourceTimeMs);
            // PNG stores straight alpha. Use those same pixels before the first flatten,
            // so loading original.png cannot introduce another premultiplication rounding.
            var original = new FormatConvertedBitmap(still, PixelFormats.Bgra32, null, 0);
            original.Freeze();
            // Only the still consumer gets this subtitle-bearing base. The video consumer
            // retains the text-free result, geometry and editable document.
            return new AnnotationEditingResult(result.Frame, result.BitmapRegion, original,
                document, result.Action, result.ImageAssetBitmaps, result.ImageAssetSources,
                result.ReduceExport, result.SourceWindowTitle, result.ShowSourceWindowTitle,
                result.RotationTurns, result.CropOrigin);
        }, "MyCapture frame still renderer");
    }
}
