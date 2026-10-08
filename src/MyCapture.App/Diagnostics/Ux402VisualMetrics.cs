using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Diagnostics;

/// <summary>Measures the live WPF visual tree, including template and group opacity.</summary>
internal static class Ux402VisualMetrics
{
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }

    internal sealed record ShortcutMetric(string Name, string Key, string State, double FontSize,
        string Foreground, string Background, double Contrast, double Minimum, bool Fits, bool Pass);

    internal static List<ShortcutMetric> Shortcuts(Window window)
    {
        var result = new List<ShortcutMetric>();
        Size client = ClientSize(window);
        foreach (var button in Descendants(window).OfType<ButtonBase>().Where(b => b.IsVisible
            && !string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(b))))
        {
            string key = AutomationProperties.GetAcceleratorKey(button);
            var hint = Descendants(button).OfType<TextBlock>().FirstOrDefault(t => t.Text == key);
            if (hint is null) continue;
            bool originalEnabled = button.IsEnabled;
            bool? originalChecked = (button as ToggleButton)?.IsChecked;
            var states = button is ToggleButton ? new[] { "unchecked", "checked" } : new[] { "enabled", "disabled" };
            foreach (string state in states)
            {
                button.SetCurrentValue(UIElement.IsEnabledProperty, state != "disabled");
                if (button is ToggleButton toggle) toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, state == "checked");
                button.UpdateLayout();
                var foreground = Pixel(hint, true);
                var background = Pixel(hint, false);
                double light = Luminance(foreground), dark = Luminance(background);
                double contrast = (Math.Max(light, dark) + .05) / (Math.Min(light, dark) + .05);
                Rect bounds = hint.TransformToAncestor(button).TransformBounds(new Rect(hint.RenderSize));
                Rect buttonBounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                bool fits = bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= button.ActualWidth + .5
                    && bounds.Bottom <= button.ActualHeight + .5 && hint.ActualWidth + .5 >= hint.DesiredSize.Width
                    && buttonBounds.Left >= 0 && buttonBounds.Top >= 0 && buttonBounds.Right <= client.Width + .5
                    && buttonBounds.Bottom <= client.Height + .5;
                double minimum = state == "disabled" ? 3 : 4.5;
                double minimumFont = button is ToggleButton ? 10 : 11;
                result.Add(new(AutomationProperties.GetName(button), key, state, hint.FontSize,
                    Hex(foreground), Hex(background), contrast, minimum, fits, fits && contrast >= minimum && hint.FontSize >= minimumFont));
            }
            button.SetCurrentValue(UIElement.IsEnabledProperty, originalEnabled);
            if (button is ToggleButton restore) restore.SetCurrentValue(ToggleButton.IsCheckedProperty, originalChecked);
        }
        return result;
    }

    // Composite from the text through actual template borders to the render backdrop.
    // This accounts for disabled opacity affecting both text and button background.
    private static Rgba Pixel(TextBlock text, bool ink)
    {
        Rgba pixel = ink ? From(text.Foreground) : default;
        for (DependencyObject? item = text; item is not null; item = VisualTreeHelper.GetParent(item))
        {
            Brush? background = item switch { Border b => b.Background, Panel p => p.Background,
                Window w => w.Background, TextBlock t => t.Background, _ => null };
            pixel = Over(pixel, From(background));
            if (item is UIElement element) pixel = pixel with { A = pixel.A * element.Opacity };
        }
        return Over(pixel, Over(From(text.TryFindResource("Surface.Base") as Brush), new(0, 0, 0, 1)));
    }
    private readonly record struct Rgba(double R, double G, double B, double A);
    private static Rgba From(Brush? brush) => brush is SolidColorBrush solid
        ? new(solid.Color.R / 255.0, solid.Color.G / 255.0, solid.Color.B / 255.0, solid.Color.A / 255.0 * solid.Opacity) : default;
    private static Rgba Over(Rgba top, Rgba bottom)
    {
        double alpha = top.A + bottom.A * (1 - top.A);
        if (alpha == 0) return default;
        return new((top.R * top.A + bottom.R * bottom.A * (1 - top.A)) / alpha,
            (top.G * top.A + bottom.G * bottom.A * (1 - top.A)) / alpha,
            (top.B * top.A + bottom.B * bottom.A * (1 - top.A)) / alpha, alpha);
    }
    private static double Luminance(Rgba c)
    {
        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }
    private static string Hex(Rgba c) => $"#{(int)Math.Round(c.R * 255):X2}{(int)Math.Round(c.G * 255):X2}{(int)Math.Round(c.B * 255):X2}";

    internal sealed record CropPixelMetric(string Region, string Source, string Expected, string Rendered, double ScrimOpacity, bool Pass);
    internal static List<CropPixelMetric> CropPixels(Window window)
    {
        var surface = Descendants(window).OfType<AnnotationEditorSurface>().Single();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var result = new List<CropPixelMetric>();
        Rgba scrim = From(surface.TryFindResource("Crop.Scrim") as Brush);
        foreach (var (region, x, y) in new[] { ("inside", 400, 250), ("outside", 600, 330) })
        {
            Point point = surface.ToSurfacePoint(new PointD(x, y));
            Rgba source = ReadPixel(surface.Frame.Bitmap, x, y);
            Rgba actual = ReadPixel(bitmap, (int)point.X, (int)point.Y);
            Rgba expected = region == "inside" ? source : Over(scrim, source);
            bool pass = Math.Abs(actual.R - expected.R) <= 2.0 / 255
                && Math.Abs(actual.G - expected.G) <= 2.0 / 255 && Math.Abs(actual.B - expected.B) <= 2.0 / 255;
            result.Add(new(region, Hex(source), Hex(expected), Hex(actual), scrim.A, pass));
        }
        return result;
    }
    private static Rgba ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return new(pixel[2] / 255.0, pixel[1] / 255.0, pixel[0] / 255.0, pixel[3] / 255.0);
    }

    internal sealed record CardMetric(int WindowWidth, int WindowHeight, int ClientWidth, int ClientHeight,
        double DpiScale, double ActualWidth, double ActualHeight, double RenderWidth, double RenderHeight,
        double ContentHeight, double ClientHeightDip, double UnusedClientHeight, bool Fits);

    internal static Size ClientSize(Window window)
    {
        if (!GetClientRect(new WindowInteropHelper(window).Handle, out var client))
            throw new InvalidOperationException("Could not measure the render client area.");
        var dpi = VisualTreeHelper.GetDpi(window);
        return new((client.Right - client.Left) / dpi.DpiScaleX, (client.Bottom - client.Top) / dpi.DpiScaleY);
    }

    internal static CardMetric CardGeometry(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var outer) || !GetClientRect(handle, out var client))
            throw new InvalidOperationException("Could not measure the card HWND.");
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        double clientHeight = (client.Bottom - client.Top) / dpi.DpiScaleY;
        double contentHeight = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        return new(outer.Right - outer.Left, outer.Bottom - outer.Top,
            client.Right - client.Left, client.Bottom - client.Top,
            dpi.DpiScaleY, window.ActualWidth, window.ActualHeight, window.RenderSize.Width,
            window.RenderSize.Height, contentHeight, clientHeight,
            clientHeight - contentHeight, Math.Abs(clientHeight - contentHeight) <= 1);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);
}
