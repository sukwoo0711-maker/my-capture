using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    // Initialized before BuildLayout -> BuildBody -> BuildColorSection. This layer is
    // in editor coordinates, not in the zoomed/image-coordinate _overlayCanvas.
    private readonly Canvas _colorFlyoutLayer = new() { ClipToBounds = true };
    private Border _customColorFlyout = null!;
    private Window? _colorFlyoutOwner;
    private HwndSource? _colorFlyoutOwnerSource;
    private bool _positioningColorFlyout;
    private MouseButton? _colorDismissedButton;
    private int? _colorDismissedTouch;

    private bool IsCustomColorFlyoutOpen => _customColorFlyout?.Visibility == Visibility.Visible;

    private void AttachColorFlyoutLayer()
    {
        Grid.SetRowSpan(_colorFlyoutLayer, RowDefinitions.Count);
        Panel.SetZIndex(_colorFlyoutLayer, int.MaxValue);
        Children.Add(_colorFlyoutLayer);
        Loaded += OnColorFlyoutLoaded;
        Unloaded += OnColorFlyoutUnloaded;
    }

    private void InitializeColorFlyout(Border flyout)
    {
        _customColorFlyout = flyout;
        flyout.Visibility = Visibility.Collapsed;
        _colorFlyoutLayer.Children.Add(flyout);
        _customColorInput.PreviewMouseUp += OnCustomColorInputPreviewMouseUp;
        _customColorInput.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(OnCustomColorInputMouseUp), true);
    }

    // The flyout opens with the hex code selected, and this app has the TextBox draw its own
    // selection (no selection adorner), so a press that changes the selection leaves the box's
    // text view waiting for layout. WPF's TextBox lays out such a view on mouse down but not on
    // mouse up, where it releases its mouse capture only if the view is already valid. A release
    // routed before the next layout pass (a press and release injected back to back, as on
    // Windows 10) therefore left capture on the box, and the next click on Apply went to the box
    // (#235). Finish that layout before WPF handles the release.
    private void OnCustomColorInputPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _customColorInput.IsMouseCaptured) _customColorInput.UpdateLayout();
    }

    // Runs after WPF's own handler. If the view still could not lay out, end the finished
    // selection gesture as WPF does, so the box's capture never outlives it.
    private void OnCustomColorInputMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.RightButton != MouseButtonState.Released
            || !_customColorInput.IsMouseCaptured) return;
        e.Handled = true;
        _customColorInput.ReleaseMouseCapture();
    }

    private void OpenCustomColorFlyout()
    {
        if (!IsLoaded || !_customColorButton.IsVisible) return;
        _customColorInput.Text = ColorCode(CurrentPropertyColor());
        _customColorFlyout.Visibility = Visibility.Visible;
        _colorFlyoutLayer.UpdateLayout();
        PositionCustomColorFlyout();
        if (!IsCustomColorFlyoutOpen) return;
        HidePointerPreview();
        _customColorInput.Focus();
        _customColorInput.SelectAll();
    }

    private void CloseCustomColorFlyout(bool returnFocus)
    {
        if (!IsCustomColorFlyoutOpen) return;
        _customColorFlyout.Visibility = Visibility.Collapsed;
        if (returnFocus && _customColorButton.IsVisible) _customColorButton.Focus();
    }

    internal static Point PlaceCustomColorFlyout(Rect opener, Size flyout, Size root)
    {
        double x = Math.Clamp(opener.Left, 0, Math.Max(0, root.Width - flyout.Width));
        double y = opener.Bottom + flyout.Height <= root.Height ? opener.Bottom : opener.Top - flyout.Height;
        return new Point(x, Math.Clamp(y, 0, Math.Max(0, root.Height - flyout.Height)));
    }

    private void PositionCustomColorFlyout()
    {
        if (!IsCustomColorFlyoutOpen || _positioningColorFlyout) return;
        if (!IsLoaded || !_customColorButton.IsVisible || _colorFlyoutLayer.ActualWidth <= 0
            || _colorFlyoutLayer.ActualHeight <= 0)
        {
            CloseCustomColorFlyout(returnFocus: false);
            return;
        }
        _positioningColorFlyout = true;
        try
        {
            _customColorFlyout.Measure(new Size(232, double.PositiveInfinity));
            Rect opener = _customColorButton.TransformToVisual(_colorFlyoutLayer)
                .TransformBounds(new Rect(_customColorButton.RenderSize));
            Point position = PlaceCustomColorFlyout(opener, _customColorFlyout.DesiredSize,
                _colorFlyoutLayer.RenderSize);
            if (Canvas.GetLeft(_customColorFlyout) != position.X) Canvas.SetLeft(_customColorFlyout, position.X);
            if (Canvas.GetTop(_customColorFlyout) != position.Y) Canvas.SetTop(_customColorFlyout, position.Y);
        }
        finally { _positioningColorFlyout = false; }
    }

    private void OnColorFlyoutLoaded(object sender, RoutedEventArgs e)
    {
        DetachColorFlyoutOwner();
        _colorFlyoutOwner = Window.GetWindow(this);
        if (_colorFlyoutOwner is not { } owner) return;
        _colorFlyoutOwnerSource = PresentationSource.FromVisual(owner) as HwndSource;
        _colorFlyoutOwnerSource?.AddHook(ColorFlyoutOwnerHook);
        owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnColorFlyoutMouseDown), true);
        owner.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnColorFlyoutMouseUp), true);
        owner.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnColorFlyoutMouseMove), true);
        owner.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnColorFlyoutTouchDown), true);
        owner.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnColorFlyoutTouchUp), true);
        owner.Deactivated += OnColorFlyoutOwnerDeactivated;
        owner.Closed += OnColorFlyoutOwnerClosed;
        owner.DpiChanged += OnColorFlyoutOwnerDpiChanged;
        owner.LayoutUpdated += OnColorFlyoutLayoutUpdated;
    }

    private void OnColorFlyoutUnloaded(object sender, RoutedEventArgs e)
    {
        CloseCustomColorFlyout(returnFocus: false);
        DetachColorFlyoutOwner();
    }

    private void DetachColorFlyoutOwner()
    {
        if (_colorFlyoutOwnerSource is { IsDisposed: false }) _colorFlyoutOwnerSource.RemoveHook(ColorFlyoutOwnerHook);
        _colorFlyoutOwnerSource = null;
        if (_colorFlyoutOwner is { } owner)
        {
            owner.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnColorFlyoutMouseDown));
            owner.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnColorFlyoutMouseUp));
            owner.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnColorFlyoutMouseMove));
            owner.RemoveHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnColorFlyoutTouchDown));
            owner.RemoveHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnColorFlyoutTouchUp));
            owner.Deactivated -= OnColorFlyoutOwnerDeactivated;
            owner.Closed -= OnColorFlyoutOwnerClosed;
            owner.DpiChanged -= OnColorFlyoutOwnerDpiChanged;
            owner.LayoutUpdated -= OnColorFlyoutLayoutUpdated;
        }
        _colorFlyoutOwner = null;
        _colorDismissedButton = null;
        _colorDismissedTouch = null;
    }

    private void OnColorFlyoutOwnerDeactivated(object? sender, EventArgs e) => CloseCustomColorFlyout(returnFocus: false);
    private void OnColorFlyoutOwnerClosed(object? sender, EventArgs e)
    {
        CloseCustomColorFlyout(returnFocus: false);
        DetachColorFlyoutOwner();
    }
    private void OnColorFlyoutOwnerDpiChanged(object sender, DpiChangedEventArgs e) => PositionCustomColorFlyout();
    private void OnColorFlyoutLayoutUpdated(object? sender, EventArgs e) => PositionCustomColorFlyout();

    private IntPtr ColorFlyoutOwnerHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Preserve the caption/system operation. Only dismiss this editor's content.
        if (message is 0x00A1 or 0x00A4 or 0x00A7 or 0x00AB) // WM_NC*BUTTONDOWN
            CloseCustomColorFlyout(returnFocus: false);
        return IntPtr.Zero;
    }

    private bool IsInsideColorFlyoutOrOpener(object source)
    {
        // A routed input source may be a Run/ContentElement rather than a Visual.
        // Walk its real parent chain without passing a ContentElement to Visual APIs.
        for (DependencyObject? node = source as DependencyObject; node is not null;)
        {
            if (ReferenceEquals(node, _customColorFlyout) || ReferenceEquals(node, _customColorButton)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnColorFlyoutMouseDown(object sender, MouseButtonEventArgs e)
    {
        _colorDismissedButton = null;
        if (!IsCustomColorFlyoutOpen || IsInsideColorFlyoutOrOpener(e.OriginalSource)) return;
        _colorDismissedButton = e.ChangedButton;
        CloseCustomColorFlyout(returnFocus: false);
        // The first outside click dismisses only; it must not begin a canvas gesture.
        e.Handled = true;
    }

    private void OnColorFlyoutMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_colorDismissedButton != e.ChangedButton) return;
        _colorDismissedButton = null;
        e.Handled = true;
    }

    private void OnColorFlyoutMouseMove(object sender, MouseEventArgs e)
    {
        if (_colorDismissedButton is not { } button) return;
        e.Handled = true;
        MouseButtonState state = button switch
        {
            MouseButton.Left => e.LeftButton, MouseButton.Right => e.RightButton,
            MouseButton.Middle => e.MiddleButton, MouseButton.XButton1 => e.XButton1,
            _ => e.XButton2,
        };
        // A release outside this owner need not route MouseUp here. Do not leave
        // hover suppression armed after such a completed dismissing gesture.
        if (state == MouseButtonState.Released) _colorDismissedButton = null;
    }

    private void OnColorFlyoutTouchDown(object? sender, TouchEventArgs e)
    {
        _colorDismissedTouch = null;
        if (!IsCustomColorFlyoutOpen || IsInsideColorFlyoutOrOpener(e.OriginalSource)) return;
        _colorDismissedTouch = e.TouchDevice.Id;
        CloseCustomColorFlyout(returnFocus: false);
        e.Handled = true;
    }

    private void OnColorFlyoutTouchUp(object? sender, TouchEventArgs e)
    {
        if (_colorDismissedTouch != e.TouchDevice.Id) return;
        _colorDismissedTouch = null;
        e.Handled = true;
    }

    internal bool IsCustomColorFlyoutOpenForTest => IsCustomColorFlyoutOpen;
    internal FrameworkElement CustomColorFlyoutContentForTest => _customColorFlyout;
}
