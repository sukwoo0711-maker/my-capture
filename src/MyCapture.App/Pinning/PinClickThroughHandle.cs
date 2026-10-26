using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>A capture-excluded recovery target with its own HWND, independent of the transparent pin.</summary>
internal sealed class PinClickThroughHandle : Window
{
    internal const double Diameter = 24;
    internal const double EdgeGap = 6;
    internal static Func<Window, bool> ApplyCaptureExclusion { get; set; } = CaptureWindowExclusion.TryApply;
    private readonly DispatcherTimer _hintTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly PinClickThroughHint _hint;
    private readonly Func<string> _readShortcut;
    private bool _placing;
    private bool _introVisible;
    private bool _hintClosed;

    internal PinClickThroughHandle(PinWindow owner, Func<string> readShortcut)
    {
        Owner = owner;
        _readShortcut = readShortcut;
        Configure(this);
        Width = Height = Diameter;
        Title = UiText.Get("Pin.ClickThroughOff");
        AutomationProperties.SetName(this, Title);
        var glyph = SymbolIcon.Create(Geometry.Parse("M4,2 L4,14 L7.5,10.5 L10,16 L12.5,15 L10,9.5 L15,9.5 Z M14,3 L18,3 M16,1 L16,5"), 16);
        glyph.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Primary");
        RecoveryButton = new Button
        {
            Width = Diameter, Height = Diameter, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(1),
            Content = glyph, Focusable = false, Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Local template keeps this target circular regardless of the global button min-size.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Diameter / 2));
        border.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        border.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        RecoveryButton.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        Content = RecoveryButton;
        AutomationProperties.SetName(RecoveryButton, Title);
        RecoveryButton.Click += (_, _) => DisableRequested?.Invoke();
        // Owned by the pin as well: WPF rejects an owner that has not been shown yet.
        _hint = new PinClickThroughHint(owner);
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _introVisible = false; _hint.Hide(); };
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (IsVisible && IsMouseOver && !_introVisible) ShowHint(TooltipText);
        };
        MouseEnter += (_, _) => { SetEmphasis(true); RefreshShortcut(); _hoverTimer.Start(); };
        MouseLeave += (_, _) => { SetEmphasis(false); _hoverTimer.Stop(); if (!_introVisible) _hint.Hide(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideHint(); };
        _hint.Closed += (_, _) => _hintClosed = true;
        Closed += (_, _) => { HideHint(); if (!_hintClosed) _hint.Close(); };
        SourceInitialized += (_, _) =>
        {
            ConfigureHandle(this, clickThrough: false);
            IsCaptureExcluded = ApplyCaptureExclusion(this);
            ApplyRoundRegion();
        };
        SizeChanged += (_, _) => ApplyRoundRegion();
        RefreshShortcut();
        SetEmphasis(false);
    }

    internal event Action? DisableRequested;
    internal Button RecoveryButton { get; }
    internal bool IsCaptureExcluded { get; private set; }
    internal string TooltipText { get; private set; } = string.Empty;
    internal PinClickThroughHint HintForTest => _hint;

    private void RefreshShortcut()
    {
        string shortcut = _readShortcut();
        TooltipText = string.IsNullOrEmpty(shortcut) ? Title : $"{Title} ({shortcut})";
        AutomationProperties.SetHelpText(RecoveryButton, TooltipText);
        AutomationProperties.SetAcceleratorKey(RecoveryButton, shortcut);
        // Use the excluded hint HWND for hover text; a standard ToolTip creates an unexcluded popup.
    }

    private void SetEmphasis(bool hover)
    {
        // Older Windows cannot exclude layered windows. Keep its opaque HWND and dim only content.
        if (AllowsTransparency) Opacity = hover ? 1 : 0.7;
        else RecoveryButton.Opacity = hover ? 1 : 0.7;
    }

    private void ApplyRoundRegion()
    {
        if (AllowsTransparency) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        IntPtr region = CreateEllipticRgn(0, 0, (int)Math.Ceiling(Diameter * dpi.DpiScaleX), (int)Math.Ceiling(Diameter * dpi.DpiScaleY));
        // Windows takes ownership only after a successful SetWindowRgn.
        if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) _ = DeleteObject(region);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyRoundRegion();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Place));
    }

    internal bool TryReveal()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        if (!IsCaptureExcluded) return false;
        Place();
        Show();
        Place();
        return true;
    }

    internal bool ShowIntroduction()
    {
        if (!ShowHint(UiText.Get("Pin.ClickThroughIntro"))) return false;
        _introVisible = true;
        _hintTimer.Start();
        return true;
    }

    private bool ShowHint(string text)
    {
        _hint.Text.Text = text;
        bool shown = _hint.TryReveal();
        Place();
        return shown;
    }

    internal void HideHint()
    {
        _hoverTimer.Stop();
        _hintTimer.Stop();
        _introVisible = false;
        _hint.Hide();
    }

    internal static RectD Placement(RectD pin, double scale)
    {
        double size = Diameter * scale;
        double gap = EdgeGap * scale;
        return new RectD(pin.Right - size - gap, pin.Top + gap, size, size);
    }

    internal void Place()
    {
        if (_placing || Owner is not PinWindow pin || pin.IsClosed) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var (left, top, right, bottom) = pin.PhysicalBounds;
        if (right <= left || bottom <= top) return;
        _placing = true;
        try
        {
            double scale = VisualTreeHelper.GetDpi(pin).DpiScaleX;
            RectD target = Placement(new RectD(left, top, right - left, bottom - top), scale);
            // Move preserves hidden state and never activates. WPF owns DIP sizing across monitors.
            PhysicalWindowPositioner.Move(hwnd, target);
            if (_hint.IsVisible)
            {
                _hint.UpdateLayout();
                double width = _hint.ActualWidth * scale;
                double height = _hint.ActualHeight * scale;
                RectD work = pin.ReadWorkArea(new WindowInteropHelper(pin).Handle);
                double x = Math.Clamp(target.Left - width - EdgeGap * scale, work.Left, Math.Max(work.Left, work.Right - width));
                double y = Math.Clamp(target.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
                PhysicalWindowPositioner.Move(new WindowInteropHelper(_hint).Handle, new RectD(x, y, width, height));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Existing global recovery commands remain available when Windows refuses placement.
        }
        finally { _placing = false; }
    }

    internal static void Configure(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.AllowsTransparency = CaptureWindowExclusion.SupportsLayeredWindows;
        window.Background = window.AllowsTransparency ? Brushes.Transparent : CaptureWindowExclusion.OpaqueBackground();
        if (!window.AllowsTransparency) ModernWindowChrome.SetEnabled(window, false);
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Topmost = true;
        window.Focusable = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -32000;
        FluidMotion.SetWindowEntrance(window, false);
        StandardWindowTheme.ApplyFloatingText(window);
    }

    internal static void ConfigureHandle(Window window, bool clickThrough)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        WindowStyleFacade.SetNoActivate(hwnd, true);
        WindowStyleFacade.ExcludeFromAltTab(hwnd);
        // Do not turn the opaque fallback into a layered window: that defeats exclusion on Windows 10.
        if (clickThrough && window.AllowsTransparency) WindowStyleFacade.SetClickThrough(hwnd, true);
        CaptureWindowExclusion.DisableTransitions(window);
        HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3);
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}

/// <summary>Shared first-use/hover text. Its separate HWND passes clicks through, including over other apps.</summary>
internal sealed class PinClickThroughHint : Window
{
    internal PinClickThroughHint(Window owner)
    {
        Owner = owner;
        PinClickThroughHandle.Configure(this);
        SizeToContent = SizeToContent.WidthAndHeight;
        Text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 };
        Text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var border = new Border { Child = Text, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        border.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        Content = border;
        IsHitTestVisible = false;
        SourceInitialized += (_, _) =>
        {
            PinClickThroughHandle.ConfigureHandle(this, clickThrough: true);
            IsCaptureExcluded = PinClickThroughHandle.ApplyCaptureExclusion(this);
            if (!AllowsTransparency)
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(PassiveHitTest);
        };
    }

    internal TextBlock Text { get; }
    internal bool IsCaptureExcluded { get; private set; }
    internal bool TryReveal()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        if (!IsCaptureExcluded) return false;
        Show();
        return true;
    }

    private IntPtr PassiveHitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0084) return IntPtr.Zero;
        // An opaque tooltip cannot use layered cross-process hit testing. Retire it as soon as
        // the pointer reaches it so it never becomes a persistent obstacle to the app underneath.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Hide));
        handled = true;
        return new IntPtr(-1); // HTTRANSPARENT
    }
}
