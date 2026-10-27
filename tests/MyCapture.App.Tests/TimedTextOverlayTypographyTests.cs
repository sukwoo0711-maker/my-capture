using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TimedTextOverlayTypographyTests
{
    [Fact]
    public void CaptionInputUsesTheExportTypeface() => StaTestHost.Run(() =>
    {
        var dialog = new TimedTextOverlayDialog(5000, 0);
        try
        {
            var input = (TextBox)typeof(TimedTextOverlayDialog).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Assert.Same(TimedTextOverlayRenderer.OverlayTypeface.FontFamily, input.FontFamily);
            Assert.Equal(TimedTextOverlayRenderer.OverlayTypeface.Weight, input.FontWeight);
            Assert.Equal(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag, input.Language.IetfLanguageTag, ignoreCase: true);
        }
        finally { dialog.Close(); }
    });
}
