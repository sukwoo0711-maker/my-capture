using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal static class TimelineVisuals
{
    private static readonly DependencyProperty PaletteObserverProperty = DependencyProperty.RegisterAttached(
        "PaletteObserver", typeof(PaletteObserver), typeof(TimelineVisuals));
    private static readonly DependencyProperty PaletteTokenProperty = DependencyProperty.RegisterAttached(
        "PaletteToken", typeof(Brush), typeof(TimelineVisuals), new PropertyMetadata(null, OnPaletteChanged));

    // Resource brushes are replaced, not recoloured. Coalesce the resource callbacks so all
    // tokens have been replaced before rebuilding frozen pens and drawing layers.
    internal static void ObservePalette(FrameworkElement owner, Action refresh)
    {
        owner.SetValue(PaletteObserverProperty, new PaletteObserver(refresh));
        owner.SetResourceReference(PaletteTokenProperty, "Surface.Base");
        owner.Loaded += (_, _) => refresh();
    }

    private static void OnPaletteChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner.GetValue(PaletteObserverProperty) is not PaletteObserver observer || observer.Pending) return;
        observer.Pending = true;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            observer.Pending = false;
            observer.Refresh();
        }));
    }

    private sealed class PaletteObserver(Action refresh)
    {
        internal Action Refresh { get; } = refresh;
        internal bool Pending { get; set; }
    }

    internal static string SnapLabel(VideoSnapKind kind) => UiText.Get(kind switch
    {
        VideoSnapKind.Playhead => "VideoEditorUx_SnapPlayhead",
        VideoSnapKind.KeptStart => "VideoEditorUx_SnapStart",
        VideoSnapKind.KeptEnd => "VideoEditorUx_SnapEnd",
        _ => "VideoEditorUx_SnapLayer",
    });

    internal static void DrawMarker(FrameworkElement owner, DrawingContext dc, double x, double height,
        double left, double right, Brush line, Brush background, Brush foreground, string? label, double labelTop = 1)
    {
        dc.DrawLine(new Pen(line, 1), new Point(x, 0), new Point(x, height));
        if (label is null || right <= left + 12) return;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, foreground, VisualTreeHelper.GetDpi(owner).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, right - left - 12), MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        double width = text.Width + 10;
        double chipX = Math.Clamp(x + 5, left, Math.Max(left, right - width));
        dc.DrawRoundedRectangle(background, new Pen(line, 1), new Rect(chipX, labelTop, width, text.Height + 4), 3, 3);
        dc.DrawText(text, new Point(chipX + 5, labelTop + 2));
    }
}
