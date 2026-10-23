using System.Reflection;
using RapidOcrNet;
using SkiaSharp;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class OcrModelBenchmarkTests
{
    // Diagnostic-only mirror of RapidOcrNet 4.2.0 DetectOnce, ending before its
    // blank-text and TextScore filters. Inference remains outside benchmark timing.
    private static object DiagnoseBeforeFilter(RapidOcr engine, SKBitmap decoded, RapidOcrOptions options)
    {
        Assembly assembly = typeof(RapidOcr).Assembly;
        Assert.Equal(new Version(4, 2, 0, 0), assembly.GetName().Version);
        Assert.Equal(new Guid("ad1ba858-a12a-4a79-ad4e-484dcbf298c1"), assembly.ManifestModule.ModuleVersionId);

        Type rapidType = typeof(RapidOcr);
        Type utilsType = assembly.GetType("RapidOcrNet.OcrUtils", throwOnError: true)!;
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags publicStatic = BindingFlags.Static | BindingFlags.Public;
        MethodInfo prepare = RequiredMethod(rapidType, "PrepareDetectorInput", privateStatic);
        MethodInfo crop = RequiredMethod(utilsType, "GetPartImages", publicStatic);
        MethodInfo rotate = RequiredMethod(utilsType, "BitmapRotateClockWise180", publicStatic);
        var detector = (TextDetector)RequiredField(rapidType, "_textDetector", privateInstance).GetValue(engine)!;
        var classifier = (TextClassifier)RequiredField(rapidType, "_textClassifier", privateInstance).GetValue(engine)!;
        var recognizer = (TextRecognizer)RequiredField(rapidType, "_textRecognizer", privateInstance).GetValue(engine)!;

        object input = prepare.Invoke(null, [decoded, options, CancellationToken.None])
            ?? throw new InvalidOperationException("RapidOcrNet did not prepare diagnostic input.");
        var lines = new List<PrefilterLine>();
        try
        {
            Type inputType = input.GetType();
            var detectorBitmap = (SKBitmap)RequiredField(inputType, "Bitmap", BindingFlags.Instance | BindingFlags.Public).GetValue(input)!;
            var scale = (ScaleParam)RequiredField(inputType, "Scale", BindingFlags.Instance | BindingFlags.Public).GetValue(input)!;
            MethodInfo map = RequiredMethod(inputType, "MapToOriginal", BindingFlags.Instance | BindingFlags.Public);
            IReadOnlyList<TextBox> boxes = detector.GetTextBoxes(detectorBitmap, scale,
                options.BoxScoreThresh, options.BoxThresh, options.UnClipRatio, CancellationToken.None) ?? [];

            // GetRotateCropImage without its context out-parameter delegates to the
            // same implementation used by GetPartImagesWithContext. Thus crops are
            // identical to ReturnWordBox=true, without reconstructing unused words.
            var parts = (SKBitmap[])(crop.Invoke(null, [detectorBitmap, boxes])
                ?? throw new InvalidOperationException("RapidOcrNet did not return diagnostic crops."));
            try
            {
                Assert.Equal(boxes.Count, parts.Length);
                Angle[] angles = classifier.GetAngles(parts, options.DoAngle, options.MostAngle,
                    options.ClsPreserveAspectRatio, CancellationToken.None);
                Assert.Equal(parts.Length, angles.Length);
                var appliedAngles = new int[angles.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    appliedAngles[i] = angles[i].Index;
                    if (angles[i].Index == 1 && angles[i].Score >= options.ClsThresh)
                    {
                        SKBitmap original = parts[i];
                        parts[i] = (SKBitmap)(rotate.Invoke(null, [original])
                            ?? throw new InvalidOperationException("RapidOcrNet did not return a rotated diagnostic crop."));
                        original.Dispose();
                    }
                    else if (angles[i].Index == 1)
                    {
                        appliedAngles[i] = 0;
                    }
                }

                TextLine[] recognized = recognizer.GetTextLines(parts, options.RecMaxDegreeOfParallelism,
                    null, CancellationToken.None);
                Assert.Equal(boxes.Count, recognized.Length);
                for (int i = 0; i < recognized.Length; i++)
                {
                    TextLine line = recognized[i];
                    string[]? chars = line.Chars?.ToArray();
                    float[]? scores = line.CharScores?.ToArray();
                    string text = chars is { Length: > 0 } ? string.Concat(chars) : string.Empty;
                    float sum = 0;
                    foreach (float score in scores ?? []) sum += score;
                    float? mean = scores is { Length: > 0 } ? sum / scores.Length : null;
                    bool blank = chars is not { Length: > 0 } || string.IsNullOrWhiteSpace(text);
                    // Match DetectOnce's positive-threshold guard and strict '<'.
                    // Missing scores pass; equality passes; NaN does not compare '<'.
                    bool passesScore = !(options.TextScore > 0 && mean is not null && mean.Value < options.TextScore);
                    SKPointI[] points = boxes[i].BoxPoints.ToArray();
                    map.Invoke(input, [points]);
                    lines.Add(new PrefilterLine(i, text, chars, scores, mean, boxes[i].Score,
                        points, blank, passesScore, appliedAngles[i], angles[i].Score));
                }
            }
            finally
            {
                foreach (SKBitmap? part in parts) part?.Dispose();
            }
        }
        finally
        {
            ((IDisposable)input).Dispose();
        }

        // A second, untimed public pipeline call is the oracle for the reflected
        // path. Any preprocessing, crop, orientation, ordering or filter drift
        // fails this diagnostic rather than silently producing misleading data.
        RapidOcrNet.OcrResult reference = engine.Detect(decoded, options, CancellationToken.None);
        PrefilterLine[] surviving = lines.Where(line => !line.IsBlank && line.PassesTextScore).ToArray();
        TextBlock[] expected = reference.TextBlocks ?? [];
        Assert.Equal(expected.Length, surviving.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            TextBlock block = expected[i];
            PrefilterLine line = surviving[i];
            Assert.Equal(block.Text, line.Text);
            Assert.True((block.Chars is null && line.Chars is null)
                || (block.Chars is not null && line.Chars is not null && block.Chars.SequenceEqual(line.Chars)),
                $"Diagnostic character sequence differs at surviving line {i}.");
            Assert.Equal(block.BoxPoints.Length, line.Points.Length);
            for (int point = 0; point < line.Points.Length; point++)
            {
                Assert.Equal(block.BoxPoints[point].X, line.Points[point].X);
                Assert.Equal(block.BoxPoints[point].Y, line.Points[point].Y);
            }
            Assert.Equal(BitConverter.SingleToInt32Bits(block.BoxScore), BitConverter.SingleToInt32Bits(line.BoxScore));
            Assert.Equal(block.AngleIndex, line.AngleIndex);
            Assert.Equal(BitConverter.SingleToInt32Bits(block.AngleScore), BitConverter.SingleToInt32Bits(line.AngleScore));
            Assert.True((block.CharScores is null && line.CharScores is null)
                || (block.CharScores is not null && line.CharScores is not null
                    && block.CharScores.Select(BitConverter.SingleToInt32Bits)
                        .SequenceEqual(line.CharScores.Select(BitConverter.SingleToInt32Bits))),
                $"Diagnostic float scores differ at surviving line {i}.");
        }

        return new
        {
            detectorBoxCount = lines.Count,
            detectedBoxes = lines.Select(line => new
            {
                index = line.DetectorIndex, score = line.BoxScore, geometry = DiagnosticGeometry(line.Points),
            }).ToArray(),
            recognizedLineCountBeforeTextScore = lines.Count,
            recognizedLinesBeforeTextScore = lines.Select(line => new
            {
                detectorIndex = line.DetectorIndex, line.Text, score = line.Score, line.BoxScore,
                chars = line.Chars, charScores = line.CharScores, geometry = DiagnosticGeometry(line.Points),
                isBlank = line.IsBlank, passesTextScore = line.PassesTextScore,
                survivesLibraryFilters = !line.IsBlank && line.PassesTextScore,
                angleIndex = line.AngleIndex, angleScore = line.AngleScore,
            }).ToArray(),
            nonblankRecognitionCount = lines.Count(line => !line.IsBlank),
            blankRecognitionCount = lines.Count(line => line.IsBlank),
            scoreDroppedLines = lines.Count(line => !line.IsBlank && !line.PassesTextScore),
            detectorMinusNonblankRecognitionCount = lines.Count(line => line.IsBlank),
            prefilterEquivalence = new
            {
                status = "passed", comparedSurvivingLines = surviving.Length,
                assemblyVersion = assembly.GetName().Version!.ToString(),
                moduleVersionId = assembly.ManifestModule.ModuleVersionId,
                note = "One record per detector box, including empty and whitespace-only recognition. Surviving text, characters, geometry, angle and bit-exact float scores match public Detect.",
            },
        };

        static MethodInfo RequiredMethod(Type type, string name, BindingFlags flags) =>
            type.GetMethod(name, flags) ?? throw new MissingMethodException(type.FullName, name);
        static FieldInfo RequiredField(Type type, string name, BindingFlags flags) =>
            type.GetField(name, flags) ?? throw new MissingFieldException(type.FullName, name);
    }

    private static object DiagnosticGeometry(SKPointI[] points)
    {
        Assert.Equal(4, points.Length);
        return new
        {
            points = points.Select(point => new { point.X, point.Y }).ToArray(),
            x = points.Min(point => point.X), y = points.Min(point => point.Y),
            width = points.Max(point => point.X) - points.Min(point => point.X),
            height = points.Max(point => point.Y) - points.Min(point => point.Y),
        };
    }

    private sealed record PrefilterLine(int DetectorIndex, string Text, string[]? Chars, float[]? CharScores,
        float? Score, float BoxScore, SKPointI[] Points, bool IsBlank, bool PassesTextScore,
        int AngleIndex, float AngleScore);
}
