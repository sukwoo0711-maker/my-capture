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
            UiText.Get("Text_D9FC5F981274"),
            context.Document,
            context.AssetBitmaps,
            privacyRedactionService)
    {
    }
}
