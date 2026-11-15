using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// The PDF dialog in the app's styles: one image a page with the third row chosen, four a page
    /// with the third row chosen, and a row being dragged. With MYCAPTURE_PDF_PREVIEW_EVIDENCE set,
    /// each scene is kept as a PNG.
    /// </summary>
    [Fact]
    public Task ContactSheetPreview_ScenesRenderInAppStyles() =>
        RunIsolated(nameof(ContactSheetPreview_ScenesRenderInAppStyles), () =>
        {
            var application = CreateLibraryMotionApplication();
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(application.Dispatcher));
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                using var scope = UiText.UseLanguage("ko-KR");
                string folder = OwnedTestDirectory.Create("mc-pdf-preview-theme-");
                GalleryContactSheetItem[] items = GalleryContactSheetOrderTests.Items(folder, Colors.IndianRed, Colors.SeaGreen,
                    Colors.SteelBlue, Colors.Goldenrod, Colors.SlateGray, Colors.Peru, Colors.MediumPurple);
                var dialog = new GalleryContactSheetDialog(items, 0, () => true);
                FluidMotion.SetWindowEntrance(dialog, false);
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -10000; dialog.Top = -10000;
                dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
                // The CI desktop is 1024 x 768; the dialog is shown at a size that fits it.
                dialog.Width = 1000; dialog.Height = 680;
                try
                {
                    dialog.Show();
                    ComboBox layout = Descendants(dialog).OfType<ComboBox>().ElementAt(1);
                    Assert.Equal(5, layout.Items.Count);

                    layout.SelectedIndex = 0; // 1 x 1
                    dialog.OrderList.SelectedIndex = 2;
                    WaitForPreview(dialog);
                    Assert.Equal(2, dialog.PreviewPage);
                    Assert.Equal(Visibility.Visible, dialog.PreviewHighlight.Visibility);
                    SavePreviewEvidence(dialog, "pdf-1x1-third.png");

                    layout.SelectedIndex = 2; // 2 x 2
                    dialog.OrderList.SelectedIndex = 2;
                    WaitForPreview(dialog);
                    Assert.Equal(0, dialog.PreviewPage);
                    Assert.Equal(2, dialog.HighlightedCell);
                    SavePreviewEvidence(dialog, "pdf-2x2-third.png");

                    // The first row held over the fourth: the rows it passed have slid up to open its place.
                    Drain(dialog);
                    Point RowCenter(int position)
                    {
                        var row = (ListBoxItem)dialog.OrderList.ItemContainerGenerator.ContainerFromIndex(position);
                        Rect bounds = row.TransformToAncestor(dialog.OrderList).TransformBounds(new Rect(row.RenderSize));
                        double shift = (row.RenderTransform as TranslateTransform)?.Y ?? 0;
                        return new Point(bounds.Left + bounds.Width / 2, bounds.Top - shift + bounds.Height / 2);
                    }
                    Assert.True(dialog.BeginRowDrag(dialog.OrderRows[0], RowCenter(0)));
                    dialog.UpdateRowDrag(RowCenter(3) + new Vector(24, 6));
                    Assert.Equal(3, dialog.DragTarget);
                    SavePreviewEvidence(dialog, "pdf-drag.png");
                    dialog.EndRowDrag(commit: false);
                }
                finally { dialog.Close(); OwnedTestDirectory.Delete(folder); }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                application.Shutdown();
            }
        }, hungAfter: TimeSpan.FromSeconds(240));

    private static void WaitForPreview(GalleryContactSheetDialog dialog)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        Drain(dialog);
        while (DateTime.UtcNow < deadline && (!dialog.PendingPreview.IsCompleted || dialog.PreviewImage is null
            || dialog.OrderRows.Any(row => row.ThumbnailRequested && !row.PendingThumbnail.IsCompleted)))
        {
            Drain(dialog);
            Thread.Sleep(20);
        }
        Assert.True(dialog.PendingPreview.IsCompletedSuccessfully, $"The preview did not finish: {dialog.PendingPreview.Exception}");
        Assert.NotNull(dialog.PreviewImage);
        Drain(dialog);
    }

    private static void SavePreviewEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_PDF_PREVIEW_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
