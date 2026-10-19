using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Globalization;
using Microsoft.Win32;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Editing;

/// <summary>
/// The selected-image annotation workspace hosted by a standalone editor window.
/// </summary>
/// <remarks>
/// <para>
/// The source monitor frame remains attached to commit metadata, but the visual surface is built
/// from the selected bitmap only. This keeps surrounding desktop pixels out of the editor while
/// preserving physical-pixel annotation coordinates and editable object layers.
/// </para>
/// <para>
/// Layout: a top command bar on the same surface as the title bar (Undo/Redo, Crop and
/// Quick Redact on the left; Save to file, Copy, Done and Cancel on the right), a left rail
/// of drawing tools grouped by intent, a central image-only viewport, a contextual
/// inspector that shows values rather than instructions, and a bottom status line that
/// reports results. Crop is a mode: while it is active the command bar becomes
/// rotate / ratio / auto trim / Apply / Cancel, as in Photos, where rotation lives inside
/// crop. There is no horizontal toolbar scrolling; compact commands retain their icon and
/// shortcut. Every icon control carries a tooltip, automation name, and keyboard
/// route, and the status region is an automation live region so screen readers hear each
/// gesture result.
/// </para>
/// <para>
/// Esc only ever steps back inside the editor (finish the text being typed, clear the
/// selection, return to the select tool); it never closes the window. Closing with the
/// title bar, Alt+F4 or Cancel asks before dropping unsaved edits.
/// </para>
/// </remarks>
internal sealed partial class AnnotationEditorControl : Grid
{
    private EditorTool _preferredTool = EditorTool.Rectangle;
    private bool _preferencesReady;
    private const double ToolRailWidth = 94;
    private const double InspectorWidth = 232;

    // Below this width the inspector collapses before it can squeeze the image workspace.
    // Normal editor startup now targets a comfortable width above this threshold.
    private const double InspectorCompactWidth = 860;

    private readonly AnnotationSourceMetadata _frame;
    private readonly BitmapSource _selectedBitmap;
    private readonly AnnotationImageStore _imageStore = new();
    private readonly AnnotationBlurCache _blurCache;
    private readonly AnnotationEditorController _controller;
    private readonly AnnotationEditorSurface _surface;
    private readonly string? _sourceWindowTitle;
    private readonly bool _showSourceWindowTitle;
    private readonly IPrivacyRedactionService? _privacyRedactionService;
    private readonly Grid _viewport = new();
    private readonly Canvas _overlayCanvas = new();
    private readonly Dictionary<EditorTool, ToggleButton> _toolButtons = new();
    private bool _mosaicDragging;
    private PointD _mosaicStart;
    private int _mosaicBlockSize = 12;
    private CropSelection? _crop;
    private PointD? _edgeCropStart;
    private Button _autoTrimButton = null!;
    private bool _autoTrimInProgress;
    private CancellationTokenSource? _autoTrimCts;
    private long _documentRevision;
    private FrameworkElement _cropSection = null!;
    private TextBox _cropWidth = null!;
    private TextBox _cropHeight = null!;
    private readonly HashSet<TextBox> _dirtyCropInputs = [];
    private readonly Dictionary<TextBox, (Border Border, TextBlock Message)> _cropInputErrors = [];
    private ComboBox _cropRatio = null!;
    internal CropSelection? PendingCrop => _crop;
    internal IReadOnlyDictionary<string, Action> ShortcutActions => _shortcutActions;
    private readonly Dictionary<string, Action> _shortcutActions = [];
    private readonly Dictionary<string, Action> _cropActions = [];

    private int _canvasWidth;
    private int _canvasHeight;
    private MonitorInfo? _frameMonitor;
    private double _frameElapsedMilliseconds;
    private RectD _originalCropRegion;
    private int _rotationTurns;

    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Button _rotateLeftButton = null!;
    private Button _rotateRightButton = null!;
    private StackPanel _rotationCommands = null!;
    private Border _cropRotationHost = null!;
    private Button _deleteButton = null!;
    private Button _redactButton = null!;
    private Button _copyTextButton = null!;
    private bool _textRecognitionInProgress;
    private Slider _thicknessSlider = null!;
    private ComboBox _strokeStyleComboBox = null!;
    private Slider _fillTransparencySlider = null!;
    private TextBlock _fillTransparencyLabel = null!;
    private FrameworkElement _shapeStyleSection = null!;
    private Slider _fontSizeSlider = null!;
    private FrameworkElement _fontSizeSection = null!;
    private FrameworkElement _textStyleSection = null!;
    private CheckBox _textBold = null!;
    private CheckBox _textBackground = null!;
    private CheckBox _textOutline = null!;
    private ComboBox _textFontFamily = null!;
    private FrameworkElement _fontFamilySection = null!;
    private bool _defaultTextBold;
    private bool _defaultTextBackground;
    private bool _defaultTextOutline = true;
    private readonly List<string> _recentFontFamilies = [];
    private Slider _blurSlider = null!;
    private FrameworkElement _blurSection = null!;
    private bool _syncingInspector;
    private ColumnDefinition _inspectorColumn = null!;
    private Border _inspectorPanel = null!;
    private WrapPanel _swatchPanel = null!;
    private TextBox _customColorInput = null!;
    private FrameworkElement _colorSection = null!;
    private FrameworkElement _thicknessSection = null!;
    private TextBlock _inspectorTitle = null!;
    private TextBlock _statusText = null!;
    private TextBlock _dimensionsText = null!;
    private bool _inspectorCompact;
    private FrameworkElement _editCommands = null!;
    private FrameworkElement _cropCommands = null!;
    private readonly List<IReadOnlyList<EditorTool>> _toolGroups = [];
    private FrameworkElement _stepSection = null!;
    private Border _stepPreview = null!;
    private Path _stepPreviewNumber = null!;
    private readonly HangulShortcutRelay _hangulRelay;
    private DispatcherTimer? _hangulNoticeTimer;
    private long? _hangulNoticeShownAt;
    private EditorPointerTarget _pointerTarget;
    private int _cursorBaseSize = EditorCursors.DefaultBaseSize;
    private EditorCursorStyle _cursorStyle = EditorCursorStyle.White;

    private TextBox? _activeTextBox;
    private TextAnnotation? _editingText;
    // Distance from the live box's outer edge to its first glyph (see PlaceTextBox).
    private Point _liveTextInset;
    private double _liveTextWidth;
    // The live box's left edge with the scroll bar on its right (see UpdateLiveTextViewport).
    private double _liveTextLeft;
    private bool _completed;
    private bool _commitInProgress;
    private bool _redactionInProgress;
    private CancellationTokenSource? _redactionCts;

    internal AnnotationEditorControl(FrozenFrame frame, RectD bitmapRegion, BitmapSource selectedBitmap)
        : this(
            frame,
            bitmapRegion,
            selectedBitmap,
            initialDocument: null,
            initialAssets: null,
            privacyRedactionService: null)
    {
    }

    /// <summary>
    /// Creates the editor over <paramref name="selectedBitmap"/>, optionally seeded with an
    /// existing annotation layer and its decoded image assets so a stored capture can be
    /// re-edited from its unflattened original and live layer.
    /// </summary>
    /// <param name="initialDocument">
    /// A restored layer to edit, or <see langword="null"/> to start from an empty document.
    /// </param>
    /// <param name="initialAssets">
    /// Decoded, frozen pixels keyed by the layer's canonical <c>asset-XX.png</c> names, so
    /// the renderer can draw inserted images without re-reading the sidecar files.
    /// </param>
    internal AnnotationEditorControl(
        FrozenFrame frame,
        RectD bitmapRegion,
        BitmapSource selectedBitmap,
        AnnotationDocument? initialDocument,
        IReadOnlyDictionary<string, BitmapSource>? initialAssets,
        IPrivacyRedactionService? privacyRedactionService = null,
        string? sourceWindowTitle = null)
    {
        _frame = AnnotationSourceMetadata.FromFrame(frame);
        _selectedBitmap = selectedBitmap ?? throw new ArgumentNullException(nameof(selectedBitmap));
        _privacyRedactionService = privacyRedactionService;
        // A frame/clipboard editor has no captured window title. Preserve that explicit
        // absence in the result instead of falling back to a synthetic record label.
        _sourceWindowTitle = sourceWindowTitle ?? string.Empty;
        _showSourceWindowTitle = AnnotationEditorPreferences.ReadShowSourceWindowTitle?.Invoke() == true;
        _frameMonitor = frame.Monitor;
        _frameElapsedMilliseconds = frame.ElapsedMilliseconds;
        _originalCropRegion = bitmapRegion.Normalized();

        _canvasWidth = Math.Max(1, selectedBitmap.PixelWidth);
        _canvasHeight = Math.Max(1, selectedBitmap.PixelHeight);

        // Seed decoded assets before wiring the renderer so restored image annotations draw
        // on the first paint.
        if (initialAssets is not null)
        {
            _imageStore.Seed(initialAssets);
        }

        AnnotationDocument document = initialDocument ?? AnnotationDocument.CreateFor(_canvasWidth, _canvasHeight);
        foreach (TextAnnotation text in document.Items.OfType<TextAnnotation>())
        {
            // A repair, not an edit: older versions cut text that outgrew its box.
            TextAnnotationLayout.GrowToText(text);
        }

        var undo = new UndoStack();
        _controller = new AnnotationEditorController(document, undo) { TextFitter = TextAnnotationLayout.Measure };
        _controller.BaseWidthAtZeroTurns = selectedBitmap.PixelWidth;
        _controller.BaseHeightAtZeroTurns = selectedBitmap.PixelHeight;
        AnnotationDefaults defaults = AnnotationEditorPreferences.Read?.Invoke() ?? new AnnotationDefaults();
        _controller.StrokeColor = defaults.StrokeColor;
        // The settings contract is the editor's range: a width saved in settings is the width drawn.
        _controller.StrokeThickness = double.IsFinite(defaults.StrokeThickness)
            ? Math.Clamp(defaults.StrokeThickness, SettingsRanges.StrokeThickness.Min, SettingsRanges.StrokeThickness.Max) : 3;
        _controller.ApplyStrokeStyle(Enum.IsDefined(defaults.StrokeStyle) ? defaults.StrokeStyle : AnnotationStrokeStyle.Solid);
        _controller.ApplyFillTransparency(double.IsFinite(defaults.FillTransparency) ? Math.Clamp(defaults.FillTransparency, 0, 100) : 100);
        _controller.DefaultFontSize = double.IsFinite(defaults.FontSize)
            ? Math.Clamp(defaults.FontSize, SettingsRanges.FontSize.Min, SettingsRanges.FontSize.Max) : 18;
        _controller.DefaultFontFamily = string.IsNullOrWhiteSpace(defaults.FontFamily) ? "Malgun Gothic" : defaults.FontFamily;
        _defaultTextBold = defaults.TextBold;
        _defaultTextBackground = defaults.TextBackground;
        _defaultTextOutline = defaults.TextOutline;
        _recentFontFamilies.AddRange(defaults.RecentFontFamilies ?? []);
        _controller.ApplyBlurRadius(defaults.BlurRadius);
        _mosaicBlockSize = defaults.MosaicBlockSize;
        if (Enum.TryParse(defaults.LastTool, out EditorTool lastTool) && Enum.IsDefined(lastTool)) _preferredTool = lastTool == EditorTool.Crop ? EditorTool.Select : lastTool;
        _imageStore.PruneToReachable(document, undo);
        _blurCache = new AnnotationBlurCache(Dispatcher);
        var renderer = new AnnotationRenderer(_imageStore, _blurCache);

        var visualRegion = new RectD(0, 0, _canvasWidth, _canvasHeight);
        var visualFrame = new FrozenFrame(
            selectedBitmap,
            visualRegion,
            frame.Monitor,
            frame.ElapsedMilliseconds);
        _surface = new AnnotationEditorSurface(visualFrame, visualRegion, _controller, renderer)
        {
            SourceWindowTitle = _showSourceWindowTitle ? _sourceWindowTitle : null,
        };
        _blurCache.FrameReady += (_, _) => _surface.InvalidateVisual();

        SetResourceReference(BackgroundProperty, "Surface.Base");
        Focusable = true;
        FocusVisualStyle = null;
        BuildShortcutActions();
        BuildLayout();
        DisableImeOutsideTextInputs(this);
        // Second route for Hangul-mode shortcuts: jamo an IME still composes outside a text input.
        _hangulRelay = new HangulShortcutRelay(this, () => !IsTextInputFocused(), HandleShortcut, ShowHangulShortcutNotice);
        // Tool cursors follow the user's Windows pointer size and colour.
        _cursorBaseSize = EditorCursors.ReadBaseSize();
        _cursorStyle = EditorCursorStyle.Read();

        _controller.SelectionChanged += (_, _) => OnSelectionChanged();
        undo.Changed += (_, _) => OnHistoryChanged();

        _viewport.MouseLeftButtonDown += OnSurfaceMouseDown;
        _viewport.MouseMove += OnSurfaceMouseMove;
        _viewport.MouseLeftButtonUp += OnSurfaceMouseUp;
        _viewport.MouseRightButtonDown += OnSurfaceRightDown;
        _viewport.LostMouseCapture += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, _viewport) || _viewport.IsMouseCaptured) return;
            _edgeCropStart = null;
            _controller.CancelGesture();
            if (_crop is { IsDragging: true }) { _crop.End(); RefreshCrop(); }
        };

        SizeChanged += (_, _) => UpdateResponsiveLayout();

        Loaded += (_, _) =>
        {
            UpdateResponsiveLayout();
            Focus();
        };
        Unloaded += (_, _) =>
        {
            _autoTrimCts?.Cancel();
            _redactionCts?.Cancel();
            _redactionCts?.Dispose();
            _redactionCts = null;
            _hangulNoticeTimer?.Stop();
            _blurCache.Clear();
        };

        SelectTool(_preferredTool);
        _preferencesReady = true;
        RefreshHistoryButtons();
        UpdateInspector();
    }

    /// <summary>Raised when a commit that closes the editor succeeded (Done, Save, Save As).</summary>
    internal event EventHandler<AnnotationEditingResult>? EditingCompleted;

    /// <summary>
    /// Raised when the user asks to close from inside the editor (the Cancel command). The
    /// host window confirms unsaved edits exactly as it does for its title bar close button.
    /// </summary>
    internal event EventHandler? CloseRequested;

    /// <summary>
    /// Invoked when the user asks to commit. The handler flattens and performs what the
    /// action requires (see <see cref="EditorCommitAction"/>) and returns whether it
    /// succeeded. For a closing action success closes the editor and <see langword="false"/>
    /// (a cancelled or failed save) leaves it open; Copy never closes, and the result only
    /// decides which outcome the status line reports.
    /// </summary>
    internal Func<AnnotationEditingResult, Task<bool>>? CommitRequested { get; set; }

    /// <summary>
    /// Whether closing now would lose work: any step on the undo stack (every edit, rotation
    /// and crop records one), a caption still being typed, or a crop frame not yet applied.
    /// Undoing back to the opened state counts as no change.
    /// </summary>
    internal bool HasUnsavedChanges
    {
        get
        {
            if (_completed) return false;
            int steps = _controller.Undo.UndoCount;
            // An empty caption is about to vanish on its own; only its placeholder add is recorded.
            if (_activeTextBox is { } box && _editingText is { } text)
            {
                if (_controller.IsCreatingText && string.IsNullOrEmpty(box.Text)) steps--;
                else if (!string.Equals(box.Text, text.Text, StringComparison.Ordinal)) return true;
            }
            return steps > 0 || _crop is { } crop && crop.Region != crop.Bounds;
        }
    }

    /// <summary>Saves through the normal Done commit, which closes the editor once it succeeds.</summary>
    internal void SaveAndClose() => Commit(EditorCommitAction.Done);

    /// <summary>
    /// Tells the user, as the editor opens after a capture, that the capture has already been
    /// saved to the library and, when it was, copied to the clipboard.
    /// </summary>
    /// <remarks>Called by the capture flow only after both results are known.</remarks>
    internal void ShowCaptureReceipt(bool copiedToClipboard)
    {
        if (_completed || _commitInProgress) return;
        SetStatus(UiText.Get(copiedToClipboard ? "Capture.CopiedAndSaved" : "Capture.Saved"));
    }

    /// <summary>
    /// Says in the status line why a capture command the user just gave did not start. It replaces
    /// even the typing hint: the command came from outside the editor and its answer is shown here.
    /// </summary>
    internal void ShowCaptureBlocked(string message)
    {
        if (_completed || _statusText is null) return;
        _statusText.Text = message;
        RaiseStatusAutomationEvent();
    }

    internal BitmapSource DisplayedBitmap => _surface.Frame.Bitmap;

    /// <summary>Copies the same canvas, transforms, assets and title used by editor commits.</summary>
    internal Task<BitmapSource> RenderCurrentImageAsync()
    {
        Dispatcher.VerifyAccess();
        AnnotationDocument snapshot = CreateCurrentImageSnapshot();
        var assets = _imageStore.DecodedFor(snapshot.Items.OfType<ImageAnnotation>().Select(item => item.AssetFileName));
        BitmapSource bitmap = _surface.Frame.Bitmap;
        string? title = _showSourceWindowTitle ? _sourceWindowTitle : null;
        return Threading.StaThreadTask.RunAsync(() => CaptureCommitService.Flatten(bitmap, snapshot, assets, title),
            "MyCapture Quick Access renderer");
    }

    internal RectD DisplayedRegion => _surface.CropRegion;

    /// <summary>
    /// The element that owns both pointer event handlers and mouse capture. Keeping these
    /// responsibilities on one element prevents WPF from rerouting drag move/up events to an
    /// ancestor and leaving a zero-size draft in the document.
    /// </summary>
    internal UIElement PointerInputElement => _viewport;

    internal bool IsPointerCaptured => _viewport.IsMouseCaptured;

    internal bool IsCommitInProgress => _commitInProgress && !_completed;
    internal bool IsEditingOperationInProgress => IsCommitInProgress || _redactionInProgress;

    internal bool CapturePointer() => !_simulatingPointer && _viewport.CaptureMouse();

    internal void ReleasePointer()
    {
        if (_viewport.IsMouseCaptured)
        {
            _viewport.ReleaseMouseCapture();
        }
    }

    // ---- Keyboard ------------------------------------------------------------------

    internal bool HandleKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        // Hangul mode reports letters as ImeProcessed. Outside a text input they are still
        // shortcuts; inside one the IME keeps the key so a composition and its Esc stay intact.
        // Handling the key here, in the preview stage, also keeps it from the IME: WPF passes an
        // unhandled ImeProcessed key to the text services and then never raises KeyDown.
        bool textInput = IsTextInputFocused();
        Key key = ShortcutInput.Resolve(e.Key, e.ImeProcessedKey, textInput);
        bool handled = HandleShortcut(key, Keyboard.Modifiers);
        _hangulRelay.NoteKeyDown(e.Key, key, handled);
        if (!handled && ShortcutInput.IsUnidentifiedImeKey(e.Key, e.ImeProcessedKey, textInput))
        {
            // The IME did not say which key it took. If it composes jamo instead, the relay runs
            // the shortcut; only when nothing arrives is the user told why the key did nothing.
            int activity = _hangulRelay.Activity;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_hangulRelay.Activity == activity) ShowHangulShortcutNotice();
            });
        }

        return handled;
    }

    private bool IsTextInputFocused() => _activeTextBox is not null || Keyboard.FocusedElement is TextBoxBase;

    /// <summary>
    /// Tells the user, briefly, that a key typed in Hangul mode could not be read as a shortcut.
    /// </summary>
    /// <remarks>
    /// Shown only when the IME took a key without identifying it and composed nothing either;
    /// every letter it reports, directly or as jamo, already runs its shortcut. Repeats within
    /// a few seconds are dropped so key presses do not re-announce the same sentence.
    /// </remarks>
    private void ShowHangulShortcutNotice()
    {
        if (_completed || _statusText is null) return;
        long now = Environment.TickCount64;
        if (_hangulNoticeShownAt is long last && now - last < HangulNoticeRepeatMilliseconds) return;
        _hangulNoticeShownAt = now;
        SetStatus(UiText.Get("EditorUx_HangulShortcutNotice"));
        if (_hangulNoticeTimer is null)
        {
            _hangulNoticeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = MyCapture.App.Themes.FeedbackTiming.Result,
            };
            _hangulNoticeTimer.Tick += (_, _) =>
            {
                _hangulNoticeTimer.Stop();
                if (string.Equals(_statusText.Text, UiText.Get("EditorUx_HangulShortcutNotice"), StringComparison.Ordinal))
                {
                    // The status line holds results; the notice gives way to an empty line.
                    SetStatus(string.Empty);
                }
            };
        }

        _hangulNoticeTimer.Stop();
        _hangulNoticeTimer.Start();
    }

    private const int HangulNoticeRepeatMilliseconds = 3000;

    /// <summary>The status line text, for tests.</summary>
    internal string StatusText => _statusText.Text;

    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (_commitInProgress) return true;
        if (_activeTextBox is not null)
        {
            string? command = ShortcutCatalog.Match(ShortcutScope.Annotation, key, modifiers);
            if (command == "Cancel") { FinishActiveText(cancel: true); return true; }
            if (command == "Done") { CommitActiveText(); Commit(EditorCommitAction.Done); return true; }
            return false;
        }
        if (_crop is not null && Keyboard.FocusedElement is TextBox cropInput
            && (ReferenceEquals(cropInput, _cropWidth) || ReferenceEquals(cropInput, _cropHeight))
            && modifiers == ModifierKeys.None)
        {
            if (key == Key.Escape) { CancelPendingCrop(); Focus(); return true; }
            if (key == Key.Enter) { ChangeCropSize(cropInput); return true; }
        }
        if (Keyboard.FocusedElement is ButtonBase && key is Key.Space or Key.Enter
            && modifiers == ModifierKeys.None) return false;
        if (Keyboard.FocusedElement is RangeBase && key is Key.Left or Key.Right or Key.Up or Key.Down)
            return false;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem
            && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return false;
        if (_crop is not null && ShortcutCatalog.Execute(ShortcutScope.Crop, key, modifiers, _cropActions)) return true;
        if (Keyboard.FocusedElement is TextBox box
            && !(_crop is not null && (ReferenceEquals(box, _cropWidth) || ReferenceEquals(box, _cropHeight))
                && ShortcutCatalog.Match(ShortcutScope.Annotation, key, modifiers) is "Done" or "Save" or "SaveAs")) return false;
        return ShortcutCatalog.Execute(ShortcutScope.Annotation, key, modifiers, _shortcutActions);
    }

    private void BuildShortcutActions()
    {
        foreach (EditorTool tool in Enum.GetValues<EditorTool>()) _shortcutActions[tool.ToString()] = () => SelectTool(tool);
        _shortcutActions["Done"] = () => Commit(EditorCommitAction.Done);
        // Copying is how people finish an edit, so Copy saves, copies and closes like Done.
        _shortcutActions["Copy"] = () => Commit(EditorCommitAction.Done);
        _shortcutActions["Save"] = () => Commit(EditorCommitAction.QuickSave);
        _shortcutActions["SaveAs"] = () => Commit(EditorCommitAction.SaveAs);
        _shortcutActions["Privacy"] = () => _ = ApplyPrivacyRedactionsAsync();
        // Esc steps back; it is never a way out of the editor (closing asks first instead).
        _shortcutActions["Cancel"] = StepBack;
        _shortcutActions["Undo"] = () => { if (_controller.PerformUndo()) SetStatus(UiText.Get("Text_DAF93C3AA95D")); };
        _shortcutActions["Redo"] = () => { if (_controller.PerformRedo()) SetStatus(UiText.Get("Text_8B22B7BBB0CD")); };
        _shortcutActions["Delete"] = DeleteSelected;
        _shortcutActions["SelectAll"] = () => { SelectTool(EditorTool.Select); _controller.SelectAll(); };
        _shortcutActions["Duplicate"] = _controller.DuplicateSelected;
        _shortcutActions["BringForward"] = () => _controller.ReorderSelected(forward: true);
        _shortcutActions["SendBackward"] = () => _controller.ReorderSelected(forward: false);
        _shortcutActions["EditText"] = () =>
        {
            if (_controller.BeginSelectedTextEdit() is { } text) OpenTextBox(text);
        };
        _shortcutActions["RotateLeft"] = () => RotateCapture(-1);
        _shortcutActions["RotateRight"] = () => RotateCapture(1);
        _cropActions["Apply"] = ApplyPendingCrop;
        _cropActions["Cancel"] = CancelPendingCrop;
        foreach (var (name, x, y) in new[] { ("Left", -1, 0), ("Right", 1, 0), ("Up", 0, -1), ("Down", 0, 1) })
        {
            _cropActions[name] = () => { _crop?.Nudge(x, y); RefreshCrop(); };
            _cropActions[name + "Fast"] = () => { _crop?.Nudge(x * 10, y * 10); RefreshCrop(); };
            _shortcutActions[name] = () => _controller.NudgeSelected(x, y);
            _shortcutActions[name + "Fast"] = () => _controller.NudgeSelected(x * 10, y * 10);
        }
    }

    internal IReadOnlyDictionary<string, Action> CropShortcutActions => _cropActions;
    internal void ApplyPendingCrop()
    {
        if (_crop is not { } crop) return;
        if (!CommitCropInputs()) return;
        if (crop.Region.Width < 2 || crop.Region.Height < 2) { SetStatus(UiText.Get("Crop.TooSmall")); return; }
        RectD region = crop.Region;
        _crop = null; _surface.PendingCrop = null;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        ReleasePointer();
        ApplyCrop(region);
        SelectTool(EditorTool.Select);
        SetStatus(UiText.Get("Crop.Applied"));
    }
    internal void CancelPendingCrop()
    {
        if (_crop is null) return;
        _crop = null; _surface.PendingCrop = null;
        ReleasePointer(); _surface.InvalidateVisual();
        SelectTool(EditorTool.Select);
        SetStatus(UiText.Get("Crop.Cancelled"));
    }
    private void RefreshCrop()
    {
        DraftChanged?.Invoke(this, EventArgs.Empty);
        _surface.PendingCrop = _crop;
        _surface.InvalidateVisual();
        UpdateCommandMode();
        if (_crop is null)
        {
            _dirtyCropInputs.Clear();
            foreach (var box in _cropInputErrors.Keys) SetCropInputError(box, false);
            return;
        }
        if (_cropWidth is null) return;
        _syncingInspector = true;
        RectD pixels = _crop.Region.ToPixelBounds();
        if (!_dirtyCropInputs.Contains(_cropWidth)) _cropWidth.Text = pixels.Width.ToString(CultureInfo.InvariantCulture);
        if (!_dirtyCropInputs.Contains(_cropHeight)) _cropHeight.Text = pixels.Height.ToString(CultureInfo.InvariantCulture);
        _syncingInspector = false;
    }

    // ---- Rotation ------------------------------------------------------------------

    /// <summary>
    /// Rotates the capture and its annotation layer by <paramref name="quarterTurns"/>
    /// clockwise quarter turns (negative for counter-clockwise) as one undoable step.
    /// </summary>
    /// <remarks>
    /// The document rotation goes through the controller's undo stack. The base bitmap is
    /// derived state, so it is recomputed from the current rotation counter whenever the
    /// undo stack changes ??undoing a rotation then restores the original bitmap without a
    /// second command type.
    /// </remarks>
    private void RotateCapture(int quarterTurns)
    {
        if (_completed || _commitInProgress || quarterTurns == 0)
        {
            return;
        }

        CommitActiveText();
        _controller.RotateDocument(quarterTurns);
        SetStatus(UiText.Get("Text_6B2C41A9E531"));
    }

    private void SwapBaseBitmap(BitmapSource bitmap, RectD region)
    {
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        var frame = new FrozenFrame(bitmap, region, _frameMonitor, _frameElapsedMilliseconds);
        _surface.ReplaceFrame(frame, region);
        _canvasWidth = Math.Max(1, bitmap.PixelWidth);
        _canvasHeight = Math.Max(1, bitmap.PixelHeight);
        UpdateResponsiveLayout();
    }

    private static BitmapSource RotateQuarterClockwise(BitmapSource source)
    {
        var rotated = new TransformedBitmap(source, new RotateTransform(90));
        if (rotated.CanFreeze)
        {
            rotated.Freeze();
        }
        return rotated;
    }

    // ---- Pointer -------------------------------------------------------------------

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SurfacePointerDown(e.GetPosition(_surface), e.ClickCount,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) e.Handled = true;
    }

    /// <summary>
    /// Plays a canvas gesture from code, at <paramref name="viewportPoint"/> in
    /// <see cref="PointerInputElement"/> coordinates, through the same handling as the mouse.
    /// The tutorial clip generator uses it; the real mouse is never captured meanwhile.
    /// </summary>
    internal void SimulatePointerDown(Point viewportPoint, int clickCount = 1, bool shift = false, bool control = false) =>
        Simulate(() => SurfacePointerDown(_viewport.TranslatePoint(viewportPoint, _surface), clickCount, shift, control));

    /// <inheritdoc cref="SimulatePointerDown"/>
    internal void SimulatePointerMove(Point viewportPoint, bool leftPressed, bool shift = false) =>
        Simulate(() => { SurfacePointerMove(_viewport.TranslatePoint(viewportPoint, _surface), leftPressed, shift); return true; });

    /// <inheritdoc cref="SimulatePointerDown"/>
    internal void SimulatePointerUp(Point viewportPoint, bool shift = false) =>
        Simulate(() => SurfacePointerUp(_viewport.TranslatePoint(viewportPoint, _surface), shift));

    /// <summary>Where image pixel <paramref name="image"/> shows, in <see cref="PointerInputElement"/> coordinates.</summary>
    internal Point ViewportPointOf(PointD image) => _surface.TranslatePoint(_surface.ToSurfacePoint(image), _viewport);

    /// <summary>The rail button that selects <paramref name="tool"/>.</summary>
    internal ToggleButton ToolButton(EditorTool tool) => _toolButtons[tool];

    private void Simulate(Func<bool> gesture)
    {
        _simulatingPointer = true;
        try { gesture(); }
        finally { _simulatingPointer = false; }
    }

    private bool _simulatingPointer;

    /// <summary>A left press on the canvas at <paramref name="dip"/> (surface coordinates); true when handled.</summary>
    private bool SurfacePointerDown(Point dip, int clickCount, bool shift, bool control = false)
    {
        if (_completed || _commitInProgress) return false;
        _autoTrimCts?.Cancel();
        if (_activeTextBox is not null)
        {
            CommitActiveText();
            return false;
        }

        Focus();
        PointD image = _surface.ToImagePoint(dip);

        if (!control && !(_controller.Tool == EditorTool.Select && shift)
            && _controller.BeginTextAtPointer(image, clickCount, shift) is { } text)
        {
            OpenTextBox(text);
            return true;
        }

        if (!(_controller.Tool == EditorTool.Select && (shift || control)) && TryBeginEdgeCrop(image))
        {
            CapturePointer();
            return true;
        }

        switch (_controller.Tool)
        {
            case EditorTool.Image:
                InsertImage(image);
                return true;
            case EditorTool.Mosaic:
                _mosaicDragging = true;
                _mosaicStart = image;
                CapturePointer();
                return true;
            case EditorTool.Crop:
                if (_crop is not null)
                {
                    if (clickCount == 2 && _crop.Contains(image)) { ApplyPendingCrop(); return true; }
                    double scale = Math.Max(double.Epsilon, _surface.DipPerPixel);
                    _crop.Begin(image, 7 / scale, SystemParameters.MinimumHorizontalDragDistance / scale,
                        SystemParameters.MinimumVerticalDragDistance / scale);
                    RefreshCrop();
                    CapturePointer();
                }
                return true;
            default:
                _controller.HitTolerance = 6 / Math.Max(double.Epsilon, _surface.DipPerPixel);
                _controller.PointerDown(image, shift, control);
                CapturePointer();
                return true;
        }
    }

    /// <summary>
    /// Pixels the captured content inside <paramref name="region"/> and adds the patch as
    /// a movable/resizable image annotation. Sampling the frozen capture keeps the patch
    /// in sync with rotation and crop, and making it an image (not a live effect) means
    /// the flattener, video layer pipeline and re-edit story need no new code path.
    /// </summary>
    internal void ApplyMosaic(RectD region, int blockSize)
    {
        RectD normalized = region.Normalized().ClampTo(new RectD(0, 0, _canvasWidth, _canvasHeight));
        int x = (int)Math.Floor(normalized.X);
        int y = (int)Math.Floor(normalized.Y);
        int width = (int)Math.Ceiling(normalized.Width);
        int height = (int)Math.Ceiling(normalized.Height);
        if (width < 2 || height < 2)
        {
            return;
        }

        int step = Math.Clamp(blockSize, 4, 64);
        // Sample the base the user currently sees. After a rotation that base is the
        // rotated bitmap; sampling the untouched selection would read coordinates from
        // the rotated canvas space out of (or beside) the unrotated pixels.
        BitmapSource mosaicBase = _surface.Frame.Bitmap;
        int stride = mosaicBase.PixelWidth * 4;
        byte[] source = new byte[stride * mosaicBase.PixelHeight];
        mosaicBase.CopyPixels(source, stride, 0);

        int patchStride = width * 4;
        byte[] patch = new byte[patchStride * height];

        // Box-average each block into a 1횞1 sample, then write the sample across the
        // block. Block averages (not nearest-neighbour picks) keep thin dark text visible
        // as a smear rather than vanishing into the surrounding colour.
        for (int by = 0; by < height; by += step)
        {
            int blockH = Math.Min(step, height - by);
            for (int bx = 0; bx < width; bx += step)
            {
                int blockW = Math.Min(step, width - bx);
                long sumB = 0, sumG = 0, sumR = 0, count = 0;
                for (int py = 0; py < blockH; py++)
                {
                    int sy = y + by + py;
                    int row = sy * stride;
                    for (int px = 0; px < blockW; px++)
                    {
                        int sx = x + bx + px;
                        int i = row + (sx * 4);
                        if (source[i + 3] == 0)
                        {
                            continue;
                        }

                        sumB += source[i];
                        sumG += source[i + 1];
                        sumR += source[i + 2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                byte b = (byte)(sumB / count);
                byte gByte = (byte)(sumG / count);
                byte r = (byte)(sumR / count);
                for (int py = 0; py < blockH; py++)
                {
                    int dy = by + py;
                    int row = dy * patchStride;
                    for (int px = 0; px < blockW; px++)
                    {
                        int di = row + ((bx + px) * 4);
                        patch[di] = b;
                        patch[di + 1] = gByte;
                        patch[di + 2] = r;
                        patch[di + 3] = 255;
                    }
                }
            }
        }

        var decoded = BitmapSource.Create(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            patch,
            patchStride);
        decoded.Freeze();

        (BitmapSource Bitmap, string AssetFileName) asset = _imageStore.RegisterInMemory(decoded);
        // Annotation rects are in image pixels, so the patch sits exactly on the pixels it
        // was sampled from. Scaling by the display zoom shrank it on enlarged small captures
        // and left part of the selected content uncovered.
        var rect = new RectD(x, y, width, height);
        _controller.AddImageAnnotation(asset.AssetFileName, width, height, rect);
        SelectTool(EditorTool.Select);
        SetStatus(UiText.Get("Text_3F8046019B34"));
    }

    internal CropGrip? EdgeCropGripAt(PointD image)
    {
        if (_crop is not null || _controller.Tool is not (EditorTool.Select or EditorTool.Crop)) return null;
        double scale = Math.Max(double.Epsilon, _surface.DipPerPixel);
        _controller.HitTolerance = 6 / scale;
        if (_controller.Tool == EditorTool.Select && (_controller.HandleAt(image, 8 / scale) != ResizeHandle.None
            || _controller.HitTest(image) is not null)) return null;
        return CropSelection.HitTestEdge(new RectD(0, 0, _canvasWidth, _canvasHeight), image, 7 / scale);
    }

    internal bool TryBeginEdgeCrop(PointD image)
    {
        if (_completed || _commitInProgress || _activeTextBox is not null || EdgeCropGripAt(image) is null)
            return false;
        _edgeCropStart = image;
        return true;
    }

    internal bool ContinueEdgeCrop(PointD image, bool shift)
    {
        if (_edgeCropStart is not PointD start) return false;
        double scale = Math.Max(double.Epsilon, _surface.DipPerPixel);
        if (Math.Abs(image.X - start.X) < SystemParameters.MinimumHorizontalDragDistance / scale
            && Math.Abs(image.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance / scale) return true;
        _edgeCropStart = null;
        SelectTool(EditorTool.Crop);
        _crop!.Begin(start, 7 / scale);
        _crop.Drag(image, shift);
        RefreshCrop();
        return true;
    }

    internal void CompleteEdgeCropGesture(PointD image, bool shift)
    {
        if (_edgeCropStart is not PointD start) return;
        ContinueEdgeCrop(image, shift);
        _edgeCropStart = null;
        // A click below the threshold still performs normal selection/deselection.
        if (_crop is null) _controller.PointerDown(start);
    }

    internal async Task PreviewAutomaticTrimAsync()
    {
        if (_autoTrimInProgress || _completed || _commitInProgress) return;
        CommitActiveText();
        var operation = new CancellationTokenSource();
        CancellationToken token = operation.Token;
        _autoTrimCts = operation;
        _autoTrimInProgress = true;
        _autoTrimButton.IsEnabled = false;
        BitmapSource original = DisplayedBitmap;
        long revision = _documentRevision;
        CropSelection? pending = _crop;
        RectD? pendingRegion = pending?.Region;
        SetStatus(UiText.Get("Capture410.AutoTrimWorking"));
        try
        {
            // Includes live annotations, title, and the current crop/rotation. Rendering
            // and pixel scanning are outside the capture acquisition/pointer path.
            BitmapSource rendered = await RenderCurrentImageAsync();
            RectD region = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                BitmapSource source = rendered.Format == PixelFormats.Bgra32 ? rendered
                    : new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
                int stride = checked(source.PixelWidth * 4);
                byte[] pixels = new byte[checked(stride * source.PixelHeight)];
                source.CopyPixels(pixels, stride, 0);
                return AutomaticMarginTrim.Find(pixels, source.PixelWidth, source.PixelHeight, stride, token);
            }, token);
            if (token.IsCancellationRequested || _completed || _commitInProgress || revision != _documentRevision
                || !ReferenceEquals(original, DisplayedBitmap) || !ReferenceEquals(pending, _crop)
                || pendingRegion != _crop?.Region || _dirtyCropInputs.Count != 0 || _activeTextBox is not null) return;
            if (region == new RectD(0, 0, _canvasWidth, _canvasHeight))
            {
                SetStatus(UiText.Get("Capture410.AutoTrimNoMargin"));
                return;
            }
            SelectTool(EditorTool.Crop);
            _crop!.SetRegion(region);
            _cropRatio.SelectedIndex = 0;
            RefreshCrop();
            SetStatus(UiText.Get("Capture410.AutoTrimPreview"));
            if (ReferenceEquals(Keyboard.FocusedElement, _autoTrimButton)) Focus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Automatic margin inspection failed: {0}", ex);
            if (!_completed && !token.IsCancellationRequested) SetStatus(UiText.Get("Capture410.AutoTrimFailed"));
        }
        finally
        {
            if (ReferenceEquals(_autoTrimCts, operation)) _autoTrimCts = null;
            operation.Dispose();
            _autoTrimInProgress = false;
            _autoTrimButton.IsEnabled = true;
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e) =>
        SurfacePointerMove(e.GetPosition(_surface), e.LeftButton == MouseButtonState.Pressed, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    private void SurfacePointerMove(Point dip, bool leftPressed, bool shift)
    {
        if (_activeTextBox is not null)
        {
            return;
        }

        PointD image = _surface.ToImagePoint(dip);

        if (_edgeCropStart is not null)
        {
            if (leftPressed) ContinueEdgeCrop(image, shift);
            else { _edgeCropStart = null; ReleasePointer(); }
            return;
        }
        if (_crop is not null)
        {
            if (leftPressed && _crop.IsDragging)
            {
                _crop.Drag(image, shift);
                RefreshCrop();
            }
            ApplyCursor(new EditorPointerTarget(CropGrip: _crop.HitTest(image, 7 / Math.Max(double.Epsilon, _surface.DipPerPixel))));
            return;
        }
        if (leftPressed)
        {
            _controller.PointerMove(image, shift);
        }
        else if (EdgeCropGripAt(image) is CropGrip edge)
        {
            ApplyCursor(new EditorPointerTarget(CropGrip: edge));
        }
        else if (_controller.Tool == EditorTool.Select)
        {
            UpdateCursor(image);
        }
        else
        {
            ApplyCursor(default);
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SurfacePointerUp(e.GetPosition(_surface), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) e.Handled = true;
    }

    /// <summary>A left release on the canvas at <paramref name="dip"/> (surface coordinates); true when handled.</summary>
    private bool SurfacePointerUp(Point dip, bool shift)
    {
        if (_activeTextBox is not null)
        {
            return false;
        }

        if (_edgeCropStart is not null)
        {
            CompleteEdgeCropGesture(_surface.ToImagePoint(dip), shift);
        }

        if (_mosaicDragging)
        {
            _mosaicDragging = false;
            ReleasePointer();
            PointD mosaicEnd = _surface.ToImagePoint(dip);
            ApplyMosaic(
                new RectD(_mosaicStart.X, _mosaicStart.Y, mosaicEnd.X - _mosaicStart.X, mosaicEnd.Y - _mosaicStart.Y),
                _mosaicBlockSize);
            return true;
        }

        if (_crop is { IsDragging: true })
        {
            _crop.Drag(_surface.ToImagePoint(dip), shift);
            _crop.End(); ReleasePointer(); RefreshCrop();
            return true;
        }

        EditorTool toolBeforeCommit = _controller.Tool;
        int itemsBefore = _controller.Document.Items.Count;

        PointD image = _surface.ToImagePoint(dip);
        _controller.PointerUp(image, shift);
        ReleasePointer();
        SyncToolButtons();

        // A gesture that produced a new object is worth announcing; a bare click that did not
        // is not. Pencil strokes intentionally stay unselected to avoid an interrupting
        // selection polygon, so report them from the tool that began the gesture.
        if (_controller.Document.Items.Count > itemsBefore)
        {
            if (toolBeforeCommit == EditorTool.Pen)
            {
                SetStatus(UiText.Format("EditorFlow_Added", _controller.Document.Items[^1].DisplayName));
            }
            else if (_controller.Selected is { } selected)
            {
                SetStatus(UiText.Format("EditorFlow_Added", selected.DisplayName));
            }
        }

        return false;
    }

    private void OnSurfaceRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTextBox is not null)
        {
            CommitActiveText();
        }

        _controller.CancelGesture();
        _controller.SetSelected(null);
        SelectTool(EditorTool.Select);
        e.Handled = true;
    }

    private void UpdateCursor(PointD image)
    {
        double tolerance = 8 / Math.Max(double.Epsilon, _surface.DipPerPixel);
        ResizeHandle handle = _controller.HandleAt(image, tolerance);
        ApplyCursor(new EditorPointerTarget(handle, handle == ResizeHandle.None && _controller.HitTest(image) is not null));
    }

    /// <summary>The cursor the canvas shows for the active tool and <paramref name="target"/>.</summary>
    /// <remarks>
    /// Set on the image viewport only, so the tool rail, command bar and inspector keep the
    /// normal pointer while a tool is active.
    /// </remarks>
    private void ApplyCursor(EditorPointerTarget target)
    {
        _pointerTarget = target;
        EditorCursorKind kind = EditorCursorPolicy.Resolve(_controller.Tool, target);
        Cursor cursor = EditorCursors.Get(
            kind,
            EditorCursors.PixelSize(_cursorBaseSize, VisualTreeHelper.GetDpi(this).DpiScaleX),
            kind == EditorCursorKind.StepNumber ? _controller.NextStepNumber : 0,
            _controller.StrokeColor,
            SystemParameters.HighContrast || ThemeService.Current == MyCapture.Core.Themes.AppTheme.HighContrast,
            _cursorStyle);
        if (!ReferenceEquals(_viewport.Cursor, cursor)) _viewport.Cursor = cursor;
    }

    /// <summary>The cursor kind the canvas shows now, for tests.</summary>
    internal EditorCursorKind CanvasCursorKind => EditorCursorPolicy.Resolve(_controller.Tool, _pointerTarget);

    /// <summary>The cursor object the canvas shows now, for tests.</summary>
    internal Cursor? CanvasCursor => _viewport.Cursor;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyCursor(_pointerTarget);
    }

    // ---- Text entry ----------------------------------------------------------------

    private void PlaceTextBox(PointD image)
    {
        double em = _controller.DefaultFontSize;
        OpenTextBox(_controller.BeginTextAnnotation(image, em, em * 1.4));
    }

    private void OpenTextBox(TextAnnotation annotation)
    {
        if (_controller.IsCreatingText)
        {
            annotation.Bold = _defaultTextBold;
            annotation.Background = _defaultTextBackground ? ColorRgba.Black : ColorRgba.Transparent;
            annotation.HasOutline = _defaultTextOutline;
            annotation.OutlineStyle = TextOutlineStyle.Consistent;
            TextLayout.Apply(annotation, _controller.FitText(annotation, annotation.Text,
                annotation.FontSize, annotation.AutoSize, annotation.Rect));
        }
        _editingText = annotation;

        double dipPerPixel = Math.Max(double.Epsilon, _surface.DipPerPixel);
        ColorRgba foreground = annotation.Foreground;
        double luma = 0.299 * foreground.R + 0.587 * foreground.G + 0.114 * foreground.B;
        var textBox = new TextBox
        {
            Text = annotation.Text,
            // Preserve fractional image-scaled widths so live wrapping matches FormattedText.
            UseLayoutRounding = false,
            MinWidth = 0,
            MinHeight = 0,
            FontFamily = new FontFamily(annotation.FontFamily),
            FontSize = Math.Max(1, annotation.FontSize * dipPerPixel),
            FontWeight = annotation.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = annotation.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = annotation.Foreground.ToBrush(),
            // TextBox does not draw the caption's outline; keep unplated text readable while editing.
            Background = annotation.Background.A > 0 ? annotation.Background.ToBrush()
                : luma > 140 ? Brushes.Black : Brushes.White,
            Opacity = annotation.Opacity,
            BorderBrush = Brush("Border.Control", Color.FromRgb(0x56, 0x67, 0x7F)),
            BorderThickness = new Thickness(1),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 2, 4, 2),
        };
        // The committed caption is drawn with ideal glyph metrics (FormattedText). The editor
        // window's display-mode text would space and wrap the live copy differently.
        TextOptions.SetTextFormattingMode(textBox, TextFormattingMode.Ideal);
        AutomationName(textBox, UiText.Get("Text_FAC754F72C25"));
        InputMethod.SetIsInputMethodEnabled(textBox, true);

        // Border, padding and the 2 DIP the text view keeps on each side; replaced by the
        // laid-out value below.
        _liveTextInset = new Point(
            textBox.BorderThickness.Left + textBox.Padding.Left + 2,
            textBox.BorderThickness.Top + textBox.Padding.Top);
        _overlayCanvas.Children.Add(textBox);
        _activeTextBox = textBox;
        SizeChangedEventHandler resize = (_, _) => UpdateLiveTextViewport(textBox);
        ScrollChangedEventHandler scroll = (_, e) =>
        {
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
                UpdateLiveTextViewport(textBox);
        };
        ScrollViewer? contentHost = null;
        bool connected = false;
        void ConnectViewport()
        {
            if (connected) return;
            connected = true;
            _overlayCanvas.SizeChanged += resize;
            textBox.ApplyTemplate();
            contentHost = textBox.Template.FindName("PART_ContentHost", textBox) as ScrollViewer;
            if (contentHost is not null)
            {
                // Set before the host first applies a template, so the text view is never re-parented
                // once the user types or composes.
                contentHost.Template = CaptionScrollHostTemplate.Value;
                contentHost.ApplyTemplate();
                contentHost.ScrollChanged += scroll;
            }
            UpdateLiveTextViewport(textBox);
        }
        textBox.Loaded += (_, _) => ConnectViewport();
        textBox.Unloaded += (_, _) =>
        {
            if (!connected) return;
            connected = false;
            _overlayCanvas.SizeChanged -= resize;
            if (contentHost is not null) contentHost.ScrollChanged -= scroll;
            contentHost = null;
        };
        LayoutLiveTextBox(textBox);
        ConnectViewport();
        MeasureLiveTextInset(textBox);
        SyncToolButtons();
        SetStatus(UiText.Get(_controller.IsCreatingText ? "EditorText_Entering" : "EditorText_Editing"));
        UpdateInspector();

        textBox.TextChanged += OnLiveTextBoxTextChanged;
        // A box placed by a simulated gesture lives in a window that never takes keyboard focus;
        // it commits on the next canvas press instead.
        bool simulated = _simulatingPointer;
        textBox.LostKeyboardFocus += (_, _) => { if (!simulated) CommitActiveText(); };
        _ = textBox.Focus();
        textBox.CaretIndex = textBox.Text.Length;
    }

    /// <summary>
    /// Replaces the estimated inset with the laid-out one so the first glyph sits exactly
    /// where the committed caption draws it.
    /// </summary>
    private void MeasureLiveTextInset(TextBox box)
    {
        if (PresentationSource.FromVisual(box) is null)
        {
            return;
        }

        box.UpdateLayout();
        Rect first = box.GetRectFromCharacterIndex(0);
        if (first.IsEmpty || !double.IsFinite(first.X) || !double.IsFinite(first.Y))
        {
            return;
        }

        var host = box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
        var bar = host?.Template?.FindName("PART_VerticalScrollBar", host) as ScrollBar;
        // Reopened captions may already overflow when measured. The left scrollbar is
        // a separate slot, not text padding, and UpdateLiveTextViewport adds it once.
        double leftBar = bar is { Visibility: Visibility.Visible } && Grid.GetColumn(bar) == 0 ? bar.ActualWidth : 0;
        _liveTextInset = new Point(first.X - leftBar, first.Y);
        LayoutLiveTextBox(box);
    }

    private void OnLiveTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        DraftChanged?.Invoke(this, EventArgs.Empty);
        if (sender is TextBox box)
        {
            LayoutLiveTextBox(box);
        }
    }

    /// <summary>
    /// Sizes and places the live box from the fit the commit will store: the first glyph at
    /// the caption's top-left, each paragraph on one line while it fits, and wrapping at
    /// exactly the committed width once the text reaches the image's right edge.
    /// </summary>
    private void LayoutLiveTextBox(TextBox box)
    {
        if (_editingText is not { } editing)
        {
            return;
        }

        double dipPerPixel = Math.Max(double.Epsilon, _surface.DipPerPixel);
        TextLayout fit = LiveTextLayout(editing, box.Text ?? string.Empty);
        Rect target = _surface.ToSurfaceRect(fit.Rect);
        double chrome = 2 * _liveTextInset.X;
        box.MinWidth = chrome + Math.Max(1, editing.FontSize * dipPerPixel);
        if (fit.AutoSize)
        {
            box.TextWrapping = TextWrapping.NoWrap;
            // Room for the caret after the last glyph, so the line never scrolls sideways.
            _liveTextWidth = target.Width + chrome + 2;
        }
        else
        {
            box.TextWrapping = TextWrapping.Wrap;
            _liveTextWidth = target.Width + chrome;
        }

        // UpdateLiveTextViewport places the box horizontally, once the bar's side is known.
        _liveTextLeft = target.Left - _liveTextInset.X;
        Canvas.SetTop(box, target.Top - _liveTextInset.Y);
        box.RenderTransform = new RotateTransform(editing.RotationDegrees,
            target.Width / 2 + _liveTextInset.X, target.Height / 2 + _liveTextInset.Y);
        UpdateLiveTextViewport(box);
    }

    private TextLayout LiveTextLayout(TextAnnotation text, string content) =>
        string.Equals(text.Text, content, StringComparison.Ordinal)
            ? TextLayout.Of(text)
            : _controller.FitText(text, content, text.FontSize, text.AutoSize, text.Rect);

    internal enum CaptionScrollBarSide { Right, Left, Overlay }

    /// <summary>
    /// Where the live caption's vertical bar stays visible. The text viewport neither moves nor
    /// narrows (its width is the committed wrapping width), so the bar takes the side with room
    /// inside the visible canvas: right of the caption, else left of it, else over its right edge
    /// when the caption spans the whole width.
    /// </summary>
    internal static CaptionScrollBarSide ChooseCaptionScrollBarSide(double boxLeft, double boxWidth, double barWidth, double visibleWidth) =>
        barWidth <= 0 || !(visibleWidth > 0) || boxLeft + boxWidth + barWidth <= visibleWidth + 0.5 ? CaptionScrollBarSide.Right
        : boxLeft - barWidth >= -0.5 ? CaptionScrollBarSide.Left
        : CaptionScrollBarSide.Overlay;

    /// <summary>
    /// The live caption's scroll host: the text viewport between two bar slots, so the vertical bar
    /// can sit right of the caption, left of it, or over its right edge (see
    /// <see cref="ChooseCaptionScrollBarSide"/>) without changing the text view or its width. The
    /// bar keeps the application's ScrollBar style. Sealed once, so any editor thread may use it.
    /// </summary>
    private static readonly Lazy<ControlTemplate> CaptionScrollHostTemplate = new(() =>
    {
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            """
            <ControlTemplate TargetType="ScrollViewer"
                             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid Background="{TemplateBinding Background}">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Grid.Column="1"
                                            Margin="{TemplateBinding Padding}"
                                            CanContentScroll="{TemplateBinding CanContentScroll}"
                                            Content="{TemplateBinding Content}"
                                            ContentTemplate="{TemplateBinding ContentTemplate}" />
                    <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="2" Orientation="Vertical" Cursor="Arrow"
                               AutomationProperties.AutomationId="VerticalScrollBar"
                               Maximum="{TemplateBinding ScrollableHeight}"
                               ViewportSize="{TemplateBinding ViewportHeight}"
                               Value="{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"
                               Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}" />
                </Grid>
            </ControlTemplate>
            """);
        template.Seal();
        return template;
    });

    private void UpdateLiveTextViewport(TextBox box)
    {
        if (!ReferenceEquals(_activeTextBox, box)) return;
        // Canvas measures its children with infinite height. Keep the editing viewport inside
        // the visible canvas so TextBoxView formats and draws visible lines, while FitText and
        // the committed annotation continue to measure and retain the complete document.
        double height = _overlayCanvas.ActualHeight;
        if (height > 0)
        {
            double minimum = box.FontSize * 1.4 + box.Padding.Top + box.Padding.Bottom
                + box.BorderThickness.Top + box.BorderThickness.Bottom;
            box.MaxHeight = Math.Max(minimum, height - Canvas.GetTop(box));
        }
        var host = box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
        var bar = host?.Template?.FindName("PART_VerticalScrollBar", host) as ScrollBar;
        double scrollWidth = 0;
        if (host?.ComputedVerticalScrollBarVisibility == Visibility.Visible && bar is not null)
        {
            scrollWidth = bar.ActualWidth > 0 ? bar.ActualWidth
                : double.IsFinite(bar.Width) ? bar.Width : SystemParameters.VerticalScrollBarWidth;
        }
        // The scrollbar occupies chrome, not the caption's wrapping width. Keep the same text
        // viewport width and place as FitText, so neither scrolling nor the bar's side can change
        // the saved line breaks or move a glyph; the bar takes a side the canvas does not clip.
        double visibleWidth = _overlayCanvas.ActualWidth;
        CaptionScrollBarSide side = ChooseCaptionScrollBarSide(_liveTextLeft, _liveTextWidth, scrollWidth, visibleWidth);
        if (bar is not null)
        {
            bool overlay = side == CaptionScrollBarSide.Overlay;
            Grid.SetColumn(bar, side switch { CaptionScrollBarSide.Left => 0, CaptionScrollBarSide.Overlay => 1, _ => 2 });
            bar.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            // Over the right edge, pull the bar in from the box's clipped chrome to the canvas edge.
            double clipped = _liveTextLeft + _liveTextWidth - box.BorderThickness.Right - visibleWidth;
            bar.Margin = overlay && clipped > 0 ? new Thickness(0, 0, clipped, 0) : default;
        }
        box.Width = _liveTextWidth + (side == CaptionScrollBarSide.Overlay ? 0 : scrollWidth);
        Canvas.SetLeft(box, _liveTextLeft - (side == CaptionScrollBarSide.Left ? scrollWidth : 0));
    }

    internal AnnotationDocument CreateCurrentImageSnapshot()
    {
        Dispatcher.VerifyAccess();
        if (_redactionInProgress)
            throw UserFacingError.Mark(new InvalidOperationException(UiText.Get("Text_D0FD12A01E4A")));
        AnnotationDocument snapshot = CaptureCommitService.CreatePersistenceSnapshot(_controller.Document);
        if (_activeTextBox is { } box && _editingText is { } editing
            && snapshot.Items.OfType<TextAnnotation>().FirstOrDefault(item => item.Id == editing.Id) is { } text)
        {
            if (string.IsNullOrEmpty(box.Text)) snapshot.Remove(text);
            else
            {
                // Exactly what committing now would store.
                text.Text = box.Text;
                TextLayout.Apply(text, LiveTextLayout(editing, box.Text));
            }
        }
        snapshot.NormalizeZIndices();
        return snapshot;
    }

    private void CommitActiveText() => FinishActiveText(cancel: false);

    private void FinishActiveText(bool cancel)
    {
        if (_activeTextBox is null || _editingText is null)
        {
            return;
        }

        TextBox box = _activeTextBox;
        TextAnnotation annotation = _editingText;
        _activeTextBox = null;
        _editingText = null;
        // Esc, Ctrl+Enter and a canvas click finish inside the box; give the keyboard back
        // to the canvas so the next letter is a shortcut. A commit caused by focus moving
        // elsewhere (a slider, a swatch) leaves that new focus alone.
        bool hadKeyboardFocus = box.IsKeyboardFocusWithin;

        box.TextChanged -= OnLiveTextBoxTextChanged;
        _overlayCanvas.Children.Remove(box);

        bool hadText = !string.IsNullOrEmpty(box.Text);
        bool creating = _controller.IsCreatingText;
        if (cancel) _controller.CancelTextEdit(annotation);
        else _controller.CommitTextEdit(annotation, box.Text ?? string.Empty);
        SetStatus(cancel ? UiText.Get("EditorText_Cancelled")
            : !hadText ? UiText.Get("Text_8BDB6F46B7FF")
            : creating ? UiText.Format("EditorFlow_Added", UiText.Get("Text_258AD4B095A1"))
            : UiText.Get("EditorText_Updated"));
        DraftChanged?.Invoke(this, EventArgs.Empty);
        UpdateInspector();
        if (hadKeyboardFocus)
        {
            Focus();
        }
    }

    /// <summary>
    /// Crops the capture and its annotation layer to <paramref name="region"/> as one
    /// undoable step. The base bitmap is re-derived on undo via
    /// <see cref="ApplyUncroppedBaseBitmap"/>.
    /// </summary>
    internal void ApplyCrop(RectD region)
    {
        RectD normalized = region.Normalized().ClampTo(new RectD(0, 0, _canvasWidth, _canvasHeight)).ToPixelBounds();
        if (normalized.Width < 2 || normalized.Height < 2)
        {
            SelectTool(EditorTool.Select);
            return;
        }

        CommitActiveText();
        // Cut from the base the user currently sees. After a rotation that base is the
        // rotated bitmap; the untouched selection would yield the wrong region (or read
        // past its bounds, because the clamp above uses the rotated canvas size).
        BitmapSource cropBase = _surface.Frame.Bitmap;
        int x = (int)Math.Floor(normalized.X);
        int y = (int)Math.Floor(normalized.Y);
        int width = (int)Math.Ceiling(normalized.Width);
        int height = (int)Math.Ceiling(normalized.Height);
        int cropStride = width * 4;
        byte[] crop = ReadCropPixels(cropBase, x, y, width, height);

        var cropped = BitmapSource.Create(
            width,
            height,
            cropBase.DpiX,
            cropBase.DpiY,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            crop,
            cropStride);
        cropped.Freeze();

        _controller.CropDocument(normalized);
        SwapBaseBitmap(cropped, new RectD(0, 0, width, height));
        SelectTool(EditorTool.Select);
        SetStatus(UiText.Get("Tools_CropApplied"));
    }

    // ---- Image insertion -----------------------------------------------------------

    private void InsertImage(PointD image)
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Text_B2FD5E226F6B"),
            Filter = UiText.Get("Text_DC9F5ED04461"),
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            SelectTool(EditorTool.Select);
            return;
        }

        (BitmapSource Bitmap, string AssetFileName)? loaded = _imageStore.LoadFromFile(dialog.FileName);
        if (loaded is null)
        {
            MyCapture.App.Themes.ThemedDialog.Show(
                UiText.Get("Text_5DD4908475B6"),
                "MyCapture",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SelectTool(EditorTool.Select);
            return;
        }

        BitmapSource bitmap = loaded.Value.Bitmap;
        int sourceWidth = bitmap.PixelWidth;
        int sourceHeight = bitmap.PixelHeight;

        // Fit the image to at most a third of the current canvas, preserving aspect ratio,
        // centred on the click. The canvas tracks rotation and crop; the original capture
        // region does not.
        double maxWidth = _canvasWidth / 3;
        double maxHeight = _canvasHeight / 3;
        double aspect = sourceHeight > 0 ? (double)sourceWidth / sourceHeight : 1.0;

        double width = Math.Min(sourceWidth, maxWidth);
        double height = width / aspect;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * aspect;
        }

        var rect = new RectD(image.X - (width / 2), image.Y - (height / 2), width, height);
        _controller.AddImageAnnotation(loaded.Value.AssetFileName, sourceWidth, sourceHeight, rect);
        SyncToolButtons();
        SetStatus(UiText.Format("EditorFlow_Added", UiText.Get("Text_302BAE127938")));
    }

    // ---- Layout construction -------------------------------------------------------

    private void BuildLayout()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // command bar
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status

        UIElement commandBar = BuildCommandBar();
        Grid.SetRow(commandBar, 0);
        Children.Add(commandBar);

        UIElement body = BuildBody();
        Grid.SetRow(body, 1);
        Children.Add(body);

        UIElement statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 2);
        Children.Add(statusBar);
    }

    private Border BuildCommandBar()
    {
        _editCommands = BuildEditCommands();
        _cropCommands = BuildCropCommands();
        var modes = new Grid();
        modes.Children.Add(_editCommands);
        modes.Children.Add(_cropCommands);
        UpdateCommandMode();

        // Same surface as the title bar and the editor body, with no rule underneath, so the
        // caption and the commands read as one header rather than two stacked bars.
        var bar = new Border
        {
            Name = "AnnotationCommandBar",
            Padding = new Thickness(8, 6, 8, 6),
            Child = modes,
            SnapsToDevicePixels = true,
        };
        bar.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        return bar;
    }

    /// <summary>
    /// The everyday commands: history and the image commands on the left; the ways to finish
    /// on the right, in WinUI dialog order (the action, then the way out).
    /// </summary>
    private CommandGroupsPanel BuildEditCommands()
    {
        var left = CommandRow();
        _undoButton = IconTextButton(UiText.Get("Text_CE706412FA75"), UiText.Get("Text_C61A081610C9"), "Icon.Undo", FallbackUndo, () =>
        {
            if (_controller.PerformUndo())
            {
                SetStatus(UiText.Get("Text_DAF93C3AA95D"));
            }
        });
        _redoButton = IconTextButton(UiText.Get("Text_078C44D5A66E"), UiText.Get("Text_F457141C336A"), "Icon.Redo", FallbackRedo, () =>
        {
            if (_controller.PerformRedo())
            {
                SetStatus(UiText.Get("Text_8B22B7BBB0CD"));
            }
        });
        left.Children.Add(_undoButton);
        left.Children.Add(_redoButton);
        left.Children.Add(Separator());

        // Rotation remains directly available in crop mode and in the everyday overflow.
        _rotationCommands = BuildRotationCommands();

        // The single way into crop; the ratio and automatic margin finder live in that mode.
        Button crop = IconTextButton(UiText.Get("Tools_Crop"), UiText.Get("EditorFlow_CropHint"), "Icon.Crop", FallbackCrop,
            () => { SelectTool(EditorTool.Crop); Focus(); });
        AutomationProperties.SetAutomationId(crop, "Capture410Crop");
        left.Children.Add(crop);

        _redactButton = IconTextButton(
            UiText.Get("Text_E395E2567985"),
            UiText.Get("Text_A31A72A338DD"),
            "Icon.Mosaic", FallbackMosaic,
            () => _ = ApplyPrivacyRedactionsAsync());
        _redactButton.IsEnabled = _privacyRedactionService?.IsAvailable == true;
        AutomationProperties.SetHelpText(
            _redactButton,
            _redactButton.IsEnabled
                ? UiText.Get("Text_EB9AB8D7D13C")
                : UiText.Get("Text_39E626945336"));
        left.Children.Add(_redactButton);

        // Text recognition of what the editor shows now, beside the other text-reading command.
        _copyTextButton = IconTextButton(
            UiText.Get("EditorFlow_CopyText"),
            UiText.Get("EditorFlow_CopyTextHint"),
            "Icon.Text", FallbackText,
            () => _ = RecognizeCurrentTextAsync());
        AutomationProperties.SetAutomationId(_copyTextButton, "EditorFlowCopyText");
        RefreshCopyTextAvailability();
        left.Children.Add(_copyTextButton);
        Button more = BuildEditOverflowMenu();
        left.Children.Add(more);

        var right = CommandRow();
        right.Children.Add(Separator());
        Button save = BuildSaveOverflowMenu();
        Button copy = IconTextButton(
            UiText.Get("Text_37B3D3B11B26"), UiText.Get("Text_74EEFE6E43AE"), "Icon.Copy", FallbackCopy,
            () => Commit(EditorCommitAction.Done));
        copy.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        Button done = IconTextButton(
            UiText.Get("Text_727333AB0740"), UiText.Get("EditorFlow_DoneHint"), "Icon.Check", FallbackCheck,
            () => Commit(EditorCommitAction.Done));
        done.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        // Esc no longer closes, so Cancel carries no shortcut; it closes like the title bar does.
        Button cancel = IconTextButton(UiText.Get("Text_BE876433993A"), UiText.Get("EditorFlow_CancelHint"), "Icon.Close", FallbackClose, RequestClose);
        AutomationProperties.SetAutomationId(cancel, "EditorFlowCancel");
        AutomationProperties.SetAutomationId(done, "EditorFlowDone");
        AutomationProperties.SetAutomationId(copy, "EditorFlowCopy");
        right.Children.Add(save);
        right.Children.Add(copy);
        right.Children.Add(Separator());
        right.Children.Add(done);
        right.Children.Add(cancel);

        foreach (var (button, command, borderless) in new[] { (_undoButton, "Undo", true), (_redoButton, "Redo", true),
            (crop, "Crop", true), (_redactButton, "Privacy", true), (save, "Save", false), (copy, "Copy", false), (done, "Done", false) })
        {
            CommandButton(button, borderless, ShortcutScope.Annotation, command);
        }

        CommandButton(cancel, borderless: true);
        CommandButton(_copyTextButton, borderless: true);
        CommandButton(more, borderless: true);
        var panel = new CommandGroupsPanel { Children = { left, right } };
        AutomationProperties.SetName(panel, UiText.Get("EditorFlow_CommandBarName"));
        return panel;
    }

    /// <summary>
    /// Crop mode replaces the command bar, as in Photos: rotation, the frame ratio and the
    /// automatic margin finder on the left, then Apply and Cancel.
    /// </summary>
    private CommandGroupsPanel BuildCropCommands()
    {
        var left = CommandRow();
        _cropRotationHost = new Border { Child = _rotationCommands };
        left.Children.Add(_cropRotationHost);
        left.Children.Add(Separator());

        var ratioLabel = new TextBlock
        {
            Text = UiText.Get("Crop.Ratio"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
        };
        ratioLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        left.Children.Add(ratioLabel);
        _cropRatio = new ComboBox { MinHeight = 32, MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        foreach (string label in new[] { UiText.Get("Crop.Free"), UiText.Get("Crop.Original"), "1:1", "4:3", "16:9", "9:16" }) _cropRatio.Items.Add(label);
        AutomationName(_cropRatio, UiText.Get("Crop.Ratio"));
        _cropRatio.SelectedIndex = 0;
        _cropRatio.SelectionChanged += (_, _) =>
        {
            ApplyCropRatio();
            RefreshCrop();
        };
        ReturnFocusAfterMousePick(_cropRatio);
        left.Children.Add(_cropRatio);

        _autoTrimButton = IconTextButton(UiText.Get("Capture410.AutoTrim"), UiText.Get("Capture410.AutoTrimHint"),
            "Icon.Crop", FallbackCrop, () => _ = PreviewAutomaticTrimAsync());
        AutomationProperties.SetAutomationId(_autoTrimButton, "Capture410AutoTrim");
        left.Children.Add(_autoTrimButton);

        var right = CommandRow();
        Button apply = IconTextButton(UiText.Get("Crop.Apply"), UiText.Get("EditorFlow_CropApplyHint"), "Icon.Check", FallbackCheck, ApplyPendingCrop);
        apply.SetResourceReference(StyleProperty, "Button.Primary");
        Button cancel = IconTextButton(UiText.Get("Text_BE876433993A"), UiText.Get("EditorFlow_CropCancelHint"), "Icon.Close", FallbackClose, () =>
        {
            CancelPendingCrop();
            Focus();
        });
        AutomationProperties.SetAutomationId(apply, "EditorFlowCropApply");
        AutomationProperties.SetAutomationId(cancel, "EditorFlowCropCancel");
        right.Children.Add(apply);
        right.Children.Add(cancel);

        CommandButton(_autoTrimButton, borderless: true);
        CommandButton(apply, borderless: false, ShortcutScope.Crop, "Apply");
        CommandButton(cancel, borderless: true, ShortcutScope.Crop, "Cancel");
        var panel = new CommandGroupsPanel { Children = { left, right } };
        AutomationProperties.SetName(panel, UiText.Get("EditorFlow_CropBarName"));
        return panel;
    }

    private StackPanel BuildRotationCommands()
    {
        // The image-plus-arrow symbols and names distinguish image rotation from undo/redo.
        _rotateLeftButton = IconTextButton(UiText.Get("EditorUx_RotateLeft"), UiText.Get("EditorUx_RotateLeftHint"), "Icon.RotateLeft", FallbackRotateLeft, () => RotateCapture(-1));
        _rotateRightButton = IconTextButton(UiText.Get("EditorUx_RotateRight"), UiText.Get("EditorUx_RotateRightHint"), "Icon.RotateRight", FallbackRotateRight, () => RotateCapture(1));
        AutomationName(_rotateLeftButton, UiText.Get("EditorUx_RotateLeftHint"));
        AutomationName(_rotateRightButton, UiText.Get("EditorUx_RotateRightHint"));
        CommandButton(_rotateLeftButton, borderless: true, ShortcutScope.Annotation, "RotateLeft");
        CommandButton(_rotateRightButton, borderless: true, ShortcutScope.Annotation, "RotateRight");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _rotateLeftButton, _rotateRightButton },
        };
    }

    private static StackPanel CommandRow() => new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// Gives a command bar button its shortcut (tooltip, accelerator key and the visible key
    /// line beneath it) and the bar's common padding, type size and height.
    /// </summary>
    /// <remarks>
    /// The disabled look comes from the shared button styles: each button level keeps its shape
    /// and only dims. <paramref name="borderless"/> identifies former ghost commands, which now
    /// use the secondary button surface so they remain recognisable before hover.
    /// </remarks>
    private void CommandButton(Button button, bool borderless, ShortcutScope? scope = null, string? command = null)
    {
        if (scope is { } shortcutScope && command is not null)
        {
            AttachEditorShortcut(button, shortcutScope, command);
        }

        if (borderless) button.SetResourceReference(StyleProperty, "Button.Secondary");
        button.Width = double.NaN;
        CommandButtonPresentation.Apply(button);
        CommandButtonPresentation.ReserveHint(button);
    }

    private void ApplyCropRatio() =>
        _crop?.SetRatio(_cropRatio.SelectedIndex switch { 1 => (double)_canvasWidth / _canvasHeight, 2 => 1, 3 => 4.0 / 3, 4 => 16.0 / 9, 5 => 9.0 / 16, _ => null });

    /// <summary>Shows the crop commands while a crop frame is open and the everyday commands otherwise.</summary>
    private void UpdateCommandMode()
    {
        if (_editCommands is null || _cropCommands is null) return;
        bool cropping = _crop is not null;
        bool hidingFocus = (cropping ? _editCommands : _cropCommands).IsKeyboardFocusWithin;
        if (hidingFocus) Focus();
        _editCommands.Visibility = cropping ? Visibility.Collapsed : Visibility.Visible;
        _cropCommands.Visibility = cropping ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the crop commands are showing, for tests.</summary>
    internal bool IsCropCommandBarVisible => _cropCommands.Visibility == Visibility.Visible;

    /// <summary>The tool rail's groups, top to bottom, for tests.</summary>
    internal IReadOnlyList<IReadOnlyList<EditorTool>> ToolGroups => _toolGroups;

    private Button BuildEditOverflowMenu()
    {
        var menu = new ContextMenu();
        foreach (var (command, key, source) in new[]
        {
            ("RotateLeft", "EditorUx_RotateLeft", _rotateLeftButton),
            ("RotateRight", "EditorUx_RotateRight", _rotateRightButton),
        })
        {
            var item = new MenuItem
            {
                Header = UiText.Get(key),
                InputGestureText = ShortcutCatalog.Get(ShortcutScope.Annotation, command).Hint,
            };
            item.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = source });
            AutomationName(item, UiText.Get(key));
            AutomationProperties.SetAcceleratorKey(item, item.InputGestureText);
            item.Click += (_, _) => _shortcutActions[command]();
            menu.Items.Add(item);
        }

        Button button = IconTextButton(UiText.Get("Common_More"), UiText.Get("Common_More"),
            "Icon.More", () => Geometry.Parse("M4,10 L4.1,10 M10,10 L10.1,10 M16,10 L16.1,10"), () => { });
        AutomationProperties.SetAutomationId(button, "EditorMore");
        button.ContextMenu = menu;
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    private Button BuildSaveOverflowMenu()
    {
        var menu = new ContextMenu { MinWidth = 230 };

        var quickSave = new MenuItem
        {
            Header = UiText.Get("Text_DFC084A111D0"),
            InputGestureText = ShortcutCatalog.Get(ShortcutScope.Annotation, "Save").Hint,
            Icon = BuildIcon("Icon.Save", FallbackSave, 16),
        };
        quickSave.Click += (_, _) => Commit(EditorCommitAction.QuickSave);
        AutomationName(quickSave, UiText.Get("Text_DFC084A111D0"));

        var saveAs = new MenuItem
        {
            Header = UiText.Get("Text_57950AFF46BC"),
            InputGestureText = ShortcutCatalog.Get(ShortcutScope.Annotation, "SaveAs").Hint,
            Icon = BuildIcon("Icon.SaveAs", FallbackSaveAs, 16),
        };
        saveAs.Click += (_, _) => Commit(EditorCommitAction.SaveAs);
        AutomationName(saveAs, UiText.Get("Text_57950AFF46BC"));

        menu.Items.Add(quickSave);
        menu.Items.Add(saveAs);
        var reduceExport = new MenuItem { Header = UiText.Get("ExportReduction_Title") };
        AutomationName(reduceExport, UiText.Get("ExportReduction_Title"));
        reduceExport.Click += (_, _) => Commit(EditorCommitAction.SaveAs, reduceExport: true);
        menu.Items.Add(reduceExport);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(BuildIcon("Icon.Save", FallbackSave, 16));
        content.Children.Add(new TextBlock
        {
            // "Save" alone read like Done, which also saves (to the library); this one writes a file.
            Text = UiText.Get("EditorFlow_SaveToFile"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0),
        });
        content.Children.Add(BuildIcon("Icon.ChevronDown", FallbackChevronDown, 12));

        var button = new Button
        {
            Content = content,
            ToolTip = UiText.Get("EditorFlow_SaveToFileHint"),
            MinWidth = 78,
            Margin = new Thickness(2, 0, 2, 0),
            ContextMenu = menu,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        AutomationName(button, UiText.Get("EditorFlow_SaveToFile"));
        AutomationProperties.SetAutomationId(button, "EditorFlowSaveToFile");
        PreserveEditorMouseFocus(button);
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    private UIElement BuildBody()
    {
        var grid = new Grid
        {
            Background = Brush("Surface.Base", Color.FromRgb(0x0B, 0x0F, 0x17)),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ToolRailWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _inspectorColumn = new ColumnDefinition { Width = new GridLength(InspectorWidth) };
        grid.ColumnDefinitions.Add(_inspectorColumn);

        grid.SetResourceReference(Panel.BackgroundProperty, "Surface.Base");
        UIElement toolRail = BuildToolRail();
        Grid.SetColumn(toolRail, 0);
        grid.Children.Add(toolRail);

        UIElement viewportFrame = BuildViewport();
        Grid.SetColumn(viewportFrame, 1);
        grid.Children.Add(viewportFrame);

        _inspectorPanel = BuildInspector();
        Grid.SetColumn(_inspectorPanel, 2);
        grid.Children.Add(_inspectorPanel);

        return grid;
    }

    private Border BuildToolRail()
    {
        // Grouped by intent, a rule between groups: select / shapes / freehand / words and
        // numbers / covering / pictures. Each group starts a new row of the two-column grid,
        // so a row never pairs tools from different groups. Crop is not here: it is an image
        // command in the top bar, next to the rotate commands it now contains.
        var groups = new StackPanel { Name = "AnnotationToolGroups", VerticalAlignment = VerticalAlignment.Top };
        ToolGroup(groups, (EditorTool.Select, UiText.Get("Text_8D1A750C9351"), UiText.Get("Text_8A3ADCEB89EF"), "Icon.Select", FallbackSelect));
        ToolGroup(groups,
            (EditorTool.Rectangle, UiText.Get("Text_DC0760235344"), UiText.Get("Text_B659038CC17D"), "Icon.Rectangle", FallbackRectangle),
            (EditorTool.Ellipse, UiText.Get("Tools.Ellipse"), UiText.Get("Tools.Ellipse"), "Icon.Ellipse", () => new EllipseGeometry(new Rect(3, 3, 14, 14))),
            (EditorTool.Arrow, UiText.Get("Text_2785CE58DF74"), UiText.Get("Text_D53ABD064C5E"), "Icon.Arrow", FallbackArrow));
        ToolGroup(groups,
            (EditorTool.Pen, UiText.Get("Text_37DB9D0B1C8E"), UiText.Get("Text_684786FB2840"), "Icon.Pen", FallbackPen),
            (EditorTool.Highlighter, UiText.Get("Tools.Highlighter"), UiText.Get("Tools.Highlighter"), "Icon.Highlighter", () => Geometry.Parse("M4,14 L13,3 L17,7 L8,18 Z M2,18 H12")));
        ToolGroup(groups,
            (EditorTool.Text, UiText.Get("Text_258AD4B095A1"), UiText.Get("Text_B3711BAFCD9C"), "Icon.Text", FallbackText),
            (EditorTool.StepNumber, UiText.Get("Tools.StepNumber"), UiText.Get("EditorUx_StepNumberToolTip"), "Icon.StepNumber", () => Geometry.Parse("M3,3 H17 V17 H3 Z M8,7 L10,5 V15 M7,15 H13")));
        // The purpose sentence lives on the tool (tooltip and help text), not in the panel.
        AutomationProperties.SetHelpText(_toolButtons[EditorTool.StepNumber], UiText.Get("EditorUx_StepNumberInstruction"));
        ToolGroup(groups,
            (EditorTool.Mosaic, UiText.Get("Text_3F8046019B32"), UiText.Get("Text_3F8046019B33"), "Icon.Mosaic", FallbackMosaic),
            (EditorTool.Blur, UiText.Get("EditorBlur_Name"), UiText.Get("EditorBlur_ToolTip"), "Icon.Blur", FallbackBlur));
        ToolGroup(groups, (EditorTool.Image, UiText.Get("Text_302BAE127938"), UiText.Get("Text_93D363BDFEA5"), "Icon.Image", FallbackImage));

        var panel = new Border { Name = "AnnotationToolRail", Child = groups };
        panel.SetResourceReference(FrameworkElement.StyleProperty, "Rail.Panel");
        // Tighter than the shared rail padding: six groups must still fit the 460 DIP minimum height.
        panel.Padding = new Thickness(6, 4, 6, 4);
        // A wrapped command row reduces the available rail height. Keep every tool's
        // hit area intact and reclaim only spacing between tools and intent groups.
        var regularHeight = panel.Padding.Top + panel.Padding.Bottom + groups.Children
            .OfType<FrameworkElement>()
            .Sum(child => child is UniformGrid grid
                ? Math.Ceiling(grid.Children.Count / (double)grid.Columns) * 40
                : 9);
        panel.SizeChanged += (_, _) =>
        {
            var compact = panel.ActualHeight < regularHeight;
            foreach (var button in _toolButtons.Values)
            {
                button.Margin = new Thickness(0, compact ? 0 : 1, 0, compact ? 0 : 1);
            }

            foreach (var separator in groups.Children.OfType<Border>())
            {
                separator.Margin = new Thickness(8, compact ? 0 : 4, 8, compact ? 0 : 4);
            }
        };
        return panel;
    }

    private void ToolGroup(Panel groups, params (EditorTool Tool, string Label, string ToolTip, string Icon, Func<Geometry> Fallback)[] tools)
    {
        if (groups.Children.Count > 0)
        {
            groups.Children.Add(RailSeparator());
        }

        var grid = new UniformGrid { Columns = 2 };
        foreach (var (tool, label, toolTip, icon, fallback) in tools)
        {
            AddToolButton(grid, tool, label, toolTip, icon, fallback);
        }

        groups.Children.Add(grid);
        _toolGroups.Add([.. tools.Select(item => item.Tool)]);
    }

    private Border RailSeparator()
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            SnapsToDevicePixels = true,
            Tag = "AnnotationToolGroupSeparator",
        };
        rule.SetResourceReference(Border.BackgroundProperty, "Border.Subtle");
        return rule;
    }

    private Border BuildViewport()
    {
        _viewport.SetResourceReference(Panel.BackgroundProperty, "Surface.Sunken");
        _viewport.Children.Add(_surface);
        _overlayCanvas.IsHitTestVisible = true;
        _overlayCanvas.Background = null;
        _viewport.Children.Add(_overlayCanvas);

        return ThemedBorder(new Border
        {
            Background = Brush("Surface.Sunken", Colors.Black),
            BorderBrush = Brush("Border.Subtle", Colors.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(12),
            ClipToBounds = true,
            Child = _viewport,
        }, "Surface.Sunken");
    }

    private Border BuildInspector()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };

        _inspectorTitle = new TextBlock
        {
            Text = UiText.Get("Text_33406FC582BE"),
            Foreground = Brush("Text.Primary", Colors.White),
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        };
        // The panel names what is being edited and shows its values. How a tool is used is in
        // the tool's tooltip, and results are in the status line, so no sentence repeats here.
        _inspectorTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        stack.Children.Add(_inspectorTitle);

        _stepSection = BuildStepNumberSection();
        stack.Children.Add(_stepSection);

        _fontFamilySection = BuildFontFamilySection();
        stack.Children.Add(_fontFamilySection);
        _fontSizeSection = BuildFontSizeSection();
        stack.Children.Add(_fontSizeSection);
        _textStyleSection = BuildTextStyleSection();
        stack.Children.Add(_textStyleSection);

        _colorSection = BuildColorSection();
        stack.Children.Add(_colorSection);

        _thicknessSection = BuildThicknessSection();
        stack.Children.Add(_thicknessSection);
        _shapeStyleSection = BuildShapeStyleSection();
        stack.Children.Add(_shapeStyleSection);
        _blurSection = BuildBlurSection();
        stack.Children.Add(_blurSection);
        _cropSection = BuildCropSection();
        stack.Children.Add(_cropSection);

        _deleteButton = TextButton(UiText.Get("Text_0DED92FCE6B0"), UiText.Get("Text_7DFFA4D81765"), "Button.Danger", DeleteSelected);
        _deleteButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _deleteButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        _deleteButton.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(_deleteButton);

        return ThemedBorder(new Border
        {
            Background = Brush("Surface.Raised", Color.FromRgb(0x10, 0x17, 0x22)),
            BorderBrush = Brush("Border.Subtle", Colors.Gray),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(18),
            Child = new ScrollViewer
            {
                Name = "AnnotationInspectorScroll",
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            SnapsToDevicePixels = true,
        }, "Surface.Raised");
    }

    /// <summary>
    /// Shows, while the step tool is active, the badge the next click adds: "Next number [3]".
    /// A value, not an explanation; the tool's tooltip says what the tool is for.
    /// </summary>
    private FrameworkElement BuildStepNumberSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionLabel(UiText.Get("EditorUx_StepNumberNext")));
        _stepPreviewNumber = new Path
        {
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)),
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _stepPreview = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = _stepPreviewNumber,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _stepPreview.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
        panel.Children.Add(_stepPreview);
        return panel;
    }

    /// <summary>Paints the next badge in the colour and number the step tool will use.</summary>
    private void UpdateStepPreview()
    {
        if (_stepPreview is null) return;
        int number = _controller.NextStepNumber;
        string text = number.ToString(CultureInfo.CurrentCulture);
        ColorRgba color = _controller.StrokeColor;
        _stepPreview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            text.Length > 2 ? 15 : 20, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Geometry digits = formatted.BuildGeometry(new Point(0, 0));
        // Start the glyph outline at the path origin so the badge centres the ink, not the line box.
        Rect ink = digits.Bounds;
        if (!ink.IsEmpty) digits.Transform = new TranslateTransform(-ink.X, -ink.Y);
        _stepPreviewNumber.Data = digits;
        AutomationName(_stepPreview, UiText.Format("EditorUx_StepNumberNextName", number));
    }

    /// <summary>The number the step preview shows, for tests.</summary>
    internal string StepPreviewName => AutomationProperties.GetName(_stepPreview);

    /// <summary>Keeps the step preview and the numbered cursor on the next number and colour.</summary>
    private void RefreshStepFeedback()
    {
        UpdateStepPreview();
        if (_controller.Tool == EditorTool.StepNumber) ApplyCursor(_pointerTarget);
    }

    /// <summary>
    /// The crop frame's size in pixels. The ratio, Apply and Cancel are in the crop command
    /// bar, so the panel only holds the two values.
    /// </summary>
    private FrameworkElement BuildCropSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var dimensions = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 10) };
        _cropWidth = new TextBox { MinHeight = 30, Margin = new Thickness(0, 0, 4, 0) };
        _cropHeight = new TextBox { MinHeight = 30 };
        foreach (var (box, key) in new[] { (_cropWidth, "Crop.Width"), (_cropHeight, "Crop.Height") })
        {
            var field = new StackPanel(); field.Children.Add(SectionLabel(UiText.Get(key)));
            var border = new Border { Child = box, BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
            var error = new TextBlock { Text = UiText.Get("Crop.InvalidSizeLabel"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            error.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            _cropInputErrors.Add(box, (border, error));
            field.Children.Add(border); field.Children.Add(error); dimensions.Children.Add(field);
            AutomationName(box, UiText.Get(key));
            InputMethod.SetIsInputMethodEnabled(box, false); // pixel counts only
            box.TextChanged += (_, _) =>
            {
                if (!_syncingInspector && _crop is not null) _dirtyCropInputs.Add(box);
            };
            box.LostKeyboardFocus += (_, _) => ChangeCropSize(box);
        }
        panel.Children.Add(dimensions);
        return panel;
    }

    private bool ChangeCropSize(TextBox box)
    {
        if (_crop is null || !_dirtyCropInputs.Contains(box)) return true;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 2)
        {
            SetCropInputError(box, true);
            SetStatus(UiText.Get("Crop.InvalidSize"));
            return false;
        }
        _dirtyCropInputs.Remove(box);
        SetCropInputError(box, false);
        if (ReferenceEquals(box, _cropWidth)) _crop.SetWidth(value); else _crop.SetHeight(value);
        RefreshCrop();
        // A corrected size clears the error report; there is no other result to show yet.
        if (_cropInputErrors.Values.All(error => error.Message.Visibility == Visibility.Collapsed)
            && string.Equals(StatusText, UiText.Get("Crop.InvalidSize"), StringComparison.Ordinal))
            SetStatus(string.Empty);
        return true;
    }

    private bool CommitCropInputs() => ChangeCropSize(_cropWidth) & ChangeCropSize(_cropHeight);

    private void SetCropInputError(TextBox box, bool invalid)
    {
        var (border, message) = _cropInputErrors[box];
        if (invalid) border.SetResourceReference(Border.BorderBrushProperty, "State.Danger");
        else border.BorderBrush = Brushes.Transparent;
        message.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(box, invalid ? UiText.Get("Crop.InvalidSize") : string.Empty);
    }

    private FrameworkElement BuildColorSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionLabel(UiText.Get("Text_D1B87ACB2D22")));

        _swatchPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            MaxWidth = 192,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        ColorRgba[] palette =
        [
            ColorRgba.FromRgb(0xEF, 0x44, 0x44),
            ColorRgba.FromRgb(0xFB, 0xBF, 0x24),
            ColorRgba.FromRgb(0x34, 0xD3, 0x99),
            ColorRgba.FromRgb(0x3B, 0x82, 0xF6),
            ColorRgba.FromRgb(0x11, 0x18, 0x27),
            ColorRgba.White,
        ];

        foreach (ColorRgba color in palette)
        {
            ColorRgba swatchColor = color;
            var swatchButton = new Button
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(0),
                ToolTip = UiText.Format("Text_151C77170A94", ColorCode(swatchColor)),
                BorderThickness = new Thickness(1),
                // A control boundary, so a dark swatch stays outlined on a dark panel.
                BorderBrush = Brush("Border.Control", Colors.Gray),
                Background = swatchColor.ToBrush(),
            };
            AutomationName(swatchButton, UiText.Format("Text_151C77170A94", ColorCode(swatchColor)));
            PreserveEditorMouseFocus(swatchButton);
            swatchButton.Click += (_, _) =>
            {
                _controller.ApplyStrokeColor(swatchColor);
                RememberPreferences();
                RefreshStepFeedback();
                SetStatus(UiText.Format("Text_67CAE5B118A3", ColorCode(swatchColor)));
            };
            _swatchPanel.Children.Add(swatchButton);
        }

        panel.Children.Add(_swatchPanel);
        var custom = new TextBox { Text = ColorCode(_controller.StrokeColor), MinHeight = 32, Margin = new Thickness(0, 4, 0, 4) };
        _customColorInput = custom;
        AutomationName(custom, UiText.Get("Tools.CustomColor"));
        InputMethod.SetIsInputMethodEnabled(custom, true);
        // Enter applies and Esc restores; both hand the keyboard back to the canvas so the next
        // letter is a tool shortcut again instead of more text in this box. A composing IME
        // reports ImeProcessed here and keeps its own Enter/Esc.
        custom.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (ApplyCustomColor(custom.Text)) Focus();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                custom.Text = ColorCode(_controller.StrokeColor);
                Focus();
            }
        };
        panel.Children.Add(custom);
        var apply = new Button { Content = UiText.Get("Tools.CustomColor"), MinHeight = 36 };
        AutomationName(apply, UiText.Get("Tools.CustomColor"));
        PreserveEditorMouseFocus(apply);
        apply.Click += (_, _) => ApplyCustomColor(custom.Text);
        panel.Children.Add(apply);
        return panel;
    }

    /// <summary>
    /// Turns the IME off for <paramref name="root"/> and every non-text control under it.
    /// </summary>
    /// <remarks>
    /// The canvas, tool buttons, sliders and lists never take text, so a Hangul-mode IME must
    /// not hold a hidden composition there: a typed T would become an invisible ㅅ that later
    /// lands in a text box. WPF reads the setting from the focused element only (it does not
    /// inherit), so each control gets it. Text inputs keep their own setting.
    /// </remarks>
    private static void DisableImeOutsideTextInputs(DependencyObject root)
    {
        InputMethod.SetIsInputMethodEnabled(root, false);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject element and not TextBoxBase)
            {
                DisableImeOutsideTextInputs(element);
            }
        }
    }

    private bool ApplyCustomColor(string text)
    {
        if (!ColorRgba.TryParse(text, out ColorRgba color))
        {
            SetStatus(UiText.Get("Tools.InvalidColor"));
            return false;
        }

        _controller.ApplyStrokeColor(color);
        RememberPreferences();
        RefreshStepFeedback();
        // Short and lower-case forms read back in the one form the editor shows.
        if (_customColorInput is not null) _customColorInput.Text = ColorCode(color);
        SetStatus(UiText.Format("Text_67CAE5B118A3", ColorCode(color)));
        return true;
    }

    /// <summary>The color input, for tests.</summary>
    internal TextBox CustomColorInputForTest => _customColorInput;

    /// <summary>
    /// The color as people write it: <c>#RRGGBB</c> when opaque, and <c>#AARRGGBB</c> only when it
    /// is see-through. The input accepts either form.
    /// </summary>
    internal static string ColorCode(ColorRgba color) => color.A == byte.MaxValue
        ? string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}")
        : color.ToHex();

    /// <summary>
    /// After a mouse pick from <paramref name="combo"/>'s list, returns the keyboard to the
    /// canvas so letter shortcuts work again. A keyboard user keeps focus on the list.
    /// </summary>
    private void ReturnFocusAfterMousePick(ComboBox combo) =>
        combo.DropDownClosed += (_, _) =>
        {
            if (InputManager.Current.MostRecentInputDevice is not MouseDevice) return;
            // Deferred: the combo box restores its own focus while the drop-down closes.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (combo.IsKeyboardFocusWithin) Focus();
            });
        };

    private FrameworkElement BuildThicknessSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 16, 0, 0) };

        // The settings window accepts the same range; the editor never narrows it.
        _thicknessSlider = new Slider
        {
            Minimum = SettingsRanges.StrokeThickness.Min,
            Maximum = SettingsRanges.StrokeThickness.Max,
            Value = _controller.StrokeThickness,
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiText.Get("Text_70EC2D232B4F"),
            SmallChange = 1,
            LargeChange = 4,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
        };
        AutomationName(_thicknessSlider, UiText.Get("Text_70EC2D232B4F"));
        panel.Children.Add(SliderHeader(UiText.Get("Text_70EC2D232B4F"),
            ValueEntry(_thicknessSlider, UiText.Get("Editor_UnitPx"), UiText.Get("Text_70EC2D232B4F"))));
        _thicknessSlider.ValueChanged += (_, args) =>
        {
            if (_syncingInspector)
            {
                return;
            }

            _controller.ApplyStrokeThickness(args.NewValue);
            RememberPreferences();
            if (_controller.Selected is not null)
            {
                SetStatus(UiText.Format("Text_DBAF1D995258", args.NewValue));
            }
        };
        panel.Children.Add(_thicknessSlider);
        return panel;
    }

    private FrameworkElement BuildShapeStyleSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionLabel(UiText.Get("Text_E0B58D1CFED1")));
        _strokeStyleComboBox = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 4, 0, 0) };
        foreach ((AnnotationStrokeStyle style, string label) in new[]
        {
            (AnnotationStrokeStyle.Solid, UiText.Get("Text_99851CC7E6A4")),
            (AnnotationStrokeStyle.Dashed, UiText.Get("Text_2428B662D585")),
            (AnnotationStrokeStyle.Dotted, UiText.Get("Text_68CAE75183FF")),
            (AnnotationStrokeStyle.ThickDashed, UiText.Get("Text_071DBD0C6E2C")),
        })
        {
            _strokeStyleComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = style });
        }

        _strokeStyleComboBox.SelectedIndex = 0;
        AutomationName(_strokeStyleComboBox, UiText.Get("Text_3084C5AA0845"));
        _strokeStyleComboBox.SelectionChanged += (_, _) =>
        {
            if (!_syncingInspector && _strokeStyleComboBox.SelectedItem is ComboBoxItem { Tag: AnnotationStrokeStyle style })
            {
                _controller.ApplyStrokeStyle(style);
                RememberPreferences();
                UpdateInspector();
            }
        };
        ReturnFocusAfterMousePick(_strokeStyleComboBox);
        panel.Children.Add(_strokeStyleComboBox);
        _fillTransparencyLabel = SectionLabel(UiText.Get("Text_1E0DD4F46A0C"));
        _fillTransparencyLabel.Margin = new Thickness(0, 16, 0, 4);
        panel.Children.Add(_fillTransparencyLabel);
        _fillTransparencySlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            SmallChange = 1,
            LargeChange = 10,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            ToolTip = UiText.Get("Text_27FD5EFB7773"),
        };
        AutomationName(_fillTransparencySlider, UiText.Get("Text_35E5667BB610"));
        AutomationProperties.SetHelpText(_fillTransparencySlider, (string)_fillTransparencySlider.ToolTip);
        _fillTransparencySlider.ValueChanged += (_, args) =>
        {
            _fillTransparencyLabel.Text = UiText.Format("Text_5D92A3982F2F", args.NewValue);
            if (!_syncingInspector)
            {
                _controller.ApplyFillTransparency(args.NewValue);
                RememberPreferences();
            }
        };
        panel.Children.Add(_fillTransparencySlider);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Text_BA0448903510"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Text.Secondary", Colors.LightGray),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return panel;
    }

    private FrameworkElement BuildFontSizeSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        _fontSizeSlider = new Slider
        {
            Minimum = SettingsRanges.FontSize.Min,
            Maximum = SettingsRanges.FontSize.Max,
            Value = _controller.DefaultFontSize,
            SmallChange = 1,
            LargeChange = 4,
            TickFrequency = 1,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = UiText.Get("Text_5F3A9C8D1E20"),
        };
        AutomationName(_fontSizeSlider, UiText.Get("Text_5F3A9C8D1E20"));
        panel.Children.Add(SliderHeader(UiText.Get("Text_5F3A9C8D1E20"),
            ValueEntry(_fontSizeSlider, UiText.Get("Editor_UnitPt"), UiText.Get("Text_5F3A9C8D1E20"))));
        _fontSizeSlider.ValueChanged += (_, args) =>
        {
            if (_syncingInspector)
            {
                return;
            }

            _controller.ApplyFontSize(args.NewValue);
            RememberPreferences();
            if (_activeTextBox is not null && _editingText is not null)
            {
                _activeTextBox.FontSize = Math.Max(1, _editingText.FontSize * _surface.DipPerPixel);
                LayoutLiveTextBox(_activeTextBox);
            }

            if (_controller.Selected is TextAnnotation)
            {
                SetStatus(UiText.Format("Text_5F3A9C8D1E21", args.NewValue));
            }
        };
        panel.Children.Add(_fontSizeSlider);
        return panel;
    }

    private FrameworkElement BuildTextStyleSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        CheckBox Toggle(string key, Action<bool> apply)
        {
            var toggle = new CheckBox { Content = UiText.Get(key), MinHeight = 32 };
            AutomationName(toggle, UiText.Get(key));
            toggle.Click += (_, _) =>
            {
                if (_syncingInspector) return;
                bool enabled = toggle.IsChecked == true;
                CommitActiveText();
                apply(enabled);
                RememberPreferences();
                UpdateInspector();
            };
            panel.Children.Add(toggle);
            return toggle;
        }
        _textBold = Toggle("EditorText_Bold", enabled =>
        {
            if (_controller.Selected is TextAnnotation) _controller.ApplyTextBold(enabled);
            else _defaultTextBold = enabled;
        });
        _textBackground = Toggle("EditorText_Background", enabled =>
        {
            if (_controller.Selected is TextAnnotation) _controller.ApplyTextBackground(enabled);
            else _defaultTextBackground = enabled;
        });
        _textOutline = Toggle("EditorText_Outline", ApplyTextOutline);
        return panel;
    }

    private FrameworkElement BuildBlurSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        _blurSlider = new Slider
        {
            Minimum = BlurAnnotation.MinimumRadius,
            Maximum = BlurAnnotation.MaximumRadius,
            Value = _controller.BlurRadius,
            SmallChange = 1,
            LargeChange = 4,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = UiText.Get("EditorBlur_StrengthHint"),
        };
        AutomationName(_blurSlider, UiText.Get("EditorBlur_Strength"));
        panel.Children.Add(SliderHeader(UiText.Get("EditorBlur_Strength"),
            ValueEntry(_blurSlider, UiText.Get("Editor_UnitPx"), UiText.Get("EditorBlur_Strength"))));
        AutomationProperties.SetHelpText(_blurSlider, UiText.Get("EditorBlur_StrengthHint"));
        _blurSlider.ValueChanged += (_, args) =>
        {
            if (_syncingInspector)
            {
                return;
            }

            _controller.ApplyBlurRadius(args.NewValue);
            RememberPreferences();
            if (_controller.Selected is BlurAnnotation)
            {
                SetStatus(UiText.Format("EditorBlur_StrengthChanged", _controller.BlurRadius));
            }
        };
        panel.Children.Add(_blurSlider);
        return panel;
    }

    private UIElement BuildStatusBar()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _statusText = new TextBlock
        {
            Text = string.Empty,
            Foreground = Brush("Text.Secondary", Colors.LightGray),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);
        AutomationName(_statusText, UiText.Get("Text_4D6051F71010"));
        Grid.SetColumn(_statusText, 0);
        grid.Children.Add(_statusText);

        var dimensions = _dimensionsText = new TextBlock
        {
            Text = UiText.Format("Text_009548FC2109", _canvasWidth, _canvasHeight),
            Foreground = Brush("Text.Muted", Colors.Gray),
            FontFamily = FontFamilyResource("Font.Mono", "Consolas"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        dimensions.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        Grid.SetColumn(dimensions, 1);
        grid.Children.Add(dimensions);

        return ThemedBorder(new Border
        {
            Background = Brush("Surface.Raised", Color.FromRgb(0x10, 0x17, 0x22)),
            BorderBrush = Brush("Border.Subtle", Colors.Gray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 6, 12, 7),
            Child = grid,
            SnapsToDevicePixels = true,
        }, "Surface.Raised");
    }

    // ---- Tool rail / command helpers -----------------------------------------------

    private void AddToolButton(
        Panel parent,
        EditorTool tool,
        string label,
        string tooltip,
        string iconResourceKey,
        Func<Geometry> fallbackGeometry)
    {
        var button = new ToggleButton
        {
            Content = BuildIcon(iconResourceKey, fallbackGeometry, 20),
            ToolTip = tool is EditorTool.StepNumber or EditorTool.Blur ? tooltip : label,
            Width = 40, Height = 38, MinHeight = 0, MinWidth = 0, Padding = new Thickness(2), Margin = new Thickness(0, 1, 0, 1),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Rail.ToolButton");
        AutomationName(button, UiText.Format("Text_1BA9F7001237", label, ShortcutCatalog.Get(ShortcutScope.Annotation, tool.ToString()).Hint));
        AttachEditorShortcut(button, ShortcutScope.Annotation, tool.ToString(), corner: true);
        PreserveEditorMouseFocus(button);
        button.Click += (_, _) => SelectTool(tool);
        _toolButtons[tool] = button;
        parent.Children.Add(button);
    }

    private Button IconButton(
        string automationName,
        string tooltip,
        string iconResourceKey,
        Func<Geometry> fallback,
        Action onClick)
    {
        var button = new Button
        {
            Content = BuildIcon(iconResourceKey, fallback, CommandButtonPresentation.IconSize),
            ToolTip = tooltip,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.GhostCompact");
        CommandButtonPresentation.Apply(button, compact: true);
        AutomationName(button, automationName);
        PreserveEditorMouseFocus(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button IconTextButton(string label, string tooltip, string iconResourceKey, Func<Geometry> fallback, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(BuildIcon(iconResourceKey, fallback, CommandButtonPresentation.IconSize));
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(CommandButtonPresentation.IconLabelGap, 0, 0, 0),
        });

        var button = new Button
        {
            Content = content,
            ToolTip = tooltip,
            MinWidth = 48,
            Margin = new Thickness(2, 0, 2, 0),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.GhostCompact");
        AutomationName(button, label);
        PreserveEditorMouseFocus(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button TextButton(string label, string tooltip, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            ToolTip = tooltip,
            MinWidth = 48,
            Margin = new Thickness(2, 0, 2, 0),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        AutomationName(button, label);
        PreserveEditorMouseFocus(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void PreserveEditorMouseFocus(ButtonBase button)
    {
        // Mouse commands leave shortcuts on the canvas. Tab and automation can
        // still focus the same button and activate it with Space or Enter.
        button.PreviewGotKeyboardFocus += (_, e) =>
        {
            if (ReferenceEquals(e.NewFocus, button) && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                e.Handled = true;
                Focus();
            }
        };
    }

    private Border Separator() => new()
    {
        Width = 1,
        Height = 28,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 4, 6, 4),
        Background = Brush("Border.Subtle", Colors.Gray),
    };

    private static TextBlock SectionLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        return label;
    }

    /// <summary>
    /// The number box for an inspector slider. Enter and Esc hand the keyboard back to the
    /// canvas, as the custom colour box does, so the next letter is a tool shortcut again.
    /// </summary>
    private SliderValueEntry ValueEntry(Slider slider, string unit, string name)
    {
        var entry = new SliderValueEntry(slider, unit, name);
        entry.Finished += (_, _) => Focus();
        // A value past the range is applied at the nearest limit; the status says which.
        entry.Clamped += (_, value) => SetStatus(UiText.Format("SliderValue_Clamped",
            SliderValueEntry.Format(slider.Minimum), SliderValueEntry.Format(slider.Maximum), unit, SliderValueEntry.Format(value)));
        return entry;
    }

    /// <summary>A section label with the slider's value box at the right of the same row.</summary>
    private static FrameworkElement SliderHeader(string label, SliderValueEntry entry)
    {
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(entry.Element, Dock.Right);
        row.Children.Add(entry.Element);
        TextBlock text = SectionLabel(label);
        text.Margin = new Thickness(0, 0, 8, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// Renders every shared glyph on its authored 20x20 grid. A fixed canvas prevents WPF's
    /// Viewbox from normalising each geometry by its own bounds (the root cause of narrow icons
    /// becoming oversized and apparently clipped). Stroke follows the nearest control foreground,
    /// so hover, selected, primary and disabled states stay coherent.
    /// </summary>
    private Viewbox BuildIcon(string resourceKey, Func<Geometry> fallback, double size)
    {
        Geometry geometry;
        try
        {
            geometry = Application.Current?.TryFindResource(resourceKey) as Geometry ?? fallback();
        }
        catch (ResourceReferenceKeyNotFoundException)
        {
            geometry = fallback();
        }

        var glyph = new Path
        {
            Data = geometry,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Stretch = Stretch.None,
            SnapsToDevicePixels = false,
        };
        glyph.SetBinding(
            Shape.StrokeProperty,
            new Binding(nameof(Control.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
                FallbackValue = Brush("Text.Secondary", Colors.LightGray),
            });

        var grid = new Canvas
        {
            Width = 20,
            Height = 20,
            ClipToBounds = false,
        };
        grid.Children.Add(glyph);

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = grid,
            SnapsToDevicePixels = false,
        };
    }

    // Built-in 20x20 outline fallbacks (round caps/joins) matching the design symbol family, used
    // only when no shared Icon.* resource is present so tools always render.
    private static Geometry FallbackSelect() => Geometry.Parse("M4,3 L4,15 L8,11 L11,17 L13,16 L10,10 L16,10 Z");

    private static Geometry FallbackRectangle() => Geometry.Parse("M3,4 H17 V16 H3 Z");

    private static Geometry FallbackArrow() => Geometry.Parse("M4,16 L16,4 M16,4 L16,10 M16,4 L10,4");

    private static Geometry FallbackPen() => Geometry.Parse("M3,17 C6,10 9,13 12,7 C13,5 15,4 16,5 C17,6 16,8 14,9");

    private static Geometry FallbackText() => Geometry.Parse("M4,4 H16 M10,4 V16 M7,16 H13");

    private static Geometry FallbackImage() => Geometry.Parse("M3,4 H17 V16 H3 Z M3,13 L8,9 L11,12 L14,9 L17,12 M12.5,7.5 A1,1 0 1 1 12.4,7.5");

    private static Geometry FallbackMosaic() => Geometry.Parse("M3,4 H17 V16 H3 Z M3,9 H9 V4 M9,9 H15 M9,9 V16 M15,9 V14 A2,2 0 0 1 13,16");

    private static Geometry FallbackBlur() => Geometry.Parse("M10,2.75 C10,2.75 4.75,8.6 4.75,12 A5.25,5.25 0 0 0 15.25,12 C15.25,8.6 10,2.75 10,2.75 Z M7.6,12.4 A2.4,2.4 0 0 0 10,14.8");

    private static Geometry FallbackCrop() => Geometry.Parse("M5,2 V14 A1,1 0 0 0 6,15 H18 M2,5 H14 A1,1 0 0 1 15,6 V18");

    private static Geometry FallbackUndo() => Geometry.Parse("M8,6 L4,10 L8,14 M4,10 H13 A4,4 0 0 1 13,18");

    private static Geometry FallbackRedo() => Geometry.Parse("M12.75,5.25 L16.5,9 L12.75,12.75 M16.25,9 H8.75 A5,5 0 0 0 5.25,17");

    // Same as the shared Icon.RotateLeft/RotateRight: a picture under a half-turn arrow.
    private static Geometry FallbackRotateLeft() => Geometry.Parse("M7,8.75 H13 A1,1 0 0 1 14,9.75 V16 A1,1 0 0 1 13,17 H7 A1,1 0 0 1 6,16 V9.75 A1,1 0 0 1 7,8.75 Z M17,10.75 A7,7 0 0 0 3,10.75 M1,8.75 L3,10.75 L5,8.75");

    private static Geometry FallbackRotateRight() => Geometry.Parse("M13,8.75 H7 A1,1 0 0 0 6,9.75 V16 A1,1 0 0 0 7,17 H13 A1,1 0 0 0 14,16 V9.75 A1,1 0 0 0 13,8.75 Z M3,10.75 A7,7 0 0 1 17,10.75 M19,8.75 L17,10.75 L15,8.75");

    private static Geometry FallbackCopy() => Geometry.Parse("M7,3.5 H15.25 A1.25,1.25 0 0 1 16.5,4.75 V13 H7 Z M5,6.5 H4.75 A1.25,1.25 0 0 0 3.5,7.75 V15.25 A1.25,1.25 0 0 0 4.75,16.5 H12.25 A1.25,1.25 0 0 0 13.5,15.25 V15");

    private static Geometry FallbackCheck() => Geometry.Parse("M3.75,10.25 L8.15,14.65 L16.25,5.35");

    private static Geometry FallbackSave() => Geometry.Parse("M4,3.5 H13.25 L16.5,6.75 V16.5 H3.5 V4 Z M6.5,3.5 V8 H13.5 V3.75 M6.5,16.5 V11 H13.5 V16.5");

    private static Geometry FallbackSaveAs() => Geometry.Parse("M3.5,3.75 H11.75 L14.75,6.75 V10 M6.25,3.75 V7.75 H11.75 V4 M6,16.25 H3.5 V4.25 M8.5,15.75 L9.15,12.95 L14.7,7.4 A1.35,1.35 0 0 1 16.6,9.3 L11.05,14.85 Z");

    private static Geometry FallbackChevronDown() => Geometry.Parse("M5.25,7.5 L10,12.25 L14.75,7.5");

    // ---- Selection & tool state ----------------------------------------------------

    private void SelectTool(EditorTool tool)
    {
        _autoTrimCts?.Cancel();
        if (_edgeCropStart is not null) { _edgeCropStart = null; ReleasePointer(); }
        if (_activeTextBox is not null)
        {
            CommitActiveText();
        }

        bool cancelledCrop = _crop is not null && tool != EditorTool.Crop;
        if (cancelledCrop) { _crop = null; ReleasePointer(); }
        _controller.Tool = tool;
        if (tool == EditorTool.Crop && _crop is null)
        {
            _controller.SetSelected(null);
            _crop = new CropSelection(_canvasWidth, _canvasHeight);
            _cropRatio.SelectedIndex = 0;
        }
        RefreshCrop();
        if (tool is not (EditorTool.Select or EditorTool.Crop)) _preferredTool = tool;
        RememberPreferences();
        SyncToolButtons();
        ApplyCursor(default);
        UpdateInspector();
        // Switching tools is not a result to report (the rail shows the active tool); leaving
        // crop without applying it is.
        if (cancelledCrop) SetStatus(UiText.Get("Crop.Cancelled"));
    }

    private void RememberPreferences()
    {
        if (!_preferencesReady || AnnotationEditorPreferences.Write is null) return;
        AnnotationDefaults previous = AnnotationEditorPreferences.Read?.Invoke() ?? new AnnotationDefaults();
        AnnotationEditorPreferences.Write(new AnnotationDefaults
        {
            LastTool = _preferredTool.ToString(), StrokeColor = _controller.StrokeColor,
            StrokeThickness = _controller.StrokeThickness, StrokeStyle = _controller.StrokeStyle,
            FillTransparency = _controller.FillTransparency, FontSize = _controller.DefaultFontSize,
            FontFamily = _controller.DefaultFontFamily, TextColor = previous.TextColor,
            TextBold = _defaultTextBold, TextBackground = _defaultTextBackground, TextOutline = _defaultTextOutline,
            RecentFontFamilies = [.. _recentFontFamilies],
            MosaicBlockSize = previous.MosaicBlockSize, HighlighterAlpha = previous.HighlighterAlpha,
            RecentColors = [.. previous.RecentColors], BlurRadius = _controller.BlurRadius,
        });
    }

    private void SyncToolButtons()
    {
        foreach ((EditorTool tool, ToggleButton button) in _toolButtons)
        {
            button.IsChecked = _controller.Tool == tool;
        }
    }

    private void DeleteSelected()
    {
        bool had = _controller.Selected is not null;
        _controller.DeleteSelected();
        if (had)
        {
            SetStatus(UiText.Get("Text_5D27F0D47A0B"));
        }
    }

    private void OnSelectionChanged()
    {
        UpdateInspector();
        RefreshHistoryButtons();

        if (_controller.SelectedItems.Count > 1)
        {
            SetStatus(UiText.Format("EditorSelection_Count", _controller.SelectedItems.Count));
        }
        else if (_controller.Selected is TextAnnotation { IsStepNumber: true } badge && _controller.Tool == EditorTool.StepNumber)
        {
            // The step tool selects the badge it just added: report the result only. The next
            // number is already on the panel.
            SetStatus(UiText.Format("EditorFlow_StepNumberAdded", badge.Text));
        }
        else if (_controller.Selected is { } selected)
        {
            SetStatus(UiText.Format("EditorFlow_Selected", ItemName(selected)));
        }
    }

    private static string ItemName(AnnotationItem item) =>
        item is TextAnnotation { IsStepNumber: true } badge ? UiText.Format("EditorUx_StepNumberSelected", badge.Text) : item.DisplayName;

    private void OnHistoryChanged()
    {
        _documentRevision++;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        _autoTrimCts?.Cancel();
        _imageStore.PruneToReachable(_controller.Document, _controller.Undo);
        SyncBaseBitmapWithDocument();
        _dimensionsText.Text = UiText.Format("Text_009548FC2109", _canvasWidth, _canvasHeight);
        if (_crop is not null)
        {
            // A rotation (or undo) inside crop mode changes the canvas: start the frame over on
            // the new image and keep the ratio the user picked.
            _crop = new CropSelection(_canvasWidth, _canvasHeight);
            ApplyCropRatio();
            RefreshCrop();
        }
        RefreshHistoryButtons();
        UpdateInspector();
        if (_controller.Tool == EditorTool.StepNumber) ApplyCursor(_pointerTarget);
    }

    /// <summary>
    /// Re-derives the base bitmap after any undo-stack change so the visible pixels match
    /// the document canvas in its current orientation: the capture rotated by the live
    /// turn count, with any crop cut back out of it at the crop origin. Re-cutting from
    /// the rotated base keeps crop undo/redo correct after a rotation, which the unrotated
    /// selection alone cannot supply (its coordinate space no longer matches). The turn
    /// count lives on the controller because canvas width/height cannot distinguish 0
    /// from 180 degrees.
    /// </summary>
    private void SyncBaseBitmapWithDocument()
    {
        int turns = _controller.RotationTurns;
        int width = Math.Max(1, _controller.Document.CanvasWidth);
        int height = Math.Max(1, _controller.Document.CanvasHeight);
        if (turns == _rotationTurns && width == _canvasWidth && height == _canvasHeight)
        {
            return;
        }

        _rotationTurns = turns;
        BitmapSource rotated = _selectedBitmap;
        for (int pass = 0; pass < turns; pass++)
        {
            rotated = RotateQuarterClockwise(rotated);
        }

        if (width == rotated.PixelWidth && height == rotated.PixelHeight)
        {
            SwapBaseBitmap(rotated, new RectD(0, 0, rotated.PixelWidth, rotated.PixelHeight));
            return;
        }

        ApplyCropFromOriginal(width, height, rotated);
    }

    private void ApplyCropFromOriginal(int width, int height, BitmapSource baseBitmap)
    {
        PointD origin = _controller.CropOrigin;
        int x = Math.Clamp((int)Math.Round(origin.X), 0, baseBitmap.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(origin.Y), 0, baseBitmap.PixelHeight - 1);
        width = Math.Min(width, baseBitmap.PixelWidth - x);
        height = Math.Min(height, baseBitmap.PixelHeight - y);
        if (width < 1 || height < 1)
        {
            return;
        }

        int cropStride = width * 4;
        byte[] crop = ReadCropPixels(baseBitmap, x, y, width, height);

        var cropped = BitmapSource.Create(
            width,
            height,
            baseBitmap.DpiX,
            baseBitmap.DpiY,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            crop,
            cropStride);
        cropped.Freeze();
        SwapBaseBitmap(cropped, new RectD(0, 0, width, height));
    }

    private static byte[] ReadCropPixels(BitmapSource bitmap, int x, int y, int width, int height)
    {
        int cropStride = width * 4;
        byte[] crop = new byte[cropStride * height];
        if (bitmap.Format.BitsPerPixel == 32)
        {
            // Read only the selected pixels, including on history restoration. Keep the
            // raw channel bytes: the crop's existing output format and DPI stay unchanged.
            bitmap.CopyPixels(new Int32Rect(x, y, width, height), crop, cropStride, 0);
        }
        else
        {
            // Preserve the existing four-byte addressing for other source formats. A
            // format correction would change pixels and is separate from this optimization.
            int stride = bitmap.PixelWidth * 4;
            byte[] source = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(source, stride, 0);
            for (int row = 0; row < height; row++)
                Buffer.BlockCopy(source, ((y + row) * stride) + (x * 4), crop, row * cropStride, cropStride);
        }
        return crop;
    }

    private void RefreshHistoryButtons()
    {
        if (_undoButton is null)
        {
            return;
        }

        _undoButton.IsEnabled = _controller.CanUndo;
        _redoButton.IsEnabled = _controller.CanRedo;
        _undoButton.ToolTip = _controller.Undo.NextUndoDescription is { } undo
            ? UiText.Format("EditorSelection_UndoHint", undo) : UiText.Get("Text_C61A081610C9");
        _redoButton.ToolTip = _controller.Undo.NextRedoDescription is { } redo
            ? UiText.Format("EditorSelection_RedoHint", redo) : UiText.Get("Text_F457141C336A");
    }

    /// <summary>
    /// Reflects the current tool and selection into the inspector: the object or tool name and
    /// the values that apply to it (colour, thickness, the next step number, the crop size),
    /// and Delete only when there is a selection.
    /// </summary>
    private void UpdateInspector()
    {
        if (_inspectorTitle is null)
        {
            return;
        }

        AnnotationItem? selected = _controller.Selected;
        bool hasSelection = selected is not null;

        _deleteButton.IsEnabled = hasSelection;
        _deleteButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        bool stepTool = _controller.Tool == EditorTool.StepNumber;
        _inspectorTitle.Text = _controller.SelectedItems.Count > 1
            ? UiText.Format("EditorSelection_Count", _controller.SelectedItems.Count)
            : selected is not null ? ItemName(selected) : ToolName(_controller.Tool);

        bool colorApplies = hasSelection ? _controller.SelectedItems.Any(item => ColorApplies(item, _controller.Tool)) : ColorApplies(null, _controller.Tool);
        bool thicknessApplies = hasSelection ? _controller.SelectedItems.Any(item => ThicknessApplies(item, _controller.Tool)) : ThicknessApplies(null, _controller.Tool);
        bool shapeApplies = _controller.SelectedItems.Any(item => item is ShapeAnnotation) || (selected is null && _controller.Tool is EditorTool.Rectangle or EditorTool.Ellipse);
        bool fontSizeApplies = selected is TextAnnotation || (selected is null && _controller.Tool == EditorTool.Text);
        bool blurApplies = selected is BlurAnnotation || (selected is null && _controller.Tool == EditorTool.Blur);

        bool cropping = _crop is not null;
        _cropSection.Visibility = cropping ? Visibility.Visible : Visibility.Collapsed;
        _deleteButton.Visibility = cropping ? Visibility.Collapsed : Visibility.Visible;
        _colorSection.Visibility = colorApplies ? Visibility.Visible : Visibility.Collapsed;
        _thicknessSection.Visibility = thicknessApplies ? Visibility.Visible : Visibility.Collapsed;
        _shapeStyleSection.Visibility = shapeApplies ? Visibility.Visible : Visibility.Collapsed;
        _fontSizeSection.Visibility = fontSizeApplies ? Visibility.Visible : Visibility.Collapsed;
        bool textStyleApplies = selected is TextAnnotation { IsStepNumber: false }
            || (selected is null && _controller.Tool == EditorTool.Text);
        _fontFamilySection.Visibility = textStyleApplies && !cropping ? Visibility.Visible : Visibility.Collapsed;
        _textStyleSection.Visibility = textStyleApplies && !cropping
            ? Visibility.Visible : Visibility.Collapsed;
        _blurSection.Visibility = blurApplies ? Visibility.Visible : Visibility.Collapsed;
        _stepSection.Visibility = stepTool && !cropping ? Visibility.Visible : Visibility.Collapsed;
        if (stepTool) UpdateStepPreview();

        _syncingInspector = true;
        try
        {
            if (textStyleApplies)
            {
                TextAnnotation? text = selected as TextAnnotation;
                _textBold.IsChecked = text?.Bold ?? _defaultTextBold;
                _textBackground.IsChecked = text is null ? _defaultTextBackground : text.Background.A > 0;
                _textOutline.IsChecked = text?.HasOutline ?? _defaultTextOutline;
                SelectFontFamily(text?.FontFamily ?? _controller.DefaultFontFamily);
            }
            if (thicknessApplies)
            {
                AnnotationStrokeStyle style = (selected as ShapeAnnotation)?.StrokeStyle ?? _controller.StrokeStyle;
                _thicknessSlider.Minimum = (selected is ShapeAnnotation || (selected is null && _controller.Tool is EditorTool.Rectangle or EditorTool.Ellipse))
                    && style == AnnotationStrokeStyle.ThickDashed ? 6 : 1;
                if (_controller.SelectedItems.Count > 1 && _controller.SelectedItems.Any(item =>
                    item is PolylineAnnotation or PenAnnotation || item is ShapeAnnotation { StrokeStyle: not AnnotationStrokeStyle.ThickDashed }))
                    _thicknessSlider.Minimum = 1;
                SyncThicknessFromSelection(selected);
            }

            if (shapeApplies)
            {
                ShapeAnnotation? shape = selected as ShapeAnnotation;
                AnnotationStrokeStyle[] styles = _controller.SelectedItems.OfType<ShapeAnnotation>()
                    .Select(item => item.StrokeStyle).Distinct().Take(2).ToArray();
                _strokeStyleComboBox.SelectedIndex = styles.Length > 1 ? -1
                    : (int)(styles.Length == 1 ? styles[0] : _controller.StrokeStyle);
                _fillTransparencySlider.Value = shape?.FillTransparency ?? _controller.FillTransparency;
            }

            if (fontSizeApplies)
            {
                double fontSize = (selected as TextAnnotation)?.FontSize ?? _controller.DefaultFontSize;
                double clamped = Math.Clamp(fontSize, _fontSizeSlider.Minimum, _fontSizeSlider.Maximum);
                if (Math.Abs(_fontSizeSlider.Value - clamped) > 0.001)
                {
                    _fontSizeSlider.Value = clamped;
                }
            }

            if (blurApplies)
            {
                _blurSlider.Value = (selected as BlurAnnotation)?.Radius ?? _controller.BlurRadius;
            }
        }
        finally
        {
            _syncingInspector = false;
        }
    }

    private void SyncThicknessFromSelection(AnnotationItem? selected)
    {
        double thickness = selected switch
        {
            ShapeAnnotation shape => shape.StrokeThickness,
            PolylineAnnotation line => line.StrokeThickness,
            PenAnnotation pen => pen.StrokeThickness,
            _ => _controller.StrokeThickness,
        };

        double clamped = Math.Clamp(thickness, _thicknessSlider.Minimum, _thicknessSlider.Maximum);
        if (Math.Abs(_thicknessSlider.Value - clamped) > 0.001)
        {
            _thicknessSlider.Value = clamped;
        }
    }

    private static bool ColorApplies(AnnotationItem? selected, EditorTool tool)
    {
        if (selected is not null)
        {
            return selected is ShapeAnnotation or PolylineAnnotation or PenAnnotation or TextAnnotation;
        }

        return tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Pen or EditorTool.Highlighter or EditorTool.StepNumber or EditorTool.Text;
    }

    private static bool ThicknessApplies(AnnotationItem? selected, EditorTool tool)
    {
        if (selected is not null)
        {
            return selected is ShapeAnnotation or PolylineAnnotation or PenAnnotation;
        }

        return tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Arrow or EditorTool.Pen or EditorTool.Highlighter;
    }

    private static string ToolName(EditorTool tool) => tool switch
    {
        EditorTool.Crop => UiText.Get("Tools_Crop"),
        EditorTool.Ellipse => UiText.Get("Tools.Ellipse"),
        EditorTool.Highlighter => UiText.Get("Tools.Highlighter"),
        EditorTool.StepNumber => UiText.Get("Tools.StepNumber"),
        EditorTool.Select => UiText.Get("Text_33406FC582BE"),
        EditorTool.Rectangle => UiText.Get("Text_4A48782E904C"),
        EditorTool.Arrow => UiText.Get("Text_D5A82B3B1F83"),
        EditorTool.Pen => UiText.Get("Text_CEFF481585C2"),
        EditorTool.Text => UiText.Get("Text_DAB3F35F5BD0"),
        EditorTool.Image => UiText.Get("Text_A5E52C2DDFC0"),
        EditorTool.Blur => UiText.Get("EditorBlur_ToolTitle"),
        EditorTool.Mosaic => UiText.Get("Text_3F8046019B32"),
        _ => UiText.Get("Text_36C416EAD2CE"),
    };

    private void SetStatus(string message)
    {
        if (_activeTextBox is not null)
            message = UiText.Get(_controller.IsCreatingText ? "EditorText_Entering" : "EditorText_Editing");
        if (_statusText is null)
        {
            return;
        }

        if (string.Equals(_statusText.Text, message, StringComparison.Ordinal))
        {
            // Re-raise the automation event even when the text is identical, so repeated
            // gestures (e.g. adding two rectangles) are still announced.
            RaiseStatusAutomationEvent();
            return;
        }

        _statusText.Text = message;
        RaiseStatusAutomationEvent();
    }

    /// <summary>Runs local OCR and adds every high-confidence privacy cover as one undo step.</summary>
    internal async Task ApplyPrivacyRedactionsAsync()
    {
        if (_redactionInProgress || _commitInProgress)
        {
            return;
        }

        if (_privacyRedactionService is null || !_privacyRedactionService.IsAvailable)
        {
            SetStatus(UiText.Get("Text_EE36971D5C4B"));
            return;
        }

        CommitActiveText();
        _redactionInProgress = true;
        _redactButton.IsEnabled = false;
        _redactionCts?.Dispose();
        _redactionCts = new CancellationTokenSource();
        CancellationTokenSource operation = _redactionCts;
        SetStatus(UiText.Get("Text_3B5DD12EE3D1"));

        try
        {
            // Detect on the base the user currently sees so covers land in document space
            // even after a rotation or crop.
            PrivacyRedactionResult result = await _privacyRedactionService.FindAsync(
                _surface.Frame.Bitmap,
                operation.Token);
            if (operation.IsCancellationRequested)
            {
                return;
            }

            switch (result.Status)
            {
                case PrivacyRedactionStatus.Success:
                    int count = _controller.AddPrivacyRedactions(result.Regions);
                    SetStatus(UiText.Format("Text_E68817537C75", count));
                    break;
                case PrivacyRedactionStatus.NoMatches:
                    SetStatus(UiText.Get("Text_5DBC2621FC1E"));
                    break;
                case PrivacyRedactionStatus.Unavailable:
                    SetStatus(UiText.Get("Text_EE36971D5C4B"));
                    break;
                case PrivacyRedactionStatus.Failed:
                    SetStatus(UiText.Get("Text_6D63E8966A24"));
                    break;
                case PrivacyRedactionStatus.Cancelled:
                    SetStatus(UiText.Get("Text_6AE98E57E121"));
                    break;
            }
        }
        finally
        {
            if (ReferenceEquals(_redactionCts, operation))
            {
                _redactionCts.Dispose();
                _redactionCts = null;
            }

            _redactionInProgress = false;
            _redactButton.IsEnabled = _privacyRedactionService.IsAvailable;
        }
    }

    /// <summary>
    /// Recognises the text in the image as edited so far (marks, covers, crop and rotation
    /// included) and opens it in the shared text recognition window.
    /// </summary>
    internal async Task RecognizeCurrentTextAsync()
    {
        if (_textRecognitionInProgress || _commitInProgress || _completed) return;
        if (!TextRecognitionRoute.CanShow || TextRecognitionRoute.Show is not { } show)
        {
            SetStatus(UiText.Get("TextCapture_Unavailable"));
            RefreshCopyTextAvailability();
            return;
        }

        CommitActiveText();
        _textRecognitionInProgress = true;
        _copyTextButton.IsEnabled = false;
        try
        {
            BitmapSource image = await RenderCurrentImageAsync();
            if (_completed) return;
            show(image, string.IsNullOrWhiteSpace(_sourceWindowTitle) ? UiText.Get("EditorFlow_CopyText") : _sourceWindowTitle);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetStatus(UiText.Get("TextCapture_Failed"));
        }
        finally
        {
            _textRecognitionInProgress = false;
            RefreshCopyTextAvailability();
        }
    }

    private void RefreshCopyTextAvailability()
    {
        bool available = TextRecognitionRoute.CanShow;
        _copyTextButton.IsEnabled = available && !_textRecognitionInProgress;
        AutomationProperties.SetHelpText(_copyTextButton,
            UiText.Get(available ? "EditorFlow_CopyTextHint" : "TextCapture_Unavailable"));
    }

    private void RaiseStatusAutomationEvent()
    {
        if (_statusText is null || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        AutomationPeer? peer = UIElementAutomationPeer.FromElement(_statusText)
            ?? UIElementAutomationPeer.CreatePeerForElement(_statusText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ---- Responsive layout ---------------------------------------------------------

    private void UpdateResponsiveLayout()
    {
        if (_inspectorColumn is null || _inspectorPanel is null)
        {
            return;
        }

        // Keep styling reachable in compact windows; vertical scrolling handles short heights.
        bool compact = ActualWidth > 0 && ActualWidth < InspectorCompactWidth;

        if (compact == _inspectorCompact)
        {
            return;
        }

        _inspectorCompact = compact;
        _inspectorColumn.Width = new GridLength(compact ? 200 : InspectorWidth);
        _inspectorPanel.Padding = new Thickness(compact ? 12 : 18);
        _inspectorPanel.Visibility = Visibility.Visible;
    }

    // ---- Commit / cancel -----------------------------------------------------------

    private async void Commit(EditorCommitAction action, bool reduceExport = false)
    {
        if (_completed || _commitInProgress)
        {
            return;
        }

        if (_redactionInProgress)
        {
            SetStatus(UiText.Get("Text_D0FD12A01E4A"));
            return;
        }

        if (_activeTextBox is not null)
        {
            CommitActiveText();
        }

        // Validate both inputs even when the crop still spans the full image.
        if (_crop is not null && !CommitCropInputs()) return;
        bool appliedCrop = _crop is { } pending && pending.Region != pending.Bounds;
        if (appliedCrop)
        {
            ApplyPendingCrop();
            if (_crop is not null) return; // An invalid tiny crop stays editable.
        }

        AnnotationDocument document = _controller.Document;
        document.NormalizeZIndices();

        // Flatten exactly what the editor showed: the surface's base already carries any
        // rotation and crop, and committing the untouched selection would place transformed
        // annotations at stale offsets (or write rotated annotations onto an unrotated
        // image). The reported region keeps the captured desktop rect while the document is
        // untouched, follows the crop origin once a crop shrinks the canvas, and falls back
        // to the canvas rect under rotation where the desktop mapping no longer applies.
        BitmapSource baseBitmap = _surface.Frame.Bitmap;
        RectD baseRegion = _rotationTurns == 0
            ? new RectD(
                _originalCropRegion.X + _controller.CropOrigin.X,
                _originalCropRegion.Y + _controller.CropOrigin.Y,
                Math.Max(1, document.CanvasWidth),
                Math.Max(1, document.CanvasHeight))
            : _surface.CropRegion;

        IEnumerable<string> usedAssets = document.Items
            .OfType<ImageAnnotation>()
            .Select(i => i.AssetFileName)
            .ToList();

        var result = new AnnotationEditingResult(
            _frame,
            baseRegion,
            baseBitmap,
            document,
            action,
            _imageStore.DecodedFor(usedAssets),
            _imageStore.SourcesFor(usedAssets),
            reduceExport,
            _sourceWindowTitle,
            _showSourceWindowTitle,
            _rotationTurns,
            _controller.CropOrigin);

        // Keep the editor still while background PNG encoding or clipboard contention
        // resolves, so a second command cannot interleave with this one.
        _commitInProgress = true;
        IsHitTestVisible = false;
        bool copyOnly = action == EditorCommitAction.CopyToClipboard;
        string commitStatus = copyOnly ? UiText.Get("Text_78666F37A521") : UiText.Get("Text_88DAAEEDFE4C");
        SetStatus(appliedCrop ? UiText.Get("Crop.Applied") + " · " + commitStatus : commitStatus);
        bool succeeded;
        try
        {
            // Copy needs a host that can reach the clipboard; a closing commit with no host
            // simply finishes, as before.
            succeeded = CommitRequested is null ? !copyOnly : await CommitRequested(result);
        }
        catch (Exception)
        {
            succeeded = false;
        }

        if (succeeded && !copyOnly)
        {
            _completed = true;
            EditingCompleted?.Invoke(this, result);
            return;
        }

        // Copy is a secondary action: the editor stays open either way and the status line
        // reports the outcome. A failed closing commit also stays open so the user can retry.
        _commitInProgress = false;
        IsHitTestVisible = true;
        SetStatus(copyOnly
            ? UiText.Get(succeeded ? "EditorFlow_Copied" : "Text_3D1E714B56CA")
            : UiText.Get("Text_06F58809C083"));
    }

    /// <summary>
    /// Esc: cancel typing (handled before this runs), else clear the selection, else go back
    /// to the select tool. Crop mode has its own Esc (cancel the frame). It never closes.
    /// </summary>
    private void StepBack()
    {
        if (_completed || _commitInProgress)
        {
            return;
        }

        _controller.CancelGesture();
        ReleasePointer();
        if (_controller.Selected is not null)
        {
            _controller.SetSelected(null);
            SetStatus(UiText.Get("EditorFlow_SelectionCleared"));
        }
        else if (_controller.Tool != EditorTool.Select)
        {
            SelectTool(EditorTool.Select);
        }
    }

    /// <summary>The Cancel command: asks the host window to close (it confirms unsaved edits).</summary>
    private void RequestClose()
    {
        if (_completed || _commitInProgress)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- Resource helpers ----------------------------------------------------------

    private static Border ThemedBorder(Border border, string background)
    {
        border.SetResourceReference(Border.BackgroundProperty, background);
        border.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        return border;
    }

    private static Brush Brush(string key, Color fallback) => ThemeBrushes.Follow(key, fallback);

    private static FontFamily FontFamilyResource(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily(fallback);

    private static void AutomationName(DependencyObject element, string name) =>
        AutomationProperties.SetName(element, name);
}
