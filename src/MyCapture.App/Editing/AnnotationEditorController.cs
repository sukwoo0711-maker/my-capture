using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;

namespace MyCapture.App.Editing;

/// <summary>
/// The interaction state machine behind the annotation editor.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of WPF input types. It takes pointer positions in the selected
/// image's pixel space and drives the domain <see cref="AnnotationDocument"/> and
/// <see cref="UndoStack"/>, so the whole editing model — create, select, move, resize,
/// delete, restyle, undo — is unit-testable without a UI thread or a message pump.
/// </para>
/// <para>
/// Every mutation goes through the undo stack. A continuous gesture (drag to create,
/// drag to move) records exactly one command on release, using the stack's own merge
/// rules to collapse the intermediate mouse moves.
/// </para>
/// </remarks>
internal sealed class AnnotationEditorController
{
    private readonly AnnotationDocument _document;
    private readonly UndoStack _undo;

    private EditorTool _tool = EditorTool.Pen;
    private AnnotationItem? _selected;

    // Live-gesture state.
    private Gesture _gesture = Gesture.None;
    private PointD _gestureStart;
    private PointD _gestureLast;
    private RectD _transformBefore;
    private ResizeHandle _resizeHandle = ResizeHandle.None;
    private AnnotationItem? _draftItem;
    private List<PointD>? _draftPoints;

    internal AnnotationEditorController(AnnotationDocument document, UndoStack undo)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
    }

    /// <summary>Raised whenever the visible state changed and the surface must repaint.</summary>
    internal event EventHandler? VisualInvalidated;

    /// <summary>Raised when the selected item changes, so the style bar can refresh.</summary>
    internal event EventHandler? SelectionChanged;

    internal AnnotationDocument Document => _document;

    internal UndoStack Undo => _undo;

    internal EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
            {
                return;
            }

            _tool = value;
            if (value != EditorTool.Select)
            {
                SetSelected(null);
            }

            RaiseVisual();
        }
    }

    internal AnnotationItem? Selected => _selected;

    /// <summary>Current stroke/foreground colour applied to new items.</summary>
    internal ColorRgba StrokeColor { get; set; } = ColorRgba.FromRgb(0xEF, 0x44, 0x44);

    /// <summary>Current stroke thickness applied to new shape/line/pen items.</summary>
    internal double StrokeThickness { get; set; } = 3;

    internal bool CanUndo => _undo.CanUndo;

    internal bool CanRedo => _undo.CanRedo;

    internal bool IsCreatingText { get; private set; }

    /// <summary>
    /// Pixels within which a click counts as hitting an annotation. Scaled by the caller
    /// to stay constant on screen regardless of zoom.
    /// </summary>
    internal double HitTolerance { get; set; } = 6;

    internal void SetSelected(AnnotationItem? item)
    {
        if (ReferenceEquals(_selected, item))
        {
            return;
        }

        _selected = item;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseVisual();
    }

    internal bool PerformUndo()
    {
        if (!_undo.Undo())
        {
            return false;
        }

        EnsureSelectionValid();
        RaiseVisual();
        return true;
    }

    internal bool PerformRedo()
    {
        if (!_undo.Redo())
        {
            return false;
        }

        EnsureSelectionValid();
        RaiseVisual();
        return true;
    }

    internal void DeleteSelected()
    {
        if (_selected is null)
        {
            return;
        }

        AnnotationItem victim = _selected;
        SetSelected(null);
        _undo.Execute(new RemoveAnnotationCommand(_document, victim));
        RaiseVisual();
    }

    // ---- Pointer gestures (image-pixel coordinates) ---------------------------------

    internal void PointerDown(PointD point)
    {
        switch (_tool)
        {
            case EditorTool.Select:
                BeginSelectGesture(point);
                break;
            case EditorTool.Rectangle:
            case EditorTool.Arrow:
            case EditorTool.Pen:
                BeginDraftGesture(point);
                break;
            case EditorTool.Text:
            case EditorTool.Image:
                // Placed via a single click, handled by the view (TextBox / OpenFileDialog).
                break;
        }
    }

    internal void PointerMove(PointD point)
    {
        switch (_gesture)
        {
            case Gesture.Create:
                UpdateDraft(point);
                break;
            case Gesture.Move:
                if (_selected is not null)
                {
                    double dx = point.X - _gestureLast.X;
                    double dy = point.Y - _gestureLast.Y;
                    _selected.Translate(dx, dy);
                    _gestureLast = point;
                    RaiseVisual();
                }

                break;
            case Gesture.Resize:
                if (_selected is not null)
                {
                    RectD resized = ApplyResize(_transformBefore, _resizeHandle, point);
                    _selected.SetBounds(resized);
                    RaiseVisual();
                }

                break;
            case Gesture.PenDraw:
                _draftPoints?.Add(point);
                if (_draftItem is PenAnnotation pen)
                {
                    pen.Points = [.. _draftPoints!];
                }

                RaiseVisual();
                break;
        }
    }

    internal void PointerUp(PointD point)
    {
        switch (_gesture)
        {
            case Gesture.Create:
                CommitDraftShape(point);
                break;
            case Gesture.PenDraw:
                CommitPen();
                break;
            case Gesture.Move:
            case Gesture.Resize:
                if (_selected is not null)
                {
                    RectD after = _selected.Bounds;
                    if (after != _transformBefore)
                    {
                        // The item already followed the pointer; record the net transform so
                        // one drag is one undo step.
                        _undo.Push(new TransformAnnotationCommand(_selected, _transformBefore, after));
                    }
                }

                break;
        }

        _gesture = Gesture.None;
        _resizeHandle = ResizeHandle.None;
        _draftItem = null;
        _draftPoints = null;
        RaiseVisual();
    }

    // ---- Single-click placement (text / image) --------------------------------------

    /// <summary>
    /// Creates and selects a text annotation at <paramref name="topLeft"/>, entering the
    /// live-edit state. The caller opens a real TextBox and calls
    /// <see cref="CommitTextEdit"/> when the user commits.
    /// </summary>
    internal TextAnnotation BeginTextAnnotation(PointD topLeft, double defaultBoxWidth, double defaultBoxHeight)
    {
        var text = new TextAnnotation
        {
            Rect = new RectD(topLeft.X, topLeft.Y, defaultBoxWidth, defaultBoxHeight),
            Foreground = StrokeColor,
            FontSize = 18,
            Text = string.Empty,
        };

        _undo.Execute(new AddAnnotationCommand(_document, text));
        IsCreatingText = true;
        Tool = EditorTool.Select;
        SetSelected(text);
        return text;
    }

    /// <summary>
    /// Finishes a live text edit. An empty box is removed (its add is undone) so a stray
    /// click that types nothing does not litter the document.
    /// </summary>
    internal void CommitTextEdit(TextAnnotation text, string finalText)
    {
        ArgumentNullException.ThrowIfNull(text);
        IsCreatingText = false;

        if (string.IsNullOrEmpty(finalText))
        {
            // Undo the add so the empty box leaves no trace and no undo entry.
            if (_undo.CanUndo && ReferenceEquals(_document.Items.LastOrDefault(), text))
            {
                _undo.Undo();
            }
            else
            {
                _document.Remove(text);
            }

            SetSelected(null);
        }
        else if (!string.Equals(text.Text, finalText, StringComparison.Ordinal))
        {
            string before = text.Text;
            _undo.Push(new PropertyChangeCommand<TextAnnotation, string>(
                text, "텍스트", static (t, v) => t.Text = v, before, finalText));
            text.Text = finalText;
        }

        RaiseVisual();
    }

    internal ImageAnnotation AddImageAnnotation(string assetFileName, int sourceWidth, int sourceHeight, RectD rect)
    {
        var image = new ImageAnnotation
        {
            AssetFileName = assetFileName,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            Rect = rect.Normalized(),
        };

        _undo.Execute(new AddAnnotationCommand(_document, image));
        Tool = EditorTool.Select;
        SetSelected(image);
        return image;
    }

    /// <summary>
    /// Adds opaque, editable privacy covers as one reversible user action. The rectangles are
    /// ordinary annotations: users can move, resize, delete or undo them before committing.
    /// </summary>
    internal int AddPrivacyRedactions(IEnumerable<RectD> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);

        List<RectD> bounded = regions
            .Select(region => region.Normalized().ClampTo(new RectD(0, 0, _document.CanvasWidth, _document.CanvasHeight)))
            .Where(static region => !region.IsEmpty)
            .Distinct()
            .ToList();
        if (bounded.Count == 0)
        {
            return 0;
        }

        RectangleAnnotation? last = null;
        using (_undo.BeginBatch("민감정보 빠른 가리기"))
        {
            foreach (RectD region in bounded)
            {
                last = new RectangleAnnotation
                {
                    Rect = region,
                    Stroke = ColorRgba.FromRgb(0x16, 0x16, 0x16),
                    Fill = ColorRgba.FromRgb(0x16, 0x16, 0x16),
                    StrokeThickness = 1,
                    CornerRadius = 2,
                };
                _undo.Execute(new AddAnnotationCommand(_document, last));
            }
        }

        Tool = EditorTool.Select;
        SetSelected(last);
        RaiseVisual();
        return bounded.Count;
    }

    // ---- Style changes on the current selection -------------------------------------

    internal void ApplyStrokeColor(ColorRgba color)
    {
        StrokeColor = color;
        if (_selected is null)
        {
            return;
        }

        switch (_selected)
        {
            case ShapeAnnotation shape:
                PushProperty(shape, "색상", static (s, v) => s.Stroke = v, shape.Stroke, color);
                break;
            case PolylineAnnotation line:
                PushProperty(line, "색상", static (s, v) => s.Stroke = v, line.Stroke, color);
                break;
            case PenAnnotation pen:
                PushProperty(pen, "색상", static (s, v) => s.Stroke = v, pen.Stroke, color);
                break;
            case TextAnnotation text:
                PushProperty(text, "색상", static (s, v) => s.Foreground = v, text.Foreground, color);
                break;
        }

        RaiseVisual();
    }

    internal void ApplyStrokeThickness(double thickness)
    {
        StrokeThickness = thickness;
        if (_selected is null)
        {
            return;
        }

        switch (_selected)
        {
            case ShapeAnnotation shape:
                PushProperty(shape, "두께", static (s, v) => s.StrokeThickness = v, shape.StrokeThickness, thickness);
                break;
            case PolylineAnnotation line:
                PushProperty(line, "두께", static (s, v) => s.StrokeThickness = v, line.StrokeThickness, thickness);
                break;
            case PenAnnotation pen:
                PushProperty(pen, "두께", static (s, v) => s.StrokeThickness = v, pen.StrokeThickness, thickness);
                break;
        }

        RaiseVisual();
    }

    /// <summary>
    /// Hit-tests handles first, then the item itself. Exposed so the view can pick a cursor.
    /// </summary>
    internal ResizeHandle HandleAt(PointD point, double handlePixels)
    {
        if (_selected is null || !_selected.SupportsResize)
        {
            return ResizeHandle.None;
        }

        return HitTestHandle(_selected.Bounds, point, handlePixels);
    }

    internal AnnotationItem? HitTest(PointD point) => _document.HitTest(point, HitTolerance);

    private void BeginSelectGesture(PointD point)
    {
        if (_selected is not null && _selected.SupportsResize)
        {
            ResizeHandle handle = HitTestHandle(_selected.Bounds, point, HitTolerance * 1.5);
            if (handle != ResizeHandle.None)
            {
                _gesture = Gesture.Resize;
                _resizeHandle = handle;
                _transformBefore = _selected.Bounds;
                return;
            }
        }

        AnnotationItem? hit = _document.HitTest(point, HitTolerance);
        SetSelected(hit);

        if (hit is not null)
        {
            _gesture = Gesture.Move;
            _gestureStart = point;
            _gestureLast = point;
            _transformBefore = hit.Bounds;
        }
    }

    private void BeginDraftGesture(PointD point)
    {
        _gestureStart = point;
        _gestureLast = point;

        if (_tool == EditorTool.Pen)
        {
            _draftPoints = [point];
            var pen = new PenAnnotation
            {
                Points = [point],
                Stroke = StrokeColor,
                StrokeThickness = StrokeThickness,
            };
            _draftItem = pen;
            _document.Add(pen);
            _gesture = Gesture.PenDraw;
            SetSelected(null);
            return;
        }

        _gesture = Gesture.Create;
        AnnotationItem draft = _tool switch
        {
            EditorTool.Rectangle => new RectangleAnnotation
            {
                Rect = new RectD(point.X, point.Y, 0, 0),
                Stroke = StrokeColor,
                StrokeThickness = StrokeThickness,
            },
            EditorTool.Arrow => PolylineAnnotation.CreateArrow(point, point, StrokeColor, StrokeThickness),
            _ => throw new InvalidOperationException($"Tool {_tool} has no draft shape."),
        };

        _draftItem = draft;
        _document.Add(draft);
        SetSelected(null);
    }

    private void UpdateDraft(PointD point)
    {
        switch (_draftItem)
        {
            case RectangleAnnotation rect:
                rect.Rect = RectD.FromCorners(_gestureStart, point);
                break;
            case PolylineAnnotation line:
                line.Points = [_gestureStart, point];
                break;
        }

        _gestureLast = point;
        RaiseVisual();
    }

    private void CommitDraftShape(PointD point)
    {
        UpdateDraft(point);

        AnnotationItem? draft = _draftItem;
        if (draft is null)
        {
            return;
        }

        if (IsDegenerate(draft))
        {
            // A click without a drag: remove the zero-size draft rather than leaving an
            // invisible, unselectable item behind.
            _document.Remove(draft);
            return;
        }

        // The item was added directly during the drag so it could be drawn live. Wrap that
        // in an undoable add without re-adding it.
        _undo.Push(new AlreadyAddedCommand(_document, draft));
        Tool = EditorTool.Select;
        SetSelected(draft);
    }

    private void CommitPen()
    {
        if (_draftItem is not PenAnnotation pen)
        {
            return;
        }

        if (pen.Points.Count < 2)
        {
            _document.Remove(pen);
            return;
        }

        pen.SimplifyInPlace();
        _undo.Push(new AlreadyAddedCommand(_document, pen));

        // Freehand drawing is a continuous mode: keep the pencil active and leave the
        // completed stroke unselected so a selection polygon/adorner does not interrupt
        // the next stroke. The user can still press V or right-click to enter selection.
        SetSelected(null);
    }

    private static bool IsDegenerate(AnnotationItem item)
    {
        RectD b = item.Bounds;
        return b.Width < 2 && b.Height < 2;
    }

    private void PushProperty<TItem, TValue>(
        TItem item, string label, Action<TItem, TValue> setter, TValue before, TValue after)
        where TItem : AnnotationItem
    {
        if (EqualityComparer<TValue>.Default.Equals(before, after))
        {
            return;
        }

        setter(item, after);
        _undo.Push(new PropertyChangeCommand<TItem, TValue>(item, label, setter, before, after));
    }

    private void EnsureSelectionValid()
    {
        if (_selected is not null && !_document.Items.Contains(_selected))
        {
            SetSelected(null);
        }
    }

    private void RaiseVisual() => VisualInvalidated?.Invoke(this, EventArgs.Empty);

    private static RectD ApplyResize(RectD initial, ResizeHandle handle, PointD point)
    {
        double left = initial.Left;
        double top = initial.Top;
        double right = initial.Right;
        double bottom = initial.Bottom;

        if (handle is ResizeHandle.TopLeft or ResizeHandle.MiddleLeft or ResizeHandle.BottomLeft)
        {
            left = point.X;
        }

        if (handle is ResizeHandle.TopRight or ResizeHandle.MiddleRight or ResizeHandle.BottomRight)
        {
            right = point.X;
        }

        if (handle is ResizeHandle.TopLeft or ResizeHandle.TopCenter or ResizeHandle.TopRight)
        {
            top = point.Y;
        }

        if (handle is ResizeHandle.BottomLeft or ResizeHandle.BottomCenter or ResizeHandle.BottomRight)
        {
            bottom = point.Y;
        }

        return new RectD(left, top, right - left, bottom - top).Normalized();
    }

    /// <summary>
    /// Which resize handle, if any, sits under <paramref name="point"/>.
    /// </summary>
    internal static ResizeHandle HitTestHandle(RectD bounds, PointD point, double tolerance)
    {
        RectD b = bounds.Normalized();
        double cx = (b.Left + b.Right) / 2;
        double cy = (b.Top + b.Bottom) / 2;

        (ResizeHandle Handle, double X, double Y)[] handles =
        [
            (ResizeHandle.TopLeft, b.Left, b.Top),
            (ResizeHandle.TopCenter, cx, b.Top),
            (ResizeHandle.TopRight, b.Right, b.Top),
            (ResizeHandle.MiddleRight, b.Right, cy),
            (ResizeHandle.BottomRight, b.Right, b.Bottom),
            (ResizeHandle.BottomCenter, cx, b.Bottom),
            (ResizeHandle.BottomLeft, b.Left, b.Bottom),
            (ResizeHandle.MiddleLeft, b.Left, cy),
        ];

        foreach ((ResizeHandle handle, double x, double y) in handles)
        {
            if (Math.Abs(point.X - x) <= tolerance && Math.Abs(point.Y - y) <= tolerance)
            {
                return handle;
            }
        }

        return ResizeHandle.None;
    }

    private enum Gesture
    {
        None,
        Create,
        Move,
        Resize,
        PenDraw,
    }
}

/// <summary>
/// Resize handle positions shared by the controller and the view.
/// </summary>
internal enum ResizeHandle
{
    None,
    TopLeft,
    TopCenter,
    TopRight,
    MiddleRight,
    BottomRight,
    BottomCenter,
    BottomLeft,
    MiddleLeft,
}

/// <summary>
/// Records an add for an item that was already inserted into the document during a live
/// gesture, so undo removes it and redo re-inserts it at its original paint position.
/// </summary>
/// <remarks>
/// A plain <see cref="AddAnnotationCommand"/> would add the item a second time on first
/// execute. This variant treats the initial state as "already present" and only performs
/// the reinsertion on redo.
/// </remarks>
internal sealed class AlreadyAddedCommand : IUndoableCommand
{
    private readonly AnnotationDocument _document;
    private readonly AnnotationItem _item;
    private int _index;

    internal AlreadyAddedCommand(AnnotationDocument document, AnnotationItem item)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _index = document.IndexOf(item);
    }

    public string Description => $"{_item.DisplayName} 추가";

    public void Execute()
    {
        if (!_document.Items.Contains(_item))
        {
            _document.Insert(_index < 0 ? _document.Items.Count : _index, _item);
        }
    }

    public void Undo()
    {
        _index = _document.IndexOf(_item);
        _document.Remove(_item);
    }
}
