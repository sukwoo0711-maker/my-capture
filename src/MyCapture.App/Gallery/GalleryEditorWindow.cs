using MyCapture.App.Editing;
using MyCapture.App.Ocr;

namespace MyCapture.App.Gallery;

/// <summary>Gallery-specific constructor over the shared standalone annotation editor.</summary>
internal sealed class GalleryEditorWindow : AnnotationEditorWindow
{
    internal GalleryEditorWindow(
        GalleryReeditContext context,
        IPrivacyRedactionService? privacyRedactionService = null)
        : base(
            (context ?? throw new ArgumentNullException(nameof(context))).Frame,
            context.CropRegion,
            context.OriginalBitmap,
            "MyCapture — 다시 편집",
            context.Document,
            context.AssetBitmaps,
            privacyRedactionService)
    {
    }
}
