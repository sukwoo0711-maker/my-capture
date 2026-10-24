using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Repairs measured in the 2026-10-24 translation probe: real engine outputs put back to the
/// source's identifiers and numbers, and the values that cannot be repaired left as warnings.
/// </summary>
public sealed class TranslationProtectedTokensTests
{
    [Theory]
    // OPUS-MT misspelled a register bit name.
    [InlineData("Bit 3 OVR: Overrun error. This bit is cleared by writing 1 to the OVRCF bit in the ADC_ICR register.",
        "비트 3 OVR: 오버런 오류. 이 비트는 ADC_ICR 레지스터의 OCRCF 비트에 1을 기록하여 지워집니다.",
        "비트 3 OVR: 오버런 오류. 이 비트는 ADC_ICR 레지스터의 OVRCF 비트에 1을 기록하여 지워집니다.")]
    // OPUS-MT changed the case of a quoted type name.
    [InlineData("CS0246: The type or namespace name 'OcrEngine' could not be found (are you missing a using directive or an assembly reference?)",
        "CS0246: 형식 또는 네임스페이스 이름 'Ocrengine'을 찾을 수 없습니다(사용 지침이나 어셈블리 참조가 누락되었습니까?)",
        "CS0246: 형식 또는 네임스페이스 이름 'OcrEngine'을 찾을 수 없습니다(사용 지침이나 어셈블리 참조가 누락되었습니까?)")]
    // Bergamot translated half of an identifier.
    [InlineData("TimeoutError: Request to https://api.example.com/v2/status timed out after 30000 ms.",
        "시간outError: https://api.example.com/v2/status에 대한 요청은 30000 ms 후에 시간을 내었습니다.",
        "TimeoutError: https://api.example.com/v2/status에 대한 요청은 30000 ms 후에 시간을 내었습니다.")]
    // Bergamot dropped a digit; the only stray number of the same shape is put back.
    [InlineData("Improved startup time by 35% on Windows 10 22H2 (build 19045).",
        "Windows 10 22H2에서 35 %의 향상된 시작 시간 (빌드 1904).",
        "Windows 10 22H2에서 35%의 향상된 시작 시간 (빌드 19045).")]
    // A tolerance with a different sign and spacing.
    [InlineData("The internal clock IRC8M is accurate to ±1% (25 °C).",
        "내부 고속 클럭 IRC8M은 ~ 1 % (25 °C)의 주파수 정확도를 가지고 있습니다.",
        "내부 고속 클럭 IRC8M은 ±1% (25 °C)의 주파수 정확도를 가지고 있습니다.")]
    // TranslateGemma escaped an underscore like Markdown and added backticks.
    [InlineData("This bit is set by hardware when the content of the USART_TDR register has been transferred.",
        "이 비트는 `USART\\_TDR` 레지스터의 내용이 전송되면 하드웨어가 설정합니다.",
        "이 비트는 USART_TDR 레지스터의 내용이 전송되면 하드웨어가 설정합니다.")]
    // Micro sign and spacing of a unit.
    [InlineData("The device consumes 2.1 µA in Stop mode.", "이 장치는 Stop 모드에서 2.1μA를 소비합니다.",
        "이 장치는 Stop 모드에서 2.1 µA를 소비합니다.")]
    public void Repair_PutsBackChangedTokens(string source, string output, string expected)
    {
        (string repaired, int repairs) = ProtectedTokens.Repair(source, output);

        Assert.Equal(expected, repaired);
        Assert.True(repairs > 0);
        ProtectedTokenCheck check = ProtectedTokens.Check(source, repaired);
        Assert.Empty(check.Altered);
        Assert.Empty(check.Invented);
    }

    [Fact]
    public void Repair_LeavesAPreservedTranslationUnchanged()
    {
        const string Source = "Bit 0 EN: Counter enable. 0: Counter disabled. 1: Counter enabled.";
        const string Output = "비트 0 EN: 카운터 사용. 0: 카운터 사용 안 함. 1: 카운터 사용.";

        Assert.Equal((Output, 0), ProtectedTokens.Repair(Source, Output));
        ProtectedTokenCheck check = ProtectedTokens.Check(Source, Output);
        Assert.Equal(check.Checked, check.Preserved);
    }

    [Fact]
    public void Check_ReportsAnOmittedAcronymAndAnInventedValue()
    {
        const string Source = "When VDD drops below the BOR threshold of 2.7 V, the device is held in reset.";
        const string Output = "VDD 전압이 2.7V 이하로 떨어지면 60fps 장치는 재설정 상태로 유지됩니다.";

        // "2.7V" for "2.7 V" is only a spacing change, which the repair puts back.
        Assert.Contains(ProtectedTokens.Check(Source, Output).FormatChanged,
            issue => issue.Token == "2.7 V" && issue.Change == ProtectedTokenChange.Spacing);
        (string repaired, _) = ProtectedTokens.Repair(Source, Output);
        ProtectedTokenCheck check = ProtectedTokens.Check(Source, repaired);

        Assert.Contains("2.7 V", repaired, StringComparison.Ordinal);
        Assert.Equal("BOR", Assert.Single(check.Altered).Token);
        Assert.Equal("60fps", Assert.Single(check.Invented).Token);
    }

    [Theory]
    [InlineData("rw", false)]
    [InlineData("CKD[1:0]", false)]
    [InlineData("0x0000_0000", false)]
    [InlineData("15:10", false)]
    [InlineData("USART_TDR", false)]
    [InlineData("Reserved", true)]
    [InlineData("Counter enable. 0: Counter disabled.", true)]
    public void HasTranslatableWords_SkipsIdentifiersNumbersAndAccessCodes(string text, bool expected) =>
        Assert.Equal(expected, ProtectedTokens.HasTranslatableWords(text));

    [Fact]
    public void Find_KeepsIdentifiersNextToKoreanParticles()
    {
        IReadOnlyList<ProtectedSpan> spans = ProtectedTokens.Find("irq_status를 0x1F로 바꾸면 Ctrl+Shift+C가 동작합니다");

        Assert.Equal(["irq_status", "0x1F", "Ctrl+Shift+C"], spans.Select(span => span.Text));
        Assert.Equal(["ident", "hex", "key"], spans.Select(span => span.Kind));
    }
}
