using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// Shows a real product window far outside every monitor, so it loads, lays out and runs its own
/// code exactly as it does for a user, and renders its client area for a frame. Nothing is shown
/// on screen, activated or added to the taskbar.
/// </summary>
internal sealed class OffscreenWindow : IDisposable
{
    private const double Offscreen = -24000;
    private bool _closed;

    internal OffscreenWindow(Window window, double clientWidth, double clientHeight, bool show = true)
    {
        Window = window;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Offscreen;
        window.Top = Offscreen;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.SizeToContent = SizeToContent.Manual;
        window.Width = clientWidth;
        window.Height = clientHeight;
        if (!show) return;
        window.Show();
        Stage.Pump(60);
        Resize(clientWidth, clientHeight);
    }

    internal Window Window { get; }

    /// <summary>The element that fills the client area (the window template's root).</summary>
    internal FrameworkElement ClientRoot => (FrameworkElement)VisualTreeHelper.GetChild(Window, 0);

    internal Size ClientSize => new(ClientRoot.ActualWidth, ClientRoot.ActualHeight);

    /// <summary>Sets the outer size so the client area has exactly the requested size.</summary>
    internal void Resize(double clientWidth, double clientHeight)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Window.UpdateLayout();
            Size client = ClientSize;
            double dx = clientWidth - client.Width, dy = clientHeight - client.Height;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
            Window.MinWidth = Math.Min(Window.MinWidth, clientWidth);
            Window.MinHeight = Math.Min(Window.MinHeight, clientHeight);
            Window.Width = Window.ActualWidth + dx;
            Window.Height = Window.ActualHeight + dy;
            Stage.Pump(40);
        }
    }

    /// <summary>Renders the client area at output resolution (optionally scaled, still vector sharp).</summary>
    internal BitmapSource Snapshot(double scale = 1)
    {
        Window.UpdateLayout();
        FrameworkElement root = ClientRoot;
        return SnapshotElement(root, root.ActualWidth, root.ActualHeight, scale);
    }

    internal static BitmapSource SnapshotElement(Visual visual, double width, double height, double scale = 1, Brush? backdrop = null)
    {
        double factor = Stage.Scale * scale;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)),
            96 * factor, 96 * factor, PixelFormats.Pbgra32);
        var underlay = new DrawingVisual();
        using (DrawingContext dc = underlay.RenderOpen())
        {
            // HwndSource clears to black before WPF draws a translucent glass Surface.Base.
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            if (backdrop is not null) dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(underlay);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Centre of <paramref name="element"/> in client coordinates.</summary>
    internal PointD CenterOf(FrameworkElement element) => PointOf(element, new Point(element.ActualWidth / 2, element.ActualHeight / 2));

    internal PointD PointOf(FrameworkElement element, Point local)
    {
        if (element.ActualWidth <= 0 && element.ActualHeight <= 0)
            throw new InvalidOperationException($"{element.GetType().Name} is not laid out in {Window.GetType().Name}.");
        Point point = element.TranslatePoint(local, ClientRoot);
        return new PointD(point.X, point.Y);
    }

    internal Rect BoundsOf(FrameworkElement element)
    {
        Point topLeft = element.TranslatePoint(new Point(), ClientRoot);
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
    }

    internal IEnumerable<T> Find<T>() where T : DependencyObject => Descendants(Window).OfType<T>();

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject item = pending.Pop();
            yield return item;
            int count = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(item) : 0;
            for (int index = count - 1; index >= 0; index--) pending.Push(VisualTreeHelper.GetChild(item, index));
        }
    }

    /// <summary>A button (or other element) whose automation name or content text matches.</summary>
    internal T Named<T>(string name) where T : FrameworkElement =>
        Find<T>().FirstOrDefault(element => element.IsVisible && (System.Windows.Automation.AutomationProperties.GetName(element) == name
            || element is ContentControl { Content: string text } && text == name))
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} named '{name}' in {Window.GetType().Name}.");

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { Window.Close(); }
        catch (InvalidOperationException) { }
        Stage.Pump(20);
    }
}
