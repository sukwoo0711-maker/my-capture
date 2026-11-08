using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>Bounded windows only. A detached instance has no session, frame, or owner HWND.</summary>
internal sealed class NativeSelectorChrome : IDisposable
{
    private NativeCaptureOverlay? _owner;
    private readonly List<Window> _windows = [];
    private readonly Dictionary<Window, FrameworkElement> _content = [];
    private readonly HashSet<Window> _pendingFrames = [];
    private readonly TextBlock _size, _guideText;
    private readonly CaptureModeBar _bar;
    private readonly Window _barWindow;
    private readonly Window? _sizeWindow, _guideWindow, _magnifierWindow;
    private readonly NativeSelectorStaticChrome? _static;
    private readonly WriteableBitmap? _magnifier;
    private readonly byte[] _sample = new byte[15 * 15 * 4];
    private readonly double _scale;
    private readonly RectD _workArea;
    private readonly string _paletteKey;
    private readonly string _languageKey = UiText.Culture.Name;
    private bool _closed, _invalid, _rendered, _showingWindows, _guideVisible = true;

    internal NativeSelectorChrome(NativeCaptureOverlay owner, bool magnifier, bool requireExclusion)
    {
        CaptureGuideArea area = owner.ChromeArea;
        _scale = area.Scale > 0 ? area.Scale : 1;
        _workArea = area.WorkArea;
        IReadOnlyDictionary<string, Brush> palette = CaptureGuidePalette.Resolve();
        _paletteKey = PaletteKey(palette);
        try
        {
            _bar = owner.ModeBar;
            _bar.HelpRequested += ToggleHelp;
            _bar.ConstrainWidth(_workArea.Width / _scale - 24);
            _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _barWindow = Create(_bar, _bar.DesiredSize.Width, _bar.DesiredSize.Height, interactive: true);
            _size = Text("", palette);
            _guideText = Text(DefaultGuide, palette);
            if (owner.ChromeStrategy == NativeSelectorChromeStrategy.Dib)
                _static = new NativeSelectorStaticChrome(Chip(_size, palette), Chip(_guideText, palette));
            else
            {
                _sizeWindow = Create(Chip(_size, palette), 190, 36, interactive: false);
                _guideWindow = Create(Chip(_guideText, palette), 320, 38, interactive: false);
            }
            if (magnifier)
            {
                _magnifier = new WriteableBitmap(15, 15, 96, 96, PixelFormats.Bgr32, null);
                var image = new Image { Source = _magnifier, Width = 150, Height = 150, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                _magnifierWindow = Create(Chip(image, palette), 166, 166, interactive: false);
            }
            Bind(owner, requireExclusion);
        }
        catch { Dispose(); throw; }
    }

    private static string DefaultGuide => UiText.Get("CaptureGuide2_DragCapture") + "  ·  " + UiText.Get("CaptureGuide2_Esc");
    private static string PaletteKey(IReadOnlyDictionary<string, Brush> palette) =>
        string.Join(";", palette.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
    internal bool AllCaptureExcluded { get; private set; }
    internal IReadOnlyList<Window> Windows => _windows;
    internal bool IsDetached => _owner is null;
    internal int PendingFrameCount => _pendingFrames.Count;
    internal bool CanRetain => !_closed && !_invalid && _rendered && _static is null;

    internal bool CanReuse(NativeCaptureOverlay owner, bool magnifier) =>
        CanRetain && IsDetached && magnifier == (_magnifier is not null)
        && owner.ChromeArea.WorkArea == _workArea && (owner.ChromeArea.Scale > 0 ? owner.ChromeArea.Scale : 1) == _scale
        && PaletteKey(CaptureGuidePalette.Resolve()) == _paletteKey && UiText.Culture.Name == _languageKey;

    internal void Bind(NativeCaptureOverlay owner, bool requireExclusion)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_owner is not null) throw new InvalidOperationException("Chrome already belongs to a selector.");
        SelectorTimeline.Mark?.Invoke("native-chrome-bind-start");
        _owner = owner;
        _rendered = false;
        _showingWindows = false;
        _guideVisible = true;
        owner.AttachModeBar(_bar);
        _bar.ConstrainWidth(_workArea.Width / _scale - 24);
        _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _barWindow.Width = _bar.DesiredSize.Width;
        _barWindow.Height = _bar.DesiredSize.Height;
        _pendingFrames.Clear();
        AllCaptureExcluded = true;
        foreach (Window window in _windows)
        {
            new WindowInteropHelper(window).Owner = owner.Handle;
            if (window.Content is null) window.Content = _content[window];
            // Exclusion is re-applied on every lease, before any of these HWNDs can be shown.
            bool excluded = CaptureWindowExclusion.TryApply(window);
            AllCaptureExcluded &= excluded;
            if (requireExclusion && !excluded) throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
            CaptureWindowExclusion.DisableTransitions(window);
            _pendingFrames.Add(window);
        }
        Place(_barWindow, _workArea.Left + (_workArea.Width - _barWindow.Width * _scale) / 2, _workArea.Top + 12 * _scale, Placement.Layout);
        if (_guideWindow is { } guide)
            Place(guide, _workArea.Left + (_workArea.Width - 320 * _scale) / 2, _workArea.Top + 68 * _scale, Placement.Layout);
        Update(owner.ReadCursor(), Int32Rect.Empty);
        SelectorTimeline.Mark?.Invoke("native-chrome-bind-complete");
    }

    private Window Create(FrameworkElement content, double width, double height, bool interactive)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, AllowsTransparency = false,
            Width = width, Height = height, Background = CaptureWindowExclusion.OpaqueBackground(), Content = content,
            Left = _workArea.Left / _scale, Top = _workArea.Top / _scale,
        };
        _windows.Add(window);
        _content.Add(window, content);
        FluidMotion.SetWindowEntrance(window, false);
        InputMethod.SetIsInputMethodEnabled(window, false);
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (!interactive) WindowStyleFacade.SetClickThrough(handle, true);
            HwndSource.FromHwnd(handle)?.AddHook(OnMessage);
        };
        window.Deactivated += (_, _) => _owner?.CheckFocusLater();
        window.ContentRendered += OnRendered;
        window.PreviewKeyDown += OnKeyDown;
        return window;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E) // Hidden top-level chrome receives display broadcasts too.
        {
            _invalid = true;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                _owner?.Close();
                NativeSelectorChromeCache.Trim();
            }));
        }
        return IntPtr.Zero;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_owner is not { } owner) return;
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (_bar.Holds(e.OriginalSource as DependencyObject))
        {
            if (key == Key.Enter && e.OriginalSource is ButtonBase button)
            {
                _bar.ActivateButton(button);
                e.Handled = true;
                return;
            }
            if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.Space) return;
        }
        e.Handled = owner.HandleKey(key, Keyboard.Modifiers);
    }

    private static TextBlock Text(string text, IReadOnlyDictionary<string, Brush> palette) => new()
    {
        Text = text, Foreground = palette["CaptureGuide.Text"], FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static Border Chip(UIElement child, IReadOnlyDictionary<string, Brush> palette) => new()
    {
        Child = child, Padding = new Thickness(7), Background = palette["CaptureGuide.ChipSurface"],
        BorderBrush = palette["CaptureGuide.ChipBorder"], BorderThickness = new Thickness(1),
    };

    internal void Show()
    {
        _showingWindows = true;
        foreach (Window window in _windows.ToArray())
        {
            if (_closed || _owner is null) return;
            MarkWindow(window, "show-start");
            window.Show();
            if (_closed || _owner is null) return;
            MarkWindow(window, "show-complete");
            Place(window, window.Left * _scale, window.Top * _scale, Placement.Show);
            MarkWindow(window, "placed");
        }
        _owner?.FlushChromeStamps();
    }

    private void OnRendered(object? sender, EventArgs args)
    {
        if (_closed || !_showingWindows || _owner is null || sender is not Window window) return;
        if (!_pendingFrames.Remove(window)) return;
        MarkWindow(window, "rendered");
        if (_pendingFrames.Count == 0)
        {
            _rendered = true;
            _owner.FlushChromeStamps();
            _owner.ChromeReady();
        }
    }

    private void MarkWindow(Window window, string phase)
    {
        if (SelectorTimeline.Mark is not { } mark) return;
        string name = ReferenceEquals(window, _barWindow) ? "bar"
            : ReferenceEquals(window, _sizeWindow) ? "size"
            : ReferenceEquals(window, _guideWindow) ? "guide" : "magnifier";
        mark($"native-chrome-{name}-{phase}");
    }

    private void ToggleHelp()
    {
        if (_owner is null) return;
        _guideVisible = !_guideVisible;
        if (_guideWindow is { } guide) { if (_guideVisible) guide.Show(); else guide.Hide(); }
        Update(_owner.ReadCursor(), _owner.SelectionRectangle);
    }

    internal void Update(PointD pointer, Int32Rect selection)
    {
        if (_closed || _owner is not { } owner) return;
        _size.Text = CaptureOverlayView.FormatReadout(selection.Width, selection.Height, owner.Ratio);
        _guideText.Text = owner.ToastMessage ?? DefaultGuide;
        if (_sizeWindow is { } size) Place(size, pointer.X + 20 * _scale, pointer.Y + 20 * _scale, Follow(size));
        if (_static is not null)
            owner.SetChromeStamps(_static.Update(_size.Text, _guideText.Text, pointer, _workArea, owner.ScreenBounds, _scale, _guideVisible));
        if (_magnifier is not null && _magnifierWindow is not null && owner.Frame is { } frame)
        {
            BitmapSource source = frame.Bitmap;
            int width = Math.Min(15, source.PixelWidth), height = Math.Min(15, source.PixelHeight);
            int x = Math.Clamp((int)(pointer.X - frame.ScreenBounds.Left) - width / 2, 0, source.PixelWidth - width);
            int y = Math.Clamp((int)(pointer.Y - frame.ScreenBounds.Top) - height / 2, 0, source.PixelHeight - height);
            BitmapSource sample = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
            if (sample.Format != PixelFormats.Bgr32) sample = new FormatConvertedBitmap(sample, PixelFormats.Bgr32, null, 0);
            sample.CopyPixels(_sample, 15 * 4, 0);
            _magnifier.WritePixels(new Int32Rect(0, 0, width, height), _sample, 15 * 4, 0);
            Place(_magnifierWindow, pointer.X + 20 * _scale, pointer.Y + 64 * _scale, Follow(_magnifierWindow));
        }
    }

    private enum Placement { Layout, Show, Follow }

    private static Placement Follow(Window window) => window.IsVisible ? Placement.Follow : Placement.Layout;

    private void Place(Window window, double x, double y, Placement placement)
    {
        x = Math.Clamp(x, _workArea.Left, Math.Max(_workArea.Left, _workArea.Right - window.Width * _scale));
        y = Math.Clamp(y, _workArea.Top, Math.Max(_workArea.Top, _workArea.Bottom - window.Height * _scale));
        window.Left = x / _scale;
        window.Top = y / _scale;
        if (placement == Placement.Layout) return;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        var bounds = new RectD(x, y, window.Width * _scale, window.Height * _scale);
        // Restacking a topmost window makes Windows synthesize a mouse move over the selector. When
        // every WM_MOUSEMOVE restacked the size chip and magnifier, a resting pointer fed itself mouse
        // moves (about 110 a second), WPF's input queue never emptied and Input-priority work such as
        // ContentRendered starved. Owned chrome already stays above the background, so after the one
        // topmost placement in Show, following the pointer only moves, and not at all when unchanged.
        if (placement == Placement.Show) PhysicalWindowPositioner.PlaceTopmostWithoutActivation(handle, bounds);
        else PhysicalWindowPositioner.Move(handle, bounds);
    }

    internal void Hide()
    {
        _showingWindows = false;
        _bar?.DismissMenus();
        foreach (Window window in _windows.ToArray()) window.Hide();
    }

    internal void Detach()
    {
        Hide();
        _owner?.DetachModeBar();
        _owner = null;
        _pendingFrames.Clear();
        foreach (Window window in _windows)
        {
            new WindowInteropHelper(window).Owner = IntPtr.Zero;
            // WPF does not raise ContentRendered again for an unchanged hidden/showing Window.
            // Reattach the same small visual on the next lease to observe its new content frame.
            window.Content = null;
        }
        Array.Clear(_sample);
        _magnifier?.WritePixels(new Int32Rect(0, 0, 15, 15), _sample, 15 * 4, 0);
        if (_size is { } size) size.Text = string.Empty;
        if (_guideText is { } guide) guide.Text = DefaultGuide;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Detach();
        if (_bar is { } bar) bar.HelpRequested -= ToggleHelp;
        foreach (Window window in _windows)
        {
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.RemoveHook(OnMessage);
            window.ContentRendered -= OnRendered;
            window.PreviewKeyDown -= OnKeyDown;
            window.Content = null;
            window.Close();
        }
        _windows.Clear();
        _content.Clear();
    }
}
