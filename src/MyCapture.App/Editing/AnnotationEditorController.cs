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
    private PointD _resizePointerOffset;
    private PointD[]? _resizePointsBefore;
    private PointerIntent? _activePointerIntent;
    private TextLayout _textLayoutBefore;
    private AnnotationItem? _draftItem;
    private List<PointD>? _draftPoints;

    // The draft pen's own point list, kept equal to _draftPoints by appending each move's point
    // instead of copying the whole stroke. Null when something else changed the pen's points,
    // so the next move gives it a fresh copy of the drawn points, as every move used to.
    private PenPointCollection? _draftPenPoints;
    private bool _updatingDraftPen;

    // The text being typed into a live box. Its draft is visible but has no undo entry
    // until its content is committed, so style changes apply directly (see PushProperty).
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

    /// <summary>A non-degenerate arrow was drawn and committed by a pointer gesture.</summary>
    internal event Action? UserArrowAdded;

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
            if (value != EditorTool.Select
                && !(value == EditorTool.StepNumber && _selection.Count == 1 && _selected is TextAnnotation { IsStepNumber: true }))
            {
                SetSelected(null);
            }

            RaiseVisual();
        }
    }

    internal AnnotationItem? Selected => _selected;
    internal IReadOnlyList<AnnotationItem> SelectedItems => _selection;
    internal RectD? MarqueeBounds { get; private set; }
    internal RectD? SelectionBounds
    {
        get
        {
            if (_selection.Count == 0) return null;
            RectD first = _selection[0].Bounds;
            double left = first.Left, top = first.Top, right = first.Right, bottom = first.Bottom;
            for (int i = 1; i < _selection.Count; i++)
            {
                RectD bounds = _selection[i].Bounds;
                left = Math.Min(left, bounds.Left);
                top = Math.Min(top, bounds.Top);
                right = Math.Max(right, bounds.Right);
                bottom = Math.Max(bottom, bounds.Bottom);
            }
            return new RectD(left, top, right - left, bottom - top);
        }
    }

    /// <summary>Current stroke/foreground colour applied to new items.</summary>
    internal ColorRgba StrokeColor { get; set; } = ColorRgba.FromRgb(0xEF, 0x44, 0x44);

    /// <summary>Current stroke thickness applied to new shape/line/pen items.</summary>
    internal double StrokeThickness { get; set; } = 3;
    internal AnnotationStrokeStyle StrokeStyle { get; private set; }
    internal ArrowHeadStyle ArrowHeads { get; private set; }

    internal void ApplyArrowHeads(ArrowHeadStyle style)
    {
        ArrowHeads = Enum.IsDefined(style) ? style : ArrowHeadStyle.End;
        ApplySelectionStyle(UiText.Get("EditorProperty_ArrowHead"), item =>
        {
            if (item is PolylineAnnotation line)
                PushProperty(line, UiText.Get("EditorProperty_ArrowHead"), static (target, heads) =>
                {
                    target.HeadAtStart = heads.Start;
                    target.HeadAtEnd = heads.End;
                }, (Start: line.HeadAtStart, End: line.HeadAtEnd),
                (Start: ArrowHeads == ArrowHeadStyle.Both, End: ArrowHeads != ArrowHeadStyle.None));
        });
    }
    internal double FillTransparency { get; private set; } = 100;
    internal double DefaultFontSize { get; set; } = 18;
    internal string DefaultFontFamily { get; set; } = AnnotationDefaults.DefaultFontFamily;

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
    internal double DipPerPixel { get; set; } = 1;
    internal double HandleTolerance => 8 / Math.Max(0.0001, DipPerPixel);
    internal PointerIntent? ActivePointerIntent => _activePointerIntent;
    internal Func<TextAnnotation, double>? StepFontFitter { get; set; }

    internal void SetSelected(AnnotationItem? item)
    {
        SetSelection(item is null ? [] : [item]);
    }

    private void SetSelection(IEnumerable<AnnotationItem> items)
    {
        AnnotationItem[] selection = items.Where(item => item is not PendingMosaicAnnotation).Distinct().ToArray();
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
        int stepCount = copies.OfType<TextAnnotation>().Count(text => text.IsStepNumber);
        int next = NextStepNumber;
        if (stepCount > 0 && (next < 1 || next > 1000 - stepCount)) return;
        foreach (AnnotationItem copy in copies) copy.Translate(10, 10);
        foreach (TextAnnotation badge in copies.OfType<TextAnnotation>().Where(text => text.IsStepNumber))
        {
            badge.Text = (next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            FitStepFont(badge);
        }
        IUndoableCommand command = new CompositeCommand(UiText.Get("EditorSelection_Duplicate"),
            copies.Select(item => new AddAnnotationCommand(_document, item)));
        _undo.Execute(stepCount > 0 ? new StepSequenceCommand(this, command, _manualNextStep,
            _manualNextStep.HasValue ? next : null) : command);
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
        PointerIntent intent = ResolvePointerIntent(point, shift, control);
        _activePointerIntent = intent;
        switch (_tool)
        {
            case EditorTool.Select:
                BeginSelectGesture(point, shift || control, intent);
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
                if (intent.Action is PointerAction.Move or PointerAction.Resize)
                    BeginSelectGesture(point, false, intent);
                else if (intent.Action == PointerAction.AddStepNumber) TryAddStepNumber(point);
                break;
        }
        if (_gesture == Gesture.None) _activePointerIntent = null;
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
                PointD localPointer = _selected is TextAnnotation { RotationDegrees: not 0 } rotated
                    ? GeometryMath.Rotate(point, _transformBefore.Center, -rotated.RotationDegrees) : point;
                PointD resizePoint = localPointer.Offset(_resizePointerOffset.X, _resizePointerOffset.Y);
                if (_selected is TextAnnotation text)
                {
                    ResizeText(text, resizePoint);
                    if (text.RotationDegrees != 0)
                    {
                        PointD center = GeometryMath.Rotate(text.Rect.Center, _transformBefore.Center, text.RotationDegrees);
                        text.Rect = new RectD(center.X - text.Rect.Width / 2, center.Y - text.Rect.Height / 2,
                            text.Rect.Width, text.Rect.Height);
                    }
                    RaiseVisual();
                }
                else if (_selected is PolylineAnnotation line && _resizePointsBefore is { } original)
                {
                    RectD target = ApplyResize(_transformBefore, _resizeHandle, resizePoint, normalize: false);
                    line.Points = original.Select(p => new PointD(
                        _transformBefore.Width == 0 ? target.X : target.X + (p.X - _transformBefore.X) / _transformBefore.Width * target.Width,
                        _transformBefore.Height == 0 ? target.Y : target.Y + (p.Y - _transformBefore.Y) / _transformBefore.Height * target.Height)).ToList();
                    RaiseVisual();
                }
                else if (_selected is not null)
                {
                    _selected.SetBounds(ApplyResize(_transformBefore, _resizeHandle, resizePoint));
                    RaiseVisual();
                }

                break;
            case Gesture.PenDraw:
                _draftPoints?.Add(point);
                if (_draftItem is PenAnnotation pen)
                {
                    // Appending now raises the points/bounds notifications itself. Guard
                    // the append too, so it is not mistaken for an external draft edit.
                    _updatingDraftPen = true;
                    try
                    {
                        if (_draftPenPoints is { } own && ReferenceEquals(pen.Points, own))
                            own.Add(point);
                        else
                            pen.Points = [.. _draftPoints!];
                        _draftPenPoints = pen.Points;
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
                if (_draftPoints is not { Count: > 0 } points || points[^1] != point) PointerMove(point, shift);
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
                if (text.IsStepNumber) StepSize = text.Rect.Width;

                break;
            case Gesture.Move:
                if (_moveOffset != default)
                    _undo.Push(new TranslateAnnotationsCommand(_gestureSelection, _moveOffset.X, _moveOffset.Y));
                break;
            case Gesture.Resize:
                if (_selected is PolylineAnnotation line && _resizePointsBefore is { } beforePoints)
                {
                    if (!beforePoints.SequenceEqual(line.Points))
                        _undo.Push(new CompositeCommand(UiText.Get("Text_258AD4B095A1"),
                            [new ReplacePointsCommand(line, beforePoints, line.Points)]));
                }
                else if (_selected is not null)
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
                if (_selected is PolylineAnnotation line && _resizePointsBefore is { } points)
                    line.Points = [.. points];
                else _selected?.SetBounds(_transformBefore);
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
        _activePointerIntent = null;
        _resizePointsBefore = null;
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

        _document.Add(text);
        IsCreatingText = true;
        _creatingText = text;
        EditingText = text;
        Tool = EditorTool.Select;
        SetSelected(text);
        return text;
    }

    /// <summary>
    /// Finishes a live text edit. An empty new box discards its draft without changing
    /// history; a new caption's content, layout and add are recorded as one step.
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
            else
            {
                _document.Remove(text);
            }

            SetSelected(null);
        }
        else if (creating)
        {
            text.Text = finalText;
            TextLayout.Apply(text, FitText(text, finalText, text.FontSize, text.AutoSize, text.Rect));
            _undo.Push(new AlreadyAddedCommand(_document, text));
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
    /// <para>
    /// The caption stays inside the image, which clips what it draws. An auto-size box that would
    /// run past the image's right edge becomes a fixed-width box at least ten em wide, capped to
    /// the image width minus the outline margin, so the caption stays readable as it wraps. A box
    /// that still reaches past the right or bottom edge or starts before the left or top
    /// edge moves just enough to lie inside it. The live editing box takes this same box, so what
    /// is typed near an edge or a corner is exactly what the committed caption shows.
    /// </para>
    /// <para>
    /// Typing, committing and changing the size all follow this rule; moving does not refit, so a
    /// caption renders the same wherever it is dragged.
    /// </para>
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
        double canvasWidth = _document.CanvasWidth, canvasHeight = _document.CanvasHeight;
        if (!keepInsideImage || text.IsStepNumber || canvasWidth <= 0 || canvasHeight <= 0)
        {
            return new TextLayout(measured, probe.FontSize, probe.AutoSize);
        }

        // A box that starts outside the image wraps from the edge it moves to.
        double anchor = Math.Clamp(rect.X, 0, canvasWidth);
        if (probe.AutoSize && anchor + measured.Width > canvasWidth + 0.01)
        {
            double minimumWidth = Math.Min(10 * probe.FontSize,
                Math.Max(1, canvasWidth - TextAnnotationLayout.OutlineMargin(probe)));
            probe.AutoSize = false;
            probe.Rect = new RectD(rect.X, rect.Y, Math.Max(canvasWidth - anchor, minimumWidth), rect.Height);
            measured = TextFitter?.Invoke(probe) ?? probe.Rect;
        }

        double left = Math.Max(0, Math.Min(measured.X, canvasWidth - measured.Width));
        double top = Math.Max(0, Math.Min(measured.Y, canvasHeight - measured.Height));
        return new TextLayout(measured with { X = left, Y = top }, probe.FontSize, probe.AutoSize);
    }

    /// <param name="opacity">
    /// The image's opacity (0..1) from the start, so the add and its look are one undo step. The
    /// annotation's own <see cref="AnnotationItem.Opacity"/> is used: it is already saved with the
    /// layer and drawn by the renderer, so no format change is involved.
    /// </param>
    internal ImageAnnotation AddImageAnnotation(string assetFileName, int sourceWidth, int sourceHeight, RectD rect, double opacity = 1.0)
    {
        var image = new ImageAnnotation
        {
            AssetFileName = assetFileName,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            Rect = rect.Normalized(),
            Opacity = opacity,
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
    /// Applies a font size to all selected captions, or records it as the default
    /// for the next text annotation when nothing is selected. The box is refitted to the
    /// text at the new size, so a larger font is never trimmed by its own rect.
    /// </summary>
    internal void ApplyFontSize(double fontSize)
    {
        double size = double.IsFinite(fontSize)
            ? Math.Clamp(fontSize, SettingsRanges.FontSize.Min, SettingsRanges.FontSize.Max) : DefaultFontSize;
        if (_selection.Count == 1 && _selected is TextAnnotation { IsStepNumber: true } badge)
        {
            ApplyStepSize(Math.Clamp(badge.Rect.Width * size / Math.Max(1, badge.FontSize), 24, Math.Max(24, MaximumStepSize)));
            return;
        }
        DefaultFontSize = size;
        ApplySelectionStyle(UiText.Get("Text_258AD4B095A1"), item =>
        {
            if (item is not TextAnnotation { IsStepNumber: false } text || Math.Abs(text.FontSize - size) < 1e-6) return;
            TextLayout after = FitText(text, text.Text, size, text.AutoSize, text.Rect);
            PushProperty(text, UiText.Get("Text_258AD4B095A1"), TextLayout.Apply, TextLayout.Of(text), after);
        });
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
        return PointerIntentGeometry.NearestHandle(GetSelectionHandles(DipPerPixel), point, handlePixels);
    }

    internal IReadOnlyList<SelectionHandle> GetSelectionHandles(double dipPerPixel) =>
        _selection.Count == 1 && _selected is { SupportsResize: true }
        && (_tool == EditorTool.Select || (_tool == EditorTool.StepNumber && _selected is TextAnnotation { IsStepNumber: true }))
            ? PointerIntentGeometry.SelectionHandles(_selected, dipPerPixel) : [];

    internal PointerIntent ResolvePointerIntent(PointD point, bool shift = false, bool control = false)
    {
        if (_activePointerIntent is { } active) return active;
        bool step = _tool == EditorTool.StepNumber;
        bool editingStep = step && _selection.Count == 1 && _selected is TextAnnotation { IsStepNumber: true };
        bool toggle = _tool == EditorTool.Select && (shift || control);
        if (_tool == EditorTool.Select || editingStep)
        {
            ResizeHandle handle = toggle ? ResizeHandle.None : HandleAt(point, HandleTolerance);
            if (handle != ResizeHandle.None)
                return new(PointerAction.Resize, _selected, handle,
                    PointerIntentGeometry.ResizeCursor(_selected!, handle));
            AnnotationItem? hit = step
                ? (_selected!.DistanceTo(point) <= HitTolerance ? _selected : null) : HitTest(point);
            if (hit is not null)
                return new(toggle ? PointerAction.ToggleSelection : PointerAction.Move, hit, Cursor: EditorCursorKind.Move);
            if (!step) return new(PointerAction.Marquee);
        }
        EditorCursorKind cursor = EditorCursorPolicy.Resolve(_tool, default);
        return new(_tool switch
        {
            EditorTool.StepNumber => CanAddStepNumber ? PointerAction.AddStepNumber : PointerAction.None,
            EditorTool.Pen or EditorTool.Highlighter => PointerAction.PenDraw,
            EditorTool.Text => PointerAction.Text,
            EditorTool.Image => PointerAction.Image,
            _ => PointerAction.Create,
        }, Cursor: cursor);
    }

    /// <summary>
    /// <paramref name="handle"/>, or <see cref="ResizeHandle.None"/> when it does nothing for
    /// <paramref name="item"/>: a caption's height always follows its text, so its top and
    /// bottom handles are not offered.
    /// </summary>
    internal static ResizeHandle UsableHandle(AnnotationItem item, ResizeHandle handle) =>
        (item is TextAnnotation { IsStepNumber: false } && handle is ResizeHandle.TopCenter or ResizeHandle.BottomCenter)
        || (item is TextAnnotation { IsStepNumber: true } && !IsCornerHandle(handle))
            ? ResizeHandle.None
            : handle;

    internal AnnotationItem? HitTest(PointD point) => _document.HitTest(point, HitTolerance);

    private void BeginSelectGesture(PointD point, bool toggle, PointerIntent intent)
    {
        _gestureStart = point;
        if (intent.Action == PointerAction.Resize && _selected is not null)
        {
            ResizeHandle handle = intent.Handle;
            if (handle != ResizeHandle.None)
            {
                _gesture = Gesture.Resize;
                _resizeHandle = handle;
                _transformBefore = _selected.Bounds;
                _textLayoutBefore = _selected is TextAnnotation text ? TextLayout.Of(text) : default;
                _resizePointsBefore = _selected is PolylineAnnotation line ? [.. line.Points] : null;
                PointD local = _selected is TextAnnotation { RotationDegrees: not 0 } rotated
                    ? GeometryMath.Rotate(point, _transformBefore.Center, -rotated.RotationDegrees) : point;
                PointD anchor = PointerIntentGeometry.HandlePoint(_transformBefore, handle);
                _resizePointerOffset = new PointD(anchor.X - local.X, anchor.Y - local.Y);
                return;
            }
        }

        AnnotationItem? hit = intent.Target;
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
            EditorTool.Arrow => new PolylineAnnotation
            {
                Points = [point, point], Stroke = StrokeColor, StrokeThickness = StrokeThickness,
                HeadAtStart = ArrowHeads == ArrowHeadStyle.Both, HeadAtEnd = ArrowHeads != ArrowHeadStyle.None,
            },
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
        if (draft is PolylineAnnotation) UserArrowAdded?.Invoke();
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

    private long _nextStepRevision = -1;
    private int _nextStepNumber;
    private string? _nextStepPositiveSign;
    private string? _nextStepNegativeSign;
    private int? _manualNextStep;
    internal double StepSize { get; private set; } = 36;
    internal double MaximumStepSize => Math.Min(256, Math.Min(_document.CanvasWidth, _document.CanvasHeight));
    internal bool CanFitStepNumber => MaximumStepSize >= 24;
    internal bool CanAddStepNumber => CanFitStepNumber && NextStepNumber is >= 1 and <= 999;

    /// <summary>
    /// The number the step tool adds next: one more than the highest number on the image, so
    /// deleting or undoing the last badge gives its number back.
    /// </summary>
    internal int NextStepNumber
    {
        get
        {
            if (_manualNextStep is int manual) return manual;
            // Preview and cursor both ask for this value. Selection/style changes do not
            // change the sequence. Keep the original culture-sensitive integer parsing.
            var format = System.Globalization.CultureInfo.CurrentCulture.NumberFormat;
            if (_nextStepRevision != _document.StepNumberRevision
                || _nextStepPositiveSign != format.PositiveSign || _nextStepNegativeSign != format.NegativeSign)
            {
                int largest = _document.Items.OfType<TextAnnotation>().Where(t => t.IsStepNumber)
                    .Select(t => int.TryParse(t.Text, out int n) ? n : 0).DefaultIfEmpty(0).Max();
                _nextStepNumber = largest == int.MaxValue ? int.MaxValue : largest + 1;
                _nextStepRevision = _document.StepNumberRevision;
                _nextStepPositiveSign = format.PositiveSign;
                _nextStepNegativeSign = format.NegativeSign;
            }
            return _nextStepNumber;
        }
    }

    internal bool TrySetNextStepNumber(int number)
    {
        if (number is < 1 or > 999) return false;
        _manualNextStep = number;
        RaiseVisual();
        return true;
    }

    internal RectD? StepNumberPreviewBounds(PointD point)
    {
        if (!CanAddStepNumber) return null;
        double size = Math.Clamp(StepSize, 24, MaximumStepSize);
        return new RectD(point.X - size / 2, point.Y - size / 2, size, size)
            .ClampTo(new RectD(0, 0, _document.CanvasWidth, _document.CanvasHeight));
    }

    internal TextAnnotation AddStepNumber(PointD point) => TryAddStepNumber(point)
        ?? throw new InvalidOperationException("The step number cannot be placed on this image.");

    internal TextAnnotation? TryAddStepNumber(PointD point)
    {
        if (StepNumberPreviewBounds(point) is not { } bounds) return null;
        int number = NextStepNumber;
        var badge = new TextAnnotation { IsStepNumber = true, Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Rect = bounds, FontFamily = DefaultFontFamily,
            Background = StrokeColor, Foreground = ColorRgba.White, Bold = true, HasOutline = true };
        FitStepFont(badge);
        _undo.Execute(new StepSequenceCommand(this, new AddAnnotationCommand(_document, badge), _manualNextStep,
            _manualNextStep.HasValue ? number + 1 : null));
        SetSelected(badge);
        RaiseVisual();
        return badge;
    }

    internal bool ApplySelectedStepNumber(int number)
    {
        if (number is < 1 or > 999 || _selection.Count != 1 || _selected is not TextAnnotation { IsStepNumber: true } badge) return false;
        string value = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (badge.Text == value) return true;
        var probe = (TextAnnotation)badge.Clone();
        probe.Text = value;
        FitStepFont(probe);
        _undo.Execute(new CompositeCommand(UiText.Get("EditorStep_Number"),
        [
            new PropertyChangeCommand<TextAnnotation, string>(badge, UiText.Get("EditorStep_Number"),
                static (text, v) => text.Text = v, badge.Text, value),
            new PropertyChangeCommand<TextAnnotation, double>(badge, UiText.Get("EditorStep_Number"),
                static (text, v) => text.FontSize = v, badge.FontSize, probe.FontSize),
        ]));
        RaiseVisual();
        return true;
    }

    internal bool ApplyStepSize(double size)
    {
        if (_selection.Count > 1 || !CanFitStepNumber || !double.IsFinite(size) || size < 24 || size > MaximumStepSize) return false;
        StepSize = size;
        if (_selected is TextAnnotation { IsStepNumber: true } badge)
        {
            TextLayout before = TextLayout.Of(badge);
            RectD box = new(badge.Rect.Center.X - size / 2, badge.Rect.Center.Y - size / 2, size, size);
            box = box.ClampTo(new RectD(0, 0, _document.CanvasWidth, _document.CanvasHeight));
            TextLayout after = StepLayout(badge, box);
            if (before != after)
                _undo.Execute(new PropertyChangeCommand<TextAnnotation, TextLayout>(badge,
                    UiText.Get("EditorStep_Size"), TextLayout.Apply, before, after));
        }
        RaiseVisual();
        return true;
    }

    private TextAnnotation[] OrderedStepNumbers() => _document.Items.OfType<TextAnnotation>()
        .Where(t => t.IsStepNumber && int.TryParse(t.Text, out _))
        .OrderBy(t => int.Parse(t.Text, System.Globalization.CultureInfo.CurrentCulture)).ThenBy(t => t.Id).ToArray();

    internal bool CanRenumberStepNumbers => OrderedStepNumbers().Length is > 0 and <= 999;

    internal bool RenumberStepNumbers()
    {
        TextAnnotation[] badges = OrderedStepNumbers();
        if (badges.Length is 0 or > 999) return false;
        var commands = new List<IUndoableCommand>();
        for (int index = 0; index < badges.Length; index++)
        {
            TextAnnotation badge = badges[index];
            string number = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (badge.Text == number) continue;
            var probe = (TextAnnotation)badge.Clone();
            probe.Text = number;
            FitStepFont(probe);
            commands.Add(new PropertyChangeCommand<TextAnnotation, string>(badge, UiText.Get("EditorStep_Number"),
                static (text, v) => text.Text = v, badge.Text, number));
            commands.Add(new PropertyChangeCommand<TextAnnotation, double>(badge, UiText.Get("EditorStep_Number"),
                static (text, v) => text.FontSize = v, badge.FontSize, probe.FontSize));
        }
        if (commands.Count > 0) _undo.Execute(new CompositeCommand(UiText.Get("EditorStep_Renumber"), commands));
        RaiseVisual();
        return true;
    }

    private void FitStepFont(TextAnnotation badge)
    {
        double size = Math.Min(badge.Rect.Width, badge.Rect.Height);
        badge.FontSize = Math.Max(10, Math.Min(size / 2, (size - 4) / Math.Max(0.65, badge.Text.Length * 0.65)));
        if (StepFontFitter is not null) badge.FontSize = Math.Max(10, StepFontFitter(badge));
    }

    private TextLayout StepLayout(TextAnnotation badge, RectD bounds)
    {
        var probe = (TextAnnotation)badge.Clone();
        probe.Rect = bounds;
        FitStepFont(probe);
        return new(bounds, probe.FontSize, badge.AutoSize);
    }

    private sealed class StepSequenceCommand(AnnotationEditorController owner, IUndoableCommand inner,
        int? before, int? after) : IUndoableCommand
    {
        public string Description => inner.Description;
        public IEnumerable<string> ReferencedImageAssets => inner.ReferencedImageAssets;
        public void Execute() { inner.Execute(); owner._manualNextStep = after; }
        public void Undo() { inner.Undo(); owner._manualNextStep = before; }
    }

    private static bool IsDegenerate(AnnotationItem item)
    {
        RectD b = item.Bounds;
        return b.Width < 2 && b.Height < 2;
    }

    internal void ApplyTextBold(bool bold)
    {
        string label = UiText.Get("EditorText_Bold");
        ApplySelectionStyle(label, item =>
        {
            if (item is not TextAnnotation { IsStepNumber: false } text || text.Bold == bold) return;
            var probe = (TextAnnotation)text.Clone();
            probe.Bold = bold;
            TextLayout fitted = FitText(probe, text.Text, text.FontSize, text.AutoSize, text.Rect);
            PushProperty(text, label, static (t, v) =>
            {
                t.Bold = v.Bold;
                TextLayout.Apply(t, v.Layout);
            }, (Bold: text.Bold, Layout: TextLayout.Of(text)), (Bold: bold, Layout: fitted));
        });
    }

    internal void ApplyTextBackground(bool enabled)
    {
        string label = UiText.Get("EditorText_Background");
        ApplySelectionStyle(label, item =>
        {
            if (item is not TextAnnotation { IsStepNumber: false } text || (text.Background.A > 0) == enabled) return;
            ColorRgba background = enabled
                ? (text.Background == ColorRgba.Transparent ? ColorRgba.Black : text.Background.WithAlpha(255))
                : text.Background.WithAlpha(0);
            PushProperty(text, label, static (t, v) => t.Background = v, text.Background, background);
        });
    }

    internal void ApplyTextFontFamily(string family)
    {
        if (!_selection.OfType<TextAnnotation>().Any(text => !text.IsStepNumber))
        {
            DefaultFontFamily = family;
            return;
        }
        string label = UiText.Get("EditorText_FontFamily");
        ApplySelectionStyle(label, item =>
        {
            if (item is not TextAnnotation { IsStepNumber: false } text
                || string.Equals(text.FontFamily, family, StringComparison.Ordinal)) return;
            var probe = (TextAnnotation)text.Clone();
            probe.FontFamily = family;
            TextLayout fitted = FitText(probe, text.Text, text.FontSize, text.AutoSize, text.Rect);
            PushProperty(text, label, static (t, v) =>
            {
                t.FontFamily = v.Family;
                TextLayout.Apply(t, v.Layout);
            }, (Family: text.FontFamily, Layout: TextLayout.Of(text)), (Family: family, Layout: fitted));
        });
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
            // The draft's final style becomes part of the single add when committed.
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
            if (!CanFitStepNumber) return;
            bool left = _resizeHandle is ResizeHandle.TopLeft or ResizeHandle.BottomLeft;
            bool top = _resizeHandle is ResizeHandle.TopLeft or ResizeHandle.TopRight;
            double anchorX = left ? box.Right : box.Left, anchorY = top ? box.Bottom : box.Top;
            double dx = left ? anchorX - point.X : point.X - anchorX;
            double dy = top ? anchorY - point.Y : point.Y - anchorY;
            // Orthogonally project onto the diagonal, retaining the opposite corner.
            double size = Math.Clamp((dx + dy) / 2, 24, MaximumStepSize);
            RectD resized = new(left ? anchorX - size : anchorX, top ? anchorY - size : anchorY, size, size);
            TextLayout.Apply(text, StepLayout(text, resized));
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

    private static RectD ApplyResize(RectD initial, ResizeHandle handle, PointD point, bool normalize = true)
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

        RectD result = new(left, top, right - left, bottom - top);
        return normalize ? result.Normalized() : result;
    }

    private static bool IsCornerHandle(ResizeHandle handle) =>
        handle is ResizeHandle.TopLeft or ResizeHandle.TopRight
            or ResizeHandle.BottomLeft or ResizeHandle.BottomRight;

    /// <summary>
    /// Which resize handle, if any, sits under <paramref name="point"/>.
    /// </summary>
    internal static ResizeHandle HitTestHandle(RectD bounds, PointD point, double tolerance)
    {
        return PointerIntentGeometry.NearestHandle(Enum.GetValues<ResizeHandle>()
            .Where(h => h != ResizeHandle.None).Select(h => new SelectionHandle(h,
                PointerIntentGeometry.HandlePoint(bounds.Normalized(), h))), point, tolerance);
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
