using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>New synthetic corpus, independent of the unavailable historical 19 inputs.</summary>
internal static class OcrCommercialCorpusData
{
    internal const string CorpusId = "commercial-synthetic-300-v1";
    internal const uint Seed = 20261104;
    internal static readonly string[] Categories = ["ko", "en", "mixed", "small", "code"];
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal sealed record Box(double X, double Y, double Width, double Height)
    {
        internal static Box From(Rect value) => new(value.X, value.Y, value.Width, value.Height);
        internal bool Intersects(OcrRect value) => value.Width > 0 && value.Height > 0 &&
            X < value.Right && X + Width > value.X && Y < value.Bottom && Y + Height > value.Y;
    }
    internal sealed record TruthLine(string Text, Box Bounds, string[] Cells);
    internal sealed record Sample(string Id, string SceneId, string Category, string Split, string[] Languages,
        double Scale, string Theme, double FontSize, string RequestedFont, bool LowContrast,
        int Width, int Height, string Text, TruthLine[] Lines, string[] FontUris,
        string Png, string PngSha256, string PixelSha256, string TruthSha256);
    internal sealed record FontEvidence(string Uri, string Sha256);
    internal sealed record Manifest(int SchemaVersion, string Id, uint Seed, int IndependentScenes,
        string Scope, Sample[] Samples, FontEvidence[] Fonts);
    internal sealed record Metric(int TruthCharacters, int CharacterEdits, double Cer,
        int TruthLines, int MissingLineCount, int ExactUnmatchedLines, bool RawExact);

    internal static string RepositoryRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "MyCapture.slnx"))) return directory;
        throw new InvalidOperationException("The corpus test must run from this repository's test output.");
    }

    internal static string ArtifactPath(string value)
    {
        string root = Path.GetFullPath(Path.Combine(RepositoryRoot(), "artifacts")) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(value);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length <= root.Length)
            throw new IOException("Corpus paths must be child directories of this repository's artifacts directory.");
        // The diagnostic helper checks every ancestor for junctions/symbolic links without creating anything.
        _ = DiagnosticOutputPaths.Child(path, "path-check");
        return path;
    }

    internal static string NewOutput(string value)
    {
        string root = ArtifactPath(value);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Output already exists; preserve it and choose a new directory.");
        DiagnosticOutputPaths.Create(root);
        using var claim = new FileStream(DiagnosticOutputPaths.Child(root, "owned-output.txt"), FileMode.CreateNew, FileAccess.Write);
        byte[] bytes = Encoding.UTF8.GetBytes("Synthetic OCR evidence. All files are retained; this test performs no cleanup.\n");
        claim.Write(bytes);
        return root;
    }

    internal static void Write(string root, string name, object value)
    {
        using var stream = new FileStream(DiagnosticOutputPaths.Child(root, name), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, value, Json);
        stream.Flush(true);
    }

    internal static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string PixelHash(BitmapSource bitmap)
    {
        BitmapSource pixels = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = checked(pixels.PixelWidth * 4);
        byte[] bytes = new byte[checked(stride * pixels.PixelHeight)];
        pixels.CopyPixels(bytes, stride, 0);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static Manifest Generate(string root)
    {
        var samples = new List<Sample>(300);
        var fonts = new Dictionary<string, FontEvidence>(StringComparer.Ordinal);
        foreach (string category in Categories)
        for (int scene = 0; scene < 10; scene++)
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        foreach (bool dark in new[] { false, true })
            samples.Add(Render(root, category, scene, scale, dark, fonts));
        var manifest = new Manifest(1, CorpusId, Seed, 50,
            "50 separately authored content scenes, each rendered at 100/125/150 percent in light/dark: 300 images, 60 per category. " +
            "Scenes 00-05 are development and 06-09 are held-out; every derivative of one scene stays in the same split. " +
            "This new synthetic corpus does not replace the missing historical 19-input corpus. No real captures or user text.",
            samples.ToArray(), fonts.Values.OrderBy(font => font.Uri, StringComparer.Ordinal).ToArray());
        Validate(manifest);
        Write(root, "corpus.json", manifest);
        Write(root, "generation.json", new { status = "GENERATED", releaseGate = "NOT_EVALUATED", count = samples.Count,
            corpusSha256 = FileHash(DiagnosticOutputPaths.Child(root, "corpus.json")), createdUtc = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(), logicalProcessors = Environment.ProcessorCount });
        return manifest;
    }

    internal static Manifest Read(string root)
    {
        root = ArtifactPath(root);
        using var stream = File.OpenRead(DiagnosticOutputPaths.Child(root, "corpus.json"));
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(stream, Json) ?? throw new InvalidDataException("Missing corpus manifest.");
        Validate(manifest);
        foreach (Sample sample in manifest.Samples)
        {
            string path = DiagnosticOutputPaths.Child(root, sample.Png);
            Assert.Equal(sample.PngSha256, FileHash(path));
            BitmapSource bitmap = ImageCodec.TryLoad(path) ?? throw new InvalidDataException("Cannot decode corpus input: " + sample.Id);
            Assert.Equal(sample.Width, bitmap.PixelWidth);
            Assert.Equal(sample.Height, bitmap.PixelHeight);
            Assert.Equal(sample.PixelSha256, PixelHash(bitmap));
        }
        return manifest;
    }

    private static void Validate(Manifest manifest)
    {
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(CorpusId, manifest.Id);
        Assert.Equal(Seed, manifest.Seed);
        Assert.Equal(50, manifest.IndependentScenes);
        Assert.Equal(300, manifest.Samples.Length);
        Assert.Equal(300, manifest.Samples.Select(sample => sample.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(50, manifest.Samples.Select(sample => sample.SceneId).Distinct(StringComparer.Ordinal).Count());
        foreach (string category in Categories) Assert.Equal(60, manifest.Samples.Count(sample => sample.Category == category));
        foreach (IGrouping<string, Sample> scene in manifest.Samples.GroupBy(sample => sample.SceneId))
        {
            Assert.Equal(6, scene.Count());
            Assert.Single(scene.Select(sample => sample.Split).Distinct());
            Assert.Single(scene.Select(sample => sample.Text).Distinct());
        }
        foreach (Sample sample in manifest.Samples)
        {
            Assert.NotEmpty(sample.Lines);
            Assert.Equal(string.Join('\n', sample.Lines.Select(line => line.Text)), sample.Text);
            Assert.Equal(TruthHash(sample), sample.TruthSha256);
            Assert.NotEmpty(sample.FontUris);
            Assert.All(sample.FontUris, uri => Assert.Contains(manifest.Fonts, font => font.Uri == uri && font.Sha256.Length == 64));
        }
    }

    private static Sample Render(string root, string category, int index, double scale, bool dark,
        Dictionary<string, FontEvidence> fonts)
    {
        string sceneId = $"{category}-{index:00}";
        string id = $"{sceneId}-{(int)(scale * 100)}-{(dark ? "dark" : "light")}";
        string[][] rows = Content(category, index);
        bool table = category == "ko" && index == 0;
        string font = category == "code" ? "Consolas" : category == "en" ? "Segoe UI" : "Malgun Gothic";
        double size = table ? 22 : category == "small" ? 10 + index % 3 : category == "code" ? 16 : 20;
        bool lowContrast = category == "small" && index >= 5;
        double logicalWidth = table ? 560 : 1000;
        double rowHeight = table ? 60 : size * 1.8;
        double logicalHeight = table ? 300 : 64 + rowHeight * rows.Length;
        int width = checked((int)Math.Ceiling(logicalWidth * scale));
        int height = checked((int)Math.Ceiling(logicalHeight * scale));
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        Color foreground = lowContrast ? (dark ? Color.FromRgb(130, 135, 145) : Color.FromRgb(125, 130, 140)) :
            dark ? Color.FromRgb(235, 237, 242) : Colors.Black;
        Brush ink = new SolidColorBrush(foreground);
        var lines = new List<TruthLine>();
        var previousCells = new List<Rect>();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(dark ? new SolidColorBrush(Color.FromRgb(20, 24, 31)) : Brushes.White, null, new Rect(0, 0, width, height));
            for (int row = 0; row < rows.Length; row++)
            {
                Rect lineBounds = Rect.Empty;
                for (int column = 0; column < rows[row].Length; column++)
                {
                    var text = new FormattedText(rows[row][column], CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                        new Typeface(font), size * scale, ink, 1.0);
                    double x = table ? (column switch { 0 => 32 * scale, 1 => 280 * scale - text.Width, _ => 520 * scale - text.Width }) : 32 * scale;
                    var origin = new Point(x, (table ? 35 + row * rowHeight : 32 + row * rowHeight) * scale);
                    Rect bounds = text.BuildGeometry(origin).Bounds;
                    Assert.False(bounds.IsEmpty, "No visible input glyphs: " + id);
                    Assert.True(bounds.Left >= 1 && bounds.Top >= 1 && bounds.Right <= width - 1 && bounds.Bottom <= height - 1, "Clipped truth: " + id);
                    Assert.All(previousCells, previous => Assert.False(bounds.IntersectsWith(previous), "Overlapping truth: " + id));
                    previousCells.Add(bounds);
                    lineBounds.Union(bounds);
                    dc.DrawText(text, origin);
                }
                if (table && row < rows.Length - 1)
                    dc.DrawLine(new Pen(dark ? Brushes.DimGray : Brushes.LightGray, scale), new Point(25 * scale, (82 + row * 60) * scale), new Point(535 * scale, (82 + row * 60) * scale));
                lines.Add(new TruthLine(string.Join('\t', rows[row]), Box.From(lineBounds), rows[row]));
            }
        }
        GlyphRun[] runs = GlyphRuns(visual.Drawing).ToArray();
        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.DoesNotContain((ushort)0, run.GlyphIndices));
        string[] uris = runs.Select(run => run.GlyphTypeface.FontUri.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (GlyphRun run in runs)
        {
            string uri = run.GlyphTypeface.FontUri.ToString();
            if (fonts.ContainsKey(uri)) continue;
            using Stream stream = run.GlyphTypeface.FontUri.IsFile ? File.OpenRead(run.GlyphTypeface.FontUri.LocalPath) :
                Application.GetResourceStream(run.GlyphTypeface.FontUri)?.Stream ?? throw new IOException("Actual rendered font cannot be hashed.");
            fonts.Add(uri, new FontEvidence(uri, Convert.ToHexString(SHA256.HashData(stream))));
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
            stream.Flush(true);
        }
        string[] languages = category is "en" or "code" || category == "small" && index % 2 == 1 ? ["en-US"] :
            category == "mixed" ? ["ko-KR", "en-US"] : ["ko-KR"];
        var sample = new Sample(id, sceneId, category, index < 6 ? "development" : "held-out", languages,
            scale, dark ? "dark" : "light", size, font, lowContrast, width, height,
            string.Join('\n', lines.Select(line => line.Text)), lines.ToArray(), uris, filename, FileHash(path), PixelHash(bitmap), "");
        return sample with { TruthSha256 = TruthHash(sample) };
    }

    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (GlyphRun run in GlyphRuns(child)) yield return run;
    }

    private static string TruthHash(Sample sample) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(sample with { PngSha256 = "", PixelSha256 = "", TruthSha256 = "" }, Json)));

    private static string[][] Content(string category, int index)
    {
        // Literal scene families are fixed; the stable seed only supplies non-sensitive numeric fields.
        uint state = Seed ^ ((uint)Array.IndexOf(Categories, category) + 1) * 0x9E3779B9u ^ (uint)index;
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        int number = 10000 + (int)(state % 80000);
        string[][] ko =
        [
            ["항목\t수량\t금액", "키보드\t2\t128,400", "USB 케이블\t12\t9,900", "모니터\t1\t1,204,000"],
            ["새 문서를 저장합니다.", "저장 위치: 작업 폴더", "자동 저장 간격: 5분", "최근 수정 내용을 확인합니다."],
            ["화면 캡처 설정", "선택 영역을 클립보드에 복사", "마우스 포인터 포함", "완료 후 편집기 열기"],
            ["파일 이름을 입력하십시오.", "이름이 같은 파일이 있습니다.", "기존 항목을 보존합니다.", "다른 이름으로 저장"],
            ["녹화 준비가 완료되었습니다.", "오디오 입력 장치를 선택합니다.", "일시 정지 후 다시 시작", "녹화 시간: 00:12:34"],
            ["자료 목록", "회의록 12개, 첨부 파일 7개", "선택한 항목의 날짜를 확인합니다.", "찾는 내용이 없습니다."],
            ["책갈피 관리", "첫 페이지와 마지막 페이지", "서로 다른 글꼴의 한글 문장", "변경한 순서를 저장합니다."],
            ["전송 상태", "받은 문서: 23개", "남은 작업: 4개", "모든 항목을 다시 확인하십시오."],
            ["프로젝트 환경", "화면 배율 125 퍼센트", "기본 언어: 한국어", "다음 실행부터 적용됩니다."],
            ["월별 정산 내역", "공급가액 57,300원", "부가세 5,730원", "총 합계 63,030원"],
        ];
        string[][] en =
        [
            ["Capture the selected region.", "Keep the pointer outside the document.", "Review the image before saving."],
            ["The archive contains twelve reports.", "Each report has a title and a date.", "Search matches both fields."],
            ["Open the preferences window.", "Choose a folder for new images.", "Apply changes to future captures."],
            ["Reading order follows the printed lines.", "Punctuation matters: commas, colons; periods.", "Do not replace missing words."],
            ["Connect the display before recording.", "Check the microphone level.", "Press the shortcut to finish."],
            ["A local backup keeps the original file.", "The edited copy uses a different name.", "Restore only the selected version."],
            ["The northern station opens at 08:45.", "The last train leaves at 22:10.", "Passengers should retain their tickets."],
            ["Measurements use a fixed input sequence.", "A faster average can hide a slow example.", "Record every observed result."],
            ["Three panels share the available width.", "Long labels may move to the next row.", "Buttons remain reachable by keyboard."],
            ["The document ends with an appendix.", "Its references identify the source material.", "Check the edition before quoting."],
        ];
        string[][] mixed =
        [
            ["Project Alpha 작업을 시작합니다.", "새 파일 12개를 local 폴더에 저장합니다.", "완료 후 Review 항목을 확인합니다."],
            ["설정 Settings", "OCR 언어: 한국어 and English", "원본 Original 이미지를 보존합니다."],
            ["회의 Meeting 10:30", "담당자 Owner: 개발 팀", "Action items 7개를 확인합니다."],
            ["빌드 Build 성공", "경고 Warnings 0개", "테스트 Results를 저장합니다."],
            ["Invoice 청구서", "Subtotal 23,400원", "부가세 Tax 2,340원"],
            ["키보드 Shortcut 설정", "Ctrl + Shift + C 영역 캡처", "Esc 취소, Enter 확인"],
            ["Storage 저장 위치", "Documents 문서 폴더", "Read only 읽기 전용 상태"],
            ["Network 연결 상태", "응답 Response: 200 OK", "재시도 Retry 간격 5초"],
            ["화면 Display 1920 x 1080", "Scale 배율 150 퍼센트", "창 Window 위치를 복원합니다."],
            ["Release 배포 기록", "변경 Changes 14개", "검증 Validation 결과를 확인합니다."],
        ];
        string[] lines = category switch
        {
            "ko" => ko[index], "en" => en[index], "mixed" => mixed[index],
            "small" when index % 2 == 0 => [$"작은 글자 안내 {number}", "문장 부호와 띄어쓰기를 보존합니다.", $"페이지 {index + 1} / 10, 금액 12,450원"],
            "small" => [$"Small text notice {number}", "Preserve punctuation and letter case.", $"Page {index + 1} / 10; total 12,450.00"],
            "code" => Code(index, number),
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        return lines.Select(line => line.Split('\t')).ToArray();
    }

    private static string[] Code(int index, int number) => index switch
    {
        0 => ["def greet(name):", "    if not name:", "        return 'Hello, world!'", "    return f'Hello, {name}!'"],
        1 => ["public int Sum(int left, int right)", "{", "    return checked(left + right);", "}"],
        2 => ["{", $"  \"requestId\": {number},", "  \"enabled\": true,", "  \"names\": [\"alpha\", \"beta\"] }"],
        3 => ["const values = [1, 2, 3];", "const total = values.reduce((a, b) => a + b, 0);", "console.log(`Total: ${total}`);"],
        4 => ["SELECT item_id, amount", "FROM invoices", "WHERE amount >= 1000 AND active = 1", "ORDER BY amount DESC;"],
        5 => ["for index in range(10):", "    value = index * 3", "    print(f'{index}: {value}')"],
        6 => ["using var stream = File.OpenRead(path);", "byte[] hash = SHA256.HashData(stream);", "return Convert.ToHexString(hash);"],
        7 => ["function clamp(value, min, max) {", "  return Math.min(max, Math.max(min, value));", "}"],
        8 => ["[settings]", "enabled = true", $"sample_id = {number}", "names = ['first', 'second']"],
        _ => ["try:", "    result = parse(source)", "except ValueError as error:", "    raise RuntimeError('Invalid input') from error"],
    };

    internal static Metric Measure(Sample sample, OcrResult result)
    {
        string truth = Normalize(sample.Text);
        string observed = Normalize(result.Text);
        int edits = EditDistance(truth, observed);
        string[] expected = sample.Lines.Select(line => Normalize(line.Text)).ToArray();
        string[] actual = result.Text.Split('\n').Select(Normalize).Where(line => line.Length > 0).ToArray();
        int[] matched = new int[actual.Length + 1];
        foreach (string line in expected)
        {
            int diagonal = 0;
            for (int j = 1; j <= actual.Length; j++)
            {
                int old = matched[j];
                matched[j] = line == actual[j - 1] ? diagonal + 1 : Math.Max(matched[j], matched[j - 1]);
                diagonal = old;
            }
        }
        int missing = sample.Lines.Count(line => !result.Lines.Any(candidate => !string.IsNullOrWhiteSpace(candidate.Text) && line.Bounds.Intersects(candidate.Bounds)));
        return new Metric(truth.Length, edits, (double)edits / Math.Max(1, truth.Length), expected.Length,
            missing, expected.Length - matched[^1], string.Equals(sample.Text, result.Text, StringComparison.Ordinal));
    }

    private static string Normalize(string text) => string.Join('\n', text.Normalize(NormalizationForm.FormC)
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
        .Select(line => Regex.Replace(line, @"[\t\p{Zs}]+", " ", RegexOptions.CultureInvariant).Trim()).Where(line => line.Length > 0));

    internal static int EditDistance(string truth, string observed)
    {
        int[] previous = Enumerable.Range(0, observed.Length + 1).ToArray();
        int[] current = new int[observed.Length + 1];
        for (int i = 1; i <= truth.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= observed.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (truth[i - 1] == observed[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[^1];
    }
}
