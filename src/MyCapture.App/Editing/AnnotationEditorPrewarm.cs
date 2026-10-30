using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Editing;

/// <summary>
/// Builds the image editor once, off screen, while the resident app is idle after launch, so the
/// first editor after a capture does not pay first-use costs: the editor's code, the command bar's
/// and inspector's templates, icons and fonts, and the first layout and drawing of its surface.
/// </summary>
/// <remarks>
/// An unshown host supplies the real window's resource and sizing inputs, without an HWND,
/// draft session, preference write or clipboard access. It is closed after off-screen drawing.
/// </remarks>
internal static class AnnotationEditorPrewarm
{
    private const int ImageWidth = 64, ImageHeight = 36;

    /// <summary>The editor's opening size on a common display (DIPs), so the command bar lays out fully.</summary>
    private static readonly Size Viewport = new(1000, 640);

    internal static void Run()
    {
        BitmapSource bitmap = BitmapSource.Create(ImageWidth, ImageHeight, 96, 96, PixelFormats.Bgr32, null,
            new byte[ImageWidth * ImageHeight * 4], ImageWidth * 4);
        bitmap.Freeze();
        var region = new RectD(0, 0, ImageWidth, ImageHeight);
        Application? application = Application.Current;
        Window? mainWindow = application?.MainWindow;
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetAll();
        // A captured app/page has a source chip; imported images may not. They deliberately
        // have separate cache keys, so measure both instead of assuming identical chrome.
        foreach (CaptureProvenance provenance in new[] { CaptureProvenance.None,
            CaptureProvenance.Create(appName: "MyCapture") })
        {
            AnnotationEditorWindow? host = null;
            try
            {
                host = AnnotationEditorWindow.CreatePrewarmHost(new FrozenFrame(bitmap, region, null, 0),
                    region, bitmap, monitors, provenance);
                if (!provenance.HasSource)
                {
                    host.Editor.Measure(Viewport);
                    host.Editor.Arrange(new Rect(Viewport));
                    var target = new RenderTargetBitmap((int)Viewport.Width, (int)Viewport.Height, 96, 96, PixelFormats.Pbgra32);
                    target.Render(host.Editor);
                }
            }
            finally
            {
                // WPF assigns its first constructed Window as MainWindow, even without Show.
                if (application is not null && (host is null || ReferenceEquals(application.MainWindow, host)))
                    application.MainWindow = mainWindow;
                host?.Close();
            }
        }
    }
}
