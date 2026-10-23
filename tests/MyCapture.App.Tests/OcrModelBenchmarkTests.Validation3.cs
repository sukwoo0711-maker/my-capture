using System.Windows;

namespace MyCapture.App.Tests;

public sealed partial class OcrModelBenchmarkTests
{
    /// <summary>
    /// A third held-out corpus, authored before the merger's alignment and punctuation rules
    /// were revised and not used to tune them. It adds offset and larger headings, a Latin
    /// heading and a Latin term inside Korean labels, button and tab rows, punctuation-heavy
    /// C# and JSON lines, and a time table. Rendering is shared with validation2.
    /// </summary>
    private static List<Sample> CreateValidation3Corpus(string output)
    {
        ValidationScene[] scenes =
        [
            new("korean-ui", "settings-sidebar", 300, 128,
                [Line("환경 설정", 40, 3, 13.5, true), Line("Wi-Fi 연결", 14, 36, 11),
                 Line("알림 끄기", 14, 63, 11), Line("화면 잠금", 14, 90, 11)],
                [new(10, 28, 280, 0)], [new(8, 59, 140, 24)]),
            new("korean-ui", "dialog-buttons", 320, 118,
                [Line("파일을 삭제하시겠습니까?", 12, 8, 12), Line("삭제한 파일은 휴지통에 남습니다", 12, 36, 10.5),
                 Row(["삭제", "취소"], [150, 230], 76, 11.5)],
                [], [new(140, 70, 64, 28), new(220, 70, 64, 28)]),
            new("korean-ui", "tab-header", 290, 124,
                [Row(["일반", "고급", "정보"], [10, 70, 130], 4, 11.5), Line("자동 업데이트 확인", 22, 40, 11),
                 Line("버전 4.6.1", 22, 70, 11), Line("저장 위치 변경", 22, 97, 10.5)],
                [new(6, 26, 276, 0)], [new(4, 2, 56, 24)]),
            new("korean-ui", "status-list", 280, 130,
                [Line("작업 현황", 5, 2, 12.5, true), Line("대기 중인 작업 3개", 30, 33, 11),
                 Line("업로드 완료", 12, 61, 11), Line("오류 1건 확인 필요", 44, 89, 11)],
                [new(5, 27, 260, 0)], [new(36, 84, 150, 26)]),
            new("korean-ui", "large-heading", 260, 140,
                [Line("녹화 기록", 58, 2, 16, true), Line("오늘", 10, 44, 10.5),
                 Line("어제", 10, 70, 10.5), Line("지난주 보관", 10, 96, 10.5)],
                [new(4, 34, 250, 0), new(90, 40, 0, 90)], [new(6, 66, 76, 22)]),
            new("korean-ui", "english-heading", 270, 132,
                [Line("Library", 6, 4, 13, true), Line("최근 캡처", 26, 36, 11),
                 Line("고정된 항목", 26, 63, 11), Line("휴지통 비우기", 26, 90, 11)],
                [new(4, 28, 258, 0)], [new(20, 32, 120, 25)]),

            new("code", "csharp-control", 470, 150,
                [Line("var items = list.Where(x => x.Id > 0);", 24, 6, 11),
                 Line("Console.WriteLine($\"{a}, {b}\");", 24, 30, 11),
                 Line("int[] sizes = { 8, 16, 32 };", 24, 54, 11),
                 Line("// 목록 정리: done", 24, 78, 11)],
                [new(16, 3, 0, 140), new(3, 112, 460, 0)], [new(20, 51, 440, 23)]),
            new("code", "json-control", 440, 160,
                [Line("{ \"name\": \"캡처\", \"size\": 12,", 10, 5, 11.5),
                 Line("\"tags\": [\"ui\", \"ocr\"],", 30, 31, 11.5),
                 Line("\"ratio\": 1.5, \"ok\": true }", 30, 57, 11.5),
                 Line("// 설정 파일 예시", 10, 83, 11.5)],
                [new(4, 116, 430, 0)], [new(24, 28, 400, 25)]),

            new("spreadsheet", "time-schedule", 360, 150,
                [Row(["항목", "시작", "종료"], [10, 110, 220], 6, 11),
                 Row(["회의", "09:30", "10:15"], [10, 110, 220], 33, 11),
                 Row(["점심", "12:00", "13:00"], [10, 110, 220], 60, 11),
                 Row(["검토", "14:20", "15:05"], [10, 110, 220], 87, 11)],
                ValidationGrid([5, 102, 212, 352], [3, 30, 57, 84, 111, 138]),
                [new(6, 4, 344, 25)]),
            new("spreadsheet", "price-list", 470, 168,
                [Row(["이름", "수량", "금액", "비고"], [14, 104, 200, 318], 10, 11.5),
                 Row(["연필", "12", "3,600", "기본"], [14, 104, 200, 318], 38, 11.5),
                 Row(["공책", "5", "7,500", "추가"], [14, 104, 200, 318], 66, 11.5),
                 Row(["지우개", "30", "9,000", "할인"], [14, 104, 200, 318], 94, 11.5)],
                ValidationGrid([8, 96, 192, 310, 462], [6, 34, 62, 90, 118, 146]),
                [new(9, 63, 452, 26)]),
        ];

        return RenderValidationCorpus(output, "validation3", scenes, new
        {
            koreanUi = 36, codeControls = 12, spreadsheetControls = 12,
            themes = new[] { "light", "dark" }, captureScales = new[] { 1.0, 1.25, 1.5 },
            notes = new[]
            {
                "Authored before the merger revision; not used to choose or tune merger rules.",
                "Korean layouts add offset and 16 px headings, a Latin heading, a Latin term in a Korean label, and button/tab rows whose cells join with one space.",
                "Code controls are punctuation-heavy C# and JSON lines; spreadsheets use times and grouped amounts with a blank final row.",
            },
        });
    }
}
