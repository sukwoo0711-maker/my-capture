using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal sealed partial class VideoEditorWindow
{
    private Button? _saveCurrentFrameButton;
    private TextBlock? _saveCurrentFrameText;
    private readonly Button _showSavedFrameButton = new() { Name = "VideoShowSavedFrame", Visibility = Visibility.Collapsed };
    private VideoFrameCaptureService? _frameCaptureService;
    private bool _savingCurrentFrame;
    private CancellationTokenSource? _frameSaveCts;
    private Guid? _savedFrameId;
    private bool _frameNavigationPending;
    private string? _resumedFrameSaveStatus;
    private long _frameSeekAvailabilityVersion;

    internal VideoFrameCaptureService? FrameCaptureService
    {
        get => _frameCaptureService;
        set { _frameCaptureService = value; UpdateCurrentFrameSaveState(); }
    }
    internal Guid FrameSourceVideoId { get; set; }
    internal long FrameSourceRevision { get; set; }
    internal Action<Guid>? ShowFrameInLibrary { get; set; }

    internal async Task RequestCloseForNavigation(Action afterClosed)
    {
        if (_frameNavigationPending || _operationRunning || _savingCurrentFrame) return;
        _frameNavigationPending = true;
        try
        {
            // Reuse the existing protected close flow: it awaits Save/Discard and returns
            // false on Cancel or failure, so no callback survives to an unrelated close.
            if (await PrepareForAppExitAsync()) afterClosed();
        }
        finally { _frameNavigationPending = false; }
    }

    private bool CanSaveCurrentFrame => _mediaReady && !_closing && !_operationRunning && !_extractingFrame
        && !_savingCurrentFrame && !_previewSeeks.IsSeeking && !_cutSeekInFlight
        && FrameCaptureService?.CanCapture(FrameSourceVideoId, FrameSourceRevision) == true;

    private async Task RefreshFrameAvailabilityAfterSeekAsync()
    {
        long version = ++_frameSeekAvailabilityVersion;
        try
        {
            // PositionRequested runs before the coordinator enqueues its request. Yield one
            // dispatcher turn, then include queued sampling time as well as active decoding.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (version != _frameSeekAvailabilityVersion || _closing) return;
            await _previewSeeks.WaitForIdleAsync();
            if (version == _frameSeekAvailabilityVersion && !_closing) UpdateCurrentFrameSaveState();
        }
        catch (OperationCanceledException) { }
    }

    private void InitializeCurrentFrameSave(Grid transport)
    {
        _saveCurrentFrameText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new SymbolIcon { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
        icon.SetResourceReference(SymbolIcon.DataProperty, "Icon.Image");
        content.Children.Add(icon);
        content.Children.Add(_saveCurrentFrameText);
        _saveCurrentFrameButton = new Button
        {
            Name = "VideoSaveCurrentFrame", Content = content, Margin = new Thickness(8, 0, 8, 0),
            ToolTip = UiText.Get("VideoFrame_Save") + " (Shift+S)",
        };
        CommandButtonPresentation.Apply(_saveCurrentFrameButton);
        AutomationProperties.SetName(_saveCurrentFrameButton, UiText.Get("VideoFrame_Save"));
        ShortcutPresentation.Attach(_saveCurrentFrameButton, ShortcutScope.Video, "SaveFrame");
        _saveCurrentFrameButton.Click += (_, _) => _shortcutActions["SaveFrame"]();
        Grid.SetColumn(_saveCurrentFrameButton, 2);
        transport.Children.Add(_saveCurrentFrameButton);
        _editControls.Add(_saveCurrentFrameButton);
        // Opening Details narrows this row independently of the window. Keep the complete
        // timecode visible by using the existing frame icon when its caption will not fit.
        void FitTransport()
        {
            if (transport.ActualWidth <= 0 || _saveCurrentFrameButton.DesiredSize.Width <= 0) return;
            double faceWidth = icon.Width + 6 + TextWidth(_saveCurrentFrameText);
            if (_saveCurrentFrameButton.Content is Grid face) faceWidth = Math.Max(faceWidth, face.MinWidth);
            // Measure the named face with its standard padding so the choice does not flip
            // back as soon as the icon-only padding frees a few pixels.
            Thickness padding = _saveCurrentFrameButton.TryFindResource("CommandButton.Padding.Standard") is Thickness standard
                ? standard : _saveCurrentFrameButton.Padding;
            double saveWidth = Math.Max(_saveCurrentFrameButton.MinWidth, faceWidth
                + padding.Left + padding.Right
                + _saveCurrentFrameButton.BorderThickness.Left + _saveCurrentFrameButton.BorderThickness.Right)
                + _saveCurrentFrameButton.Margin.Left + _saveCurrentFrameButton.Margin.Right;
            double required = _playButton!.DesiredSize.Width + _positionChip.Margin.Left
                + TextWidth(_positionLabel) + saveWidth + _detailToggle.DesiredSize.Width;
            bool compact = required > transport.ActualWidth + 0.5;
            // The shared helper also switches to the icon-only padding used by every command face.
            CommandButtonPresentation.CompactLabel(_saveCurrentFrameButton, compact);
            icon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        }
        transport.SizeChanged += (_, _) => FitTransport();
        _saveCurrentFrameButton.SizeChanged += (_, _) => FitTransport();
        _positionLabel.SizeChanged += (_, _) => FitTransport();
        SizeChanged += (_, _) => UpdateCurrentFrameSaveState();
        UpdateCurrentFrameSaveState();
    }

    private static double TextWidth(TextBlock text) => new FormattedText(text.Text,
        System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
        Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private UIElement BuildFrameSaveStatus()
    {
        var status = new DockPanel();
        _showSavedFrameButton.Content = UiText.Get("VideoFrame_ShowInLibrary");
        _showSavedFrameButton.SetResourceReference(StyleProperty, "Button.Ghost");
        _showSavedFrameButton.Click += (_, _) => { if (_savedFrameId is { } id) ShowFrameInLibrary?.Invoke(id); };
        DockPanel.SetDock(_showSavedFrameButton, Dock.Right);
        status.Children.Add(_showSavedFrameButton);
        status.Children.Add(_statusLabel);
        return status;
    }

    private void UpdateCurrentFrameSaveState()
    {
        if (_saveCurrentFrameButton is null || _saveCurrentFrameText is null) return;
        _saveCurrentFrameButton.IsEnabled = CanSaveCurrentFrame;
        _saveCurrentFrameText.Text = UiText.Get(ActualWidth is > 0 and < 1100 ? "VideoFrame_SaveShort" : "VideoFrame_Save");
    }

    internal async Task<CaptureRecord?> SaveCurrentFrameAsync()
    {
        if (!CanSaveCurrentFrame) return null;
        bool capturePlayingPosition = _isPlaying;
        bool atCutEnd = _settlingCutPlaybackEnd && !capturePlayingPosition;
        bool wasPlaying = _isPlaying || _playRequested;
        PausePlayback();
        // The timeline keeps the exclusive cut end while the preview shows its last kept
        // frame. Retain that state across saves, including a second save without a seek.
        _settlingCutPlaybackEnd = atCutEnd;
        double requestedMs = atCutEnd
            ? PreviewCutPlayback.LastKeptFrame(_cutTimeline, _recording.Fps)
            : capturePlayingPosition ? Math.Clamp(_media.Position.TotalMilliseconds, 0, _durationMs) : CurrentMs();
        if (capturePlayingPosition)
        {
            // The transport timer may not have published the paused clock yet. Pin overlays
            // and the later resume to the same position used by this save.
            _timeline.SetPlayhead(requestedMs, ensureVisible: false);
            _layerTimeline.SetPlayhead(requestedMs);
            _overlayPreview.SetSourceTime(requestedMs);
            _layerCanvas.SetSourceTime(requestedMs);
            UpdatePositionLabel(requestedMs);
        }
        VideoEditDocument snapshot = BuildCurrentDocument();
        _savingCurrentFrame = true;
        _resumedFrameSaveStatus = null;
        SetEditControlsEnabled(false);
        _statusLabel.Text = UiText.Get("VideoFrame_Saving");
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
        _showSavedFrameButton.Visibility = Visibility.Collapsed;
        using var cancellation = new CancellationTokenSource();
        _frameSaveCts = cancellation;
        long pausedVersion = _playRequestVersion;
        try
        {
            CaptureRecord saved = await FrameCaptureService!.SaveAsync(FrameSourceVideoId, FrameSourceRevision,
                _recording.OutputPath, requestedMs, snapshot, cancellation.Token);
            if (!_closing)
            {
                _savedFrameId = saved.Id;
                _statusLabel.Text = UiText.Format("VideoFrame_Saved", saved.Title);
                _resumedFrameSaveStatus = _statusLabel.Text;
                _showSavedFrameButton.Visibility = ShowFrameInLibrary is null ? Visibility.Collapsed : Visibility.Visible;
            }
            return saved;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save the current editor frame");
            if (!_closing)
            {
                _statusLabel.Text = UiText.Get("VideoFrame_SaveFailed");
                _resumedFrameSaveStatus = _statusLabel.Text;
                _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            }
            return null;
        }
        finally
        {
            if (ReferenceEquals(_frameSaveCts, cancellation)) _frameSaveCts = null;
            _savingCurrentFrame = false;
            if (!_closing)
            {
                SetEditControlsEnabled(_mediaReady && !_operationRunning);
                if (wasPlaying && pausedVersion == _playRequestVersion) TogglePlay();
            }
        }
    }
}
