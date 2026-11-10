using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MyCapture.Ocr;
using Xunit;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;

namespace MyCapture.App.Tests;

/// <summary>Pure geometry and retained development inputs; no OS OCR, model or file access.</summary>
public sealed class WindowsOcrTextAngleMappingTests
{
    [Fact]
    public void NullZeroAndUnusableMetadataKeepExactInputGeometry()
    {
        var bounds = new OcrRect(13.25, 29.5, 47.125, 11.75);
        foreach (double? angle in new double?[] { null, 0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Equal(bounds, WindowsOcrRecognizer.MapTextAngleBounds(bounds, angle, 800, 300));
        Assert.Equal(bounds, WindowsOcrRecognizer.MapTextAngleBounds(bounds, 30, 0, 300));
        Assert.Equal(bounds, WindowsOcrRecognizer.MapTextAngleBounds(bounds, 30, 800, 0));
    }

    [Theory]
    [InlineData(-180)]
    [InlineData(-90)]
    [InlineData(-30)]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(180)]
    public void EveryCornerUsesClockwiseRotationAroundTheImageCenter(double angle)
    {
        const int width = 800, height = 300;
        OcrRect[] rectangles = [new(13.25, 29.5, 47.125, 11.75), new(654, 33, 82, 27),
            new(31, 241, 165, 19), new(635, 228, 116, 35), new(100, 50, 0, 27), new(-5, 4, 30, 12)];
        foreach (OcrRect input in rectangles)
        {
            OcrRect actual = WindowsOcrRecognizer.MapTextAngleBounds(input, angle, width, height);
            // Independent WPF matrix implementation, not the production helper's corner arithmetic.
            OcrRect expected = Oracle(input, angle, width, height);
            EqualBounds(expected, actual, width, height);
        }
    }

    [Fact]
    public void QuarterTurnHasAHandCalculatedNonOriginCenterOracle()
    {
        // Center=(100,50). (10,20)..(40,30) -> (120,-40)..(130,-10), clockwise.
        OcrRect actual = WindowsOcrRecognizer.MapTextAngleBounds(new(10, 20, 30, 10), 90, 200, 100);
        EqualBounds(new(120, -40, 10, 30), actual, 200, 100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void AngleThenUnscaleThenWholeImageRotationKeepsSourceCoordinates(int wholeRotation)
    {
        foreach (int scale in new[] { 1, 2, 3 })
        foreach (double angle in new[] { -30.0, 30.0 })
        {
            const int readingWidth = 320, readingHeight = 180;
            int sourceWidth = wholeRotation is 90 or 270 ? readingHeight : readingWidth;
            int sourceHeight = wholeRotation is 90 or 270 ? readingWidth : readingHeight;
            var prepared = new OcrRect(115 * scale, 70 * scale, 70 * scale, 22 * scale);
            OcrRect actual = WindowsOcrRecognizer.MapTextAngleBounds(prepared, angle,
                    readingWidth * scale, readingHeight * scale)
                .Unscale(scale).MapFromClockwiseRotation(wholeRotation, sourceWidth, sourceHeight);

            Matrix textRotation = Matrix.Identity;
            textRotation.RotateAt(angle, readingWidth * scale / 2.0, readingHeight * scale / 2.0);
            Point[] points = Corners(prepared);
            textRotation.Transform(points);
            Matrix restore = wholeRotation switch
            {
                0 => Matrix.Identity,
                90 => new Matrix(0, -1, 1, 0, 0, sourceHeight),
                180 => new Matrix(-1, 0, 0, -1, sourceWidth, sourceHeight),
                270 => new Matrix(0, 1, -1, 0, sourceWidth, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(wholeRotation)),
            };
            for (int i = 0; i < points.Length; i++) points[i] = restore.Transform(new Point(points[i].X / scale, points[i].Y / scale));
            EqualBounds(BoundsOf(points), actual, sourceWidth, sourceHeight);
        }
    }

    [Fact]
    public void AdapterDoesNotClipBeforeTheExistingServiceBoundary()
    {
        var input = new OcrRect(-5, -3, 20, 10);
        OcrRect mapped = WindowsOcrRecognizer.MapTextAngleBounds(input, 0, 100, 80);
        Assert.Equal(input, mapped);
        Assert.Equal(new OcrRect(0, 0, 15, 7), mapped.MapFromClockwiseRotation(0, 100, 80));
    }

    public static IEnumerable<object[]> DevelopmentFixtures() => DevelopmentCases.Select(value => new object[] { value.Id });

    [Theory]
    [MemberData(nameof(DevelopmentFixtures))]
    public void RetainedDevelopmentGeometryMapsBeforeMergingAndDoesNotRegressItsText(string id)
    {
        Replay fixture = Assert.Single(DevelopmentCases, value => value.Id == id);
        Assert.Equal("development", fixture.Truth.Split);
        Assert.Equal(64, fixture.FastSha256.Length);
        Assert.Equal(64, fixture.AccurateSha256.Length);
        string before = JsonSerializer.Serialize(fixture, Json);
        OcrLine[] mapped = MapLines(fixture.Windows, fixture.Angle, fixture.Width, fixture.Height);
        for (int row = 0; row < fixture.Windows.Length; row++)
        {
            Assert.Equal(fixture.Windows[row].Text, mapped[row].Text);
            Assert.Equal(fixture.Windows[row].Words.Select(word => word.Text), mapped[row].Words.Select(word => word.Text));
            for (int word = 0; word < fixture.Windows[row].Words.Count; word++)
                EqualBounds(Oracle(fixture.Windows[row].Words[word].Bounds, fixture.Angle, fixture.Width, fixture.Height),
                    mapped[row].Words[word].Bounds, fixture.Width, fixture.Height);
        }
        OcrResult windows = OcrResult.Success(fixture.WindowsText, fixture.WindowsLanguage, mapped, TimeSpan.Zero,
            new OcrReadingOrientation(0, fixture.Width, fixture.Height), fixture.Angle);
        OcrResult neural = OcrResult.Success(fixture.NeuralText, fixture.NeuralLanguage, fixture.Neural, TimeSpan.Zero);
        OcrResult merged = OcrResultMerger.Merge(windows, neural, TimeSpan.Zero);

        Assert.Equal(OcrStatus.Success, merged.Status);
        Assert.Distinct(merged.Lines.Select(line => Compact(line.Text)));
        Data.Metric metric = Data.Measure(fixture.Truth, merged);
        Assert.True(metric.CharacterEdits <= fixture.BaselineEdits,
            $"Development {id}: {metric.CharacterEdits} edits exceed retained baseline {fixture.BaselineEdits}. Text: {merged.Text}");
        Assert.True(merged.Lines.Count <= fixture.BaselineLineCount,
            $"Development {id}: new output lines exceed the retained baseline; inspect possible duplicates.");
        Assert.Equal(fixture.Angle, windows.TextAngle);
        Assert.Equal(before, JsonSerializer.Serialize(fixture, Json));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void CorrectedBoxesKeepIdenticalTextOnSeparateRowsAndColumns(double angle)
    {
        const int width = 800, height = 300;
        OcrLine[] raw = [Line("Repeated value", new(40, 40, 100, 18)), Line("Repeated value", new(40, 95, 100, 18)),
            Line("Other column", new(500, 40, 110, 18))];
        OcrLine[] mapped = MapLines(raw, angle, width, height);
        OcrLine[] independentNeural = raw.Select(line => Line(line.Text, Oracle(line.Bounds, angle, width, height))).ToArray();
        OcrResult merged = OcrResultMerger.Merge(Result(mapped, angle), Result(independentNeural, null), TimeSpan.Zero);

        Assert.Equal(3, merged.Lines.Count);
        Assert.Equal(2, merged.Lines.Count(line => line.Text == "Repeated value"));
        Assert.Single(merged.Lines, line => line.Text == "Other column");
        Assert.All(mapped, expected => Assert.Contains(merged.Lines, line => line.Text == expected.Text && line.Bounds == expected.Bounds));
    }

    private static OcrLine[] MapLines(OcrLine[] source, double angle, int width, int height) => source.Select(line =>
    {
        OcrWord[] words = line.Words.Select(word => word with
        {
            Bounds = WindowsOcrRecognizer.MapTextAngleBounds(word.Bounds, angle, width, height),
        }).ToArray();
        return line with { Words = words, Bounds = words.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union) };
    }).ToArray();

    private static OcrRect Oracle(OcrRect input, double angle, int width, int height)
    {
        Matrix matrix = Matrix.Identity;
        matrix.RotateAt(angle, width / 2.0, height / 2.0);
        Point[] points = Corners(input);
        matrix.Transform(points);
        return BoundsOf(points);
    }

    private static Point[] Corners(OcrRect input) => [new(input.X, input.Y), new(input.Right, input.Y),
        new(input.X, input.Bottom), new(input.Right, input.Bottom)];
    private static OcrRect BoundsOf(Point[] points) => new(points.Min(p => p.X), points.Min(p => p.Y),
        points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
    private static void EqualBounds(OcrRect expected, OcrRect actual, int width, int height)
    {
        // Conservative 64 binary64 rounding units at the image-coordinate magnitude, below
        // 3e-11 pixels here. This compares independent arithmetic, not a spatial merge budget.
        double error = 64 * 2.2204460492503131e-16 * Math.Max(width, height);
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, error);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, error);
        Assert.InRange(Math.Abs(expected.Width - actual.Width), 0, error);
        Assert.InRange(Math.Abs(expected.Height - actual.Height), 0, error);
    }
    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static OcrLine Line(string text, OcrRect bounds) => new(text, bounds, [new(text, bounds)]);
    private static OcrResult Result(OcrLine[] lines, double? angle) => OcrResult.Success(string.Join('\n', lines.Select(line => line.Text)),
        "ko-KR", lines, TimeSpan.Zero, angle);

    private sealed record Replay(string Id, int Width, int Height, double Angle, OcrLine[] Windows, OcrLine[] Neural,
        string WindowsText, string WindowsLanguage, string NeuralText, string NeuralLanguage,
        Data.Sample Truth, int BaselineEdits, string BaselineText, int BaselineLineCount, string FastSha256, string AccurateSha256);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Replay[] DevelopmentCases = JsonSerializer.Deserialize<Replay[]>(
        """
[{"id":"mixed-01-125-light","width":1250,"height":215,"angle":1.599999999999909,"windows":[{"text":"설정 Settings","bounds":{"x":40.333333333333336,"y":60.666666666666664,"width":147.33333333333334,"height":25.333333333333336,"right":187.66666666666669,"bottom":86},"words":[{"text":"설정","bounds":{"x":40.333333333333336,"y":60.666666666666664,"width":45.666666666666664,"height":24.666666666666664,"right":86,"bottom":85.33333333333333}},{"text":"Settings","bounds":{"x":98.66666666666667,"y":61,"width":89.00000000000001,"height":25,"right":187.66666666666669,"bottom":86}}]},{"text":"OCR 언어: 한국어 and English","bounds":{"x":41,"y":99.66666666666667,"width":336.3333333333333,"height":28.999999999999986,"right":377.3333333333333,"bottom":128.66666666666666},"words":[{"text":"OCR","bounds":{"x":41,"y":109,"width":49.66666666666666,"height":19.333333333333343,"right":90.66666666666666,"bottom":128.33333333333334}},{"text":"언어:","bounds":{"x":102,"y":104,"width":52.33333333333334,"height":24.666666666666657,"right":154.33333333333334,"bottom":128.66666666666666}},{"text":"한국어","bounds":{"x":164.66666666666666,"y":101.66666666666667,"width":71,"height":25.33333333333333,"right":235.66666666666666,"bottom":127}},{"text":"and","bounds":{"x":248,"y":102,"width":40.333333333333314,"height":20.66666666666667,"right":288.3333333333333,"bottom":122.66666666666667}},{"text":"English","bounds":{"x":300.3333333333333,"y":99.66666666666667,"width":77,"height":27,"right":377.3333333333333,"bottom":126.66666666666667}}]},{"text":"원본 Original 이미지를 보존합니다.","bounds":{"x":43,"y":141.33333333333334,"width":393.66666666666663,"height":35.66666666666666,"right":436.66666666666663,"bottom":177},"words":[{"text":"원본","bounds":{"x":43,"y":150.66666666666666,"width":47.66666666666666,"height":24.666666666666657,"right":90.66666666666666,"bottom":175.33333333333331}},{"text":"Original","bounds":{"x":101,"y":150,"width":86.66666666666669,"height":27,"right":187.66666666666669,"bottom":177}},{"text":"이미지를","bounds":{"x":200.33333333333334,"y":145,"width":96.66666666666666,"height":25.666666666666657,"right":297,"bottom":170.66666666666666}},{"text":"보존합니다.","bounds":{"x":308,"y":141.33333333333334,"width":128.66666666666663,"height":26,"right":436.66666666666663,"bottom":167.33333333333334}}]}],"neural":[{"text":"설정 Settings","bounds":{"x":36,"y":38,"width":157,"height":38,"right":193,"bottom":76},"words":[{"text":"설정","bounds":{"x":43,"y":38,"width":43,"height":38,"right":86,"bottom":76}},{"text":"Settings","bounds":{"x":99,"y":38,"width":94,"height":38,"right":193,"bottom":76}}]},{"text":"OCR 언어:한국어 and English","bounds":{"x":34,"y":83,"width":346,"height":38,"right":380,"bottom":121},"words":[{"text":"OCR","bounds":{"x":42,"y":83,"width":47,"height":37,"right":89,"bottom":120}},{"text":"언어:한국어","bounds":{"x":105,"y":83,"width":127,"height":38,"right":232,"bottom":121}},{"text":"and","bounds":{"x":248,"y":84,"width":40,"height":37,"right":288,"bottom":121}},{"text":"English","bounds":{"x":297,"y":85,"width":81,"height":36,"right":378,"bottom":121}}]},{"text":"원본 Original 이미지를 보존합니다.","bounds":{"x":36,"y":130,"width":402,"height":36,"right":438,"bottom":166},"words":[{"text":"원본","bounds":{"x":41,"y":130,"width":44,"height":36,"right":85,"bottom":166}},{"text":"Original","bounds":{"x":101,"y":130,"width":92,"height":36,"right":193,"bottom":166}},{"text":"이미지를","bounds":{"x":203,"y":130,"width":92,"height":36,"right":295,"bottom":166}},{"text":"보존합니다.","bounds":{"x":311,"y":130,"width":127,"height":36,"right":438,"bottom":166}}]}],"truth":{"id":"mixed-01-125-light","sceneId":"mixed-01","category":"mixed","split":"development","languages":["ko-KR","en-US"],"scale":1.25,"theme":"light","fontSize":20,"requestedFont":"Malgun Gothic","lowContrast":false,"width":1250,"height":215,"text":"설정 Settings\nOCR 언어: 한국어 and English\n원본 Original 이미지를 보존합니다.","lines":[{"text":"설정 Settings","bounds":{"x":41.8310546875,"y":45.859901428222656,"width":146.52574157714844,"height":27.392578125},"cells":["설정 Settings"]},{"text":"OCR 언어: 한국어 and English","bounds":{"x":41.1962890625,"y":90.85990142822266,"width":335.5935363769531,"height":27.392578125},"cells":["OCR 언어: 한국어 and English"]},{"text":"원본 Original 이미지를 보존합니다.","bounds":{"x":42.24609375,"y":135.5303192138672,"width":392.34771728515625,"height":27.72216796875},"cells":["원본 Original 이미지를 보존합니다."]}],"fontUris":[],"png":"mixed-01-125-light.png","pngSha256":"CC809D0C88172E8C198AADDD8D32D5E2244A7FE3B68FD7BC3DC9E9AABCE54052","pixelSha256":"F86DF8BAAC3B34CA0AFA37833C869C8111732BC2B1C5CB97A94C835F6EA51DA8","truthSha256":"E993E0A5ADF0199CF2FEE43FC13FAA6223908A0CA4392C1D0C0296F1E41EAE09"},"baselineEdits":0,"baselineText":"설정 Settings\nOCR 언어: 한국어 and English\n원본 Original 이미지를 보존합니다.","baselineLineCount":3,"fastSha256":"846F566DC8692CFF5C5092DE58A461B5ECE9DF7B79840467D5BBE8147262CB25","accurateSha256":"2F8AB33E799A8F99C9B1EBADF9F215F19992A602C78AA3283E48AB33CD26BE92","windowsText":"설정 Settings\nOCR 언어: 한국어 and English\n원본 Original 이미지를 보존합니다.","windowsLanguage":"ko","neuralText":"설정 Settings\nOCR 언어:한국어 and English\n원본 Original 이미지를 보존합니다.","neuralLanguage":"ko-KR"},{"id":"mixed-01-125-dark","width":1250,"height":215,"angle":1.599999999999909,"windows":[{"text":"설정 Settings","bounds":{"x":40.333333333333336,"y":60.666666666666664,"width":147.33333333333334,"height":25.333333333333336,"right":187.66666666666669,"bottom":86},"words":[{"text":"설정","bounds":{"x":40.333333333333336,"y":60.666666666666664,"width":45.666666666666664,"height":24.666666666666664,"right":86,"bottom":85.33333333333333}},{"text":"Settings","bounds":{"x":98.66666666666667,"y":61,"width":89.00000000000001,"height":25,"right":187.66666666666669,"bottom":86}}]},{"text":"OCR 언어: 한국어 and English","bounds":{"x":41,"y":100,"width":336.3333333333333,"height":28.666666666666657,"right":377.3333333333333,"bottom":128.66666666666666},"words":[{"text":"OCR","bounds":{"x":41,"y":109,"width":49.66666666666666,"height":19.333333333333343,"right":90.66666666666666,"bottom":128.33333333333334}},{"text":"언어:","bounds":{"x":102,"y":104,"width":52.33333333333334,"height":24.666666666666657,"right":154.33333333333334,"bottom":128.66666666666666}},{"text":"한국어","bounds":{"x":164.66666666666666,"y":101.66666666666667,"width":71,"height":25.33333333333333,"right":235.66666666666666,"bottom":127}},{"text":"and","bounds":{"x":248,"y":102,"width":40.333333333333314,"height":20.66666666666667,"right":288.3333333333333,"bottom":122.66666666666667}},{"text":"English","bounds":{"x":300.3333333333333,"y":100,"width":77,"height":26.66666666666667,"right":377.3333333333333,"bottom":126.66666666666667}}]},{"text":"원본 Original 이미지를 보존합니다.","bounds":{"x":43,"y":141.33333333333334,"width":393.66666666666663,"height":35.66666666666666,"right":436.66666666666663,"bottom":177},"words":[{"text":"원본","bounds":{"x":43,"y":151,"width":47.66666666666666,"height":24.333333333333343,"right":90.66666666666666,"bottom":175.33333333333334}},{"text":"Original","bounds":{"x":101,"y":150,"width":86.66666666666669,"height":27,"right":187.66666666666669,"bottom":177}},{"text":"이미지를","bounds":{"x":200.33333333333334,"y":145,"width":96.66666666666666,"height":25.666666666666657,"right":297,"bottom":170.66666666666666}},{"text":"보존합니다.","bounds":{"x":308,"y":141.33333333333334,"width":128.66666666666663,"height":26,"right":436.66666666666663,"bottom":167.33333333333334}}]}],"neural":[{"text":"설정 Settings","bounds":{"x":33,"y":34,"width":163,"height":48,"right":196,"bottom":82},"words":[{"text":"설정","bounds":{"x":43,"y":34,"width":39,"height":44,"right":82,"bottom":78}},{"text":"Settings","bounds":{"x":93,"y":35,"width":103,"height":47,"right":196,"bottom":82}}]},{"text":"OCR","bounds":{"x":35,"y":83,"width":62,"height":35,"right":97,"bottom":118},"words":[{"text":"OCR","bounds":{"x":41,"y":83,"width":50,"height":35,"right":91,"bottom":118}}]},{"text":"언어:한국어 and English","bounds":{"x":86,"y":82,"width":296,"height":41,"right":382,"bottom":123},"words":[{"text":"언어:한국어","bounds":{"x":107,"y":82,"width":122,"height":39,"right":229,"bottom":121}},{"text":"and","bounds":{"x":245,"y":84,"width":40,"height":38,"right":285,"bottom":122}},{"text":"English","bounds":{"x":295,"y":84,"width":79,"height":39,"right":374,"bottom":123}}]},{"text":"원본 Original 이미지를 보존합니다.","bounds":{"x":34,"y":125,"width":406,"height":42,"right":440,"bottom":167},"words":[{"text":"원본","bounds":{"x":42,"y":125,"width":40,"height":42,"right":82,"bottom":167}},{"text":"Original","bounds":{"x":98,"y":125,"width":96,"height":42,"right":194,"bottom":167}},{"text":"이미지를","bounds":{"x":203,"y":125,"width":89,"height":42,"right":292,"bottom":167}},{"text":"보존합니다.","bounds":{"x":308,"y":125,"width":131,"height":42,"right":439,"bottom":167}}]}],"truth":{"id":"mixed-01-125-dark","sceneId":"mixed-01","category":"mixed","split":"development","languages":["ko-KR","en-US"],"scale":1.25,"theme":"dark","fontSize":20,"requestedFont":"Malgun Gothic","lowContrast":false,"width":1250,"height":215,"text":"설정 Settings\nOCR 언어: 한국어 and English\n원본 Original 이미지를 보존합니다.","lines":[{"text":"설정 Settings","bounds":{"x":41.8310546875,"y":45.859901428222656,"width":146.52574157714844,"height":27.392578125},"cells":["설정 Settings"]},{"text":"OCR 언어: 한국어 and English","bounds":{"x":41.1962890625,"y":90.85990142822266,"width":335.5935363769531,"height":27.392578125},"cells":["OCR 언어: 한국어 and English"]},{"text":"원본 Original 이미지를 보존합니다.","bounds":{"x":42.24609375,"y":135.5303192138672,"width":392.34771728515625,"height":27.72216796875},"cells":["원본 Original 이미지를 보존합니다."]}],"fontUris":[],"png":"mixed-01-125-dark.png","pngSha256":"A64AEB080AFA0EAEA98DEDDE01F2C9DC09B7F6BAF1FE77A3E71D4D45EA447643","pixelSha256":"C73F1782429EBC23E7592A84093D991089B7D018C24CB5DC559BE465C1824603","truthSha256":"DD120AF6A88DD97CBAB5D46AB5DD6142C5CF2C9DE4AF276AB3A15D3B26ACEE6E"},"baselineEdits":2,"baselineText":"설정 Settings\nOCR\n언어:한국어 and English\n원본 Original 이미지를 보존합니다.","baselineLineCount":4,"fastSha256":"B3BC9CFA3B00304954D7D32022FEF4B77850B6DC0D5ABBFC9387A902C94DA3A5","accurateSha256":"77DAAFE556FB3043348450B3A3C40B7FEA3D565E563675E6F59B8167459822BC","windowsText":"설정 Settings\nOCR 언어: 한국어 and English\n원본 Original 이미지를 보존합니다.","windowsLanguage":"ko","neuralText":"설정 Settings\nOCR\n언어:한국어 and English\n원본 Original 이미지를 보존합니다.","neuralLanguage":"ko-KR"},{"id":"mixed-04-125-light","width":1250,"height":215,"angle":-2,"windows":[{"text":"lnvoice 청구서","bounds":{"x":43.666666666666664,"y":27.666666666666668,"width":159.00000000000003,"height":26.666666666666668,"right":202.66666666666669,"bottom":54.333333333333336},"words":[{"text":"lnvoice","bounds":{"x":43.666666666666664,"y":28.333333333333332,"width":77.33333333333334,"height":21.333333333333332,"right":121,"bottom":49.666666666666664}},{"text":"청구서","bounds":{"x":132.33333333333334,"y":27.666666666666668,"width":70.33333333333334,"height":26.666666666666668,"right":202.66666666666669,"bottom":54.333333333333336}}]},{"text":"Subt0taI 23 400원","bounds":{"x":41,"y":73.33333333333333,"width":199,"height":27.66666666666667,"right":240,"bottom":101},"words":[{"text":"Subt0taI","bounds":{"x":41,"y":73.33333333333333,"width":93,"height":21.66666666666667,"right":134,"bottom":95}},{"text":"23","bounds":{"x":144,"y":77,"width":25.666666666666657,"height":19.33333333333333,"right":169.66666666666666,"bottom":96.33333333333333}},{"text":"400원","bounds":{"x":176.33333333333334,"y":77,"width":63.66666666666666,"height":24,"right":240,"bottom":101}}]},{"text":"부가세 Tax 2,340원","bounds":{"x":39.666666666666664,"y":116.66666666666667,"width":212.66666666666666,"height":29.666666666666643,"right":252.33333333333331,"bottom":146.33333333333331},"words":[{"text":"부가세","bounds":{"x":39.666666666666664,"y":116.66666666666667,"width":72,"height":24.66666666666667,"right":111.66666666666666,"bottom":141.33333333333334}},{"text":"Tax","bounds":{"x":123,"y":121.33333333333333,"width":37.33333333333334,"height":19.66666666666667,"right":160.33333333333334,"bottom":141}},{"text":"2,340원","bounds":{"x":170,"y":122.33333333333333,"width":82.33333333333331,"height":23.999999999999986,"right":252.33333333333331,"bottom":146.33333333333331}}]}],"neural":[{"text":"Invoice 청구서","bounds":{"x":35,"y":38,"width":170,"height":34,"right":205,"bottom":72},"words":[{"text":"Invoice","bounds":{"x":35,"y":38,"width":86,"height":34,"right":121,"bottom":72}},{"text":"청구서","bounds":{"x":131,"y":38,"width":69,"height":34,"right":200,"bottom":72}}]},{"text":"Subtotal 23,400원","bounds":{"x":34,"y":85,"width":211,"height":36,"right":245,"bottom":121},"words":[{"text":"Subtotal","bounds":{"x":43,"y":85,"width":91,"height":36,"right":134,"bottom":121}},{"text":"23,400원","bounds":{"x":145,"y":85,"width":91,"height":36,"right":236,"bottom":121}}]},{"text":"부가세 Tax 2,340원","bounds":{"x":35,"y":128,"width":222,"height":37,"right":257,"bottom":165},"words":[{"text":"부가세","bounds":{"x":42,"y":128,"width":66,"height":37,"right":108,"bottom":165}},{"text":"Tax","bounds":{"x":122,"y":128,"width":42,"height":37,"right":164,"bottom":165}},{"text":"2,340원","bounds":{"x":165,"y":128,"width":85,"height":37,"right":250,"bottom":165}}]}],"truth":{"id":"mixed-04-125-light","sceneId":"mixed-04","category":"mixed","split":"development","languages":["ko-KR","en-US"],"scale":1.25,"theme":"light","fontSize":20,"requestedFont":"Malgun Gothic","lowContrast":false,"width":1250,"height":215,"text":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","lines":[{"text":"Invoice 청구서","bounds":{"x":42.392578125,"y":45.640174865722656,"width":157.26553344726562,"height":23.76708984375},"cells":["Invoice 청구서"]},{"text":"Subtotal 23,400원","bounds":{"x":41.5380859375,"y":90.85990142822266,"width":197.85025024414062,"height":24.67041015625},"cells":["Subtotal 23,400원"]},{"text":"부가세 Tax 2,340원","bounds":{"x":41.57470703125,"y":135.8599090576172,"width":211.3936309814453,"height":24.67041015625},"cells":["부가세 Tax 2,340원"]}],"fontUris":[],"png":"mixed-04-125-light.png","pngSha256":"D9916B7587758ADB8A5E37CA5BB4420C5240432ABCAA101D95821B0F37DB3AB1","pixelSha256":"BD047A3487ADDEE4DE3CD6D3AC21CC7F56FF9B32950853D55B3EA102D15E7848","truthSha256":"D6ADB995A93B467DE8B34AC41B9BA48C45F08D04235F5BB34FDB11FBE587807A"},"baselineEdits":0,"baselineText":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","baselineLineCount":3,"fastSha256":"E1460F25554DFE9019B70F8922D592AD814C6E4B2A4D272729E50B8DDECC48F7","accurateSha256":"87B6E07E3A73C35302022FEBDF8BE03321E2995008C7F4397072E0BE11DA95CD","windowsText":"lnvoice 청구서\nSubt0taI 23 400원\n부가세 Tax 2,340원","windowsLanguage":"ko","neuralText":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","neuralLanguage":"ko-KR"},{"id":"mixed-04-125-dark","width":1250,"height":215,"angle":-2,"windows":[{"text":"lnvoice 청구서","bounds":{"x":43.666666666666664,"y":27.666666666666668,"width":159.00000000000003,"height":26.666666666666668,"right":202.66666666666669,"bottom":54.333333333333336},"words":[{"text":"lnvoice","bounds":{"x":43.666666666666664,"y":28.333333333333332,"width":77.33333333333334,"height":21.333333333333332,"right":121,"bottom":49.666666666666664}},{"text":"청구서","bounds":{"x":132.33333333333334,"y":27.666666666666668,"width":70.33333333333334,"height":26.666666666666668,"right":202.66666666666669,"bottom":54.333333333333336}}]},{"text":"Subt0taI 23 400원","bounds":{"x":41,"y":73.33333333333333,"width":199,"height":27.66666666666667,"right":240,"bottom":101},"words":[{"text":"Subt0taI","bounds":{"x":41,"y":73.33333333333333,"width":93,"height":21.66666666666667,"right":134,"bottom":95}},{"text":"23","bounds":{"x":144,"y":77,"width":25.333333333333343,"height":19.33333333333333,"right":169.33333333333334,"bottom":96.33333333333333}},{"text":"400원","bounds":{"x":176.33333333333334,"y":77,"width":63.66666666666666,"height":24,"right":240,"bottom":101}}]},{"text":"부가세 Tax 2,340원","bounds":{"x":39.666666666666664,"y":116.66666666666667,"width":212.66666666666666,"height":29.666666666666643,"right":252.33333333333331,"bottom":146.33333333333331},"words":[{"text":"부가세","bounds":{"x":39.666666666666664,"y":116.66666666666667,"width":71.66666666666669,"height":24.66666666666667,"right":111.33333333333334,"bottom":141.33333333333334}},{"text":"Tax","bounds":{"x":123,"y":121.33333333333333,"width":37.33333333333334,"height":19.66666666666667,"right":160.33333333333334,"bottom":141}},{"text":"2,340원","bounds":{"x":170,"y":122.33333333333333,"width":82.33333333333331,"height":23.999999999999986,"right":252.33333333333331,"bottom":146.33333333333331}}]}],"neural":[{"text":"Invoice 청구서","bounds":{"x":34,"y":35,"width":173,"height":40,"right":207,"bottom":75},"words":[{"text":"Invoice","bounds":{"x":34,"y":36,"width":89,"height":39,"right":123,"bottom":75}},{"text":"청구서","bounds":{"x":130,"y":35,"width":70,"height":39,"right":200,"bottom":74}}]},{"text":"Subtotal 23,400원","bounds":{"x":32,"y":80,"width":213,"height":40,"right":245,"bottom":120},"words":[{"text":"Subtotal","bounds":{"x":42,"y":80,"width":97,"height":39,"right":139,"bottom":119}},{"text":"23,400원","bounds":{"x":145,"y":81,"width":90,"height":39,"right":235,"bottom":120}}]},{"text":"부가세 Tax 2,340원","bounds":{"x":34,"y":127,"width":225,"height":39,"right":259,"bottom":166},"words":[{"text":"부가세","bounds":{"x":48,"y":127,"width":62,"height":38,"right":110,"bottom":165}},{"text":"Tax","bounds":{"x":125,"y":127,"width":43,"height":39,"right":168,"bottom":166}},{"text":"2,340원","bounds":{"x":170,"y":128,"width":88,"height":38,"right":258,"bottom":166}}]}],"truth":{"id":"mixed-04-125-dark","sceneId":"mixed-04","category":"mixed","split":"development","languages":["ko-KR","en-US"],"scale":1.25,"theme":"dark","fontSize":20,"requestedFont":"Malgun Gothic","lowContrast":false,"width":1250,"height":215,"text":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","lines":[{"text":"Invoice 청구서","bounds":{"x":42.392578125,"y":45.640174865722656,"width":157.26553344726562,"height":23.76708984375},"cells":["Invoice 청구서"]},{"text":"Subtotal 23,400원","bounds":{"x":41.5380859375,"y":90.85990142822266,"width":197.85025024414062,"height":24.67041015625},"cells":["Subtotal 23,400원"]},{"text":"부가세 Tax 2,340원","bounds":{"x":41.57470703125,"y":135.8599090576172,"width":211.3936309814453,"height":24.67041015625},"cells":["부가세 Tax 2,340원"]}],"fontUris":[],"png":"mixed-04-125-dark.png","pngSha256":"851FCD8E2B714A6C57015180B64246E69ECA1203DD59EF7CBA525C8AA9FD9EFE","pixelSha256":"51703DAD13335E58A09E134B16C28E876D85FA21C89E709361D9D1BAE16CE900","truthSha256":"D818937611C0CB6BD3B23941AC9DD73342FA9991EBE8F5C0C68FB6D2DA0CED43"},"baselineEdits":0,"baselineText":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","baselineLineCount":3,"fastSha256":"565DDA8BA148D3AFE3F4D481D16DEDE46EABE31413EF4487523167632728EF64","accurateSha256":"3DF5C46362176B53685640AA46EF5F1C8D2EA45935DEE5E9130A94F74BD2B7D2","windowsText":"lnvoice 청구서\nSubt0taI 23 400원\n부가세 Tax 2,340원","windowsLanguage":"ko","neuralText":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","neuralLanguage":"ko-KR"},{"id":"mixed-04-150-dark","width":1500,"height":258,"angle":1.1999999999999318,"windows":[{"text":"lnvoice 청구서","bounds":{"x":49,"y":65.33333333333333,"width":190.33333333333334,"height":30.66666666666667,"right":239.33333333333334,"bottom":96},"words":[{"text":"lnvoice","bounds":{"x":49,"y":72,"width":93,"height":24,"right":142,"bottom":96}},{"text":"청구서","bounds":{"x":155.33333333333334,"y":65.33333333333333,"width":84,"height":30.66666666666667,"right":239.33333333333334,"bottom":96}}]},{"text":"SubtotaI 23 400원","bounds":{"x":49.666666666666664,"y":118.33333333333333,"width":238.33333333333334,"height":31.66666666666667,"right":288,"bottom":150},"words":[{"text":"SubtotaI","bounds":{"x":49.666666666666664,"y":124,"width":110,"height":26,"right":159.66666666666666,"bottom":150}},{"text":"23","bounds":{"x":173,"y":124.33333333333333,"width":30.333333333333343,"height":22.999999999999986,"right":203.33333333333334,"bottom":147.33333333333331}},{"text":"400원","bounds":{"x":211,"y":118.33333333333333,"width":77,"height":28.999999999999986,"right":288,"bottom":147.33333333333331}}]},{"text":"부가세 Tax 2,340원","bounds":{"x":50.333333333333336,"y":172,"width":255.33333333333334,"height":34.66666666666666,"right":305.6666666666667,"bottom":206.66666666666666},"words":[{"text":"부가세","bounds":{"x":50.333333333333336,"y":175.66666666666666,"width":85.33333333333331,"height":31,"right":135.66666666666666,"bottom":206.66666666666666}},{"text":"Tax","bounds":{"x":149.66666666666666,"y":179,"width":45.66666666666666,"height":22.666666666666657,"right":195.33333333333331,"bottom":201.66666666666666}},{"text":"2,340원","bounds":{"x":207.33333333333334,"y":172,"width":98.33333333333334,"height":32.33333333333334,"right":305.6666666666667,"bottom":204.33333333333334}}]}],"neural":[{"text":"Invoice 청구서","bounds":{"x":42,"y":43,"width":205,"height":50,"right":247,"bottom":93},"words":[{"text":"Invoice","bounds":{"x":42,"y":43,"width":110,"height":50,"right":152,"bottom":93}},{"text":"청구서","bounds":{"x":163,"y":43,"width":83,"height":50,"right":246,"bottom":93}}]},{"text":"Subtotal 23,400원","bounds":{"x":42,"y":99,"width":250,"height":46,"right":292,"bottom":145},"words":[{"text":"Subtotal","bounds":{"x":45,"y":100,"width":119,"height":45,"right":164,"bottom":145}},{"text":"23,400원","bounds":{"x":170,"y":99,"width":111,"height":45,"right":281,"bottom":144}}]},{"text":"부가세 Tax 2,340원","bounds":{"x":42,"y":151,"width":268,"height":48,"right":310,"bottom":199},"words":[{"text":"부가세","bounds":{"x":52,"y":151,"width":77,"height":48,"right":129,"bottom":199}},{"text":"Tax","bounds":{"x":150,"y":151,"width":52,"height":48,"right":202,"bottom":199}},{"text":"2,340원","bounds":{"x":206,"y":151,"width":102,"height":48,"right":308,"bottom":199}}]}],"truth":{"id":"mixed-04-150-dark","sceneId":"mixed-04","category":"mixed","split":"development","languages":["ko-KR","en-US"],"scale":1.5,"theme":"dark","fontSize":20,"requestedFont":"Malgun Gothic","lowContrast":false,"width":1500,"height":258,"text":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","lines":[{"text":"Invoice 청구서","bounds":{"x":50.87109375,"y":54.766212463378906,"width":188.7146453857422,"height":28.5205078125},"cells":["Invoice 청구서"]},{"text":"Subtotal 23,400원","bounds":{"x":49.845703125,"y":109.02987670898438,"width":237.42428588867188,"height":29.6044921875},"cells":["Subtotal 23,400원"]},{"text":"부가세 Tax 2,340원","bounds":{"x":49.8896484375,"y":163.02987670898438,"width":253.66702270507812,"height":29.6044921875},"cells":["부가세 Tax 2,340원"]}],"fontUris":[],"png":"mixed-04-150-dark.png","pngSha256":"D7ACE2D0E47214140969D9183702A355EEA5F0823288D6AE1EE01A08479C34A9","pixelSha256":"091AC21A203A1F22BF5E71BE015B9D36E09E84BF20F2A17A5D3D0AC65B38CFC7","truthSha256":"43C1C6DD11C2FB6FF761D641B8E2232EC0C76EDA608ACAAF7E1B15A15BC7D899"},"baselineEdits":0,"baselineText":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","baselineLineCount":3,"fastSha256":"C41C6FC28261D45FCF750AE63C846296E7E96811BBE6C986D6F01E03D26B3830","accurateSha256":"C1245D8A27E68CE5AEF5AB4C7AEEA536C4B502065BF6963B15F416CA842847D2","windowsText":"lnvoice 청구서\nSubtotaI 23 400원\n부가세 Tax 2,340원","windowsLanguage":"ko","neuralText":"Invoice 청구서\nSubtotal 23,400원\n부가세 Tax 2,340원","neuralLanguage":"ko-KR"},{"id":"mixed-05-150-light","width":1500,"height":258,"angle":-1.5,"windows":[{"text":"키보드 Shortcut 설정","bounds":{"x":52,"y":37,"width":279.3333333333333,"height":35,"right":331.3333333333333,"bottom":72},"words":[{"text":"키보드","bounds":{"x":52,"y":37,"width":86.33333333333331,"height":29.66666666666667,"right":138.33333333333331,"bottom":66.66666666666667}},{"text":"Shortcut","bounds":{"x":151.33333333333334,"y":42.333333333333336,"width":113.00000000000003,"height":26.333333333333336,"right":264.33333333333337,"bottom":68.66666666666667}},{"text":"설정","bounds":{"x":277.3333333333333,"y":42.666666666666664,"width":54,"height":29.333333333333336,"right":331.3333333333333,"bottom":72}}]},{"text":"ctrl + shift + 영역 캡치","bounds":{"x":49.333333333333336,"y":94.33333333333333,"width":347.6666666666667,"height":34.66666666666667,"right":397,"bottom":129},"words":[{"text":"ctrl","bounds":{"x":49.333333333333336,"y":94.33333333333333,"width":44.666666666666664,"height":23.66666666666667,"right":94,"bottom":118}},{"text":"+","bounds":{"x":109,"y":102,"width":15.666666666666671,"height":14.666666666666671,"right":124.66666666666667,"bottom":116.66666666666667}},{"text":"shift","bounds":{"x":138.66666666666666,"y":96,"width":59.66666666666666,"height":25,"right":198.33333333333331,"bottom":121}},{"text":"+","bounds":{"x":212,"y":104.66666666666667,"width":15.666666666666657,"height":14.666666666666671,"right":227.66666666666666,"bottom":119.33333333333334}},{"text":"영역","bounds":{"x":274.3333333333333,"y":96.66666666666667,"width":52.333333333333314,"height":30.66666666666667,"right":326.66666666666663,"bottom":127.33333333333334}},{"text":"캡치","bounds":{"x":343,"y":98.66666666666667,"width":54,"height":30.33333333333333,"right":397,"bottom":129}}]},{"text":"Esc 취소, Enter 확인","bounds":{"x":49,"y":146,"width":265,"height":34,"right":314,"bottom":180},"words":[{"text":"Esc","bounds":{"x":49,"y":148.33333333333334,"width":40.33333333333334,"height":23.666666666666657,"right":89.33333333333334,"bottom":172}},{"text":"취소,","bounds":{"x":101.66666666666667,"y":146,"width":63.33333333333333,"height":32,"right":165,"bottom":178}},{"text":"Enter","bounds":{"x":179.33333333333334,"y":152,"width":68,"height":24,"right":247.33333333333334,"bottom":176}},{"text":"확인","bounds":{"x":258.6666666666667,"y":150,"width":55.333333333333314,"height":30,"right":314,"bottom":180}}]}],"neural":[{"text":"키보드 Shortcut 설정","bounds":{"x":42,"y":43,"width":294,"height":48,"right":336,"bottom":91},"words":[{"text":"키보드","bounds":{"x":50,"y":43,"width":80,"height":48,"right":130,"bottom":91}},{"text":"Shortcut","bounds":{"x":145,"y":43,"width":120,"height":48,"right":265,"bottom":91}},{"text":"설정","bounds":{"x":272,"y":43,"width":56,"height":48,"right":328,"bottom":91}}]},{"text":"Ctrl +Shift + C 영역 캡처","bounds":{"x":42,"y":99,"width":361,"height":44,"right":403,"bottom":143},"words":[{"text":"Ctrl","bounds":{"x":48,"y":101,"width":56,"height":42,"right":104,"bottom":143}},{"text":"+Shift","bounds":{"x":104,"y":100,"width":99,"height":43,"right":203,"bottom":143}},{"text":"+","bounds":{"x":209,"y":100,"width":23,"height":43,"right":232,"bottom":143}},{"text":"C","bounds":{"x":237,"y":100,"width":23,"height":42,"right":260,"bottom":142}},{"text":"영역","bounds":{"x":272,"y":99,"width":52,"height":43,"right":324,"bottom":142}},{"text":"캡처","bounds":{"x":344,"y":99,"width":59,"height":43,"right":403,"bottom":142}}]},{"text":"Esc 취소,Enter 확인","bounds":{"x":44,"y":155,"width":276,"height":42,"right":320,"bottom":197},"words":[{"text":"Esc","bounds":{"x":45,"y":155,"width":48,"height":42,"right":93,"bottom":197}},{"text":"취소,Enter","bounds":{"x":108,"y":155,"width":141,"height":42,"right":249,"bottom":197}},{"text":"확인","bounds":{"x":264,"y":155,"width":48,"height":42,"right":312,"bottom":197}}]}],"truth":{"id":"mixed-05-150-light","sceneId":"mixed-05","category":"mixed","split":"development","languages":["ko-KR","en-US"],"scale":1.5,"theme":"light","fontSize":20,"requestedFont":"Malgun Gothic","lowContrast":false,"width":1500,"height":258,"text":"키보드 Shortcut 설정\nCtrl + Shift + C 영역 캡처\nEsc 취소, Enter 확인","lines":[{"text":"키보드 Shortcut 설정","bounds":{"x":50.607421875,"y":55.029884338378906,"width":278.8829345703125,"height":28.2568359375},"cells":["키보드 Shortcut 설정"]},{"text":"Ctrl + Shift + C 영역 캡처","bounds":{"x":49.435546875,"y":109.02987670898438,"width":346.5735168457031,"height":28.2568359375},"cells":["Ctrl + Shift + C 영역 캡처"]},{"text":"Esc 취소, Enter 확인","bounds":{"x":50.8564453125,"y":162.79550170898438,"width":264.0263671875,"height":29.8388671875},"cells":["Esc 취소, Enter 확인"]}],"fontUris":[],"png":"mixed-05-150-light.png","pngSha256":"24B6C1332CE7E2B33C42E57309E7CE4DE90C12BC8C75936D0F443D4D47AB55D0","pixelSha256":"7BC8A39FC5DE4729282F3F2AA9170AC5E71C5D2691E6758D38FC4A4A96FDD634","truthSha256":"FA7E1D69C8E4B266344E5B015E7F9BD4980F29208771571DBEADB5F1315F15C2"},"baselineEdits":1,"baselineText":"키보드 Shortcut 설정\nCtrl +Shift + C 영역 캡처\nEsc 취소, Enter 확인","baselineLineCount":3,"fastSha256":"460252E77007519CD287483302EE277F29054DD0A31F3362E5A437639C6EF9EF","accurateSha256":"44EB8F17A65F60CF1335AD52A5C0009EFC58F4129137E2BD0BA0DC12E111864C","windowsText":"키보드 Shortcut 설정\nctrl + shift + 영역 캡치\nEsc 취소, Enter 확인","windowsLanguage":"ko","neuralText":"키보드 Shortcut 설정\nCtrl +Shift + C 영역 캡처\nEsc 취소,Enter 확인","neuralLanguage":"ko-KR"}]
""", Json) ?? throw new InvalidOperationException("Missing retained development geometry.");
}
