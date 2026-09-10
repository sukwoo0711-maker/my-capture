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
using Microsoft.Win32;
using MyCapture.App.Ocr;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;

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
/// Layout follows the warm-yellow/charcoal desktop UX direction: a calm top command bar
/// (document context and Undo/Redo on the left, a save overflow menu plus Cancel/Copy/Done
/// on the right), a fixed 52px left rail of vector-icon buttons, a central image-only
/// viewport, a 232px contextual inspector, and a bottom live-status region. There is no
/// horizontal toolbar scrolling. Every icon control carries a label, tooltip, automation
/// name, and keyboard route, and the status region is an automation live region so screen
/// readers hear each gesture result.
/// </para>
/// </remarks>
internal sealed class AnnotationEditorControl : Grid
{
    private EditorTool _preferredTool = EditorTool.Rectangle;
    private bool _preferencesReady;
    private const double ToolRailWidth = 52;
    private const double InspectorWidth = 232;

    // Below this width the inspector collapses before it can squeeze the image workspace.
    // Normal editor startup now targets a comfortable width above this threshold.
    private const double InspectorCompactWidth = 860;

    private readonly AnnotationSourceMetadata _frame;
    private readonly RectD _cropRegion;
    private readonly BitmapSource _selectedBitmap;
    private readonly AnnotationImageStore _imageStore = new();
    private readonly AnnotationEditorController _controller;
    private readonly AnnotationEditorSurface _surface;
    private readonly IPrivacyRedactionService? _privacyRedactionService;
    private readonly Grid _viewport = new();
    private readonly Canvas _overlayCanvas = new();
    private readonly Dictionary<EditorTool, ToggleButton> _toolButtons = new();

    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Button _deleteButton = null!;
    private Button _redactButton = null!;
    private Slider _thicknessSlider = null!;
    private ComboBox _strokeStyleComboBox = null!;
    private Slider _fillTransparencySlider = null!;
    private TextBlock _fillTransparencyLabel = null!;
    private FrameworkElement _shapeStyleSection = null!;
    private bool _syncingInspector;
    private ColumnDefinition _inspectorColumn = null!;
    private Border _inspectorPanel = null!;
    private WrapPanel _swatchPanel = null!;
    private FrameworkElement _colorSection = null!;
    private FrameworkElement _thicknessSection = null!;
    private TextBlock _inspectorTitle = null!;
    private TextBlock _inspectorInstruction = null!;
    private TextBlock _statusText = null!;
    private bool _inspectorCompact;

    private TextBox? _activeTextBox;
    private TextAnnotation? _editingText;
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
        IPrivacyRedactionService? privacyRedactionService = null)
    {
        _frame = AnnotationSourceMetadata.FromFrame(frame);
        _cropRegion = bitmapRegion.Normalized();
        _selectedBitmap = selectedBitmap ?? throw new ArgumentNullException(nameof(selectedBitmap));
        _privacyRedactionService = privacyRedactionService;

        _canvasWidth = Math.Max(1, selectedBitmap.PixelWidth);
        _canvasHeight = Math.Max(1, selectedBitmap.PixelHeight);

        // Seed decoded assets before wiring the renderer so restored image annotations draw
        // on the first paint.
        if (initialAssets is not null)
        {
            _imageStore.Seed(initialAssets);
        }

        AnnotationDocument document = initialDocument ?? AnnotationDocument.CreateFor(_canvasWidth, _canvasHeight);
        var undo = new UndoStack();
        _controller = new AnnotationEditorController(document, undo);
        AnnotationDefaults defaults = AnnotationEditorPreferences.Read?.Invoke() ?? new AnnotationDefaults();
        _controller.StrokeColor = defaults.StrokeColor;
        _controller.StrokeThickness = double.IsFinite(defaults.StrokeThickness) ? Math.Clamp(defaults.StrokeThickness, 1, 24) : 3;
        _controller.ApplyStrokeStyle(Enum.IsDefined(defaults.StrokeStyle) ? defaults.StrokeStyle : AnnotationStrokeStyle.Solid);
        _controller.ApplyFillTransparency(double.IsFinite(defaults.FillTransparency) ? Math.Clamp(defaults.FillTransparency, 0, 100) : 100);
        _controller.DefaultFontSize = double.IsFinite(defaults.FontSize) ? Math.Clamp(defaults.FontSize, 8, 200) : 18;
        _controller.DefaultFontFamily = string.IsNullOrWhiteSpace(defaults.FontFamily) ? "Malgun Gothic" : defaults.FontFamily;
        if (Enum.TryParse(defaults.LastTool, out EditorTool lastTool) && Enum.IsDefined(lastTool)) _preferredTool = lastTool;
        _imageStore.PruneToReachable(document, undo);
        var renderer = new AnnotationRenderer(_imageStore);

        var visualRegion = new RectD(0, 0, _canvasWidth, _canvasHeight);
        var visualFrame = new FrozenFrame(
            selectedBitmap,
            visualRegion,
            frame.Monitor,
            frame.ElapsedMilliseconds);
        _surface = new AnnotationEditorSurface(visualFrame, visualRegion, _controller, renderer);

        Background = Brush("Surface.Base", Color.FromRgb(0x0B, 0x0F, 0x17));
        Focusable = true;
        FocusVisualStyle = null;

        BuildLayout();

        _controller.SelectionChanged += (_, _) => OnSelectionChanged();
        undo.Changed += (_, _) => OnHistoryChanged();

        _viewport.MouseLeftButtonDown += OnSurfaceMouseDown;
        _viewport.MouseMove += OnSurfaceMouseMove;
        _viewport.MouseLeftButtonUp += OnSurfaceMouseUp;
        _viewport.MouseRightButtonDown += OnSurfaceRightDown;

        SizeChanged += (_, _) => UpdateResponsiveLayout();

        Loaded += (_, _) =>
        {
            UpdateResponsiveLayout();
            Focus();
        };
        Unloaded += (_, _) =>
        {
            _redactionCts?.Cancel();
            _redactionCts?.Dispose();
            _redactionCts = null;
        };

        SelectTool(_preferredTool);
        _preferencesReady = true;
        RefreshHistoryButtons();
        UpdateInspector();
    }

    /// <summary>Raised when the user commits the edit (Done / Ctrl+Enter).</summary>
    internal event EventHandler<AnnotationEditingResult>? EditingCompleted;

    /// <summary>Raised when the user cancels the edit (Esc / cancel button).</summary>
    internal event EventHandler? EditingCancelled;

    /// <summary>
    /// Invoked when the user asks to commit, before the editor closes. The
    /// handler flattens, persists, and performs any clipboard/export the action requires,
    /// and returns whether the editor should close. Returning <see langword="false"/> (a
    /// cancelled or failed Save As) leaves the editor open.
    /// </summary>
    internal Func<AnnotationEditingResult, Task<bool>>? CommitRequested { get; set; }

    internal BitmapSource DisplayedBitmap => _surface.Frame.Bitmap;

    internal RectD DisplayedRegion => _surface.CropRegion;

    /// <summary>
    /// The element that owns both pointer event handlers and mouse capture. Keeping these
    /// responsibilities on one element prevents WPF from rerouting drag move/up events to an
    /// ancestor and leaving a zero-size draft in the document.
    /// </summary>
    internal UIElement PointerInputElement => _viewport;

    internal bool IsPointerCaptured => _viewport.IsMouseCaptured;

    internal bool IsCommitInProgress => _commitInProgress && !_completed;

    internal bool CapturePointer() => _viewport.CaptureMouse();

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
        return HandleShortcut(e.Key, Keyboard.Modifiers);
    }

    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (_commitInProgress)
        {
            // The snapshot being persisted must remain immutable until the operation either
            // succeeds or reports that the editor should stay open.
            return true;
        }

        // While typing in a text box, only Escape/Ctrl+Enter are editor shortcuts.
        if (_activeTextBox is not null)
        {
            if (key == Key.Escape)
            {
                CommitActiveText();
                return true;
            }

            if (key == Key.Enter && modifiers.HasFlag(ModifierKeys.Control))
            {
                CommitActiveText();
                Commit(EditorCommitAction.Done);
                return true;
            }

            return false;
        }

        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);

        switch (key)
        {
            case Key.Enter when ctrl:
                Commit(EditorCommitAction.Done);
                return true;
            case Key.C when ctrl:
                Commit(EditorCommitAction.CopyToClipboard);
                return true;
            case Key.S when ctrl && shift:
                Commit(EditorCommitAction.SaveAs);
                return true;
            case Key.S when ctrl:
                Commit(EditorCommitAction.QuickSave);
                return true;
            case Key.R when ctrl && shift:
                _ = ApplyPrivacyRedactionsAsync();
                return true;
            case Key.Escape:
                Cancel();
                return true;
            case Key.Z when ctrl:
                if (_controller.PerformUndo())
                {
                    SetStatus(UiText.Get("Text_DAF93C3AA95D"));
                }

                return true;
            case Key.Y when ctrl:
                if (_controller.PerformRedo())
                {
                    SetStatus(UiText.Get("Text_8B22B7BBB0CD"));
                }

                return true;
            case Key.Delete:
            case Key.Back:
                DeleteSelected();
                return true;
            case Key.V:
                SelectTool(EditorTool.Select);
                return true;
            case Key.R:
                SelectTool(EditorTool.Rectangle);
                return true;
            case Key.A:
                SelectTool(EditorTool.Arrow);
                return true;
            case Key.P:
                SelectTool(EditorTool.Pen);
                return true;
            case Key.T:
                SelectTool(EditorTool.Text);
                return true;
            case Key.I:
                SelectTool(EditorTool.Image);
                return true;
        }

        return false;
    }

    // ---- Pointer -------------------------------------------------------------------

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTextBox is not null)
        {
            CommitActiveText();
            return;
        }

        Focus();
        Point dip = e.GetPosition(_surface);
        PointD image = _surface.ToImagePoint(dip);

        switch (_controller.Tool)
        {
            case EditorTool.Text:
                PlaceTextBox(image);
                e.Handled = true;
                return;
            case EditorTool.Image:
                InsertImage(image);
                e.Handled = true;
                return;
            default:
                _controller.HitTolerance = 6 / Math.Max(double.Epsilon, _surface.DipPerPixel);
                _controller.PointerDown(image);
                CapturePointer();
                e.Handled = true;
                break;
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_activeTextBox is not null)
        {
            return;
        }

        Point dip = e.GetPosition(_surface);
        PointD image = _surface.ToImagePoint(dip);

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _controller.PointerMove(image);
        }
        else if (_controller.Tool == EditorTool.Select)
        {
            UpdateCursor(image);
        }
        else
        {
            Cursor = Cursors.Cross;
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeTextBox is not null)
        {
            return;
        }

        EditorTool toolBeforeCommit = _controller.Tool;
        int itemsBefore = _controller.Document.Items.Count;

        Point dip = e.GetPosition(_surface);
        PointD image = _surface.ToImagePoint(dip);
        _controller.PointerUp(image);
        ReleasePointer();
        SyncToolButtons();

        // A gesture that produced a new object is worth announcing; a bare click that did not
        // is not. Pencil strokes intentionally stay unselected to avoid an interrupting
        // selection polygon, so report them from the tool that began the gesture.
        if (_controller.Document.Items.Count > itemsBefore)
        {
            if (toolBeforeCommit == EditorTool.Pen)
            {
                SetStatus(UiText.Get("Text_F554B87B81B3"));
            }
            else if (_controller.Selected is { } selected)
            {
                SetStatus(UiText.Format("Text_C7E7E4FA07DA", selected.DisplayName));
            }
        }
    }

    private void OnSurfaceRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTextBox is not null)
        {
            CommitActiveText();
        }

        _controller.SetSelected(null);
        SelectTool(EditorTool.Select);
        e.Handled = true;
    }

    private void UpdateCursor(PointD image)
    {
        double tolerance = 8 / Math.Max(double.Epsilon, _surface.DipPerPixel);
        ResizeHandle handle = _controller.HandleAt(image, tolerance);
        Cursor = handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            ResizeHandle.TopCenter or ResizeHandle.BottomCenter => Cursors.SizeNS,
            ResizeHandle.MiddleLeft or ResizeHandle.MiddleRight => Cursors.SizeWE,
            _ when _controller.HitTest(image) is not null => Cursors.SizeAll,
            _ => Cursors.Arrow,
        };
    }

    // ---- Text entry ----------------------------------------------------------------

    private void PlaceTextBox(PointD image)
    {
        double defaultWidth = 180 / Math.Max(double.Epsilon, _surface.DipPerPixel);
        double defaultHeight = 40 / Math.Max(double.Epsilon, _surface.DipPerPixel);
        TextAnnotation annotation = _controller.BeginTextAnnotation(image, defaultWidth, defaultHeight);
        _editingText = annotation;

        Rect box = _surface.ToSurfaceRect(annotation.Rect);
        var textBox = new TextBox
        {
            Width = Math.Max(60, box.Width),
            MinHeight = Math.Max(28, box.Height),
            FontSize = Math.Max(12, annotation.FontSize * _surface.DipPerPixel),
            Foreground = annotation.Foreground.ToBrush(),
            Background = Brush("Surface.Floating", Color.FromArgb(0xF2, 0x15, 0x1E, 0x2B)),
            BorderBrush = Brush("Accent.Default", Color.FromRgb(0x58, 0xC7, 0xF3)),
            BorderThickness = new Thickness(1),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(2),
        };
        AutomationName(textBox, UiText.Get("Text_FAC754F72C25"));

        Canvas.SetLeft(textBox, box.Left);
        Canvas.SetTop(textBox, box.Top);
        _overlayCanvas.Children.Add(textBox);
        _activeTextBox = textBox;
        SetStatus(UiText.Get("Text_230D26DEB9AE"));

        textBox.LostKeyboardFocus += (_, _) => CommitActiveText();
        _ = textBox.Focus();
    }

    private void CommitActiveText()
    {
        if (_activeTextBox is null || _editingText is null)
        {
            return;
        }

        TextBox box = _activeTextBox;
        TextAnnotation annotation = _editingText;
        _activeTextBox = null;
        _editingText = null;

        _overlayCanvas.Children.Remove(box);
        bool hadText = !string.IsNullOrEmpty(box.Text);
        _controller.CommitTextEdit(annotation, box.Text ?? string.Empty);
        SetStatus(hadText ? UiText.Get("Text_B09611FF35C1") : UiText.Get("Text_8BDB6F46B7FF"));
        UpdateInspector();
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
            MessageBox.Show(
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

        // Fit the image to at most a third of the crop, preserving aspect ratio, centred on
        // the click.
        double maxWidth = _cropRegion.Width / 3;
        double maxHeight = _cropRegion.Height / 3;
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
        SetStatus(UiText.Get("Text_D8F8A6B5650D"));
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
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var document = new TextBlock
        {
            Text = UiText.Get("Text_3E409AB1A37C"),
            Foreground = Brush("Text.Primary", Colors.White),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0),
        };
        left.Children.Add(document);
        left.Children.Add(Separator());

        _undoButton = IconButton(UiText.Get("Text_CE706412FA75"), UiText.Get("Text_C61A081610C9"), "Icon.Undo", FallbackUndo, () =>
        {
            if (_controller.PerformUndo())
            {
                SetStatus(UiText.Get("Text_DAF93C3AA95D"));
            }
        });
        _redoButton = IconButton(UiText.Get("Text_078C44D5A66E"), UiText.Get("Text_F457141C336A"), "Icon.Redo", FallbackRedo, () =>
        {
            if (_controller.PerformRedo())
            {
                SetStatus(UiText.Get("Text_8B22B7BBB0CD"));
            }
        });
        left.Children.Add(_undoButton);
        left.Children.Add(_redoButton);
        left.Children.Add(Separator());

        _redactButton = TextButton(
            UiText.Get("Text_E395E2567985"),
            UiText.Get("Text_A31A72A338DD"),
            "Button.Secondary",
            () => _ = ApplyPrivacyRedactionsAsync());
        _redactButton.MinWidth = 92;
        _redactButton.IsEnabled = _privacyRedactionService?.IsAvailable == true;
        AutomationProperties.SetHelpText(
            _redactButton,
            _redactButton.IsEnabled
                ? UiText.Get("Text_EB9AB8D7D13C")
                : UiText.Get("Text_39E626945336"));
        left.Children.Add(_redactButton);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        right.Children.Add(BuildSaveOverflowMenu());
        right.Children.Add(Separator());

        Button cancel = TextButton(UiText.Get("Text_BE876433993A"), UiText.Get("Text_22B802C1B7FF"), "Button.GhostCompact", Cancel);
        Button copy = IconTextButton(
            UiText.Get("Text_37B3D3B11B26"), UiText.Get("Text_74EEFE6E43AE"), "Icon.Copy", FallbackCopy,
            () => Commit(EditorCommitAction.CopyToClipboard));
        copy.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        copy.MinWidth = 76;

        Button done = IconTextButton(
            UiText.Get("Text_727333AB0740"), UiText.Get("Text_0B201CC58BDD"), "Icon.Check", FallbackCheck,
            () => Commit(EditorCommitAction.Done));
        done.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        done.MinWidth = 78;

        right.Children.Add(cancel);
        right.Children.Add(copy);
        right.Children.Add(done);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return new Border
        {
            Background = Brush("Surface.Raised", Color.FromRgb(0x10, 0x17, 0x22)),
            BorderBrush = Brush("Border.Subtle", Colors.Gray),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8, 12, 8),
            Child = grid,
            SnapsToDevicePixels = true,
        };
    }

    private Button BuildSaveOverflowMenu()
    {
        var menu = new ContextMenu { MinWidth = 230 };

        var quickSave = new MenuItem
        {
            Header = UiText.Get("Text_DFC084A111D0"),
            InputGestureText = "Ctrl+S",
            Icon = BuildIcon("Icon.Save", FallbackSave, 16),
        };
        quickSave.Click += (_, _) => Commit(EditorCommitAction.QuickSave);
        AutomationName(quickSave, UiText.Get("Text_DFC084A111D0"));

        var saveAs = new MenuItem
        {
            Header = UiText.Get("Text_57950AFF46BC"),
            InputGestureText = "Ctrl+Shift+S",
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
            Text = UiText.Get("Text_5FB926229090"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0),
        });
        content.Children.Add(BuildIcon("Icon.ChevronDown", FallbackChevronDown, 12));

        var button = new Button
        {
            Content = content,
            ToolTip = UiText.Get("Text_5825293860A7"),
            MinWidth = 78,
            Margin = new Thickness(2, 0, 2, 0),
            ContextMenu = menu,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.GhostCompact");
        AutomationName(button, UiText.Get("Text_EA33C8FF45A0"));
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
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AddToolButton(stack, EditorTool.Select, UiText.Get("Text_8D1A750C9351"), "V", UiText.Get("Text_8A3ADCEB89EF"), "Icon.Select", FallbackSelect);
        AddToolButton(stack, EditorTool.Rectangle, UiText.Get("Text_DC0760235344"), "R", UiText.Get("Text_B659038CC17D"), "Icon.Rectangle", FallbackRectangle);
        AddToolButton(stack, EditorTool.Arrow, UiText.Get("Text_2785CE58DF74"), "A", UiText.Get("Text_D53ABD064C5E"), "Icon.Arrow", FallbackArrow);
        AddToolButton(stack, EditorTool.Pen, UiText.Get("Text_37DB9D0B1C8E"), "P", UiText.Get("Text_684786FB2840"), "Icon.Pen", FallbackPen);
        AddToolButton(stack, EditorTool.Text, UiText.Get("Text_258AD4B095A1"), "T", UiText.Get("Text_B3711BAFCD9C"), "Icon.Text", FallbackText);
        AddToolButton(stack, EditorTool.Image, UiText.Get("Text_302BAE127938"), "I", UiText.Get("Text_93D363BDFEA5"), "Icon.Image", FallbackImage);

        var panel = new Border { Child = stack };
        panel.SetResourceReference(FrameworkElement.StyleProperty, "Rail.Panel");
        return panel;
    }

    private Border BuildViewport()
    {
        _viewport.Background = Brush("Surface.Sunken", Colors.Black);
        _viewport.Children.Add(_surface);
        _overlayCanvas.IsHitTestVisible = true;
        _overlayCanvas.Background = null;
        _viewport.Children.Add(_overlayCanvas);

        return new Border
        {
            Background = Brush("Surface.Sunken", Colors.Black),
            BorderBrush = Brush("Border.Subtle", Colors.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(12),
            ClipToBounds = true,
            Child = _viewport,
        };
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
        _inspectorInstruction = new TextBlock
        {
            Text = string.Empty,
            Foreground = Brush("Text.Secondary", Colors.LightGray),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        stack.Children.Add(_inspectorTitle);
        stack.Children.Add(_inspectorInstruction);

        _colorSection = BuildColorSection();
        stack.Children.Add(_colorSection);

        _thicknessSection = BuildThicknessSection();
        stack.Children.Add(_thicknessSection);
        _shapeStyleSection = BuildShapeStyleSection();
        stack.Children.Add(_shapeStyleSection);

        _deleteButton = TextButton(UiText.Get("Text_0DED92FCE6B0"), UiText.Get("Text_7DFFA4D81765"), "Button.Danger", DeleteSelected);
        _deleteButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _deleteButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        _deleteButton.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(_deleteButton);

        return new Border
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
        };
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
                ToolTip = UiText.Format("Text_151C77170A94", swatchColor.ToHex()),
                BorderThickness = new Thickness(1),
                BorderBrush = Brush("Border.Subtle", Colors.Gray),
                Background = swatchColor.ToBrush(),
            };
            AutomationName(swatchButton, UiText.Format("Text_151C77170A94", swatchColor.ToHex()));
            swatchButton.Click += (_, _) =>
            {
                _controller.ApplyStrokeColor(swatchColor);
                RememberPreferences();
                SetStatus(UiText.Format("Text_67CAE5B118A3", swatchColor.ToHex()));
            };
            _swatchPanel.Children.Add(swatchButton);
        }

        panel.Children.Add(_swatchPanel);
        return panel;
    }

    private FrameworkElement BuildThicknessSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionLabel(UiText.Get("Text_70EC2D232B4F")));

        _thicknessSlider = new Slider
        {
            Minimum = 1,
            Maximum = 24,
            Value = _controller.StrokeThickness,
            Margin = new Thickness(0, 4, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiText.Get("Text_70EC2D232B4F"),
            SmallChange = 1,
            LargeChange = 2,
        };
        AutomationName(_thicknessSlider, UiText.Get("Text_70EC2D232B4F"));
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
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);
        AutomationName(_statusText, UiText.Get("Text_4D6051F71010"));
        Grid.SetColumn(_statusText, 0);
        grid.Children.Add(_statusText);

        var dimensions = new TextBlock
        {
            Text = UiText.Format("Text_009548FC2109", _canvasWidth, _canvasHeight),
            Foreground = Brush("Text.Muted", Colors.Gray),
            FontFamily = FontFamilyResource("Font.Mono", "Consolas"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        Grid.SetColumn(dimensions, 1);
        grid.Children.Add(dimensions);

        return new Border
        {
            Background = Brush("Surface.Raised", Color.FromRgb(0x10, 0x17, 0x22)),
            BorderBrush = Brush("Border.Subtle", Colors.Gray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 6, 12, 7),
            Child = grid,
            SnapsToDevicePixels = true,
        };
    }

    // ---- Tool rail / command helpers -----------------------------------------------

    private void AddToolButton(
        Panel parent,
        EditorTool tool,
        string label,
        string shortcut,
        string tooltip,
        string iconResourceKey,
        Func<Geometry> fallbackGeometry)
    {
        var button = new ToggleButton
        {
            Content = BuildIcon(iconResourceKey, fallbackGeometry, 20),
            ToolTip = tooltip,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Rail.ToolButton");
        AutomationName(button, UiText.Format("Text_1BA9F7001237", label, shortcut));
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
            Content = BuildIcon(iconResourceKey, fallback, 16),
            ToolTip = tooltip,
            Width = 32,
            Height = 32,
            Padding = new Thickness(6),
            Margin = new Thickness(2, 0, 2, 0),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.GhostCompact");
        AutomationName(button, automationName);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button IconTextButton(string label, string tooltip, string iconResourceKey, Func<Geometry> fallback, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(BuildIcon(iconResourceKey, fallback, 16));
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
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
        button.Click += (_, _) => onClick();
        return button;
    }

    private Border Separator() => new()
    {
        Width = 1,
        Margin = new Thickness(6, 4, 6, 4),
        Background = Brush("Border.Subtle", Colors.Gray),
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Application.Current?.TryFindResource("Text.Muted") as Brush ?? Brushes.Gray,
        Margin = new Thickness(0, 0, 0, 4),
    };

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

    private static Geometry FallbackUndo() => Geometry.Parse("M8,6 L4,10 L8,14 M4,10 H13 A4,4 0 0 1 13,18");

    private static Geometry FallbackRedo() => Geometry.Parse("M12.75,5.25 L16.5,9 L12.75,12.75 M16.25,9 H8.75 A5,5 0 0 0 5.25,17");

    private static Geometry FallbackCopy() => Geometry.Parse("M7,3.5 H15.25 A1.25,1.25 0 0 1 16.5,4.75 V13 H7 Z M5,6.5 H4.75 A1.25,1.25 0 0 0 3.5,7.75 V15.25 A1.25,1.25 0 0 0 4.75,16.5 H12.25 A1.25,1.25 0 0 0 13.5,15.25 V15");

    private static Geometry FallbackCheck() => Geometry.Parse("M3.75,10.25 L8.15,14.65 L16.25,5.35");

    private static Geometry FallbackSave() => Geometry.Parse("M4,3.5 H13.25 L16.5,6.75 V16.5 H3.5 V4 Z M6.5,3.5 V8 H13.5 V3.75 M6.5,16.5 V11 H13.5 V16.5");

    private static Geometry FallbackSaveAs() => Geometry.Parse("M3.5,3.75 H11.75 L14.75,6.75 V10 M6.25,3.75 V7.75 H11.75 V4 M6,16.25 H3.5 V4.25 M8.5,15.75 L9.15,12.95 L14.7,7.4 A1.35,1.35 0 0 1 16.6,9.3 L11.05,14.85 Z");

    private static Geometry FallbackChevronDown() => Geometry.Parse("M5.25,7.5 L10,12.25 L14.75,7.5");

    // ---- Selection & tool state ----------------------------------------------------

    private void SelectTool(EditorTool tool)
    {
        if (_activeTextBox is not null)
        {
            CommitActiveText();
        }

        _controller.Tool = tool;
        if (tool != EditorTool.Select) _preferredTool = tool;
        RememberPreferences();
        SyncToolButtons();
        Cursor = tool == EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
        UpdateInspector();
        SetStatus(ToolStatus(tool));
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
            MosaicBlockSize = previous.MosaicBlockSize, HighlighterAlpha = previous.HighlighterAlpha,
            RecentColors = [.. previous.RecentColors],
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

        if (_controller.Selected is { } selected)
        {
            SetStatus(UiText.Format("Text_0ECA3A448221", selected.DisplayName));
        }
    }

    private void OnHistoryChanged()
    {
        _imageStore.PruneToReachable(_controller.Document, _controller.Undo);
        RefreshHistoryButtons();
        UpdateInspector();
    }

    private void RefreshHistoryButtons()
    {
        if (_undoButton is null)
        {
            return;
        }

        _undoButton.IsEnabled = _controller.CanUndo;
        _redoButton.IsEnabled = _controller.CanRedo;
    }

    /// <summary>
    /// Reflects the current tool and selection into the inspector: object/tool name and a
    /// plain-language instruction, colour and thickness only when they apply, and Delete only
    /// when there is a selection.
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

        if (selected is not null)
        {
            _inspectorTitle.Text = selected.DisplayName;
            _inspectorInstruction.Text = selected.SupportsResize
                ? UiText.Get("Text_4F445554C9D0")
                : UiText.Get("Text_C2F4730D02E8");
        }
        else
        {
            _inspectorTitle.Text = ToolName(_controller.Tool);
            _inspectorInstruction.Text = ToolInstruction(_controller.Tool);
        }

        bool colorApplies = ColorApplies(selected, _controller.Tool);
        bool thicknessApplies = ThicknessApplies(selected, _controller.Tool);
        bool shapeApplies = selected is ShapeAnnotation || (selected is null && _controller.Tool == EditorTool.Rectangle);

        _colorSection.Visibility = colorApplies ? Visibility.Visible : Visibility.Collapsed;
        _thicknessSection.Visibility = thicknessApplies ? Visibility.Visible : Visibility.Collapsed;
        _shapeStyleSection.Visibility = shapeApplies ? Visibility.Visible : Visibility.Collapsed;

        _syncingInspector = true;
        try
        {
            if (thicknessApplies)
            {
                AnnotationStrokeStyle style = (selected as ShapeAnnotation)?.StrokeStyle ?? _controller.StrokeStyle;
                _thicknessSlider.Minimum = (selected is ShapeAnnotation || (selected is null && _controller.Tool == EditorTool.Rectangle))
                    && style == AnnotationStrokeStyle.ThickDashed ? 6 : 1;
                SyncThicknessFromSelection(selected);
            }

            if (shapeApplies)
            {
                ShapeAnnotation? shape = selected as ShapeAnnotation;
                _strokeStyleComboBox.SelectedIndex = (int)(shape?.StrokeStyle ?? _controller.StrokeStyle);
                _fillTransparencySlider.Value = shape?.FillTransparency ?? _controller.FillTransparency;
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

        return tool is EditorTool.Rectangle or EditorTool.Arrow or EditorTool.Pen or EditorTool.Text;
    }

    private static bool ThicknessApplies(AnnotationItem? selected, EditorTool tool)
    {
        if (selected is not null)
        {
            return selected is ShapeAnnotation or PolylineAnnotation or PenAnnotation;
        }

        return tool is EditorTool.Rectangle or EditorTool.Arrow or EditorTool.Pen;
    }

    private static string ToolName(EditorTool tool) => tool switch
    {
        EditorTool.Select => UiText.Get("Text_33406FC582BE"),
        EditorTool.Rectangle => UiText.Get("Text_4A48782E904C"),
        EditorTool.Arrow => UiText.Get("Text_D5A82B3B1F83"),
        EditorTool.Pen => UiText.Get("Text_CEFF481585C2"),
        EditorTool.Text => UiText.Get("Text_DAB3F35F5BD0"),
        EditorTool.Image => UiText.Get("Text_A5E52C2DDFC0"),
        _ => UiText.Get("Text_36C416EAD2CE"),
    };

    private static string ToolInstruction(EditorTool tool) => tool switch
    {
        EditorTool.Select => UiText.Get("Text_563638D54DBC"),
        EditorTool.Rectangle => UiText.Get("Text_205E8D74A1C2"),
        EditorTool.Arrow => UiText.Get("Text_2AE708D62044"),
        EditorTool.Pen => UiText.Get("Text_8AA2E937C447"),
        EditorTool.Text => UiText.Get("Text_E8DF95164C87"),
        EditorTool.Image => UiText.Get("Text_994DA3588A6B"),
        _ => string.Empty,
    };

    private static string ToolStatus(EditorTool tool) => tool switch
    {
        EditorTool.Select => UiText.Get("Text_FEFD9D67E873"),
        EditorTool.Rectangle => UiText.Get("Text_93103CC109A3"),
        EditorTool.Arrow => UiText.Get("Text_190A3BE20786"),
        EditorTool.Pen => UiText.Get("Text_9CD399964425"),
        EditorTool.Text => UiText.Get("Text_632459CEB61F"),
        EditorTool.Image => UiText.Get("Text_92675EB6DE53"),
        _ => string.Empty,
    };

    private void SetStatus(string message)
    {
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
            PrivacyRedactionResult result = await _privacyRedactionService.FindAsync(
                _selectedBitmap,
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

        AnnotationDocument document = _controller.Document;
        document.NormalizeZIndices();

        IEnumerable<string> usedAssets = document.Items
            .OfType<ImageAnnotation>()
            .Select(i => i.AssetFileName)
            .ToList();

        var result = new AnnotationEditingResult(
            _frame,
            _cropRegion,
            _selectedBitmap,
            document,
            action,
            _imageStore.DecodedFor(usedAssets),
            _imageStore.SourcesFor(usedAssets),
            reduceExport);

        // Keep the editor alive while background PNG encoding or clipboard contention
        // resolves. This guarantees Ctrl+C means copy-then-close, never close-then-copy.
        _commitInProgress = true;
        IsHitTestVisible = false;
        SetStatus(action == EditorCommitAction.CopyToClipboard ? UiText.Get("Text_78666F37A521") : UiText.Get("Text_88DAAEEDFE4C"));
        bool shouldClose;
        try
        {
            shouldClose = CommitRequested is null || await CommitRequested(result);
        }
        catch (Exception)
        {
            shouldClose = false;
        }

        if (shouldClose)
        {
            _completed = true;
            EditingCompleted?.Invoke(this, result);
        }
        else
        {
            _commitInProgress = false;
            IsHitTestVisible = true;
            SetStatus(action == EditorCommitAction.CopyToClipboard
                ? UiText.Get("Text_3D1E714B56CA")
                : UiText.Get("Text_06F58809C083"));
        }
    }

    private void Cancel()
    {
        if (_completed || _commitInProgress)
        {
            return;
        }

        _completed = true;
        EditingCancelled?.Invoke(this, EventArgs.Empty);
    }

    // ---- Resource helpers ----------------------------------------------------------

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily FontFamilyResource(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily(fallback);

    private static void AutomationName(DependencyObject element, string name) =>
        AutomationProperties.SetName(element, name);
}
