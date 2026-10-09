using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ocr410WindowTests
{
    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void TableSelectionChangesDisplayedAndExportedContentAndClearRestoresIt(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            Assert.Same(image, window.ImageOverlay.SourceImage);
            Assert.Equal(3, window.TableGrid.Columns.Count);
            Assert.Equal("항목", window.TableGrid.Columns[0].Header);
            Assert.Equal(12, window.ImageOverlay.Regions.Count);
            OcrRect cell = window.Extraction!.Table.Rows[2].Cells[2].Region;
            window.ImageOverlay.SetSelection(cell);
            Assert.Equal("9,900", window.Extraction!.Table.Rows[0].Cells[0].Text);
            Assert.Equal("9,900", window.ExtractionText);
            Assert.DoesNotContain("키보드", OcrTableExport.Csv(window.Extraction.Table));
            window.ImageOverlay.SetSelection(null);
            Assert.Contains("키보드", window.ExtractionText);
            Assert.Equal(3, window.TableGrid.Columns.Count);
            Assert.Equal(UiText.Get("OCR410_SelectionHelp"), AutomationProperties.GetHelpText(window.ImageOverlay));
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void BusyAndFailedResultClearAllExportableContentAndOldImage() => Ocr410Fixture.RunSta(() =>
    {
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "A", image);
            window.ShowBusy("B");
            Assert.Empty(window.ExtractionText);
            Assert.Null(window.ImageOverlay.SourceImage);
            Assert.Empty(window.ImageOverlay.Regions);
            window.ShowResult(OcrResult.Failed("synthetic error"), "B");
            Assert.Empty(window.ExtractionText);
            Assert.Empty(window.TableGrid.Columns);
            Assert.Null(window.ImageOverlay.SourceImage);
            Assert.False(Descendants(window).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == UiText.Get("OCR410_CopyCsv")).IsEnabled);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void TextJoinAndHeaderOverrideAreLiveUserOptions() => Ocr410Fixture.RunSta(() =>
    {
        var window = new OcrResultWindow { ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(OcrResult.Success("first\nsecond\n\nthird", "en-US", [], TimeSpan.Zero), "fixture");
            window.SelectExtractionMode(OcrExtractionMode.Text);
            var join = Descendants(window).OfType<CheckBox>()
                .Single(control => AutomationProperties.GetName(control) == UiText.Get("OCR410_JoinLines"));
            join.IsChecked = true;
            Assert.Equal("first second\n\nthird", window.ExtractionText);
            var (image, result) = Ocr410Fixture.Table();
            window.ShowResult(result, "table", image);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            var header = Descendants(window).OfType<CheckBox>()
                .Single(control => AutomationProperties.GetName(control) == UiText.Get("OCR410_FirstRowHeader"));
            header.IsChecked = false;
            Assert.Equal("A", window.TableGrid.Columns[0].Header);
            Assert.Equal(4, window.TableGrid.Items.Count);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void CachedTextRequestsGeometryOnceWhenAdvancedModeIsChosen() => Ocr410Fixture.RunSta(() =>
    {
        var window = new OcrResultWindow { ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        int requests = 0;
        window.GeometryRequested += (_, _) => requests++;
        try
        {
            window.ShowResult(OcrResult.Success("cached words", "en-US", [], TimeSpan.Zero), "cached");
            Assert.Equal(0, requests);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            window.SelectExtractionMode(OcrExtractionMode.Code);
            Assert.Equal(1, requests);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void CachedRerunWithoutGeometryDoesNotStartAnAutomaticRecognitionLoop() => Ocr410Fixture.RunSta(() =>
    {
        var window = new OcrResultWindow { ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        int requests = 0;
        window.GeometryRequested += (_, _) => requests++;
        try
        {
            window.ShowResult(OcrResult.Success("public class Sample { }", "en-US", [], TimeSpan.Zero), "cached");
            window.SelectExtractionMode(OcrExtractionMode.Code);
            Assert.Equal(1, requests);
            window.ShowBusy("cached", preserveSelection: true);
            window.ShowResult(OcrResult.Success("public class Sample { }", "en-US", [], TimeSpan.Zero),
                "cached", preserveSelection: true);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            window.SelectExtractionMode(OcrExtractionMode.Code);
            Assert.Equal(1, requests);
            Assert.Contains("public class Sample", window.ExtractionText);
            window.ShowResult(OcrResult.Success("public class Other { }", "en-US", [], TimeSpan.Zero), "new");
            Assert.Equal(2, requests);
        }
        finally { window.CloseForExit(); }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void MinimumWindowKeepsVisibleActionsInsideClientArea(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { ShowInTaskbar = false, Width = 760, Height = 540 };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            window.UpdateLayout();
            Ocr410Fixture.Pump();
            foreach (Button button in Descendants(window).OfType<Button>().Where(button => button.IsVisible
                         && AutomationProperties.GetName(button).Length > 0))
            {
                Rect bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= window.ActualWidth
                    && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
                    $"{AutomationProperties.GetName(button)}: {bounds}; window={window.ActualWidth}x{window.ActualHeight}");
            }
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void PresenterUsesFrozenWindowsOnlySnapshotAndCancelsOnDismiss() => Ocr410Fixture.RunSta(() =>
    {
        var (image, _) = Ocr410Fixture.Table();
        var service = new PendingOcr();
        using var presenter = new OcrResultPresenter(service, Dispatcher.CurrentDispatcher, _ => { },
            NullLogger<OcrResultPresenter>.Instance);
        presenter.ShowRecognized(() => OcrRequest.FromBitmap(image, useNeuralModel: true, useSuperResolution: true), "fixture");
        Assert.NotNull(service.Request);
        Assert.Same(image, service.Request.Bitmap);
        Assert.False(service.Request.UseNeuralModel);
        Assert.False(service.Request.UseSuperResolution);
        var window = (OcrResultWindow)typeof(OcrResultPresenter).GetField("_window",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(presenter)!;
        window.Close();
        Assert.True(service.Token.IsCancellationRequested);
        service.Complete();
        Ocr410Fixture.Pump();
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void SourceBytesBecomeFrozenWithoutKeepingStreamOpen() => Ocr410Fixture.RunSta(() =>
    {
        var (image, _) = Ocr410Fixture.Table();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        BitmapSource? snapshot = OcrSourceSnapshot.LoadAsync(OcrRequest.FromBytes(bytes.ToArray()), CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsFrozen);
        Assert.Equal(image.PixelWidth, snapshot.PixelWidth);
        Assert.Equal(image.PixelHeight, snapshot.PixelHeight);
    });

    [Fact]
    public void KeyboardRegionSelectionAndEscapeUseTheWindowInputRoute() => Ocr410Fixture.RunSta(() =>
    {
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            var overlay = window.ImageOverlay;
            _ = overlay.Focus();
            var source = PresentationSource.FromVisual(overlay)!;
            overlay.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                source, Environment.TickCount, System.Windows.Input.Key.Right)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            overlay.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                source, Environment.TickCount, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Assert.NotNull(overlay.Selection);
            Assert.Equal("수량", window.ExtractionText);
            overlay.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                source, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.Null(overlay.Selection);
            Assert.True(window.IsVisible);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void RerunRetainsRegionButNewRequestResetsIt() => Ocr410Fixture.RunSta(() =>
    {
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { ShowInTaskbar = false };
        try
        {
            window.ShowResult(result, "same", image);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            window.ImageOverlay.SetSelection(window.Extraction!.Table.Rows[2].Cells[2].Region);
            window.ShowBusy("same", preserveSelection: true);
            window.ShowResult(result, "same", image, preserveSelection: true);
            Assert.Equal("9,900", window.ExtractionText);
            window.ShowBusy("new");
            window.ShowResult(result, "new", image);
            Assert.Contains("키보드", window.ExtractionText);
            Assert.Null(window.ImageOverlay.Selection);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void ExcelLaunchUsesCompletedLocalFileAndRetainsFileOnHandlerFailure()
    {
        string root = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "MyCapture-Ocr410-" + Guid.NewGuid().ToString("N"));
        root = MyCapture.Core.Storage.ValidatedFilePaths.CreateDirectory(root);
        var table = new OcrTable([new([new("=1+1", default, default)])], [new(false)], false);
        ProcessStartInfo? observed = null;
        string path = OcrExcelExport.Open(table, start =>
        {
            observed = start;
            Assert.True(File.Exists(start.FileName));
            using var zip = ZipFile.OpenRead(start.FileName);
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
        }, root);
        Assert.NotNull(observed);
        Assert.True(observed.UseShellExecute);
        Assert.Empty(observed.Arguments);
        Assert.Equal(path, observed.FileName);
        Assert.EndsWith(".xlsx", path);
        string? failedPath = null;
        Assert.Throws<Win32Exception>(() => OcrExcelExport.Open(table, start =>
        {
            failedPath = start.FileName;
            throw new Win32Exception("Synthetic missing association");
        }, root));
        Assert.NotNull(failedPath);
        Assert.True(File.Exists(failedPath));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class PendingOcr : IOcrService
    {
        private readonly TaskCompletionSource<OcrResult> _completion = new();
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko-KR", "en-US"];
        internal OcrRequest? Request { get; private set; }
        internal CancellationToken Token { get; private set; }
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Token = cancellationToken;
            return _completion.Task;
        }
        internal void Complete() => _completion.SetResult(OcrResult.Success("late", "en-US", [], TimeSpan.Zero));
    }
}
