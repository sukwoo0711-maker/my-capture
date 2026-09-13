using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LayerPropertiesDialogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericSourceRangeRejectsInvalidInputAndPreservesLayerData(bool frameMode) => StaTestHost.Run(() =>
    {
        var frame = new FrameEditLayer { StartMs = 200, EndMs = 233.333, Name = "Frame graphic", OverlayPngBase64 = "immutable encoded asset" };
        var text = new TimedTextOverlay { StartMs = 200, EndMs = 1200, Text = "Preserved text", Placement = VideoTextPlacement.Top };
        string original = frameMode ? JsonSerializer.Serialize(frame) : JsonSerializer.Serialize(text);
        var dialog = new TimedTextOverlayDialog(5000, 200, frameMode ? null : text, existingFrame: frameMode ? frame : null);
        dialog.ShowInTaskbar = false;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        Exception? failure = null;
        dialog.Loaded += (_, _) =>
        {
            try
            {
                TextBox start = Descendants(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == UiText.Get("Text_FE872AF40869"));
                TextBox end = Descendants(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == UiText.Get("Text_1882C23FDDB1"));
                Button save = Descendants(dialog).OfType<Button>().Single(button => button.IsDefault);
                start.Text = "00:00:01.250";
                end.Text = "1";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible);
                Assert.Null(dialog.FrameResult);
                Assert.Null(dialog.Result);
                end.Text = "6";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible);
                end.Text = "3.5";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        bool? saved = dialog.ShowDialog();
        if (failure is not null) throw failure;
        Assert.True(saved);
        if (frameMode)
        {
            FrameEditLayer result = Assert.IsType<FrameEditLayer>(dialog.FrameResult);
            Assert.Equal(1250, result.StartMs);
            Assert.Equal(3500, result.EndMs);
            Assert.True(result.IsActiveAt(1250));
            Assert.False(result.IsActiveAt(3500));
            result.StartMs = frame.StartMs; result.EndMs = frame.EndMs;
            Assert.Equal(original, JsonSerializer.Serialize(result));
            Assert.Equal(original, JsonSerializer.Serialize(frame));
        }
        else
        {
            TimedTextOverlay result = Assert.IsType<TimedTextOverlay>(dialog.Result);
            Assert.Equal(1250, result.StartMs);
            Assert.Equal(3500, result.EndMs);
            result.StartMs = text.StartMs; result.EndMs = text.EndMs;
            Assert.Equal(original, JsonSerializer.Serialize(result));
            Assert.Equal(original, JsonSerializer.Serialize(text));
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
