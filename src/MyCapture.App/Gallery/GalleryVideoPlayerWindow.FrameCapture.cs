using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryVideoPlayerWindow
{
    private readonly Button _saveFrameButton = new() { Name = "PlayerSaveFrame", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _showFrameButton = new() { Name = "PlayerShowFrame", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _saveFrameText = new();
    private VideoFrameCaptureService? _frameCaptureService;
    private Action<Guid>? _showFrameInLibrary;
    private long _frameSourceRevision;
    private long _frameOpenVersion;
    private bool _frameCaptureRunning;
    private bool _frameSeekPending;
    private bool _frameBuffering;
    private bool _frameClosed;
    private Guid? _lastFrameId;
    private CancellationTokenSource? _frameCaptureCts;
    private readonly SemaphoreSlim _frameSeekSerial = new(1, 1);
    private IVideoPreviewEngine? _frameSeekEngine;
    private CancellationTokenSource? _frameSeekCts;
    private Task _frameSeekTask = Task.CompletedTask;
    private long _frameSeekVersion;
    private bool _frameSeekFailed;
    private bool _resumeAfterFrameSeek;
    private double? _framePresentedPositionMs;

    internal Func<MediaElement, IVideoPreviewEngine>? FrameSeekEngineFactoryForTest { get; set; }
    internal Task FrameSeekTaskForTest => _frameSeekTask;

    private void ResetFrameSeeking()
    {
        _frameSeekVersion++;
        _frameSeekCts?.Cancel();
        _frameSeekEngine?.Dispose();
        // The player may open variable-rate media. Do not invent a frame grid for a slider seek.
        _frameSeekEngine = FrameSeekEngineFactoryForTest?.Invoke(_video) ?? new MediaElementPreviewEngine(_video, fps: 0);
        _frameSeekPending = false;
        _frameSeekFailed = false;
        _resumeAfterFrameSeek = false;
        _framePresentedPositionMs = null;
    }

    private void CloseFrameSeeking()
    {
        _frameSeekVersion++;
        _frameSeekCts?.Cancel();
        _frameSeekEngine?.Dispose();
    }

    private void RequestFrameSeek(double targetMs)
    {
        bool resume = _frameSeekPending ? _resumeAfterFrameSeek : _playing;
        _frameSeekCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _frameSeekCts = cancellation;
        long version = ++_frameSeekVersion;
        _frameSeekPending = true;
        _frameSeekFailed = false;
        _framePresentedPositionMs = null;
        SetPlayback(false, seekTransition: true);
        _resumeAfterFrameSeek = resume;
        SetStatusPlaying(resume);
        UpdateFrameCaptureState();
        _frameSeekTask = SeekFrameAsync(targetMs, version, _frameSeekEngine!, cancellation);
    }

    private async Task SeekFrameAsync(double targetMs, long version, IVideoPreviewEngine engine, CancellationTokenSource cancellation)
    {
        bool entered = false;
        try
        {
            await _frameSeekSerial.WaitAsync(cancellation.Token);
            entered = true;
            var request = new PreviewSeekRequest(version, targetMs, 0, PreviewSeekMode.Exact);
            PresentedPreviewFrame presented = await engine.SeekAsync(request, cancellation.Token);
            // An adapter may report where a failed seek actually stopped. That observation
            // must not turn a miss into a successful request just because its clock is stable.
            if (!MediaElementPreviewEngine.HasLanded(presented.PresentedPositionMs, targetMs))
                throw new InvalidOperationException("The preview did not reach the requested frame.");
            if (engine is MediaElementPreviewEngine native)
            {
                await native.WhenPresentedAsync(cancellation.Token);
                // Composition must follow the landed seek before capture becomes available.
                if (!_video.HasVideo || !MediaElementPreviewEngine.HasLanded(_video.Position.TotalMilliseconds,
                        native.PresentationPositionMs(targetMs)))
                    throw new InvalidOperationException("The player did not settle on the requested frame.");
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_frameClosed || version != _frameSeekVersion) return;
            _framePresentedPositionMs = targetMs;
            _frameSeekPending = false;
            UpdateFrameCaptureState();
            UpdatePosition();
            SetPlayback(_resumeAfterFrameSeek);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_frameClosed && version == _frameSeekVersion)
            {
                _frameSeekPending = false;
                _frameSeekFailed = true;
                _resumeAfterFrameSeek = false;
                SetStatusPlaying(false);
                _status.Text = UiText.Get("VideoMulticut_PreviewSeekFailed");
                UpdateFrameCaptureState();
                _log?.LogWarning(error, "Could not settle the library video seek");
            }
        }
        finally
        {
            if (entered) _frameSeekSerial.Release();
            if (ReferenceEquals(_frameSeekCts, cancellation)) _frameSeekCts = null;
            cancellation.Dispose();
        }
    }

    internal void ConfigureFrameCapture(VideoFrameCaptureService service, long contentRevision, Action<Guid>? showInLibrary = null)
    {
        _frameCaptureService = service;
        _frameSourceRevision = contentRevision;
        _showFrameInLibrary = showInLibrary;
        UpdateFrameCaptureState();
    }

    void IGalleryVideoPlayer.ConfigureFrameCapture(VideoFrameCaptureService service, long contentRevision, Action<Guid>? showInLibrary) =>
        ConfigureFrameCapture(service, contentRevision, showInLibrary);

    private void InitializeFrameCapture(Panel controls)
    {
        _saveFrameButton.SetResourceReference(StyleProperty, "Button.Ghost");
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new SymbolIcon { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
        icon.SetResourceReference(SymbolIcon.DataProperty, "Icon.Image");
        label.Children.Add(icon);
        label.Children.Add(_saveFrameText);
        _saveFrameButton.Content = label;
        _saveFrameButton.ToolTip = UiText.Get("VideoFrame_Save") + " (Shift+S)";
        AutomationProperties.SetName(_saveFrameButton, UiText.Get("VideoFrame_Save"));
        ShortcutPresentation.Attach(_saveFrameButton, ShortcutScope.Video, "SaveFrame");
        _saveFrameButton.Click += async (_, _) => await SaveCurrentFrameAsync();
        _showFrameButton.Content = UiText.Get("VideoFrame_ShowInLibrary");
        _showFrameButton.SetResourceReference(StyleProperty, "Button.Ghost");
        _showFrameButton.Click += (_, _) => { if (_lastFrameId is { } id) _showFrameInLibrary?.Invoke(id); };
        controls.Children.Add(_saveFrameButton);
        controls.Children.Add(_showFrameButton);
        SizeChanged += (_, _) => UpdateFrameCaptureState();
        Activated += (_, _) => UpdateFrameCaptureState();
        controls.MouseEnter += (_, _) => UpdateFrameCaptureState();
        _video.BufferingStarted += (_, _) => { _frameBuffering = true; UpdateFrameCaptureState(); };
        _video.BufferingEnded += (_, _) => { _frameBuffering = false; UpdateFrameCaptureState(); };
        UpdateFrameCaptureState();
    }

    private bool CanSaveCurrentFrame => _mediaReady && !_frameClosed && !_frameCaptureRunning
        && !_frameSeekPending && !_frameSeekFailed && !_seek.IsMouseCaptureWithin && !_frameBuffering
        && _frameCaptureService?.CanCapture(_videoId, _frameSourceRevision) == true;

    private void UpdateFrameCaptureState()
    {
        _saveFrameButton.IsEnabled = CanSaveCurrentFrame;
        _saveFrameText.Text = UiText.Get(ActualWidth is > 0 and < 680 ? "VideoFrame_SaveShort" : "VideoFrame_Save");
    }

    internal bool HandleFrameShortcut(Key key, ModifierKeys modifiers, IInputElement? focusedElement, bool isRepeat = false)
    {
        if (key != Key.S || modifiers != ModifierKeys.Shift || isRepeat
            || focusedElement is TextBoxBase or PasswordBox || !CanSaveCurrentFrame) return false;
        _ = SaveCurrentFrameAsync();
        return true;
    }

    internal async Task<CaptureRecord?> SaveCurrentFrameAsync()
    {
        if (!CanSaveCurrentFrame || _video.Source is not { IsFile: true } uri) return null;
        VideoFrameCaptureService service = _frameCaptureService!;
        Guid id = _videoId;
        long version = _frameOpenVersion, revision = _frameSourceRevision;
        string path = uri.LocalPath;
        double time = !_playing && _framePresentedPositionMs is { } presented ? presented : _video.Position.TotalMilliseconds;
        bool wasPlaying = _playing;
        SetPlayback(false);
        _frameCaptureRunning = true;
        _playButton.IsEnabled = false;
        _seek.IsEnabled = false;
        UpdateFrameCaptureState();
        _status.Text = UiText.Get("VideoFrame_Saving");
        _showFrameButton.Visibility = Visibility.Collapsed;
        using var cancellation = new CancellationTokenSource();
        _frameCaptureCts = cancellation;
        try
        {
            CaptureRecord saved = await service.SaveAsync(id, revision, path, time, cancellationToken: cancellation.Token);
            if (!_frameClosed && version == _frameOpenVersion)
            {
                _lastFrameId = saved.Id;
                _status.Text = UiText.Format("VideoFrame_Saved", saved.Title);
                _showFrameButton.Visibility = _showFrameInLibrary is null ? Visibility.Collapsed : Visibility.Visible;
            }
            return saved;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not save a library video frame");
            if (!_frameClosed && version == _frameOpenVersion) _status.Text = UiText.Get("VideoFrame_SaveFailed");
            return null;
        }
        finally
        {
            if (ReferenceEquals(_frameCaptureCts, cancellation)) _frameCaptureCts = null;
            _frameCaptureRunning = false;
            if (!_frameClosed)
            {
                _playButton.IsEnabled = true;
                _seek.IsEnabled = true;
                UpdateFrameCaptureState();
                if (version == _frameOpenVersion && wasPlaying) SetPlayback(true);
            }
        }
    }
}
