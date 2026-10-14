using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    private TextBlock _identityTitle = null!;
    private TextBlock _identityDetails = null!;
    private FrameworkElement _identityText = null!;
    private readonly List<TextBlock> _toolLabels = [];
    private Button _trimToolButton = null!;
    private Button _addTextToolButton = null!;
    private Button _restoreSegmentButton = null!;
    private VideoCutTimeline _cutTimeline = VideoCutTimeline.Create(0, 1);
    private readonly PreviewCutPlayback _cutPlayback = new();
    private bool _cutSeekInFlight;
    private bool _settlingCutPlaybackEnd;
    private int _commandBarLevel = -1;
    private bool _fittingCommandBar;
    private ScrollViewer? _layerTracksScroll;
    private Border _layerPanel = null!;
    private ColumnDefinition _layerPanelColumn = null!;
    private System.Windows.Controls.Primitives.ToggleButton _layerPanelToggle = null!;
    private TextBlock _layerPanelCount = null!;
    private bool _layerPanelUserChoice;
    private bool _settingLayerPanel;
    private readonly Border _trimPreviewBadge;
    private readonly TextBlock _trimPreviewBadgeText;
    private readonly MediaElementPreviewEngine _previewEngine;
    private readonly PreviewSeekCoordinator _previewSeeks;
    private readonly TwoLineTimeline _timeline;
    private readonly VideoLayerTimeline _layerTimeline;
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
    private DispatcherTimer? _loadProgressTimer;
    private DispatcherTimer? _openTimeoutTimer;
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _operationCts;
    private Task<VideoSourceInfo>? _sourceInfo;

    private double _durationMs;
    private bool _mediaReady;
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

    // Test hooks so a headless self-test can confirm the editor actually reaches the ready
    // state for a real clip (the field report: a ~2s video failed to load).
    internal bool IsMediaReadyForTest => _mediaReady;

    internal bool HasMediaFailedForTest => _mediaFailed;

    internal string MediaFailureForTest => _mediaFailure;

    internal double DurationMsForTest => _durationMs;

    internal TwoLineTimeline TimelineForTest => _timeline;

    internal PreviewSeekCoordinator PreviewSeekCoordinatorForTest => _previewSeeks;

    internal MediaElement MediaForTest => _media;

    /// <summary>The frame the preview shows, as a frame edit captures it.</summary>
    internal BitmapSource? RenderCurrentFrameForTest() => TryRenderCurrentFrame();

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
        VideoEditDocument? editDocument = null)
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
        // Recordings are studio range; the source probe removes the expansion for a full-range clip.
        StudioRangeVideoEffect.Apply(_media, studioRange: true);
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
        _layerCanvas.SelectionViewBoundsChanged += (_, _) => PlacePositionChip();
        _previewEngine = new MediaElementPreviewEngine(_media);
        _previewSeeks = new PreviewSeekCoordinator(_previewEngine, recording.Fps);
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
            Foreground = TryBrush("Text.OnAccent", Color.FromRgb(0x0A, 0x08, 0x18)),
        };
        _trimPreviewBadge = new Border
        {
            Name = "VideoTrimPreviewBadge",
            Background = TryBrush("Accent.Default", Color.FromRgb(0x7C, 0x6C, 0xFF)),
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
            Text = UiText.Get("Text_DA09CBABFC1E"),
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
        _sourceInfo = VideoSourceProbe.ReadAsync(_recording.OutputPath, _log);
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

    private void StartLoadProgress()
    {
        // Local files have no real download percentage, so animate an indeterminate-but-honest
        // percentage from MediaElement.DownloadProgress/BufferingProgress, and guarantee the UI
        // never gets stuck on "loading": if MediaOpened has not fired within a timeout, fall
        // back to the known recording duration and open anyway.
        _loadProgressTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(80) };
        int synthetic = 5;
        _loadProgressTimer.Tick += (_, _) =>
        {
            if (_mediaReady || _mediaFailed)
            {
                return;
            }

            double dl = _media.DownloadProgress;      // 0..1, jumps to 1 for local files
            double buf = _media.BufferingProgress;     // 0..1
            int pct = (int)Math.Round(Math.Max(dl, buf) * 100.0);
            if (pct <= 0)
            {
                // No real signal for a local file yet: creep a synthetic value so the user sees motion.
                synthetic = Math.Min(90, synthetic + 5);
                pct = synthetic;
            }

            _loadingLabel.Text = UiText.Format("Text_7E6DD424D331", pct);
        };
        _loadProgressTimer.Start();

        _openTimeoutTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
        _openTimeoutTimer.Tick += (_, _) =>
        {
            _openTimeoutTimer?.Stop();
            if (!_mediaReady && !_mediaFailed)
            {
                _log.LogWarning("MediaOpened did not fire within 5s; opening with the container or recorded duration");
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
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 112 }); // preview
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // controls
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MaxHeight = 250 }); // timeline
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status

        var previewStack = new Grid { Name = "VideoPreviewViewport", MinHeight = 112, ClipToBounds = true };
        previewStack.Children.Add(_media);
        previewStack.Children.Add(_overlayPreview);
        previewStack.Children.Add(_layerCanvas);
        previewStack.Children.Add(_trimPreviewBadge);
        // Current / total time sits on the preview, like a player's time readout, so it is
        // visible at every window size without taking a row from the timeline.
        var positionContent = new StackPanel { Orientation = Orientation.Horizontal };
        _positionLabel.Foreground = TryBrush("Text.Badge", Colors.White);
        _positionLabel.VerticalAlignment = VerticalAlignment.Center;
        _positionFrameLabel.Foreground = TryBrush("Text.Badge", Colors.White);
        _positionFrameLabel.FontSize = 12;
        _positionFrameLabel.Margin = new Thickness(10, 0, 0, 0);
        _positionFrameLabel.VerticalAlignment = VerticalAlignment.Center;
        positionContent.Children.Add(_positionLabel);
        positionContent.Children.Add(_positionFrameLabel);
        var positionChip = new Border
        {
            Name = "VideoPositionChip",
            Background = TryBrush("Surface.Badge", Color.FromArgb(0xE6, 0x08, 0x0C, 0x12)),
            BorderBrush = TryBrush("Border.Badge", Color.FromArgb(0x66, 0xF6, 0xF8, 0xFC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = positionContent,
        };
        _positionChip = positionChip;
        // A small preview keeps the time and drops the frame count, which the status line
        // still reports for frame steps.
        previewStack.SizeChanged += (_, _) =>
        {
            _positionFrameLabel.Visibility = previewStack.ActualWidth >= 440 ? Visibility.Visible : Visibility.Collapsed;
            PlacePositionChip();
        };
        positionChip.SizeChanged += (_, _) => PlacePositionChip();
        _trimPreviewBadge.IsVisibleChanged += (_, _) => PlacePositionChip();
        previewStack.Children.Add(positionChip);
        previewStack.Children.Add(_loadingOverlay);

        var workspace = new Grid();
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 112 });
        FrameworkElement tools = BuildOverlayLane();
        // The command bar spans the window above the preview and the layer panel, so the
        // screen's name, its tools and save/export (top right) stay readable at common sizes.
        Grid header = BuildCommandBar();
        Grid.SetColumnSpan(header, 2);
        workspace.Children.Add(previewStack);
        var preview = new Border
        {
            Background = TryBrush("Surface.Canvas", Colors.Black),
            BorderBrush = TryBrush("Border.Subtle", Color.FromRgb(0x2B, 0x3A, 0x50)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 6, 8, 6),
            Child = workspace,
        };
        var upper = new Grid();
        upper.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // command bar
        upper.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // preview + layers
        upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 300 });
        upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220, MaxWidth = 330 });
        upper.Children.Add(header);
        Grid.SetRow(preview, 1);
        upper.Children.Add(preview);
        var layerPanel = new Border { Name = "VideoLayersPanel", Child = tools, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(1) };
        layerPanel.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        layerPanel.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        Grid.SetRow(layerPanel, 1);
        Grid.SetColumn(layerPanel, 1); upper.Children.Add(layerPanel);
        // A recording with nothing but a trim keeps the whole width for the preview: the
        // layer panel only takes its column while it is shown.
        _layerPanel = layerPanel;
        _layerPanelColumn = upper.ColumnDefinitions[1];
        ApplyLayerPanelVisibility();
        root.Children.Add(upper);
        // Fit the command bar to the window. Only a new width can change what fits, and
        // the grid, unlike the bar, does not grow with the bar's content.
        upper.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
            {
                FitCommandBar(header, upper.ActualWidth);
            }
        };
        _fitCommandBarForTest = available => FitCommandBar(header, available);

        Grid timeline = BuildTimeline();
        var timelineScroll = new ScrollViewer
        {
            Name = "VideoTimelineToolsScroll",
            Content = timeline,
            MaxHeight = 250,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0),
            Focusable = false,
        };
        timelineScroll.GotKeyboardFocus += (_, args) =>
        {
            if (args.NewFocus is FrameworkElement focused)
            {
                focused.BringIntoView();
            }
        };
        AutomationProperties.SetName(timelineScroll, UiText.Get("Text_D9378793F9FE"));
        Grid.SetRow(timelineScroll, 2);
        root.Children.Add(timelineScroll);

        var controls = new StackPanel();
        controls.Children.Add(BuildControlRow());
        RefreshOverlayList();
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        var statusBar = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Child = _statusLabel,
        };
        Grid.SetRow(statusBar, 3);
        root.Children.Add(statusBar);

        void FitTimelineToAvailableHeight()
        {
            if (root.ActualHeight <= 0) return;
            // The command rows share the preview card, but must never take space
            // from its visible media viewport. Compact windows scroll the timeline;
            // larger windows retain enough height for both time strips and layer tracks.
            // Grid.DesiredSize may already be clipped by its star-row allocation.
            // Reserve the independent minimum of its auto command rows and readable list.
            double requiredLayers = layerPanel.Visibility == Visibility.Visible
                ? ((Grid)tools).RowDefinitions.Sum(row => row.Height.IsStar ? row.MinHeight : row.ActualHeight)
                : 0;
            double requiredPreview = header.DesiredSize.Height + Math.Max(
                previewStack.MinHeight
                    + preview.Padding.Top + preview.Padding.Bottom + preview.BorderThickness.Top + preview.BorderThickness.Bottom,
                requiredLayers + layerPanel.Padding.Top + layerPanel.Padding.Bottom
                    + layerPanel.BorderThickness.Top + layerPanel.BorderThickness.Bottom);
            if (Math.Abs(root.RowDefinitions[0].MinHeight - requiredPreview) > 0.5)
                root.RowDefinitions[0].MinHeight = requiredPreview;
            // ActualHeight can include Grid's overflowing minimum rows. Budget against
            // its parent viewport, otherwise the overflow feeds itself back as space.
            double clientHeight = VisualTreeHelper.GetParent(root) is FrameworkElement client && client.ActualHeight > 0
                ? Math.Max(0, client.ActualHeight - root.Margin.Top - root.Margin.Bottom)
                : root.ActualHeight;
            double available = clientHeight - controls.DesiredSize.Height - statusBar.DesiredSize.Height - requiredPreview;
            double maximum = Math.Clamp(Math.Floor(available), 100, 250);
            if (Math.Abs(root.RowDefinitions[2].MaxHeight - maximum) > 0.5)
            {
                root.RowDefinitions[2].MaxHeight = maximum;
                // Bound the ScrollViewer itself so its internal viewport scrolls,
                // instead of arranging full content height outside a clipped Grid cell.
                timelineScroll.MaxHeight = maximum;
            }
        }
        root.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        header.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        tools.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        controls.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        statusBar.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
        root.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetParent(root) is FrameworkElement client)
                client.SizeChanged += (_, _) => FitTimelineToAvailableHeight();
            FitTimelineToAvailableHeight();
        };

        return root;
    }

    /// <summary>
    /// The command bar across the top, grouped by purpose like the image editor's bar:
    /// what this screen is, history, editing tools, and saving/exporting at the right edge.
    /// </summary>
    private Grid BuildCommandBar()
    {
        var bar = new Grid { Name = "VideoEditorCommandBar", Margin = new Thickness(0, 0, 0, 6) };
        foreach (GridLength width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        AutomationProperties.SetName(bar, UiText.Get("VideoEditorUx_CommandBarName"));

        // 1. Identity: this is the recording (video) editor, with the clip's facts. The mark is
        // the recording glyph, not a play triangle that would look like a second play button.
        var mark = new SymbolIcon
        {
            Size = 16,
            Data = TryGeometry("Icon.Record"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mark.SetResourceReference(SymbolIcon.StrokeProperty, "Accent.Default");
        mark.SetResourceReference(SymbolIcon.FillProperty, "Accent.Default");
        var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center, Child = mark };
        tile.SetResourceReference(Border.BackgroundProperty, "Accent.Subtle");
        tile.SetResourceReference(Border.BorderBrushProperty, "Border.Accent");
        tile.BorderThickness = new Thickness(1);
        _identityTitle = new TextBlock { Text = UiText.Get("VideoEditorUx_HeaderTitle"), FontSize = 14, FontWeight = FontWeights.SemiBold };
        _identityTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _identityDetails = new TextBlock { FontSize = 11.5 };
        _identityDetails.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        RefreshIdentityDetails(_recording.DurationMs);
        var identityText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        identityText.Children.Add(_identityTitle);
        identityText.Children.Add(_identityDetails);
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(tile);
        identity.Children.Add(identityText);
        AutomationProperties.SetName(identity, UiText.Get("VideoEditorUx_HeaderTitle"));
        _identityText = identityText;
        bar.Children.Add(identity);

        // 2. History: undo / redo belong with the commands, not buried under keyboard only.
        var history = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        history.Children.Add(CommandSeparator());
        Button undo = MakeTransportIcon("Icon.Undo", UiText.Get("Text_CE706412FA75"), "Button.Ghost", () => RestoreEdit(false));
        Button redo = MakeTransportIcon("Icon.Redo", UiText.Get("Text_078C44D5A66E"), "Button.Ghost", () => RestoreEdit(true));
        _undoButton = undo; _redoButton = redo;
        history.Children.Add(undo); history.Children.Add(redo);
        history.Children.Add(CommandSeparator());
        Grid.SetColumn(history, 1);
        bar.Children.Add(history);

        // 3. Editing tools: trim, add text, and edit the playhead frame as an image (the
        // still-image editor returns it as a layer).
        var editing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _trimToolButton = ToolButton("Icon.Trim", UiText.Get("VideoEditorUx_Trim"), UiText.Get("VideoEditorUx_TrimHelp"), SelectTrimHandle, "Trim");
        _addTextToolButton = ToolButton("Icon.Text", UiText.Get("VideoEditorUx_AddText"), UiText.Get("VideoEditorUx_AddTextHelp"), AddTextOverlay, "AddText");
        Button frameEdit = ToolButton("Icon.Image", UiText.Get("VideoEditorUx_FrameImageEdit"), UiText.Get("Text_A79A9F93D479"), EditCurrentFrame, "FrameEdit");
        editing.Children.Add(_trimToolButton);
        editing.Children.Add(ToolButton("Icon.Trim", UiText.Get("VideoMulticut_Split"),
            UiText.Get("VideoMulticut_SplitHelp"), SplitHere, "Split"));
        _restoreSegmentButton = ToolButton("Icon.Undo", UiText.Get("VideoMulticut_Restore"),
            UiText.Get("VideoMulticut_RestoreHelp"), RestoreSelectedSegment);
        editing.Children.Add(_restoreSegmentButton);
        editing.Children.Add(_addTextToolButton);
        editing.Children.Add(frameEdit);
        _layerPanelToggle = LayerPanelToggle();
        editing.Children.Add(_layerPanelToggle);
        Grid.SetColumn(editing, 2);
        bar.Children.Add(editing);

        // 4. Output, at the right edge: export a new file, then the finishing action. Saving
        // into the library is the emphasised last button, like Done in the image editor.
        var output = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        // Each button says what it does to the library and to the original; the shared hint
        // left people unsure which one keeps the recording intact.
        Button saveEdits = MediaExportVisuals.Button(this, UiText.Get("MediaExport_SaveEdits"), primary: true);
        saveEdits.ToolTip = UiText.Get("VideoEditorUx_SaveEditsHelp");
        AutomationProperties.SetHelpText(saveEdits, UiText.Get("VideoEditorUx_SaveEditsHelp"));
        saveEdits.Click += (_, _) => CommitTrim();
        Button export = MediaExportVisuals.Button(this, UiText.Get("MediaExport_ExportFormats"), "MediaExport_ArrowExport");
        export.ToolTip = UiText.Get("VideoEditorUx_ExportHelp");
        AutomationProperties.SetHelpText(export, UiText.Get("VideoEditorUx_ExportHelp"));
        export.Click += (_, _) => OpenExport();
        _editControls.Add(saveEdits); _editControls.Add(export);
        output.Children.Add(export); output.Children.Add(saveEdits);
        Grid.SetColumn(output, 4);
        bar.Children.Add(output);

        return bar;
    }

    private Button ToolButton(string iconKey, string label, string help, Action onClick, string? command = null)
    {
        StackPanel content = BuildIconLabel(iconKey, label);
        // Same height with or without the caption, so compacting never moves the preview.
        content.MinHeight = 20;
        _toolLabels.Add(content.Children.OfType<TextBlock>().Single());
        Button button = MakeButton(content, label, "Button.Secondary", onClick);
        button.ToolTip = help;
        AutomationProperties.SetHelpText(button, help);
        if (command is not null) ShortcutPresentation.Attach(button, ShortcutScope.Video, command);
        button.MinWidth = 44;
        button.Margin = new Thickness(0, 0, 6, 0);
        return button;
    }

    /// <summary>
    /// The switch for the layer panel, with the layer count. The panel opens by itself for
    /// the first layer and closes again when the last one goes, unless the user chose a
    /// state by hand; most recordings are only trimmed and keep the preview wide.
    /// </summary>
    private System.Windows.Controls.Primitives.ToggleButton LayerPanelToggle()
    {
        StackPanel content = BuildIconLabel("Icon.Layers", UiText.Get("VideoEditorUx_LayerPanel"));
        content.MinHeight = 20;
        _toolLabels.Add(content.Children.OfType<TextBlock>().Single());
        _layerPanelCount = new TextBlock
        {
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
        };
        content.Children.Add(_layerPanelCount);
        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Name = "VideoLayerPanelToggle",
            Content = content,
            MinWidth = 44,
            MinHeight = 42,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiText.Get("VideoEditorUx_LayerPanelHelp"),
        };
        AutomationProperties.SetName(toggle, UiText.Get("VideoEditorUx_LayerPanel"));
        AutomationProperties.SetHelpText(toggle, UiText.Get("VideoEditorUx_LayerPanelHelp"));
        toggle.Checked += (_, _) => OnLayerPanelToggled();
        toggle.Unchecked += (_, _) => OnLayerPanelToggled();
        return toggle;
    }

    private void OnLayerPanelToggled()
    {
        if (!_settingLayerPanel)
        {
            _layerPanelUserChoice = true;
        }

        ApplyLayerPanelVisibility();
    }

    private void ApplyLayerPanelVisibility()
    {
        if (_layerPanel is null || _layerPanelColumn is null || _layerPanelToggle is null)
        {
            return;
        }

        bool visible = _layerPanelToggle.IsChecked == true;
        _layerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _layerPanelColumn.MinWidth = visible ? 220 : 0;
        _layerPanelColumn.MaxWidth = visible ? 330 : 0;
        _layerPanelColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>Follows the document: the panel shows while there are layers, until the user decides.</summary>
    private void SyncLayerPanelToLayers(int layerCount)
    {
        if (_layerPanelToggle is null)
        {
            return;
        }

        _layerPanelCount.Text = layerCount.ToString(CultureInfo.CurrentCulture);
        _layerPanelCount.Visibility = layerCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool shouldShow = layerCount > 0;
        if (_layerPanelUserChoice || (_layerPanelToggle.IsChecked == true) == shouldShow)
        {
            return;
        }

        _settingLayerPanel = true;
        try
        {
            _layerPanelToggle.IsChecked = shouldShow;
        }
        finally
        {
            _settingLayerPanel = false;
        }
    }

    internal bool IsLayerPanelVisibleForTest => _layerPanel.Visibility == Visibility.Visible && _layerPanelColumn.ActualWidth > 0;

    private Border CommandSeparator() => new()
    {
        Width = 1,
        Margin = new Thickness(6, 6, 6, 6),
        Background = TryBrush("Border.Subtle", Colors.Gray),
    };

    /// <summary>
    /// Narrows the command bar step by step instead of clipping it: clip facts, then the
    /// screen title, then tool captions (icons and shortcuts stay), and last the tools that
    /// have another visible entry (the trim bar, the layer menu) and a shortcut (X, Ctrl+T).
    /// </summary>
    private void FitCommandBar(Grid bar, double available)
    {
        // WPF arranges an overflowing element at its own desired width, so the bar's
        // ActualWidth cannot tell what fits; the caller passes its container's real width.
        if (available <= 0 || _fittingCommandBar) return;
        _fittingCommandBar = true;
        try
        {
            for (int level = 0; level <= 4; level++)
            {
                ApplyCommandBarLevel(level);
                double needed = 0;
                foreach (UIElement child in bar.Children)
                {
                    // A nested caption's visibility does not dirty this child, so Measure would
                    // return the previous level's size without this.
                    InvalidateMeasureTree(child);
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    needed += child.DesiredSize.Width;
                }

                if (needed <= available + 0.5) break;
            }
        }
        finally
        {
            _fittingCommandBar = false;
        }
    }

    private static void InvalidateMeasureTree(DependencyObject element)
    {
        if (element is UIElement visual)
        {
            visual.InvalidateMeasure();
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            InvalidateMeasureTree(VisualTreeHelper.GetChild(element, index));
        }
    }

    private void ApplyCommandBarLevel(int level)
    {
        if (level == _commandBarLevel) return;
        _commandBarLevel = level;
        _identityDetails.Visibility = level >= 1 ? Visibility.Collapsed : Visibility.Visible;
        _identityText.Visibility = level >= 2 ? Visibility.Collapsed : Visibility.Visible;
        foreach (TextBlock label in _toolLabels)
        {
            label.Visibility = level >= 3 ? Visibility.Collapsed : Visibility.Visible;
        }

        _trimToolButton.Visibility = level >= 4 ? Visibility.Collapsed : Visibility.Visible;
        _addTextToolButton.Visibility = level >= 4 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal int CommandBarLevelForTest => _commandBarLevel;

    private Action<double>? _fitCommandBarForTest;

    /// <summary>
    /// Fits the command bar to a container width chosen by a test. A runner screen smaller than
    /// the requested window clamps the window, and with it the width the bar is fitted to.
    /// </summary>
    internal void FitCommandBarForTest(double available) => _fitCommandBarForTest?.Invoke(available);

    private void RefreshIdentityDetails(double durationMs)
    {
        _identityDetails.Text = UiText.Format("VideoEditorUx_HeaderDetails",
            TrimPresentation.Clock(durationMs), _recording.Width, _recording.Height, _recording.Fps);
        // Recording health belongs with the clip's facts, not in every status line.
        _identityDetails.ToolTip = RecordingHealth();
    }

    private string RecordingHealth() => _recording.DroppedFrames == 0
        ? UiText.Get("Text_C1BE53C8ADAF")
        : UiText.Format("Text_271908EF0BBE", _recording.DroppedFrames, _recording.DropRate);

    private Grid _timelineCache = null!;

    /// <summary>
    /// The trim bar under the trim timeline: set the start or end at the playhead, read how
    /// long the result is, type a precise time, or go back to the full length.
    /// </summary>
    /// <remarks>
    /// Dragging the handles stays the primary path, so a committed value applies immediately
    /// and there is no separate "apply" gate to forget. Enter is kept for typed values. In a
    /// narrow window the bar becomes two deliberate rows, the actions with their result over
    /// the typed range, rather than wrapping whichever control no longer fits.
    /// </remarks>
    private FrameworkElement BuildTrimBar()
    {
        var bar = new TrimBarPanel { Name = "VideoTrimBar", Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(bar, UiText.Get("VideoEditorUx_TrimBarName"));
        _trimBar = bar;
        var row = new StackPanel { Name = "VideoTrimActions", Orientation = Orientation.Horizontal };
        row.Children.Add(MakeMarkButton(UiText.Get("VideoEditorUx_MarkInHere"), UiText.Get("VideoEditorUx_MarkInHelp"),
            "M9.5,3.5 H5.5 V16.5 H9.5 M8,10 H16", "In"));
        row.Children.Add(MakeMarkButton(UiText.Get("VideoEditorUx_MarkOutHere"), UiText.Get("VideoEditorUx_MarkOutHelp"),
            "M10.5,3.5 H14.5 V16.5 H10.5 M12,10 H4", "Out"));

        _keptLengthLabel = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        _keptLengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _removedLengthLabel = new TextBlock { FontSize = 12 };
        _removedLengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var summary = new StackPanel
        {
            Name = "VideoTrimSummary",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0),
            MinWidth = 120,
        };
        summary.Children.Add(_keptLengthLabel);
        summary.Children.Add(_removedLengthLabel);
        row.Children.Add(summary);
        bar.Children.Add(row);

        // The range: the exact start and end, and the way back to the whole recording.
        var range = new StackPanel { Name = "VideoTrimRange", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        TextBox Input(string name)
        {
            range.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 5, 0) });
            var input = new TextBox
            {
                Width = 120, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = UiText.Get("Video.SourceTimeHint"),
            };
            AutomationProperties.SetName(input, name);
            AutomationProperties.SetHelpText(input, UiText.Get("Video.SourceTimeHint"));
            input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ApplyTrimInputs(); } };
            input.LostFocus += (_, _) => ApplyTrimInputs();
            range.Children.Add(input); _editControls.Add(input);
            return input;
        }
        _trimInInput = Input(UiText.Get("Video.TrimIn"));
        _trimOutInput = Input(UiText.Get("Video.TrimOut"));

        _resetTrimButton = MakeButton(UiText.Get("VideoEditorUx_ResetTrim"), UiText.Get("VideoEditorUx_ResetTrim"), "Button.Secondary", ResetTrim);
        _resetTrimButton.MinHeight = 36;
        _resetTrimButton.Margin = new Thickness(0);
        _resetTrimButton.ToolTip = UiText.Get("VideoEditorUx_ResetTrimHelp");
        AutomationProperties.SetHelpText(_resetTrimButton, UiText.Get("VideoEditorUx_ResetTrimHelp"));
        range.Children.Add(_resetTrimButton);
        bar.Children.Add(range);
        RefreshTrimInputs();
        return bar;
    }

    private TrimBarPanel? _trimBar;

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

    /// <summary>Whether the trim bar currently puts the typed range on a second row.</summary>
    internal bool TrimBarHasTwoRowsForTest => _trimBar?.IsTwoRows == true;

    private Grid BuildTimeline()
    {
        if (_timelineCache is not null)
        {
            return _timelineCache;
        }

        var timeline = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // trim timeline (+ detail)
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // trim bar
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // layer tracks

        timeline.Children.Add(_timeline);
        FrameworkElement trimBar = BuildTrimBar();
        Grid.SetRow(trimBar, 1);
        timeline.Children.Add(trimBar);

        var layerScroll = new ScrollViewer
        {
            Content = _layerTimeline, MaxHeight = 72, Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        AutomationProperties.SetName(layerScroll, UiText.Get("Text_D9378793F9FE"));
        _layerTracksScroll = layerScroll;
        // The rows reserve a label gutter and may acquire a vertical scrollbar. Give both
        // time strips exactly that same horizontal time area, even when the detail is folded.
        _layerTimeline.SizeChanged += (_, _) => AlignTimelineTracks();
        layerScroll.SizeChanged += (_, _) => AlignTimelineTracks();
        _timeline.Margin = new Thickness(VideoLayerTimeline.LabelWidth - TwoLineTimeline.TrackInset, 0,
            -TwoLineTimeline.TrackInset, 0);
        Grid.SetRow(layerScroll, 2);
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

        var label = new TextBlock
        {
            Text = UiText.Get("Text_DCA8CAFD9D0D"),
            FontWeight = FontWeights.SemiBold,
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(label, 0);
        label.Text = UiText.Get("Video.Layers");
        var layerHeader = new DockPanel();
        lane.Children.Add(layerHeader);

        // Each row already shows its layer's name and visible interval, and the edit and delete
        // commands above act on the selected row. A separate properties block repeated the same
        // name and times under the list and took the height the preview needs in small windows.
        Grid.SetRow(_overlayList, 1);
        _overlayList.MaxHeight = double.PositiveInfinity;
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
        _addTextButton.ContextMenu = addMenu;
        addMenu.PlacementTarget = _addTextButton;
        _editTextButton = MakeTransportIcon("Icon.Edit", UiText.Get("Text_AEB8B9AA4E6F"), "Button.Ghost", EditSelectedOverlay);
        _deleteTextButton = MakeTransportIcon("Icon.Delete", UiText.Get("Text_9163C86EDB22"), "Button.Ghost", DeleteSelectedOverlay);
        actions.Children.Add(_addTextButton);
        var selectedActions = new StackPanel { Orientation = Orientation.Horizontal };
        selectedActions.Children.Add(_editTextButton);
        selectedActions.Children.Add(_deleteTextButton);
        DockPanel.SetDock(selectedActions, Dock.Right);
        layerHeader.Children.Add(selectedActions);
        layerHeader.Children.Add(label);
        foreach (Button action in actions.Children.OfType<Button>())
        {
            if (double.IsNaN(action.Width))
            {
                action.MinWidth = 52;
                action.Padding = new Thickness(10, 5, 10, 5);
            }
            action.MinHeight = 36;
            action.Margin = new Thickness(0, 0, 4, 0);
        }
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

        // One row, two groups: playback in the middle, as under any player, and the timeline
        // view tools (zoom, fit, detail strip) at the right edge, next to what they change.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var transport = new StackPanel
        {
            Name = "VideoTransport",
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // Cluster 1: transport position.
        transport.Children.Add(MakeTransportIcon("Icon.First", UiText.Get("Text_DC710AA11970"), "Button.Ghost", () => Seek(_timeline.InMs)));
        transport.Children.Add(MakeTransportIcon("Icon.Rewind", UiText.Get("Text_E166B75FADF4"), "Button.Ghost", () => StepCoarse(-1)));
        // The row's largest target: play/pause. It is bordered, not filled: the one filled
        // accent button in this window is Save to library, the action that finishes the edit,
        // and the accent elsewhere marks the selection (trim range, chosen layer).
        Button play = MakeTransportIcon("Icon.Play", UiText.Get("Text_9A9C87658130"), "Button.Secondary", TogglePlay, glyphSize: 20);
        play.MinWidth = 56;
        play.MinHeight = 48;
        play.Margin = new Thickness(0, 0, 3, 0);
        _playButton = play;
        UpdatePlayButton();
        transport.Children.Add(play);
        transport.Children.Add(MakeTransportIcon("Icon.FastForward", UiText.Get("Text_E5CAF7CFC916"), "Button.Ghost", () => StepCoarse(1)));
        transport.Children.Add(MakeTransportIcon("Icon.Last", UiText.Get("Text_DFCA22AB9F61"), "Button.Ghost", () => Seek(_timeline.OutMs)));
        transport.Children.Add(CommandSeparator());
        // Cluster 2: frame stepping.
        transport.Children.Add(MakeTransportIcon("Icon.StepBack", UiText.Get("Text_B39342508541"), "Button.Ghost", () => StepFrames(-1)));
        transport.Children.Add(MakeTransportIcon("Icon.StepForward", UiText.Get("Text_B753165F6585"), "Button.Ghost", () => StepFrames(1)));
        Grid.SetColumn(transport, 1);
        row.Children.Add(transport);
        // Cluster 3: timeline view, at the right edge of the row.
        var view = new StackPanel
        {
            Name = "VideoTimelineViewTools",
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view.Children.Add(MakeTransportIcon("Icon.ZoomOut", UiText.Get("Text_48D137437347"), "Button.Ghost", () => _timeline.ZoomAroundPlayhead(1.25)));
        view.Children.Add(MakeTransportIcon("Icon.ZoomIn", UiText.Get("Text_C5176D8C3041"), "Button.Ghost", () => _timeline.ZoomAroundPlayhead(0.8)));
        view.Children.Add(MakeTransportIcon("Icon.FitAll", UiText.Get("Text_6CA53DEDFF4A"), "Button.Ghost", () => { ShowDetailTimeline(); _timeline.FitAll(); }));
        // The frame-level detail strip is an expert view: folded by default so the trim
        // timeline is the one timeline people see first. Zooming opens it.
        _detailToggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = BuildIconLabel("Icon.ChevronDown", UiText.Get("VideoEditorUx_DetailTimeline")),
            MinHeight = 42,
            Padding = new Thickness(6, 4, 8, 4),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiText.Get("VideoEditorUx_DetailTimelineHelp"),
        };
        AutomationProperties.SetName(_detailToggle, UiText.Get("VideoEditorUx_DetailTimeline"));
        AutomationProperties.SetHelpText(_detailToggle, UiText.Get("VideoEditorUx_DetailTimelineHelp"));
        _detailToggle.Checked += (_, _) => _timeline.SetDetailVisible(true);
        _detailToggle.Unchecked += (_, _) => _timeline.SetDetailVisible(false);
        _editControls.Add(_detailToggle);
        // Its caption folds with the tool captions in narrow windows; the icon and tooltip stay.
        _toolLabels.Add(((StackPanel)_detailToggle.Content).Children.OfType<TextBlock>().Single());
        view.Children.Add(_detailToggle);
        Grid.SetColumn(view, 2);
        row.Children.Add(view);
        // Start/end marks moved to the trim bar under the timeline, next to what they change.
        Grid.SetRow(row, 0);
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
            glyph.Data = TryGeometry(playing ? "Icon.Pause" : "Icon.Play");
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

    private async void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        double? playerMs = _media.NaturalDuration.HasTimeSpan
            ? _media.NaturalDuration.TimeSpan.TotalMilliseconds
            : null;
        // The probe never faults and normally finishes before MediaOpened.
        VideoSourceInfo source = _sourceInfo is null ? new VideoSourceInfo(0, StudioRange: true) : await _sourceInfo;
        if (_closing)
        {
            return;
        }

        StudioRangeVideoEffect.Apply(_media, source.StudioRange);
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
        _loadProgressTimer?.Stop();
        _loadProgressTimer = null;
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
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != _playRequestVersion || !_playRequested || !IsLoaded) { return; }
                _playRequested = false;
                _media.Play();
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
            SkipRemovedRange(decision.SourceTimeMs);
            return;
        }
        if (decision.Action == PreviewCutAction.Stop)
        {
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

    private async void SkipRemovedRange(double targetMs)
    {
        _cutSeekInFlight = true;
        long version = _playRequestVersion;
        try
        {
            // The coordinator serializes decoder access. Its completion is best-effort;
            // subsequent ticks observe the actual landing before publishing new overlays.
            await _previewSeeks.RequestExactAsync(targetMs);
            if (version != _playRequestVersion || !_isPlaying || _closing) return;
            _media.Play();
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

    private void ShowDetailTimeline()
    {
        if (_detailToggle is not null && _detailToggle.IsChecked != true)
        {
            _detailToggle.IsChecked = true;
        }
    }

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
        _restoreSegmentButton.IsEnabled = _mediaReady && !_operationRunning
            && _timeline.SelectedSegment is { } selected && _cutTimeline.RemovedRanges.Contains(selected);
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
        _editDocument.RemovedRanges = [.. next.RemovedRanges];
        _editDocument.SplitPoints = [.. next.SplitPoints];
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
        if (_editDocument.RemovedRanges.Count > 0)
        {
            VideoCutTimeline source = VideoCutTimeline.Create(0, _durationMs, _editDocument.RemovedRanges);
            double minimum = Math.Min(TrimSelection.MinimumSpanMs, _timeline.OutMs - _timeline.InMs);
            if (!source.KeptRanges.Any(range => Math.Min(range.EndMs, _timeline.OutMs)
                - Math.Max(range.StartMs, _timeline.InMs) >= minimum - 1e-7))
            {
                // Recovery's all-cut fallback is for malformed files, never an implicit
                // restoration of a user's deleted content during a handle/keyboard edit.
                SynchronizeTrim();
                UpdateStatusForMode();
                return;
            }
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
        RefreshTrimSummary();
    }

    private void ApplyTrimInputs()
    {
        // A focus change can also mean the window is closing; never touch a closed media
        // element, and do not stop playback just because the field lost focus.
        if (!_mediaReady || _operationRunning || _closing) return;
        if (!TryReadTrimInput(_trimInInput.Text, _timeline.InMs, out double start)
            || !TryReadTrimInput(_trimOutInput.Text, _timeline.OutMs, out double end)
            || !_timeline.SetTrimRange(start, end))
        {
            _statusLabel.Text = UiText.Get("Video.TrimInputInvalid");
            RefreshTrimInputs();
            return;
        }
        RefreshTrimInputs();
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
            Stroke = TryBrush("Accent.Default", Colors.Teal),
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
            // Layer tracks appear once there is a layer; empty "no layer" lanes are noise
            // under the trim tools. Add text / Add layer remain the entry points.
            int layerCount = _editDocument.TextOverlays.Count + _editDocument.FrameEditLayers.Count;
            if (_layerTracksScroll is not null)
            {
                _layerTracksScroll.Visibility = layerCount > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            SyncLayerPanelToLayers(layerCount);
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
        if (!_mediaReady)
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

        try
        {
            _statusLabel.Text = UiText.Get("Text_571C8E331C15");
            await _previewSeeks.RequestExactAsync(CurrentMs());
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

        if (!IsLoaded)
        {
            return;
        }

        BitmapSource? frame = TryRenderCurrentFrame();
        if (frame is null)
        {
            _statusLabel.Text = UiText.Get("Text_33F7635FDE36");
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return;
        }

        // Wrap the extracted frame as a FrozenFrame so the existing capture editor opens
        // unchanged — image editing from a video frame is identical to editing a capture.
        var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
        var frozen = new FrozenFrame(frame, region, null, 0);

        var editor = new AnnotationEditorWindow(
            frozen,
            region,
            frame,
            title: UiText.Get("Text_2A0C70A93334"),
            privacyRedactionService: PrivacyRedactionService);
        FrameImageCommitSession? commitSession = FrameImageCommitHandlerFactory?.Invoke();
        editor.CommitRequested = commitSession?.CommitAsync ?? (_ => Task.FromResult(false));
        editor.Committed += (_, result) =>
        {
            FrameImageCaptured?.Invoke(this, new AnnotationFrameCapturedEventArgs(result));
            AddFrameEditLayer(result, layerTimeMs);
        };
        editor.Closed += (_, _) => commitSession?.Dispose();
        editor.Owner = this;
        editor.Show();
        _ = editor.Activate();
    }

    private void AddFrameEditLayer(AnnotationEditingResult result, double sourceTimeMs)
    {
        try
        {
            if (result.Document.Items.Count == 0)
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
            // is why the turn count, not the size, decides).
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

    private BitmapSource? TryRenderCurrentFrame()
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
                // A VisualBrush of the MediaElement captures the frame currently shown. The
                // element is in manual/scrubbing mode, so whatever Position it is parked at
                // is the frame we paint into the render target.
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
        if (running) { _resumeAfterScrub = false; PausePlayback(); }
        _operationRunning = running;
        SetEditControlsEnabled(!running && _mediaReady);
        _overlayList.IsEnabled = !running && _mediaReady;
        _layerTimeline.IsEnabled = !running && _mediaReady;
        _layerCanvas.IsEnabled = !running && _mediaReady;
        if (running) _saveProgress.Start(UiText.Get("VideoEdit_ImageLayerPreparing"), indeterminate: true);
        else _saveProgress.Stop();
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
                File.Delete(fullPath);
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
        if (!_mediaReady || Keyboard.FocusedElement is TextBox) return;
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
    /// Space is play/pause everywhere outside text input. Without this a focused button (the
    /// one just clicked, say "start here") would take Space as a click and repeat itself.
    /// </summary>
    private void OnPreviewSpaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || !_mediaReady
            || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
        {
            return;
        }

        e.Handled = HandleShortcut(Key.Space, ModifierKeys.None);
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
        string kept = TrimPresentation.Clock(_cutTimeline.OutputDurationMs);
        if (_timeline.IsTrimDragging)
        {
            // While a handle moves, the status names the end being adjusted and the result.
            _statusLabel.Text = _timeline.IsEndHandleActive
                ? UiText.Format("VideoEditorUx_TrimDraggingEnd", TrimPresentation.Clock(_timeline.OutMs), kept)
                : UiText.Format("VideoEditorUx_TrimDraggingStart", TrimPresentation.Clock(_timeline.InMs), kept);
            return;
        }

        // The idle status says what to do next, not every count the editor knows: an
        // untrimmed clip gets the trim hint, a trimmed one how to apply it. The lengths are in
        // the trim bar's summary and the layer count on the layer switch, so neither repeats
        // here. Dropped frames appear only when there is something to report.
        var parts = new List<string>(2)
        {
            UiText.Get(_timeline.IsFullClip && _cutTimeline.RemovedRanges.Count == 0
                ? "VideoEditorUx_StatusUntrimmed" : "VideoEditorUx_StatusTrimmed"),
        };
        if (_recording.DroppedFrames > 0)
        {
            parts.Add(RecordingHealth());
        }

        _statusLabel.Text = string.Join(" · ", parts);
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

    /// <summary>The corner of the preview the time readout sits in; bottom left unless a selected layer is there.</summary>
    internal (HorizontalAlignment Horizontal, VerticalAlignment Vertical) PositionChipCornerForTest =>
        (_positionChip.HorizontalAlignment, _positionChip.VerticalAlignment);

    /// <summary>
    /// Keeps the time readout off the selected layer. It sits bottom left, like a player's
    /// readout; while the selected layer's outline or handles would be under it, it moves to
    /// the first other corner that leaves them free (top left only while the trim badge, which
    /// lives there, is hidden), or else to the corner that covers the least of them.
    /// </summary>
    private void PlacePositionChip()
    {
        if (_positionChip is null)
        {
            return;
        }

        (HorizontalAlignment Horizontal, VerticalAlignment Vertical) chosen = (HorizontalAlignment.Left, VerticalAlignment.Bottom);
        if (_layerCanvas.SelectionViewBounds is { } layer && !layer.IsEmpty)
        {
            Size area = _layerCanvas.RenderSize;
            Thickness margin = _positionChip.Margin;
            Size chip = _positionChip.IsMeasureValid && _positionChip.DesiredSize.Width > 0
                ? _positionChip.DesiredSize
                : new Size(_positionChip.ActualWidth + margin.Left + margin.Right, _positionChip.ActualHeight + margin.Top + margin.Bottom);
            double leastCovered = double.PositiveInfinity;
            foreach ((HorizontalAlignment h, VerticalAlignment v) corner in new[]
            {
                (HorizontalAlignment.Left, VerticalAlignment.Bottom),
                (HorizontalAlignment.Left, VerticalAlignment.Top),
                (HorizontalAlignment.Right, VerticalAlignment.Bottom),
                (HorizontalAlignment.Right, VerticalAlignment.Top),
            })
            {
                if (corner == (HorizontalAlignment.Left, VerticalAlignment.Top) && _trimPreviewBadge.IsVisible)
                {
                    continue;
                }

                var spot = new Rect(
                    corner.h == HorizontalAlignment.Left ? 0 : Math.Max(0, area.Width - chip.Width),
                    corner.v == VerticalAlignment.Top ? 0 : Math.Max(0, area.Height - chip.Height),
                    chip.Width,
                    chip.Height);
                Rect overlap = Rect.Intersect(spot, layer);
                double covered = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                if (covered < leastCovered)
                {
                    leastCovered = covered;
                    chosen = corner;
                }

                if (covered == 0)
                {
                    break;
                }
            }
        }

        _positionChip.HorizontalAlignment = chosen.Horizontal;
        _positionChip.VerticalAlignment = chosen.Vertical;
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
        _previewSeeks.PreviewPresented -= OnPreviewPresented;
        _previewSeeks.SeekFailed -= OnPreviewSeekFailed;
        _previewSeeks.Dispose();
        _previewEngine.Dispose();
        _timeline.Dispose();
        KeyDown -= OnKeyDown;
        PreviewKeyDown -= OnPreviewImeKeyDown;
        PreviewKeyDown -= OnPreviewSpaceKeyDown;
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
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = automationName,
        };
        // A resource reference, so the tier also applies when the theme arrives after the
        // window is built (an isolated window or a test that loads the theme afterwards).
        button.SetResourceReference(StyleProperty, styleKey);
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => onClick();
        _editControls.Add(button);
        return button;
    }

    /// <summary>
    /// A large "start here" / "end here" button. Its bracket glyph matches the trim handle
    /// it moves, and its shortcut (I or O) is printed under the label.
    /// </summary>
    private Button MakeMarkButton(string label, string help, string bracketFigures, string command)
    {
        var glyph = new SymbolIcon
        {
            Size = 20,
            Data = Geometry.Parse(bracketFigures),
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(SymbolIcon.StrokeProperty, "Accent.Default");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Button button = MakeButton(content, label, "Button.Secondary", _shortcutActions[command]);
        button.ToolTip = help;
        AutomationProperties.SetHelpText(button, help);
        ShortcutPresentation.Attach(button, ShortcutScope.Video, command);
        button.MinWidth = 96;
        button.MinHeight = 44;
        button.Padding = new Thickness(10, 3, 12, 3);
        button.Margin = new Thickness(0, 2, 6, 2);
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
        button.Margin = new Thickness(0, 0, 4, 0);
        button.MinWidth = 52;
        return button;
    }

    private Button MakeTransportIcon(string iconKey, string automationName, string styleKey, Action onClick, double glyphSize = 20)
    {
        string? command = iconKey switch
        {
            "Icon.Undo" => "Undo", "Icon.Redo" => "Redo", "Icon.Edit" => "EditLayer", "Icon.Delete" => "DeleteLayer",
            "Icon.First" => "First", "Icon.Rewind" => "Previous", "Icon.Play" => "Play", "Icon.FastForward" => "Next",
            "Icon.Last" => "Last", "Icon.StepBack" => "PreviousFrame", "Icon.StepForward" => "NextFrame",
            "Icon.ZoomIn" => "ZoomIn", "Icon.ZoomOut" => "ZoomOut", _ => null,
        };
        Button button = MakeButton(BuildIconLabel(iconKey, string.Empty, glyphSize), automationName, styleKey,
            command is null ? onClick : _shortcutActions[command]);
        button.MinWidth = 36; button.MinHeight = 42;
        button.Padding = new Thickness(4); button.Margin = new Thickness(0, 0, 3, 0);
        if (command is "PreviousFrame" or "NextFrame")
        {
            // The frame-step glyphs look like go-to-start/end, and their keys (, and .) were
            // unreadable dots; a short name says what they do. The keys stay in the tooltip.
            var face = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var glyph = (UIElement)button.Content;
            button.Content = null;
            face.Children.Add(glyph);
            var name = new TextBlock
            {
                Text = UiText.Get(command == "PreviousFrame" ? "VideoEditorUx_PreviousFrameShort" : "VideoEditorUx_NextFrameShort"),
                FontSize = ShortcutPresentation.CaptionFontSize,
                FontWeight = FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            name.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
            face.Children.Add(name);
            button.Content = face;
            ShortcutPresentation.Describe(button, ShortcutScope.Video, command);
        }
        else if (command is not null)
        {
            ShortcutPresentation.Attach(button, ShortcutScope.Video, command);
        }

        return button;
    }

    private static StackPanel BuildIconLabel(string iconKey, string label, double glyphSize = 20)
    {
        var glyph = new SymbolIcon
        {
            Size = glyphSize,
            Data = TryGeometry(iconKey),
            Fill = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
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
            Margin = new Thickness(4, 0, 0, 0),
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
