using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Developer notations that look like everyday values (a bit range like a clock time, a number-base header like a
/// month, "AI" as analog input, "OK" as a status) keep their exact comparison; everyday text keeps its equivalences.
/// Also the compound and everyday counts and the output-side units that pair with source number words.
/// </summary>
public sealed class TranslationValueGuardTests
{
    private static bool Accepted(string source, string output)
    {
        ProtectedTokenCheck check = ProtectedTokens.Check(source, output);
        return check.Altered.Count == 0 && check.Invented.Count == 0;
    }

    [Theory]
    // G1: a bare colon pair in the source is a clock time only with a cue.
    [InlineData("Bits 15:10 select the clock source.", "비트 15시 10분은 클럭 소스를 선택합니다.")]
    [InlineData("Bits 11:10 are reserved.", "비트 11시 10분은 예약되어 있습니다.")]
    [InlineData("a 16:10 display", "16시 10분 디스플레이")]
    [InlineData("See John 3:16.", "요한복음 3시 16분을 보십시오.")]
    // "and" and "to" join two times only when one of them is a time on its own; two bit ranges stay ranges.
    [InlineData("Bits 23:16 and 15:10 are reserved.", "비트 23시 16분과 15시 10분은 예약되어 있습니다.")]
    [InlineData("Shift bits 23:16 to 15:10.", "비트를 23시 16분에서 15시 10분으로 옮깁니다.")]
    [InlineData("The store opens 9:30 to 18:00.", "가게는 9시 30분부터 18시까지 엽니다.")]
    // G2: an abbreviated month name is a month only next to a day or a year.
    [InlineData("Dec", "12월")]
    [InlineData("Oct", "10월")]
    [InlineData("Sep", "9월")]
    [InlineData("Hex Dec Oct", "16진수 12월 10월")]
    // G3: listed acronyms pair only in a sentence and never next to a hyphen.
    [InlineData("AI", "인공지능")]
    [InlineData("AI input range", "인공지능 입력 범위")]
    [InlineData("Set the en-US locale.", "미국 로캘을 설정합니다.")]
    [InlineData("Use the US-ASCII table here.", "여기에서 미국-ASCII 표를 사용합니다.")]
    // G4: OK as a status code or alone.
    [InlineData("Returns OK on success.", "성공하면 확인을 반환합니다.")]
    [InlineData("HTTP 200 OK", "HTTP 200 확인")]
    [InlineData("OK", "확인")]
    [InlineData("The status is OK: no errors.", "상태가 좋습니다: 오류가 없습니다.")]
    [InlineData("Is it OK?", "확인입니까?")]
    public void Guard_KeepsDeveloperNotationsStrict(string source, string output) =>
        Assert.False(Accepted(source, output));

    [Theory]
    [InlineData("The train leaves at 14:30.", "기차는 14시 30분에 출발합니다.")]
    [InlineData("We are open from 9:00 to 17:30.", "9시부터 17시 30분까지 영업합니다.")]
    [InlineData("Hours: 9:00–17:30", "영업 시간: 9시–17시 30분")]
    [InlineData("The store opens at 9:30 and closes at 18:00.", "가게는 9시 30분에 열고 18시에 닫습니다.")]
    [InlineData("15시 10분에 시작합니다.", "It starts at 15:10.")]
    [InlineData("The sale ends on Dec 12.", "판매는 12월 12일에 끝납니다.")]
    [InlineData("Due 12 Dec", "12월 12일 마감")]
    [InlineData("Released in Dec. 2025", "2025년 12월에 출시")]
    [InlineData("AI tools are popular.", "인공지능 도구가 인기 있습니다.")]
    [InlineData("미국 시장이 커졌습니다.", "The US market grew.")]
    [InlineData("Click OK to continue.", "확인을 클릭하여 계속하십시오.")]
    [InlineData("Press the OK button.", "확인 단추를 누르십시오.")]
    [InlineData("Is it OK?", "괜찮습니까?")]
    // E1: compound counts.
    [InlineData("The hall holds three hundred people.", "홀은 300명을 수용합니다.")]
    [InlineData("It took one hundred and five days.", "105일이 걸렸습니다.")]
    [InlineData("He walked nine thousand steps.", "그는 9000걸음을 걸었습니다.")]
    [InlineData("He ran nine thousand steps today.", "오늘 하루 9천 걸음을 걸었다.")]
    [InlineData("The hall holds three hundred people.", "홀은 3백 명을 수용합니다.")]
    // Korean day counts.
    [InlineData("The package arrived 2 days late.", "패키지는 이틀 늦게 도착했습니다.")]
    [InlineData("It took 1 day.", "하루가 걸렸습니다.")]
    // E2: everyday singular forms.
    [InlineData("It takes an hour.", "1시간이 걸립니다.")]
    [InlineData("Take it twice a day.", "하루에 2번 복용하십시오.")]
    [InlineData("Wait a couple of days.", "2일 기다리십시오.")]
    [InlineData("Buy a dozen eggs.", "달걀 12개를 사십시오.")]
    [InlineData("Click once.", "1번 클릭하십시오.")]
    [InlineData("Wait a second.", "1초 기다리십시오.")]
    // E3: a unit or currency on the output digit.
    [InlineData("Prices rose fifty percent.", "가격이 50% 올랐습니다.")]
    [InlineData("We walked ten kilometers.", "우리는 10km를 걸었습니다.")]
    [InlineData("It costs five dollars.", "가격은 $5입니다.")]
    public void EverydayValues_AreAccepted(string source, string output) =>
        Assert.True(Accepted(source, output));

    [Theory]
    [InlineData("The hall holds three hundred people.", "홀은 400명을 수용합니다.")]
    [InlineData("The hall holds three hundred people.", "홀은 3명을 수용합니다.")]
    [InlineData("It takes an hour.", "2시간이 걸립니다.")]
    [InlineData("Buy a book.", "책 1권을 사십시오.")]
    [InlineData("He ran nine thousand steps today.", "오늘 8천 걸음을 걸었다.")]
    [InlineData("He ran 9000 steps today.", "오늘 9천 걸음을 걸었다.")]
    [InlineData("The package arrived 2 days late.", "패키지는 사흘 늦게 도착했습니다.")]
    [InlineData("Prices rose fifty percent.", "가격이 15% 올랐습니다.")]
    [InlineData("Use 2 kg of flour.", "밀가루 두 kg을 사용합니다.")]
    public void ChangedEverydayValues_AreRejected(string source, string output) =>
        Assert.False(Accepted(source, output));

    /// <summary>
    /// G5: for developer text nothing is paired, so <see cref="ProtectedTokens.Check"/> compares exactly as before the
    /// equivalences existed (no masking, no covered span, no plain abbreviation); the expected verdicts are main's.
    /// </summary>
    [Theory]
    [InlineData("Set bit 7 of REG_CTRL to 1.", "REG_CTRL의 비트 7을 1로 설정합니다.", true)]
    [InlineData("Write 0x1F to the CR1 register.", "CR1 레지스터에 0x1F를 씁니다.", true)]
    [InlineData("Bits [15:10] select the clock source.", "비트 [15:10]은 클럭 소스를 선택합니다.", true)]
    [InlineData("Bits 15:10 select the clock source.", "비트 15:10은 클럭 소스를 선택합니다.", true)]
    [InlineData("CKD[1:0]: Clock division", "CKD[1:0]: 클럭 분할", true)]
    [InlineData("Requires version 2.4.1 or later.", "버전 2.4.1 이상이 필요합니다.", true)]
    [InlineData("Run the tool with --dry-run.", "--dry-run으로 도구를 실행합니다.", true)]
    [InlineData(@"The file is in C:\tools\bin.", @"파일은 C:\tools\bin에 있습니다.", true)]
    [InlineData("The supply is 3.3 V and the clock is 48 MHz.", "공급 전압은 3.3 V이고 클럭은 48 MHz입니다.", true)]
    [InlineData("The supply is 3.3 V.", "공급 전압은 3 V입니다.", false)]
    [InlineData("Write 0x1F to CR1.", "CR1에 0x1E를 씁니다.", false)]
    [InlineData("Bits 15:10 select the clock source.", "비트 15~11은 클럭 소스를 선택합니다.", false)]
    [InlineData("Bits 15:10 select the clock source.", "비트 15시 10분은 클럭 소스를 선택합니다.", false)]
    [InlineData("Set GPIO 5 high.", "핀 5를 높게 설정합니다.", false)]
    [InlineData("The SPI clock runs at 8 MHz.", "SPI 클럭은 8 kHz로 동작합니다.", true)] // main reports a changed unit as a format change
    [InlineData("Configure the CAN bus.", "버스를 구성합니다.", false)]
    [InlineData("Field DEC selects decimal output.", "필드 12월은 십진 출력을 선택합니다.", false)]
    [InlineData("Returns OK or ERR.", "확인 또는 ERR을 반환합니다.", false)]
    [InlineData("Timeout is 500 ms (default).", "시간 제한은 500 ms(기본값)입니다.", true)]
    public void DeveloperText_IsComparedExactlyAsBefore(string source, string output, bool accepted)
    {
        Assert.True(ValueEquivalence.Match(source, output).IsEmpty);
        Assert.DoesNotContain(ProtectedTokens.Find(source), ValueEquivalence.IsPlainAbbreviation);
        Assert.Equal(accepted, Accepted(source, output));
    }
}
