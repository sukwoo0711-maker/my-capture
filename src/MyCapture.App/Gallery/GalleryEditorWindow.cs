using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.App.Gallery;

/// <summary>Gallery-specific constructor over the shared standalone annotation editor.</summary>
internal sealed class GalleryEditorWindow : AnnotationEditorWindow
{
    internal GalleryEditorWindow(
        GalleryReeditContext context,
        IPrivacyRedactionService? privacyRedactionService = null,
        CaptureRecord? record = null,
        Func<int>? retentionHours = null)
        : base(
            (context ?? throw new ArgumentNullException(nameof(context))).Frame,
            context.CropRegion,
            context.OriginalBitmap,
            UiText.Get("Text_D9FC5F981274"),
            context.Document,
            context.AssetBitmaps,
            privacyRedactionService)
    {
        if (record is null || Content is not UIElement editor)
        {
            return;
        }

        TimeSpan ttl = CaptureRetention.TimeToLive(new QueueSettings
        {
            ImageRetentionHours = retentionHours?.Invoke() ?? CaptureRetention.DefaultImageRetentionHours,
        });
        string countdown = CaptureRetention.FormatCountdown(
            record.CreatedAt,
            ttl,
            DateTimeOffset.Now,
            record.IsImage,
            record.IsPinned);

        var banner = new Border
        {
            Padding = new Thickness(16, 8, 16, 8),
            Background = TryFindResource("Surface.Overlay") as Brush,
            Child = new TextBlock
            {
                Text = countdown,
                FontFamily = TryFindResource("Font.Mono") as FontFamily,
                Foreground = TryFindResource("Text.Secondary") as Brush,
                FontSize = 13,
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        root.Children.Add(banner);
        root.Children.Add(editor);
        Content = root;
    }
}
