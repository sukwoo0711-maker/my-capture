using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorPinVisualTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "VisualEvidence")]
    public void RenderEditorWithArrowProperties()
    {
        StaTestHost.Run(() =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var document = AnnotationDocument.CreateFor(640, 360);
            document.Add(PolylineAnnotation.CreateArrow(new PointD(130, 230), new PointD(470, 110), ColorRgba.FromRgb(0xEF, 0x44, 0x44), 4));
            using var host = EditorWindowHost.Create(document: document);
            host.Window.Resources = SystemHighContrastThemeTests.LoadTheme();
            host.Window.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "Font.Ui");
            MyCapture.App.Themes.ThemeFonts.ApplyText(host.Window);
            host.Window.Width = 1000;
            host.Window.Height = 640;
            host.Editor.HandleShortcut(Key.A, ModifierKeys.None);
            ShortcutPresentation.SetAltRevealed(host.Window, true);
            ShortcutPresentation.Refresh();
            host.Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
            Assert.True(host.Editor.ActualWidth > 900);
            var rendered = new RenderTargetBitmap((int)host.Editor.ActualWidth, (int)host.Editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(host.Editor);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            Assert.True(stream.Length > 1000);
            output.WriteLine($"Editor rendered at {rendered.PixelWidth} × {rendered.PixelHeight} pixels.");
        });
    }
}
