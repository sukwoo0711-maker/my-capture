using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinProvenanceTests
{
    [Fact]
    public void ImageProvenanceDoesNotChangeTheExportUrlAndIsIncludedInRetainedMemory() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        CaptureProvenance source = CaptureProvenance.Create("https://example.test/spec?secret=value#part",
            @"C:\specs\사양서.xlsx", "Excel", @"C:\Office\EXCEL.EXE", "사양서 - Excel");
        PinContent content = PinContent.FromImage(image, provenance: source);
        Assert.Same(source, content.Provenance);
        Assert.Equal(string.Empty, content.SourcePageUrl);
        Assert.True(content.EstimatedRetainedBytes >= PinContent.FromImage(image).EstimatedRetainedBytes
            + ClosedPinMemory.EstimateTextBytes(source.Url)
            + ClosedPinMemory.EstimateTextBytes(source.DocumentPath)
            + ClosedPinMemory.EstimateTextBytes(source.AppName)
            + ClosedPinMemory.EstimateTextBytes(source.AppExecutablePath)
            + ClosedPinMemory.EstimateTextBytes(source.WindowTitle));
        Assert.Equal("https://export.test/old", PinContent.FromImage(image, "https://export.test/old", source).SourcePageUrl);
    });

    [Fact]
    public void RerenderedSessionContentRetainsTheSameProvenance() => StaTestHost.Run(() =>
    {
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\specs\reference.txt", appName: "Notepad");
        var pending = new TaskCompletionSource<CaptureProvenance>();
        PinContent content = PinContent.FromSession(ClipboardTextRenderer.Render("reference", false),
            PinContentKind.Text, "reference", string.Empty, true, PinTextStyle.PlainText, provenance: source)
            with { PendingProvenance = pending.Task };
        PinContent rendered = content.RenderedAt(2);
        Assert.NotSame(content.Image, rendered.Image);
        Assert.Same(source, rendered.Provenance);
        Assert.Equal(content.SourcePageUrl, rendered.SourcePageUrl);
        Assert.Same(pending.Task, rendered.PendingProvenance);
        CaptureProvenance resolved = CaptureProvenance.Create(url: "https://example.test/resolved");
        PinContent enriched = rendered.WithProvenance(resolved);
        Assert.Same(rendered.Image, enriched.Image);
        Assert.Equal(rendered.LayoutWidthDip, enriched.LayoutWidthDip);
        Assert.Equal(rendered.LayoutHeightDip, enriched.LayoutHeightDip);
        Assert.Equal(rendered.OriginalText, enriched.OriginalText);
        Assert.Same(resolved, enriched.Provenance);
        Assert.Null(enriched.PendingProvenance);
    });

    [Fact]
    public async Task ClipboardPngKeepsItsExistingUrlAsProvenanceWithoutReadingTheClipboard()
    {
        const string url = "https://example.test/spec?token=private#section";
        BitmapSource image = Image();
        byte[] png = PngSourceUrlMetadata.WithSourceUrl(ImageCodec.EncodePng(image), url);
        ClipboardImageReader.PinReadAttempt result = await ClipboardImageReader.CompletePinAttemptAsync(
            ClipboardImageReader.CapturedAttempt.Content(png, null, clipboardSequence: 17),
            _ => image,
            (_, _) => throw new InvalidOperationException("Text rendering is not expected."),
            _ => throw new InvalidOperationException("Clipboard fallback is not expected."));
        PinContent content = Assert.IsType<PinContent>(result.Content);
        Assert.Equal(url, content.SourcePageUrl);
        Assert.Equal(url, content.Provenance.Url);
        Assert.Equal(CaptureProvenanceKind.Web, content.Provenance.Kind);
        Assert.DoesNotContain("token", content.Provenance.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardTokenResolvesOnlyTheLatestRegisteredCapture()
    {
        CaptureProvenance first = CaptureProvenance.Create(documentPath: @"C:\specs\first.xlsx", appName: "Excel");
        CaptureProvenance second = CaptureProvenance.Create(documentPath: @"C:\specs\second.xlsx", appName: "Excel");
        string firstToken = ClipboardProvenance.Remember(first);
        Assert.Same(first, ClipboardProvenance.Resolve(firstToken));
        Assert.True(Guid.TryParseExact(firstToken, "N", out _));
        Assert.DoesNotContain("specs", firstToken, StringComparison.Ordinal);
        string secondToken = ClipboardProvenance.Remember(second);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Null(ClipboardProvenance.Resolve(firstToken));
        Assert.Null(ClipboardProvenance.Resolve("unknown-token"));
        Assert.Null(ClipboardProvenance.Resolve(null));
        Assert.Same(second, ClipboardProvenance.Resolve(secondToken));
    }

    [Fact]
    public void PendingClipboardSourceEnrichesOnlyTheCurrentTokenAndFailureKeepsInitialSource()
    {
        var pending = new TaskCompletionSource<CaptureProvenance>();
        string token = ClipboardProvenance.Remember(CaptureProvenance.None, pending.Task);
        Assert.Same(CaptureProvenance.None, ClipboardProvenance.Resolve(token));
        CaptureProvenance resolved = CaptureProvenance.Create(documentPath: @"C:\specs\resolved.xlsx", appName: "Excel");
        pending.SetResult(resolved);
        Assert.True(SpinWait.SpinUntil(() => ReferenceEquals(resolved, ClipboardProvenance.Resolve(token)), TimeSpan.FromSeconds(2)));

        var expiredPending = new TaskCompletionSource<CaptureProvenance>();
        string expiredToken = ClipboardProvenance.Remember(CaptureProvenance.None, expiredPending.Task);
        CaptureProvenance current = CaptureProvenance.Create(appName: "Current app");
        string currentToken = ClipboardProvenance.Remember(current);
        expiredPending.SetResult(resolved);
        Assert.Null(ClipboardProvenance.Resolve(expiredToken));
        Assert.Same(current, ClipboardProvenance.Resolve(currentToken));

        string failedToken = ClipboardProvenance.Remember(current,
            Task.FromException<CaptureProvenance>(new InvalidOperationException("Source lookup failed.")));
        Assert.Same(current, ClipboardProvenance.Resolve(failedToken));
    }

    [Fact]
    public void ClipboardCaptureKeepsTheMatchingTokenSourceWhileDecodingAndIgnoresOtherCopies() => StaTestHost.Run(() =>
    {
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\specs\reference.xlsx", appName: "Excel");
        string token = ClipboardProvenance.Remember(source);
        BitmapSource image = Image();
        var data = new DataObject();
        data.SetData("PNG", ImageCodec.EncodePng(image), autoConvert: false);
        data.SetData(ClipboardProvenance.Format, token, autoConvert: false);
        ClipboardImageReader.CapturedAttempt captured = ClipboardImageReader.CaptureFrom(data, 19,
            () => throw new InvalidOperationException("Clipboard fallback is not expected."));
        Assert.Same(source, captured.Provenance);
        ClipboardProvenance.Remember(CaptureProvenance.Create(appName: "Other app"));
        PinContent content = Assert.IsType<PinContent>(ClipboardImageReader.CompletePinAttemptAsync(captured,
            _ => image,
            (_, _) => throw new InvalidOperationException("Text rendering is not expected."),
            _ => throw new InvalidOperationException("Clipboard fallback is not expected.")).GetAwaiter().GetResult().Content);
        Assert.Same(source, content.Provenance);
        Assert.Equal(string.Empty, content.SourcePageUrl);
        Assert.Null(ClipboardImageReader.CaptureFrom(data, 20, () => null).Provenance);
        var unrelated = new DataObject();
        unrelated.SetData("PNG", ImageCodec.EncodePng(image), autoConvert: false);
        Assert.Null(ClipboardImageReader.CaptureFrom(unrelated, 21, () => null).Provenance);
    });

    [Fact]
    public void HoverSourceButtonIsShownOnlyWithProvenanceAndKeepsItsTooltipInCompactMode() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var empty = new PinHoverBar();
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\specs\사양서.xlsx", appName: "Excel");
        var bar = new PinHoverBar(source);
        try
        {
            Assert.Equal(Visibility.Collapsed, empty.SourceButton.Visibility);
            Assert.Equal(Visibility.Visible, bar.SourceButton.Visibility);
            Assert.Contains(source.DisplayText, (string)bar.SourceButton.ToolTip, StringComparison.Ordinal);
            Assert.Contains(source.DocumentPath, (string)bar.SourceButton.ToolTip, StringComparison.Ordinal);
            Assert.Equal(UiText.Format("Source.OpenAutomation", source.DisplayText), AutomationProperties.GetName(bar.SourceButton));
            Assert.Equal(string.Empty, AutomationProperties.GetAcceleratorKey(bar.SourceButton));
            int opens = 0;
            bar.SourceRequested += () => opens++;
            bar.Compact = true;
            bar.SourceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, opens);
            Assert.Equal(Visibility.Visible, bar.SourceButton.Visibility);
        }
        finally { empty.Close(); bar.Close(); }
    });

    [Fact]
    public void PinSourceActionUsesTheInjectedOpenerAndReportsItsStatus() => StaTestHost.Run(() =>
    {
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\specs\reference.txt", appName: "Notepad");
        var pin = new PinWindow(PinContent.FromImage(Image(), provenance: source), new PinViewState(8, 6, 1, 1, 0.1), 0, 0);
        int opens = 0;
        pin.OpenSource = actual =>
        {
            Assert.Same(source, actual);
            opens++;
            return Task.FromResult("원본을 찾을 수 없습니다.");
        };
        try
        {
            pin.OpenSourceAsync().GetAwaiter().GetResult();
            Assert.Equal(1, opens);
            Assert.Equal("원본을 찾을 수 없습니다.", pin.FeedbackForTest.Text);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void DirectClipboardAndPinCopyRoutesKeepProvenanceWithoutShowingWindows() => StaTestHost.Run(() =>
    {
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\specs\reference.txt", appName: "Notepad");
        CaptureProvenance? copiedSource = null;
        var manager = new PinManager(() => new PinSettings(), saveService: null,
            (_, _, _, provenance) => { copiedSource = provenance; return Task.FromResult(true); },
            _ => Task.FromResult(true), NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => new MonitorInfo("fake", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), 96, true),
            ShowPinForTest = (_, _) => { },
            ActivatePinForTest = _ => { },
        };
        try
        {
            PinWindow direct = manager.PinImage(Image(), source);
            Assert.Same(source, direct.Provenance);
            Assert.Equal(string.Empty, direct.SourcePageUrl);
            MenuItem copy = Assert.Single(direct.ContextMenu.Items.OfType<MenuItem>(), item => item.InputGestureText == "Ctrl+C");
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(source, copiedSource);
            PinContent clipboard = PinContent.FromImage(Image(), "https://example.test/clipboard");
            Assert.Equal(PasteResult.Pinned, manager.PasteFromClipboardAsync(
                () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(clipboard))).GetAwaiter().GetResult());
            Assert.Contains(manager.SnapshotOpenPins(), pin => ReferenceEquals(pin.Content.Provenance, clipboard.Provenance));
        }
        finally { manager.CloseAllForExit(); }
    });

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 6 * 4], 8 * 4);
        image.Freeze();
        return image;
    }
}
