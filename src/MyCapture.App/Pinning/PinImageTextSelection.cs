using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Localization;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

/// <summary>A reading-order range over source pixels. No bitmap or recognizer is retained.</summary>
internal sealed class PinImageTextSelection
{
    internal readonly record struct Word(string Text, Rect Bounds, int Line, int Start, int End);
    private readonly string[] _lines;
    private readonly string _allText;
    private readonly string _lineBreak;
    internal Word[] Words { get; }
    internal int Anchor { get; private set; } = -1;
    internal int Active { get; private set; } = -1;
    internal bool HasSelection => Anchor >= 0 && Active >= 0;

    internal PinImageTextSelection(OcrResult result)
    {
        _allText = result.Text;
        int lineBreak = _allText.IndexOfAny(['\r', '\n']);
        _lineBreak = lineBreak < 0 ? Environment.NewLine
            : _allText[lineBreak] == '\r' && lineBreak + 1 < _allText.Length && _allText[lineBreak + 1] == '\n'
                ? "\r\n" : _allText[lineBreak].ToString();
        _lines = new string[result.Lines.Count];
        var words = new List<Word>();
        for (int row = 0; row < result.Lines.Count; row++)
        {
            OcrLine line = result.Lines[row];
            string text = line.Text;
            int cursor = 0;
            // Use the recognizer's separators (including wide gaps and punctuation), not
            // geometry-derived spaces. Some engines return a line as one word.
            var sourceWords = line.Words.Count > 0 ? line.Words : [new OcrWord(line.Text, line.Bounds)];
            foreach (OcrWord word in sourceWords)
            {
                if (string.IsNullOrEmpty(word.Text)) continue;
                int start = text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
                if (start < 0)
                {
                    // A corrected line can differ from its original word strings. Its line
                    // box remains honest; do not assign corrected characters to guessed boxes.
                    words.RemoveAll(item => item.Line == row);
                    Add(text, line.Bounds, 0, text.Length);
                    break;
                }
                Add(word.Text, word.Bounds, start, start + word.Text.Length);
                cursor = start + word.Text.Length;
            }
            _lines[row] = text;

            void Add(string value, OcrRect bounds, int start, int end)
            {
                if (string.IsNullOrEmpty(value) || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
                    || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
                    || bounds.Width <= 0 || bounds.Height <= 0) return;
                words.Add(new Word(value, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), row, start, end));
            }
        }
        Words = words.ToArray();
    }

    internal string AllText => _allText;
    internal string? SelectedText
    {
        get
        {
            if (!HasSelection) return null;
            int first = Math.Min(Anchor, Active), last = Math.Max(Anchor, Active);
            if (first == 0 && last == Words.Length - 1) return _allText;
            var text = new StringBuilder();
            for (int index = first; index <= last;)
            {
                Word start = Words[index], end = start;
                while (++index <= last && Words[index].Line == start.Line) end = Words[index];
                if (text.Length > 0) text.Append(_lineBreak);
                text.Append(_lines[start.Line].AsSpan(start.Start, end.End - start.Start));
            }
            return text.ToString();
        }
    }

    /// <summary>Only painting joins nearby words. Text, hit tests and automation keep their word boxes.</summary>
    internal static IReadOnlyList<Rect> HighlightRuns(IReadOnlyList<Word> words, int anchor, int active)
    {
        if (words.Count == 0 || anchor < 0 || active < 0) return [];
        int first = Math.Min(anchor, active), last = Math.Max(anchor, active);
        var runs = new List<Rect>();
        Word previous = words[first];
        Rect run = previous.Bounds;
        for (int index = first + 1; index <= last; index++)
        {
            Word word = words[index];
            double gap = word.Bounds.Left - previous.Bounds.Right;
            if (word.Line == previous.Line && gap <= Math.Max(previous.Bounds.Height, word.Bounds.Height))
                run.Union(word.Bounds);
            else
            {
                runs.Add(run);
                run = word.Bounds;
            }
            previous = word;
        }
        runs.Add(run);
        return runs;
    }

    internal void Select(int anchor, int active)
    {
        if (Words.Length == 0) return;
        Anchor = Math.Clamp(anchor, 0, Words.Length - 1);
        Active = Math.Clamp(active, 0, Words.Length - 1);
    }

    internal void Clear() => Anchor = Active = -1;
    internal void SelectAll() => Select(0, Words.Length - 1);
    internal void SelectLine(int index)
    {
        if (index < 0 || index >= Words.Length) return;
        int start = index, end = index;
        while (start > 0 && Words[start - 1].Line == Words[index].Line) start--;
        while (end + 1 < Words.Length && Words[end + 1].Line == Words[index].Line) end++;
        Select(start, end);
    }

    internal int HitTest(Point point, bool nearest = false)
    {
        int best = -1;
        double distance = double.PositiveInfinity;
        for (int index = 0; index < Words.Length; index++)
        {
            Rect box = Words[index].Bounds;
            if (box.Contains(point)) return index;
            if (!nearest) continue;
            double x = Math.Max(box.Left - point.X, Math.Max(0, point.X - box.Right));
            double y = Math.Max(box.Top - point.Y, Math.Max(0, point.Y - box.Bottom));
            double squared = x * x + y * y;
            if (squared < distance) { distance = squared; best = index; }
        }
        return best;
    }

    internal void Move(Key key, bool extend)
    {
        if (Words.Length == 0) return;
        int next = HasSelection ? Active : 0;
        if (HasSelection)
        {
            if (key == Key.Left) next--;
            else if (key == Key.Right) next++;
            else if (key is Key.Up or Key.Down)
            {
                int line = Words[Active].Line + (key == Key.Up ? -1 : 1);
                double x = Words[Active].Bounds.Left + Words[Active].Bounds.Width / 2;
                double nearest = double.PositiveInfinity;
                for (int index = 0; index < Words.Length; index++)
                {
                    if (Words[index].Line != line) continue;
                    double distance = Math.Abs(Words[index].Bounds.Left + Words[index].Bounds.Width / 2 - x);
                    if (distance < nearest) { nearest = distance; next = index; }
                }
            }
        }
        Select(extend && HasSelection ? Anchor : next, next);
    }
}

/// <summary>Transparent interaction layer; actual arranged size is the image's Stretch.Fill size.</summary>
internal sealed class PinImageTextSelectionView : FrameworkElement
{
    private readonly Size _pixels;
    private bool _dragging;
    private Point _pressPoint;
    private int _pendingAnchor = -1;
    internal PinImageTextSelection Selection { get; }
    private static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        "SelectionBrush", typeof(Brush), typeof(PinImageTextSelectionView),
        new FrameworkPropertyMetadata(SystemColors.HighlightBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    internal PinImageTextSelectionView(OcrResult result, int pixelWidth, int pixelHeight)
    {
        Selection = new PinImageTextSelection(result);
        _pixels = new Size(pixelWidth, pixelHeight);
        Focusable = true;
        Cursor = Cursors.IBeam;
        SetResourceReference(SelectionBrushProperty, SystemColors.HighlightBrushKey);
        AutomationProperties.SetName(this, UiText.Get("Pin_SelectText"));
        Unloaded += (_, _) => StopDrag();
        LostMouseCapture += (_, _) => EndDrag();
    }

    internal Point ToPixels(Point point) => new(point.X * _pixels.Width / Math.Max(1, ActualWidth),
        point.Y * _pixels.Height / Math.Max(1, ActualHeight));

    internal Rect ToView(Rect rect) => new(rect.X * ActualWidth / _pixels.Width,
        rect.Y * ActualHeight / _pixels.Height, rect.Width * ActualWidth / _pixels.Width,
        rect.Height * ActualHeight / _pixels.Height);

    internal bool SelectAt(Point point, bool extend = false, int clicks = 1)
    {
        int index = Selection.HitTest(ToPixels(point), nearest: extend);
        if (index < 0) { Selection.Clear(); Changed(); return false; }
        if (clicks >= 3) Selection.SelectLine(index);
        else Selection.Select(extend && Selection.HasSelection ? Selection.Anchor : index, index);
        Changed();
        return true;
    }

    // These gestures operate in view DIPs, including the system drag threshold. Capture
    // belongs to the mouse handlers, so selection can also be exercised without a desktop.
    internal bool BeginDrag(Point point, bool extend = false, int clicks = 1)
    {
        _pressPoint = point;
        _pendingAnchor = -1;
        _dragging = Selection.Words.Length > 0;
        if (!SelectAt(point, extend, clicks))
            _pendingAnchor = Selection.HitTest(ToPixels(point), nearest: true);
        return _dragging;
    }

    internal void ContinueDrag(Point point)
    {
        if (!_dragging) return;
        if (_pendingAnchor >= 0)
        {
            if (Math.Abs(point.X - _pressPoint.X) <= SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _pressPoint.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
            Selection.Select(_pendingAnchor, Selection.HitTest(ToPixels(point), nearest: true));
            _pendingAnchor = -1;
            Changed();
        }
        else SelectAt(point, extend: true);
    }

    internal void EndDrag()
    {
        _dragging = false;
        _pendingAnchor = -1;
    }

    internal void SelectAll() { Selection.SelectAll(); Changed(); }
    internal void Move(Key key, bool extend) { Selection.Move(key, extend); Changed(); }
    internal void Changed()
    {
        InvalidateVisual();
        if (UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.SelectionPatternOnInvalidated);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (!Selection.HasSelection) return;
        // The system highlight colour and square boxes match text selection. Alpha keeps
        // the original image glyphs readable; no raster or translated pixels are replaced.
        dc.PushOpacity(.4);
        foreach (Rect run in PinImageTextSelection.HighlightRuns(Selection.Words, Selection.Anchor, Selection.Active))
            dc.DrawRectangle((Brush)GetValue(SelectionBrushProperty), null, ToView(run));
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (BeginDrag(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), e.ClickCount)
            && !CaptureMouse()) EndDrag();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        ContinueDrag(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        StopDrag();
        e.Handled = true;
    }

    private void StopDrag()
    {
        EndDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SelectionPeer(this);

    private sealed class SelectionPeer(PinImageTextSelectionView view) : FrameworkElementAutomationPeer(view), ISelectionProvider
    {
        protected override string GetClassNameCore() => nameof(PinImageTextSelectionView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);
        protected override List<AutomationPeer> GetChildrenCore() =>
            Enumerable.Range(0, view.Selection.Words.Length).Select(index => (AutomationPeer)new WordPeer(view, this, index)).ToList();
        public bool CanSelectMultiple => true;
        public bool IsSelectionRequired => false;
        public IRawElementProviderSimple[] GetSelection() => !view.Selection.HasSelection ? [] :
            Enumerable.Range(Math.Min(view.Selection.Anchor, view.Selection.Active), Math.Abs(view.Selection.Active - view.Selection.Anchor) + 1)
                .Select(index => ProviderFromPeer(new WordPeer(view, this, index))).ToArray();
    }

    private sealed class WordPeer(PinImageTextSelectionView view, SelectionPeer parent, int index) : AutomationPeer, ISelectionItemProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.SelectionItem ? this : null;
        protected override string GetNameCore() => view.Selection.Words[index].Text;
        protected override string GetClassNameCore() => "RecognizedWord";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override Rect GetBoundingRectangleCore()
        {
            Rect box = view.ToView(view.Selection.Words[index].Bounds);
            return PresentationSource.FromVisual(view) is null ? Rect.Empty : new Rect(view.PointToScreen(box.TopLeft), view.PointToScreen(box.BottomRight));
        }
        protected override Point GetClickablePointCore() => GetBoundingRectangleCore().TopLeft;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override bool IsOffscreenCore() => !view.IsVisible;
        protected override bool IsEnabledCore() => view.IsEnabled;
        protected override bool IsKeyboardFocusableCore() => true;
        protected override bool HasKeyboardFocusCore() => view.IsKeyboardFocused && view.Selection.Active == index;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsPasswordCore() => false;
        protected override bool IsRequiredForFormCore() => false;
        protected override string GetAcceleratorKeyCore() => string.Empty;
        protected override string GetAccessKeyCore() => string.Empty;
        protected override string GetAutomationIdCore() => $"Word{index}";
        protected override string GetHelpTextCore() => string.Empty;
        protected override string GetItemStatusCore() => string.Empty;
        protected override string GetItemTypeCore() => string.Empty;
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
        protected override void SetFocusCore() { view.Focus(); Select(); }
        public bool IsSelected => view.Selection.HasSelection && index >= Math.Min(view.Selection.Anchor, view.Selection.Active)
            && index <= Math.Max(view.Selection.Anchor, view.Selection.Active);
        public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(parent);
        public void Select() { view.Selection.Select(index, index); view.Changed(); }
        public void AddToSelection() { view.Selection.Select(view.Selection.HasSelection ? view.Selection.Anchor : index, index); view.Changed(); }
        public void RemoveFromSelection() { if (IsSelected) { view.Selection.Clear(); view.Changed(); } }
    }
}
