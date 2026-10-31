using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class OcrEditingWindowTests
{
    [Fact]
    public void TableEditsFeedEveryFormatAndSurviveHeaderToggle() => WithWindow(window =>
    {
        var (image, result) = Ocr410Fixture.Table();
        window.ShowResult(result, "table", image);
        window.SelectExtractionMode(OcrExtractionMode.Table);
        window.EditTableCell(2, 1, "123");
        Assert.Contains("123", OcrTableExport.Tsv(window.CurrentTable()));
        Assert.Contains("123", OcrTableExport.Csv(window.CurrentTable()));
        Assert.Contains("123", OcrTableExport.Markdown(window.CurrentTable()));
        Assert.Contains("123", window.ExtractionText);
        Assert.Equal("편집 1곳", Field<TextBlock>(window, "_editCount").Text);
        foreach (string key in new[] { "OCR410_CopyTsv", "OCR410_CopyCsv", "OCR410_CopyMarkdown" })
        {
            Button(window, key).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Contains("123", Assert.IsType<string>(RecordingClipboard.LastWrite!.Formats[DataFormats.UnicodeText]));
        }
        Field<CheckBox>(window, "_firstRowHeader").IsChecked = false;
        Assert.Contains("123", window.ExtractionText);
        window.EditTableCell(0, 1, "수량 수정");
        Field<CheckBox>(window, "_firstRowHeader").IsChecked = true;
        Assert.Equal("수량 수정", window.TableGrid.Columns[1].Header);
        Assert.Contains("123", window.ExtractionText);
        window.EditTableCell(2, 1, "12");
        Assert.Equal("편집 1곳", Field<TextBlock>(window, "_editCount").Text);
        window.RevertCurrentEdits();
        Assert.False(window.HasEditedText);
        Assert.Equal(Visibility.Collapsed, Field<WrapPanel>(window, "_editIndicator").Visibility);
        Assert.Equal("수량", window.TableGrid.Columns[1].Header);
    });

    [Theory]
    [InlineData(OcrExtractionMode.Text)]
    [InlineData(OcrExtractionMode.Code)]
    public void TextEditsSurviveTabsAndFeedCopyAndExport(OcrExtractionMode mode) => WithWindow(window =>
    {
        window.ShowResult(Text("first\nsecond"), "text");
        window.SelectExtractionMode(mode);
        window.ResultTextBox.Text = "수정한 원문\n42";
        window.ResultTextBox.Select(0, 0);
        window.ShortcutActions["Copy"]();
        Assert.Equal("수정한 원문\n42", RecordingClipboard.LastWrite!.Formats[DataFormats.UnicodeText]);
        string root = DiagnosticOutputPaths.Create(Path.Combine(Path.GetTempPath(), "ocr-edit-" + Guid.NewGuid().ToString("N")));
        string path = DiagnosticOutputPaths.Child(root, "edited.txt");
        window.TextExportPathChooser = () => path;
        window.ShortcutActions["Export"]();
        Assert.Equal("수정한 원문\n42", File.ReadAllText(path));
        OcrExtractionMode other = mode == OcrExtractionMode.Text ? OcrExtractionMode.Code : OcrExtractionMode.Text;
        window.SelectExtractionMode(other);
        Assert.Equal("first\nsecond", window.ExtractionText);
        window.ResultTextBox.Text = "다른 탭";
        window.SelectExtractionMode(mode);
        Assert.Equal("수정한 원문\n42", window.ExtractionText);
        window.RevertCurrentEdits();
        Assert.Equal("first\nsecond", window.ExtractionText);
        Assert.True(window.HasEditedText);
        window.SelectExtractionMode(other);
        Assert.Equal("다른 탭", window.ExtractionText);
    });

    [Fact]
    public void SelectionAndJoinDeclineRestoreStateAndNewSourceDropsEditsWithoutPrompt() => WithWindow(window =>
    {
        var (image, result) = Ocr410Fixture.Table();
        window.ShowResult(result, "table", image);
        window.SelectExtractionMode(OcrExtractionMode.Table);
        window.EditTableCell(2, 1, "123");
        int confirmations = 0;
        bool accept = false;
        window.ConfirmReplaceEditedText = () => { confirmations++; return accept; };
        OcrRect selected = window.Extraction!.Table.Rows[2].Cells[1].Region;
        window.ImageOverlay.SetSelection(selected);
        Assert.Equal(1, confirmations);
        Assert.Null(window.ImageOverlay.Selection);
        Assert.Contains("123", window.ExtractionText);
        accept = true;
        window.ImageOverlay.SetSelection(selected);
        Assert.Equal(2, confirmations);
        Assert.Equal(selected, window.ImageOverlay.Selection);
        Assert.False(window.HasEditedText);
        window.ShowResult(Text("first\nsecond"), "text");
        window.SelectExtractionMode(OcrExtractionMode.Text);
        window.ResultTextBox.Text = "edited";
        accept = false;
        var join = Field<CheckBox>(window, "_joinLines");
        join.IsChecked = true;
        Assert.False(join.IsChecked);
        Assert.Equal("edited", window.ExtractionText);
        Assert.Equal(3, confirmations);
        accept = true;
        join.IsChecked = true;
        Assert.Equal("first second", window.ExtractionText);
        Assert.False(window.HasEditedText);
        window.ResultTextBox.Text = "edited again";
        window.ShowResult(Text("new source"), "new");
        Assert.Equal(4, confirmations);
        Assert.Equal("new source", window.ExtractionText);
        Assert.False(window.HasEditedText);
    });

    [Fact]
    public void RerunPromptsForEditsInHiddenTabsAndDeclineKeepsThem() => WithWindow(window =>
    {
        var (image, result) = Ocr410Fixture.Table();
        var service = new CountingOcr(result);
        using var presenter = new OcrResultPresenter(service, Dispatcher.CurrentDispatcher, _ => { },
            NullLogger.Instance, window: window);
        presenter.ShowCached(result, "source", () => OcrRequest.FromBitmap(image));
        window.SelectExtractionMode(OcrExtractionMode.Text);
        window.ResultTextBox.Text = "hidden edit";
        window.SelectExtractionMode(OcrExtractionMode.Table);
        int confirmations = 0;
        window.ConfirmReplaceEditedText = () => { confirmations++; return false; };
        Button(window, "Text_613230F07E18").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(1, confirmations);
        Assert.Equal(0, service.Calls);
        window.SelectExtractionMode(OcrExtractionMode.Text);
        Assert.Equal("hidden edit", window.ExtractionText);
        window.ConfirmReplaceEditedText = () => { confirmations++; return true; };
        Button(window, "Text_613230F07E18").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Ocr410Fixture.Pump();
        Assert.Equal(2, confirmations);
        Assert.Equal(1, service.Calls);
        Assert.False(window.HasEditedText);
    });

    [Fact]
    public void CurrentTableRowHoverAndLeaveMapToOriginalRegions() => WithWindow(window =>
    {
        var (image, result) = Ocr410Fixture.Table();
        window.ShowResult(result, "table", image);
        window.SelectExtractionMode(OcrExtractionMode.Table);
        window.TableGrid.CurrentCell = new DataGridCellInfo(window.TableGrid.Items[1], window.TableGrid.Columns[0]);
        Assert.Equal(new[] { 6, 7, 8 }, window.ImageOverlay.HighlightedRegions);
        window.SetHoveredTableRow(1);
        Assert.Equal(new[] { 3, 4, 5 }, window.ImageOverlay.HighlightedRegions);
        window.SetHoveredTableRow(null);
        Assert.Equal(new[] { 6, 7, 8 }, window.ImageOverlay.HighlightedRegions);
        window.ImageOverlay.SetSelection(window.Extraction!.Table.Rows[2].Cells.Select(c => c.Region).Aggregate(OcrRect.Union));
        window.TableGrid.CurrentCell = new DataGridCellInfo(window.TableGrid.Items[0], window.TableGrid.Columns[0]);
        Assert.Equal(window.Extraction!.Table.Rows[0].Cells.Select(c => c.Region),
            window.ImageOverlay.HighlightedRegions.Select(i => window.ImageOverlay.Regions[i]));
    });

    [Fact]
    public void CaretParagraphAndCodeRegionHighlightDisappearWhenLinesNoLongerMatch() => WithWindow(window =>
    {
        var (image, result) = Ocr410Fixture.Table();
        window.ShowResult(result, "text", image);
        window.SelectExtractionMode(OcrExtractionMode.Text);
        Assert.True(window.Extraction!.Paragraphs.Count > 1);
        int offset = string.Join("\n", window.Extraction.Paragraphs[0].Lines).Length + 2;
        window.ResultTextBox.CaretIndex = offset;
        Assert.Equal(1, window.ImageOverlay.HighlightedRegion);
        window.ResultTextBox.AppendText("\nextra");
        Assert.Empty(window.ImageOverlay.HighlightedRegions);
        window.RevertCurrentEdits();
        window.SelectExtractionMode(OcrExtractionMode.Code);
        window.ResultTextBox.CaretIndex = 1;
        Assert.NotEmpty(window.ImageOverlay.HighlightedRegions);
        window.ResultTextBox.AppendText("\nextra");
        Assert.Empty(window.ImageOverlay.HighlightedRegions);
    });

    [Fact]
    public void CodeCaretMapsSeparatedBlocksAndNarrowedExtractionByRegionEquality() => WithWindow(window =>
    {
        OcrResult result = Ocr410ExtractionTests.CodeResult("public class Sample\n{\n    int Count = 12;\n}\n\n\npublic class Second\n{\n}");
        window.ShowResult(result, "code", Ocr410Fixture.Table().Image);
        window.SelectExtractionMode(OcrExtractionMode.Code);
        Assert.Equal(2, window.Extraction!.Code.Regions.Count);
        window.ResultTextBox.CaretIndex = window.ExtractionText.IndexOf("Second", StringComparison.Ordinal);
        Assert.Equal(1, window.ImageOverlay.HighlightedRegion);
        OcrRect second = window.Extraction.Code.Regions[1];
        window.ImageOverlay.SetSelection(second);
        window.ResultTextBox.CaretIndex = window.ExtractionText.IndexOf("Second", StringComparison.Ordinal);
        Assert.Equal(second, window.ImageOverlay.Regions[window.ImageOverlay.HighlightedRegion!.Value]);
    });

    [Fact]
    public void CellEscapeCancelsWithoutClosingAndEnterCommits() => WithWindow(window =>
    {
        window.SuppressPresentation = false;
        using var size = new WindowTrackSizeScope(window, new Size(1040, 720));
        var (image, result) = Ocr410Fixture.Table();
        window.ShowResult(result, "table", image);
        window.SelectExtractionMode(OcrExtractionMode.Table);
        window.UpdateLayout();
        DataGrid grid = window.TableGrid;
        grid.CurrentCell = new DataGridCellInfo(grid.Items[1], grid.Columns[1]);
        grid.Focus();
        Assert.True(grid.BeginEdit());
        window.UpdateLayout();
        TextBox editor = Assert.IsType<TextBox>(grid.Columns[1].GetCellContent(grid.Items[1]));
        editor.Text = "987";
        KeyEventArgs preview = Key(window, System.Windows.Input.Key.Escape, Keyboard.PreviewKeyDownEvent);
        editor.RaiseEvent(preview);
        Assert.False(preview.Handled);
        editor.RaiseEvent(Key(window, System.Windows.Input.Key.Escape, Keyboard.KeyDownEvent));
        Assert.True(window.IsVisible);
        Assert.False(window.HasEditedText);
        Assert.True(grid.BeginEdit());
        window.UpdateLayout();
        editor = Assert.IsType<TextBox>(grid.Columns[1].GetCellContent(grid.Items[1]));
        editor.Text = "987";
        editor.RaiseEvent(Key(window, System.Windows.Input.Key.Enter, Keyboard.KeyDownEvent));
        Assert.True(window.HasEditedText);
        Assert.Equal("987", window.CurrentTable().Rows[2].Cells[1].Text);
    });

    [Theory]
    [InlineData(OcrExtractionMode.Table, "a\tb\n1\t2", "Table", false)]
    [InlineData(OcrExtractionMode.Text, "exact text", "Text", false)]
    [InlineData(OcrExtractionMode.Code, "public class Sample { public int Value = 42; }", "Text", true)]
    [InlineData(OcrExtractionMode.Code, "no detected language", "Text", false)]
    public void PinContentUsesExactSourceAndExistingCodePipeline(OcrExtractionMode mode, string text,
        string kind, bool code) => Ocr410Fixture.RunSta(() =>
    {
        PinContent content = PinManager.CreateOcrContentAsync(text, mode).GetAwaiter().GetResult();
        Assert.Equal(kind, content.Kind.ToString());
        Assert.Equal(text, content.OriginalText);
        Assert.Equal(code, content.CodeDocument is not null);
    });

    [Fact]
    public void PinRequestCarriesEditedTableAndPinCopyDoesNotRequestOcr() => WithWindow(window =>
    {
        window.SuppressPresentation = false;
        using var windowSize = new WindowTrackSizeScope(window, new Size(1040, 720));
        var (image, result) = Ocr410Fixture.Table();
        window.ShowResult(result, "table", image);
        window.SelectExtractionMode(OcrExtractionMode.Table);
        window.EditTableCell(2, 1, "123");
        string expected = OcrTableExport.Tsv(window.CurrentTable());
        string? copied = null;
        int ocrCalls = 0;
        var manager = new PinManager(() => new PinSettings { ClosedWindowRestoreLimit = 0 }, null,
            (_, _, text) => { copied = text; return Task.FromResult(true); },
            _ => throw new InvalidOperationException("Unexpected text copy."), NullLogger.Instance);
        manager.OcrRequested += (_, _) => ocrCalls++;
        WindowTrackSizeScope? size = null;
        manager.BeforeShowForTest = pin => size = new WindowTrackSizeScope(pin, new Size(1040, 720));
        PinWindow? made = null;
        window.PinRequested += (_, request) =>
        {
            Assert.Equal(OcrExtractionMode.Table, request.Kind);
            Assert.Equal(expected, request.Text);
            made = manager.PinOcrAsync(request.Text, request.Kind).GetAwaiter().GetResult();
            request.Completion = Task.FromResult(true);
        };
        try
        {
            window.RequestPinAsync().GetAwaiter().GetResult();
            PinWindow pin = Assert.IsType<PinWindow>(made);
            var key = Key(pin, System.Windows.Input.Key.C, Keyboard.KeyDownEvent);
            pin.HandleKeyDown(key, ModifierKeys.Control);
            Assert.True(key.Handled);
            Assert.Equal(expected, copied);
            Assert.Equal(0, ocrCalls);
            Assert.True(window.IsVisible);
            Assert.Contains("핀으로 띄웠습니다.", KoreanWordWrap.Strip(Field<TextBlock>(window, "_status").Text));
        }
        finally { manager.CloseAllForExit(); size?.Dispose(); }
    });

    [Theory]
    [InlineData(OcrExtractionMode.Text)]
    [InlineData(OcrExtractionMode.Code)]
    public void TextAndCodePinRequestsUseTheEditedActiveTab(OcrExtractionMode mode) => WithWindow(window =>
    {
        window.ShowResult(Text("first\nsecond"), "text");
        window.SelectExtractionMode(mode);
        window.ResultTextBox.Text = "edited source";
        int requests = 0;
        window.PinRequested += (_, request) =>
        {
            requests++;
            Assert.Equal(mode, request.Kind);
            Assert.Equal("edited source", request.Text);
            request.Completion = Task.FromResult(true);
        };
        window.RequestPinAsync().GetAwaiter().GetResult();
        Assert.Equal(1, requests);
    });

    [Fact]
    public void LargeEditableResultRetainsFullSelectionAndAutomationText() => WithWindow(window =>
    {
        window.SuppressPresentation = false;
        using var size = new WindowTrackSizeScope(window, new Size(1040, 720));
        string content = string.Join('\n', Enumerable.Repeat("전체 원문 한글😀 abcXYZ0123456789", 4000));
        Assert.True(content.Length >= 100_000);
        window.ShowResult(Text(content), "large");
        window.UpdateLayout();
        TextBox box = window.ResultTextBox;
        Assert.False(box.IsReadOnly);
        Assert.True(box.IsUndoEnabled);
        Assert.False(InputMethod.GetIsInputMethodEnabled(box));
        Assert.Equal(content, box.Text);
        Assert.Equal(content, box.SelectedText);
        var scroll = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        Assert.True(scroll.ScrollableHeight > 0);
        var peer = new TextBoxAutomationPeer(box);
        var provider = Assert.IsAssignableFrom<ITextProvider>(peer.GetPattern(PatternInterface.Text));
        Assert.Equal(content, provider.DocumentRange.GetText(-1));
        box.Select(0, 2);
        Assert.Equal(content[..2], Assert.Single(provider.GetSelection()).GetText(-1));
        Assert.True(InputMethod.GetIsInputMethodEnabled(box));
        box.SelectAll();
        Assert.Equal(content, box.SelectedText);
        Assert.True(window.ShortcutActions.ContainsKey("Copy"));
        box.Select(0, 2);
        box.SelectedText = "수정";
        Assert.StartsWith("수정", box.Text);
        Assert.True(window.HasEditedText);
        box.Undo();
        Assert.Equal(content, box.Text);
    });

    [Fact]
    public void PinIsHiddenForTranslationDisabledForEmptyAndShowsFailure() => WithWindow(window =>
    {
        window.ShowResult(Text("text"), "text");
        window.SelectExtractionMode(OcrExtractionMode.Text);
        Assert.True(Field<Button>(window, "_pinButton").IsEnabled);
        window.RequestPinAsync().GetAwaiter().GetResult();
        Assert.Equal(UiText.Get("Onboard_PinFailed"), KoreanWordWrap.Strip(Field<TextBlock>(window, "_status").Text));
        window.SelectExtractionMode(OcrExtractionMode.Translation);
        Assert.Equal(Visibility.Collapsed, Field<Button>(window, "_pinButton").Visibility);
        window.SelectExtractionMode(OcrExtractionMode.Text);
        window.ResultTextBox.Clear();
        Assert.False(Field<Button>(window, "_pinButton").IsEnabled);
    });

    internal static void WithWindow(Action<OcrResultWindow> body) => Ocr410Fixture.RunSta(() =>
    {
        using var language = UiText.UseLanguage("ko");
        var window = new OcrResultWindow { SuppressPresentation = true, ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        try { body(window); }
        finally { window.CloseForExit(); }
    });

    internal static Button Button(OcrResultWindow window, string key) => Descendants(window).OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == UiText.Get(key));
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
            if (child is DependencyObject dependency)
            {
                yield return dependency;
                foreach (DependencyObject nested in Descendants(dependency)) yield return nested;
            }
    }
    private static KeyEventArgs Key(Window window, Key key, RoutedEvent route) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = route };
    private static OcrResult Text(string text) => OcrResult.Success(text, "en", [], TimeSpan.Zero);
    private sealed class CountingOcr(OcrResult result) : IOcrService
    {
        internal int Calls;
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(result); }
    }
}
