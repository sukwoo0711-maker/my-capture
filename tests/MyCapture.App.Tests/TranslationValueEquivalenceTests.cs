using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A value written another way on the other side of a translation is the same value; a value that really changed
/// is not. Outputs are those of opus-mt-en-ko-1 on the 2026-11-13 easy-sentence table where one exists.
/// </summary>
public sealed class TranslationValueEquivalenceTests
{
    private static bool Accepted(string source, string output)
    {
        ProtectedTokenCheck check = ProtectedTokens.Check(source, output);
        return check.Altered.Count == 0 && check.Invented.Count == 0;
    }

    [Theory]
    // Number words and digits, both directions.
    [InlineData("The store opens at nine and closes at six.", "가게는 9시에 문을 열고 6시에 문을 닫습니다.")]
    [InlineData("You can borrow up to five books at a time.", "한 번에 최대 5 권의 책을 빌릴 수 있습니다.")]
    [InlineData("The children played on the grass for two hours.", "아이들은 풀밭에서 2시간 동안 놀았습니다.")]
    [InlineData("It happened on the third day.", "3일째에 일어났습니다.")]
    [InlineData("We waited for twenty-one days.", "우리는 21일을 기다렸습니다.")]
    [InlineData("two cats and two dogs", "고양이 2마리와 개 2마리")]
    [InlineData("Add 2 items to the list.", "목록에 항목 두 개를 추가합니다.")]
    [InlineData("Wait 12 hours.", "열두 시간 기다리십시오.")]
    [InlineData("You have 3 new messages.", "새 메시지가 세 개 있습니다.")]
    [InlineData("책 5권을 빌릴 수 있습니다.", "You can borrow five books.")]
    [InlineData("책 다섯 권을 빌릴 수 있습니다.", "You can borrow 5 books.")]
    [InlineData("사과 하나와 배 둘", "1 apple and 2 pears")]
    [InlineData("9 cats and nine dogs", "고양이 9마리와 개 9마리")]
    // Months.
    [InlineData("Version 5.3.1 was released on November 12.", "버전 5.3.1은 11월 12일에 출시되었다.")]
    [InlineData("The sale ends on Jan. 5.", "판매는 1월 5일에 끝납니다.")]
    [InlineData("We moved in May.", "우리는 5월에 이사했습니다.")]
    [InlineData("버전 5.3.1은 11월 12일에 출시되었습니다.", "Version 5.3.1 was released on November 12.")]
    // Clock times.
    [InlineData("The meeting is on March 3, 2025 at 2:30 PM.", "회의는 2025년 3월 3일 오후 2시 30분에 있습니다.")]
    [InlineData("Dr. Smith will see you at 10 a.m. on Monday.", "스미스 박사는 월요일 오전 10시에 만나요.")]
    [InlineData("Call me before 6 p.m.", "오후 6시 전에 전화하십시오.")]
    [InlineData("The train leaves at 14:30.", "기차는 오후 2시 30분에 출발합니다.")]
    [InlineData("The train leaves at 14:30.", "기차는 14시 30분에 출발합니다.")]
    [InlineData("It starts at 3:30.", "3시 반에 시작합니다.")]
    [InlineData("It starts at 9:00.", "9시에 시작합니다.")]
    [InlineData("It starts at nine a.m.", "오전 9시에 시작합니다.")]
    [InlineData("It starts at 12 PM.", "오후 12시에 시작합니다.")]
    [InlineData("회의는 오후 2시 30분에 시작합니다.", "The meeting starts at 2:30 p.m.")]
    [InlineData("회의는 오전 10시에 시작합니다.", "The meeting starts at 10 AM.")]
    // Everyday abbreviations and listed acronyms.
    [InlineData("The U.S. team won the game, e.g. by two points.", "미국 팀이 2점 차이로 승리했다.")]
    [InlineData("Use a short name, i.e. one word.", "짧은 이름, 즉 한 단어를 사용하십시오.")]
    [InlineData("She has a Ph.D. in physics.", "그녀는 물리학 박사 학위를 가지고 있습니다.")]
    [InlineData("Prices in the US and the UK", "미국과 영국의 가격")]
    [InlineData("The U.S.A. and the U.K. signed it.", "미국과 영국이 서명했습니다.")]
    [InlineData("The EU and the UN agreed.", "유럽 연합과 유엔이 동의했습니다.")]
    [InlineData("Press OK to continue.", "계속하려면 확인을 누르십시오.")]
    [InlineData("Turn off the TV.", "텔레비전을 끄십시오.")]
    [InlineData("AI tools are popular.", "인공지능 도구가 인기 있습니다.")]
    [InlineData("미국 시장이 커졌습니다.", "The US market grew.")]
    public void Check_AcceptsTheSameValueWrittenAnotherWay(string source, string output) =>
        Assert.True(Accepted(source, output), Describe(source, output));

    [Theory]
    [InlineData("The store opens at nine.", "가게는 8시에 문을 엽니다.")]
    [InlineData("You can borrow five books.", "책 6권을 빌릴 수 있습니다.")]
    [InlineData("The meeting is on March 3.", "회의는 4월 3일입니다.")]
    [InlineData("The meeting is at 2:30 PM.", "회의는 오전 2시 30분입니다.")]
    [InlineData("The meeting is at 2:30 PM.", "회의는 2시 30분입니다.")]
    [InlineData("The train leaves at 14:30.", "기차는 오후 3시 30분에 출발합니다.")]
    [InlineData("The train leaves at 14:30.", "기차는 2시 30분에 출발합니다.")]
    [InlineData("Call me at 10 a.m.", "오후 10시에 전화하십시오.")]
    [InlineData("Call me at 6 p.m.", "6시에 전화하십시오.")]
    [InlineData("The U.S. team won.", "영국 팀이 이겼습니다.")]
    [InlineData("The US team won.", "영국 팀이 이겼습니다.")]
    [InlineData("The code is 1234.", "코드는 123입니다.")]
    [InlineData("Open 2 files.", "이 파일을 여십시오.")]
    [InlineData("Wait 10 minutes.", "문을 열 수 있습니다.")]
    [InlineData("two cats and a dog", "고양이 2마리와 개 2마리")]
    [InlineData("nine cats", "고양이 9마리와 개 9마리")]
    [InlineData("Configure the CAN bus and the SPI clock.", "버스와 클럭을 구성합니다.")]
    [InlineData("Set GPIO 5 high.", "핀 5를 높게 설정합니다.")]
    [InlineData("Use 2 kg of flour.", "밀가루 두 kg을 사용합니다.")]
    [InlineData("Read https://example.com/US/nine for details.", "자세한 내용은 https://example.com/미국/9를 읽으십시오.")]
    [InlineData("시장이 커졌습니다.", "The US market grew.")]
    [InlineData("The US and the US", "미국과 그곳")]
    public void Check_RejectsAValueThatReallyChanged(string source, string output) =>
        Assert.False(Accepted(source, output), Describe(source, output));

    [Fact]
    public void Find_CatchesDottedAbbreviationsButNotVsOrEtc()
    {
        IReadOnlyList<ProtectedSpan> spans = ProtectedTokens.Find("e.g. i.e. U.S. U.K. U.N. E.U. U.S.A. Ph.D. a.m. p.m. vs. etc. AM PM US OK");

        Assert.Equal(["e.g", "i.e", "U.S", "U.K", "U.N", "E.U", "U.S.A", "Ph.D", "a.m", "p.m", "AM", "PM", "US", "OK"],
            spans.Select(span => span.Text));
        Assert.All(spans.Take(10), span => Assert.Equal("dotted", span.Kind));
        Assert.All(spans.Skip(10), span => Assert.Equal("acro", span.Kind));
    }

    [Fact]
    public void Check_ReportsAnEquivalentValueAsAFormatChange()
    {
        ProtectedTokenCheck check = ProtectedTokens.Check("Call me before 6 p.m.", "오후 6시 전에 전화하십시오.");

        Assert.Contains(check.FormatChanged, issue => issue.Token == "p.m" && issue.Change == ProtectedTokenChange.Equivalent);
        Assert.Empty(check.Altered);
        Assert.Empty(check.Invented);
    }

    [Fact]
    public void Repair_DoesNotTurnARenderedWordIntoAMissingNumber()
    {
        // "12" is missing; the only other number renders "nine" and must not be rewritten into 12.
        const string Source = "We open at nine on day 12.";
        const string Output = "우리는 9시에 문을 엽니다.";

        Assert.Equal((Output, 0), ProtectedTokens.Repair(Source, Output));
        Assert.Equal("12", Assert.Single(ProtectedTokens.Check(Source, Output).Altered).Token);
    }

    [Theory]
    [InlineData("Press 1 time.", "한 번 누릅니다.", true)]
    [InlineData("Go 2 together.", "둘이 갑니다.", true)]
    [InlineData("Open 10 doors.", "문을 열 수 있습니다.", false)]
    [InlineData("He did 1 thing.", "그가 한 것을 봅니다.", false)]
    [InlineData("Use 4 ports.", "인터넷을 사용합니다.", false)]
    [InlineData("Reset 3 times.", "리셋합니다.", false)]
    [InlineData("I have 2 fears.", "두려움이 있습니다.", false)]
    [InlineData("Open 2 files.", "이 파일을 엽니다.", false)]
    public void KoreanNumerals_AreWholeWordsBeforeACounter(string source, string output, bool accepted) =>
        Assert.Equal(accepted, Accepted(source, output));

    private static string Describe(string source, string output)
    {
        ProtectedTokenCheck check = ProtectedTokens.Check(source, output);
        return $"altered: {string.Join(", ", check.Altered.Select(issue => issue.Token))}; "
            + $"invented: {string.Join(", ", check.Invented.Select(issue => issue.Token))}";
    }
}
