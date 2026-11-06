using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using Xunit;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;

namespace MyCapture.App.Tests;

/// <summary>
/// #226 stratified synthetic corpus: content kind × text size × theme × crop. Tight crops put
/// most inputs in the adaptive 4×/3× upscale regime that the 1000-pixel-wide 300-image corpus
/// barely reaches; tables and numeric lines carry thousands/decimal/time separators. Every
/// (kind, size) cell is split across development and held-out with both themes and crops.
/// </summary>
internal static class Ocr226StrataCorpus
{
    internal const string CorpusId = "ocr226-strata-336-v1";
    internal const uint Seed = 20261106;
    internal static readonly string[] Kinds = ["table-ko", "ui-ko", "num", "en", "mixed", "table-en"];
    internal static readonly double[] Sizes = [9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32, 40];

    internal static Data.Manifest Generate(string root)
    {
        var samples = new List<Data.Sample>();
        var fonts = new Dictionary<string, Data.FontEvidence>(StringComparer.Ordinal);
        int scene = 0;
        foreach (string kind in Kinds)
        for (int size = 0; size < Sizes.Length; size++)
        foreach (bool dark in new[] { false, true })
        foreach (bool tight in new[] { true, false })
        {
            // Light-tight and dark-wide are development; the other two are held out.
            string split = dark == tight ? "held-out" : "development";
            samples.Add(Render(root, kind, Sizes[size], dark, tight, split, scene++, fonts));
        }
        var manifest = new Data.Manifest(1, CorpusId, Seed, samples.Count,
            "6 kinds x 14 text sizes (9-40 px) x light/dark x tight/wide crop = 336 images. Each (kind, size) has two development " +
            "and two held-out renderings with distinct content. Synthetic text only; no captures or user text.",
            samples.ToArray(), fonts.Values.OrderBy(font => font.Uri, StringComparer.Ordinal).ToArray());
        Data.Write(root, "corpus.json", manifest);
        return manifest;
    }

    private static Data.Sample Render(string root, string kind, double size, bool dark, bool tight, string split, int scene,
        Dictionary<string, Data.FontEvidence> fonts)
    {
        uint state = Seed ^ (uint)(scene + 1) * 0x9E3779B9u;
        string[][] rows = Content(kind, ref state);
        bool table = kind.StartsWith("table", StringComparison.Ordinal);
        string font = kind is "en" or "table-en" ? "Segoe UI" : kind == "num" && scene % 2 == 1 ? "Segoe UI" : "Malgun Gothic";
        string id = $"{kind}-{size:00}-{(dark ? "dark" : "light")}-{(tight ? "tight" : "wide")}";
        Brush ink = new SolidColorBrush(dark ? Color.FromRgb(232, 234, 238) : Color.FromRgb(20, 20, 20));
        Brush background = new SolidColorBrush(dark ? Color.FromRgb(24, 27, 33) : Colors.White);
        Brush rule = dark ? new SolidColorBrush(Color.FromRgb(70, 74, 82)) : Brushes.LightGray;
        var formatting = scene % 3 == 0 ? TextFormattingMode.Ideal : TextFormattingMode.Display;
        FormattedText Text(string value) => new(value, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            new Typeface(font), size, ink, null, formatting, 1.0);

        double pad = tight ? Math.Round(size * 0.7) + 4 : 32;
        double pitch = table ? Math.Round(size * 2.7) : Math.Round(size * 1.7);
        double[] rights = [];
        double contentWidth;
        if (table)
        {
            double[] widths = Enumerable.Range(0, 3).Select(column => rows.Max(row => Text(row[column]).WidthIncludingTrailingWhitespace)).ToArray();
            double gap = size * 3;
            rights = [pad + widths[0], pad + widths[0] + gap + widths[1], pad + widths[0] + gap + widths[1] + gap + widths[2]];
            contentWidth = rights[2] - pad;
        }
        else contentWidth = rows.Max(row => Text(row[0]).WidthIncludingTrailingWhitespace);
        int width = (int)Math.Ceiling(tight ? contentWidth + pad * 2 : Math.Max(1200, contentWidth + pad * 2));
        int height = (int)Math.Ceiling(pad * 2 + pitch * (rows.Length - 1) + size * 1.4 + (tight ? 0 : 120));
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, formatting);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        var lines = new List<Data.TruthLine>();
        var cells = new List<Rect>();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            for (int row = 0; row < rows.Length; row++)
            {
                Rect lineBounds = Rect.Empty;
                double y = Math.Round(pad + row * pitch);
                for (int column = 0; column < rows[row].Length; column++)
                {
                    FormattedText text = Text(rows[row][column]);
                    double x = !table || column == 0 ? pad : rights[column] - text.Width;
                    var origin = new Point(x, y);
                    Rect bounds = text.BuildGeometry(origin).Bounds;
                    Assert.False(bounds.IsEmpty, "No visible glyphs: " + id);
                    Assert.True(bounds.Left >= 1 && bounds.Top >= 1 && bounds.Right <= width - 1 && bounds.Bottom <= height - 1, "Clipped truth: " + id);
                    Assert.All(cells, previous => Assert.False(bounds.IntersectsWith(previous), "Overlapping truth: " + id));
                    cells.Add(bounds);
                    lineBounds.Union(bounds);
                    dc.DrawText(text, origin);
                }
                if (table && row < rows.Length - 1)
                {
                    double ruleY = Math.Round(y + (pitch + size * 1.3) / 2) + 0.5;
                    dc.DrawLine(new Pen(rule, 1), new Point(pad - 6, ruleY), new Point(rights[2] + 6, ruleY));
                }
                lines.Add(new Data.TruthLine(string.Join('\t', rows[row]), Data.Box.From(lineBounds), rows[row]));
            }
        }
        GlyphRun[] runs = GlyphRuns(visual.Drawing).ToArray();
        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.DoesNotContain((ushort)0, run.GlyphIndices));
        string[] uris = runs.Select(run => run.GlyphTypeface.FontUri.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (GlyphRun run in runs)
        {
            string uri = run.GlyphTypeface.FontUri.ToString();
            if (!fonts.ContainsKey(uri) && run.GlyphTypeface.FontUri.IsFile)
                fonts.Add(uri, new Data.FontEvidence(uri, Data.FileHash(run.GlyphTypeface.FontUri.LocalPath)));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        string filename = id + ".png";
        string path = DiagnosticOutputPaths.Child(root, filename);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
        string[] languages = kind is "en" or "table-en" ? ["en-US"] : kind == "mixed" ? ["ko-KR", "en-US"] : ["ko-KR"];
        var sample = new Data.Sample(id, $"{kind}-{scene:000}", kind, split, languages, 1.0, dark ? "dark" : "light", size, font,
            false, width, height, string.Join('\n', lines.Select(line => line.Text)), lines.ToArray(), uris, filename,
            Data.FileHash(path), Data.PixelHash(bitmap), "");
        return sample with { TruthSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(sample, Data.Json))) };
    }

    internal static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (GlyphRun run in GlyphRuns(child)) yield return run;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return state;
    }

    private static string Pick(string[] values, ref uint state) => values[Next(ref state) % (uint)values.Length];

    private static string Amount(ref uint state)
    {
        // Mix of 4-7 digit amounts so one, two or no thousands separators all occur.
        int digits = 3 + (int)(Next(ref state) % 5);
        long value = (long)(Math.Pow(10, digits - 1) + Next(ref state) % (uint)(9 * Math.Pow(10, digits - 1)));
        return value.ToString("#,0", CultureInfo.InvariantCulture);
    }

    private static string[][] Content(string kind, ref uint state)
    {
        string[] koItems = ["키보드", "마우스", "모니터", "USB 케이블", "노트북 거치대", "외장 하드", "프린터 토너", "무선 충전기", "책상 램프", "웹캠"];
        string[] enItems = ["Keyboard", "Mouse", "Monitor", "USB cable", "Laptop stand", "External drive", "Printer toner", "Desk lamp", "Webcam", "Headset"];
        string[][] koHeaders = [["항목", "수량", "금액"], ["품목", "개수", "가격"], ["제품명", "수량", "합계"], ["구분", "건수", "금액(원)"]];
        string[][] enHeaders = [["Item", "Qty", "Amount"], ["Product", "Count", "Price"], ["Name", "Units", "Total"]];
        switch (kind)
        {
            case "table-ko":
            case "table-en":
            {
                bool korean = kind == "table-ko";
                var rows = new List<string[]> { korean ? koHeaders[Next(ref state) % (uint)koHeaders.Length] : enHeaders[Next(ref state) % (uint)enHeaders.Length] };
                var used = new HashSet<string>();
                int count = 3 + (int)(Next(ref state) % 2);
                while (rows.Count <= count)
                {
                    string item = Pick(korean ? koItems : enItems, ref state);
                    if (!used.Add(item)) continue;
                    rows.Add([item, (1 + Next(ref state) % 24).ToString(CultureInfo.InvariantCulture), Amount(ref state)]);
                }
                return rows.ToArray();
            }
            case "ui-ko":
            {
                string[][] scenes =
                [
                    ["설정을 저장했습니다.", "변경 사항은 다음 실행부터 적용됩니다."],
                    ["새 폴더 만들기", "이름 바꾸기", "삭제"],
                    ["캡처한 영역을 클립보드에 복사합니다.", "편집기에서 열기"],
                    ["파일을 찾을 수 없습니다.", "다시 시도하십시오."],
                    ["녹화 준비 완료", "마이크 입력 장치 선택", "일시 정지"],
                    ["글자 인식 결과", "표로 보기", "원문 복사"],
                    ["보관 기간이 지난 항목을 정리합니다.", "정리 전에 목록을 확인하십시오."],
                    ["화면 배율 125퍼센트", "기본 언어 한국어"],
                ];
                return scenes[Next(ref state) % (uint)scenes.Length].Select(line => new[] { line }).ToArray();
            }
            case "num":
            {
                int month = 1 + (int)(Next(ref state) % 12), day = 1 + (int)(Next(ref state) % 28);
                int hour = (int)(Next(ref state) % 24), minute = (int)(Next(ref state) % 60), second = (int)(Next(ref state) % 60);
                // Invariant formatting keeps the truth text and pixels the same on every locale.
                CultureInfo invariant = CultureInfo.InvariantCulture;
                string[] lines =
                [
                    $"합계 {Amount(ref state)}원",
                    string.Create(invariant, $"2026-{month:00}-{day:00} {hour:00}:{minute:00}:{second:00}"),
                    string.Create(invariant, $"부가세 {Amount(ref state)}원, 할인 {(Next(ref state) % 1000) / 10.0:0.0}%"),
                    string.Create(invariant, $"버전 {1 + Next(ref state) % 9}.{Next(ref state) % 20}.{Next(ref state) % 10}, 용량 {(Next(ref state) % 100000) / 100.0:#,0.00} MB"),
                ];
                return lines.Select(line => new[] { line }).ToArray();
            }
            case "en":
            {
                string[][] scenes =
                [
                    ["Settings saved.", "Changes apply the next time you start the app."],
                    ["Copy the captured region to the clipboard.", "Open in editor"],
                    ["The file could not be found.", "Try again."],
                    ["Recording ready", "Select a microphone", "Pause"],
                    ["Text recognition result", "View as table", "Copy original"],
                    ["Display scale 125 percent", "Default language English"],
                    ["Items older than the retention period are removed.", "Review the list first."],
                    ["Search results: 12 matches in 3 folders", "Sort by date"],
                ];
                return scenes[Next(ref state) % (uint)scenes.Length].Select(line => new[] { line }).ToArray();
            }
            case "mixed":
            {
                string[][] scenes =
                [
                    ["OCR 결과를 Markdown 표로 복사합니다.", "Ctrl + C 원문 복사"],
                    ["Windows 10 사용자는 Store 업데이트를 확인하십시오.", "Release 5.1.0"],
                    ["PDF 문서 3개, PNG 이미지 12개", "Export 완료"],
                    ["Wi-Fi 연결 상태 확인", "Retry 간격 5초"],
                    ["HDR 화면 캡처 지원", "GPU 가속 사용"],
                    ["Project Alpha 회의 10:30", "Owner 개발 팀"],
                ];
                return scenes[Next(ref state) % (uint)scenes.Length].Select(line => new[] { line }).ToArray();
            }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
