using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Gallery;

internal sealed class GalleryCompareWindow : Window
{
    internal GalleryCompareWindow(BitmapSource first, BitmapSource second,
        string firstTitle, DateTimeOffset firstTime, string secondTitle, DateTimeOffset secondTime)
    {
        Title = UiText.Get("Gallery.Compare");
        Width = 1120;
        Height = 760;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");

        var viewer = new CompareSurface(first, second);
        var mode = new ComboBox { MinWidth = 150, SelectedIndex = 0, Margin = new Thickness(0, 0, 20, 0) };
        mode.Items.Add(UiText.Get("Gallery.Compare.SideBySide"));
        mode.Items.Add(UiText.Get("Gallery.Compare.Overlay"));
        AutomationProperties.SetName(mode, UiText.Get("Gallery.Compare.Mode"));
        mode.SelectionChanged += (_, _) => viewer.SetOverlay(mode.SelectedIndex == 1);
        var sync = new CheckBox
        {
            Content = UiText.Get("Gallery.Compare.Sync"), IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sync.Checked += (_, _) => viewer.SetSynchronized(true);
        sync.Unchecked += (_, _) => viewer.SetSynchronized(false);
        var close = new Button { Content = UiText.Get("Text_1E8C10206F5B"), MinWidth = 76 };
        close.SetResourceReference(StyleProperty, "Button.Ghost");
        close.Click += (_, _) => Close();

        var toolbar = new DockPanel { Margin = new Thickness(16, 12, 16, 12), LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        toolbar.Children.Add(close);
        toolbar.Children.Add(mode);
        toolbar.Children.Add(sync);
        var headers = new Grid { Margin = new Thickness(16, 0, 16, 12) };
        headers.ColumnDefinitions.Add(new ColumnDefinition());
        headers.ColumnDefinitions.Add(new ColumnDefinition());
        StackPanel left = Header(firstTitle, firstTime);
        StackPanel right = Header(secondTitle, secondTime);
        Grid.SetColumn(right, 1);
        headers.Children.Add(left);
        headers.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(headers, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(headers);
        root.Children.Add(viewer);
        Content = root;
        Loaded += (_, _) => viewer.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
            {
                viewer.Swap();
                Grid.SetColumn(left, 1 - Grid.GetColumn(left));
                Grid.SetColumn(right, 1 - Grid.GetColumn(right));
                e.Handled = true;
            }
        };
    }

    private static StackPanel Header(string title, DateTimeOffset time)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title,
        });
        var timestamp = new TextBlock { Text = time.ToString("yyyy-MM-dd HH:mm:ss zzz") };
        timestamp.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        panel.Children.Add(timestamp);
        return panel;
    }

    private sealed class CompareSurface : FrameworkElement
    {
        private readonly BitmapSource[] _images;
        private readonly CompareViewport[] _views = new CompareViewport[2];
        private readonly double _canvasWidth;
        private readonly double _canvasHeight;
        private bool _overlay;
        private bool _synchronized = true;
        private bool _initialized;
        private bool _dragDivider;
        private int _active;
        private Point _previous;
        private double _divider = 0.5;

        internal CompareSurface(BitmapSource first, BitmapSource second)
        {
            _images = [first, second];
            (_canvasWidth, _canvasHeight) = CompareViewport.CanvasSize(
                first.PixelWidth, first.PixelHeight, second.PixelWidth, second.PixelHeight);
            Focusable = true;
            ClipToBounds = true;
            AutomationProperties.SetName(this, UiText.Get("Gallery.Compare.View"));
            ToolTip = UiText.Get("Gallery.Compare.Controls");
            SetResourceReference(SurfaceBrushProperty, "Surface.Overlay");
            SetResourceReference(DividerBrushProperty, "Text.Primary");
            SizeChanged += (_, _) => { if (!_initialized) Fit(); InvalidateVisual(); };
            LostMouseCapture += (_, _) => { _dragDivider = false; Cursor = Cursors.Arrow; };
        }

        private static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
            "SurfaceBrush", typeof(Brush), typeof(CompareSurface),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(
            "DividerBrush", typeof(Brush), typeof(CompareSurface),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        private double FitScale => CompareViewport.FitScale(_canvasWidth, _canvasHeight, ActualWidth, ActualHeight, _overlay);

        private void Fit()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            _views[0] = _views[1] = new CompareViewport(FitScale, 0, 0);
            _initialized = true;
        }

        internal void SetOverlay(bool overlay)
        {
            _overlay = overlay;
            Fit();
            InvalidateVisual();
        }

        internal void SetSynchronized(bool synchronized)
        {
            _synchronized = synchronized;
            if (synchronized) _views[1 - _active] = _views[_active];
            InvalidateVisual();
        }

        internal void Swap()
        {
            (_images[0], _images[1]) = (_images[1], _images[0]);
            (_views[0], _views[1]) = (_views[1], _views[0]);
            _active = 1 - _active;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle((Brush)GetValue(SurfaceBrushProperty), null, new Rect(RenderSize));
            double split = ActualWidth * (_overlay ? _divider : 0.5);
            for (int index = 0; index < 2; index++)
            {
                double left = index == 0 ? 0 : split;
                dc.PushClip(new RectangleGeometry(new Rect(left, 0,
                    index == 0 ? split : ActualWidth - split, ActualHeight)));
                CompareViewport view = _views[index];
                double origin = !_overlay && index == 1 ? split : 0;
                dc.DrawImage(_images[index], new Rect(origin + view.X, view.Y,
                    _images[index].PixelWidth * view.Scale, _images[index].PixelHeight * view.Scale));
                dc.Pop();
            }
            var pen = new Pen((Brush)GetValue(DividerBrushProperty), _overlay ? 2 : 1);
            double line = Math.Clamp(split, 1, Math.Max(1, ActualWidth - 1));
            dc.DrawLine(pen, new Point(line, 0), new Point(line, ActualHeight));
            if (_overlay)
                dc.DrawRoundedRectangle((Brush)GetValue(SurfaceBrushProperty), pen,
                    new Rect(line - 5, Math.Max(0, ActualHeight / 2 - 20), 10, 40), 3, 3);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            Point point = e.GetPosition(this);
            _active = SideAt(point);
            double x = point.X - (!_overlay && _active == 1 ? ActualWidth / 2 : 0);
            _views[_active] = _views[_active].ZoomAt(Math.Pow(1.2, e.Delta / 120.0), x, point.Y,
                FitScale / 16, Math.Max(32, FitScale));
            PublishView();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            _previous = e.GetPosition(this);
            _active = SideAt(_previous);
            _dragDivider = NearDivider(_previous);
            CaptureMouse();
            Cursor = _dragDivider ? Cursors.SizeWE : Cursors.Hand;
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point point = e.GetPosition(this);
            if (!IsMouseCaptured) { Cursor = NearDivider(point) ? Cursors.SizeWE : Cursors.Arrow; return; }
            if (_dragDivider) _divider = CompareViewport.DividerFraction(point.X, ActualWidth);
            else _views[_active] = _views[_active].Pan(point.X - _previous.X, point.Y - _previous.Y);
            _previous = point;
            PublishView();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_overlay && e.Key is Key.Left or Key.Right)
            {
                _divider = Math.Clamp(_divider + (e.Key == Key.Left ? -0.02 : 0.02), 0, 1);
                InvalidateVisual();
                e.Handled = true;
            }
        }

        private int SideAt(Point point) => point.X < ActualWidth * (_overlay ? _divider : 0.5) ? 0 : 1;
        private bool NearDivider(Point point) => _overlay && Math.Abs(point.X - ActualWidth * _divider) <= 8;
        private void PublishView()
        {
            if (_synchronized) _views[1 - _active] = _views[_active];
            InvalidateVisual();
        }
    }
}
