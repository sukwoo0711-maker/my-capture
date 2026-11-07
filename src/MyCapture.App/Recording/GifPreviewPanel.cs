using MyCapture.App.Themes;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MyCapture.App.Recording;

/// <summary>
/// Plays the GIF preview sample at its real frame delays, with a frame slider, an optional
/// side-by-side original of the same moment and a 1:1 pixel view whose two sides scroll
/// together. Playback starts paused when Windows animation effects are off.
/// </summary>
internal sealed class GifPreviewPanel : Border
{
    internal const double ViewportHeight = 248;

    private readonly Func<GifPreviewSample, int, CancellationToken, Task<BitmapSource>> _renderOriginal;
    private readonly GifSamplePlayer _player;
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };
    // Hidden twins stand in for what a sample brings. The info line's twin, with the paused-start
    // notice, shares the line's cell, so the card keeps its height as a sample arrives. The twins
    // of the play captions and the widest frame counter sit in canvases, which take no space:
    // they only enter the decision to wrap the controls, so the controls keep their rows as a
    // sample arrives, plays or pauses, while the button and the counter keep their own widths.
    private readonly TextBlock _infoReserve = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Hidden };
    private readonly TextBlock _frameReserve = new() { Visibility = Visibility.Hidden };
    private readonly TextBlock[] _playCaptionReserves;
    // The widest info line a sample of the source can show (see WidestInfo). Word for word it is
    // at least as wide as any real line, so it wraps into at least as many lines.
    private readonly string _widestInfo;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
    private readonly Grid _pair = new();
    private readonly Image _gif = new() { Stretch = Stretch.Uniform };
    private readonly Image _original = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _gifView = new();
    private readonly ScrollViewer _originalView = new();
    private readonly TextBlock _gifCaption = new() { Margin = new Thickness(0, 0, 0, 4), Text = "GIF" };
    private readonly TextBlock _originalCaption = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly FrameworkElement _originalColumn;
    private readonly Button _play = new();
    private readonly Path _playIcon = new() { Width = 16, Height = 16, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _playText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _position = new() { Minimum = 0, Maximum = 0, MinWidth = 64, SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    private readonly TextBlock _frameLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };
    private readonly ToggleButton _compare = new() { Margin = new Thickness(8, 0, 0, 0) };
    private readonly ToggleButton _actualSize = new() { Margin = new Thickness(6, 0, 0, 0) };
    private GifPreviewSample? _sample;
    private CancellationTokenSource? _originalCancellation;
    private int _originalFrame = -1;
    private bool _syncingSlider;
    private bool _syncingScroll;

    /// <param name="renderOriginal">Renders the original of a sample frame for comparison.</param>
    /// <param name="sourceDurationMs">Length of the recording the samples come from, for the info line's reserve; 0 when unknown.</param>
    internal GifPreviewPanel(Func<GifPreviewSample, int, CancellationToken, Task<BitmapSource>> renderOriginal, double sourceDurationMs = 0)
    {
        _renderOriginal = renderOriginal;
        _player = new GifSamplePlayer(Dispatcher);
        SetResourceReference(StyleProperty, "Card.Sunken");
        Margin = new Thickness(0, 12, 0, 4);
        Padding = new Thickness(12);
        AutomationProperties.SetName(this, UiText.Get("GifPreview_Heading"));

        var root = new StackPanel();
        var heading = new TextBlock { Text = UiText.Get("GifPreview_Heading"), FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        root.Children.Add(heading);
        _info.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _infoReserve.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var infoCell = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        infoCell.Children.Add(_info);
        infoCell.Children.Add(_infoReserve);
        root.Children.Add(infoCell);
        _widestInfo = WidestInfo(sourceDurationMs);
        ReserveInfo(notice: !FluidMotion.AnimationsEnabled);

        _originalCaption.Text = UiText.Get("GifPreview_Original");
        foreach (TextBlock caption in new[] { _gifCaption, _originalCaption })
        {
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            caption.Visibility = Visibility.Collapsed;
        }

        AutomationProperties.SetName(_gif, UiText.Get("GifPreview_GifFrame"));
        AutomationProperties.SetName(_original, UiText.Get("GifPreview_OriginalFrame"));
        AutomationProperties.SetName(_gifView, UiText.Get("GifPreview_GifFrame"));
        AutomationProperties.SetName(_originalView, UiText.Get("GifPreview_OriginalFrame"));
        RenderOptions.SetBitmapScalingMode(_original, BitmapScalingMode.HighQuality);
        _pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _originalColumn = Column(_originalCaption, _originalView, _original, 0);
        _originalColumn.Visibility = Visibility.Collapsed;
        _pair.Children.Add(_originalColumn);
        _pair.Children.Add(Column(_gifCaption, _gifView, _gif, 1));
        // In the 1:1 view both sides show the same region: scrolling one moves the other.
        _gifView.ScrollChanged += (_, _) => SyncScroll(_gifView, _originalView);
        _originalView.ScrollChanged += (_, _) => SyncScroll(_originalView, _gifView);
        var stage = new Grid { Height = ViewportHeight, ClipToBounds = true };
        stage.Children.Add(_pair);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        stage.Children.Add(_message);
        root.Children.Add(stage);

        var controls = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        foreach (GridLength width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
        {
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _play.SetResourceReference(StyleProperty, "Button.Secondary");
        CommandButtonPresentation.Apply(_play, compact: true);
        CommandButtonPresentation.Apply(_compare, compact: true);
        CommandButtonPresentation.Apply(_actualSize, compact: true);
        _playIcon.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = _play });
        var playContent = new StackPanel { Orientation = Orientation.Horizontal };
        playContent.Children.Add(_playIcon);
        playContent.Children.Add(_playText);
        // Measured in the button's font for the wrap decision below; the canvas adds no width.
        _playCaptionReserves = [.. new[] { "GifPreview_Play", "GifPreview_Pause" }
            .Select(key => new TextBlock { Text = UiText.Get(key), Visibility = Visibility.Hidden })];
        var playCaptionHost = new Canvas();
        foreach (TextBlock reserve in _playCaptionReserves) playCaptionHost.Children.Add(reserve);
        playContent.Children.Add(playCaptionHost);
        _play.Content = playContent;
        _play.Click += (_, _) => TogglePlayback();
        AutomationProperties.SetName(_position, UiText.Get("GifPreview_Position"));
        _position.ValueChanged += (_, e) =>
        {
            if (_syncingSlider || _sample is null) return;
            _player.Pause();
            _player.Seek((int)Math.Round(e.NewValue));
            UpdatePlayButton();
        };
        _frameLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _frameReserve.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _frameReserve.Text = UiText.Format("GifPreview_Frame", 444, 444);
        Configure(_compare, "GifPreview_Compare", "GifPreview_CompareHelp");
        Configure(_actualSize, "GifPreview_ActualSize", "GifPreview_ActualSizeHelp");
        _compare.Checked += (_, _) => { _player.Pause(); UpdatePlayButton(); ApplyLayout(); };
        _compare.Unchecked += (_, _) => ApplyLayout();
        _actualSize.Checked += (_, _) => ApplyLayout();
        _actualSize.Unchecked += (_, _) => ApplyLayout();
        var frameReserveHost = new Canvas();
        frameReserveHost.Children.Add(_frameReserve);
        AddAt(controls, _play, 0);
        AddAt(controls, _position, 1);
        AddAt(controls, frameReserveHost, 2);
        AddAt(controls, _frameLabel, 2);
        AddAt(controls, _compare, 3);
        AddAt(controls, _actualSize, 4);
        bool wrappedControls = false;
        controls.LayoutUpdated += (_, _) =>
        {
            if (controls.ActualWidth <= 0) return;
            // Count the longer play caption and the widest counter, not the current ones.
            double caption = _playCaptionReserves.Max(reserve => reserve.DesiredSize.Width);
            double play = _play.DesiredSize.Width
                + (_playText.Visibility == Visibility.Visible ? Math.Max(0, caption - _playText.DesiredSize.Width) : 0);
            double frame = Math.Max(_frameLabel.DesiredSize.Width, _frameReserve.DesiredSize.Width);
            double needed = play + frame
                + _compare.DesiredSize.Width + _actualSize.DesiredSize.Width
                + _position.MinWidth + _position.Margin.Left + _position.Margin.Right;
            bool wrap = needed > controls.ActualWidth + 0.5;
            if (wrap == wrappedControls) return;
            wrappedControls = wrap;
            // Preserve a usable seek bar at narrow widths or enlarged text sizes. The
            // image viewport stays fixed; comparison controls use a second row only as needed.
            Grid.SetColumnSpan(_position, wrap ? 3 : 1);
            Grid.SetColumn(_frameLabel, wrap ? 4 : 2);
            Grid.SetRow(_compare, wrap ? 1 : 0);
            Grid.SetRow(_actualSize, wrap ? 1 : 0);
            _compare.Margin = new Thickness(8, wrap ? 6 : 0, 0, 0);
            _actualSize.Margin = new Thickness(6, wrap ? 6 : 0, 0, 0);
        };
        root.Children.Add(controls);
        Child = root;

        _player.FrameChanged += ShowFrame;
        Unloaded += (_, _) => Stop();
        SetControlsEnabled(false);
        ApplyLayout();
        UpdatePlayButton();
    }

    internal bool IsPlaying => _player.IsPlaying;

    internal int FrameIndex => _player.Index;

    internal int FrameCount => _sample?.Frames.Frames.Count ?? 0;

    internal bool HasSample => _sample is not null;

    internal bool IsComparing => _compare.IsChecked == true;

    internal BitmapSource? OriginalFrame => _original.Source as BitmapSource;

    internal string InfoText => KoreanWordWrap.Strip(_info.Text);

    internal string MessageText => _message.Visibility == Visibility.Visible ? KoreanWordWrap.Strip(_message.Text) : string.Empty;

    internal void ShowPreparing()
    {
        Detach();
        KoreanWordWrap.Show(_info, string.Empty);
        ReserveInfo(notice: !FluidMotion.AnimationsEnabled);
        ShowMessage(UiText.Get("GifPreview_Preparing"));
    }

    internal void ShowFailure(string message)
    {
        Detach();
        KoreanWordWrap.Show(_info, string.Empty);
        ReserveInfo(notice: !FluidMotion.AnimationsEnabled);
        ShowMessage(UiText.Format("GifPreview_Failed", message));
    }

    /// <summary>Shows a sample the caller keeps owning; playback starts unless motion is reduced.</summary>
    internal void ShowSample(GifPreviewSample sample, bool autoplay)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Detach();
        _sample = sample;
        GifSampleFrames frames = sample.Frames;
        _message.Visibility = Visibility.Collapsed;
        _pair.Visibility = Visibility.Visible;
        _syncingSlider = true;
        _position.Maximum = Math.Max(0, frames.Frames.Count - 1);
        _position.Value = 0;
        _syncingSlider = false;
        string info = UiText.Format("GifPreview_Info", sample.Window.StartMs / 1000, sample.Window.EndMs / 1000,
            frames.PixelWidth, frames.PixelHeight, frames.Frames.Count, frames.Timeline.TotalMs / 1000);
        ReserveInfo(notice: !autoplay);
        KoreanWordWrap.Show(_info, autoplay ? info : info + Environment.NewLine + UiText.Get("GifPreview_MotionReduced"));
        SetControlsEnabled(true);
        _player.Load(frames.Timeline);
        ApplyLayout();
        if (autoplay) _player.Play();
        UpdatePlayButton();
    }

    internal void TogglePlayback()
    {
        if (_sample is null) return;
        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            // Comparison is a still view; playing returns to the animated GIF alone.
            _compare.IsChecked = false;
            _player.Play();
        }

        UpdatePlayButton();
    }

    internal void Stop()
    {
        _player.Pause();
        CancellationTokenSource? cancellation = _originalCancellation;
        if (cancellation is not null)
        {
            // Release the pending frame immediately: a decoder can finish cancelling
            // after comparison is opened again, even for a single-image sample.
            _originalCancellation = null;
            _originalFrame = -1;
            cancellation.Cancel();
        }
        UpdatePlayButton();
    }

    private void Detach()
    {
        _player.Load(null);
        _originalCancellation?.Cancel();
        _originalCancellation = null;
        _originalFrame = -1;
        _sample = null;
        _gif.Source = null;
        _original.Source = null;
        _compare.IsChecked = false;
        _frameLabel.Text = string.Empty;
        SetControlsEnabled(false);
        UpdatePlayButton();
    }

    /// <summary>Holds the info line's place, with the notice line when playback will start paused.</summary>
    private void ReserveInfo(bool notice) => KoreanWordWrap.Show(_infoReserve,
        notice ? _widestInfo + Environment.NewLine + UiText.Get("GifPreview_MotionReduced") : _widestInfo);

    /// <summary>
    /// The widest info line a sample of a source this long can show, every number in 4s, the
    /// widest digit of Pretendard: the start and end with as many digits as the source's length
    /// (three when it is unknown), the size and frame count with three (at most 960 px and 400
    /// frames) and the loop with two (a 6 s sample at half speed loops in 12 s).
    /// </summary>
    private static string WidestInfo(double sourceDurationMs)
    {
        double seconds = Math.Round(sourceDurationMs / 1000, 1);
        int digits = double.IsFinite(seconds) && seconds > 0 ? ((long)seconds).ToString(CultureInfo.InvariantCulture).Length : 3;
        double time = double.Parse(new string('4', digits) + ".4", CultureInfo.InvariantCulture);
        return UiText.Format("GifPreview_Info", time, time, 444, 444, 444, 44.4);
    }

    private void ShowMessage(string text)
    {
        KoreanWordWrap.Show(_message, text);
        _message.Visibility = Visibility.Visible;
        _pair.Visibility = Visibility.Hidden;
    }

    private void ShowFrame(int index)
    {
        if (_sample is null)
        {
            return;
        }

        IReadOnlyList<BitmapSource> frames = _sample.Frames.Frames;
        _gif.Source = frames[Math.Clamp(index, 0, frames.Count - 1)];
        _frameLabel.Text = UiText.Format("GifPreview_Frame", index + 1, frames.Count);
        _syncingSlider = true;
        _position.Value = index;
        _syncingSlider = false;
        if (IsComparing) RequestOriginal(index);
    }

    private async void RequestOriginal(int index)
    {
        GifPreviewSample? sample = _sample;
        if (sample is null || index == _originalFrame) return;
        _originalCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _originalCancellation = cancellation;
        _originalFrame = index;
        _original.Source = null;
        _originalCaption.Text = UiText.Get("GifPreview_OriginalLoading");
        try
        {
            BitmapSource original = await _renderOriginal(sample, index, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_originalCancellation, cancellation)
                || !ReferenceEquals(sample, _sample) || _originalFrame != index) return;
            _original.Source = original;
            _originalCaption.Text = UiText.Get("GifPreview_Original");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_originalCancellation, cancellation)) _originalFrame = -1;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_originalCancellation, cancellation)
                && ReferenceEquals(sample, _sample) && _originalFrame == index)
            {
                _originalFrame = -1;
                _originalCaption.Text = UiText.Format("GifPreview_Failed", error.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_originalCancellation, cancellation)) _originalCancellation = null;
        }
    }

    private void ApplyLayout()
    {
        bool compare = IsComparing && _sample is not null;
        bool actual = _actualSize.IsChecked == true && _sample is not null;
        _originalColumn.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        _pair.ColumnDefinitions[0].Width = compare ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _gifCaption.Visibility = _originalCaption.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        // 1:1 means one GIF pixel per physical screen pixel, at any display scale; the
        // original is drawn at the same size so both sides show the same region.
        RenderOptions.SetBitmapScalingMode(_gif, actual ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (ScrollViewer view in new[] { _gifView, _originalView })
        {
            view.HorizontalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            view.VerticalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            // The 1:1 view can exceed the viewport; arrow keys scroll it once it has focus.
            view.Focusable = actual;
            if (!actual)
            {
                view.ScrollToHome();
            }
        }

        foreach (Image image in new[] { _gif, _original })
        {
            image.Width = actual ? _sample!.Frames.PixelWidth / scale : double.NaN;
            image.Height = actual ? _sample!.Frames.PixelHeight / scale : double.NaN;
            image.HorizontalAlignment = actual ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            image.VerticalAlignment = actual ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        }

        if (compare) RequestOriginal(_player.Index);
    }

    private void SyncScroll(ScrollViewer source, ScrollViewer target)
    {
        if (_syncingScroll || !IsComparing) return;
        _syncingScroll = true;
        try
        {
            target.ScrollToHorizontalOffset(source.HorizontalOffset);
            target.ScrollToVerticalOffset(source.VerticalOffset);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void UpdatePlayButton()
    {
        bool playing = _player.IsPlaying;
        string caption = UiText.Get(playing ? "GifPreview_Pause" : "GifPreview_Play");
        _playText.Text = caption;
        _playIcon.SetResourceReference(Path.DataProperty, playing ? "Icon.Pause" : "Icon.Play");
        AutomationProperties.SetName(_play, caption);
        _play.ToolTip = caption;
    }

    private void SetControlsEnabled(bool enabled)
    {
        bool animated = enabled && (_sample?.Frames.Frames.Count ?? 0) > 1;
        _play.IsEnabled = animated;
        _position.IsEnabled = animated;
        _compare.IsEnabled = enabled;
        _actualSize.IsEnabled = enabled;
    }

    private void ForwardWheelWhenFitting(object sender, MouseWheelEventArgs e)
    {
        // Outside the 1:1 view nothing scrolls here; let the wheel scroll the dialog.
        if (_actualSize.IsChecked == true || e.Handled) return;
        e.Handled = true;
        if (Parent is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
        }
    }

    private FrameworkElement Column(TextBlock caption, ScrollViewer view, Image image, int column)
    {
        view.Content = image;
        view.PreviewMouseWheel += ForwardWheelWhenFitting;
        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 0 ? 4 : 0, 0) };
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);
        panel.Children.Add(view);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private static void Configure(ToggleButton toggle, string captionKey, string helpKey)
    {
        toggle.Content = UiText.Get(captionKey);
        string help = UiText.Get(helpKey);
        toggle.ToolTip = help;
        AutomationProperties.SetName(toggle, UiText.Get(captionKey));
        AutomationProperties.SetHelpText(toggle, help);
    }

    private static void AddAt(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
