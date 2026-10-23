using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class OcrModelBenchmarkTests
{
    /// <summary>
    /// A held-out corpus authored separately from CreateCorpus. The strings, positions,
    /// spacing, canvas sizes and decoration are fixed before detector tuning. No samples
    /// are derived from the original images or selected using a detector's output.
    /// </summary>
    private static List<Sample> CreateValidationCorpus(string output)
    {
        ValidationScene[] scenes =
        [
            new("korean-ui", "edge-list", 264, 130,
                [Line("작업 목록", 3, 2, 13, true), Line("최근 항목", 21, 35, 11),
                 Line("선택 해제", 21, 61, 11), Line("전체 보관", 21, 87, 11)],
                [new(3, 26, 248, 0)], []),
            new("korean-ui", "indented-scale", 270, 112,
                [Line("화면 배율", 2, 4, 12, true), Line("원본 크기", 28, 37, 11.5),
                 Line("맞춤 보기", 52, 67, 11.5)],
                [new(16, 34, 0, 54)], [new(44, 63, 112, 25)]),
            new("korean-ui", "storage-card", 282, 123,
                [Line("보관함", 6, 1, 14, true), Line("남은 용량", 6, 39, 11),
                 Line("폴더 이동", 62, 75, 12)],
                [new(6, 65, 260, 0)], [new(55, 71, 118, 29)]),
            new("korean-ui", "recording-panel", 296, 126,
                [Line("녹화 상태", 4, 3, 12.5, true), Line("일시 정지", 38, 43, 12),
                 Line("녹화 종료", 38, 81, 12)],
                [new(4, 29, 278, 0)], [new(30, 38, 130, 29), new(30, 76, 130, 29)]),
            new("korean-ui", "offset-heading", 300, 137,
                [Line("내보내기", 31, 2, 13, true), Line("이미지 형식", 3, 37, 11),
                 Line("색상 유지", 3, 66, 11), Line("품질 조정", 3, 95, 11)],
                [new(3, 27, 282, 0), new(176, 34, 0, 85)], []),
            new("korean-ui", "staggered-filter", 286, 136,
                [Line("빠른 찾기", 2, 5, 11.5, true), Line("태그 선택", 48, 35, 11.5),
                 Line("날짜 범위", 17, 64, 11.5), Line("조건 초기화", 48, 93, 11.5)],
                [new(8, 29, 0, 94)], [new(40, 31, 126, 25), new(40, 89, 126, 25)]),

            // Controls contain meaningful code, punctuation and empty editor space.
            // Borders, highlights and gutters carry no text and must not yield extra lines.
            new("code", "javascript-control", 448, 154,
                [Line("let active = cache.get(\"theme\");", 27, 6, 11),
                 Line("cfg.enabled = active !== null;", 27, 30, 11),
                 Line("emit(\"mode\", cfg.enabled);", 27, 54, 11),
                 Line("// 다음 실행 대기", 27, 78, 11)],
                [new(19, 3, 0, 144), new(3, 119, 436, 0)],
                [new(22, 28, 418, 23)]),
            new("code", "query-control", 472, 166,
                [Line("SELECT tag, COUNT(*) AS n", 7, 4, 11.5),
                 Line("FROM captures WHERE score > 0.75", 23, 31, 11.5),
                 Line("GROUP BY tag ORDER BY n DESC;", 23, 58, 11.5),
                 Line("-- 검토 대상: draft_v2", 7, 85, 11.5)],
                [new(4, 115, 456, 0), new(4, 148, 456, 0)],
                [new(4, 55, 456, 25)]),

            // The last table row is deliberately blank. It is a false-positive control,
            // not an omitted truth line. Every drawn character is represented below.
            new("spreadsheet", "four-column-ledger", 440, 163,
                [Row(["구분", "건수", "단가", "잔액"], [8, 94, 196, 303], 6, 11),
                 Row(["문서", "18", "2,750", "49,500"], [8, 94, 196, 303], 34, 11),
                 Row(["사진", "42", "1,600", "67,200"], [8, 94, 196, 303], 62, 11),
                 Row(["누계", "60", "4,350", "116,700"], [8, 94, 196, 303], 90, 11)],
                ValidationGrid([3, 87, 189, 296, 437], [3, 31, 59, 87, 115, 143]),
                [new(4, 4, 432, 26)]),
            new("spreadsheet", "offset-inventory", 458, 178,
                [Row(["대상", "배율", "오차", "코드"], [23, 126, 231, 342], 15, 11.5),
                 Row(["창문", "125%", "0.08", "C-204"], [23, 126, 231, 342], 44, 11.5),
                 Row(["책상", "150%", "0.12", "D-517"], [23, 126, 231, 342], 73, 11.5),
                 Row(["선반", "100%", "0.03", "F-832"], [23, 126, 231, 342], 102, 11.5)],
                ValidationGrid([17, 119, 224, 335, 449], [11, 40, 69, 98, 127, 156]),
                [new(18, 99, 430, 27)]),
        ];

        return RenderValidationCorpus(output, "validation2", scenes, new
        {
            koreanUi = 36, codeControls = 12, spreadsheetControls = 12,
            themes = new[] { "light", "dark" }, captureScales = new[] { 1.0, 1.25, 1.5 },
            notes = new[]
            {
                "Six Korean layouts use one-to-three-word labels, small headings and independently varied top/left offsets.",
                "Code and spreadsheet controls contain untitled blank areas, rules and highlights for checking extra recognized lines.",
                "All drawn text is in images[].lines. Each spreadsheet row joins its left-to-right cells with one space.",
                "layouts[].lines[].bounds and cells[].bounds are actual glyph geometry in PNG pixels; they exclude decoration and are checked against the canvas.",
            },
        });
    }

    // Each scene renders at three capture scales in both themes; ids carry the corpus name.
    private static List<Sample> RenderValidationCorpus(string output, string corpus, ValidationScene[] scenes, object coverage)
    {
        string images = Path.Combine(output, "images");
        Directory.CreateDirectory(images);
        string uiFont = SelectFont("Malgun Gothic", "Pretendard", "Segoe UI");
        string codeFont = SelectFont("Cascadia Mono", "Consolas");
        var samples = new List<Sample>();
        var layouts = new List<ValidationLayout>();

        foreach (ValidationScene scene in scenes)
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        foreach (bool dark in new[] { false, true })
            Add(scene, scale, dark);

        Assert.Equal(scenes.Length * 6, samples.Count);
        Assert.Equal(samples.Count, samples.Select(s => s.Truth.Id).Distinct(StringComparer.Ordinal).Count());
        WriteJson(Path.Combine(output, "truth.json"), new
        {
            schemaVersion = 2,
            corpus,
            rendering = "Independent WPF layouts; Display formatting; grayscale antialiasing; 96 DPI PNG; logical geometry and em size multiplied by 1/1.25/1.5; no random data",
            fonts = new { uiFont, codeFont },
            coverage,
            images = samples.Select(s => s.Truth).ToArray(),
            layouts,
        });
        return samples;

        void Add(ValidationScene scene, double scale, bool dark)
        {
            int width = (int)Math.Ceiling(scene.Width * scale);
            int height = (int)Math.Ceiling(scene.Height * scale);
            string font = scene.Category == "code" ? codeFont : uiFont;
            string id = $"{samples.Count + 1:D3}-{corpus}-{scene.Category}-{scene.Name}-{scale * 100:0}-{(dark ? "dark" : "light")}";
            var visual = new DrawingVisual();
            TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            TextOptions.SetTextHintingMode(visual, TextHintingMode.Fixed);
            Brush background = new SolidColorBrush(dark ? Color.FromRgb(24, 28, 35) : Color.FromRgb(249, 250, 252));
            Brush foreground = new SolidColorBrush(dark ? Color.FromRgb(226, 232, 240) : Color.FromRgb(32, 41, 54));
            Brush highlight = new SolidColorBrush(dark ? Color.FromRgb(38, 47, 59) : Color.FromRgb(232, 238, 246));
            var rulePen = new Pen(new SolidColorBrush(dark ? Color.FromRgb(67, 77, 91) : Color.FromRgb(184, 195, 208)), 1);
            var lineLayouts = new List<ValidationLineLayout>();

            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                foreach (Rect rect in scene.Highlights)
                    dc.DrawRectangle(highlight, null, new Rect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale));
                foreach (Rect rule in scene.Rules)
                    dc.DrawLine(rulePen, new Point(rule.X * scale, rule.Y * scale),
                        new Point((rule.X + rule.Width) * scale, (rule.Y + rule.Height) * scale));

                for (int row = 0; row < scene.Lines.Length; row++)
                {
                    ValidationLine line = scene.Lines[row];
                    Assert.Equal(line.Cells.Length, line.X.Length);
                    var cells = new List<ValidationCellLayout>();
                    Rect lineBounds = Rect.Empty;
                    for (int column = 0; column < line.Cells.Length; column++)
                    {
                        var typeface = new Typeface(new FontFamily(font), FontStyles.Normal,
                            line.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
                        var formatted = new FormattedText(line.Cells[column], CultureInfo.GetCultureInfo("ko-KR"),
                            FlowDirection.LeftToRight, typeface, line.FontSize * scale, foreground,
                            null, TextFormattingMode.Display, 1.0);
                        var origin = new Point(line.X[column] * scale, line.Y * scale);
                        Rect glyphBounds = formatted.BuildGeometry(origin).Bounds;
                        double rightLimit = (column + 1 < line.X.Length ? line.X[column + 1] - 4 : scene.Width - 3) * scale;
                        Assert.True(origin.X + formatted.WidthIncludingTrailingWhitespace <= rightLimit,
                            $"Validation text must fit its cell: {id}, {line.Cells[column]}");
                        Assert.True(!glyphBounds.IsEmpty && glyphBounds.Left >= 0.5 && glyphBounds.Top >= 0.5 &&
                            glyphBounds.Right <= width - 0.5 && glyphBounds.Bottom <= height - 0.5,
                            $"Validation glyphs must fit without clipping: {id}, {line.Cells[column]}, {glyphBounds}");
                        Assert.All(cells, previous => Assert.False(glyphBounds.IntersectsWith(previous.GlyphRect),
                            $"Validation cells must not overlap: {id}, {line.Cells[column]}"));
                        dc.DrawText(formatted, origin);
                        lineBounds.Union(glyphBounds);
                        cells.Add(new ValidationCellLayout(line.Cells[column], ValidationBounds.From(glyphBounds),
                            line.FontSize * scale, line.Bold) { GlyphRect = glyphBounds });
                    }
                    if (lineLayouts.Count > 0)
                        Assert.True(lineBounds.Top > lineLayouts[^1].Bounds.Y + lineLayouts[^1].Bounds.Height,
                            $"Validation truth lines must have distinct top-to-bottom positions: {id}");
                    lineLayouts.Add(new ValidationLineLayout(row, string.Join(' ', line.Cells),
                        ValidationBounds.From(lineBounds), cells.ToArray()));
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            string imagePath = Path.Combine(images, id + ".png");
            ImageCodec.SavePng(bitmap, imagePath);
            using var stream = File.OpenRead(imagePath);
            string[] lines = lineLayouts.Select(l => l.Text).ToArray();
            var truth = new TruthImage
            {
                Id = id, Category = scene.Category, SourceCategory = scene.Category,
                Text = string.Join('\n', lines), Lines = lines, Font = font,
                FontSize = scene.Lines[0].FontSize, Scale = scale, Theme = dark ? "dark" : "light",
                Width = width, Height = height, Png = "images/" + id + ".png",
                PngSha256 = Convert.ToHexString(SHA256.HashData(stream)),
            };
            samples.Add(new Sample(truth, bitmap));
            layouts.Add(new ValidationLayout(id, scene.Name, lineLayouts.ToArray()));
        }
    }

    private static ValidationLine Line(string text, double x, double y, double size, bool bold = false) =>
        new([text], [x], y, size, bold);

    private static ValidationLine Row(string[] cells, double[] x, double y, double size) =>
        new(cells, x, y, size, false);

    private static Rect[] ValidationGrid(double[] columns, double[] rows) =>
        rows.Select(y => new Rect(columns[0], y, columns[^1] - columns[0], 0))
            .Concat(columns.Select(x => new Rect(x, rows[0], 0, rows[^1] - rows[0]))).ToArray();

    private sealed record ValidationScene(string Category, string Name, double Width, double Height,
        ValidationLine[] Lines, Rect[] Rules, Rect[] Highlights);
    private sealed record ValidationLine(string[] Cells, double[] X, double Y, double FontSize, bool Bold);
    private sealed record ValidationLayout(string Id, string Name, ValidationLineLayout[] Lines);
    private sealed record ValidationLineLayout(int Index, string Text, ValidationBounds Bounds, ValidationCellLayout[] Cells);
    private sealed record ValidationCellLayout(string Text, ValidationBounds Bounds, double FontSizePixels, bool Bold)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Rect GlyphRect { get; init; }
    }
    private sealed record ValidationBounds(double X, double Y, double Width, double Height)
    {
        public static ValidationBounds From(Rect bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
