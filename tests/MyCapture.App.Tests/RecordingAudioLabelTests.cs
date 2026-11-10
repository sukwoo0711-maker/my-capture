using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioLabelTests
{
    public static IEnumerable<object[]> States()
    {
        foreach (string culture in new[] { "ko-KR", "en-US" })
        foreach (string state in new[] { "ready", "recording", "paused", "saving", "saved" })
        foreach (double scale in new[] { 1d, 1.5d, 2d })
            yield return [culture, state, scale];
    }

    [Theory]
    [MemberData(nameof(States))]
    public void LabelIsReadableAndAccessibleInEveryState(string culture, string state, double scale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        var window = RecordingAudioEvidenceTests.Create(state);
        try
        {
            if (state == "saved") window.PreviewSavedForTest(TimeSpan.FromSeconds(12));
            if (state == "saving") Assert.False(window.PauseButtonForTest.IsEnabled);
            Border strip = RecordingAudioEvidenceTests.Field<Border>(window, "_controlStrip");
            TextBlock label = RecordingAudioEvidenceTests.Field<TextBlock>(window, "_audioStatusText");
            // Physical strip dimensions stay fixed while WPF uses fewer DIPs at higher DPI.
            strip.Width /= scale;
            strip.Height /= scale;
            strip.Measure(new Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            string expected = culture == "ko-KR" ? "소리 없음" : "No audio";
            Assert.Equal(expected, label.Text);
            Assert.Equal(expected, AutomationProperties.GetName(label));
            var peer = new TextBlockAutomationPeer(label);
            Assert.Equal(expected, peer.GetName());
            Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
            Assert.False(label.Focusable);
            Assert.Equal(Visibility.Visible, label.Visibility);
            Assert.Equal(TextTrimming.None, label.TextTrimming);
            var natural = new TextBlock
            {
                Text = expected, FontFamily = label.FontFamily, FontSize = label.FontSize,
                FontWeight = label.FontWeight, UseLayoutRounding = label.UseLayoutRounding,
            };
            TextOptions.SetTextFormattingMode(natural, TextOptions.GetTextFormattingMode(label));
            natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(label.ActualWidth >= natural.DesiredSize.Width,
                $"Localized text must fit without trimming: actual {label.ActualWidth}, natural {natural.DesiredSize.Width}, desired {label.DesiredSize.Width}.");
            Rect bounds = label.TransformToAncestor(strip).TransformBounds(new Rect(label.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= strip.ActualWidth);
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= strip.ActualHeight);
        }
        finally { window.Close(); }
    });
}
