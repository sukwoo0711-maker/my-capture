using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Rotation interacting with the rest of the editor: the crop origin must follow the
/// canvas orientation, and a plain trim export must be able to skip composition.
/// </summary>
public sealed class AnnotationRotationCropTests
{
    private const int BaseWidth = 1920;
    private const int BaseHeight = 1080;

    private static AnnotationEditorController NewController(out AnnotationDocument document, out UndoStack undo)
    {
        document = AnnotationDocument.CreateFor(BaseWidth, BaseHeight);
        undo = new UndoStack();
        var controller = new AnnotationEditorController(document, undo)
        {
            BaseWidthAtZeroTurns = BaseWidth,
            BaseHeightAtZeroTurns = BaseHeight,
        };
        return controller;
    }

    [Fact]
    public void CropThenRotate_RemapsCropOriginIntoRotatedSpace()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);

        controller.CropDocument(new RectD(100, 50, 800, 600));
        controller.RotateDocument(1);

        Assert.Equal(1, controller.RotationTurns);
        Assert.Equal(600, document.CanvasWidth);
        Assert.Equal(800, document.CanvasHeight);
        // The cropped region rotated about the 1920x1080 base: (1080-(50+600), 100).
        Assert.Equal(430, controller.CropOrigin.X, 3);
        Assert.Equal(100, controller.CropOrigin.Y, 3);

        controller.PerformUndo();

        Assert.Equal(0, controller.RotationTurns);
        Assert.Equal(800, document.CanvasWidth);
        Assert.Equal(600, document.CanvasHeight);
        Assert.Equal(100, controller.CropOrigin.X, 3);
        Assert.Equal(50, controller.CropOrigin.Y, 3);
    }

    [Fact]
    public void CropRotateCropThenUndo_RestoresCropOriginInUnrotatedSpace()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);

        controller.CropDocument(new RectD(100, 50, 800, 600));
        controller.RotateDocument(1);
        Assert.Equal(430, controller.CropOrigin.X, 3);
        Assert.Equal(100, controller.CropOrigin.Y, 3);

        controller.CropDocument(new RectD(50, 50, 100, 100));
        Assert.Equal(480, controller.CropOrigin.X, 3);
        Assert.Equal(150, controller.CropOrigin.Y, 3);

        controller.PerformUndo(); // undo the second crop
        Assert.Equal(600, document.CanvasWidth);
        Assert.Equal(800, document.CanvasHeight);
        Assert.Equal(430, controller.CropOrigin.X, 3);
        Assert.Equal(100, controller.CropOrigin.Y, 3);

        controller.PerformUndo(); // undo the rotation

        Assert.Equal(0, controller.RotationTurns);
        Assert.Equal(800, document.CanvasWidth);
        Assert.Equal(600, document.CanvasHeight);
        Assert.Equal(100, controller.CropOrigin.X, 3);
        Assert.Equal(50, controller.CropOrigin.Y, 3);
    }

    [Fact]
    public void HalfTurnOnCroppedDocument_KeepsCanvasSizeButRemapsOrigin()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);

        controller.CropDocument(new RectD(100, 50, 800, 600));
        controller.RotateDocument(2);

        Assert.Equal(2, controller.RotationTurns);
        Assert.Equal(800, document.CanvasWidth);
        Assert.Equal(600, document.CanvasHeight);
        // 180° about the base centre: (1920-900, 1080-650).
        Assert.Equal(1020, controller.CropOrigin.X, 3);
        Assert.Equal(430, controller.CropOrigin.Y, 3);

        controller.PerformUndo();

        Assert.Equal(0, controller.RotationTurns);
        Assert.Equal(800, document.CanvasWidth);
        Assert.Equal(600, document.CanvasHeight);
        Assert.Equal(100, controller.CropOrigin.X, 3);
        Assert.Equal(50, controller.CropOrigin.Y, 3);
    }

    [Fact]
    public void RotateThenCropThenUndoCrop_RestoresRotatedFullBase()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);

        controller.RotateDocument(1);
        controller.CropDocument(new RectD(100, 200, 400, 300));
        controller.PerformUndo();

        Assert.Equal(1, controller.RotationTurns);
        Assert.Equal(1080, document.CanvasWidth);
        Assert.Equal(1920, document.CanvasHeight);
        Assert.Equal(0, controller.CropOrigin.X, 3);
        Assert.Equal(0, controller.CropOrigin.Y, 3);
    }

    [Fact]
    public void ThreeQuarterTurns_RemapSurvivesEveryOrientationStep()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);

        controller.CropDocument(new RectD(100, 50, 800, 600));
        controller.RotateDocument(3);

        Assert.Equal(3, controller.RotationTurns);
        Assert.Equal(600, document.CanvasWidth);
        Assert.Equal(800, document.CanvasHeight);
        // Three forward steps: (430,100) -> (1020,430) -> (50,1020).
        Assert.Equal(50, controller.CropOrigin.X, 3);
        Assert.Equal(1020, controller.CropOrigin.Y, 3);

        controller.PerformUndo();

        Assert.Equal(0, controller.RotationTurns);
        Assert.Equal(800, document.CanvasWidth);
        Assert.Equal(600, document.CanvasHeight);
        Assert.Equal(100, controller.CropOrigin.X, 3);
        Assert.Equal(50, controller.CropOrigin.Y, 3);

        controller.PerformRedo();

        Assert.Equal(3, controller.RotationTurns);
        Assert.Equal(600, document.CanvasWidth);
        Assert.Equal(800, document.CanvasHeight);
        Assert.Equal(50, controller.CropOrigin.X, 3);
        Assert.Equal(1020, controller.CropOrigin.Y, 3);
    }

    [Fact]
    public void Annotations_FollowRotatedCanvasAcrossCropUndo()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);

        var box = new RectangleAnnotation { Rect = new RectD(200, 150, 120, 60) };
        document.Add(box);
        controller.CropDocument(new RectD(100, 50, 800, 600));
        Assert.Equal(100, box.Rect.X, 3);
        Assert.Equal(100, box.Rect.Y, 3);

        controller.RotateDocument(1);
        // The rect's new top-left is the image of its old bottom-left:
        // (600-(100+60), 100) = (440, 100) in the 600x800 rotated canvas.
        Assert.Equal(440, box.Rect.X, 3);
        Assert.Equal(100, box.Rect.Y, 3);

        controller.PerformUndo(); // undo rotation
        Assert.Equal(100, box.Rect.X, 3);
        Assert.Equal(100, box.Rect.Y, 3);

        controller.PerformUndo(); // undo crop
        Assert.Equal(200, box.Rect.X, 3);
        Assert.Equal(150, box.Rect.Y, 3);
    }

    [Fact]
    public void CompositionBypass_RequiresNoOverlaysLayersAndMatchingSize()
    {
        var request = new VideoFrameRenderRequest(
            "ignored.mp4", 0, 1000, 30, 1920, 1080, 1920, 1080, [], []);

        Assert.True(VideoFrameRenderPipeline.CanBypassComposition(request, 1920, 1080));
        Assert.False(VideoFrameRenderPipeline.CanBypassComposition(request, 1280, 720));

        var scaled = request with { Width = 1280, Height = 720 };
        Assert.False(VideoFrameRenderPipeline.CanBypassComposition(scaled, 1920, 1080));

        var withText = request with { TextOverlays = [new TimedTextOverlay { Text = "hi" }] };
        Assert.False(VideoFrameRenderPipeline.CanBypassComposition(withText, 1920, 1080));

        var withLayer = request with
        {
            FrameEditLayers = [new FrameEditLayer { StartMs = 0, EndMs = 40, OverlayPngBase64 = "aGk=" }],
        };
        Assert.False(VideoFrameRenderPipeline.CanBypassComposition(withLayer, 1920, 1080));
    }
}
