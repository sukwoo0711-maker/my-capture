using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Structural accessibility guarantees for the transient delayed-capture surface. The countdown
/// must keep one numeric focal point, use Korean context instead of decorative English copy, and
/// expose every tick as a polite live-region update.
/// </summary>
public sealed class CountdownWindowAccessibilityTests
{
    [Fact]
    public void CountdownUsesKoreanContextAndPoliteLiveRegion()
    {
        StaTestHost.Run(() =>
        {
            var window = new CountdownWindow(3);
            try
            {
                Border frame = Assert.IsType<Border>(window.Content);
                StackPanel content = Assert.IsType<StackPanel>(frame.Child);
                TextBlock[] labels = content.Children.OfType<TextBlock>().ToArray();

                Assert.Equal(3, labels.Length);
                Assert.Equal("3", labels[0].Text);
                Assert.Equal("초 후 캡처됩니다", labels[1].Text);
                Assert.Equal("Esc로 취소", labels[2].Text);
                Assert.DoesNotContain(labels, label =>
                    label.Text.Contains("DELAYED CAPTURE", StringComparison.OrdinalIgnoreCase));

                Assert.Equal(
                    "지연 캡처 카운트다운",
                    AutomationProperties.GetName(labels[0]));
                Assert.Equal(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(labels[0]));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
