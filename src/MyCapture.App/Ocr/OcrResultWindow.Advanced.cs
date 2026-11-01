using MyCapture.App.Themes;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

internal sealed partial class OcrResultWindow
{
    private TabControl _tabs = null!;
    private DataGrid _tableGrid = null!;
    private OcrImageOverlay _imageOverlay = null!;
    private CheckBox _joinLines = null!;
    private CheckBox _firstRowHeader = null!;
    private TextBlock _codeBadge = null!;
    private TextBlock _imageEmpty = null!;
    private Button _clearSelection = null!;
    private Button _plainExportButton = null!;
    private WrapPanel _tableActions = null!;
    private OcrResult? _result;
    private bool _geometryRequested;
    private OcrExtraction? _allExtraction;
    private OcrExtraction? _currentExtraction;
    private OcrRect? _deferredSelection;
    private bool _updatingAdvanced;
    private bool _modeChosen;
    internal event EventHandler? GeometryRequested;
    internal OcrExtractionMode ExtractionMode => (OcrExtractionMode)Math.Max(0, _tabs.SelectedIndex);
    internal OcrExtraction? Extraction => _currentExtraction;
    internal OcrImageOverlay ImageOverlay => _imageOverlay;
    internal DataGrid TableGrid => _tableGrid;
    internal string ExtractionText => _textBox.Text;

    private Grid BuildAdvancedWorkspace()
    {
        var workspace = new Grid();
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _tabs = new TabControl { Height = 42, BorderThickness = new Thickness(0), SelectedIndex = 2 };
        AutomationProperties.SetName(_tabs, UiText.Get("OCR410_ExtractionMode"));
        // Tab order follows OcrExtractionMode; the translation tab shows once a translator is set.
        foreach (string key in new[] { "OCR410_Table", "OCR410_Code", "OCR410_Text", "Translate_Tab" })
        {
            string label = UiText.Get(key);
            var tab = new TabItem { Header = label, MinWidth = 88, Padding = new Thickness(14, 7, 14, 7) };
            tab.SetResourceReference(StyleProperty, "Tab.Help");
            AutomationProperties.SetName(tab, label);
            _tabs.Items.Add(tab);
        }
        _translationTab = (TabItem)_tabs.Items[(int)OcrExtractionMode.Translation];
        _translationTab.Visibility = Visibility.Collapsed;
        workspace.Children.Add(_tabs);

        var options = new WrapPanel { Margin = new Thickness(8, 6, 8, 8) };
        _joinLines = Option("OCR410_JoinLines");
        _firstRowHeader = Option("OCR410_FirstRowHeader");
        _codeBadge = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _codeBadge.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _clearSelection = MakeButton(UiText.Get("OCR410_ClearSelection"), UiText.Get("OCR410_ClearSelection"));
        _clearSelection.SetResourceReference(StyleProperty, "Button.Ghost");
        options.Children.Add(_joinLines);
        options.Children.Add(_firstRowHeader);
        options.Children.Add(_codeBadge);
        AddEditIndicator(options);
        options.Children.Add(_clearSelection);
        Grid.SetRow(options, 1);
        workspace.Children.Add(options);

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 190 });
        panes.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        panes.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
        _imageOverlay = new OcrImageOverlay();
        _imageEmpty = KoreanWordWrap.Show(new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
        }, UiText.Get("OCR410_ImageUnavailable"));
        _imageEmpty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var preview = new Grid { Margin = new Thickness(8) };
        preview.Children.Add(_imageOverlay);
        preview.Children.Add(_imageEmpty);
        panes.Children.Add(preview);
        var splitter = new GridSplitter
        {
            Width = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Focusable = true,
        };
        splitter.SetResourceReference(Control.BackgroundProperty, "Border.Subtle");
        AutomationProperties.SetName(splitter, UiText.Get("OCR410_ResizePanes"));
        Grid.SetColumn(splitter, 1);
        panes.Children.Add(splitter);

        var results = new Grid();
        results.Children.Add(_textBox);
        _tableGrid = new DataGrid
        {
            IsReadOnly = false, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserResizeRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            GridLinesVisibility = DataGridGridLinesVisibility.All, BorderThickness = new Thickness(0),
            MinRowHeight = 34, FontSize = TypeScale.Body, Visibility = Visibility.Collapsed,
        };
        _tableGrid.SetResourceReference(DataGrid.RowBackgroundProperty, "Surface.Sunken");
        _tableGrid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Surface.Base");
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface.Raised")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text.Primary")));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Border.Subtle")));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        _tableGrid.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
        cellStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text.Primary")));
        var selectedCell = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selectedCell.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface.Selected")));
        selectedCell.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Text.Primary")));
        cellStyle.Triggers.Add(selectedCell);
        _tableGrid.CellStyle = cellStyle;
        _tableGrid.SetResourceReference(Control.BackgroundProperty, "Surface.Sunken");
        _tableGrid.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        _tableGrid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Border.Subtle");
        _tableGrid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "Border.Subtle");
        AutomationProperties.SetName(_tableGrid, UiText.Get("OCR410_Table"));
        _tableGrid.CopyingRowClipboardContent += (_, e) =>
        {
            for (int index = 0; index < e.ClipboardRowContent.Count; index++)
            {
                var cell = e.ClipboardRowContent[index];
                e.ClipboardRowContent[index] = new DataGridClipboardCellContent(cell.Item, cell.Column,
                    OcrTableExport.SpreadsheetLiteral(cell.Content?.ToString() ?? ""));
            }
        };
        results.Children.Add(_tableGrid);
        results.Children.Add(BuildTranslationPanel());
        Grid.SetColumn(results, 2);
        panes.Children.Add(results);
        Grid.SetRow(panes, 2);
        workspace.Children.Add(panes);
        return workspace;
    }

    private static CheckBox Option(string key)
    {
        string label = UiText.Get(key);
        var checkbox = new CheckBox
        {
            Content = label, VerticalAlignment = VerticalAlignment.Center, MinHeight = 32,
            Margin = new Thickness(0, 0, 16, 0),
        };
        checkbox.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(checkbox, label);
        return checkbox;
    }

    private void AddAdvancedActions(Panel buttons)
    {
        // In table mode these replace Copy and export, with the same order: the primary copy first.
        _tableActions = new WrapPanel { Visibility = Visibility.Collapsed };
        foreach (var action in new (string Key, Action Execute)[]
        {
            ("OCR410_CopyTsv", () => CopyValue(_currentExtraction is null ? "" : OcrTableExport.Tsv(CurrentTable()))),
            ("OCR410_CopyCsv", () => CopyValue(_currentExtraction is null ? "" : OcrTableExport.Csv(CurrentTable()))),
            ("OCR410_CopyMarkdown", () => CopyValue(_currentExtraction is null ? "" : OcrTableExport.Markdown(CurrentTable()))),
            ("OCR410_OpenExcel", OpenExcel),
        })
        {
            var button = MakeButton(UiText.Get(action.Key), UiText.Get(action.Key));
            button.Margin = new Thickness(0, 2, 8, 2);
            button.SetResourceReference(StyleProperty, action.Key == "OCR410_CopyTsv" ? "Button.Primary" : "Button.Ghost");
            button.Click += (_, _) => action.Execute();
            _tableActions.Children.Add(button);
            if (action.Key == "OCR410_CopyTsv")
            {
                _tablePinButton = CreatePinButton();
                _tableActions.Children.Add(_tablePinButton);
            }
        }
        buttons.Children.Add(_tableActions);
    }

    private void CompleteAdvancedSetup(Button exportButton)
    {
        _plainExportButton = exportButton;
        InitializeEditing();
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _tabs) || _updatingAdvanced) return;
            _hoverRow = null;
            _modeChosen = true;
            RefreshAdvancedView();
            RequestGeometryIfNeeded();
        };
        _joinLines.Checked += (_, _) => ChangeJoinLines();
        _joinLines.Unchecked += (_, _) => ChangeJoinLines();
        _firstRowHeader.Checked += (_, _) => RefreshAdvancedView();
        _firstRowHeader.Unchecked += (_, _) => RefreshAdvancedView();
        _clearSelection.Click += (_, _) => _imageOverlay.SetSelection(null);
        _imageOverlay.SelectionChanged += (_, _) =>
        {
            if (_updatingAdvanced || _result is null || _allExtraction is null) return;
            if (ExtractionMode == OcrExtractionMode.Translation && RevealTranslationBlock(_imageOverlay.Selection)) return;
            if (!ConfirmReplacement())
            {
                _imageOverlay.SetSelection(_deferredSelection, notify: false);
                return;
            }
            ResetEdits();
            _deferredSelection = _imageOverlay.Selection;
            if (!_allExtraction.HasGeometry && _deferredSelection is not null)
            {
                RequestGeometryIfNeeded(force: true);
                return;
            }
            _currentExtraction = _deferredSelection is null ? _allExtraction
                : OcrExtractionAnalyzer.Analyze(_result, _deferredSelection);
            SetHeaderEstimate();
            RefreshAdvancedView();
        };
        RefreshAdvancedView();
    }

    internal void SelectExtractionMode(OcrExtractionMode mode) => _tabs.SelectedIndex = (int)mode;

    internal void SetSourceImage(BitmapSource? image)
    {
        _imageOverlay.SetImage(image);
        _imageEmpty.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PrepareAdvancedBusy(bool preserveSelection)
    {
        _deferredSelection = preserveSelection ? _imageOverlay.Selection : null;
        if (preserveSelection)
        {
            _tabs.IsEnabled = false;
            return;
        }
        if (!preserveSelection)
        {
            _modeChosen = false;
            _geometryRequested = false;
        }
        ClearAdvancedResult();
        _tabs.IsEnabled = false;
        RefreshAdvancedView();
    }

    private void ClearAdvancedResult()
    {
        ResetEdits();
        _result = null;
        _allExtraction = null;
        _currentExtraction = null;
        ResetTranslation();
        SetEditorText(string.Empty);
        _tableGrid.ItemsSource = null;
        _tableGrid.Columns.Clear();
        _imageOverlay.SetSelection(null, notify: false);
        _imageOverlay.SetRegions([]);
        SetSourceImage(null);
    }

    /// <summary>Keep the reusable controls, but release the dismissed capture and its derived results.</summary>
    internal void ReleaseResult()
    {
        ReleaseAccuracy();
        _deferredSelection = null;
        _modeChosen = false;
        _geometryRequested = false;
        ClearAdvancedResult();
        _status.Text = string.Empty;
        _rerunButton.IsEnabled = false;
        _copyButton.IsEnabled = false;
        _tabs.IsEnabled = false;
        RefreshAdvancedView();
    }

    private void ApplyAdvancedResult(OcrResult result)
    {
        _result = result;
        _allExtraction = OcrExtractionAnalyzer.Analyze(result);
        _currentExtraction = _deferredSelection is null ? _allExtraction
            : OcrExtractionAnalyzer.Analyze(result, _deferredSelection);
        _imageOverlay.SetSelection(_deferredSelection, notify: false);
        _tabs.IsEnabled = true;
        if (!_modeChosen)
        {
            _updatingAdvanced = true;
            _tabs.SelectedIndex = _allExtraction.Table.HasStructure && _allExtraction.Table.Columns.Any(column => column.IsNumeric)
                ? 0 : _allExtraction.Code.Language is not null ? 1 : 2;
            _updatingAdvanced = false;
        }
        SetHeaderEstimate();
        RefreshAdvancedView();
    }

    private void SetHeaderEstimate()
    {
        _updatingAdvanced = true;
        _firstRowHeader.IsChecked = _currentExtraction?.Table.HasHeader == true;
        _updatingAdvanced = false;
    }

    internal OcrTable CurrentTable()
    {
        OcrTable table = _currentExtraction?.Table ?? OcrTable.Empty;
        return table with
        {
            HasHeader = _firstRowHeader.IsChecked == true,
            Rows = table.Rows.Select((row, r) => new OcrTableRow(row.Cells.Select((cell, c) =>
                _tableEdits.TryGetValue((r, c), out string? text) ? cell with { Text = text } : cell).ToArray())).ToArray(),
        };
    }

    private void RefreshAdvancedView()
    {
        if (_updatingAdvanced) return;
        CommitTableEdit();
        OcrExtractionMode mode = ExtractionMode;
        bool table = mode == OcrExtractionMode.Table;
        bool translation = mode == OcrExtractionMode.Translation;
        _tableGrid.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        _translationPanel.Visibility = translation ? Visibility.Visible : Visibility.Collapsed;
        _textBox.Visibility = table || translation ? Visibility.Collapsed : Visibility.Visible;
        _tableActions.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        _copyButton.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        SetCopyButtonLabel(translation ? "Translate_Copy" : "Text_37B3D3B11B26",
            translation ? "Translate_CopyHelp" : "Text_20CF6A996B4D");
        _copyWithSourceButton.Visibility = translation ? Visibility.Visible : Visibility.Collapsed;
        _plainExportButton.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        _joinLines.Visibility = mode == OcrExtractionMode.Text ? Visibility.Visible : Visibility.Collapsed;
        _firstRowHeader.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        _codeBadge.Visibility = mode == OcrExtractionMode.Code ? Visibility.Visible : Visibility.Collapsed;
        _clearSelection.Visibility = _imageOverlay.Selection is null ? Visibility.Collapsed : Visibility.Visible;
        _joinLines.IsEnabled = _currentExtraction is not null;
        _firstRowHeader.IsEnabled = _currentExtraction?.Table.Rows.Count > 1;
        _textBox.TextWrapping = mode == OcrExtractionMode.Code ? TextWrapping.NoWrap : TextWrapping.Wrap;
        _textBox.FontFamily = ResourceFont(mode == OcrExtractionMode.Code ? "Font.Mono" : "Font.Body",
            mode == OcrExtractionMode.Code ? "Consolas, Malgun Gothic" : "Segoe UI, Malgun Gothic");
        // In the translation view the hidden text box holds the translations for copy and export.
        if (translation) EnsureTranslation();
        string generated = _currentExtraction is null ? "" : mode switch
        {
            OcrExtractionMode.Table => OcrTableExport.Tsv(CurrentTable()),
            OcrExtractionMode.Code => _currentExtraction.Code.Text,
            OcrExtractionMode.Translation => _translationPanel.CopyText,
            _ => _currentExtraction.Text(_joinLines.IsChecked == true),
        };
        _generatedText = generated;
        if (mode == OcrExtractionMode.Code) BuildCodeRegionMap();
        SetEditorText(_textEdits.GetValueOrDefault(mode, generated));
        bool hasContent = _textBox.Text.Length > 0;
        _copyButton.IsEnabled = hasContent;
        _copyWithSourceButton.IsEnabled = translation && hasContent;
        _plainExportButton.IsEnabled = hasContent;
        _tableActions.IsEnabled = hasContent;
        UpdateEditIndicator();
        UpdatePinButtons();
        _codeBadge.Text = _currentExtraction is null ? "" : CodeBadge(_currentExtraction.Code);
        if (table) PopulateTable(CurrentTable());
        if (translation)
        {
            ShowTranslationRegions();
            return;
        }
        _imageOverlay.SetRegions(_currentExtraction is null ? [] : mode switch
        {
            OcrExtractionMode.Table => _currentExtraction.Table.Rows.SelectMany(row => row.Cells).Select(cell => cell.Region),
            OcrExtractionMode.Code => _currentExtraction.Code.Regions,
            _ => _currentExtraction.Paragraphs.Select(paragraph => paragraph.Bounds),
        });
        UpdateSourceHighlight();
    }

    /// <summary>The primary button names what it copies in this view: the text, or only the translation.</summary>
    private void SetCopyButtonLabel(string labelKey, string helpKey)
    {
        string label = UiText.Get(labelKey);
        if (_copyButton.Content is Grid { Name: "CommandButtonFace", Children.Count: > 0 } face
            && face.Children[0] is TextBlock text) text.Text = label;
        else _copyButton.Content = label;
        AutomationProperties.SetName(_copyButton, UiText.Get(helpKey));
        // The tooltip says what the button does now, with the same key.
        _copyButton.ClearValue(FrameworkElement.ToolTipProperty);
        ShortcutPresentation.Describe(_copyButton, ShortcutScope.Ocr, "Copy");
    }

    private void PopulateTable(OcrTable table)
    {
        _tableGrid.Columns.Clear();
        for (int column = 0; column < table.Columns.Count; column++)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,
                table.Columns[column].IsNumeric ? TextAlignment.Right : TextAlignment.Left));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 5, 8, 5)));
            var editedStyle = new Style(typeof(DataGridCell), _tableGrid.CellStyle);
            var edited = new DataTrigger { Binding = new Binding($"Cells[{column}].IsEdited"), Value = true };
            edited.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("State.Success")));
            edited.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2, 0, 0, 0)));
            editedStyle.Triggers.Add(edited);
            _tableGrid.Columns.Add(new DataGridTextColumn
            {
                Header = table.HasHeader && table.Rows.Count > 0
                    ? table.Rows[0].Cells[column].Text : OcrTableExport.ColumnName(column),
                Binding = new Binding($"Cells[{column}].Text") { Mode = BindingMode.TwoWay },
                CellStyle = editedStyle,
                ElementStyle = style, MinWidth = 80,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
        }
        _tableGrid.ItemsSource = table.Rows.Select((row, r) => new EditableRow(r,
            row.Cells.Select((cell, c) => new EditableCell(cell.Text, _tableEdits.ContainsKey((r, c)))).ToArray()))
            .Skip(table.HasHeader ? 1 : 0).ToArray();
    }

    private string CodeBadge(OcrCode code)
    {
        string language = code.Language is null ? UiText.Get("OCR410_UnknownLanguage")
            : UiText.Format("OCR410_EstimatedLanguage", code.Language);
        string font = UiText.Get(code.IsMonospaced switch
        {
            true => "OCR410_Monospace", false => "OCR410_Proportional", null => "OCR410_UnknownFont",
        });
        return language + " · " + font;
    }

    private FontFamily ResourceFont(string key, string fallback) =>
        TryFindResource(key) as FontFamily ?? new FontFamily(fallback);

    private void FocusAdvancedResult()
    {
        if (ExtractionMode == OcrExtractionMode.Table) _ = _tableGrid.Focus();
        else if (ExtractionMode == OcrExtractionMode.Translation) _ = _tabs.Focus();
        else SelectAllText();
    }

    private void RequestGeometryIfNeeded(bool force = false)
    {
        if (_result?.HasText != true || _allExtraction?.HasGeometry != false
            || (!force && ExtractionMode == OcrExtractionMode.Text)
            || _geometryRequested) return;
        _geometryRequested = true;
        GeometryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenExcel()
    {
        if (_currentExtraction?.Table.Rows.Count is not > 0) return;
        try { _ = OcrExcelExport.Open(CurrentTable()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _status.Text = UiText.Get("OCR410_ExcelFailed") + " "
                + MyCapture.App.Themes.ThemedDialog.RecoveryMessage(error);
        }
    }

    private void CopyValue(string text)
    {
        if (text.Length == 0) return;
        try
        {
            ClipboardWriter.SetText(text);
            _status.Text = UiText.Get("OCR410_Copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _status.Text = UiText.Get("Text_75425A6D9BE8");
        }
    }
}
