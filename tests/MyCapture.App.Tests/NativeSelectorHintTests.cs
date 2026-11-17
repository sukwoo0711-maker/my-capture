using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The hint chip reads the existing resource strings: keyboard keys become key caps, mouse words
/// stay words, and toasts are shown exactly as given.
/// </summary>
public sealed class NativeSelectorHintTests
{
    [Fact]
    public void KeyboardKeysBecomeKeyCapsAndMouseWordsKeepTheirColon() => StaTestHost.Run(() =>
    {
        var hint = new NativeSelectorHint(CaptureGuidePalette.Create(AppTheme.Midnight, null, false));
        Assert.True(hint.Show("드래그: 영역 캡처  ·  Esc: 취소"));
        Assert.Equal(["Esc"], hint.KeycapsForTest);
        // A key cap already sets its key apart, so its colon goes; a word keeps its colon.
        Assert.Equal(["드래그:", "영역 캡처", "·", "취소"], Words(hint));
        Assert.Equal("드래그: 영역 캡처  ·  Esc: 취소", AutomationProperties.GetName(hint));
        Assert.Equal("드래그: 영역 캡처  ·  Esc: 취소", hint.Text);

        Assert.True(hint.Show("Drag: Capture region  ·  Click: Capture window  ·  Esc: Cancel"));
        Assert.Equal(["Esc"], hint.KeycapsForTest);
        Assert.Equal(["Drag:", "Capture region", "·", "Click:", "Capture window", "·", "Cancel"], Words(hint));

        Assert.True(hint.Show("클릭 또는 Enter: 색상 복사  ·  Esc: 취소"));
        Assert.Equal(["Enter", "Esc"], hint.KeycapsForTest);
        Assert.Equal(["클릭 또는", "색상 복사", "·", "취소"], Words(hint));

        // "Key · action" keeps its own dot.
        Assert.True(hint.Show("Shift · 픽셀 정밀 이동"));
        Assert.Equal(["Shift"], hint.KeycapsForTest);
        Assert.Equal(["·", "픽셀 정밀 이동"], Words(hint));

        Assert.True(hint.Show("포인터로 자유형 그리기 · 놓으면 캡처"));
        Assert.Empty(hint.KeycapsForTest);
        Assert.Equal(["포인터로 자유형 그리기", "·", "놓으면 캡처"], Words(hint));

        Assert.True(hint.Show("Ctrl+Shift+S: 다시 캡처"));
        Assert.Equal(["Ctrl+Shift+S"], hint.KeycapsForTest);
    });

    [Fact]
    public void ToastsArePlainTextAndUnchangedTextKeepsTheLayout() => StaTestHost.Run(() =>
    {
        var hint = new NativeSelectorHint(CaptureGuidePalette.Create(AppTheme.Daylight, null, false));
        Assert.True(hint.Show("Esc: 취소"));
        Assert.False(hint.Show("Esc: 취소"));
        Assert.True(hint.Show("Esc: 취소", plain: true));
        Assert.Empty(hint.KeycapsForTest);
        Assert.Equal(["Esc: 취소"], Words(hint));
        Assert.False(hint.Show("Esc: 취소", plain: true));
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void ChipKeyCapsAndWordsUseTheirTokensSizesAndPixelEdges(double scale) => StaTestHost.Run(() =>
    {
        IReadOnlyDictionary<string, Brush> palette = CaptureGuidePalette.Create(AppTheme.Midnight, null, false);
        var hint = new NativeSelectorHint(palette);
        hint.SetScale(scale);
        hint.Show("드래그: 영역 캡처  ·  Esc: 취소");
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(NativeSelectorHint.ChipHeight, hint.DesiredSize.Height);
        Assert.Equal(new Thickness(NativeSelectorHint.PaddingX, 0, NativeSelectorHint.PaddingX, 0), hint.Padding);
        Assert.Equal(Color(palette, "CaptureGuide.ChipSurface"), ((SolidColorBrush)hint.Background).Color);
        Assert.Equal(1 / scale, hint.BorderThickness.Left, 8);

        Border cap = Assert.Single(hint.KeycapBordersForTest);
        Assert.Equal(NativeSelectorHint.KeycapHeight, cap.Height);
        Assert.Equal(new CornerRadius(4), cap.CornerRadius);
        Assert.Equal(1 / scale, cap.BorderThickness.Left, 8);
        Assert.Equal(Color(palette, "CaptureGuide.KeycapSurface"), ((SolidColorBrush)cap.Background).Color);
        Assert.Equal(Color(palette, "CaptureGuide.ChipHairline"), ((SolidColorBrush)cap.BorderBrush).Color);
        var capText = Assert.IsType<TextBlock>(cap.Child);
        Assert.Equal(TypeScale.Micro, capText.FontSize);
        Assert.Equal(FontWeights.SemiBold, capText.FontWeight);

        foreach (TextBlock word in hint.WordsForTest)
        {
            // Korean words never take the Micro size.
            Assert.Equal(TypeScale.Caption, word.FontSize);
            Assert.Equal(FontWeights.Medium, word.FontWeight);
        }
        TextBlock dot = hint.WordsForTest.Single(word => word.Text == "·");
        Assert.Equal(Color(palette, "CaptureGuide.MutedText"), ((SolidColorBrush)dot.Foreground).Color);
        Assert.Equal(1, dot.Opacity, 8);
        Assert.All(hint.WordsForTest.Where(word => word.Text != "·"),
            word => Assert.Equal(Color(palette, "CaptureGuide.Text"), ((SolidColorBrush)word.Foreground).Color));

        // A DWM-drawn window edge replaces the chip's own; key caps keep theirs.
        hint.Outlined = false;
        Assert.Equal(0, hint.BorderThickness.Left);
        Assert.Equal(1 / scale, cap.BorderThickness.Left, 8);
        hint.Outlined = true;
        Assert.Equal(1 / scale, hint.BorderThickness.Left, 8);
    });

    private static string[] Words(NativeSelectorHint hint) => hint.WordsForTest.Select(word => word.Text).ToArray();

    private static Color Color(IReadOnlyDictionary<string, Brush> palette, string key) => ((SolidColorBrush)palette[key]).Color;
}
