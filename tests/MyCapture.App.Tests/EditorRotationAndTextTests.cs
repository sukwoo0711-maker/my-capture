using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Runtime checks for capture rotation (bitmap vs annotation space) and live text entry.
/// </summary>
public sealed class EditorRotationAndTextTests
{
    [Fact]
    public void WpfRotateTransform90_IsCounterClockwise()
    {
        StaTestHost.Run(() =>
        {
            BitmapSource source = RedBlueLandscape();
            var rotated = new TransformedBitmap(source, new RotateTransform(90));
            rotated.Freeze();

            Assert.Equal(100, rotated.PixelWidth);
            Assert.Equal(200, rotated.PixelHeight);

            // WPF Y grows downward, so a +90° transform lands visually clockwise:
            // the left (red) half of a landscape becomes the top of the portrait.
            Assert.True(IsRed(Sample(rotated, 50, 25)), "RotateTransform(90) should move left-red to the top.");
            Assert.True(IsBlue(Sample(rotated, 50, 150)), "RotateTransform(90) should move right-blue to the bottom.");
        });
    }

    [Fact]
    public void RotateRight_KeepsHitTestOnTheDrawnRectangle_AndMatchesImageContent()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create(RedBlueLandscape(), 200, 100);
            AnnotationEditorController controller = Field<AnnotationEditorController>(host.Editor, "_controller");
            AnnotationEditorSurface surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");

            var mark = new RectangleAnnotation
            {
                Rect = new RectD(0, 0, 100, 100),
                Fill = ColorRgba.FromRgb(0xFF, 0xFF, 0x00),
            };
            controller.Document.Add(mark);
            controller.SetSelected(mark);

            host.Window.UpdateLayout();
            Assert.True(host.Editor.HandleShortcut(Key.W, ModifierKeys.None));
            host.Window.UpdateLayout();

            Assert.Equal(100, host.Editor.DisplayedBitmap.PixelWidth);
            Assert.Equal(200, host.Editor.DisplayedBitmap.PixelHeight);
            Assert.Equal(new RectD(0, 0, 100, 200), host.Editor.DisplayedRegion);

            // After a clockwise quarter-turn the original left (red) half occupies the top
            // 100x100 of a 100x200 canvas, and the rectangle that covered it must sit there.
            Assert.Equal(0, mark.Rect.X, 3);
            Assert.Equal(0, mark.Rect.Y, 3);
            Assert.Equal(100, mark.Rect.Width, 3);
            Assert.Equal(100, mark.Rect.Height, 3);

            PointD topCenter = new(50, 50);
            Assert.Same(mark, controller.HitTest(topCenter));

            byte[] top = Sample(host.Editor.DisplayedBitmap, 50, 25);
            byte[] bottom = Sample(host.Editor.DisplayedBitmap, 50, 150);
            SaveEvidence("rotate-right.png", host.Editor.DisplayedBitmap);

            Assert.True(
                IsRed(top),
                $"Clockwise rotate must put red (left) on top. top={Format(top)} bottom={Format(bottom)}");
            Assert.True(IsBlue(bottom), $"Clockwise rotate must put blue (right) on bottom. bottom={Format(bottom)}");
        });
    }

    [Fact]
    public void TwoRightRotations_KeepDisplayedRegionMatchedToBitmap()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create(RedBlueLandscape(), 200, 100);
            host.Window.UpdateLayout();
            Assert.True(host.Editor.HandleShortcut(Key.W, ModifierKeys.None));
            host.Window.UpdateLayout();
            Assert.True(host.Editor.HandleShortcut(Key.W, ModifierKeys.None));
            host.Window.UpdateLayout();

            Assert.Equal(200, host.Editor.DisplayedBitmap.PixelWidth);
            Assert.Equal(100, host.Editor.DisplayedBitmap.PixelHeight);
            Assert.Equal(
                new RectD(0, 0, host.Editor.DisplayedBitmap.PixelWidth, host.Editor.DisplayedBitmap.PixelHeight),
                host.Editor.DisplayedRegion);
        });
    }

    [Fact]
    public void UndoRotation_RestoresSelectionCropNotDesktopCoordinates()
    {
        StaTestHost.Run(() =>
        {
            BitmapSource bitmap = RedBlueLandscape();
            // The live-capture path stores the desktop crop separately from the editor
            // canvas, which is always the selected pixels at (0,0).
            var desktopCrop = new RectD(14, 18, 200, 100);
            using EditorHost host = EditorHost.Create(bitmap, 200, 100, desktopCrop);
            host.Window.UpdateLayout();
            Assert.True(host.Editor.HandleShortcut(Key.W, ModifierKeys.None));
            host.Window.UpdateLayout();
            Assert.True(host.Editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            host.Window.UpdateLayout();

            Assert.Equal(new RectD(0, 0, 200, 100), host.Editor.DisplayedRegion);
            Assert.Equal(200, host.Editor.DisplayedBitmap.PixelWidth);
            Assert.Equal(100, host.Editor.DisplayedBitmap.PixelHeight);
        });
    }

    [Fact]
    public void LiveTextBox_UsesAnnotationFontAndGrowsWithoutEarlyWrap()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create(Solid(640, 200, 0x20), 640, 200);
            host.Window.UpdateLayout();

            AnnotationEditorSurface surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
            MethodInfo place = typeof(AnnotationEditorControl).GetMethod(
                "PlaceTextBox",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            place.Invoke(host.Editor, [new PointD(20, 20)]);
            host.Window.UpdateLayout();

            TextBox box = FindLiveTextBox(host.Editor);
            string family = box.FontFamily.Source;
            Assert.True(
                family.Contains("Malgun", StringComparison.OrdinalIgnoreCase)
                    || family.Contains("맑은", StringComparison.Ordinal),
                $"Live box must use the annotation font, not the default UI font. actual={family}");
            Assert.Equal(TextWrapping.NoWrap, box.TextWrapping);

            // Wider than the old 180-DIP default, still shorter than the remaining canvas.
            box.Text = "한 줄짜리 긴 문장을 쓰고 싶습니다.";
            host.Window.UpdateLayout();

            Assert.Equal(TextWrapping.NoWrap, box.TextWrapping);
            Assert.True(
                box.ActualHeight < 80,
                $"Sentence should stay on one line before the right edge. height={box.ActualHeight:0} width={box.Width:0}");

            MethodInfo commit = typeof(AnnotationEditorControl).GetMethod(
                "CommitActiveText",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            commit.Invoke(host.Editor, null);

            AnnotationEditorController controller = Field<AnnotationEditorController>(host.Editor, "_controller");
            TextAnnotation text = Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items));
            Assert.DoesNotContain('\n', text.Text.Replace("\r", ""));
            Assert.True(text.Rect.Width > 180, $"Committed box stayed narrow: {text.Rect.Width:0}");
        });
    }

    private static TextBox FindLiveTextBox(AnnotationEditorControl editor)
    {
        var overlay = Field<Canvas>(editor, "_overlayCanvas");
        TextBox box = overlay.Children.OfType<TextBox>().Single();
        return box;
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static BitmapSource RedBlueLandscape()
    {
        const int width = 200;
        const int height = 100;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                bool red = x < width / 2;
                pixels[i] = red ? (byte)0 : (byte)255;
                pixels[i + 1] = 0;
                pixels[i + 2] = red ? (byte)255 : (byte)0;
                pixels[i + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Solid(int width, int height, byte value)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Sample(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static bool IsRed(byte[] bgra) => bgra[2] > 200 && bgra[1] < 40 && bgra[0] < 40;

    private static bool IsBlue(byte[] bgra) => bgra[0] > 200 && bgra[1] < 40 && bgra[2] < 40;

    private static string Format(byte[] bgra) => $"B={bgra[0]} G={bgra[1]} R={bgra[2]}";

    private static void SaveEvidence(string name, BitmapSource bitmap)
    {
        string directory = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "validation", "rotation-text");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class EditorHost : IDisposable
    {
        private EditorHost(Window window, AnnotationEditorControl editor)
        {
            Window = window;
            Editor = editor;
        }

        public Window Window { get; }

        public AnnotationEditorControl Editor { get; }

        public static EditorHost Create(BitmapSource bitmap, int width, int height, RectD? sourceCrop = null)
        {
            var frame = new FrozenFrame(bitmap, new RectD(0, 0, width, height), null, 1);
            var editor = new AnnotationEditorControl(frame, sourceCrop ?? new RectD(0, 0, width, height), bitmap);
            var window = new Window
            {
                Content = editor,
                Width = 1100,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
                ShowActivated = false,
                Left = -4000,
                Top = -4000,
            };
            window.Show();
            window.UpdateLayout();
            return new EditorHost(window, editor);
        }

        public void Dispose() => Window.Close();
    }
}
