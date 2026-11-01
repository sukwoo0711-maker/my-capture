using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

internal sealed partial class OcrResultWindow
{
    private readonly Dictionary<(int Row, int Column), string> _tableEdits = [];
    private readonly Dictionary<OcrExtractionMode, string> _textEdits = [];
    private WrapPanel _editIndicator = null!;
    private TextBlock _editCount = null!;
    private bool _settingEditorText;
    private bool _editingCell;
    private bool _acceptedJoinLines;
    private TextBox? _cellEditor;
    private int? _hoverRow;
    private int[] _codeRegionForLine = [];
    internal long EditRevision { get; private set; }

    private void AddEditIndicator(Panel options)
    {
        _editIndicator = new WrapPanel { Visibility = Visibility.Collapsed };
        _editCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _editCount.SetResourceReference(TextBlock.ForegroundProperty, "State.Success");
        _editIndicator.Children.Add(_editCount);
        var revert = MakeButton(UiText.Get("Ocr_RevertEdits"), UiText.Get("Ocr_RevertEdits"));
        revert.SetResourceReference(StyleProperty, "Button.Ghost");
        revert.Click += (_, _) => RevertCurrentEdits();
        _editIndicator.Children.Add(revert);
        options.Children.Add(_editIndicator);
    }

    private void InitializeEditing()
    {
        _textBox.TextChanged += (_, _) =>
        {
            if (_settingEditorText || _updatingAdvanced || _currentExtraction is null
                || ExtractionMode is not (OcrExtractionMode.Text or OcrExtractionMode.Code)) return;
            string text = _textBox.Text;
            if (text == _generatedText) _textEdits.Remove(ExtractionMode);
            else _textEdits[ExtractionMode] = text;
            EditRevision++;
            _copyButton.IsEnabled = _plainExportButton.IsEnabled = text.Length > 0;
            UpdateEditIndicator();
            UpdatePinButtons();
            UpdateSourceHighlight();
        };
        _textBox.SelectionChanged += (_, _) =>
        {
            UpdateInputMethod();
            if (!_settingEditorText) UpdateSourceHighlight();
        };
        _tableGrid.BeginningEdit += (_, _) => _editingCell = true;
        _tableGrid.PreparingCellForEdit += (_, e) =>
        {
            _cellEditor = e.EditingElement as TextBox;
            if (_cellEditor is not null) _cellEditor.TextChanged += OnCellDraftChanged;
        };
        _tableGrid.CellEditEnding += (_, e) =>
        {
            if (_cellEditor is not null) _cellEditor.TextChanged -= OnCellDraftChanged;
            _cellEditor = null;
            _editingCell = false;
            if (e.EditAction == DataGridEditAction.Commit && e.Row.Item is EditableRow row
                && e.EditingElement is TextBox editor)
                EditTableCell(row.RawIndex, _tableGrid.Columns.IndexOf(e.Column), editor.Text);
        };
        _tableGrid.CurrentCellChanged += (_, _) => UpdateSourceHighlight();
        _tableGrid.MouseMove += (_, e) =>
        {
            DependencyObject? current = e.OriginalSource as DependencyObject;
            while (current is not null && current is not DataGridRow)
                current = current switch
                {
                    Visual visual => VisualTreeHelper.GetParent(visual),
                    FrameworkContentElement element => element.Parent,
                    _ => null,
                };
            SetHoveredTableRow((current as DataGridRow)?.Item is EditableRow row ? row.RawIndex : null);
        };
        _tableGrid.MouseLeave += (_, _) => SetHoveredTableRow(null);
    }

    private void OnCellDraftChanged(object sender, TextChangedEventArgs e) => EditRevision++;

    // Words, lines and paragraphs can be retyped in Korean. A larger selection, such as the
    // select-all a result opens with, turns the IME off until the selection shrinks again.
    internal const int InputMethodSelectionLimit = 1024;

    private void UpdateInputMethod() =>
        InputMethod.SetIsInputMethodEnabled(_textBox, _textBox.SelectionLength <= InputMethodSelectionLimit);

    private void SelectAllText()
    {
        // Turn the IME off before TSF can see a select-all above the limit.
        if (_textBox.Text.Length > InputMethodSelectionLimit) InputMethod.SetIsInputMethodEnabled(_textBox, false);
        _ = _textBox.Focus();
        _textBox.SelectAll();
    }

    private void CommitTableEdit()
    {
        if (!_editingCell) return;
        _tableGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        _tableGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    internal void EditTableCell(int row, int column, string text)
    {
        OcrTable? original = _currentExtraction?.Table;
        if (original is null || row < 0 || row >= original.Rows.Count
            || column < 0 || column >= original.Rows[row].Cells.Count) return;
        if (original.Rows[row].Cells[column].Text == text) _tableEdits.Remove((row, column));
        else _tableEdits[(row, column)] = text;
        if (_tableGrid.Items.OfType<EditableRow>().FirstOrDefault(item => item.RawIndex == row) is { } displayed)
        {
            displayed.Cells[column].Text = text;
            displayed.Cells[column].IsEdited = _tableEdits.ContainsKey((row, column));
        }
        if (_firstRowHeader.IsChecked == true && row == 0 && column < _tableGrid.Columns.Count)
            _tableGrid.Columns[column].Header = text;
        if (ExtractionMode == OcrExtractionMode.Table) SetEditorText(OcrTableExport.Tsv(CurrentTable()));
        EditRevision++;
        UpdateEditIndicator();
        UpdatePinButtons();
    }

    private void SetEditorText(string text)
    {
        _settingEditorText = true;
        try { _textBox.Text = text; }
        finally { _settingEditorText = false; }
    }

    private void ResetEdits()
    {
        _tableGrid?.CancelEdit(DataGridEditingUnit.Cell);
        _tableGrid?.CancelEdit(DataGridEditingUnit.Row);
        _editingCell = false;
        _tableEdits.Clear();
        _textEdits.Clear();
        _hoverRow = null;
        _codeRegionForLine = [];
        EditRevision++;
    }

    internal void RevertCurrentEdits()
    {
        if (ExtractionMode == OcrExtractionMode.Table)
        {
            _tableGrid.CancelEdit(DataGridEditingUnit.Cell);
            _tableGrid.CancelEdit(DataGridEditingUnit.Row);
            _tableEdits.Clear();
        }
        else _textEdits.Remove(ExtractionMode);
        EditRevision++;
        RefreshAdvancedView();
    }

    private void UpdateEditIndicator()
    {
        bool table = ExtractionMode == OcrExtractionMode.Table;
        bool edited = table ? _tableEdits.Count > 0 : _textEdits.ContainsKey(ExtractionMode);
        _editIndicator.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;
        _editCount.Text = table ? UiText.Format("Ocr_EditedCells", _tableEdits.Count) : UiText.Get("Ocr_Edited");
        AutomationProperties.SetName(_editCount, _editCount.Text);
    }

    private void ChangeJoinLines()
    {
        if (_updatingAdvanced) return;
        if (_textEdits.ContainsKey(OcrExtractionMode.Text) && !ConfirmReplaceEditedText())
        {
            _updatingAdvanced = true;
            _joinLines.IsChecked = _acceptedJoinLines;
            _updatingAdvanced = false;
            return;
        }
        _acceptedJoinLines = _joinLines.IsChecked == true;
        _textEdits.Remove(OcrExtractionMode.Text);
        EditRevision++;
        RefreshAdvancedView();
    }

    internal void SetHoveredTableRow(int? rawIndex)
    {
        if (_hoverRow == rawIndex) return;
        _hoverRow = rawIndex;
        UpdateSourceHighlight();
    }

    private void UpdateSourceHighlight()
    {
        if (_imageOverlay is null || _currentExtraction is null || ExtractionMode == OcrExtractionMode.Translation) return;
        IEnumerable<OcrRect> regions = [];
        if (ExtractionMode == OcrExtractionMode.Table)
        {
            int? row = _hoverRow ?? (_tableGrid.CurrentItem as EditableRow)?.RawIndex;
            if (row >= 0 && row < _currentExtraction.Table.Rows.Count)
                regions = _currentExtraction.Table.Rows[row.Value].Cells.Select(cell => cell.Region);
        }
        else
        {
            string generated = NormalizeLines(_generatedText);
            string edited = NormalizeLines(_textBox.Text);
            string[] lines = generated.Split('\n');
            if (lines.Length == edited.Count(c => c == '\n') + 1)
            {
                int line = NormalizeLines(_textBox.Text[..Math.Min(_textBox.CaretIndex, _textBox.Text.Length)]).Count(c => c == '\n');
                if (ExtractionMode == OcrExtractionMode.Text)
                {
                    int first = 0;
                    foreach (OcrParagraph paragraph in _currentExtraction.Paragraphs)
                    {
                        int count = _joinLines.IsChecked == true ? 1 : paragraph.Lines.Count;
                        if (line >= first && line < first + count) { regions = [paragraph.Bounds]; break; }
                        first += count + 1;
                    }
                }
                else
                {
                    int region = line < _codeRegionForLine.Length ? _codeRegionForLine[line] : -1;
                    if (region >= 0 && region < _currentExtraction.Code.Regions.Count)
                        regions = [_currentExtraction.Code.Regions[region]];
                }
            }
        }
        List<OcrRect> overlayRegions = _imageOverlay.Regions.ToList();
        _imageOverlay.SetHighlightedRegions(regions.Select(region => overlayRegions.IndexOf(region)));
    }

    private void BuildCodeRegionMap()
    {
        _codeRegionForLine = [];
        if (_result is null || _currentExtraction is null || _currentExtraction.Code.Regions.Count == 0) return;
        string[] lines = NormalizeLines(_currentExtraction.Code.Text).Split('\n');
        int[] mapping = Enumerable.Repeat(-1, lines.Length).ToArray();
        int[] nonempty = Enumerable.Range(0, lines.Length).Where(i => !string.IsNullOrWhiteSpace(lines[i])).ToArray();
        int next = 0;
        for (int region = 0; region < _currentExtraction.Code.Regions.Count; region++)
        {
            // Reuse the extraction's geometry instead of guessing group boundaries from gaps.
            string block = OcrExtractionAnalyzer.Analyze(_result, _currentExtraction.Code.Regions[region]).Code.Text;
            int count = NormalizeLines(block).Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));
            if (next + count > nonempty.Length) return;
            for (int index = 0; index < count; index++) mapping[nonempty[next++]] = region;
        }
        if (next == nonempty.Length) _codeRegionForLine = mapping;
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record EditableRow(int RawIndex, EditableCell[] Cells);
    private sealed class EditableCell(string text, bool edited) : INotifyPropertyChanged
    {
        private string _text = text;
        private bool _isEdited = edited;
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public bool IsEdited { get => _isEdited; set { _isEdited = value; PropertyChanged?.Invoke(this, new(nameof(IsEdited))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
