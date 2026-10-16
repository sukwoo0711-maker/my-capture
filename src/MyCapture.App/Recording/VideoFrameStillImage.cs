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
/// rotate/crop/annotate first, then draw upright text at its normalized output position.
/// Baking the completed still also makes its original PNG and later library exports agree,
/// without replaying an annotation or a translucent subtitle background a second time.
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
        AnnotationDocument document = result.Document.Clone();
        return StaThreadTask.RunAsync(() =>
        {
            BitmapSource edited = CaptureCommitService.Flatten(result.SelectedBitmap, document, result.ImageAssetBitmaps);
            int width = edited.PixelWidth, height = edited.PixelHeight;
            BitmapSource still = VideoFrameRenderPipeline.RenderFrame(edited, width, height, width, height,
                TextOverlays, [], new Dictionary<Guid, BitmapSource>(), SourceTimeMs);
            // The video consumer still receives the original result and editable document.
            // The image consumer receives a completed frame, so its sidecar must not replay
            // the already-composited annotations over the text or rotate the text with them.
            return new AnnotationEditingResult(result.Frame, result.BitmapRegion, still,
                AnnotationDocument.CreateFor(width, height), result.Action,
                new Dictionary<string, BitmapSource>(), new Dictionary<string, string>(),
                result.ReduceExport, result.SourceWindowTitle, result.ShowSourceWindowTitle,
                result.RotationTurns, result.CropOrigin);
        }, "MyCapture frame still renderer");
    }
}
