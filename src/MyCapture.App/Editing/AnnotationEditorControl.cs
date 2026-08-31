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
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
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
    private const double ToolRailWidth = 52;
    private const double InspectorWidth = 232;

    // Below this width the inspector collapses before it can squeeze the image workspace.
    // Normal editor startup now targets a comfortable width above this threshold.
    private const double InspectorCollapseWidth = 860;

    private readonly FrozenFrame _frame;
    private readonly RectD _cropRegion;
    private readonly BitmapSource _selectedBitmap;
    private readonly AnnotationImageStore _imageStore = new();
    private readonly AnnotationEditorController _controller;
    private readonly AnnotationEditorSurface _surface;
    private readonly Grid _viewport = new();
    private readonly Canvas _overlayCanvas = new();
    private readonly Dictionary<EditorTool, ToggleButton> _toolButtons = new();

    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Button _deleteButton = null!;
    private Slider _thicknessSlider = null!;
    private ColumnDefinition _inspectorColumn = null!;
    private Border _inspectorPanel = null!;
    private WrapPanel _swatchPanel = null!;
    private FrameworkElement _colorSection = null!;
    private FrameworkElement _thicknessSection = null!;
    private TextBlock _inspectorTitle = null!;
    private TextBlock _inspectorInstruction = null!;
    private TextBlock _statusText = null!;
    private bool _inspectorCollapsed;

    private TextBox? _activeTextBox;
    private TextAnnotation? _editingText;
    private bool _completed;

    internal AnnotationEditorControl(FrozenFrame frame, RectD bitmapRegion, BitmapSource selectedBitmap)
        : this(frame, bitmapRegion, selectedBitmap, initialDocument: null, initialAssets: null)
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
        IReadOnlyDictionary<string, BitmapSource>? initialAssets)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _cropRegion = bitmapRegion.Normalized();
        _selectedBitmap = selectedBitmap ?? throw new ArgumentNullException(nameof(selectedBitmap));

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
        var renderer = new AnnotationRenderer(_imageStore);

        var visualRegion = new RectD(0, 0, _canvasWidth, _canvasHeight);
        var visualFrame = new FrozenFrame(
            selectedBitmap,
            visualRegion,
            frame.Monitor,
            frame.ElapsedMilliseconds);
        _surface = new AnnotationEditorSurface(visualFrame, visualRegion, _controller, renderer);

        Background = Brush("Surface.Base", Color.FromRgb(0x1B, 0x17, 0x12));
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

        SelectTool(EditorTool.Pen);
        RefreshHistoryButtons();
        UpdateInspector();
    }

    /// <summary>Raised when the user commits the edit (Done / Ctrl+Enter).</summary>
    internal event EventHandler<AnnotationEditingResult>? EditingCompleted;

    /// <summary>Raised when the user cancels the edit (Esc / cancel button).</summary>
    internal event EventHandler? EditingCancelled;

    /// <summary>
    /// Invoked synchronously when the user asks to commit, before the editor closes. The
    /// handler flattens, persists, and performs any clipboard/export the action requires,
    /// and returns whether the editor should close. Returning <see langword="false"/> (a
    /// cancelled or failed Save As) leaves the editor open.
    /// </summary>
    internal Func<AnnotationEditingResult, bool>? CommitRequested { get; set; }

    internal BitmapSource DisplayedBitmap => _surface.Frame.Bitmap;

    internal RectD DisplayedRegion => _surface.CropRegion;

    /// <summary>
    /// The element that owns both pointer event handlers and mouse capture. Keeping these
    /// responsibilities on one element prevents WPF from rerouting drag move/up events to an
    /// ancestor and leaving a zero-size draft in the document.
    /// </summary>
    internal UIElement PointerInputElement => _viewport;

    internal bool IsPointerCaptured => _viewport.IsMouseCaptured;

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
            case Key.Escape:
                Cancel();
                return true;
            case Key.Z when ctrl:
                if (_controller.PerformUndo())
                {
                    SetStatus("실행을 취소했습니다");
                }

                return true;
            case Key.Y when ctrl:
                if (_controller.PerformRedo())
                {
                    SetStatus("다시 실행했습니다");
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
                SetStatus("연필 획을 추가했습니다 · 계속 그릴 수 있습니다 · Ctrl+Z로 취소");
            }
            else if (_controller.Selected is { } selected)
            {
                SetStatus($"{selected.DisplayName}을(를) 추가했습니다 · Ctrl+Z로 취소");
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
            Background = new SolidColorBrush(Color.FromArgb(230, 42, 36, 28)),
            BorderBrush = Brush("Accent.Default", Color.FromRgb(0xFF, 0xD4, 0x00)),
            BorderThickness = new Thickness(1),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(2),
        };
        AutomationName(textBox, "주석 텍스트 입력");

        Canvas.SetLeft(textBox, box.Left);
        Canvas.SetTop(textBox, box.Top);
        _overlayCanvas.Children.Add(textBox);
        _activeTextBox = textBox;
        SetStatus("텍스트를 입력한 뒤 Esc 또는 다른 곳을 클릭해 확정하세요");

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
        SetStatus(hadText ? "텍스트를 추가했습니다 · Ctrl+Z로 취소" : "빈 텍스트를 취소했습니다");
        UpdateInspector();
    }

    // ---- Image insertion -----------------------------------------------------------

    private void InsertImage(PointD image)
    {
        var dialog = new OpenFileDialog
        {
            Title = "이미지 삽입",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|모든 파일|*.*",
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
                "선택한 파일을 이미지로 읽을 수 없습니다.",
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
        SetStatus("이미지를 추가했습니다 · Ctrl+Z로 취소");
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
            Text = "주석 편집기",
            Foreground = Brush("Text.Primary", Colors.White),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0),
        };
        left.Children.Add(document);
        left.Children.Add(Separator());

        _undoButton = IconButton("실행 취소", "실행 취소 (Ctrl+Z)", "Icon.Undo", FallbackUndo, () =>
        {
            if (_controller.PerformUndo())
            {
                SetStatus("실행을 취소했습니다");
            }
        });
        _redoButton = IconButton("다시 실행", "다시 실행 (Ctrl+Y)", "Icon.Redo", FallbackRedo, () =>
        {
            if (_controller.PerformRedo())
            {
                SetStatus("다시 실행했습니다");
            }
        });
        left.Children.Add(_undoButton);
        left.Children.Add(_redoButton);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        right.Children.Add(BuildSaveOverflowMenu());
        right.Children.Add(Separator());

        Button cancel = TextButton("취소", "편집 취소 (Esc)", "Button.GhostCompact", Cancel);
        Button copy = IconTextButton(
            "복사", "편집한 이미지를 클립보드에 복사 (Ctrl+C)", "Icon.Copy", FallbackCopy,
            () => Commit(EditorCommitAction.CopyToClipboard));
        copy.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        copy.MinWidth = 76;

        Button done = IconTextButton(
            "완료", "편집 완료 (Ctrl+Enter)", "Icon.Check", FallbackCheck,
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
            Background = Brush("Surface.Raised", Color.FromRgb(0x22, 0x1D, 0x17)),
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
            Header = "빠른 저장",
            InputGestureText = "Ctrl+S",
            Icon = BuildIcon("Icon.Save", FallbackSave, 16),
        };
        quickSave.Click += (_, _) => Commit(EditorCommitAction.QuickSave);
        AutomationName(quickSave, "빠른 저장");

        var saveAs = new MenuItem
        {
            Header = "다른 이름으로 저장",
            InputGestureText = "Ctrl+Shift+S",
            Icon = BuildIcon("Icon.SaveAs", FallbackSaveAs, 16),
        };
        saveAs.Click += (_, _) => Commit(EditorCommitAction.SaveAs);
        AutomationName(saveAs, "다른 이름으로 저장");

        menu.Items.Add(quickSave);
        menu.Items.Add(saveAs);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(BuildIcon("Icon.Save", FallbackSave, 16));
        content.Children.Add(new TextBlock
        {
            Text = "저장",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0),
        });
        content.Children.Add(BuildIcon("Icon.ChevronDown", FallbackChevronDown, 12));

        var button = new Button
        {
            Content = content,
            ToolTip = "저장 및 내보내기 (Ctrl+S · Ctrl+Shift+S)",
            MinWidth = 78,
            Margin = new Thickness(2, 0, 2, 0),
            ContextMenu = menu,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.GhostCompact");
        AutomationName(button, "저장 메뉴");
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
            Background = Brush("Surface.Base", Color.FromRgb(0x1B, 0x17, 0x12)),
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
        AddToolButton(stack, EditorTool.Select, "선택", "V", "선택 도구 (V) — 주석을 선택·이동·크기 조정", "Icon.Select", FallbackSelect);
        AddToolButton(stack, EditorTool.Rectangle, "사각형", "R", "사각형 (R) — 이미지 위를 드래그", "Icon.Rectangle", FallbackRectangle);
        AddToolButton(stack, EditorTool.Arrow, "화살표", "A", "화살표 (A) — 시작점에서 끝점으로 드래그", "Icon.Arrow", FallbackArrow);
        AddToolButton(stack, EditorTool.Pen, "연필", "P", "연필 (P) — 자유롭게 그리기", "Icon.Pen", FallbackPen);
        AddToolButton(stack, EditorTool.Text, "텍스트", "T", "텍스트 (T) — 클릭해 입력", "Icon.Text", FallbackText);
        AddToolButton(stack, EditorTool.Image, "이미지", "I", "이미지 삽입 (I) — 파일을 선택", "Icon.Image", FallbackImage);

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
            Text = "선택 도구",
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

        _deleteButton = TextButton("주석 삭제", "선택한 주석 삭제 (Delete)", "Button.Danger", DeleteSelected);
        _deleteButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _deleteButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        _deleteButton.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(_deleteButton);

        return new Border
        {
            Background = Brush("Surface.Raised", Color.FromRgb(0x22, 0x1D, 0x17)),
            BorderBrush = Brush("Border.Subtle", Colors.Gray),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(18),
            Child = stack,
            SnapsToDevicePixels = true,
        };
    }

    private FrameworkElement BuildColorSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionLabel("색상"));

        _swatchPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Width = 192,
            HorizontalAlignment = HorizontalAlignment.Left,
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
                ToolTip = $"색상 {swatchColor.ToHex()}",
                BorderThickness = new Thickness(1),
                BorderBrush = Brush("Border.Subtle", Colors.Gray),
                Background = swatchColor.ToBrush(),
            };
            AutomationName(swatchButton, $"색상 {swatchColor.ToHex()}");
            swatchButton.Click += (_, _) =>
            {
                _controller.ApplyStrokeColor(swatchColor);
                SetStatus($"색상을 {swatchColor.ToHex()}(으)로 바꿨습니다");
            };
            _swatchPanel.Children.Add(swatchButton);
        }

        panel.Children.Add(_swatchPanel);
        return panel;
    }

    private FrameworkElement BuildThicknessSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionLabel("선 두께"));

        _thicknessSlider = new Slider
        {
            Minimum = 1,
            Maximum = 24,
            Value = _controller.StrokeThickness,
            Margin = new Thickness(0, 4, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "선 두께",
            SmallChange = 1,
            LargeChange = 2,
        };
        AutomationName(_thicknessSlider, "선 두께");
        _thicknessSlider.ValueChanged += (_, args) =>
        {
            _controller.ApplyStrokeThickness(args.NewValue);
            if (_controller.Selected is not null)
            {
                SetStatus($"선 두께를 {args.NewValue:0}px로 바꿨습니다");
            }
        };
        panel.Children.Add(_thicknessSlider);
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
        AutomationName(_statusText, "편집 상태");
        Grid.SetColumn(_statusText, 0);
        grid.Children.Add(_statusText);

        var dimensions = new TextBlock
        {
            Text = $"{_canvasWidth} × {_canvasHeight} px  ·  Ctrl+C 복사  ·  Ctrl+S 저장  ·  Esc 취소",
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
            Background = Brush("Surface.Raised", Color.FromRgb(0x22, 0x1D, 0x17)),
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
        AutomationName(button, $"{label} 도구 ({shortcut})");
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
        SyncToolButtons();
        Cursor = tool == EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
        UpdateInspector();
        SetStatus(ToolStatus(tool));
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
            SetStatus("주석을 삭제했습니다 · Ctrl+Z로 취소");
        }
    }

    private void OnSelectionChanged()
    {
        UpdateInspector();
        RefreshHistoryButtons();

        if (_controller.Selected is { } selected)
        {
            SetStatus($"{selected.DisplayName}을(를) 선택했습니다");
        }
    }

    private void OnHistoryChanged()
    {
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
                ? "가장자리 핸들로 크기를 조정하거나 드래그해 이동하세요."
                : "드래그해 이동하세요.";
        }
        else
        {
            _inspectorTitle.Text = ToolName(_controller.Tool);
            _inspectorInstruction.Text = ToolInstruction(_controller.Tool);
        }

        bool colorApplies = ColorApplies(selected, _controller.Tool);
        bool thicknessApplies = ThicknessApplies(selected, _controller.Tool);

        _colorSection.Visibility = colorApplies ? Visibility.Visible : Visibility.Collapsed;
        _thicknessSection.Visibility = thicknessApplies ? Visibility.Visible : Visibility.Collapsed;

        if (thicknessApplies)
        {
            SyncThicknessFromSelection(selected);
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
        EditorTool.Select => "선택 도구",
        EditorTool.Rectangle => "사각형 도구",
        EditorTool.Arrow => "화살표 도구",
        EditorTool.Pen => "연필 도구",
        EditorTool.Text => "텍스트 도구",
        EditorTool.Image => "이미지 도구",
        _ => "도구",
    };

    private static string ToolInstruction(EditorTool tool) => tool switch
    {
        EditorTool.Select => "주석을 클릭해 선택한 뒤 이동하거나 크기를 조정하세요.",
        EditorTool.Rectangle => "이미지 위를 드래그해 사각형을 그리세요.",
        EditorTool.Arrow => "시작점에서 끝점까지 드래그해 화살표를 그리세요.",
        EditorTool.Pen => "이미지 위에서 자유롭게 그리세요.",
        EditorTool.Text => "이미지를 클릭한 뒤 텍스트를 입력하세요.",
        EditorTool.Image => "이미지를 클릭하면 삽입할 파일을 고를 수 있습니다.",
        _ => string.Empty,
    };

    private static string ToolStatus(EditorTool tool) => tool switch
    {
        EditorTool.Select => "선택 도구 · 주석을 클릭해 선택하세요",
        EditorTool.Rectangle => "사각형 도구 · 이미지 위를 드래그하세요",
        EditorTool.Arrow => "화살표 도구 · 시작점에서 끝점으로 드래그하세요",
        EditorTool.Pen => "연필 도구 · 자유롭게 그리세요",
        EditorTool.Text => "텍스트 도구 · 이미지를 클릭해 입력하세요",
        EditorTool.Image => "이미지 도구 · 이미지를 클릭해 파일을 선택하세요",
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

        // Collapse the inspector below a practical width so the tool rail and viewport keep
        // their space; the primary tools are never hidden.
        bool shouldCollapse = ActualWidth > 0 && ActualWidth < InspectorCollapseWidth;
        if (shouldCollapse == _inspectorCollapsed)
        {
            return;
        }

        _inspectorCollapsed = shouldCollapse;
        if (shouldCollapse)
        {
            _inspectorColumn.Width = new GridLength(0);
            _inspectorPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _inspectorColumn.Width = new GridLength(InspectorWidth);
            _inspectorPanel.Visibility = Visibility.Visible;
        }
    }

    // ---- Commit / cancel -----------------------------------------------------------

    private void Commit(EditorCommitAction action)
    {
        if (_completed)
        {
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
            _imageStore.SourcesFor(usedAssets));

        // The consumer performs flatten/persist/clipboard/export and reports whether the
        // editor should close. A cancelled or failed Save As keeps the editor open so the
        // user does not silently lose the choice; every other action closes on success.
        bool shouldClose = CommitRequested?.Invoke(result) ?? true;
        if (shouldClose)
        {
            _completed = true;
            EditingCompleted?.Invoke(this, result);
        }
        else
        {
            SetStatus("저장이 완료되지 않았습니다");
        }
    }

    private void Cancel()
    {
        if (_completed)
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
