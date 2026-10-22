using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorSourceProvenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceChipUsesConfirmedContextAndDoesNotEnableTheOptionalTitleBadge(bool hasSource) => StaTestHost.Run(() =>
    {
        var source = hasSource ? DocumentSource() : CaptureProvenance.None;
        BitmapSource image = Image();
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        Func<bool>? prior = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => false;
        try
        {
            var editor = new AnnotationEditorControl(new FrozenFrame(image, region, null, 0), region, image,
                null, null, sourceWindowTitle: source.WindowTitle, provenance: source);
            Button[] chips = Descendants(editor).OfType<Button>()
                .Where(button => AutomationProperties.GetAutomationId(button) == "CaptureSourceChip").ToArray();
            Assert.Equal(hasSource ? 1 : 0, chips.Length);
            if (hasSource)
            {
                Button chip = Assert.Single(chips);
                Assert.True(chip.IsTabStop);
                Assert.Equal(320, chip.MaxWidth);
                Assert.Contains("report.xlsx", AutomationProperties.GetName(chip));
                Assert.Equal(source.Tooltip, chip.ToolTip);
                Assert.Equal(3, chip.ContextMenu.Items.OfType<MenuItem>().Count());
            }
            var surface = (AnnotationEditorSurface)typeof(AnnotationEditorControl)
                .GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            Assert.Null(surface.SourceWindowTitle);
        }
        finally { AnnotationEditorPreferences.ReadShowSourceWindowTitle = prior; }
    });

    [Fact]
    public void LateCaptureReadAddsChipWithoutWaitingToConstructTheEditor() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        var region = new RectD(0, 0, 8, 8);
        var pending = new TaskCompletionSource<CaptureSelectionSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selection = new CaptureSelectionCompletedEventArgs(new FrozenFrame(image, region, null, 0), region, image)
        { PendingSource = pending.Task };
        Task<CaptureProvenance> resolved = CaptureSelectionProvenance.Pending(selection)!;
        var editor = new AnnotationEditorControl(selection.Frame, region, image, null, null,
            provenance: CaptureSelectionProvenance.Current(selection), pendingProvenance: resolved);
        Assert.DoesNotContain(Descendants(editor).OfType<Button>(), button => button.Name == "CaptureSourceChip");
        pending.SetResult(new CaptureSelectionSource("report.xlsx - Excel", "", new CaptureSourceDetails(
            "Excel", @"C:\Apps\excel.exe", @"C:\Specs\report.xlsx")));
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        timer.Tick += (_, _) => frame.Continue = !Descendants(editor).OfType<Button>().Any(button => button.Name == "CaptureSourceChip")
            && DateTime.UtcNow < deadline;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Button chip = Assert.Single(Descendants(editor).OfType<Button>(), button => button.Name == "CaptureSourceChip");
        Assert.Contains("report.xlsx", AutomationProperties.GetName(chip));
    });

    [Fact]
    public async Task FailedEnrichmentKeepsTheConfirmedSource()
    {
        BitmapSource image = Image();
        var region = new RectD(0, 0, 8, 8);
        var selection = new CaptureSelectionCompletedEventArgs(new FrozenFrame(image, region, null, 0), region, image,
            sourcePageUrl: "https://example.com/spec")
        { PendingSource = Task.FromException<CaptureSelectionSource>(new InvalidOperationException("read failed")) };
        CaptureProvenance source = await CaptureSelectionProvenance.Pending(selection)!;
        Assert.Equal("example.com/spec", source.DisplayText);
    }

    [Fact]
    public void GalleryEditorUsesItsOwnRecord() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        var region = new RectD(0, 0, 8, 8);
        var record = new CaptureRecord { SourcePageUrl = "https://www.example.com/spec?token=hidden#section" };
        var context = new GalleryReeditContext(record, new FrozenFrame(image, region, null, 0), region, image,
            AnnotationDocument.CreateFor(8, 8), new Dictionary<string, BitmapSource>());
        var window = new GalleryEditorWindow(context);
        try
        {
            Button chip = Assert.Single(Descendants(window.Editor).OfType<Button>(), button => button.Name == "CaptureSourceChip");
            Assert.Contains("example.com/spec", AutomationProperties.GetName(chip));
            Assert.DoesNotContain("hidden", AutomationProperties.GetName(chip));
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task ReceiptPinUsesTheCurrentRenderedPixelsWithTheRecordProvenance()
    {
        BitmapSource image = Image();
        PinContent? pinned = null;
        var record = new CaptureRecord { SourceDocumentPath = @"C:\Specs\report.xlsx", SourceAppName = "Excel" };
        var actions = new QuickAccessImageActions(() => Task.FromResult(image), _ => Task.FromResult(true),
            bitmap => pinned = PinContent.FromImage(bitmap, provenance: CaptureProvenance.FromRecord(record)),
            _ => Task.FromResult(true));
        await actions.PinAsync();
        Assert.NotNull(pinned);
        Assert.Same(image, pinned.Image);
        Assert.Equal(record.SourceDocumentPath, pinned.Provenance.DocumentPath);
    }

    [Fact]
    public void ReceiptKeepsStatusAndHidesUrlSecrets()
    {
        var source = CaptureProvenance.Create(url: "https://www.example.com/spec?token=hidden#section");
        Assert.Equal("Saved · example.com/spec", CaptureSelectionProvenance.ReceiptText("Saved", source));
        Assert.Equal("Saved", CaptureSelectionProvenance.ReceiptText("Saved", CaptureProvenance.None));
    }

    private static CaptureProvenance DocumentSource() => CaptureProvenance.Create(documentPath: @"C:\Specs\report.xlsx",
        appName: "Excel", appExecutablePath: @"C:\Apps\excel.exe", windowTitle: "report.xlsx - Excel");

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        image.Freeze();
        return image;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependency) continue;
            yield return dependency;
            foreach (DependencyObject nested in Descendants(dependency)) yield return nested;
        }
    }
}
