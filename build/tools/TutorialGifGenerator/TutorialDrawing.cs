using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;


internal static class TutorialDrawing
{
    internal const int Width = 640, Height = 360, SceneHeight = 312;
    internal const int SelectorWidth = 864, SelectorHeight = 421;
    internal const double SceneScale = 1.35;
    /// <summary>Draw fixture content in a logical 640x312 scene; raster backing is 864x421.</summary>
    internal static BitmapSource DrawScene(Action<DrawingContext> render) => Draw(SceneHeight, render);
    /// <summary>Draw an opaque 640x360 final frame, including any caption area.</summary>
    internal static BitmapSource DrawFrame(Action<DrawingContext> render) => Draw(Height, render);
    internal static BitmapSource Blend(BitmapSource from, BitmapSource to, double progress) => Draw(SceneHeight, dc =>
    {
        dc.DrawImage(from, new Rect(0, 0, Width, SceneHeight));
        dc.PushOpacity(TutorialStoryboard.SmoothStep(progress));
        dc.DrawImage(to, new Rect(0, 0, Width, SceneHeight));
        dc.Pop();
    });

    internal static BitmapSource Compose(BitmapSource scene, string caption, string key = "", PointD? demoCursor = null,
        PointD clickAt = default, double clickProgress = 0) => Draw(Height, dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, Height));
        dc.DrawImage(scene, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 21, 29)), null, new Rect(0, SceneHeight, Width, Height - SceneHeight));
        Text(dc, caption, 20, 326, 18, Brushes.White);
        if (clickProgress > 0 && clickProgress < 1)
        {
            dc.PushOpacity(Math.Sin(clickProgress * Math.PI) * 0.85);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(163, 144, 255)), 2),
                new Point(clickAt.X, clickAt.Y), 7 + clickProgress * 14, 7 + clickProgress * 14);
            dc.Pop();
        }
        if (demoCursor is { } pointer)
        {
            dc.PushTransform(new TranslateTransform(pointer.X, pointer.Y));
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), Geometry.Parse("M0,0 L0,18 L4.5,14 L8,21 L11,19.5 L7.5,12.5 L14,12 Z"));
            dc.Pop();
        }
        if (key.Length > 0)
        {
            int badgeWidth = key.Length == 1 ? 36 : 84;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(48, 57, 72)), new Pen(new SolidColorBrush(Color.FromRgb(112, 126, 145)), 1), new Rect(Width - badgeWidth - 18, 320, badgeWidth, 32), 5, 5);
            Text(dc, key, Width - badgeWidth - 10, 326, 16, Brushes.White, mono: true);
        }
    });

    internal static BitmapSource Draw(int height, Action<DrawingContext> render)
    {
        var visual = new DrawingVisual();
        bool scene = height == SceneHeight;
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (scene) dc.PushTransform(new ScaleTransform(SceneScale, SceneScale));
            render(dc);
            if (scene) dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(scene ? SelectorWidth : Width, scene ? SelectorHeight : height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    internal static void Text(DrawingContext dc, string value, double x, double y, double size, Brush brush, bool mono = false)
    {
        var family = (FontFamily)Application.Current.Resources[mono ? "Font.Mono" : "Font.Ui"];
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, 1);
        dc.DrawText(text, new Point(x, y));
    }

    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
