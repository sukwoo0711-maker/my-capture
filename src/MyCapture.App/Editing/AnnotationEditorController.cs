using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
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
/// drag to move) records exactly one command on release. Inspector changes use the
/// stack's merge window to keep continuous slider adjustments together.
/// </para>
/// </remarks>
internal sealed class AnnotationEditorController
{
    private readonly AnnotationDocument _document;
    private readonly UndoStack _undo;

    private EditorTool _tool = EditorTool.Rectangle;
    private AnnotationItem? _selected;
    private readonly List<AnnotationItem> _selection = [];
    private AnnotationItem[] _gestureSelection = [];
    private PointD _moveOffset;
    private List<IUndoableCommand>? _selectionStyleChanges;

    // Live-gesture state.
    private Gesture _gesture = Gesture.None;
    private PointD _gestureStart;
    private RectD _transformBefore;
    private ResizeHandle _resizeHandle = ResizeHandle.None;
    private bool _resizeStarted;
    private TextLayout _textLayoutBefore;
    private AnnotationItem? _draftItem;
    private List<PointD>? _draftPoints;

    // The draft pen's own point list, kept equal to _draftPoints by appending each move's point
    // instead of copying the whole stroke. Null when something else changed the pen's points,
    // so the next move gives it a fresh copy of the drawn points, as every move used to.
    private List<PointD>? _draftPenPoints;
    private bool _updatingDraftPen;

    // The text being typed into a live box. Until it is committed its add command is the
    // whole history of the item, so style changes apply to it directly (see PushProperty).
    private TextAnnotation? _creatingText;

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

            CancelGesture();
            _tool = value;
            if (value != EditorTool.Select)
            {
                SetSelected(null);
            }

            RaiseVisual();
        }
    }

    internal AnnotationItem? Selected => _selected;
    internal IReadOnlyList<AnnotationItem> SelectedItems => _selection;
    internal RectD? MarqueeBounds { get; private set; }
    internal RectD? SelectionBounds => _selection.Count == 0 ? null : new RectD(
        _selection.Min(item => item.Bounds.Left), _selection.Min(item => item.Bounds.Top),
        _selection.Max(item => item.Bounds.Right) - _selection.Min(item => item.Bounds.Left),
        _selection.Max(item => item.Bounds.Bottom) - _selection.Min(item => item.Bounds.Top));

    /// <summary>Current stroke/foreground colour applied to new items.</summary>
    internal ColorRgba StrokeColor { get; set; } = ColorRgba.FromRgb(0xEF, 0x44, 0x44);

    /// <summary>Current stroke thickness applied to new shape/line/pen items.</summary>
    internal double StrokeThickness { get; set; } = 3;
    internal AnnotationStrokeStyle StrokeStyle { get; private set; }
    internal double FillTransparency { get; private set; } = 100;
    internal double DefaultFontSize { get; set; } = 18;
    internal string DefaultFontFamily { get; set; } = "Malgun Gothic";

    /// <summary>Blur strength applied to new blur layers, in image pixels.</summary>
    internal int BlurRadius { get; private set; } = BlurAnnotation.DefaultRadius;

    internal bool CanUndo => _undo.CanUndo;

    internal bool CanRedo => _undo.CanRedo;

    internal bool IsCreatingText { get; private set; }

    internal TextAnnotation? EditingText { get; private set; }

    internal TextAnnotation? BeginSelectedTextEdit() =>
        _selection.Count == 1 && _selected is TextAnnotation text && BeginTextEdit(text) ? text : null;

    internal bool BeginTextEdit(TextAnnotation text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (EditingText is not null || text.IsStepNumber
            || !_document.Items.Contains(text)) return false;
        EditingText = text;
        Tool = EditorTool.Select;
        SetSelected(text);
        RaiseVisual();
        return true;
    }

    internal TextAnnotation? BeginTextAtPointer(PointD point, int clickCount, bool shift)
    {
        if (EditingText is not null) return null;
        if (_tool == EditorTool.Text)
        {
            if (!shift && HitTest(point) is TextAnnotation text && BeginTextEdit(text)) return text;
            return BeginTextAnnotation(point, DefaultFontSize, DefaultFontSize * 1.4);
        }
        return _tool == EditorTool.Select && clickCount == 2
            && HitTest(point) is TextAnnotation hit && BeginTextEdit(hit) ? hit : null;
    }

    internal void CancelTextEdit(TextAnnotation text)
    {
        if (!ReferenceEquals(EditingText, text)) return;
        if (IsCreatingText) CommitTextEdit(text, string.Empty);
        else
        {
            EditingText = null;
            RaiseVisual();
        }
    }

    /// <summary>
    /// Measures the box a caption needs for its text, size and wrapping mode, keeping its
    /// top-left corner (see <see cref="TextAnnotationLayout.Measure"/>). Without one, boxes keep
    /// the size they were given, which is enough for tests that do not lay out text.
    /// </summary>
    internal Func<TextAnnotation, RectD>? TextFitter { get; set; }

    /// <summary>
    /// Pixels within which a click counts as hitting an annotation. Scaled by the caller
    /// to stay constant on screen regardless of zoom.
    /// </summary>
    internal double HitTolerance { get; set; } = 6;

    internal void SetSelected(AnnotationItem? item)
    {
        SetSelection(item is null ? [] : [item]);
    }

    private void SetSelection(IEnumerable<AnnotationItem> items)
    {
        AnnotationItem[] selection = items.Distinct().ToArray();
        if (_selection.SequenceEqual(selection)) return;
        _selection.Clear();
        _selection.AddRange(selection);
        _selected = _selection.LastOrDefault();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseVisual();
    }

    internal void ToggleSelected(AnnotationItem item)
    {
        if (!_document.Items.Contains(item)) return;
        SetSelection(_selection.Contains(item) ? _selection.Where(selected => selected != item) : _selection.Append(item));
    }

    internal void SelectAll()
    {
        CancelGesture();
        Tool = EditorTool.Select;
        SetSelection(_document.Items);
    }

    internal bool PerformUndo()
    {
        CancelGesture();
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
        CancelGesture();
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
        CancelGesture();
        if (_selection.Count == 0) return;
        AnnotationItem[] victims = _selection.ToArray();
        SetSelected(null);
        _undo.Execute(new CompositeCommand(UiText.Get("EditorSelection_Delete"),
            victims.Select(item => new RemoveAnnotationCommand(_document, item))));
        RaiseVisual();
    }

    internal void NudgeSelected(double dx, double dy)
    {
        CancelGesture();
        if (_selection.Count == 0 || (dx == 0 && dy == 0)) return;
        _undo.Execute(new TranslateAnnotationsCommand(_selection, dx, dy));
        RaiseVisual();
    }

    internal void DuplicateSelected()
    {
        CancelGesture();
        if (_selection.Count == 0) return;
        AnnotationItem[] copies = _document.Items.Where(_selection.Contains).Select(item => item.Clone()).ToArray();
        foreach (AnnotationItem copy in copies) copy.Translate(10, 10);
        _undo.Execute(new CompositeCommand(UiText.Get("EditorSelection_Duplicate"),
            copies.Select(item => new AddAnnotationCommand(_document, item))));
        SetSelection(copies);
        RaiseVisual();
    }

    internal void ReorderSelected(bool forward)
    {
        CancelGesture();
        var commands = new List<IUndoableCommand>();
        var selected = _selection.ToHashSet();
        // Walk from the leading edge so selected neighbours keep their relative order.
        AnnotationItem[] ordered = _document.Items.Where(selected.Contains).ToArray();
        if (forward) Array.Reverse(ordered);
        foreach (AnnotationItem item in ordered)
        {
            int index = _document.IndexOf(item);
            int target = index + (forward ? 1 : -1);
            if (target < 0 || target >= _document.Items.Count || selected.Contains(_document.Items[target])) continue;
            var command = new ReorderAnnotationCommand(_document, item, target);
            command.Execute();
            commands.Add(command);
        }
        if (commands.Count > 0)
            _undo.Push(new CompositeCommand(UiText.Get(forward ? "EditorSelection_Forward" : "EditorSelection_Backward"), commands));
        RaiseVisual();
    }

    /// <summary>
    /// Rotates the annotation layer by <paramref name="quarterTurns"/> clockwise quarter
    /// turns as one undoable step. The caller rotates the base bitmap itself and keeps it
    /// in sync with the returned document dimensions.
    /// </summary>
    /// <summary>
    /// Clockwise quarter-turns applied to the live document relative to the original
    /// capture. The view uses this to keep the base bitmap in the same orientation;
    /// canvas width/height alone cannot distinguish 0° from 180°.
    /// </summary>
    internal int RotationTurns { get; private set; }

    /// <summary>
    /// The original capture's pixel size at zero turns. Rotation must remap the crop
    /// origin between orientations, and the canvas size alone cannot supply the rotated
    /// base's dimensions once a crop has shrunk it. Leave both at zero to skip remapping.
    /// </summary>
    internal int BaseWidthAtZeroTurns { get; set; }

    internal int BaseHeightAtZeroTurns { get; set; }

    /// <summary>
    /// Remaps the crop origin between canvas orientations so it keeps describing the
    /// canvas top-left's offset within the base bitmap at the current orientation.
    /// Rotation swaps the axes, so a crop taken before a rotation would otherwise be cut
    /// (or undone) from the wrong region afterwards.
    /// </summary>
    internal void RemapCropOriginForRotation(int turns, int canvasWidthBefore, int canvasHeightBefore, bool forward)
    {
        if (turns == 0 || BaseWidthAtZeroTurns <= 0 || BaseHeightAtZeroTurns <= 0)
        {
            return;
        }

        PointD origin = CropOrigin;
        int width = canvasWidthBefore;
        int height = canvasHeightBefore;
        int orientation = RotationTurns;
        for (int step = 0; step < Math.Abs(turns); step++)
        {
            if (forward)
            {
                int baseHeight = orientation % 2 == 0 ? BaseHeightAtZeroTurns : BaseWidthAtZeroTurns;
                origin = new PointD(baseHeight - (origin.Y + height), origin.X);
                orientation++;
            }
            else
            {
                // Inverse of the forward step at this orientation: the pre-step canvas
                // width is the post-step canvas width of the state we are stepping into.
                int baseHeight = (orientation - 1) % 2 == 0 ? BaseHeightAtZeroTurns : BaseWidthAtZeroTurns;
                origin = new PointD(origin.Y, baseHeight - origin.X - width);
                orientation--;
            }

            (width, height) = (height, width);
        }

        CropOrigin = origin;
    }

    internal void RotateDocument(int quarterTurns)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0)
        {
            return;
        }

        SetSelected(null);
        _undo.Execute(new DocumentRotationCommand(this, _document, turns));
        RaiseVisual();
    }

    private void AddRotation(int quarterTurns) =>
        RotationTurns = ((RotationTurns + quarterTurns) % 4 + 4) % 4;

    /// <summary>
    /// Crops the capture to <paramref name="region"/> as one undoable step: the canvas
    /// shrinks and every annotation translates by the new origin. The caller replaces the
    /// base bitmap with the cropped pixels and keeps them in sync via undo-stack changes.
    /// </summary>
    internal void CropDocument(RectD region)
    {
        RectD clamped = region.Normalized()
            .ClampTo(new RectD(0, 0, _document.CanvasWidth, _document.CanvasHeight));
        if (clamped.Width < 2 || clamped.Height < 2
            || (clamped.Width >= _document.CanvasWidth && clamped.Height >= _document.CanvasHeight))
        {
            return;
        }

        SetSelected(null);
        _undo.Execute(new DocumentCropCommand(this, _document, clamped));
        RaiseVisual();
    }

    /// <summary>How far the live canvas origin sits from the base bitmap at the current orientation, in pixels.</summary>
    internal PointD CropOrigin { get; private set; }

    internal void RestoreDraftGeometry(int rotationTurns, PointD cropOrigin)
    {
        RotationTurns = ((rotationTurns % 4) + 4) % 4;
        CropOrigin = cropOrigin;
    }

    /// <summary>Rotates the annotation document in place as one reversible command.</summary>
    private sealed class DocumentCropCommand : IUndoableCommand
    {
        private readonly AnnotationEditorController _owner;
        private readonly AnnotationDocument _document;
        private readonly RectD _region;
        private readonly int _widthBefore;
        private readonly int _heightBefore;

        public DocumentCropCommand(AnnotationEditorController owner, AnnotationDocument document, RectD region)
        {
            _owner = owner;
            _document = document;
            _region = region;
            _widthBefore = document.CanvasWidth;
            _heightBefore = document.CanvasHeight;
        }

        public string Description => UiText.Get("Tools_Crop");

        public void Execute()
        {
            Apply(_document, _region);
            _owner.CropOrigin = new PointD(_owner.CropOrigin.X + _region.X, _owner.CropOrigin.Y + _region.Y);
        }

        public void Undo()
        {
            // Restore the exact pre-crop canvas. Growing by the crop's right/bottom edges
            // is only correct for a crop that reached them; a middle crop must come back
            // to the size the canvas had before the cut.
            Apply(_document, new RectD(-_region.X, -_region.Y, _widthBefore, _heightBefore));
            _owner.CropOrigin = new PointD(_owner.CropOrigin.X - _region.X, _owner.CropOrigin.Y - _region.Y);
        }

        private static void Apply(AnnotationDocument document, RectD offset)
        {
            foreach (AnnotationItem item in document.Items)
            {
                item.Translate(-offset.X, -offset.Y);
            }

            document.CanvasWidth = Math.Max(1, (int)offset.Width);
            document.CanvasHeight = Math.Max(1, (int)offset.Height);
        }
    }

    /// <summary>Rotates the annotation document in place as one reversible command.</summary>
    private sealed class DocumentRotationCommand : IUndoableCommand
    {
        private readonly AnnotationEditorController _owner;
        private readonly AnnotationDocument _document;
        private readonly int _turns;

        public DocumentRotationCommand(AnnotationEditorController owner, AnnotationDocument document, int turns)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _turns = turns;
        }

        public string Description => UiText.Get("Text_6B2C41A9E530");

        public void Execute()
        {
            int width = _document.CanvasWidth;
            int height = _document.CanvasHeight;
            AnnotationDocumentRotator.Rotate(_document, _turns);
            _owner.RemapCropOriginForRotation(_turns, width, height, forward: true);
            _owner.AddRotation(_turns);
        }

        public void Undo()
        {
            int width = _document.CanvasWidth;
            int height = _document.CanvasHeight;
            AnnotationDocumentRotator.Rotate(_document, -_turns);
            _owner.RemapCropOriginForRotation(-_turns, width, height, forward: false);
            _owner.AddRotation(-_turns);
        }
    }

    // ---- Pointer gestures (image-pixel coordinates) ---------------------------------

    internal void PointerDown(PointD point, bool shift = false, bool control = false)
    {
        CancelGesture();
        switch (_tool)
        {
            case EditorTool.Select:
                BeginSelectGesture(point, shift || control);
                break;
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            case EditorTool.Highlighter:
            case EditorTool.Arrow:
            case EditorTool.Pen:
            case EditorTool.Blur:
                BeginDraftGesture(point);
                break;
            case EditorTool.Text:
            case EditorTool.Image:
                // Placed via a single click, handled by the view (TextBox / OpenFileDialog).
                break;
            case EditorTool.StepNumber:
                AddStepNumber(point);
                break;
        }
    }

    internal void PointerMove(PointD point, bool shift = false)
    {
        switch (_gesture)
        {
            case Gesture.ModifiedSelect:
                if (Math.Abs(point.X - _gestureStart.X) <= HitTolerance
                    && Math.Abs(point.Y - _gestureStart.Y) <= HitTolerance) break;
                SetSelection(_gestureSelection);
                _gesture = Gesture.Move;
                PointerMove(point, shift);
                break;
            case Gesture.Create:
                UpdateDraft(point, shift);
                break;
            case Gesture.Move:
                PointD constrained = shift ? AnnotationGestureConstraints.Axis(_gestureStart, point) : point;
                PointD offset = new(constrained.X - _gestureStart.X, constrained.Y - _gestureStart.Y);
                foreach (AnnotationItem item in _gestureSelection)
                    item.Translate(offset.X - _moveOffset.X, offset.Y - _moveOffset.Y);
                bool moved = offset != _moveOffset;
                _moveOffset = offset;
                // A repeated position (WPF raises MouseMove again after a layout pass, and an
                // axis-locked drag ignores the other axis) moves nothing, so nothing repaints.
                if (moved) RaiseVisual();
                break;
            case Gesture.Marquee:
                RectD? marqueeBefore = MarqueeBounds;
                MarqueeBounds = RectD.FromCorners(_gestureStart, point);
                // Explorer-style intersection selection, including touching bounds. A click
                // has no area and only clears selection (or preserves it with a modifier).
                SetSelection(MarqueeBounds.Value.IsEmpty ? _gestureSelection
                    : _gestureSelection.Concat(_document.HitTestRegion(MarqueeBounds.Value)));
                if (MarqueeBounds != marqueeBefore) RaiseVisual();
                break;
            case Gesture.Resize:
                // Handles extend beyond the bounds; a stationary click must not snap the
                // annotation to the press point, including a same-position move event.
                if (!_resizeStarted && point == _gestureStart) break;
                _resizeStarted = true;
                if (_selected is TextAnnotation text)
                {
                    ResizeText(text, point);
                    RaiseVisual();
                }
                else if (_selected is not null)
                {
                    _selected.SetBounds(ApplyResize(_transformBefore, _resizeHandle, point));
                    RaiseVisual();
                }

                break;
            case Gesture.PenDraw:
                _draftPoints?.Add(point);
                if (_draftItem is PenAnnotation pen)
                {
                    // The pen's list already holds every earlier point, so one append keeps it
                    // equal to the drawn points; reassigning it raises the same change
                    // notifications the copy did.
                    List<PointD> points;
                    if (_draftPenPoints is { } own && ReferenceEquals(pen.Points, own))
                    {
                        own.Add(point);
                        points = own;
                    }
                    else
                    {
                        points = [.. _draftPoints!];
                    }

                    _draftPenPoints = points;
                    _updatingDraftPen = true;
                    try
                    {
                        pen.Points = points;
                    }
                    finally
                    {
                        _updatingDraftPen = false;
                    }
                }

                RaiseVisual();
                break;
        }
    }

    internal void PointerUp(PointD point, bool shift = false)
    {
        if (_gesture is Gesture.Move or Gesture.ModifiedSelect or Gesture.Marquee or Gesture.Resize) PointerMove(point, shift);
        switch (_gesture)
        {
            case Gesture.Create:
                CommitDraftShape(point, shift);
                break;
            case Gesture.PenDraw:
                CommitPen();
                break;
            case Gesture.Resize when _selected is TextAnnotation text:
                // Box, size and wrapping mode change together; one state command restores all
                // three exactly. Replaying a transform instead would rescale the font again.
                TextLayout resized = TextLayout.Of(text);
                if (resized != _textLayoutBefore)
                {
                    _undo.Push(new PropertyChangeCommand<TextAnnotation, TextLayout>(
                        text, UiText.Get("Text_258AD4B095A1"), TextLayout.Apply, _textLayoutBefore, resized));
                }

                break;
            case Gesture.Move:
                if (_moveOffset != default)
                    _undo.Push(new TranslateAnnotationsCommand(_gestureSelection, _moveOffset.X, _moveOffset.Y));
                break;
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

        ResetGesture();
        RaiseVisual();
    }

    internal void CancelGesture()
    {
        switch (_gesture)
        {
            case Gesture.Move:
                foreach (AnnotationItem item in _gestureSelection) item.Translate(-_moveOffset.X, -_moveOffset.Y);
                break;
            case Gesture.Resize when _selected is TextAnnotation text:
                TextLayout.Apply(text, _textLayoutBefore);
                break;
            case Gesture.Resize:
                _selected?.SetBounds(_transformBefore);
                break;
            case Gesture.Create:
            case Gesture.PenDraw:
                if (_draftItem is not null) _document.Remove(_draftItem);
                break;
            case Gesture.Marquee:
                SetSelection(_gestureSelection);
                break;
            case Gesture.None:
                return;
        }
        ResetGesture();
        RaiseVisual();
    }

    private void ResetGesture()
    {
        _gesture = Gesture.None;
        _gestureSelection = [];
        _moveOffset = default;
        MarqueeBounds = null;
        _resizeHandle = ResizeHandle.None;
        _resizeStarted = false;
        if (_draftItem is PenAnnotation draftPen)
        {
            draftPen.PropertyChanged -= OnDraftPenChanged;
        }

        _draftItem = null;
        _draftPoints = null;
        _draftPenPoints = null;
    }

    /// <summary>
    /// Anything but the drawing gesture that changes the draft pen's points (moving or rotating
    /// the document mid-stroke) ends the shared list, so the next move copies the drawn points
    /// over that change exactly as it did when every move copied.
    /// </summary>
    private void OnDraftPenChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_updatingDraftPen && e.PropertyName == nameof(PenAnnotation.Points))
        {
            _draftPenPoints = null;
        }
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
            FontSize = DefaultFontSize,
            FontFamily = DefaultFontFamily,
            Text = string.Empty,
            // A new caption follows its text until the user sets a width.
            AutoSize = true,
        };
        TextLayout.Apply(text, FitText(text, text.Text, text.FontSize, autoSize: true, text.Rect));

        _undo.Execute(new AddAnnotationCommand(_document, text));
        IsCreatingText = true;
        _creatingText = text;
        EditingText = text;
        Tool = EditorTool.Select;
        SetSelected(text);
        return text;
    }

    /// <summary>
    /// Finishes a live text edit. An empty new box undoes its add; clearing an existing
    /// caption records a removal. Otherwise the final text and fitted box change as one step.
    /// </summary>
    internal void CommitTextEdit(TextAnnotation text, string finalText)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool creating = ReferenceEquals(_creatingText, text);
        IsCreatingText = false;
        _creatingText = null;
        EditingText = null;

        if (string.IsNullOrEmpty(finalText))
        {
            if (!creating)
            {
                _undo.Execute(new RemoveAnnotationCommand(_document, text));
            }
            else if (_undo.CanUndo && ReferenceEquals(_document.Items.LastOrDefault(), text))
            {
                // New-box style changes do not add history, so its add is still on top.
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
            string label = UiText.Get("Text_258AD4B095A1");
            TextLayout fitted = FitText(text, finalText, text.FontSize, text.AutoSize, text.Rect);
            // An explicit composite keeps separate editing sessions separate, even when
            // the measured box stays unchanged and edits finish within the merge window.
            _undo.Execute(new CompositeCommand(label,
            [
                new PropertyChangeCommand<TextAnnotation, string>(text, label,
                    static (t, v) => t.Text = v, text.Text, finalText),
                new PropertyChangeCommand<TextAnnotation, TextLayout>(text, label,
                    TextLayout.Apply, TextLayout.Of(text), fitted),
            ]));
        }

        RaiseVisual();
    }

    /// <summary>
    /// The layout a caption takes with <paramref name="content"/>, <paramref name="fontSize"/>
    /// and the box <paramref name="rect"/> (its top-left, and its width when not auto-size).
    /// </summary>
    /// <remarks>
    /// An auto-size box that would run past the image's right edge becomes a fixed-width box
    /// ending at that edge (at least one em wide), so the caption wraps and grows downward
    /// instead of leaving the image. Typing, committing and changing the size all follow this
    /// rule; moving does not refit, so a caption renders the same wherever it is dragged.
    /// </remarks>
    internal TextLayout FitText(
        TextAnnotation text, string content, double fontSize, bool autoSize, RectD rect, bool keepInsideImage = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        var probe = (TextAnnotation)text.Clone();
        probe.Text = content;
        probe.FontSize = fontSize;
        probe.AutoSize = autoSize && !text.IsStepNumber;
        probe.Rect = rect;
        RectD measured = TextFitter?.Invoke(probe) ?? rect;

        if (probe.AutoSize && keepInsideImage && _document.CanvasWidth > 0
            && measured.Right > _document.CanvasWidth + 0.01)
        {
            probe.AutoSize = false;
            probe.Rect = new RectD(rect.X, rect.Y, Math.Max(_document.CanvasWidth - rect.X, probe.FontSize), rect.Height);
            measured = TextFitter?.Invoke(probe) ?? probe.Rect;
        }

        return new TextLayout(measured, probe.FontSize, probe.AutoSize);
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
        using (_undo.BeginBatch(UiText.Get("Text_ACE79917CCBE")))
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
        ApplySelectionStyle(UiText.Get("Text_D1B87ACB2D22"), item => ApplyStrokeColor(item, color));
    }

    private void ApplyStrokeColor(AnnotationItem item, ColorRgba color)
    {
        switch (item)
        {
            case ShapeAnnotation shape:
                PushProperty(shape, UiText.Get("Text_D1B87ACB2D22"), static (s, v) =>
                    {
                        s.Stroke = v.Stroke;
                        s.Fill = v.Fill;
                    },
                    (Stroke: shape.Stroke, Fill: shape.Fill),
                    (Stroke: color, Fill: shape.FillMatchesStroke ? color.WithAlpha(shape.Fill.A) : shape.Fill));
                break;
            case PolylineAnnotation line:
                PushProperty(line, UiText.Get("Text_D1B87ACB2D22"), static (s, v) => s.Stroke = v, line.Stroke, color);
                break;
            case PenAnnotation pen:
                PushProperty(pen, UiText.Get("Text_D1B87ACB2D22"), static (s, v) => s.Stroke = v, pen.Stroke, color);
                break;
            case TextAnnotation { IsStepNumber: true } badge:
                PushProperty(badge, UiText.Get("Text_D1B87ACB2D22"), static (s, v) => s.Background = v, badge.Background, color);
                break;
            case TextAnnotation text:
                PushProperty(text, UiText.Get("Text_D1B87ACB2D22"), static (s, v) => s.Foreground = v, text.Foreground, color);
                break;
        }
    }

    internal void ApplyStrokeThickness(double thickness)
    {
        // The settings range is the one contract for a stroke width, wherever it is typed.
        thickness = double.IsFinite(thickness)
            ? Math.Clamp(thickness, SettingsRanges.StrokeThickness.Min, SettingsRanges.StrokeThickness.Max)
            : StrokeThickness;
        if ((_selection.Count == 1 && _selected is ShapeAnnotation { StrokeStyle: AnnotationStrokeStyle.ThickDashed })
            || (_selected is null && Tool == EditorTool.Rectangle && StrokeStyle == AnnotationStrokeStyle.ThickDashed))
        {
            thickness = Math.Max(6, thickness);
        }
        StrokeThickness = thickness;
        ApplySelectionStyle(UiText.Get("Text_6953A4195620"), item => ApplyStrokeThickness(item, thickness));
    }

    private void ApplyStrokeThickness(AnnotationItem item, double thickness)
    {
        switch (item)
        {
            case ShapeAnnotation shape:
                PushProperty(shape, UiText.Get("Text_6953A4195620"), static (s, v) => s.StrokeThickness = v, shape.StrokeThickness,
                    shape.StrokeStyle == AnnotationStrokeStyle.ThickDashed ? Math.Max(6, thickness) : thickness);
                break;
            case PolylineAnnotation line:
                PushProperty(line, UiText.Get("Text_6953A4195620"), static (s, v) => s.StrokeThickness = v, line.StrokeThickness, thickness);
                break;
            case PenAnnotation pen:
                PushProperty(pen, UiText.Get("Text_6953A4195620"), static (s, v) => s.StrokeThickness = v, pen.StrokeThickness, thickness);
                break;
        }
    }

    internal void ApplyStrokeStyle(AnnotationStrokeStyle style)
    {
        StrokeStyle = Enum.IsDefined(style) ? style : AnnotationStrokeStyle.Solid;
        if (StrokeStyle == AnnotationStrokeStyle.ThickDashed)
        {
            StrokeThickness = Math.Max(6, StrokeThickness);
        }

        ApplySelectionStyle(UiText.Get("Text_E0B58D1CFED1"), item =>
        {
            if (item is ShapeAnnotation shape)
            {
                PushProperty(shape, UiText.Get("Text_E0B58D1CFED1"), static (s, v) =>
                    {
                        s.StrokeStyle = v.Style;
                        s.StrokeThickness = v.Thickness;
                    },
                    (Style: shape.StrokeStyle, Thickness: shape.StrokeThickness),
                    (Style: StrokeStyle, Thickness: StrokeStyle == AnnotationStrokeStyle.ThickDashed
                        ? Math.Max(6, shape.StrokeThickness) : shape.StrokeThickness));
            }
        });
    }

    internal void ApplyFillTransparency(double transparency)
    {
        FillTransparency = double.IsFinite(transparency) ? Math.Clamp(transparency, 0, 100) : 100;
        ApplySelectionStyle(UiText.Get("Text_5321CB80BCA3"), item =>
        {
            if (item is ShapeAnnotation shape)
            {
                PushProperty(shape, UiText.Get("Text_5321CB80BCA3"), static (s, v) =>
                    {
                        s.Fill = v.Fill;
                        s.FillMatchesStroke = v.Matches;
                    },
                    (Fill: shape.Fill, Matches: shape.FillMatchesStroke),
                    (Fill: ShapeAnnotation.FillForTransparency(shape.Stroke, FillTransparency), Matches: true));
            }
        });
    }

    private void ApplySelectionStyle(string label, Action<AnnotationItem> apply)
    {
        var changes = new List<IUndoableCommand>();
        _selectionStyleChanges = changes;
        try
        {
            foreach (AnnotationItem item in _selection) apply(item);
        }
        finally
        {
            _selectionStyleChanges = null;
        }
        if (changes.Count > 0)
            _undo.Push(_selection.Count == 1 ? changes[0] : new SelectionStyleCommand(label, _selection, changes));
        RaiseVisual();
    }

    /// <summary>
    /// Applies a font size to the selected text annotation, or records it as the default
    /// for the next text annotation when nothing is selected. The box is refitted to the
    /// text at the new size, so a larger font is never trimmed by its own rect.
    /// </summary>
    internal void ApplyFontSize(double fontSize)
    {
        double size = double.IsFinite(fontSize)
            ? Math.Clamp(fontSize, SettingsRanges.FontSize.Min, SettingsRanges.FontSize.Max) : DefaultFontSize;
        DefaultFontSize = size;
        if (_selected is not TextAnnotation text)
        {
            return;
        }

        if (Math.Abs(text.FontSize - size) < 1e-6)
        {
            return;
        }

        TextLayout after;
        if (text.IsStepNumber)
        {
            // A badge's number is centred in its own square; scale the square with it.
            double ratio = size / Math.Max(double.Epsilon, text.FontSize);
            RectD box = text.Rect;
            after = new TextLayout(new RectD(box.X, box.Y, box.Width * ratio, box.Height * ratio), size, text.AutoSize);
        }
        else
        {
            after = FitText(text, text.Text, size, text.AutoSize, text.Rect);
        }

        PushProperty(text, UiText.Get("Text_258AD4B095A1"), TextLayout.Apply, TextLayout.Of(text), after);
        RaiseVisual();
    }

    /// <summary>
    /// Applies a blur strength to the selected blur layer as one undoable change, or records
    /// it as the strength of the next blur when no blur is selected.
    /// </summary>
    internal void ApplyBlurRadius(double radius)
    {
        BlurRadius = BlurAnnotation.ClampRadius(radius);
        if (_selected is BlurAnnotation blur)
        {
            PushProperty(blur, UiText.Get("EditorBlur_Strength"), static (b, v) => b.Radius = v, blur.Radius, BlurRadius);
        }

        RaiseVisual();
    }

    /// <summary>
    /// Hit-tests handles first, then the item itself. Exposed so the view can pick a cursor.
    /// </summary>
    internal ResizeHandle HandleAt(PointD point, double handlePixels)
    {
        if (_selection.Count != 1 || _selected is null || !_selected.SupportsResize)
        {
            return ResizeHandle.None;
        }

        return UsableHandle(_selected, HitTestHandle(_selected.Bounds, point, handlePixels));
    }

    /// <summary>
    /// <paramref name="handle"/>, or <see cref="ResizeHandle.None"/> when it does nothing for
    /// <paramref name="item"/>: a caption's height always follows its text, so its top and
    /// bottom handles are not offered.
    /// </summary>
    internal static ResizeHandle UsableHandle(AnnotationItem item, ResizeHandle handle) =>
        item is TextAnnotation { IsStepNumber: false } && handle is ResizeHandle.TopCenter or ResizeHandle.BottomCenter
            ? ResizeHandle.None
            : handle;

    internal AnnotationItem? HitTest(PointD point) => _document.HitTest(point, HitTolerance);

    private void BeginSelectGesture(PointD point, bool toggle)
    {
        _gestureStart = point;
        if (!toggle && _selection.Count == 1 && _selected is not null && _selected.SupportsResize)
        {
            ResizeHandle handle = UsableHandle(_selected, HitTestHandle(_selected.Bounds, point, HitTolerance * 1.5));
            if (handle != ResizeHandle.None)
            {
                _gesture = Gesture.Resize;
                _resizeHandle = handle;
                _transformBefore = _selected.Bounds;
                _textLayoutBefore = _selected is TextAnnotation text ? TextLayout.Of(text) : default;
                return;
            }
        }

        AnnotationItem? hit = _document.HitTest(point, HitTolerance);
        if (hit is not null)
        {
            if (toggle)
            {
                AnnotationItem[] before = _selection.ToArray();
                ToggleSelected(hit);
                // A modifier click toggles, but a drag moves the group that was picked.
                // This also permits holding Shift before starting an axis-locked drag.
                _gestureSelection = before.Contains(hit) ? before : _selection.ToArray();
                _gesture = Gesture.ModifiedSelect;
                return;
            }
            if (!_selection.Contains(hit)) SetSelected(hit);
            _gesture = Gesture.Move;
            _gestureSelection = _selection.ToArray();
            _moveOffset = default;
        }
        else
        {
            _gestureSelection = toggle ? _selection.ToArray() : [];
            if (!toggle) SetSelected(null);
            _gesture = Gesture.Marquee;
            MarqueeBounds = new RectD(point.X, point.Y, 0, 0);
            RaiseVisual();
        }
    }

    private void BeginDraftGesture(PointD point)
    {
        _gestureStart = point;

        if (_tool is EditorTool.Pen or EditorTool.Highlighter)
        {
            _draftPoints = [point];
            _draftPenPoints = [point];
            var pen = new PenAnnotation
            {
                IsHighlighter = _tool == EditorTool.Highlighter,
                Points = _draftPenPoints,
                Stroke = StrokeColor,
                StrokeThickness = _tool == EditorTool.Highlighter ? Math.Max(12, StrokeThickness) : StrokeThickness,
            };
            pen.PropertyChanged += OnDraftPenChanged;
            _draftItem = pen;
            _document.Add(pen);
            _gesture = Gesture.PenDraw;
            SetSelected(null);
            return;
        }

        _gesture = Gesture.Create;
        AnnotationItem draft = _tool switch
        {
            EditorTool.Ellipse => new EllipseAnnotation
            {
                Rect = new RectD(point.X, point.Y, 0, 0), Stroke = StrokeColor,
                StrokeThickness = StrokeThickness, StrokeStyle = StrokeStyle,
                Fill = ShapeAnnotation.FillForTransparency(StrokeColor, FillTransparency), FillMatchesStroke = true,
            },
            EditorTool.Rectangle => new RectangleAnnotation
            {
                Rect = new RectD(point.X, point.Y, 0, 0),
                Stroke = StrokeColor,
                StrokeThickness = StrokeThickness,
                StrokeStyle = StrokeStyle,
                Fill = ShapeAnnotation.FillForTransparency(StrokeColor, FillTransparency),
                FillMatchesStroke = true,
            },
            EditorTool.Arrow => PolylineAnnotation.CreateArrow(point, point, StrokeColor, StrokeThickness),
            EditorTool.Blur => new BlurAnnotation { Rect = new RectD(point.X, point.Y, 0, 0), Radius = BlurRadius },
            _ => throw new InvalidOperationException($"Tool {_tool} has no draft shape."),
        };

        _draftItem = draft;
        _document.Add(draft);
        SetSelected(null);
    }

    private void UpdateDraft(PointD point, bool shift = false)
    {
        if (shift)
        {
            if (_draftItem is ShapeAnnotation) point = AnnotationGestureConstraints.Square(_gestureStart, point);
            else if (_draftItem is PolylineAnnotation) point = AnnotationGestureConstraints.Line(_gestureStart, point);
        }
        // Unchanged when the pointer reports the same position again; nothing then repaints.
        bool changed = true;
        switch (_draftItem)
        {
            case ShapeAnnotation rect:
                RectD rectBefore = rect.Rect;
                rect.Rect = RectD.FromCorners(_gestureStart, point);
                changed = rect.Rect != rectBefore;
                break;
            case BlurAnnotation blur:
                RectD blurBefore = blur.Rect;
                blur.Rect = RectD.FromCorners(_gestureStart, point);
                changed = blur.Rect != blurBefore;
                break;
            case PolylineAnnotation line:
                List<PointD> lineBefore = line.Points;
                line.Points = [_gestureStart, point];
                changed = lineBefore is not [var start, var end] || start != _gestureStart || end != point;
                break;
        }

        if (changed) RaiseVisual();
    }

    private void CommitDraftShape(PointD point, bool shift)
    {
        UpdateDraft(point, shift);

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
        // Shape tools stay active after a completed shape (like the pen) so a user filling
        // an area with boxes does not have to re-select the tool for every one. The shape
        // is still selected for immediate inspector edits; the next press starts fresh.
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

    /// <summary>
    /// The number the step tool adds next: one more than the highest number on the image, so
    /// deleting or undoing the last badge gives its number back.
    /// </summary>
    internal int NextStepNumber => _document.Items.OfType<TextAnnotation>().Where(t => t.IsStepNumber)
        .Select(t => int.TryParse(t.Text, out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1;

    internal TextAnnotation AddStepNumber(PointD point)
    {
        int number = NextStepNumber;
        double size = Math.Max(32, DefaultFontSize * 2);
        var badge = new TextAnnotation { IsStepNumber = true, Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Rect = new RectD(point.X, point.Y, size, size), FontSize = Math.Max(16, DefaultFontSize),
            Background = StrokeColor, Foreground = ColorRgba.White, Bold = true, HasOutline = true };
        _undo.Execute(new AddAnnotationCommand(_document, badge));
        SetSelected(badge);
        RaiseVisual();
        return badge;
    }

    private static bool IsDegenerate(AnnotationItem item)
    {
        RectD b = item.Bounds;
        return b.Width < 2 && b.Height < 2;
    }

    internal void ApplyTextBold(bool bold)
    {
        if (_selected is not TextAnnotation { IsStepNumber: false } text || text.Bold == bold) return;
        string label = UiText.Get("EditorText_Bold");
        var probe = (TextAnnotation)text.Clone();
        probe.Bold = bold;
        TextLayout fitted = FitText(probe, text.Text, text.FontSize, text.AutoSize, text.Rect);
        using (_undo.BeginBatch(label))
        {
            PushProperty(text, label, static (t, v) => t.Bold = v, text.Bold, bold);
            PushProperty(text, label, TextLayout.Apply, TextLayout.Of(text), fitted);
        }
        RaiseVisual();
    }

    internal void ApplyTextBackground(bool enabled)
    {
        if (_selected is not TextAnnotation { IsStepNumber: false } text || (text.Background.A > 0) == enabled) return;
        ColorRgba background = enabled
            ? (text.Background == ColorRgba.Transparent ? ColorRgba.Black : text.Background.WithAlpha(255))
            : text.Background.WithAlpha(0);
        PushProperty(text, UiText.Get("EditorText_Background"), static (t, v) => t.Background = v,
            text.Background, background);
        RaiseVisual();
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
        if (ReferenceEquals(item, _creatingText))
        {
            // Still being typed: its add command restores the item with whatever style it has
            // when committed. A separate entry would sit above the add, so committing an
            // empty box would undo this change instead of removing the box.
            return;
        }

        var command = new PropertyChangeCommand<TItem, TValue>(item, label, setter, before, after);
        if (_selectionStyleChanges is { } changes) changes.Add(command);
        else _undo.Push(command);
    }

    /// <summary>
    /// Follows a resize handle on a caption from the state captured when the drag began, so
    /// the font scales once per gesture rather than compounding on every pointer move.
    /// </summary>
    /// <remarks>
    /// Corner: scales the font by the box-height ratio; an auto-size caption stays auto-size, a
    /// fixed-width caption scales its wrapping width by the same ratio. Left/right edge: sets a
    /// fixed wrapping width (at least one em) and the height follows the text. The opposite
    /// corner or edge stays put. A step badge simply takes the dragged box.
    /// </remarks>
    private void ResizeText(TextAnnotation text, PointD point)
    {
        TextLayout before = _textLayoutBefore;
        RectD box = before.Rect;
        if (text.IsStepNumber)
        {
            double badgeSize = before.FontSize;
            RectD dragged = ApplyResize(box, _resizeHandle, point);
            double badgeRatio = box.Height > double.Epsilon ? dragged.Height / box.Height : 1;
            if (IsCornerHandle(_resizeHandle) && double.IsFinite(badgeRatio) && badgeRatio > 0)
            {
                badgeSize = Math.Clamp(before.FontSize * badgeRatio, 8, 200);
            }

            TextLayout.Apply(text, new TextLayout(dragged, badgeSize, before.AutoSize));
            return;
        }

        TextLayout fitted;
        if (IsCornerHandle(_resizeHandle))
        {
            RectD dragged = ApplyResize(box, _resizeHandle, point);
            double ratio = box.Height > double.Epsilon ? dragged.Height / box.Height : 1;
            if (!double.IsFinite(ratio) || ratio <= 0)
            {
                return;
            }

            double size = Math.Clamp(before.FontSize * ratio, 8, 200);
            double applied = size / Math.Max(double.Epsilon, before.FontSize);
            RectD wrap = before.AutoSize ? box : new RectD(box.X, box.Y, box.Width * applied, box.Height);
            fitted = FitText(text, text.Text, size, before.AutoSize, wrap, keepInsideImage: false);
        }
        else if (_resizeHandle is ResizeHandle.MiddleLeft or ResizeHandle.MiddleRight)
        {
            double em = Math.Max(1, before.FontSize);
            double left = _resizeHandle == ResizeHandle.MiddleLeft ? Math.Min(point.X, box.Right - em) : box.Left;
            double right = _resizeHandle == ResizeHandle.MiddleRight ? Math.Max(point.X, box.Left + em) : box.Right;
            fitted = FitText(text, text.Text, before.FontSize, autoSize: false,
                new RectD(left, box.Y, right - left, box.Height), keepInsideImage: false);
        }
        else
        {
            return;
        }

        RectD measured = fitted.Rect;
        bool anchorRight = _resizeHandle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft or ResizeHandle.MiddleLeft;
        bool anchorBottom = _resizeHandle is ResizeHandle.TopLeft or ResizeHandle.TopRight;
        double x = anchorRight ? box.Right - measured.Width : box.Left;
        double y = anchorBottom ? box.Bottom - measured.Height : box.Top;
        TextLayout.Apply(text, fitted with { Rect = new RectD(x, y, measured.Width, measured.Height) });
    }

    private void EnsureSelectionValid()
    {
        SetSelection(_selection.Where(_document.Items.Contains));
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

    private static bool IsCornerHandle(ResizeHandle handle) =>
        handle is ResizeHandle.TopLeft or ResizeHandle.TopRight
            or ResizeHandle.BottomLeft or ResizeHandle.BottomRight;

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
        Marquee,
        ModifiedSelect,
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
/// A caption's box, size and wrapping mode, which always change together.
/// </summary>
internal readonly record struct TextLayout(RectD Rect, double FontSize, bool AutoSize)
{
    internal static TextLayout Of(TextAnnotation text) => new(text.Rect, text.FontSize, text.AutoSize);

    internal static void Apply(TextAnnotation text, TextLayout layout)
    {
        text.FontSize = layout.FontSize;
        text.AutoSize = layout.AutoSize;
        text.Rect = layout.Rect;
    }
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
    public IEnumerable<string> ReferencedImageAssets =>
        _item is ImageAnnotation image ? [image.AssetFileName] : [];
    private readonly AnnotationDocument _document;
    private readonly AnnotationItem _item;
    private int _index;

    internal AlreadyAddedCommand(AnnotationDocument document, AnnotationItem item)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _index = document.IndexOf(item);
    }

    public string Description => UiText.Format("Text_47E781C43156", _item.DisplayName);

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
