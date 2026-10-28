using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.App.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Plays a finished recording and lets the user trim it, step it frame by frame, and
/// pull any frame into the existing still-image annotation editor.
/// </summary>
/// <remarks>
/// <para>
/// Playback and seeking use WPF <see cref="MediaElement"/> in manual clock mode, which
/// decodes on demand rather than holding every frame in memory — the property the brief
/// needs for a weak PC. Trimming is non-destructive (a <see cref="TrimSelection"/>); the
/// source MP4 is only re-encoded when the user commits.
/// </para>
/// <para>
/// Arrow-key behaviour is the crux of the request and is delegated wholly to
/// <see cref="FrameStepCalculator"/>: in frame-step mode Left/Right move one frame
/// (Shift = 10); otherwise they move a normal-editor "coarse" step.
/// </para>
/// </remarks>
internal sealed partial class VideoEditorWindow : Window
{
    private readonly Dictionary<string, Action> _shortcutActions = [];
    internal IReadOnlyDictionary<string, Action> ShortcutActions => _shortcutActions;
    private readonly HangulShortcutRelay _hangulRelay;
    private readonly RecordingResult _recording;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VideoEditorWindow> _log;
    private VideoEditDocument _editDocument;
    private VideoEditDocument? _initialDocument;

    private readonly MediaElement _media;
    private readonly TimedTextPreviewView _overlayPreview;
    private readonly VideoLayerCanvas _layerCanvas;
    private readonly List<VideoEditDocument> _undo = [];
    private readonly List<VideoEditDocument> _redo = [];
    private VideoEditDocument? _interactionBefore;
    private VideoEditDocument? _trimInteractionBefore;
    private bool _syncingTrim;
    private TextBox _trimInInput = null!;
    private TextBox _trimOutInput = null!;
    private TextBlock _keptLengthLabel = null!;
    private TextBlock _removedLengthLabel = null!;
    private Button _resetTrimButton = null!;
    private System.Windows.Controls.Primitives.ToggleButton _detailToggle = null!;
    private TextBlock _identityDetails = null!;
    private Button _trimToolButton = null!;
    private Button _addTextToolButton = null!;
    private Button _frameEditToolButton = null!;
    private Button _exportToolButton = null!;
    private Button _moreToolsButton = null!;
    private Button _restoreSegmentButton = null!;
    private Button _splitToolButton = null!;
    private VideoCutTimeline _cutTimeline = VideoCutTimeline.Create(0, 1);
    private readonly PreviewCutPlayback _cutPlayback = new();
    private bool _cutSeekInFlight;
    private bool _settlingCutPlaybackEnd;
    private ScrollViewer? _layerTracksScroll;
    private Border _layerPanel = null!;
    private Border _detailsPanel = null!;
    private readonly Border _trimPreviewBadge;
    private readonly TextBlock _trimPreviewBadgeText;
    private readonly MediaElementPreviewEngine _previewEngine;
    private readonly PreviewSeekCoordinator _previewSeeks;
    private readonly Action _playMedia;
    private readonly TwoLineTimeline _timeline;
    private readonly VideoLayerTimeline _layerTimeline;
    internal Func<MouseButtonEventArgs, Point>? LayerTrackPointerPositionForTest { get; set; }
    private readonly TextBlock _positionLabel;
    private readonly TextBlock _positionFrameLabel = new();
    private Border _positionChip = null!;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _loadingLabel;
    private readonly Border _loadingOverlay;
    private readonly ListBox _overlayList;
    private Button? _undoButton;
    private Button? _redoButton;
    private Button _addTextButton = null!;
    private Button _editTextButton = null!;
    private Button _deleteTextButton = null!;
    private readonly MediaExportProgressRow _saveProgress = new();
    private readonly List<Control> _editControls = [];
    private Grid _controlRows = null!;
    private DispatcherTimer? _openTimeoutTimer;
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _operationCts;
    private Task<VideoSourceInfo>? _sourceInfo;

    private double _durationMs;
    private bool _mediaReady;
    private bool _keyboardNavigation = true;
    private bool _trimInputsInvalid;
    private bool _mediaOpened;
    private bool _mediaFailed;
    private bool _closing;
    private string _mediaFailure = string.Empty;
    private bool _committed;
    private bool _isPlaying;
    private bool _playRequested;
    private long _playRequestVersion;
    private bool _operationRunning;
    private bool _closeRequested;
    private bool _updatingOverlayList;
    private bool _extractingFrame;

    // Test hooks so a headless self-test can confirm the editor actually reaches the ready
    // state for a real clip (the field report: a ~2s video failed to load).
    internal bool IsMediaReadyForTest => _mediaReady;

    internal bool HasMediaFailedForTest => _mediaFailed;

    /// <summary>MediaOpened arrived. The open fallback can make the editor ready before it does.</summary>
    internal bool HasMediaOpenedForTest => _mediaOpened;

    /// <summary>The source probe finished and its range is applied to the preview.</summary>
    internal bool IsSourceRangeKnownForTest => _sourceInfo is { IsCompleted: true };

    /// <summary>How long the editor waits for MediaOpened before it opens with a fallback duration.</summary>
    internal TimeSpan OpenTimeoutForTest { get; set; } = TimeSpan.FromSeconds(5);

    internal string MediaFailureForTest => _mediaFailure;

    internal double DurationMsForTest => _durationMs;

    internal TwoLineTimeline TimelineForTest => _timeline;

    internal PreviewSeekCoordinator PreviewSeekCoordinatorForTest => _previewSeeks;

    internal MediaElementPreviewEngine PreviewEngineForTest => _previewEngine;

    internal MediaElement MediaForTest => _media;

    /// <summary>Visual-only test hook: includes presenter errors and the preview effect.</summary>
    internal BitmapSource? RenderCurrentFrameForTest() => TryRenderPreviewForTest();

    /// <summary>The exact decoded/composited pixels handed to the frame annotation editor.</summary>
    internal Task<BitmapSource?> DecodeFrameForEditAsync(double sourceTimeMs)
    {
        VerifyAccess();
        // Snapshot before leaving the dispatcher: later playhead/layer changes must not alter
        // the frame being edited or the time at which its PNG layer will be stored.
        VideoEditDocument document = _editDocument.Clone();
        return TryDecodeFrameForEditAsync(sourceTimeMs, document);
    }

    private async Task<BitmapSource?> TryDecodeFrameForEditAsync(double sourceTimeMs, VideoEditDocument document)
    {
        try
        {
            // Text remains a live, upright overlay in preview/export. Baking it here would
            // rotate one copy into the PNG and draw another copy over the resulting layer.
            return await StaThreadTask.RunAsync(() => VideoFrameRenderPipeline.RenderSingleFrame(
                _recording.OutputPath, sourceTimeMs, _recording.Width, _recording.Height,
                overlays: null, frameEditLayers: document.FrameEditLayers), "MyCapture frame edit decode");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rendering the current video frame failed");
            return null;
        }
    }

    internal int ControlRowCountForTest => _controlRows.RowDefinitions.Count;

    internal double ControlAreaWidthForTest => _controlRows.ActualWidth;

    internal double WidestControlRowWidthForTest => _controlRows.Children
        .OfType<FrameworkElement>()
        .Max(child => child.DesiredSize.Width);

    internal double WidestControlRowContentWidthForTest => _controlRows.Children
        .OfType<Panel>()
        .Max(panel => panel.Children
            .OfType<FrameworkElement>()
            .Sum(child => child.ActualWidth + child.Margin.Left + child.Margin.Right));

    internal VideoEditorWindow(
        RecordingResult recording,
        AppPaths paths,
        ILoggerFactory loggerFactory,
        VideoEditDocument? editDocument = null,
        IVideoPreviewEngine? previewEngine = null,
        Action? playMedia = null)
    {
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = loggerFactory.CreateLogger<VideoEditorWindow>();
        VideoLayerResourceBudget.Validate(editDocument?.FrameEditLayers);
        // Until the media opens, keep the document on its own measured source length so trim
        // and layer times are not clipped to a different recorded value; CompleteOpen then
        // normalizes once more against the container duration.
        double documentDurationMs = editDocument is { SourceDurationMs: > 0 and < double.PositiveInfinity }
            ? editDocument.SourceDurationMs
            : recording.DurationMs;
        _editDocument = (editDocument ?? VideoEditDocument.CreateFor(
                recording.Width,
                recording.Height,
                recording.DurationMs))
            .NormalizeFor(recording.Width, recording.Height, documentDurationMs);

        WorkspaceTheme.Attach(this, WorkspaceRole.VideoEditor);
        StandardWindowTheme.Apply(this);
        MyCapture.App.Capture.CaptureEditorRegistry.Track(this, static editor => new MyCapture.App.Capture.CaptureGuardState
        {
            EditorOpen = true,
            EditorBusy = editor.IsCaptureBusy,
        });

        // The title names the task first: this window edits a recorded video, not an image.
        Title = UiText.Get("VideoEditorUx_WindowTitle");
        Background = TryBrush("Surface.Base", Color.FromRgb(0x0B, 0x0F, 0x17));
        Foreground = TryBrush("Text.Primary", Colors.White);
        FontFamily = TryFont("Font.Ui");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        MinWidth = 760;
        MinHeight = 555;

        Width = Math.Max(920, recording.Width + 120);
        Height = Math.Max(680, recording.Height + 260);
        FitInitialSizeToMonitor(MonitorEnumerator.GetFromCursor());
        SourceInitialized += OnSourceInitialized;

        AutomationProperties.SetName(this, UiText.Get("VideoEditorUx_WindowName"));

        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
        };
        _media.MediaOpened += OnMediaOpened;
        _media.MediaFailed += OnMediaFailed;
        _media.MediaEnded += OnMediaEnded;
        _overlayPreview = new TimedTextPreviewView();
        _overlayPreview.SetCanvas(recording.Width, recording.Height);
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _overlayPreview.SetFrameLayers(_editDocument.FrameEditLayers);
        _layerCanvas = new VideoLayerCanvas();
        _layerCanvas.SetDocument(_editDocument);
        _layerCanvas.SelectionChanged += (_, _) =>
        {
            if (_layerCanvas.SelectedId is { } id) { RefreshOverlayList(id); }
            else if (_overlayList is not null) { _overlayList.SelectedItem = null; }
        };
        _layerCanvas.InteractionStarted += (_, _) => BeginLayerInteraction();
        _layerCanvas.BoundsChanged += (_, _) => { _overlayPreview.InvalidateVisual(); _draftSession?.Changed(); };
        _layerCanvas.InteractionCompleted += (_, _) => CompleteLayerInteraction();
        _layerCanvas.LayerActivated += (_, _) => EditSelectedOverlay();
        _previewEngine = new MediaElementPreviewEngine(_media, recording.Fps);
        _previewSeeks = new PreviewSeekCoordinator(previewEngine ?? _previewEngine, recording.Fps);
        _playMedia = playMedia ?? _media.Play;
        _previewSeeks.PositionRequested += OnPreviewPositionRequested;
        _previewSeeks.PreviewPresented += OnPreviewPresented;
        _previewSeeks.SeekFailed += OnPreviewSeekFailed;

        _timeline = new TwoLineTimeline();
        _timeline.SetDetailVisible(false);
        _layerTimeline = new VideoLayerTimeline();
        _timeline.ViewportUpdated += (_, _) => SyncLayerViewport();
        _timeline.HoverChanged += (_, time) => _layerTimeline.SetHover(time);
        _layerTimeline.HoverChanged += (_, time) => _timeline.SetHover(time);
        _layerTimeline.SnapChanged += (_, snap) => _timeline.SetSnapGuide(snap);
        _timeline.TrimChanged += (_, _) => _layerTimeline.SetKeptRange(_timeline.InMs, _timeline.OutMs);
        _timeline.SegmentSelected += (_, _) =>
        {
            if (_timeline.SelectedSegment is not null)
            {
                if (_overlayList is not null) _overlayList.SelectedItem = null;
                _layerTimeline.SelectLayer(null);
                _layerCanvas.Select(null);
            }
            UpdateCutActionStates();
        };
        _layerTimeline.LayerSelected += (_, _) =>
        {
            if (_mediaReady && !_operationRunning)
            {
                RefreshOverlayList(_layerTimeline.SelectedLayerId);
            }
        };
        _layerTimeline.ToolTip = UiText.Get("VideoEditorUx_LayerTrackHelp");
        AutomationProperties.SetHelpText(_layerTimeline, UiText.Get("VideoEditorUx_LayerTrackHelp"));
        _layerTimeline.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2 && _mediaReady && !_operationRunning
                && _layerTimeline.SelectedBarBounds.Contains(
                    LayerTrackPointerPositionForTest?.Invoke(args) ?? args.GetPosition(_layerTimeline)))
            {
                args.Handled = true;
                _shortcutActions["EditLayer"]();
            }
        };
        _layerTimeline.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && _mediaReady && !_operationRunning && SelectedLayerId() is not null)
            {
                args.Handled = true;
                _shortcutActions["EditLayer"]();
            }
        };
        _timeline.ToolTip = UiText.Get("VideoEditorUx_TrimTrackHelp");
        AutomationProperties.SetHelpText(_timeline, UiText.Get("VideoEditorUx_TrimTrackHelp"));
        _layerTimeline.LayerTimingInteractionStarted += (_, _) => BeginLayerInteraction();
        _layerTimeline.LayerTimingChanged += OnLayerTimingChanged;
        _layerTimeline.LayerTimingInteractionCompleted += OnLayerTimingInteractionCompleted;
        _timeline.PlayheadChanged += OnTimelinePlayhead;
        _timeline.PlayheadInteractionCompleted += OnTimelinePlayheadInteractionCompleted;
        _timeline.TrimChanged += (_, _) => OnTrimChanged();
        _timeline.TrimInteractionStarted += (_, _) => BeginTrimInteraction();
        _timeline.TrimInteractionCompleted += (_, _) => CompleteTrimInteraction();
        // While a trim handle is dragged the preview shows that handle's frame; the badge
        // says which end it is, like the handle chip on the timeline.
        _trimPreviewBadgeText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryBrush("Text.Primary", Colors.White),
        };
        _trimPreviewBadge = new Border
        {
            Name = "VideoTrimPreviewBadge",
            Background = TryBrush("Surface.Raised", Color.FromRgb(0x10, 0x17, 0x22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 4),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _trimPreviewBadgeText,
        };
        _positionLabel = BuildMono(UiText.Format("VideoEditorUx_PositionTime", VideoTimecode.Format(0), VideoTimecode.Format(0)));
        _statusLabel = new TextBlock
        {
            Text = UiText.Get("Text_831103D5C64B"),
            ToolTip = UiText.Get("Text_59CC7803B118"),
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _statusLabel.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _statusLabel });
        AutomationProperties.SetLiveSetting(_statusLabel, AutomationLiveSetting.Polite);
        _loadingLabel = new TextBlock
        {
            Text = UiText.Get("Text_831103D5C64B"),
            Foreground = TryBrush("Text.Primary", Colors.White),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetLiveSetting(_loadingLabel, AutomationLiveSetting.Polite);
        _loadingOverlay = new Border
        {
            Background = TryBrush("Surface.Scrim", Color.FromArgb(0xC8, 0x08, 0x0C, 0x12)),
            CornerRadius = new CornerRadius(12),
            Child = _loadingLabel,
        };

        _overlayList = new ListBox
        {
            MinHeight = 38,
            MaxHeight = 120,
            MinWidth = 0,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // Rows take the panel's width and trim a long name; a sideways scroll bar under a
        // single layer took a row of its own in small windows.
        ScrollViewer.SetHorizontalScrollBarVisibility(_overlayList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_overlayList, ScrollBarVisibility.Auto);
        _overlayList.SelectionChanged += OnOverlaySelectionChanged;
        _overlayList.MouseDoubleClick += (_, _) => EditSelectedOverlay();
        AutomationProperties.SetName(_overlayList, UiText.Get("Text_8D1EFD1B2B84"));

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _playbackTimer.Tick += OnPlaybackTick;

        BuildShortcutActions();
        Content = BuildLayout();

        // Controls start DISABLED until the media is ready; a click during load must do nothing.
        SetEditControlsEnabled(false);

        KeyDown += OnKeyDown;
        PreviewKeyDown += OnPreviewImeKeyDown;
        PreviewKeyDown += OnPreviewSpaceKeyDown;
        PreviewMouseDown += OnPreviewPointerDown;
        _hangulRelay = new HangulShortcutRelay(this,
            () => _mediaReady && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase,
            HandleShortcut);
        Closing += OnClosingInternal;
        InitializeDraftRecovery();
        Loaded += OnLoadedInternal;
        Closed += OnClosedInternal;
        IsVisibleChanged += (_, _) => { if (!IsVisible) { PausePlayback(); } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { PausePlayback(); } };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // The close box, Alt+F4 and the system menu arrive as SC_CLOSE before WM_CLOSE; a
        // programmatic Close() does not. Only the user's own close asks about unsaved edits.
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(OnWindowMessage);

        // CenterScreen uses the owner's monitor, or the pointer's monitor without an
        // owner. The primary work area can be much larger (e.g. an ultrawide beside a
        // portrait display), and an already oversized HWND may straddle both monitors.
        // Respect callers that explicitly position/size an editor instead.
        if (WindowStartupLocation != WindowStartupLocation.CenterScreen) return;
        IntPtr ownerHandle = Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
        MonitorInfo monitor = ownerHandle == IntPtr.Zero
            ? MonitorEnumerator.GetFromCursor()
            : MonitorEnumerator.GetFromWindow(ownerHandle);
        FitInitialSizeToMonitor(monitor);
        // WPF has already computed the CenterScreen origin from the previous size.
        // Recenter the resized HWND in physical pixels; preserve its activation/z-order.
        double scale = Math.Max(1, monitor.Dpi) / 96.0;
        int width = (int)Math.Round(Width * scale);
        int height = (int)Math.Round(Height * scale);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (!SetWindowPos(handle, IntPtr.Zero,
            (int)Math.Round(monitor.WorkArea.Left + (monitor.WorkArea.Width - width) / 2),
            (int)Math.Round(monitor.WorkArea.Top + (monitor.WorkArea.Height - height) / 2),
            width, height, 0x0004 | 0x0010)) // SWP_NOZORDER | SWP_NOACTIVATE
            _log.LogWarning("Could not fit the video editor to its target monitor (Win32 {Error})", Marshal.GetLastWin32Error());
    }

    private HwndSource? _hwndSource;
    private bool _userRequestedClose;
    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScClose)
        {
            _userRequestedClose = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>Marks the next Close() as the user's own, as the close box would.</summary>
    internal void MarkUserCloseRequestedForTest() => _userRequestedClose = true;

    /// <summary>Replaces the unsaved-edits dialog in tests; null shows the shared close dialog.</summary>
    internal Func<EditorCloseChoice>? UnsavedEditsPromptForTest { get; set; }

    /// <summary>
    /// The same three-way close confirmation as the image editor and the settings window:
    /// the filled action first, the alternative next, Cancel last; Enter saves, Esc cancels.
    /// </summary>
    private EditorCloseChoice AskUnsavedEdits()
    {
        if (UnsavedEditsPromptForTest is not null)
        {
            return UnsavedEditsPromptForTest();
        }

        var dialog = new EditorCloseDialog(
            UiText.Get("VideoEditorUx_UnsavedTitle"),
            UiText.Get("VideoEditorUx_UnsavedPrompt"),
            UiText.Get("VideoEditorUx_SaveAndClose"),
            UiText.Get("EditorFlow_DontSave"),
            UiText.Get("Dialog.Cancel"));
        if (IsVisible) dialog.Owner = this;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _ = dialog.ShowDialog();
        return dialog.Choice;
    }

    internal bool HasUnsavedEditsForTest => HasUnsavedEdits();

    private bool HasUnsavedEdits() =>
        !_committed && (_recoveredDraft || (_mediaReady && _initialDocument is not null
        && !DocumentsEquivalent(_initialDocument, BuildCurrentDocument())));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void FitInitialSizeToMonitor(MonitorInfo monitor)
    {
        double scale = Math.Max(1, monitor.Dpi) / 96.0;
        double availableWidth = Math.Max(1, monitor.WorkArea.Width / scale);
        double availableHeight = Math.Max(1, monitor.WorkArea.Height / scale);
        MinWidth = Math.Min(760, availableWidth);
        MinHeight = Math.Min(555, availableHeight);
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, availableWidth - 80));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, availableHeight - 60));
    }

    private void OnLoadedInternal(object? sender, RoutedEventArgs e)
    {
        // Set the source only after the window (and the MediaElement's visual tree) is loaded.
        // A MediaElement asked to open before it is connected to a rendered tree can silently
        // never raise MediaOpened for a short clip — the reported "2s video won't load" case.
        // The exact container duration is read in parallel: MediaElement's NaturalDuration
        // comes from Windows Media Player and is truncated to whole seconds.
        _sourceInfo = ReadSourceAsync();
        try
        {
            _media.Source = new Uri(_recording.OutputPath, UriKind.Absolute);
            _media.Play();   // Manual mode: Play kicks decoding so MediaOpened fires reliably…
            _media.Pause();  // …then immediately pause so we sit on frame 0.
        }
        catch (Exception ex)
        {
            OnMediaFailed(this, null!);
            _log.LogError(ex, "Could not set the media source");
        }

        StartLoadProgress();
    }

    /// <summary>
    /// Probes the clip and applies its range as soon as it is known, without waiting for
    /// MediaOpened. When MediaOpened is late the open fallback makes the editor ready first, and
    /// when it never arrives a full-range clip would otherwise keep the studio-range expansion.
    /// </summary>
    private async Task<VideoSourceInfo> ReadSourceAsync()
    {
        VideoSourceInfo source = await VideoSourceProbe.ReadAsync(_recording.OutputPath, _log);
        if (!_closing)
        {
            StudioRangeVideoEffect.Apply(_media, source.StudioRange, VideoPlayerKind.Editor, _log);
        }

        return source;
    }

    private void StartLoadProgress()
    {
        // The recording is a local file: download/buffering percentages do not measure
        // readiness. Keep the loading message until the existing open or failure path runs.
        _openTimeoutTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = OpenTimeoutForTest };
        _openTimeoutTimer.Tick += (_, _) =>
        {
            _openTimeoutTimer?.Stop();
            if (!_mediaReady && !_mediaFailed)
            {
                _log.LogWarning(
                    "MediaOpened did not fire within {Timeout:0.#}s; opening with the container or recorded duration",
                    OpenTimeoutForTest.TotalSeconds);
                double containerMs = _sourceInfo is { IsCompletedSuccessfully: true } probe ? probe.Result.DurationMs : 0;
                CompleteOpen(VideoDurationResolver.Resolve(containerMs, null, _recording.DurationMs), fromFallback: true);
            }
        };
        _openTimeoutTimer.Start();
    }

    /// <summary>Raised when the user commits a still-image edit taken from a frame.</summary>
    internal event EventHandler<AnnotationFrameCapturedEventArgs>? FrameImageCaptured;

    internal Func<FrameImageCommitSession>? FrameImageCommitHandlerFactory { get; set; }

    internal IPrivacyRedactionService? PrivacyRedactionService { get; set; }

    /// <summary>Allocates a private, same-directory MP4 stage for a queue-backed editor.</summary>
    internal Func<string>? RenderStagingPathFactory { get; set; }

    /// <summary>Atomically commits a completed stage and non-destructive edit document.</summary>
    internal Func<VideoEditDocument, string, CancellationToken, Task>? VideoCommitHandler { get; set; }

    /// <summary>Set by the gallery's GIF command so export opens as soon as media is ready.</summary>
    internal bool ExportGifWhenReady { get; set; }

    internal event EventHandler? VideoCommitted;

    // ---- layout ----

    private Grid BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // details
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // commands
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 126 }); // preview
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // transport
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MaxHeight = 250 }); // timelines
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // operation status

        Grid header = BuildCommandBar();
        Grid.SetColumnSpan(header, 2);
        root.Children.Add(header);
        var previewStack = new Grid { Name = "VideoPreviewViewport", MinHeight = 112, ClipToBounds = true };
        previewStack.Children.Add(_media);
        previewStack.Children.Add(_overlayPreview);
        previewStack.Children.Add(_layerCanvas);
        previewStack.Children.Add(_trimPreviewBadge);
        previewStack.Children.Add(_loadingOverlay);
        var preview = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 6, 8, 6), Child = previewStack,
        };
        preview.SetResourceReference(Border.BackgroundProperty, "Surface.Canvas");
        preview.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        var previewArea = new Grid { Name = "VideoPreviewArea" };
        previewArea.Children.Add(preview);
        Grid.SetRow(previewArea, 1);
        root.Children.Add(previewArea);
        // Reserve a separate column so the aspect-fit video and layer handles remain
        // visible and interactive. Spanning the editing rows keeps frame stepping beside
        // the trim tracks even in short windows. The reflow uses no animation.
        _detailsPanel = BuildDetailsPanel();
        Grid.SetColumn(_detailsPanel, 1);
        Grid.SetRow(_detailsPanel, 1);
        Grid.SetRowSpan(_detailsPanel, 3);
        root.Children.Add(_detailsPanel);

        Grid controls = BuildControlRow();
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);
        var timelineScroll = new ScrollViewer
        {
            Name = "VideoTimelineToolsScroll", Content = BuildTimeline(), MaxHeight = 250,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0), Focusable = false,
        };
        timelineScroll.GotKeyboardFocus += (_, args) =>
        {
            if (args.NewFocus is FrameworkElement focused) focused.BringIntoView();
        };
        AutomationProperties.SetName(timelineScroll, UiText.Get("Text_D9378793F9FE"));
        Grid.SetRow(timelineScroll, 3);
        root.Children.Add(timelineScroll);
        var statusBar = new Border { MinHeight = 20, Margin = new Thickness(0, 4, 0, 0), Child = _statusLabel };
        Grid.SetRow(statusBar, 4);
        Grid.SetColumnSpan(statusBar, 2);
        root.Children.Add(statusBar);
        RefreshOverlayList();
        RefreshIdentityDetails(_recording.DurationMs);

        void FitTimelineToAvailableHeight()
        {
            if (root.ActualHeight <= 0) return;
            double clientHeight = VisualTreeHelper.GetParent(root) is FrameworkElement client && client.ActualHeight > 0
                ? Math.Max(0, client.ActualHeight - root.Margin.Top - root.Margin.Bottom)
                : root.ActualHeight;
            double available = clientHeight - header.DesiredSize.Height - controls.DesiredSize.Height
                - statusBar.DesiredSize.Height - root.RowDefinitions[1].MinHeight;
            double maximum = Math.Clamp(Math.Floor(available), 100, 250);
            if (Math.Abs(timelineScroll.MaxHeight - maximum) > 0.5)
            {
                root.RowDefinitions[3].MaxHeight = maximum;
                timelineScroll.MaxHeight = maximum;
            }
        }
        root.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        header.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        controls.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        statusBar.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        root.Loaded += (_, _) => FitTimelineToAvailableHeight();
        return root;
    }

    internal bool IsCaptureBusy => _operationRunning || _extractingFrame || _closing || _closeRequested || !WindowForeground.IsEnabled(this);

    private Grid BuildCommandBar()
    {
        var bar = new Grid { Name = "VideoEditorCommandBar", Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(bar, UiText.Get("VideoEditorUx_CommandBarName"));
        _identityDetails = new TextBlock { FontSize = 12 };
        var editing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        editing.Children.Add(MyCapture.App.Capture.CaptureCommandMenu.CreateButton());
        Button mediaInfo = MediaExportVisuals.Button(this, UiText.Get("MediaInfo.Title"));
        mediaInfo.Name = "MediaInfo";
        CommandButtonPresentation.Apply(mediaInfo);
        mediaInfo.ToolTip = UiText.Get("MediaInfo.Title");
        AutomationProperties.SetName(mediaInfo, UiText.Get("MediaInfo.Title"));
        mediaInfo.Click += async (_, _) => await MyCapture.App.Media.MediaDetailsWindow.ShowAsync(this,
            [new(_recording.OutputPath, _recording.Width, _recording.Height, _durationMs > 0 ? _durationMs : _recording.DurationMs)]);
        editing.Children.Add(mediaInfo);
        _undoButton = MakeTransportIcon("Icon.Undo", UiText.Get("Text_CE706412FA75"), "Button.Ghost", () => RestoreEdit(false));
        _redoButton = MakeTransportIcon("Icon.Redo", UiText.Get("Text_078C44D5A66E"), "Button.Ghost", () => RestoreEdit(true));
        editing.Children.Add(_undoButton);
        editing.Children.Add(_redoButton);
        editing.Children.Add(CommandSeparator());
        _trimToolButton = ToolButton("Icon.Trim", UiText.Get("VideoEditorUx_Trim"), UiText.Get("VideoEditorUx_TrimHelp"), SelectTrimHandle, "Trim");
        _addTextToolButton = ToolButton("Icon.Text", UiText.Get("VideoEditorUx_TextShort"), UiText.Get("VideoEditorUx_AddTextHelp"), AddTextOverlay, "AddText");
        AutomationProperties.SetName(_addTextToolButton, UiText.Get("VideoEditorUx_AddText"));
        editing.Children.Add(_trimToolButton);
        editing.Children.Add(_addTextToolButton);
        // Command models retain enabled state and shortcuts while More presents them.
        _splitToolButton = ToolButton("Icon.Split", UiText.Get("VideoMulticut_Split"), UiText.Get("VideoMulticut_SplitHelp"), SplitHere, "Split");
        _restoreSegmentButton = ToolButton("Icon.Undo", UiText.Get("VideoMulticut_Restore"), UiText.Get("VideoMulticut_RestoreHelp"), RestoreSelectedSegment);
        _frameEditToolButton = ToolButton("Icon.Image", UiText.Get("VideoEditorUx_FrameImageEdit"), UiText.Get("Text_A79A9F93D479"), EditCurrentFrame, "FrameEdit");
        _splitToolButton.Visibility = _restoreSegmentButton.Visibility = _frameEditToolButton.Visibility = Visibility.Collapsed;
        _moreToolsButton = ToolButton("Icon.More", string.Empty, UiText.Get("Common_More"), OpenMoreTools);
        _moreToolsButton.Name = "VideoMoreTools";
        AutomationProperties.SetName(_moreToolsButton, UiText.Get("Common_More"));
        CommandButtonPresentation.AlwaysIcon(_undoButton);
        CommandButtonPresentation.AlwaysIcon(_redoButton);
        CommandButtonPresentation.AlwaysIcon(_moreToolsButton);
        editing.Children.Add(_moreToolsButton);

        var output = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button saveEdits = MediaExportVisuals.Button(this, UiText.Get("VideoEditorUx_SaveShort"), primary: true);
        saveEdits.Name = "VideoSave";
        CommandButtonPresentation.Apply(saveEdits);
        CommandButtonPresentation.KeepLabel(saveEdits);
        AutomationProperties.SetName(saveEdits, UiText.Get("MediaExport_SaveEdits"));
        saveEdits.ToolTip = UiText.Get("VideoEditorUx_SaveEditsHelp");
        AutomationProperties.SetHelpText(saveEdits, UiText.Get("VideoEditorUx_SaveEditsHelp"));
        saveEdits.Click += (_, _) => CommitTrim();
        Button export = MediaExportVisuals.Button(this, UiText.Get("VideoEditorUx_ExportShort"), "MediaExport_ArrowExport");
        export.Name = "VideoExport";
        _exportToolButton = export;
        CommandButtonPresentation.Apply(export);
        AutomationProperties.SetName(export, UiText.Get("MediaExport_ExportFormats"));
        export.ToolTip = UiText.Get("VideoEditorUx_ExportHelp");
        AutomationProperties.SetHelpText(export, UiText.Get("VideoEditorUx_ExportHelp"));
        export.Click += (_, _) => OpenExport();
        _editControls.Add(saveEdits); _editControls.Add(export);
        output.Children.Add(export); output.Children.Add(saveEdits);
        bar.Children.Add(new CommandGroupsPanel { Children = { editing, output } });
        BuildMoreToolsMenu();
        return bar;
    }

    private Button ToolButton(string iconKey, string label, string help, Action onClick, string? command = null)
    {
        StackPanel content = BuildIconLabel(iconKey, label);
        Button button = MakeButton(content, label, "Button.Secondary", onClick);
        button.ToolTip = help;
        AutomationProperties.SetHelpText(button, help);
        if (command is not null) ShortcutPresentation.Attach(button, ShortcutScope.Video, command);
        return button;
    }

    private void BuildMoreToolsMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = _moreToolsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        Add(_splitToolButton, SplitHere, "Split");
        Add(_restoreSegmentButton, RestoreSelectedSegment);
        Add(_frameEditToolButton, EditCurrentFrame, "FrameEdit");
        menu.Items.Add(new Separator());
        AddView("Text_48D137437347", "ZoomOut", _shortcutActions["ZoomOut"]);
        AddView("Text_C5176D8C3041", "ZoomIn", _shortcutActions["ZoomIn"]);
        AddView("Text_6CA53DEDFF4A", null, () => { ShowDetailTimeline(); _timeline.FitAll(); });
        var detail = new MenuItem { Header = UiText.Get("VideoEditorUx_DetailTimeline"), IsCheckable = true };
        detail.ToolTip = UiText.Get("VideoEditorUx_DetailTimelineHelp");
        AutomationProperties.SetName(detail, UiText.Get("VideoEditorUx_DetailTimeline"));
        detail.Click += (_, _) => _timeline.SetDetailVisible(detail.IsChecked);
        menu.Opened += (_, _) => detail.IsChecked = _timeline.DetailVisible;
        menu.Items.Add(detail);
        _moreToolsButton.ContextMenu = menu;

        void Add(Control tool, Action action, string? command = null)
        {
            MenuItem item = AddView(AutomationProperties.GetName(tool), command, action, isResourceKey: false);
            item.ToolTip = tool.ToolTip;
            AutomationProperties.SetHelpText(item, AutomationProperties.GetHelpText(tool));
            item.SetBinding(IsEnabledProperty, new Binding(nameof(tool.IsEnabled)) { Source = tool });
        }
        MenuItem AddView(string label, string? command, Action action, bool isResourceKey = true)
        {
            string name = isResourceKey ? UiText.Get(label) : label;
            var item = new MenuItem { Header = name, ToolTip = name };
            AutomationProperties.SetName(item, name);
            if (command is not null)
            {
                item.InputGestureText = ShortcutCatalog.Get(ShortcutScope.Video, command).Hint;
                item.ToolTip = $"{name} ({item.InputGestureText})";
                AutomationProperties.SetAcceleratorKey(item, item.InputGestureText);
            }
            AutomationProperties.SetHelpText(item, item.ToolTip as string ?? name);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
    }

    private void OpenMoreTools()
    {
        if (_moreToolsButton.ContextMenu is { } menu) menu.IsOpen = true;
    }

    private Border BuildDetailsPanel()
    {
        var content = new StackPanel();
        var heading = new TextBlock
        {
            Text = UiText.Get("VideoEditorUx_Details"), FontSize = 14,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 0, 8),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        content.Children.Add(heading);
        content.Children.Add(BuildPreciseTransport());
        content.Children.Add(BuildTrimBar());
        _layerPanel = new Border
        {
            Name = "VideoLayersPanel", Child = BuildOverlayLane(),
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        _layerPanel.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        content.Children.Add(_layerPanel);
        var scroll = new ScrollViewer
        {
            Name = "VideoDetailsScroll", Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Padding = new Thickness(10),
        };
        scroll.GotKeyboardFocus += (_, args) =>
        {
            if (args.NewFocus is FrameworkElement focused) focused.BringIntoView();
        };
        AutomationProperties.SetName(scroll, UiText.Get("VideoEditorUx_Details"));
        var panel = new Border
        {
            Name = "VideoDetailsPanel", Width = 330,
            Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = scroll, Visibility = Visibility.Collapsed,
        };
        panel.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        panel.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        return panel;
    }

    private FrameworkElement BuildPreciseTransport()
    {
        var precise = new StackPanel { Name = "VideoPreciseTransport" };
        var navigation = new WrapPanel();
        navigation.Children.Add(MakeTransportIcon("Icon.First", UiText.Get("Text_DC710AA11970"), "Button.Ghost", () => Seek(_timeline.InMs)));
        navigation.Children.Add(MakeTransportIcon("Icon.Rewind", UiText.Get("Text_E166B75FADF4"), "Button.Ghost", () => StepCoarse(-1)));
        navigation.Children.Add(MakeTransportIcon("Icon.FastForward", UiText.Get("Text_E5CAF7CFC916"), "Button.Ghost", () => StepCoarse(1)));
        navigation.Children.Add(MakeTransportIcon("Icon.Last", UiText.Get("Text_DFCA22AB9F61"), "Button.Ghost", () => Seek(_timeline.OutMs)));
        precise.Children.Add(navigation);
        var frames = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        frames.Children.Add(MakeTransportIcon("Icon.StepBack", UiText.Get("Text_B39342508541"), "Button.Ghost", () => StepFrames(-1)));
        frames.Children.Add(MakeTransportIcon("Icon.StepForward", UiText.Get("Text_B753165F6585"), "Button.Ghost", () => StepFrames(1)));
        precise.Children.Add(frames);
        _positionFrameLabel.FontSize = 12;
        _positionFrameLabel.Margin = new Thickness(3, 4, 0, 8);
        _positionFrameLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        precise.Children.Add(_positionFrameLabel);
        return precise;
    }

    private void ApplyDetailsVisibility()
    {
        bool expanded = _detailToggle.IsChecked == true;
        if (!expanded && _detailsPanel.IsKeyboardFocusWithin) _detailToggle.Focus();
        _detailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    internal bool AreDetailsExpandedForTest => _detailToggle.IsChecked == true;
    internal void SetDetailsExpandedForTest(bool expanded) => _detailToggle.IsChecked = expanded;
    internal bool IsLayerPanelVisibleForTest => AreDetailsExpandedForTest;

    private Border CommandSeparator() => new()
    {
        Width = 1, Margin = new Thickness(6), Background = TryBrush("Border.Subtle", Colors.Gray),
    };

    private void RefreshIdentityDetails(double durationMs)
    {
        _identityDetails.Text = UiText.Format("VideoEditorUx_HeaderDetails",
            TrimPresentation.Clock(durationMs), _recording.Width, _recording.Height, _recording.Fps);
        _identityDetails.ToolTip = RecordingHealth();
        if (_positionChip is not null) _positionChip.ToolTip = _identityDetails.Text + Environment.NewLine + RecordingHealth();
    }

    private string RecordingHealth() => _recording.DroppedFrames == 0
        ? UiText.Get("Text_C1BE53C8ADAF")
        : UiText.Format("Text_271908EF0BBE", _recording.DroppedFrames, _recording.DropRate);

    private Grid _timelineCache = null!;

    /// <summary>
    /// The trim bar under the trim timeline: type precise times, restore the full length,
    /// and read how long the result is. The handles and I/O set the range directly.
    /// </summary>
    /// <remarks>
    /// Dragging the handles stays the primary path, so a committed value applies immediately
    /// and there is no separate "apply" gate to forget. Enter is kept for typed values. In a
    /// narrow window the range and summary stay in deliberate groups instead of wrapping
    /// whichever control no longer fits.
    /// </remarks>
    private FrameworkElement BuildTrimBar()
    {
        var bar = new StackPanel { Name = "VideoTrimBar", Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(bar, UiText.Get("VideoEditorUx_TrimBarName"));
        _keptLengthLabel = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        _keptLengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _removedLengthLabel = new TextBlock { FontSize = 12 };
        _removedLengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var summary = new StackPanel
        {
            Name = "VideoTrimSummary",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 120,
        };
        summary.Children.Add(_keptLengthLabel);
        summary.Children.Add(_removedLengthLabel);
        // The range: the exact start and end, and the way back to the whole recording.
        var range = new StackPanel { Name = "VideoTrimRange" };
        TextBox Input(string name)
        {
            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            inputRow.Children.Add(new TextBlock { Text = name, FontSize = 12, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 5, 0) });
            var input = new TextBox
            {
                Width = 120, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = UiText.Get("Video.SourceTimeHint"),
            };
            AutomationProperties.SetName(input, name);
            AutomationProperties.SetHelpText(input, UiText.Get("Video.SourceTimeHint"));
            input.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; ApplyTrimInputs(); }
                else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    RefreshTrimInputs();
                }
            };
            input.LostFocus += (_, _) => ApplyTrimInputs();
            inputRow.Children.Add(input); range.Children.Add(inputRow); _editControls.Add(input);
            return input;
        }
        _trimInInput = Input(UiText.Get("Video.TrimIn"));
        _trimOutInput = Input(UiText.Get("Video.TrimOut"));

        _resetTrimButton = MakeButton(UiText.Get("VideoEditorUx_ResetTrim"), UiText.Get("VideoEditorUx_ResetTrim"), "Button.Secondary", ResetTrim);
        CommandButtonPresentation.Apply(_resetTrimButton, compact: true);
        CommandButtonPresentation.ReserveHint(_resetTrimButton);
        _resetTrimButton.ToolTip = UiText.Get("VideoEditorUx_ResetTrimHelp");
        AutomationProperties.SetHelpText(_resetTrimButton, UiText.Get("VideoEditorUx_ResetTrimHelp"));
        _resetTrimButton.HorizontalAlignment = HorizontalAlignment.Left;
        range.Children.Add(_resetTrimButton);
        bar.Children.Add(range);
        bar.Children.Add(summary);
        RefreshTrimInputs();
        return bar;
    }

    private TimelineViewport? _fullLayerViewport;

    private void SyncLayerViewport()
    {
        if (_fullLayerViewport is null || _fullLayerViewport.DurationMs != _timeline.DurationMs)
            _fullLayerViewport = new TimelineViewport(_timeline.DurationMs, _recording.Fps);
        _layerTimeline.SetViewport(_timeline.DetailVisible ? _timeline.Viewport : _fullLayerViewport);
        _layerTimeline.SetKeptRange(_timeline.InMs, _timeline.OutMs);
    }

    private void AlignTimelineTracks()
    {
        if (_layerTracksScroll is null || _layerTimeline.ActualWidth <= 0) return;
        double right = Math.Max(0, _layerTracksScroll.ActualWidth - _layerTimeline.ActualWidth);
        _timeline.Margin = new Thickness(VideoLayerTimeline.LabelWidth - TwoLineTimeline.TrackInset, 0,
            right - TwoLineTimeline.TrackInset, 0);
    }

    private Grid BuildTimeline()
    {
        if (_timelineCache is not null)
        {
            return _timelineCache;
        }

        var timeline = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // trim timeline (+ detail)
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // layer tracks

        timeline.Children.Add(_timeline);

        var layerScroll = new ScrollViewer
        {
            Content = _layerTimeline, MaxHeight = 72, Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        AutomationProperties.SetName(layerScroll, UiText.Get("Text_D9378793F9FE"));
        _layerTracksScroll = layerScroll;
        layerScroll.ScrollChanged += (_, _) => _layerTimeline.SetVerticalOffset(layerScroll.VerticalOffset);
        // The rows reserve a label gutter and may acquire a vertical scrollbar. Give both
        // time strips exactly that same horizontal time area, even when the detail is folded.
        _layerTimeline.SizeChanged += (_, _) => AlignTimelineTracks();
        layerScroll.SizeChanged += (_, _) => AlignTimelineTracks();
        _timeline.Margin = new Thickness(VideoLayerTimeline.LabelWidth - TwoLineTimeline.TrackInset, 0,
            -TwoLineTimeline.TrackInset, 0);
        Grid.SetRow(layerScroll, 1);
        timeline.Children.Add(layerScroll);


        RefreshOverlayList();

        _timelineCache = timeline;
        return timeline;
    }

    private FrameworkElement BuildOverlayLane()
    {
        var lane = new Grid();

        lane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // The list scrolls inside this row. Its reserved minimum, one layer, only binds in the
        // smallest windows, where the command bar above and the trim tools below need the height.
        lane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 48 });
        lane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The heading keeps its whole width. Edit and delete used to share this row, and in the
        // narrowest panel they left the heading 42 DIP, which cut its last letter. Add layer is
        // narrow enough to sit beside it; edit and delete moved under the list.
        var label = new TextBlock
        {
            Text = UiText.Get("Video.Layers"),
            FontWeight = FontWeights.SemiBold,
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var layerHeader = new Grid();
        layerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        layerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layerHeader.Children.Add(label);
        lane.Children.Add(layerHeader);

        // Each row already shows its layer's name and visible interval, and the edit and delete
        // commands below act on the selected row. A separate properties block repeated the same
        // name and times under the list and took the height the preview needs in small windows.
        Grid.SetRow(_overlayList, 1);
        _overlayList.MaxHeight = 128;
        _overlayList.Margin = new Thickness(0, 6, 0, 6);
        lane.Children.Add(_overlayList);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
        };
        var addMenu = new ContextMenu();
        foreach ((string key, Action add) in new (string, Action)[]
        {
            ("Text_258AD4B095A1", AddTextOverlay), ("Text_DC0760235344", () => AddShapeLayer(false)),
            ("Text_C19FD6787279", () => AddShapeLayer(true)), ("Text_302BAE127938", AddImageLayer),
        })
        {
            var item = new MenuItem { Header = UiText.Get(key) };
            if (key == "Text_258AD4B095A1") item.InputGestureText = ShortcutCatalog.Get(ShortcutScope.Video, "AddText").Hint;
            item.Click += (_, _) => add();
            addMenu.Items.Add(item);
        }
        _addTextButton = MakeButton(UiText.Get("Video.AddLayer"), UiText.Get("Video.AddLayer"), "Button.Secondary", () => addMenu.IsOpen = true);
        CommandButtonPresentation.Apply(_addTextButton, compact: true);
        CommandButtonPresentation.ReserveHint(_addTextButton);
        _addTextButton.ContextMenu = addMenu;
        addMenu.PlacementTarget = _addTextButton;
        _editTextButton = MakeTransportIcon("Icon.Edit", UiText.Get("Text_AEB8B9AA4E6F"), "Button.Ghost", EditSelectedOverlay);
        _deleteTextButton = MakeTransportIcon("Icon.Delete", UiText.Get("Text_9163C86EDB22"), "Button.Ghost", DeleteSelectedOverlay);
        CommandButtonPresentation.Apply(_editTextButton, compact: true);
        CommandButtonPresentation.Apply(_deleteTextButton, compact: true);
        _addTextButton.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_addTextButton, 2);
        layerHeader.Children.Add(_addTextButton);
        actions.Children.Add(_editTextButton);
        actions.Children.Add(_deleteTextButton);
        Grid.SetRow(actions, 2);
        Grid.SetColumnSpan(actions, 2);
        lane.Children.Add(actions);

        AutomationProperties.SetName(lane, UiText.Get("Text_E9059CA2B6E3"));
        return lane;
    }

    private Grid BuildControlRow()
    {
        var controls = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetName(controls, UiText.Get("Text_A35D0E14E844"));
        var row = new Grid { Name = "VideoTransport" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Button play = MakeTransportIcon("Icon.Play", UiText.Get("Text_9A9C87658130"), "Button.Secondary", TogglePlay);
        play.MinWidth = 44;
        play.Name = "VideoPlayPause";
        _playButton = play;
        UpdatePlayButton();
        row.Children.Add(play);
        _positionLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _positionLabel.VerticalAlignment = VerticalAlignment.Center;
        _positionChip = new Border
        {
            Name = "VideoPositionChip", Child = _positionLabel, Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_positionChip, 1);
        row.Children.Add(_positionChip);
        _detailToggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Name = "VideoDetailsToggle", Content = BuildIconLabel("Icon.ChevronDown", UiText.Get("VideoEditorUx_Details")),
            ToolTip = UiText.Get("VideoEditorUx_DetailsHelp"), IsChecked = false,
        };
        CommandButtonPresentation.Apply(_detailToggle);
        AutomationProperties.SetName(_detailToggle, UiText.Get("VideoEditorUx_Details"));
        AutomationProperties.SetHelpText(_detailToggle, UiText.Get("VideoEditorUx_DetailsHelp"));
        _detailToggle.Checked += (_, _) => ApplyDetailsVisibility();
        _detailToggle.Unchecked += (_, _) => ApplyDetailsVisibility();
        // Space follows the window rule for buttons (toggle after Tab, playback after a click); Enter also enters the opened details.
        _detailToggle.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                _detailToggle.IsChecked = _detailToggle.IsChecked != true;
                args.Handled = true;
                if (_detailToggle.IsChecked == true)
                {
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        if (!_closing && _detailToggle.IsChecked == true && _detailToggle.IsKeyboardFocused)
                            _detailsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    }));
                }
            }
        };
        Grid.SetColumn(_detailToggle, 2);
        row.Children.Add(_detailToggle);
        controls.Children.Add(row);
        ShortcutPresentation.Attach(_saveProgress.CancelButton, ShortcutScope.Video, "Cancel");
        _saveProgress.CancelButton.Click += (_, _) => _operationCts?.Cancel();
        Grid.SetRow(_saveProgress, 1);
        controls.Children.Add(_saveProgress);
        _controlRows = controls;
        return controls;
    }

    private static Border Spacer(double width) => new() { Width = width };

    private Button? _playButton;

    /// <summary>The play button shows what the next press does: play, or pause while playing.</summary>
    private void UpdatePlayButton()
    {
        if (_playButton is null)
        {
            return;
        }

        bool playing = _isPlaying || _playRequested;
        // The key caption wraps the glyph in a second panel, so look for it at any depth.
        if (FindGlyph(_playButton.Content) is { } glyph)
        {
            glyph.SetResourceReference(SymbolIcon.DataProperty, playing ? "Icon.Pause" : "Icon.Play");
        }

        // The tooltip keeps the key, like every other command with a shortcut.
        _playButton.ToolTip = $"{UiText.Get(playing ? "VideoEditorUx_Pause" : "VideoEditorUx_Play")} ({ShortcutCatalog.Get(ShortcutScope.Video, "Play").Hint})";
        _playButtonShowsPause = playing;
    }

    private static SymbolIcon? FindGlyph(object? content) => content switch
    {
        SymbolIcon icon => icon,
        Panel panel => panel.Children.Cast<object>().Select(FindGlyph).FirstOrDefault(found => found is not null),
        _ => null,
    };

    internal Geometry? PlayGlyphDataForTest => FindGlyph(_playButton?.Content)?.Data;

    private bool _playButtonShowsPause;

    internal bool PlayButtonShowsPauseForTest => _playButtonShowsPause;

    private void SetEditControlsEnabled(bool enabled)
    {
        _layerCanvas.IsEnabled = enabled;
        _timeline.IsEnabled = enabled;
        _layerTimeline.IsEnabled = enabled;
        foreach (Control c in _editControls)
        {
            c.IsEnabled = enabled;
        }

        UpdateOverlayActionStates();
        RefreshTrimSummary();
    }

    // ---- media lifecycle ----

    private void OnPreviewPositionRequested() => StudioRangeVideoEffect.PositionRequested(_media);

    private async void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        double? playerMs = _media.NaturalDuration.HasTimeSpan
            ? _media.NaturalDuration.TimeSpan.TotalMilliseconds
            : null;
        // The probe never faults and normally finishes before MediaOpened; its range is applied
        // by the time it completes.
        VideoSourceInfo source = _sourceInfo is null ? new VideoSourceInfo(0, StudioRange: true) : await _sourceInfo;
        if (_closing)
        {
            return;
        }

        // The preview engine reopens the clip to leave the player's media-ended state.
        bool reopened = _mediaOpened;
        _mediaOpened = true;
        if (_mediaReady && !reopened)
        {
            _log.LogInformation("MediaOpened arrived after the editor opened via the duration fallback");
        }

        double containerMs = source.DurationMs;

        double durationMs = VideoDurationResolver.Resolve(containerMs, playerMs, _recording.DurationMs);
        _log.LogDebug(
            "Editor duration {Duration:0.###}ms (container {Container:0.###}ms, player {Player}ms, recorded {Recorded:0.###}ms)",
            durationMs,
            containerMs,
            playerMs,
            _recording.DurationMs);
        CompleteOpen(durationMs, fromFallback: false);
    }

    /// <summary>
    /// Finalises the ready state — from either MediaOpened or the open-timeout fallback — so
    /// the editor is never stuck on the loading overlay for a clip that decodes slowly or whose
    /// MediaOpened never arrives (the reported ~2s-clip case).
    /// </summary>
    private void CompleteOpen(double durationMs, bool fromFallback)
    {
        if (_mediaReady)
        {
            return;
        }

        StopLoadTimers();

        _durationMs = durationMs > 0 ? durationMs : Math.Max(1, _recording.DurationMs);
        _previewEngine.DurationMs = _durationMs;
        _mediaReady = true;
        RefreshIdentityDetails(_durationMs);

        _timeline.Initialize(_durationMs, _recording.Fps);
        _timeline.SetFilmstripSource(_recording.OutputPath, _recording.Width, _recording.Height, _loggerFactory);
        _layerTimeline.Initialize(_durationMs);
        SyncLayerViewport();
        _editDocument = _editDocument.NormalizeFor(
            _recording.Width,
            _recording.Height,
            _durationMs);
        _initialDocument = _editDocument.Clone();
        SynchronizeTrim();
        _timeline.SetPlayhead(_editDocument.TrimInMs);
        _timeline.SetTrimMode(true);
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _overlayPreview.SetFrameLayers(_editDocument.FrameEditLayers);
        _layerCanvas.SetDocument(_editDocument);
        _layerCanvas.SetSourceTime(_editDocument.TrimInMs);
        _overlayPreview.SetSourceTime(_editDocument.TrimInMs);
        RefreshOverlayList();

        try
        {
            StudioRangeVideoEffect.PositionRequested(_media);
            _media.Position = TimeSpan.FromMilliseconds(_previewEngine.PresentationPositionMs(_editDocument.TrimInMs));
            _media.Pause();
        }
        catch (InvalidOperationException)
        {
            // Position before the element is fully ready can throw; harmless here.
        }

        _loadingOverlay.Visibility = Visibility.Collapsed;
        SetEditControlsEnabled(true);

        UpdatePositionLabel(_editDocument.TrimInMs);
        UpdateStatusForMode();
        if (fromFallback)
        {
            _log.LogInformation("Editor opened via duration fallback ({Duration:0}ms)", _durationMs);
        }

        _ = Keyboard.Focus(this);
        if (ExportGifWhenReady)
        {
            ExportGifWhenReady = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ExportGif));
        }
    }

    private void StopLoadTimers()
    {
        _openTimeoutTimer?.Stop();
        _openTimeoutTimer = null;
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        StopLoadTimers();
        _mediaFailed = true;
        _mediaFailure = e?.ErrorException?.Message ?? UiText.Get("Text_6A72B554A7C2");
        _log.LogError(e?.ErrorException, "Playback of {Path} failed", _recording.OutputPath);
        _loadingLabel.Text = UiText.Get("Text_31527734190B");
        _loadingLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        _statusLabel.Text = UiText.Get("Text_DD9DFB5B4BE3") + _mediaFailure;
        _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
    }

    private void OnMediaEnded(object? sender, RoutedEventArgs e)
    {
        _previewEngine.StreamReachedEnd();
        _media.Pause();
        _isPlaying = false;
        _playbackTimer.Stop();
        UpdatePlayButton();
        if (_mediaReady)
        {
            // Windows Media Player reports the end position at its whole-second length (2000ms
            // for a 2133ms clip), so MediaElement.Position cannot place the playhead here. The
            // stream ended, so playback reached the selection end: settle there exactly, as the
            // Last button does, and the next Play restarts at In.
            if (_cutTimeline.RemovedRanges.Count > 0) SettleCutPlaybackEnd();
            else Seek(_timeline.OutMs);
            UpdateStatusForMode();
        }
    }

    // ---- transport ----

    private void PausePlayback()
    {
        _cutPlayback.Reset();
        _cutSeekInFlight = false;
        _settlingCutPlaybackEnd = false;
        _playRequestVersion++;
        _playRequested = false;
        _isPlaying = false;
        _playbackTimer.Stop();
        if (_mediaReady) { _media.Pause(); }
        UpdatePlayButton();
    }

    private async void TogglePlay()
    {
        if (!_mediaReady || _operationRunning) { return; }
        if (_isPlaying || _playRequested)
        {
            _resumeAfterScrub = false;
            PausePlayback();
            UpdateStatusForMode();
            return;
        }
        _playRequested = true;
        UpdatePlayButton();
        long version = ++_playRequestVersion;
        double position = CurrentMs();
        double outputEnd = _cutTimeline.ToSourceTime(_cutTimeline.OutputDurationMs);
        if (position < _timeline.InMs || position >= outputEnd - 0.5) { position = _timeline.InMs; }
        position = _cutTimeline.ToSourceTime(_cutTimeline.ToOutputTime(position));
        _cutPlayback.Reset();
        try
        {
            // Reconcile queued seeks before starting playback; their engine pauses the media.
            await _previewSeeks.RequestExactAsync(position);
            // After the stream ended, the player can stop past that seek; play from where it is put back.
            await _previewEngine.WhenSettledAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != _playRequestVersion || !_playRequested || !IsLoaded) { return; }
                _playRequested = false;
                _playMedia();
                _isPlaying = true;
                _playbackTimer.Start();
                UpdatePlayButton();
                _statusLabel.Text = UiText.Get("Text_FD6443988325");
            });
        }
        catch (OperationCanceledException)
        {
            // The seek was superseded, or the editor closed and its dispatcher shut down while
            // the start was queued; either way only a still-current request is undone.
            if (!Dispatcher.HasShutdownStarted && version == _playRequestVersion) { _playRequested = false; UpdatePlayButton(); }
        }
        catch (Exception ex)
        {
            if (!Dispatcher.HasShutdownStarted && version == _playRequestVersion) { PausePlayback(); OnPreviewSeekFailed(ex); }
        }
    }
    private void Seek(double positionMs)
    {
        if (!_mediaReady)
        {
            return;
        }

        PausePlayback();
        double clamped = Math.Clamp(positionMs, 0, _durationMs);
        _previewSeeks.RequestExact(clamped);
        _timeline.SetPlayhead(clamped);
        _layerTimeline.SetPlayhead(clamped);
        UpdatePositionLabel(clamped);
    }

    /// <summary>
    /// A scrub on the timeline during playback resumes it from the released position, as
    /// players do; a trim drag or an explicit pause does not.
    /// </summary>
    private bool _resumeAfterScrub;

    /// <summary>Playhead visual intent moved during pointer interaction.</summary>
    private void OnTimelinePlayhead(object? sender, double ms)
    {
        if (!_mediaReady)
        {
            return;
        }

        if (_isPlaying || _playRequested)
        {
            _resumeAfterScrub = true;
        }

        PausePlayback();
        double clamped = Math.Clamp(ms, 0, _durationMs);
        UpdatePositionLabel(clamped);
        _layerTimeline.SetPlayhead(clamped);
        _previewSeeks.RequestPreview(clamped);
    }

    /// <summary>Pointer release requests one priority exact reconciliation seek.</summary>
    private void OnTimelinePlayheadInteractionCompleted(object? sender, double ms)
    {
        if (!_mediaReady)
        {
            return;
        }

        _previewSeeks.RequestExact(Math.Clamp(ms, 0, _durationMs));
        if (_resumeAfterScrub && !_timeline.IsTrimDragging)
        {
            _resumeAfterScrub = false;
            TogglePlay();
        }
    }

    private void OnPreviewSeekFailed(Exception exception)
    {
        _log.LogWarning(exception, "Preview seek failed");
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnPreviewSeekFailed(exception)),
                DispatcherPriority.Background);
            return;
        }

        if (IsLoaded)
        {
            _statusLabel.Text = UiText.Get("Text_483B0F23F316") + MyCapture.App.Themes.ThemedDialog.RecoveryMessage(exception);
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }
    }

    private void OnPreviewPresented(PresentedPreviewFrame frame)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnPreviewPresented(frame)),
                DispatcherPriority.Render);
            return;
        }

        if (IsLoaded && !_cutSeekInFlight)
        {
            double presented = Math.Clamp(
                frame.PresentedPositionMs,
                0,
                _durationMs);
            _overlayPreview.SetSourceTime(presented);
            _layerCanvas.SetSourceTime(presented);
            _layerTimeline.SetPlayhead(_settlingCutPlaybackEnd ? _timeline.PlayheadMs : presented);
        }
    }

    private void StepFrames(int frames)
    {
        double next = FrameStepCalculator.StepByFrames(CurrentMs(), frames, _recording.Fps, _durationMs);
        Seek(next);
        int total = TotalFrameCount();
        int frame = Math.Min(total, FrameStepCalculator.FrameIndexAt(next, _recording.Fps, _durationMs) + 1);
        _statusLabel.Text = UiText.Format("Text_D5D43E09190C", frame, total, FormatMs(next));
    }

    private void StepCoarse(int direction)
    {
        double next = FrameStepCalculator.StepCoarse(CurrentMs(), direction, _durationMs, 5.0);
        Seek(next);
    }

    private double CurrentMs() => _timeline.PlayheadMs;

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || !_mediaReady)
        {
            return;
        }

        double position = _media.Position.TotalMilliseconds;
        PreviewCutDecision decision = _cutPlayback.Observe(_cutTimeline, position, PlaybackClock());
        if (decision.Action == PreviewCutAction.TimedOut)
        {
            PausePlayback();
            _statusLabel.Text = UiText.Get("VideoMulticut_PreviewSeekFailed");
            return;
        }
        if (_cutSeekInFlight || decision.Action == PreviewCutAction.Wait) return;
        if (decision.Action == PreviewCutAction.Seek)
        {
            _ = SkipRemovedRange(decision.SourceTimeMs);
            return;
        }
        if (decision.Action == PreviewCutAction.Stop)
        {
            if (_durationMs - decision.SourceTimeMs <= 0.5)
            {
                // Paused at the last frame, the player has usually reached the end of the stream.
                _previewEngine.StreamReachedEnd();
            }

            if (_cutTimeline.RemovedRanges.Count > 0)
            {
                SettleCutPlaybackEnd();
                return;
            }
            PausePlayback();
            position = decision.SourceTimeMs;
            UpdateStatusForMode();
        }

        double clamped = Math.Clamp(decision.SourceTimeMs, _timeline.InMs, _timeline.OutMs);
        _timeline.SetPlayhead(clamped, ensureVisible: false);
        _overlayPreview.SetSourceTime(clamped);
        _layerCanvas.SetSourceTime(clamped);
        _layerTimeline.SetPlayhead(clamped);
        UpdatePositionLabel(clamped);
    }

    private static TimeSpan PlaybackClock() => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());

    private void SettleCutPlaybackEnd()
    {
        PausePlayback();
        _settlingCutPlaybackEnd = true;
        double frameMs = PreviewCutPlayback.LastKeptFrame(_cutTimeline, _recording.Fps);
        double endMs = _cutTimeline.ToSourceTime(_cutTimeline.OutputDurationMs);
        _previewSeeks.RequestExact(frameMs);
        _timeline.SetPlayhead(endMs, ensureVisible: false);
        _layerTimeline.SetPlayhead(endMs);
        _overlayPreview.SetSourceTime(frameMs);
        _layerCanvas.SetSourceTime(frameMs);
        UpdatePositionLabel(endMs);
        UpdateStatusForMode();
    }

    private async Task SkipRemovedRange(double targetMs)
    {
        _cutSeekInFlight = true;
        long version = _playRequestVersion;
        try
        {
            // The coordinator serializes decoder access. Its completion is best-effort;
            // subsequent ticks observe the actual landing before publishing new overlays.
            await _previewSeeks.RequestExactAsync(targetMs);
            await _previewEngine.WhenSettledAsync();
            if (version != _playRequestVersion || !_isPlaying || _closing) return;
            _playMedia();
        }
        catch (OperationCanceledException)
        {
            if (version == _playRequestVersion) PausePlayback();
        }
        catch (Exception error)
        {
            if (version == _playRequestVersion) { PausePlayback(); OnPreviewSeekFailed(error); }
        }
        finally
        {
            if (version == _playRequestVersion) _cutSeekInFlight = false;
        }
    }

    // ---- trim ----

    /// <summary>
    /// The "Trim" command (X, as crop is X in the image editor): selects the start handle of
    /// the kept range for keyboard adjustment and shows its frame; pressed again while the
    /// timeline has focus, it switches to the end handle.
    /// </summary>
    private void SelectTrimHandle()
    {
        if (!_mediaReady || _operationRunning)
        {
            return;
        }

        // The second press (button or X) moves on to the end handle; a mouse click on the
        // button takes focus from the timeline, so the armed state decides, not focus.
        bool end = _timeline.IsHandleArmed && !_timeline.IsEndHandleActive;
        _timeline.ActivateTrimHandle(end);
        Seek(end ? _timeline.OutMs : _timeline.InMs);
        _statusLabel.Text = UiText.Get(end ? "VideoEditorUx_TrimHandleEnd" : "VideoEditorUx_TrimHandleStart");
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
    }

    private void SetInHere()
    {
        _timeline.SetTrimMode(true);
        _timeline.SetIn(CurrentMs());
        _statusLabel.Text = UiText.Format("VideoEditorUx_TrimStartSet",
            TrimPresentation.Clock(_timeline.InMs), TrimPresentation.Clock(_cutTimeline.OutputDurationMs));
    }

    private void SetOutHere()
    {
        _timeline.SetTrimMode(true);
        _timeline.SetOut(CurrentMs());
        _statusLabel.Text = UiText.Format("VideoEditorUx_TrimEndSet",
            TrimPresentation.Clock(_timeline.OutMs), TrimPresentation.Clock(_cutTimeline.OutputDurationMs));
    }

    /// <summary>Removes the trim: the whole recording is kept again (one undo step).</summary>
    private void ResetTrim()
    {
        if (!_mediaReady || _operationRunning
            || (_timeline.IsFullClip && _editDocument.RemovedRanges.Count == 0 && _editDocument.SplitPoints.Count == 0))
        {
            return;
        }

        PausePlayback();
        RememberEdit();
        _editDocument.TrimInMs = 0;
        _editDocument.TrimOutMs = _durationMs;
        _editDocument.RemovedRanges.Clear();
        _editDocument.SplitPoints.Clear();
        SynchronizeTrim();
        UpdateStatusForMode();
        _statusLabel.Text = UiText.Get("VideoEditorUx_TrimResetDone");
    }

    private void ShowDetailTimeline() => _timeline.SetDetailVisible(true);

    private void BeginTrimInteraction()
    {
        _resumeAfterScrub = false;
        PausePlayback();
        _trimInteractionBefore = _editDocument.Clone();
        UpdateTrimPreviewBadge();
    }

    private void UpdateTrimPreviewBadge()
    {
        if (!_timeline.IsTrimDragging)
        {
            _trimPreviewBadge.Visibility = Visibility.Collapsed;
            return;
        }

        _trimPreviewBadgeText.Text = _timeline.IsEndHandleActive
            ? TrimPresentation.EndChip(_timeline.OutMs)
            : TrimPresentation.StartChip(_timeline.InMs);
        _trimPreviewBadge.Visibility = Visibility.Visible;
    }

    private void RefreshTrimSummary()
    {
        if (_keptLengthLabel is null) return;
        _keptLengthLabel.Text = TrimPresentation.KeptLength(_cutTimeline);
        _removedLengthLabel.Text = TrimPresentation.RemovedLength(_timeline.DurationMs, _cutTimeline);
        AutomationProperties.SetName(_keptLengthLabel, _keptLengthLabel.Text + ", " + _removedLengthLabel.Text);
        _resetTrimButton.IsEnabled = _mediaReady && !_operationRunning
            && (!_timeline.IsFullClip || _editDocument.RemovedRanges.Count > 0 || _editDocument.SplitPoints.Count > 0);
        UpdateCutActionStates();
    }

    private void UpdateCutActionStates()
    {
        if (_restoreSegmentButton is null) return;
        bool restoreSelected = _timeline.SelectedSegment is { } selected && _cutTimeline.RemovedRanges.Contains(selected);
        _restoreSegmentButton.IsEnabled = _mediaReady && !_operationRunning && restoreSelected;

    }

    private void SynchronizeCuts()
    {
        _cutTimeline = _editDocument.CreateTimeline();
        _timeline.SetCuts(_cutTimeline);
        _layerTimeline.SetKeptRange(_editDocument.TrimInMs, _editDocument.TrimOutMs);
        _layerTimeline.SetRemovedRanges(_cutTimeline.RemovedRanges);
        UpdateCutActionStates();
        if (_mediaReady) UpdatePositionLabel(CurrentMs());
    }

    private void ApplyCutEdit(VideoCutTimeline next)
    {
        PausePlayback();
        RememberEdit();
        _editDocument.ApplyTimelineEdit(next);
        SynchronizeCuts();
        RefreshTrimSummary();
        UpdatePositionLabel(CurrentMs());
        UpdateStatusForMode();
    }

    private void SplitHere()
    {
        if (!_mediaReady || _operationRunning) return;
        double position = FrameStepCalculator.SnapToFrame(CurrentMs(), _recording.Fps, _durationMs);
        if (!_cutTimeline.TrySplit(position, out VideoCutTimeline next)) return;
        ApplyCutEdit(next);
        _timeline.ClearSegmentSelection();
        _ = _timeline.Focus();
    }

    private void DeleteSelection()
    {
        if (!_mediaReady || _operationRunning) return;
        if (_timeline.SelectedSegment is { } selected)
        {
            if (_cutTimeline.TryRemoveSegment(selected, out VideoCutTimeline next))
            {
                ApplyCutEdit(next);
                _timeline.SelectSegmentAt(selected.StartMs);
            }
            return;
        }
        DeleteSelectedOverlay();
    }

    private void RestoreSelectedSegment()
    {
        if (!_mediaReady || _operationRunning || _timeline.SelectedSegment is not { } selected) return;
        if (!_cutTimeline.TryRestoreSegment(selected, out VideoCutTimeline next)) return;
        ApplyCutEdit(next);
        _timeline.SelectSegmentAt(selected.StartMs);
    }

    // ---- timed text notes ----

    private void RememberEdit(VideoEditDocument? before = null)
    {
        _draftSession?.Changed();
        _undo.Add(before ?? _editDocument.Clone());
        if (_undo.Count > 50) { _undo.RemoveAt(0); }
        _redo.Clear();
    }

    private void BeginLayerInteraction()
    {
        PausePlayback();
        _interactionBefore = _editDocument.Clone();
    }

    private void CompleteLayerInteraction()
    {
        if (_interactionBefore is { } before && !DocumentsEquivalent(before, _editDocument)) { RememberEdit(before); }
        _interactionBefore = null;
    }

    private void OnTrimChanged()
    {
        _draftSession?.Changed();
        if (_syncingTrim) return;
        if (!HasKeptContentInTrimRange(_timeline.InMs, _timeline.OutMs))
        {
            // Recovery's all-cut fallback is for malformed files, never an implicit
            // restoration of a user's deleted content during a handle/keyboard edit.
            SynchronizeTrim();
            UpdateStatusForMode();
            return;
        }
        bool changed = _editDocument.TrimInMs != _timeline.InMs || _editDocument.TrimOutMs != _timeline.OutMs;
        if (changed && _trimInteractionBefore is null) RememberEdit();
        _editDocument.TrimInMs = _timeline.InMs;
        _editDocument.TrimOutMs = _timeline.OutMs;
        SynchronizeCuts();
        RefreshTrimInputs();
        UpdateStatusForMode();
        UpdateTrimPreviewBadge();
    }

    private bool HasKeptContentInTrimRange(double start, double end)
    {
        if (_editDocument.RemovedRanges.Count == 0) return true;
        VideoCutTimeline source = VideoCutTimeline.Create(0, _durationMs, _editDocument.RemovedRanges);
        double minimum = Math.Min(TrimSelection.MinimumSpanMs, end - start);
        return source.KeptRanges.Any(range => Math.Min(range.EndMs, end)
            - Math.Max(range.StartMs, start) >= minimum - 1e-7);
    }

    private void CompleteTrimInteraction()
    {
        if (_trimInteractionBefore is { } before
            && (before.TrimInMs != _editDocument.TrimInMs || before.TrimOutMs != _editDocument.TrimOutMs)) RememberEdit(before);
        _trimInteractionBefore = null;
        UpdateTrimPreviewBadge();
        UpdateStatusForMode();
    }

    private void SynchronizeTrim()
    {
        _syncingTrim = true;
        try { _timeline.SetTrimRange(_editDocument.TrimInMs, _editDocument.TrimOutMs); }
        finally { _syncingTrim = false; }
        SynchronizeCuts();
        RefreshTrimInputs();
    }

    private void RefreshTrimInputs()
    {
        if (_trimInInput is null) return;
        _trimInInput.Text = VideoTimecode.Format(_timeline.InMs);
        _trimOutInput.Text = VideoTimecode.Format(_timeline.OutMs);
        bool hadError = _trimInputsInvalid;
        _trimInputsInvalid = false;
        SetTrimInputError(_trimInInput, false);
        SetTrimInputError(_trimOutInput, false);
        RefreshTrimSummary();
        if (hadError) UpdateStatusForMode();
    }

    private void ApplyTrimInputs()
    {
        // A focus change can also mean the window is closing; never touch a closed media
        // element, and do not stop playback just because the field lost focus.
        if (!_mediaReady || _operationRunning || _closing) return;
        bool validStart = TryReadTrimInput(_trimInInput.Text, _timeline.InMs, out double start)
            && start >= 0 && start <= _durationMs;
        bool validEnd = TryReadTrimInput(_trimOutInput.Text, _timeline.OutMs, out double end)
            && end >= 0 && end <= _durationMs;
        if (!validStart || !validEnd || !HasKeptContentInTrimRange(start, end) || !_timeline.SetTrimRange(start, end))
        {
            _trimInputsInvalid = true;
            // A bad value belongs to its field; an invalid pair belongs to both fields.
            SetTrimInputError(_trimInInput, !validStart || validEnd);
            SetTrimInputError(_trimOutInput, !validEnd || validStart);
            _statusLabel.Text = UiText.Get("Video.TrimInputInvalid");
            return;
        }
        RefreshTrimInputs();
    }

    private static void SetTrimInputError(TextBox input, bool invalid)
    {
        string error = invalid ? UiText.Get("Video.TrimInputInvalid") : string.Empty;
        AutomationProperties.SetItemStatus(input, error);
        AutomationProperties.SetHelpText(input, invalid ? error : UiText.Get("Video.SourceTimeHint"));
    }

    /// <summary>
    /// Reads a trim field. The fields show whole milliseconds, so an unedited field keeps the
    /// exact value it shows, and the shown clip end (rounded up by up to half a millisecond)
    /// still means the end.
    /// </summary>
    private bool TryReadTrimInput(string text, double currentMs, out double ms)
    {
        if (string.Equals(text.Trim(), VideoTimecode.Format(currentMs), StringComparison.Ordinal))
        {
            ms = currentMs;
            return true;
        }

        if (!VideoTimecode.TryParse(text, out ms))
        {
            return false;
        }

        if (ms > _durationMs && ms - _durationMs <= 0.5)
        {
            ms = _durationMs;
        }

        return true;
    }

    private void RestoreEdit(bool redo)
    {
        if (_operationRunning) return;
        List<VideoEditDocument> source = redo ? _redo : _undo;
        List<VideoEditDocument> target = redo ? _undo : _redo;
        if (source.Count == 0) { return; }
        PausePlayback();
        _timeline.ClearSegmentSelection();
        Guid? selected = SelectedLayerId();
        target.Add(_editDocument.Clone());
        _editDocument = source[^1];
        source.RemoveAt(source.Count - 1);
        SynchronizeTrim();
        _layerCanvas.SetDocument(_editDocument);
        RefreshOverlayList(selected);
        RefreshTextPreview();
        UpdateStatusForMode();
        _draftSession?.Changed();
    }

    private bool CanAddGraphic() => _mediaReady && !_operationRunning
        && _editDocument.FrameEditLayers.Count < VideoEditDocument.MaximumFrameLayerCount;

    /// <summary>Default layer length, as in the layer dialog and the asset factory.</summary>
    private const double DefaultLayerLengthMs = 3000;

    /// <summary>
    /// Where a new layer starts: at the playhead, pulled back so the layer gets its default
    /// length inside the kept range. At the very end a layer would otherwise last one frame.
    /// </summary>
    private double NewLayerStartMs() =>
        Math.Clamp(CurrentMs(), _timeline.InMs, Math.Max(_timeline.InMs, _timeline.OutMs - DefaultLayerLengthMs));

    private void AddShapeLayer(bool ellipse)
    {
        if (!CanAddGraphic()) { return; }
        AddGraphicLayer(VideoLayerAssets.CreateLayer(VideoLayerAssets.CreateShape(ellipse), ellipse ? UiText.Get("Text_C19FD6787279") : UiText.Get("Text_DC0760235344"),
            NewLayerStartMs(), _durationMs, _recording.Width, _recording.Height));
    }

    private async void AddImageLayer()
    {
        if (!CanAddGraphic()) { return; }
        var dialog = new OpenFileDialog { Title = UiText.Get("Text_21C405702C2B"), Filter = UiText.Get("Text_075F6F0A90DB"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) { return; }
        double time = NewLayerStartMs();
        var operation = new CancellationTokenSource();
        _operationCts = operation;
        CancellationToken token = operation.Token;
        SetOperationRunning(true);
        _statusLabel.Text = UiText.Get("VideoEdit_ImageLayerPreparing");
        try
        {
            FrameEditLayer layer = await StaThreadTask.RunAsync(() => VideoLayerAssets.CreateLayer(
                VideoLayerAssets.ReadImage(dialog.FileName, token), Path.GetFileName(dialog.FileName), time,
                _durationMs, _recording.Width, _recording.Height), "MyCapture image layer");
            token.ThrowIfCancellationRequested();
            AddGraphicLayer(layer);
        }
        catch (OperationCanceledException) { _statusLabel.Text = UiText.Get("Text_4AF6F7075C61"); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Image layer import failed");
            _statusLabel.Text = UiText.Get("Text_690A5972C5C2") + MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex);
        }
        finally
        {
            ReleaseOperationCts(operation);
            SetOperationRunning(false);
            if (_closeRequested) { _closeRequested = false; Close(); }
        }
    }

    private void AddGraphicLayer(FrameEditLayer layer)
    {
        if (!TryValidateLayerResources([.. _editDocument.FrameEditLayers, layer])) { return; }
        RememberEdit();
        _editDocument.FrameEditLayers.Add(layer);
        RefreshOverlayList(layer.Id);
        RefreshTextPreview();
        Seek(layer.StartMs);
        _statusLabel.Text = UiText.Get("Text_35E85A6E99A5");
    }

    private bool TryValidateLayerResources(IReadOnlyList<FrameEditLayer> layers)
    {
        try { VideoLayerResourceBudget.Validate(layers); return true; }
        catch (VideoLayerLimitException ex)
        {
            _statusLabel.Text = MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex);
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return false;
        }
    }

    private void AddTextOverlay()
    {
        if (!_mediaReady || _operationRunning)
        {
            return;
        }

        if (_editDocument.TextOverlays.Count >= VideoEditDocument.MaximumOverlayCount)
        {
            _statusLabel.Text = UiText.Format("Text_EB9E222E4C18", VideoEditDocument.MaximumOverlayCount);
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            UpdateOverlayActionStates();
            return;
        }

        PausePlayback();
        var dialog = new TimedTextOverlayDialog(_durationMs, NewLayerStartMs(), defaultEndMs: _timeline.OutMs) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } overlay)
        {
            RememberEdit();
            _editDocument.TextOverlays.Add(overlay);
            RefreshOverlayList(overlay.Id);
            RefreshTextPreview();
            Seek(overlay.StartMs);
            _statusLabel.Text = UiText.Format("Text_4004AC930045", FormatMs(overlay.StartMs), FormatMs(overlay.EndMs));
        }
    }

    private void EditSelectedOverlay()
    {
        if (_mediaReady && !_operationRunning && SelectedFrameLayer() is { } frame)
        {
            PausePlayback();
            var frameDialog = new TimedTextOverlayDialog(_durationMs, frame.StartMs, existingFrame: frame) { Owner = this };
            if (frameDialog.ShowDialog() == true && frameDialog.FrameResult is { } editedFrame)
            {
                int frameIndex = _editDocument.FrameEditLayers.FindIndex(item => item.Id == frame.Id);
                if (frameIndex < 0) return;
                RememberEdit();
                _editDocument.FrameEditLayers[frameIndex] = editedFrame;
                _layerCanvas.SetDocument(_editDocument);
                RefreshOverlayList(editedFrame.Id);
                RefreshTextPreview();
                Seek(editedFrame.StartMs);
                _statusLabel.Text = UiText.Format("Text_E77D23C3D35B", FormatMs(editedFrame.StartMs), FormatMs(editedFrame.EndMs));
            }
            return;
        }
        if (!_mediaReady
            || _operationRunning
            || SelectedOverlay() is not { } selected)
        {
            return;
        }

        PausePlayback();
        var dialog = new TimedTextOverlayDialog(_durationMs, selected.StartMs, selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
        {
            return;
        }

        int index = _editDocument.TextOverlays.FindIndex(item => item.Id == selected.Id);
        if (index >= 0)
        {
            RememberEdit();
            edited.Bounds = edited.Placement == selected.Placement ? selected.Bounds : null;
            _editDocument.TextOverlays[index] = edited;
        }

        RefreshOverlayList(edited.Id);
        RefreshTextPreview();
        Seek(edited.StartMs);
        _statusLabel.Text = UiText.Format("Text_E77D23C3D35B", FormatMs(edited.StartMs), FormatMs(edited.EndMs));
    }

    private void DeleteSelectedOverlay()
    {
        if (_operationRunning)
        {
            return;
        }

        if (SelectedOverlay() is { } selected)
        {
            RememberEdit();
            _editDocument.TextOverlays.RemoveAll(item => item.Id == selected.Id);
            _statusLabel.Text = UiText.Get("Text_6474F56C528B");
        }
        else if (SelectedFrameLayer() is { } frameLayer)
        {
            RememberEdit();
            _editDocument.FrameEditLayers.RemoveAll(item => item.Id == frameLayer.Id);
            _statusLabel.Text = UiText.Get("Text_D7231656D075");
        }
        else
        {
            return;
        }

        RefreshOverlayList();
        RefreshTextPreview();
    }

    private TimedTextOverlay? SelectedOverlay() =>
        (_overlayList.SelectedItem as ListBoxItem)?.Tag as TimedTextOverlay;

    private FrameEditLayer? SelectedFrameLayer() =>
        (_overlayList.SelectedItem as ListBoxItem)?.Tag as FrameEditLayer;

    private Guid? SelectedLayerId() =>
        SelectedOverlay()?.Id ?? SelectedFrameLayer()?.Id;

    private void OnLayerTextTimingChanged(object? sender, EventArgs e) =>
        OnLayerTimingChanged(sender, e);

    private void OnLayerTimingChanged(object? sender, EventArgs e)
    {
        _draftSession?.Changed();
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _overlayPreview.SetFrameLayers(_editDocument.FrameEditLayers);
        _overlayPreview.SetSourceTime(CurrentMs());
        _layerCanvas.SetSourceTime(CurrentMs());

        Guid? selectedId = _layerTimeline.SelectedLayerId;
        if (_editDocument.TextOverlays.FirstOrDefault(item => item.Id == selectedId) is { } overlay)
        {
            _statusLabel.Text = UiText.Format("Text_380199144757", FormatMs(overlay.StartMs), FormatMs(overlay.EndMs));
        }
        else if (_editDocument.FrameEditLayers.FirstOrDefault(item => item.Id == selectedId) is { } frameLayer)
        {
            _statusLabel.Text = UiText.Format("Text_3DF1B247086E", FormatMs(frameLayer.StartMs), FormatMs(frameLayer.EndMs));
        }
    }

    private void OnLayerTimingInteractionCompleted(object? sender, EventArgs e)
    {
        CompleteLayerInteraction();
        if (!_mediaReady)
        {
            return;
        }

        RefreshOverlayList(_layerTimeline.SelectedLayerId);
        RefreshTextPreview();

        Guid? selectedId = _layerTimeline.SelectedLayerId;
        double? seekTarget = SelectedOverlay()?.StartMs
            ?? SelectedFrameLayer()?.StartMs
            ?? _editDocument.TextOverlays.FirstOrDefault(o => o.Id == selectedId)?.StartMs
            ?? _editDocument.FrameEditLayers.FirstOrDefault(f => f.Id == selectedId)?.StartMs;

        if (seekTarget.HasValue)
        {
            // Move the playhead with the preview, so frame steps continue from what is shown.
            Seek(seekTarget.Value);
        }
    }

    private void OnOverlaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingOverlayList)
        {
            return;
        }

        _layerTimeline.SelectLayer(SelectedLayerId());
        _layerCanvas.Select(SelectedLayerId());
        if (SelectedLayerId() is not null) _timeline.ClearSegmentSelection();
        UpdateOverlayActionStates();
        if (!_mediaReady)
        {
            return;
        }

        if (_layerTimeline.IsDragging)
        {
            return;
        }

        if (SelectedOverlay() is { } selected)
        {
            Seek(selected.StartMs);
        }
        else if (SelectedFrameLayer() is { } frameLayer)
        {
            Seek(frameLayer.StartMs);
        }
    }

    private FrameworkElement LayerCaption(string title, double start, double end, string icon)
    {
        var row = new DockPanel { LastChildFill = true };
        var symbol = new SymbolIcon
        {
            Size = 16,
            Data = TryGeometry(icon), Margin = new Thickness(0, 0, 10, 0),
            Stroke = TryBrush("Text.Secondary", Colors.LightGray),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(symbol, Dock.Left); row.Children.Add(symbol);
        var caption = new StackPanel();
        caption.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
        // The visible interval on one line: the times read as a range without start and end rows.
        var timing = new TextBlock
        {
            Text = UiText.Format("VideoEditorUx_LayerInterval", FormatMs(start), FormatMs(end)),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        timing.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        caption.Children.Add(timing); row.Children.Add(caption);
        return row;
    }

    private void RefreshOverlayList(Guid? selectedId = null)
    {
        _updatingOverlayList = true;
        try
        {
            Guid? keep = selectedId ?? SelectedLayerId();
            _overlayList.Items.Clear();
            foreach (TimedTextOverlay overlay in _editDocument.TextOverlays.OrderBy(item => item.StartMs))
            {
                string oneLine = overlay.Text.Replace('\r', ' ').Replace('\n', ' ');
                if (oneLine.Length > 38)
                {
                    oneLine = oneLine[..38] + "…";
                }

                var item = new ListBoxItem
                {
                    Content = LayerCaption(oneLine, overlay.StartMs, overlay.EndMs, "Icon.Text"),
                    Tag = overlay,
                    ToolTip = overlay.Text,
                    Padding = new Thickness(8, 4, 8, 4),
                };
                AutomationProperties.SetName(item, UiText.Format("Text_0669BC5BEA50", FormatMs(overlay.StartMs), FormatMs(overlay.EndMs), oneLine));
                _overlayList.Items.Add(item);
                if (keep == overlay.Id)
                {
                    _overlayList.SelectedItem = item;
                }
            }

            foreach (FrameEditLayer layer in _editDocument.FrameEditLayers.OrderBy(item => item.StartMs))
            {
                var item = new ListBoxItem
                {
                    Content = LayerCaption(layer.Name, layer.StartMs, layer.EndMs, "Icon.Image"),
                    Tag = layer,
                    ToolTip = UiText.Get("Text_E75BE1B916FD"),
                    Padding = new Thickness(8, 4, 8, 4),
                };
                AutomationProperties.SetName(
                    item,
                    UiText.Format("Text_4B622E994DF4", FormatMs(layer.StartMs), FormatMs(layer.EndMs), layer.Name));
                _overlayList.Items.Add(item);
                if (keep == layer.Id)
                {
                    _overlayList.SelectedItem = item;
                }
            }

            AutomationProperties.SetHelpText(
                _overlayList,
                UiText.Format("Text_26FF7226E337", _editDocument.TextOverlays.Count, _editDocument.FrameEditLayers.Count));
            _layerTimeline.SetLayers(_editDocument.TextOverlays, _editDocument.FrameEditLayers);
            _layerTimeline.SelectLayer(SelectedLayerId());
            _layerCanvas.Select(SelectedLayerId());
            if (SelectedLayerId() is not null) _timeline.ClearSegmentSelection();
            // The text track remains an editing entry point, including an empty document.
            UpdateOverlayActionStates();
        }
        finally
        {
            _updatingOverlayList = false;
        }
    }

    private void UpdateOverlayActionStates()
    {
        if (_addTextButton is null || _editTextButton is null || _deleteTextButton is null)
        {
            return;
        }

        bool interactive = _mediaReady && !_operationRunning;
        _addTextButton.IsEnabled = interactive
            && _editDocument.TextOverlays.Count < VideoEditDocument.MaximumOverlayCount;
        bool textSelected = SelectedOverlay() is not null;
        bool anySelected = textSelected || SelectedFrameLayer() is not null;
        _editTextButton.IsEnabled = interactive && anySelected;
        _deleteTextButton.IsEnabled = interactive && anySelected;
    }

    private void RefreshTextPreview()
    {
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _overlayPreview.SetFrameLayers(_editDocument.FrameEditLayers);
        _overlayPreview.SetSourceTime(CurrentMs());
        _layerCanvas.SetSourceTime(CurrentMs());
        _layerTimeline.SetLayers(_editDocument.TextOverlays, _editDocument.FrameEditLayers);
    }

    private VideoEditDocument BuildCurrentDocument()
    {
        VideoEditDocument current = _editDocument.Clone();
        current.SourceDurationMs = _durationMs;
        current.CanvasWidth = _recording.Width;
        current.CanvasHeight = _recording.Height;
        current.TrimInMs = _timeline.InMs;
        current.TrimOutMs = _timeline.OutMs;
        return current.NormalizeFor(_recording.Width, _recording.Height, _durationMs);
    }

    // ---- extract frame -> annotation editor ----

    private async void EditCurrentFrame()
    {
        if (!_mediaReady || _extractingFrame)
        {
            return;
        }

        if (_editDocument.FrameEditLayers.Count >= VideoEditDocument.MaximumFrameLayerCount)
        {
            _statusLabel.Text = UiText.Format("Text_576701BE51C1", VideoEditDocument.MaximumFrameLayerCount);
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return;
        }

        double layerTimeMs = CurrentMs();
        VideoEditDocument frameDocument = _editDocument.Clone();
        BitmapSource? frame;
        _extractingFrame = true;

        try
        {
            _statusLabel.Text = UiText.Get("Text_571C8E331C15");
            await _previewSeeks.RequestExactAsync(layerTimeMs);
            if (!IsLoaded) return;
            frame = await TryDecodeFrameForEditAsync(layerTimeMs, frameDocument);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Exact seek before frame edit failed");
            _statusLabel.Text = UiText.Get("Text_B0B5252B194F") + MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex);
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return;
        }
        finally
        {
            _extractingFrame = false;
        }

        if (!IsLoaded)
        {
            return;
        }

        if (frame is null)
        {
            _statusLabel.Text = UiText.Get("Text_33F7635FDE36");
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return;
        }

        AnnotationEditorWindow editor = CreateFrameEditor(frame, layerTimeMs, frameDocument);
        editor.Owner = this;
        editor.Show();
        _ = editor.Activate();
    }

    internal AnnotationEditorWindow CreateFrameEditor(BitmapSource frame, double layerTimeMs, VideoEditDocument frameDocument)
    {
        // Timed text is intentionally absent while annotating. It is added only to still
        // commits, after rotation/crop/annotations, and remains live above the video layer.
        var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
        var frozen = new FrozenFrame(frame, region, null, 0);

        var editor = new AnnotationEditorWindow(
            frozen,
            region,
            frame,
            title: UiText.Get("Text_2A0C70A93334"),
            privacyRedactionService: PrivacyRedactionService)
        {
            FrameStillImage = VideoFrameStillImage.Capture(frameDocument, layerTimeMs),
        };
        FrameImageCommitSession? commitSession = FrameImageCommitHandlerFactory?.Invoke();
        editor.CommitRequested = commitSession?.CommitAsync ?? (_ => Task.FromResult(false));
        editor.Committed += (_, result) =>
        {
            FrameImageCaptured?.Invoke(this, new AnnotationFrameCapturedEventArgs(result));
            AddFrameEditLayer(result, layerTimeMs);
        };
        editor.Closed += (_, _) => commitSession?.Dispose();
        return editor;
    }

    private void AddFrameEditLayer(AnnotationEditingResult result, double sourceTimeMs)
    {
        try
        {
            if (result.Document.Items.Count == 0 && result.RotationTurns == 0)
            {
                _statusLabel.Text = UiText.Get("Text_1AEC9153DD98");
                return;
            }

            int width = _recording.Width;
            int height = _recording.Height;
            var visual = new DrawingVisual();
            var renderer = new AnnotationRenderer(
                AnnotationImageStore.FromDecoded(result.ImageAssetBitmaps));
            // An unrotated document still lives in the frame's space: a crop only shifted
            // it, so annotations map back with the crop origin and the overlay stays
            // transparent. A rotated document has no transparent mapping onto the frame,
            // so the committed image — rotated base pixels included — is flattened and
            // letterboxed onto the frame (a 180° turn keeps the canvas dimensions, which
            // is why the turn count, not the size, decides). Keep the entire PNG opaque:
            // earlier frame layers were visible in the edit input and are already rotated
            // into this image; black letterboxing prevents them showing through a second time.
            if (result.RotationTurns != 0)
            {
                int canvasWidth = Math.Max(1, result.Document.CanvasWidth);
                int canvasHeight = Math.Max(1, result.Document.CanvasHeight);
                double scale = Math.Min((double)width / canvasWidth, (double)height / canvasHeight);
                double drawWidth = canvasWidth * scale;
                double drawHeight = canvasHeight * scale;
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                    dc.PushTransform(new TranslateTransform((width - drawWidth) / 2, (height - drawHeight) / 2));
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawImage(result.SelectedBitmap, new Rect(0, 0, canvasWidth, canvasHeight));
                    renderer.Render(dc, result.Document, pixelsPerDip: 1.0, blurSource: result.SelectedBitmap);
                    dc.Pop();
                    dc.Pop();
                }
            }
            else
            {
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(result.CropOrigin.X, result.CropOrigin.Y));
                    // Blur layers bake the edited frame's pixels into this overlay, like the
                    // mosaic patches already do.
                    renderer.Render(dc, result.Document, pixelsPerDip: 1.0, blurSource: result.SelectedBitmap);
                    dc.Pop();
                }
            }

            var transparent = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            transparent.Render(visual);
            transparent.Freeze();
            string encoded = Convert.ToBase64String(ImageCodec.EncodePng(transparent));

            double frameMs = FrameStepCalculator.FrameDurationMs(_recording.Fps);
            double start = FrameStepCalculator.SnapToFrame(
                Math.Clamp(sourceTimeMs, 0, Math.Max(0, _durationMs - frameMs)),
                _recording.Fps,
                _durationMs);
            double end = Math.Min(_durationMs, start + frameMs);
            if (end <= start)
            {
                end = Math.Min(_durationMs, start + 1);
            }

            var layer = new FrameEditLayer
            {
                StartMs = start,
                EndMs = end,
                Name = UiText.Format("Text_4C27BFC6C0E5", FormatMs(start)),
                OverlayPngBase64 = encoded,
            };
            if (!TryValidateLayerResources([.. _editDocument.FrameEditLayers, layer])) { return; }
            RememberEdit();
            _editDocument.FrameEditLayers.Add(layer);
            RefreshOverlayList(layer.Id);
            RefreshTextPreview();
            Seek(start);
            _statusLabel.Text = UiText.Format("Text_64A5A9D59A21", FormatMs(start));
            _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Creating a non-destructive frame layer failed");
            _statusLabel.Text = UiText.Get("Text_E3B50B06C736") + MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex);
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }
    }

    private BitmapSource? TryRenderPreviewForTest()
    {
        try
        {
            int w = _recording.Width;
            int h = _recording.Height;
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // This visual-only hook tests the actual preview. Persisted frames must use
                // DecodeFrameForEditAsync, never the presenter or its correction effect.
                var brush = new VisualBrush(_media) { Stretch = Stretch.Fill };
                dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
                FrameEditLayerRenderer.Draw(
                    dc,
                    _editDocument.FrameEditLayers,
                    FrameEditLayerRenderer.Decode(_editDocument.FrameEditLayers),
                    CurrentMs(),
                    w,
                    h);
                TimedTextOverlayRenderer.Draw(
                    dc,
                    _editDocument.TextOverlays,
                    CurrentMs(),
                    w,
                    h,
                    pixelsPerDip: 1.0);
            }

            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rendering the current video frame failed");
            return null;
        }
    }

    // ---- commit video / GIF ----

    private async void CommitTrim()
    {
        if (!_mediaReady || _committed || _operationRunning)
        {
            return;
        }

        if (!TryValidateLayerResources(_editDocument.FrameEditLayers)) { return; }
        VideoEditDocument document = BuildCurrentDocument();
        if (!_recoveredDraft && _initialDocument is not null && DocumentsEquivalent(_initialDocument, document))
        {
            if (!await RetireDraftBeforeCloseAsync()) return;
            _statusLabel.Text = UiText.Get("Text_2D8B0244F4F4");
            _committed = true;
            Close();
            return;
        }

        string outputPath = RenderStagingPathFactory?.Invoke() ?? BuildTrimmedPath();
        var operation = new CancellationTokenSource();
        _operationCts = operation;
        CancellationToken cancellationToken = operation.Token;
        SetOperationRunning(true);
        _saveProgress.Start(UiText.Get("VideoEditorUx_LibrarySaving"));
        _statusLabel.Text = UiText.Get("VideoEditorUx_LibrarySaving");
        var stopwatch = Stopwatch.StartNew();
        bool finalizing = false;
        var progress = new Progress<VideoFrameRenderProgress>(value =>
        {
            if (finalizing || !_operationRunning || !ReferenceEquals(_operationCts, operation) || operation.IsCancellationRequested) return;
            _saveProgress.Report(value, stopwatch.Elapsed);
        });

        try
        {
            int emitted = await StaThreadTask.RunAsync(
                () => TrimReencoder.Reencode(
                    _recording.OutputPath,
                    outputPath,
                    document.TrimInMs,
                    document.TrimOutMs,
                    _recording,
                    options => new MediaFoundationVideoEncoder(
                        options,
                        _loggerFactory.CreateLogger<MediaFoundationVideoEncoder>()),
                    _loggerFactory.CreateLogger("TrimReencoder"),
                    document.TextOverlays,
                    document.FrameEditLayers,
                    progress,
                    cancellationToken,
                    removedRanges: document.RemovedRanges),
                "MyCapture video compositor");

            finalizing = true;
            _saveProgress.FinalizeProgress();
            if (VideoCommitHandler is not null)
            {
                // The encode reached 100%, but validation, thumbnail and the staged file
                // swap still run; labelling the tail keeps the editor from looking frozen.
                _statusLabel.Text = UiText.Get("VideoEdit_Finalizing");
                await VideoCommitHandler(document, outputPath, cancellationToken);
            }

            if (!await RetireDraftBeforeCloseAsync()) return;
            _editDocument = document;
            _statusLabel.Text = UiText.Format("Text_0DA5C524F6BB", emitted);
            _committed = true;
            VideoCommitted?.Invoke(this, EventArgs.Empty);
            Close();
        }
        catch (OperationCanceledException)
        {
            DeletePrivateRenderStage(outputPath);
            _statusLabel.Text = UiText.Get("Text_9F05CC6FEDE5");
        }
        catch (Exception ex)
        {
            DeletePrivateRenderStage(outputPath);
            _log.LogError(ex, "Video re-render failed");
            _statusLabel.Text = UiText.Get("Text_2DC82774E99B") + MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex);
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }
        finally
        {
            bool closeAfterCancellation = _closeRequested;
            ReleaseOperationCts(operation);
            if (IsLoaded && !_committed)
            {
                SetOperationRunning(false);
                if (closeAfterCancellation)
                {
                    _closeRequested = false;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
                }
            }
        }
    }

    private string BuildTrimmedPath()
    {
        string dir = Path.GetDirectoryName(_recording.OutputPath) ?? _paths.CapturesRoot;
        string stem = Path.GetFileNameWithoutExtension(_recording.OutputPath);
        return Path.Combine(dir, stem + "_edited.mp4");
    }

    private void ExportGif() => OpenExport(gif: true);

    private VideoExportDialog? _exportDialog;

    /// <summary>
    /// Shows the export dialog instead of the modal <see cref="Window.ShowDialog"/>. Null in the
    /// product, so the dialog always opens modally; the tutorial video generator sets it to host
    /// the dialog offscreen, without activating it, while it records frames.
    /// </summary>
    internal Action<VideoExportDialog>? ExportDialogHostForTest { get; set; }

    private void OpenExport(bool gif = false)
    {
        if (_exportDialog is not null || !IsVisible || !_mediaReady || _operationRunning
            || !TryValidateLayerResources(_editDocument.FrameEditLayers)) return;
        PausePlayback();
        try
        {
            _exportDialog = new VideoExportDialog(_recording, BuildCurrentDocument(), _loggerFactory, gif) { Owner = this };
            if (ExportDialogHostForTest is { } host) host(_exportDialog);
            else _ = _exportDialog.ShowDialog();
        }
        catch (Exception error)
        {
            _log.LogError(error, "Could not open video export");
            _statusLabel.Text = UiText.Format("MediaExport_Error", error.Message);
        }
        finally { _exportDialog = null; }
    }
    private void SetOperationRunning(bool running)
    {
        bool starting = running && !_operationRunning;
        if (running) { _resumeAfterScrub = false; PausePlayback(); }
        _operationRunning = running;
        SetEditControlsEnabled(!running && _mediaReady);
        _overlayList.IsEnabled = !running && _mediaReady;
        _layerTimeline.IsEnabled = !running && _mediaReady;
        _layerCanvas.IsEnabled = !running && _mediaReady;
        if (starting) _saveProgress.Start(UiText.Get("VideoEdit_ImageLayerPreparing"), indeterminate: true);
        else if (!running) _saveProgress.Stop();
    }

    internal static bool DocumentsEquivalent(VideoEditDocument left, VideoEditDocument right)
    {
        const double tolerance = 0.001;
        if (Math.Abs(left.TrimInMs - right.TrimInMs) > tolerance
            || Math.Abs(left.TrimOutMs - right.TrimOutMs) > tolerance
            || !left.RemovedRanges.SequenceEqual(right.RemovedRanges)
            || !left.SplitPoints.SequenceEqual(right.SplitPoints)
            || left.TextOverlays.Count != right.TextOverlays.Count
            || left.FrameEditLayers.Count != right.FrameEditLayers.Count)
        {
            return false;
        }

        for (int index = 0; index < left.TextOverlays.Count; index++)
        {
            TimedTextOverlay a = left.TextOverlays[index];
            TimedTextOverlay b = right.TextOverlays[index];
            if (a.Id != b.Id
                || !string.Equals(a.Text, b.Text, StringComparison.Ordinal)
                || Math.Abs(a.StartMs - b.StartMs) > tolerance
                || Math.Abs(a.EndMs - b.EndMs) > tolerance
                || a.Placement != b.Placement
                || a.Bounds != b.Bounds)
            {
                return false;
            }
        }

        for (int index = 0; index < left.FrameEditLayers.Count; index++)
        {
            FrameEditLayer a = left.FrameEditLayers[index];
            FrameEditLayer b = right.FrameEditLayers[index];
            if (a.Id != b.Id
                || a.Bounds != b.Bounds
                || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || !string.Equals(a.OverlayPngBase64, b.OverlayPngBase64, StringComparison.Ordinal)
                || Math.Abs(a.StartMs - b.StartMs) > tolerance
                || Math.Abs(a.EndMs - b.EndMs) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private void DeletePrivateRenderStage(string path)
    {
        if (RenderStagingPathFactory is null)
        {
            return;
        }

        try
        {
            // Only the library-owned render stage for this recording may be removed.
            // A factory being present does not make its returned path safe to delete.
            string fullPath = ValidatedFilePaths.FileWithinRoot(_paths.CapturesRoot, path, ".mp4");
            string name = Path.GetFileName(fullPath);
            if (!name.StartsWith(".render-", StringComparison.Ordinal)
                || !Guid.TryParseExact(name[8..^4], "N", out _)
                || !string.Equals(Path.GetDirectoryName(fullPath),
                    Path.GetDirectoryName(Path.GetFullPath(_recording.OutputPath)), StringComparison.Ordinal))
            {
                return;
            }

            if (File.Exists(fullPath))
            {
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(fullPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    // ---- keyboard ----

    private void BuildShortcutActions()
    {
        _shortcutActions["Play"] = TogglePlay;
        _shortcutActions["In"] = SetInHere; _shortcutActions["Out"] = SetOutHere;
        _shortcutActions["Trim"] = SelectTrimHandle;
        _shortcutActions["Split"] = SplitHere;
        _shortcutActions["FrameEdit"] = EditCurrentFrame;
        _shortcutActions["First"] = () => Seek(_timeline.InMs);
        _shortcutActions["Last"] = () => Seek(_timeline.OutMs);
        _shortcutActions["Previous"] = () => StepCoarse(-1);
        _shortcutActions["Next"] = () => StepCoarse(1);
        _shortcutActions["PreviousFrame"] = () => StepFrames(-1);
        _shortcutActions["NextFrame"] = () => StepFrames(1);
        _shortcutActions["ZoomIn"] = () => { ShowDetailTimeline(); _timeline.ZoomAroundPlayhead(0.8); };
        _shortcutActions["ZoomOut"] = () => { ShowDetailTimeline(); _timeline.ZoomAroundPlayhead(1.25); };
        _shortcutActions["Undo"] = () => RestoreEdit(false);
        _shortcutActions["Redo"] = () => RestoreEdit(true);
        _shortcutActions["Gif"] = ExportGif;
        _shortcutActions["AddText"] = AddTextOverlay;
        _shortcutActions["EditLayer"] = () => { if (_overlayList.SelectedItem is not null) EditSelectedOverlay(); };
        _shortcutActions["DeleteLayer"] = DeleteSelection;
        _shortcutActions["Cancel"] = () => { try { _operationCts?.Cancel(); } catch (ObjectDisposedException) { } };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_mediaReady || Keyboard.FocusedElement is TextBoxBase
            || (e.Key == Key.Space && FocusedControlOwnsSpace())) return;
        // Hangul mode reports I, O, E and G as ImeProcessed; outside a text box they are shortcuts.
        Key key = ShortcutInput.Resolve(e.Key, e.ImeProcessedKey,
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase);
        e.Handled = HandleShortcut(key, Keyboard.Modifiers);
        _hangulRelay.NoteKeyDown(e.Key, key, e.Handled);
    }

    /// <summary>
    /// Runs Hangul-mode shortcut letters (I, O, E, G) in the preview stage.
    /// </summary>
    /// <remarks>
    /// WPF hands an unhandled <see cref="Key.ImeProcessed"/> PreviewKeyDown to the text
    /// services, which mark it handled, so the bubbling KeyDown above never sees the letter
    /// while a Korean IME is in Hangul mode. Handling it here keeps it from the IME as well.
    /// </remarks>
    /// <summary>
    /// Keyboard navigation gives controls their usual Space action. A pointer-clicked ordinary
    /// button still leaves Space to playback, so "start here" is not repeated accidentally.
    /// </summary>
    private void OnPreviewSpaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) _keyboardNavigation = true;
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || !_mediaReady
            || FocusedControlOwnsSpace())
        {
            return;
        }

        e.Handled = HandleShortcut(Key.Space, ModifierKeys.None);
    }

    private void OnPreviewPointerDown(object? sender, MouseButtonEventArgs e) => _keyboardNavigation = false;

    private bool FocusedControlOwnsSpace()
    {
        DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
        while (focused is not null && !ReferenceEquals(focused, this))
        {
            if (focused is TextBoxBase or PasswordBox or Selector or ComboBoxItem or RangeBase)
                return true;
            // Toggles and check boxes are buttons too: a clicked one must not repeat itself on Space.
            if (focused is ButtonBase) return _keyboardNavigation;
            focused = focused is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(focused) : LogicalTreeHelper.GetParent(focused);
        }
        return false;
    }

    private void OnPreviewImeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.ImeProcessed || !_mediaReady || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        Key key = ShortcutInput.Resolve(e.Key, e.ImeProcessedKey, textInputFocused: false);
        e.Handled = HandleShortcut(key, Keyboard.Modifiers);
        _hangulRelay.NoteKeyDown(e.Key, key, e.Handled);
    }

    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        string? command = ShortcutCatalog.Match(ShortcutScope.Video, key, modifiers);
        if (command is null || (command == "Cancel" && !_operationRunning)) return false;
        if (_operationRunning && command != "Cancel")
            return command is "Play" or "Previous" or "Next" or "PreviousFrame" or "NextFrame";
        return ShortcutCatalog.Execute(ShortcutScope.Video, key, modifiers, _shortcutActions);
    }

    private void UpdateStatusForMode()
    {
        // History buttons mirror the stack so they disable when there is nothing to undo.
        if (_undoButton is not null) _undoButton.IsEnabled = _undo.Count > 0;
        if (_redoButton is not null) _redoButton.IsEnabled = _redo.Count > 0;
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
        if (_trimInputsInvalid)
        {
            _statusLabel.Text = UiText.Get("Video.TrimInputInvalid");
            return;
        }
        string kept = TrimPresentation.Clock(_cutTimeline.OutputDurationMs);
        if (_timeline.IsTrimDragging)
        {
            // While a handle moves, the status names the end being adjusted and the result.
            _statusLabel.Text = _timeline.IsEndHandleActive
                ? UiText.Format("VideoEditorUx_TrimDraggingEnd", TrimPresentation.Clock(_timeline.OutMs), kept)
                : UiText.Format("VideoEditorUx_TrimDraggingStart", TrimPresentation.Clock(_timeline.InMs), kept);
            return;
        }

        _statusLabel.Text = _recording.DroppedFrames > 0 ? RecordingHealth() : string.Empty;

    }

    private void UpdatePositionLabel(double positionMs)
    {
        if (_cutTimeline.RemovedRanges.Count > 0)
        {
            double outputMs = _cutTimeline.ToOutputTime(positionMs);
            double durationMs = _cutTimeline.OutputDurationMs;
            int outputTotal = FrameStepCalculator.LastFrameIndex(_recording.Fps, durationMs) + 1;
            int outputFrame = Math.Min(outputTotal, FrameStepCalculator.FrameIndexAt(outputMs, _recording.Fps, durationMs) + 1);
            _positionLabel.Text = UiText.Format("VideoMulticut_OutputPosition", FormatMs(outputMs), FormatMs(durationMs));
            _positionFrameLabel.Text = UiText.Format("VideoEditorUx_PositionFrame", outputFrame, outputTotal);
            AutomationProperties.SetName(_positionChip, _positionLabel.Text + ", " + _positionFrameLabel.Text);
            return;
        }
        int total = TotalFrameCount();
        int frame = Math.Min(total, FrameStepCalculator.FrameIndexAt(positionMs, _recording.Fps, _durationMs) + 1);
        _positionLabel.Text = UiText.Format("VideoEditorUx_PositionTime", FormatMs(positionMs), FormatMs(_durationMs));
        _positionFrameLabel.Text = UiText.Format("VideoEditorUx_PositionFrame", frame, total);
        AutomationProperties.SetName(_positionChip, UiText.Format("Text_D8A09D5BCDCB", FormatMs(positionMs), FormatMs(_durationMs), frame, total));
    }

    private int TotalFrameCount() => FrameStepCalculator.LastFrameIndex(_recording.Fps, _durationMs) + 1;

    private static string FormatMs(double ms) => VideoTimecode.Format(ms);

    private void OnClosingInternal(object? sender, CancelEventArgs e)
    {
        bool userClose = _userRequestedClose;
        _userRequestedClose = false;
        if (_discardCloseTask is { IsCompleted: false } && !_discardedDraft)
        {
            e.Cancel = true;
            return;
        }
        if (userClose && !_operationRunning && HasUnsavedEdits())
        {
            // Closing from the title bar with edits: save to the library, drop them, or stay.
            EditorCloseChoice choice = AskUnsavedEdits();
            if (choice == EditorCloseChoice.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (choice == EditorCloseChoice.SaveAndClose)
            {
                // CommitTrim closes the window itself once the edit is saved.
                e.Cancel = true;
                CommitTrim();
                return;
            }
            if (_draftSession is null)
            {
                _discardedDraft = true;
                return;
            }
            e.Cancel = true;
            _discardCloseTask = DiscardAndCloseAsync();
            return;
        }

        if (!_operationRunning || _committed)
        {
            return;
        }

        e.Cancel = true;
        _closeRequested = true;
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _statusLabel.Text = UiText.Get("Text_CC2687CDFC63");
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
    }

    private void ReleaseOperationCts(CancellationTokenSource owned)
    {
        CancellationTokenSource? current = Interlocked.CompareExchange(ref _operationCts, null, owned);
        if (ReferenceEquals(current, owned))
        {
            owned.Dispose();
        }
    }

    private void OnClosedInternal(object? sender, EventArgs e)
    {
        FinishDraftRecovery();
        _closing = true;
        CancellationTokenSource? operation = Interlocked.Exchange(ref _operationCts, null);
        try
        {
            operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        operation?.Dispose();
        _playbackTimer.Stop();
        _playbackTimer.Tick -= OnPlaybackTick;
        _overlayList.SelectionChanged -= OnOverlaySelectionChanged;
        StopLoadTimers();
        _timeline.PlayheadChanged -= OnTimelinePlayhead;
        _timeline.PlayheadInteractionCompleted -= OnTimelinePlayheadInteractionCompleted;
        _layerTimeline.LayerTimingChanged -= OnLayerTimingChanged;
        _layerTimeline.LayerTimingInteractionCompleted -= OnLayerTimingInteractionCompleted;
        StudioRangeVideoEffect.Clear(_media);
        _previewSeeks.PositionRequested -= OnPreviewPositionRequested;
        _previewSeeks.PreviewPresented -= OnPreviewPresented;
        _previewSeeks.SeekFailed -= OnPreviewSeekFailed;
        _previewSeeks.Dispose();
        _previewEngine.Dispose();
        _timeline.Dispose();
        KeyDown -= OnKeyDown;
        PreviewKeyDown -= OnPreviewImeKeyDown;
        PreviewKeyDown -= OnPreviewSpaceKeyDown;
        PreviewMouseDown -= OnPreviewPointerDown;
        _hwndSource?.RemoveHook(OnWindowMessage);
        _hwndSource = null;
        Closing -= OnClosingInternal;
        Loaded -= OnLoadedInternal;
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosedInternal;
        _media.MediaOpened -= OnMediaOpened;
        _media.MediaFailed -= OnMediaFailed;
        _media.MediaEnded -= OnMediaEnded;
        try
        {
            _media.Close();
        }
        catch (InvalidOperationException)
        {
            // Closing an already-released media element is harmless.
        }
    }

    // ---- small view helpers (kept local so the editor matches the 0.4.0 token system) ----

    private Button MakeButton(object content, string automationName, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = automationName,
        };
        // A resource reference, so the tier also applies when the theme arrives after the
        // window is built (an isolated window or a test that loads the theme afterwards).
        button.SetResourceReference(StyleProperty, styleKey);
        CommandButtonPresentation.Apply(button);
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => onClick();
        _editControls.Add(button);
        return button;
    }

    private Button MakeIconButton(
        string iconKey,
        string label,
        string automationName,
        string styleKey,
        Action onClick) =>
        MakeButton(BuildIconLabel(iconKey, label), automationName, styleKey, onClick);

    private Button MakeCompactIconButton(
        string iconKey,
        string label,
        string automationName,
        string styleKey,
        Action onClick)
    {
        Button button = MakeIconButton(iconKey, label, automationName, styleKey, onClick);
        CommandButtonPresentation.Apply(button, compact: true);
        CommandButtonPresentation.ReserveHint(button);
        return button;
    }

    private Button MakeTransportIcon(string iconKey, string automationName, string styleKey, Action onClick, double glyphSize = CommandButtonPresentation.IconSize)
    {
        string? command = iconKey switch
        {
            "Icon.Undo" => "Undo", "Icon.Redo" => "Redo", "Icon.Edit" => "EditLayer", "Icon.Delete" => "DeleteLayer",
            "Icon.First" => "First", "Icon.Rewind" => "Previous", "Icon.Play" => "Play", "Icon.FastForward" => "Next",
            "Icon.Last" => "Last", "Icon.StepBack" => "PreviousFrame", "Icon.StepForward" => "NextFrame",
            "Icon.ZoomIn" => "ZoomIn", "Icon.ZoomOut" => "ZoomOut", _ => null,
        };
        string label = command switch
        {
            "EditLayer" => UiText.Get("Text_87B0ACEF85A7"),
            "DeleteLayer" => UiText.Get("Text_6139B6C3ED73"),
            "PreviousFrame" => UiText.Get("VideoEditorUx_PreviousFrameShort"),
            "NextFrame" => UiText.Get("VideoEditorUx_NextFrameShort"),
            _ => string.Empty,
        };
        Button button = MakeButton(BuildIconLabel(iconKey, label, glyphSize), automationName, styleKey,
            command is null ? onClick : _shortcutActions[command]);
        if (command is not null)
        {
            ShortcutPresentation.Attach(button, ShortcutScope.Video, command);
        }
        else CommandButtonPresentation.ReserveHint(button);

        return button;
    }

    private static StackPanel BuildIconLabel(string iconKey, string label, double glyphSize = CommandButtonPresentation.IconSize)
    {
        var glyph = new SymbolIcon
        {
            Size = glyphSize,
            Data = TryGeometry(iconKey),
            Fill = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(SymbolIcon.DataProperty, iconKey);
        glyph.SetBinding(
            SymbolIcon.StrokeProperty,
            new Binding(nameof(Control.Foreground))
            {
                // ButtonBase so toggle buttons (the detail timeline switch) draw their glyph too.
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Primitives.ButtonBase), 1),
            });

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        if (!string.IsNullOrEmpty(label)) content.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(CommandButtonPresentation.IconLabelGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return content;
    }

    private TextBlock BuildMono(string text) => new()
    {
        Text = text,
        Foreground = TryBrush("Text.Primary", Colors.White),
        FontFamily = TryFont("Font.Mono"),
        FontSize = 13,
    };

    private Brush TryBrush(string key, Color fallback) => ThemeBrushes.Follow(this, key, fallback);

    private static FontFamily TryFont(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

    private static Geometry TryGeometry(string key) =>
        Application.Current?.TryFindResource(key) as Geometry ?? Geometry.Empty;

    private Style? TryStyle(string key) =>
        TryFindResource(key) as Style;
}
