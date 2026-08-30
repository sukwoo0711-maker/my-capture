using MyCapture.App.Editing;

namespace MyCapture.App.Gallery;

/// <summary>Gallery-specific constructor over the shared standalone annotation editor.</summary>
internal sealed class GalleryEditorWindow : AnnotationEditorWindow
{
    internal GalleryEditorWindow(GalleryReeditContext context)
        : base(
            (context ?? throw new ArgumentNullException(nameof(context))).Frame,
            context.CropRegion,
            context.OriginalBitmap,
            "MyCapture — 다시 편집",
            context.Document,
            context.AssetBitmaps)
    {
    }
}
