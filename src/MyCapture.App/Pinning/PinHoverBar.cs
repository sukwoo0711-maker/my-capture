using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// The small bar a pinned image shows in its top-right corner while the pointer is over it: copy,
/// save, opacity and close, so the commonest pin commands no longer hide in a long tooltip and the
/// right-click menu.
/// </summary>
/// <remarks>
/// It is a separate, never-activated tool window owned by the pin and excluded from screen capture,
/// so it never appears in a screenshot or a recording while the pin itself stays capturable exactly
/// as before. Without Windows animations it appears and disappears at once.
/// </remarks>
internal sealed class PinHoverBar : Window
{
    /// <summary>The opacities the opacity button offers.</summary>
    internal static readonly IReadOnlyList<double> OpacityChoices = [1.0, 0.8, 0.6, 0.4];

    /// <summary>Gap between the bar and the pin's edge, in DIPs.</summary>
    internal const double EdgeGap = 6;

    /// <summary>Excludes the bar from capture; tests substitute a recorder.</summary>
    internal static Func<Window, bool> ApplyCaptureExclusion { get; set; } = CaptureWindowExclusion.TryApply;

    // 20x20 outline glyphs on the shared symbol grid.
    private static readonly Geometry OpacityGlyph = Geometry.Parse(
        "M10,3 A7,7 0 1 1 9.99,3 Z M10,3 L10,17 M10,6 L15.5,6 M10,9 L16.8,9 M10,12 L16.6,12 M10,15 L15,15");

    private readonly List<TextBlock> _labels = [];
    private readonly Border _surface;
    private ContextMenu? _opacityMenu;
    private bool _compact;

    internal PinHoverBar()
    {
        Title = UiText.Get("Onboard_PinBarName");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FluidMotion.SetWindowEntrance(this, false);
        AutomationProperties.SetName(this, UiText.Get("Onboard_PinBarName"));

        CopyButton = BarButton(SymbolGeometry("Icon.Copy"), UiText.Get("QuickAccess.Copy"), "Ctrl+C");
        SaveButton = BarButton(SymbolGeometry("Icon.Save"), UiText.Get("QuickAccess.Save"), "Ctrl+S");
        OpacityButton = BarButton(OpacityGlyph, UiText.Get("Onboard_PinOpacity"), key: null);
        CloseButton = BarButton(SymbolGeometry("Icon.Close"), UiText.Get("Dialog.Close"), "Esc", showLabel: false);
        CopyButton.Click += (_, _) => CopyRequested?.Invoke();
        SaveButton.Click += (_, _) => SaveRequested?.Invoke();
        OpacityButton.Click += (_, _) => OpenOpacityMenu();
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (Button button in new[] { CopyButton, SaveButton, OpacityButton, CloseButton })
            row.Children.Add(button);
        _surface = new Border
        {
            Child = row,
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0),
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        _surface.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        _surface.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        Content = _surface;

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            WindowStyleFacade.SetNoActivate(hwnd, true);
            WindowStyleFacade.ExcludeFromAltTab(hwnd);
            CaptureWindowExclusion.DisableTransitions(this);
            IsCaptureExcluded = ApplyCaptureExclusion(this);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
        };
    }

    internal event Action? CopyRequested;

    internal event Action? SaveRequested;

    internal event Action<double>? OpacityRequested;

    internal event Action? CloseRequested;

    internal Button CopyButton { get; }

    internal Button SaveButton { get; }

    internal Button OpacityButton { get; }

    internal Button CloseButton { get; }

    /// <summary>The window was excluded from screen capture when its handle was created.</summary>
    internal bool IsCaptureExcluded { get; private set; }

    /// <summary>Icons only: the names stay in the tooltips and automation names.</summary>
    internal bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            foreach (TextBlock label in _labels) label.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Reads the pin's current opacity, shown as the checked entry of the opacity menu.</summary>
    internal Func<double> ReadOpacity { get; set; } = () => 1;

    /// <summary>The opacity menu without opening it.</summary>
    internal ContextMenu CreateOpacityMenu()
    {
        var menu = new ContextMenu { PlacementTarget = OpacityButton, Placement = PlacementMode.Bottom, VerticalOffset = 2 };
        AutomationProperties.SetName(menu, UiText.Get("Onboard_PinOpacity"));
        foreach (double choice in OpacityChoices)
        {
            string header = $"{choice * 100:0}%";
            var item = new MenuItem { Header = header, MinHeight = 34, IsCheckable = true, IsChecked = Math.Abs(choice - ReadOpacity()) < 0.05 };
            if (item.IsChecked) item.Icon = Glyph(SymbolGeometry("Icon.Check"), 16);
            AutomationProperties.SetName(item, header);
            double opacity = choice;
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                OpacityRequested?.Invoke(opacity);
            };
            menu.Items.Add(item);
        }

        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource popup) _ = ApplyPopupExclusion(popup.Handle);
        };
        return menu;
    }

    /// <summary>
    /// Where the bar goes, in physical pixels: inside the pin's top-right corner when the pin is large
    /// enough to keep most of its image visible, otherwise just above it (or below it at the top of the
    /// work area), right-aligned and kept on the work area.
    /// </summary>
    internal static RectD Place(RectD pin, double barWidth, double barHeight, RectD workArea, double scale)
    {
        double gap = EdgeGap * (scale > 0 ? scale : 1);
        bool inside = pin.Width >= barWidth + (gap * 4) && pin.Height >= (barHeight + gap) * 2.5;
        double x = pin.Right - barWidth - (inside ? gap : 0);
        double y = inside ? pin.Top + gap
            : pin.Top - barHeight - gap >= workArea.Top ? pin.Top - barHeight - gap
            : pin.Bottom + gap;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - barWidth));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - barHeight));
        return new RectD(Math.Round(x), Math.Round(y), Math.Ceiling(barWidth), Math.Ceiling(barHeight));
    }

    /// <summary>Fades in, or appears at once when Windows animations are off.</summary>
    internal void Reveal()
    {
        BeginAnimation(OpacityProperty, null);
        if (!FluidMotion.AnimationsEnabled)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private void OpenOpacityMenu()
    {
        if (_opacityMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateOpacityMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_opacityMenu, menu)) _opacityMenu = null; };
        _opacityMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>True while the opacity menu is open, so the pin keeps the bar while the pointer is in it.</summary>
    internal bool IsMenuOpen => _opacityMenu?.IsOpen == true;

    private Button BarButton(Geometry glyph, string name, string? key, bool showLabel = true)
    {
        var button = new Button
        {
            MinWidth = 30,
            MinHeight = 30,
            Height = 30,
            Padding = new Thickness(showLabel ? 7 : 6, 0, showLabel ? 9 : 6, 0),
            Margin = new Thickness(1, 0, 1, 0),
            Focusable = false,
            FontSize = 12,
            Cursor = Cursors.Hand,
        };
        button.SetResourceReference(StyleProperty, "Button.Ghost");
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Path path = Glyph(glyph, 16);
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        content.Children.Add(path);
        if (showLabel)
        {
            var label = new TextBlock { Text = name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _labels.Add(label);
            content.Children.Add(label);
        }

        button.Content = content;
        AutomationProperties.SetName(button, name);
        if (key is not null) AutomationProperties.SetAcceleratorKey(button, key);
        button.ToolTip = key is null ? name : $"{name} ({key})";
        ToolTipService.SetInitialShowDelay(button, 300);
        return button;
    }

    private static Path Glyph(Geometry geometry, double size)
    {
        var path = new Path
        {
            Data = geometry, Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetResourceReference(Shape.StrokeProperty, "Text.Primary");
        return path;
    }

    private static Geometry SymbolGeometry(string key) =>
        Application.Current?.TryFindResource(key) as Geometry ?? System.Windows.Media.Geometry.Parse("M4,4 H16 V16 H4 Z");

    private static IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_MOUSEACTIVATE: a press on the bar runs its command without taking the foreground.
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE
    }

    private static bool ApplyPopupExclusion(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, 0x11);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
