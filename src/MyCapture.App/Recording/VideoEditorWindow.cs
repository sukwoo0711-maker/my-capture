using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
internal sealed class VideoEditorWindow : Window
{
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
    private readonly MediaElementPreviewEngine _previewEngine;
    private readonly PreviewSeekCoordinator _previewSeeks;
    private readonly TwoLineTimeline _timeline;
    private readonly VideoLayerTimeline _layerTimeline;
    private readonly TextBlock _positionLabel;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _loadingLabel;
    private readonly Border _loadingOverlay;
    private readonly ListBox _overlayList;
    private Button _trimButton = null!;
    private Button _addTextButton = null!;
    private Button _editTextButton = null!;
    private Button _deleteTextButton = null!;
    private Button _cancelOperationButton = null!;
    private readonly List<Control> _editControls = [];
    private Grid _controlRows = null!;
    private DispatcherTimer? _loadProgressTimer;
    private DispatcherTimer? _openTimeoutTimer;
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _operationCts;

    private double _durationMs;
    private bool _mediaReady;
    private bool _mediaFailed;
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
        _editDocument = (editDocument ?? VideoEditDocument.CreateFor(
                recording.Width,
                recording.Height,
                recording.DurationMs))
            .NormalizeFor(recording.Width, recording.Height, recording.DurationMs);

        StandardWindowTheme.Apply(this);

        Title = UiText.Get("Text_79188256BC8D");
        Background = TryBrush("Surface.Base", Color.FromRgb(0x0B, 0x0F, 0x17));
        Foreground = TryBrush("Text.Primary", Colors.White);
        FontFamily = TryFont("Font.Ui");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        MinWidth = 760;
        MinHeight = 555;

        Rect work = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(920, recording.Width + 120), Math.Max(MinWidth, work.Width - 80));
        Height = Math.Min(Math.Max(680, recording.Height + 260), Math.Max(MinHeight, work.Height - 60));

        AutomationProperties.SetName(this, UiText.Get("Text_EF83F428AA11"));

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
        _layerCanvas.BoundsChanged += (_, _) => _overlayPreview.InvalidateVisual();
        _layerCanvas.InteractionCompleted += (_, _) => CompleteLayerInteraction();
        _previewEngine = new MediaElementPreviewEngine(_media);
        _previewSeeks = new PreviewSeekCoordinator(_previewEngine, recording.Fps);
        _previewSeeks.PreviewPresented += OnPreviewPresented;
        _previewSeeks.SeekFailed += OnPreviewSeekFailed;

        _timeline = new TwoLineTimeline();
        _layerTimeline = new VideoLayerTimeline();
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
        _timeline.TrimChanged += (_, _) => UpdateStatusForMode();
        _positionLabel = BuildMono("00:00.000 / 00:00.000");
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
            MinWidth = 280,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_overlayList, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_overlayList, ScrollBarVisibility.Auto);
        _overlayList.SelectionChanged += OnOverlaySelectionChanged;
        _overlayList.MouseDoubleClick += (_, _) => EditSelectedOverlay();
        AutomationProperties.SetName(_overlayList, UiText.Get("Text_8D1EFD1B2B84"));

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _playbackTimer.Tick += OnPlaybackTick;

        Content = BuildLayout();

        // Controls start DISABLED until the media is ready; a click during load must do nothing.
        SetEditControlsEnabled(false);

        KeyDown += OnKeyDown;
        Closing += OnClosingInternal;
        Loaded += OnLoadedInternal;
        Closed += OnClosedInternal;
        IsVisibleChanged += (_, _) => { if (!IsVisible) { PausePlayback(); } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { PausePlayback(); } };
    }

    private void OnLoadedInternal(object? sender, RoutedEventArgs e)
    {
        // Set the source only after the window (and the MediaElement's visual tree) is loaded.
        // A MediaElement asked to open before it is connected to a rendered tree can silently
        // never raise MediaOpened for a short clip — the reported "2s video won't load" case.
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
                _log.LogWarning("MediaOpened did not fire within 5s; opening with the recorded duration fallback");
                CompleteOpen(_recording.DurationMs, fromFallback: true);
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
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MaxHeight = 240 }); // fit the timeline contents; spare space belongs to preview
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // controls
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status

        var previewStack = new Grid();
        previewStack.Children.Add(_media);
        previewStack.Children.Add(_overlayPreview);
        previewStack.Children.Add(_layerCanvas);
        previewStack.Children.Add(_loadingOverlay);

        var workspace = new Grid();
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 112 });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var exports = new StackPanel { Orientation = Orientation.Horizontal };
        Button saveEdits = MediaExportVisuals.Button(this, UiText.Get("MediaExport_SaveEdits"));
        saveEdits.Click += (_, _) => CommitTrim();
        Button export = MediaExportVisuals.Button(this, UiText.Get("MediaExport_ExportFormats"), "MediaExport_ArrowExport", true);
        export.Click += (_, _) => OpenExport();
        _editControls.Add(saveEdits); _editControls.Add(export);
        exports.Children.Add(saveEdits); exports.Children.Add(export);
        DockPanel.SetDock(exports, Dock.Right); header.Children.Add(exports);
        header.Children.Add(new TextBlock { Text = UiText.Get("MediaExport_Workspace"), FontSize = 17,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis });
        workspace.Children.Add(header);
        FrameworkElement tools = BuildOverlayLane();
        Grid.SetRow(tools, 1); workspace.Children.Add(tools);
        Grid.SetRow(previewStack, 2); workspace.Children.Add(previewStack);
        var preview = new Border
        {
            Background = TryBrush("Surface.Canvas", Colors.Black),
            BorderBrush = TryBrush("Border.Subtle", Color.FromRgb(0x2B, 0x3A, 0x50)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = workspace,
        };
        Grid.SetRow(preview, 0);
        root.Children.Add(preview);

        Grid timeline = BuildTimeline();
        var timelineScroll = new ScrollViewer
        {
            Name = "VideoTimelineToolsScroll",
            Content = timeline,
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
        Grid.SetRow(timelineScroll, 1);
        root.Children.Add(timelineScroll);

        var controls = new StackPanel();
        controls.Children.Add(BuildControlRow());
        RefreshOverlayList();
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);

        var statusBar = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Child = _statusLabel,
        };
        Grid.SetRow(statusBar, 3);
        root.Children.Add(statusBar);

        return root;
    }

    private Grid _timelineCache = null!;

    private Grid BuildTimeline()
    {
        if (_timelineCache is not null)
        {
            return _timelineCache;
        }

        var timeline = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // two-line timeline
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // position label
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // layer tracks
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // layer tools

        Grid.SetRow(_timeline, 0);
        timeline.Children.Add(_timeline);

        _positionLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _positionLabel.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_positionLabel, 1);
        timeline.Children.Add(_positionLabel);

        var layerScroll = new ScrollViewer
        {
            Content = _layerTimeline, MaxHeight = 72, Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        AutomationProperties.SetName(layerScroll, UiText.Get("Text_D9378793F9FE"));
        Grid.SetRow(layerScroll, 2);
        timeline.Children.Add(layerScroll);


        RefreshOverlayList();

        _timelineCache = timeline;
        return timeline;
    }

    private FrameworkElement BuildOverlayLane()
    {
        var lane = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        lane.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        label.Visibility = Visibility.Collapsed;
        lane.Children.Add(label);

        Grid.SetColumn(_overlayList, 1);
        _overlayList.Visibility = Visibility.Collapsed;
        lane.Children.Add(_overlayList);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _addTextButton = MakeIconButton("Icon.Text", UiText.Get("Text_258AD4B095A1"), UiText.Get("Text_991A1A1FF9ED"), "Button.Secondary", AddTextOverlay);
        _editTextButton = MakeIconButton("Icon.Edit", UiText.Get("Text_87B0ACEF85A7"), UiText.Get("Text_AEB8B9AA4E6F"), "Button.Ghost", EditSelectedOverlay);
        _deleteTextButton = MakeIconButton("Icon.Delete", UiText.Get("Text_6139B6C3ED73"), UiText.Get("Text_9163C86EDB22"), "Button.Ghost", DeleteSelectedOverlay);
        actions.Children.Add(_addTextButton);
        actions.Children.Add(MakeButton(UiText.Get("Text_DC0760235344"), UiText.Get("Text_D8F5F6738520"), "Button.Secondary", () => AddShapeLayer(false)));
        actions.Children.Add(MakeButton(UiText.Get("Text_C19FD6787279"), UiText.Get("Text_29C79CAFC1D7"), "Button.Secondary", () => AddShapeLayer(true)));
        actions.Children.Add(MakeButton(UiText.Get("Text_302BAE127938"), UiText.Get("Text_21C405702C2B"), "Button.Secondary", AddImageLayer));
        actions.Children.Add(_editTextButton);
        actions.Children.Add(_deleteTextButton);
        Grid.SetRow(actions, 1);
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

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        transport.Children.Add(MakeIconButton("Icon.First", UiText.Get("Text_91390EADFAD4"), UiText.Get("Text_DC710AA11970"), "Button.Ghost", () => Seek(_timeline.InMs)));
        transport.Children.Add(MakeIconButton("Icon.Rewind", UiText.Get("Text_39EFEAD98C7D"), UiText.Get("Text_E166B75FADF4"), "Button.Ghost", () => StepCoarse(-1)));
        transport.Children.Add(MakeIconButton("Icon.Play", UiText.Get("Text_200EEC5A1E57"), UiText.Get("Text_9A9C87658130"), "Button.Secondary", TogglePlay));
        transport.Children.Add(MakeIconButton("Icon.FastForward", UiText.Get("Text_39EFEAD98C7D"), UiText.Get("Text_E5CAF7CFC916"), "Button.Ghost", () => StepCoarse(1)));
        transport.Children.Add(MakeIconButton("Icon.Last", UiText.Get("Text_FA22CDFB3221"), UiText.Get("Text_DFCA22AB9F61"), "Button.Ghost", () => Seek(_timeline.OutMs)));
        transport.Children.Add(Spacer(10));
        transport.Children.Add(MakeIconButton("Icon.StepBack", UiText.Get("Text_D2E201C9D452"), UiText.Get("Text_B39342508541"), "Button.Ghost", () => StepFrames(-1)));
        transport.Children.Add(MakeIconButton("Icon.StepForward", UiText.Get("Text_D2E201C9D452"), UiText.Get("Text_B753165F6585"), "Button.Ghost", () => StepFrames(1)));
        Grid.SetRow(transport, 0);
        controls.Children.Add(transport);

        var precisionAndEdit = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        precisionAndEdit.Children.Add(MakeCompactIconButton("Icon.ZoomOut", UiText.Get("Text_69D1C5307298"), UiText.Get("Text_48D137437347"), "Button.Ghost", () => _timeline.ZoomAroundPlayhead(1.25)));
        precisionAndEdit.Children.Add(MakeCompactIconButton("Icon.ZoomIn", UiText.Get("Text_11C2E8E755E4"), UiText.Get("Text_C5176D8C3041"), "Button.Ghost", () => _timeline.ZoomAroundPlayhead(0.8)));
        precisionAndEdit.Children.Add(MakeCompactIconButton("Icon.FitAll", UiText.Get("Text_A4B69FAF0C11"), UiText.Get("Text_6CA53DEDFF4A"), "Button.Ghost", () => _timeline.FitAll()));
        precisionAndEdit.Children.Add(Spacer(6));
        _trimButton = MakeCompactButton(
            UiText.Get("Text_4601577BA0F6"),
            UiText.Get("Text_BBA2EFF5675C"),
            "Button.Ghost",
            ToggleTrimMode);
        precisionAndEdit.Children.Add(_trimButton);
        precisionAndEdit.Children.Add(Spacer(6));
        precisionAndEdit.Children.Add(MakeCompactIconButton("Icon.Image", UiText.Get("Text_BEE32B2A6B1A"), UiText.Get("Text_A79A9F93D479"), "Button.Secondary", EditCurrentFrame));
        precisionAndEdit.Children.Add(Spacer(6));

        _cancelOperationButton = new Button
        {
            Content = UiText.Get("Text_5FAE4DA04904"),
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 84,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Danger"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_cancelOperationButton, UiText.Get("Text_79B3DB1CD566"));
        AutomationProperties.SetHelpText(_cancelOperationButton, UiText.Get("Text_3DEFBAD62044"));
        _cancelOperationButton.Click += (_, _) => _operationCts?.Cancel();
        precisionAndEdit.Children.Add(_cancelOperationButton);
        Grid.SetRow(precisionAndEdit, 1);
        controls.Children.Add(precisionAndEdit);

        _controlRows = controls;
        return controls;
    }

    private static Border Spacer(double width) => new() { Width = width };

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
    }

    // ---- media lifecycle ----

    private void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        double dur = _media.NaturalDuration.HasTimeSpan
            ? _media.NaturalDuration.TimeSpan.TotalMilliseconds
            : _recording.DurationMs;
        CompleteOpen(dur, fromFallback: false);
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
        _mediaReady = true;

        _timeline.Initialize(_durationMs, _recording.Fps);
        _layerTimeline.Initialize(_durationMs);
        _editDocument = _editDocument.NormalizeFor(
            _recording.Width,
            _recording.Height,
            _durationMs);
        _initialDocument = _editDocument.Clone();
        _timeline.SetIn(_editDocument.TrimInMs);
        _timeline.SetOut(_editDocument.TrimOutMs);
        _timeline.SetPlayhead(_editDocument.TrimInMs);
        _timeline.SetTrimMode(false);
        _trimButton.Content = UiText.Get("Text_4601577BA0F6");
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _overlayPreview.SetFrameLayers(_editDocument.FrameEditLayers);
        _layerCanvas.SetDocument(_editDocument);
        _layerCanvas.SetSourceTime(_editDocument.TrimInMs);
        _overlayPreview.SetSourceTime(_editDocument.TrimInMs);
        RefreshOverlayList();

        try
        {
            _media.Position = TimeSpan.FromMilliseconds(_editDocument.TrimInMs);
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
    }

    // ---- transport ----

    private void PausePlayback()
    {
        _playRequestVersion++;
        _playRequested = false;
        _isPlaying = false;
        _playbackTimer.Stop();
        if (_mediaReady) { _media.Pause(); }
    }

    private async void TogglePlay()
    {
        if (!_mediaReady || _operationRunning) { return; }
        if (_isPlaying || _playRequested)
        {
            PausePlayback();
            UpdateStatusForMode();
            return;
        }
        _playRequested = true;
        long version = ++_playRequestVersion;
        double position = CurrentMs();
        if (position < _timeline.InMs || position >= _timeline.OutMs - 0.5) { position = _timeline.InMs; }
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
                _statusLabel.Text = UiText.Get("Text_FD6443988325");
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() => { if (version == _playRequestVersion) { _playRequested = false; } });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => { if (version == _playRequestVersion) { PausePlayback(); OnPreviewSeekFailed(ex); } });
        }
    }
    private void Seek(double positionMs)
    {
        if (!_mediaReady)
        {
            return;
        }

        PausePlayback();
        double clamped = Math.Clamp(positionMs, _timeline.InMs, _timeline.OutMs);
        _previewSeeks.RequestExact(clamped);
        _timeline.SetPlayhead(clamped);
        _layerTimeline.SetPlayhead(clamped);
        UpdatePositionLabel(clamped);
    }

    /// <summary>Playhead visual intent moved during pointer interaction.</summary>
    private void OnTimelinePlayhead(object? sender, double ms)
    {
        if (!_mediaReady)
        {
            return;
        }

        PausePlayback();
        double clamped = Math.Clamp(ms, _timeline.InMs, _timeline.OutMs);
        UpdatePositionLabel(clamped);
        _layerTimeline.SetPlayhead(clamped);
        _previewSeeks.RequestPreview(clamped);
    }

    /// <summary>Pointer release requests one priority exact reconciliation seek.</summary>
    private void OnTimelinePlayheadInteractionCompleted(object? sender, double ms)
    {
        if (_mediaReady)
        {
            _previewSeeks.RequestExact(Math.Clamp(ms, _timeline.InMs, _timeline.OutMs));
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
            _statusLabel.Text = UiText.Get("Text_483B0F23F316") + exception.Message;
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

        if (IsLoaded)
        {
            double presented = Math.Clamp(
                frame.PresentedPositionMs,
                _timeline.InMs,
                _timeline.OutMs);
            _overlayPreview.SetSourceTime(presented);
            _layerCanvas.SetSourceTime(presented);
            _layerTimeline.SetPlayhead(presented);
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
        if (position >= _timeline.OutMs - 0.5)
        {
            _media.Pause();
            _isPlaying = false;
            _playbackTimer.Stop();
            position = _timeline.OutMs;
            UpdateStatusForMode();
        }

        double clamped = Math.Clamp(position, _timeline.InMs, _timeline.OutMs);
        _timeline.SetPlayhead(clamped, ensureVisible: false);
        _overlayPreview.SetSourceTime(clamped);
        _layerCanvas.SetSourceTime(clamped);
        _layerTimeline.SetPlayhead(clamped);
        UpdatePositionLabel(clamped);
    }

    // ---- trim ----

    private void ToggleTrimMode()
    {
        bool enabled = !_timeline.TrimModeEnabled;
        _timeline.SetTrimMode(enabled);
        _trimButton.Content = enabled ? UiText.Get("Text_7DDE1114417E") : UiText.Get("Text_4601577BA0F6");
        _statusLabel.Text = enabled
            ? UiText.Get("Text_BE4215610EBC")
            : UiText.Format("Text_C7000F39A0DF", FormatMs(_timeline.SelectedDurationMs));
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
    }

    private void SetInHere()
    {
        if (!_timeline.TrimModeEnabled)
        {
            ToggleTrimMode();
        }

        _timeline.SetIn(CurrentMs());
        _statusLabel.Text = UiText.Format("Text_84EB14013BF5", FormatMs(_timeline.InMs));
    }

    private void SetOutHere()
    {
        if (!_timeline.TrimModeEnabled)
        {
            ToggleTrimMode();
        }

        _timeline.SetOut(CurrentMs());
        _statusLabel.Text = UiText.Format("Text_EFA9B778C037", FormatMs(_timeline.OutMs));
    }

    // ---- timed text notes ----

    private void RememberEdit(VideoEditDocument? before = null)
    {
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

    private void RestoreEdit(bool redo)
    {
        List<VideoEditDocument> source = redo ? _redo : _undo;
        List<VideoEditDocument> target = redo ? _undo : _redo;
        if (source.Count == 0) { return; }
        Guid? selected = SelectedLayerId();
        target.Add(_editDocument.Clone());
        _editDocument = source[^1];
        source.RemoveAt(source.Count - 1);
        _layerCanvas.SetDocument(_editDocument);
        RefreshOverlayList(selected);
        RefreshTextPreview();
        UpdateStatusForMode();
    }

    private bool CanAddGraphic() => _mediaReady && !_operationRunning
        && _editDocument.FrameEditLayers.Count < VideoEditDocument.MaximumFrameLayerCount;

    private void AddShapeLayer(bool ellipse)
    {
        if (!CanAddGraphic()) { return; }
        AddGraphicLayer(VideoLayerAssets.CreateLayer(VideoLayerAssets.CreateShape(ellipse), ellipse ? UiText.Get("Text_C19FD6787279") : UiText.Get("Text_DC0760235344"),
            CurrentMs(), _durationMs, _recording.Width, _recording.Height));
    }

    private async void AddImageLayer()
    {
        if (!CanAddGraphic()) { return; }
        var dialog = new OpenFileDialog { Title = UiText.Get("Text_21C405702C2B"), Filter = UiText.Get("Text_075F6F0A90DB"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) { return; }
        double time = CurrentMs();
        var operation = new CancellationTokenSource();
        _operationCts = operation;
        CancellationToken token = operation.Token;
        SetOperationRunning(true);
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
            _statusLabel.Text = UiText.Get("Text_690A5972C5C2") + ex.Message;
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
            _statusLabel.Text = ex.Message;
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

        var dialog = new TimedTextOverlayDialog(_durationMs, CurrentMs()) { Owner = this };
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
        if (!_mediaReady
            || _operationRunning
            || SelectedOverlay() is not { } selected)
        {
            return;
        }

        var dialog = new TimedTextOverlayDialog(_durationMs, selected.StartMs, selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
        {
            return;
        }

        int index = _editDocument.TextOverlays.FindIndex(item => item.Id == selected.Id);
        if (index >= 0)
        {
            RememberEdit();
            edited.Bounds = selected.Bounds;
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
            _previewSeeks.RequestExact(Math.Clamp(seekTarget.Value, _timeline.InMs, _timeline.OutMs));
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
                    Content = UiText.Format("Text_845D771D4CCB", FormatMs(overlay.StartMs), FormatMs(overlay.EndMs), oneLine),
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
                    Content = UiText.Format("Text_EC43594BD44D", FormatMs(layer.StartMs), FormatMs(layer.EndMs), layer.Name),
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
        _editTextButton.IsEnabled = interactive && textSelected;
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
            _statusLabel.Text = UiText.Get("Text_B0B5252B194F") + ex.Message;
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
            using (DrawingContext dc = visual.RenderOpen())
            {
                renderer.Render(dc, result.Document, pixelsPerDip: 1.0);
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
            _statusLabel.Text = UiText.Get("Text_E3B50B06C736") + ex.Message;
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
        if (_initialDocument is not null && DocumentsEquivalent(_initialDocument, document))
        {
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
        var progress = new Progress<VideoFrameRenderProgress>(value =>
        {
            int percent = value.TotalFrames <= 0
                ? 0
                : (int)Math.Round(value.CompletedFrames * 100.0 / value.TotalFrames);
            _statusLabel.Text = UiText.Format("Text_6421F1340C28", percent, value.CompletedFrames, value.TotalFrames);
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
                    cancellationToken),
                "MyCapture video compositor");

            if (VideoCommitHandler is not null)
            {
                await VideoCommitHandler(document, outputPath, cancellationToken);
            }

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
            _statusLabel.Text = UiText.Get("Text_2DC82774E99B") + ex.Message;
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

    private void OpenExport(bool gif = false)
    {
        if (_exportDialog is not null || !IsVisible || !_mediaReady || _operationRunning
            || !TryValidateLayerResources(_editDocument.FrameEditLayers)) return;
        PausePlayback();
        try
        {
            _exportDialog = new VideoExportDialog(_recording, BuildCurrentDocument(), _loggerFactory, gif) { Owner = this };
            _ = _exportDialog.ShowDialog();
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
        if (running) { PausePlayback(); }
        _operationRunning = running;
        SetEditControlsEnabled(!running && _mediaReady);
        _overlayList.IsEnabled = !running && _mediaReady;
        _layerTimeline.IsEnabled = !running && _mediaReady;
        _layerCanvas.IsEnabled = !running && _mediaReady;
        _cancelOperationButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _cancelOperationButton.IsEnabled = running;
    }

    internal static bool DocumentsEquivalent(VideoEditDocument left, VideoEditDocument right)
    {
        const double tolerance = 0.001;
        if (Math.Abs(left.TrimInMs - right.TrimInMs) > tolerance
            || Math.Abs(left.TrimOutMs - right.TrimOutMs) > tolerance
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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- keyboard ----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_mediaReady)
        {
            return;
        }

        // New mapping (per user request): plain Left/Right jump in LARGE steps; Ctrl or Shift
        // + Left/Right nudge by a SINGLE frame. This removes the old frame-step toggle.
        bool fine = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        switch (e.Key)
        {
            case Key.Escape when _operationRunning:
                e.Handled = true;
                try
                {
                    _operationCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                break;
            case Key.T when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                e.Handled = true;
                AddTextOverlay();
                break;
            case Key.G:
                e.Handled = true;
                ExportGif();
                break;
            case Key.F2 when _overlayList.SelectedItem is not null:
                e.Handled = true;
                EditSelectedOverlay();
                break;
            case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !_operationRunning:
                e.Handled = true; RestoreEdit(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); break;
            case Key.Y when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !_operationRunning:
                e.Handled = true; RestoreEdit(true); break;
            case Key.Delete when SelectedLayerId() is not null:
                e.Handled = true;
                DeleteSelectedOverlay();
                break;
            case Key.Left:
                e.Handled = true;
                if (fine) { StepFrames(-1); } else { StepCoarse(-1); }
                break;
            case Key.Right:
                e.Handled = true;
                if (fine) { StepFrames(1); } else { StepCoarse(1); }
                break;
            case Key.Space:
                e.Handled = true;
                TogglePlay();
                break;
            case Key.I:
                e.Handled = true;
                SetInHere();
                break;
            case Key.O:
                e.Handled = true;
                SetOutHere();
                break;
            case Key.E:
                e.Handled = true;
                EditCurrentFrame();
                break;
            case Key.Home:
                e.Handled = true;
                Seek(_timeline.InMs);
                break;
            case Key.End:
                e.Handled = true;
                Seek(_timeline.OutMs);
                break;
            case Key.OemPlus when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                      (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.Add when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                  (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true;
                _timeline.ZoomAroundPlayhead(0.8);
                break;
            case Key.OemMinus when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                       (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.Subtract when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                   (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true;
                _timeline.ZoomAroundPlayhead(1.25);
                break;
            case Key.OemComma:   // ',' previous frame (Camtasia/ScreenToGif convention)
                e.Handled = true;
                StepFrames(-1);
                break;
            case Key.OemPeriod:  // '.' next frame
                e.Handled = true;
                StepFrames(1);
                break;
        }
    }

    private void UpdateStatusForMode()
    {
        string trim = _timeline.IsFullClip
            ? UiText.Get("Text_424D8C634E79")
            : UiText.Format("Text_9E5B73EAD5B5", FormatMs(_timeline.SelectedDurationMs));
        string recordingHealth = _recording.DroppedFrames == 0
            ? UiText.Get("Text_C1BE53C8ADAF")
            : UiText.Format("Text_271908EF0BBE", _recording.DroppedFrames, _recording.DropRate);
        string layers = UiText.Format("Text_A610CF019CF2", _editDocument.TextOverlays.Count, _editDocument.FrameEditLayers.Count);
        string trimMode = _timeline.TrimModeEnabled ? UiText.Get("Text_C0C699C12863") : UiText.Get("Text_6152EFC59724");
        _statusLabel.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{trimMode} · {trim} · {layers} · {recordingHealth}");
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
    }

    private void UpdatePositionLabel(double positionMs)
    {
        int total = TotalFrameCount();
        int frame = Math.Min(total, FrameStepCalculator.FrameIndexAt(positionMs, _recording.Fps, _durationMs) + 1);
        _positionLabel.Text = UiText.Format("Text_D8A09D5BCDCB", FormatMs(positionMs), FormatMs(_durationMs), frame, total);
    }

    private int TotalFrameCount()
    {
        double frameMs = FrameStepCalculator.FrameDurationMs(_recording.Fps);
        return frameMs > 0
            ? Math.Max(1, (int)Math.Ceiling(_durationMs / frameMs))
            : 1;
    }

    private static string FormatMs(double ms)
    {
        TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private void OnClosingInternal(object? sender, CancelEventArgs e)
    {
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
        Closing -= OnClosingInternal;
        Loaded -= OnLoadedInternal;
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
            Style = TryStyle(styleKey),
            ToolTip = automationName,
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => onClick();
        _editControls.Add(button);
        return button;
    }

    private Button MakeCompactButton(string content, string automationName, string styleKey, Action onClick)
    {
        Button button = MakeButton(content, automationName, styleKey, onClick);
        button.Margin = new Thickness(0, 0, 4, 0);
        button.MinWidth = 52;
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

    private static StackPanel BuildIconLabel(string iconKey, string label)
    {
        var glyph = new System.Windows.Shapes.Path
        {
            Data = TryGeometry(iconKey),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetBinding(
            System.Windows.Shapes.Shape.StrokeProperty,
            new Binding(nameof(Control.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock
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

    private static Brush TryBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily TryFont(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

    private static Geometry TryGeometry(string key) =>
        Application.Current?.TryFindResource(key) as Geometry ?? Geometry.Empty;

    private static Style? TryStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;
}
