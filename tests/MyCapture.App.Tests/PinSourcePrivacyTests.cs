using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinSourcePrivacyTests
{
    [Fact]
    public void HoverSourceHidesPrivatePartsButOpenAndCopyKeepTheFullUrl() => StaTestHost.Run(() =>
    {
        const string url = "https://example.com/spec?access_token=secret#private";
        CaptureProvenance source = CaptureProvenance.Create(url);
        BitmapSource image = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 6 * 4], 8 * 4);
        image.Freeze();
        var pin = new PinWindow(PinContent.FromImage(image, provenance: source), new PinViewState(8, 6, 1, 1, 0.1), 0, 0);
        string? opened = null;
        pin.OpenSource = actual => { opened = actual.Url; return Task.FromResult(string.Empty); };
        // Construct the production bar and its handlers without creating desktop windows.
        PinHoverBar bar = pin.CreateHoverBarForTest();
        try
        {
            Assert.Equal("example.com/spec\nhttps://example.com/spec", bar.SourceButton.ToolTip);
            Assert.Equal("https://example.com/spec", AutomationProperties.GetHelpText(bar.SourceButton));
            Assert.DoesNotContain("secret", AutomationProperties.GetName(bar.SourceButton));
            bar.SourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(url, opened);
            MenuItem copy = bar.SourceButton.ContextMenu.Items.OfType<MenuItem>().ElementAt(1);
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(url, Assert.IsType<string>(RecordingClipboard.LastWrite!.Formats[DataFormats.UnicodeText]));

            bar.SetProvenance(CaptureProvenance.None);
            Assert.Equal(Visibility.Collapsed, bar.SourceButton.Visibility);
            Assert.Null(bar.SourceButton.ToolTip);
            Assert.Empty(AutomationProperties.GetHelpText(bar.SourceButton));
        }
        finally { pin.Close(); }
    });
}
