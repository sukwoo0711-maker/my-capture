using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The blur layer's persisted shape, backward compatibility of layer files, and the pure
/// Gaussian kernel every renderer shares.
/// </summary>
public sealed class EditorBlurTests
{
    // ---- Layer JSON ------------------------------------------------------------------

    [Fact]
    public void BlurLayer_RoundTripsGeometryStrengthIdentityAndPaintOrder()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(1920, 1080);
        document.Add(new RectangleAnnotation { Rect = new RectD(10, 20, 100, 50) });
        var blur = new BlurAnnotation { Rect = new RectD(300.5, 40.25, 220, 60), Radius = 24, Opacity = 1 };
        document.Add(blur);
        document.Add(new TextAnnotation { Rect = new RectD(5, 5, 80, 20), Text = "확인" });

        string json = document.ToJson();
        Assert.Contains("\"kind\":\"blur\"", json, StringComparison.Ordinal);
        Assert.Contains("\"radius\":24", json, StringComparison.Ordinal);

        AnnotationDocument restored = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(json));
        Assert.Collection(
            restored.Items,
            item => Assert.IsType<RectangleAnnotation>(item),
            item =>
            {
                BlurAnnotation loaded = Assert.IsType<BlurAnnotation>(item);
                Assert.Equal(blur.Id, loaded.Id);
                Assert.Equal(new RectD(300.5, 40.25, 220, 60), loaded.Rect);
                Assert.Equal(24, loaded.Radius);
            },
            item => Assert.IsType<TextAnnotation>(item));
    }

    [Theory]
    [InlineData("", BlurAnnotation.DefaultRadius)]
    [InlineData(",\"radius\":999", BlurAnnotation.MaximumRadius)]
    [InlineData(",\"radius\":-5", BlurAnnotation.MinimumRadius)]
    [InlineData(",\"radius\":9", 9)]
    public void HandEditedBlurStrength_FallsBackToDefaultOrClamps(string radiusMember, int expected)
    {
        string json = "{\"schemaVersion\":1,\"canvasWidth\":200,\"canvasHeight\":100,\"items\":[" +
            "{\"kind\":\"blur\",\"rect\":{\"x\":10,\"y\":10,\"width\":40,\"height\":30}" + radiusMember + "}]}";

        AnnotationDocument document = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(json));
        BlurAnnotation blur = Assert.IsType<BlurAnnotation>(Assert.Single(document.Items));
        Assert.Equal(expected, blur.Radius);
        Assert.Equal(new RectD(10, 10, 40, 30), blur.Rect);
    }

    [Fact]
    public void LegacyLayerFileWithoutBlur_LoadsExactlyAsBefore()
    {
        // A 4.1.0 layer file: every pre-blur kind, no blur members anywhere.
        const string legacy = """
            {"schemaVersion":1,"canvasWidth":640,"canvasHeight":360,"items":[
              {"kind":"rect","id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","zIndex":0,"opacity":1,
               "rect":{"x":10,"y":10,"width":40,"height":30},"stroke":"#FFEF4444","fill":"#00000000","strokeThickness":3,"cornerRadius":2},
              {"kind":"ellipse","zIndex":1,"rect":{"x":60,"y":10,"width":30,"height":30},"stroke":"#FF3B82F6","strokeThickness":2},
              {"kind":"polyline","zIndex":2,"points":[{"x":0,"y":0},{"x":50,"y":40}],"stroke":"#FF22C55E","strokeThickness":4,"headAtEnd":true},
              {"kind":"pen","zIndex":3,"points":[{"x":1,"y":1},{"x":5,"y":8}],"strokeThickness":2,"isHighlighter":true},
              {"kind":"text","zIndex":4,"rect":{"x":100,"y":100,"width":120,"height":30},"text":"확인 필요","fontSize":22},
              {"kind":"image","zIndex":5,"assetFileName":"asset-1.png","rect":{"x":200,"y":200,"width":80,"height":45},"sourceWidth":160,"sourceHeight":90}
            ]}
            """;

        AnnotationDocument document = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(legacy));

        Assert.Equal(640, document.CanvasWidth);
        Assert.Collection(
            document.Items,
            item => Assert.Equal(new RectD(10, 10, 40, 30), Assert.IsType<RectangleAnnotation>(item).Rect),
            item => Assert.IsType<EllipseAnnotation>(item),
            item => Assert.True(Assert.IsType<PolylineAnnotation>(item).HeadAtEnd),
            item => Assert.True(Assert.IsType<PenAnnotation>(item).IsHighlighter),
            item => Assert.Equal("확인 필요", Assert.IsType<TextAnnotation>(item).Text),
            item => Assert.Equal("asset-1.png", Assert.IsType<ImageAnnotation>(item).AssetFileName));
        Assert.DoesNotContain(document.Items, item => item is BlurAnnotation);
        Assert.DoesNotContain("blur", document.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownFutureLayerKind_FollowsTheExistingWholeDocumentFallback()
    {
        // Existing policy: an unreadable layer file opens as "no annotations" (null) so the
        // original image is still shown, rather than failing the load or dropping items silently.
        const string future = """
            {"schemaVersion":1,"canvasWidth":100,"canvasHeight":80,"items":[
              {"kind":"blur","rect":{"x":1,"y":1,"width":10,"height":10},"radius":8},
              {"kind":"spotlight","rect":{"x":10,"y":10,"width":40,"height":30}}
            ]}
            """;

        Assert.Null(AnnotationDocument.TryFromJson(future));
    }

    [Fact]
    public void BlurLayer_CloneRotateAndPickingBehaveLikeAnOpaqueRegion()
    {
        var blur = new BlurAnnotation { Rect = new RectD(10, 20, 30, 40), Radius = 12, Opacity = 0.5 };

        var clone = Assert.IsType<BlurAnnotation>(blur.Clone());
        Assert.NotEqual(blur.Id, clone.Id);
        Assert.Equal(blur.Rect, clone.Rect);
        Assert.Equal(12, clone.Radius);
        Assert.Equal(0.5, clone.Opacity);

        Assert.Equal(0, blur.DistanceTo(new PointD(25, 40)));
        Assert.Equal(5, blur.DistanceTo(new PointD(45, 40)), 6);

        AnnotationDocument document = AnnotationDocument.CreateFor(200, 100);
        document.Add(blur);
        AnnotationDocumentRotator.Rotate(document, 1);
        Assert.Equal(new RectD(40, 10, 40, 30), blur.Rect);
        Assert.Equal((100, 200), (document.CanvasWidth, document.CanvasHeight));
        AnnotationDocumentRotator.Rotate(document, -1);
        Assert.Equal(new RectD(10, 20, 30, 40), blur.Rect);
    }

    [Fact]
    public void PixelRegion_RoundsOutwardAndIntersectsTheCanvas()
    {
        var blur = new BlurAnnotation { Rect = new RectD(-5.5, 10.2, 20, 5.1) };
        Assert.Equal(new RectD(0, 10, 15, 6), blur.PixelRegionWithin(100, 100));

        blur.Rect = new RectD(95.5, 98, 10, 10);
        Assert.Equal(new RectD(95, 98, 5, 2), blur.PixelRegionWithin(100, 100));

        blur.Rect = new RectD(120, 10, 10, 10);
        Assert.True(blur.PixelRegionWithin(100, 100).IsEmpty);
    }

    [Fact]
    public void AddMoveRestyleAndDelete_AreEachOneUndoStep()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(200, 100);
        var undo = new UndoStack();
        var blur = new BlurAnnotation { Rect = new RectD(10, 10, 50, 20) };

        undo.Execute(new AddAnnotationCommand(document, blur));
        undo.Execute(new TransformAnnotationCommand(blur, blur.Rect, new RectD(30, 40, 60, 25)));
        undo.Execute(new PropertyChangeCommand<BlurAnnotation, int>(blur, "strength", static (b, v) => b.Radius = v, 16, 40));
        undo.Execute(new RemoveAnnotationCommand(document, blur));
        Assert.Empty(document.Items);

        Assert.True(undo.Undo());
        Assert.Same(blur, Assert.Single(document.Items));
        Assert.True(undo.Undo());
        Assert.Equal(BlurAnnotation.DefaultRadius, blur.Radius);
        Assert.True(undo.Undo());
        Assert.Equal(new RectD(10, 10, 50, 20), blur.Rect);
        Assert.True(undo.Undo());
        Assert.Empty(document.Items);

        Assert.True(undo.Redo());
        Assert.True(undo.Redo());
        Assert.True(undo.Redo());
        Assert.Equal(40, blur.Radius);
        Assert.Equal(new RectD(30, 40, 60, 25), blur.Rect);
    }

    // ---- Settings --------------------------------------------------------------------

    [Fact]
    public void BlurStrengthSetting_DefaultsClampsClonesAndRoundTripsThroughTheDraft()
    {
        Assert.Equal(BlurAnnotation.DefaultRadius, new AppSettings().Annotation.BlurRadius);

        var settings = new AppSettings();
        settings.Annotation.BlurRadius = 30;
        Assert.Equal(30, settings.DeepClone().Annotation.BlurRadius);

        var draft = new SettingsDraft(settings);
        Assert.Equal("30", draft.BlurRadius);
        draft.BlurRadius = "65";
        Assert.True(draft.HasErrors);
        draft.BlurRadius = "8";
        Assert.False(draft.HasErrors);
        Assert.Equal(8, draft.ToAppSettings().Annotation.BlurRadius);
    }

    // ---- Kernel ----------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(64)]
    public void UniformImage_IsUnchangedIncludingEdgesAndCorners(int radius)
    {
        const int width = 37, height = 23;
        byte[] pixels = Fill(width, height, (_, _) => (0x30, 0x80, 0xC0, 0xFF));
        byte[] expected = (byte[])pixels.Clone();

        GaussianBlurKernel.BlurInPlace(pixels, width, height, radius);

        // Clamping repeats the edge instead of padding with zeros, so nothing darkens.
        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void EdgeClamp_ReplicatesBorderPixelsInsteadOfFadingToBlack()
    {
        // A white image with one black column on the far left: with clamping the left edge
        // stays the darkest and the right edge stays pure white.
        const int width = 64, height = 8;
        byte[] pixels = Fill(width, height, (x, _) => x == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));

        GaussianBlurKernel.BlurInPlace(pixels, width, height, 4);

        for (int y = 0; y < height; y++)
        {
            byte left = pixels[(y * width * 4) + 0];
            byte nextToLeft = pixels[(y * width * 4) + 4];
            byte right = pixels[(y * width * 4) + ((width - 1) * 4)];
            Assert.True(left < nextToLeft, "The repeated black edge must stay darkest.");
            Assert.True(left < 255 && left > 0);
            Assert.Equal(255, right);
            Assert.Equal(255, pixels[(y * width * 4) + 3]);
        }

        // Top and bottom rows match the middle row: clamping vertically adds no gradient.
        Assert.Equal(pixels.AsSpan(0, width * 4).ToArray(), pixels.AsSpan((height - 1) * width * 4, width * 4).ToArray());
    }

    [Theory]
    [InlineData(5, 7, 20, 11, 3)]
    [InlineData(0, 0, 13, 9, 6)]
    [InlineData(60, 30, 20, 10, 6)]
    [InlineData(0, 0, 80, 40, 16)]
    [InlineData(31, 17, 1, 1, 9)]
    public void RegionBlur_MatchesTheSameRegionOfAWholeImageBlur(int x, int y, int width, int height, int radius)
    {
        const int imageWidth = 80, imageHeight = 40;
        var random = new Random(4242);
        byte[] image = Fill(imageWidth, imageHeight, (_, _) =>
            ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)255));

        byte[] region = GaussianBlurKernel.BlurRegion(image, imageWidth, imageHeight, imageWidth * 4, x, y, width, height, radius);

        byte[] whole = (byte[])image.Clone();
        GaussianBlurKernel.BlurInPlace(whole, imageWidth, imageHeight, radius);
        for (int row = 0; row < height; row++)
        {
            Assert.Equal(
                whole.AsSpan((((y + row) * imageWidth) + x) * 4, width * 4).ToArray(),
                region.AsSpan(row * width * 4, width * 4).ToArray());
        }
    }

    [Fact]
    public void LargeImage_ParallelPathMatchesTheRegionPathByteForByte()
    {
        // Above the parallel threshold the whole-image blur splits rows and column bands
        // across threads; the result must not depend on that split.
        const int width = 700, height = 420;
        var random = new Random(7);
        byte[] image = Fill(width, height, (_, _) =>
            ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)255));
        byte[] whole = (byte[])image.Clone();

        GaussianBlurKernel.BlurInPlace(whole, width, height, BlurAnnotation.DefaultRadius);
        byte[] region = GaussianBlurKernel.BlurRegion(image, width, height, width * 4, 300, 200, 64, 32, BlurAnnotation.DefaultRadius);

        for (int row = 0; row < 32; row++)
        {
            Assert.Equal(
                whole.AsSpan((((200 + row) * width) + 300) * 4, 64 * 4).ToArray(),
                region.AsSpan(row * 64 * 4, 64 * 4).ToArray());
        }
    }

    [Fact]
    public void DefaultStrength_FlattensTextSizedDetailBeyondRecognition()
    {
        // One-pixel strokes every three pixels stand in for 12-16px body text. The image is
        // large enough that the measured centre is beyond the kernel's reach from any edge.
        const int width = 240, height = 200;
        byte[] pixels = Fill(width, height, (x, y) =>
            x % 3 == 0 || y % 5 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));

        GaussianBlurKernel.BlurInPlace(pixels, width, height, BlurAnnotation.DefaultRadius);

        int min = 255, max = 0;
        for (int y = 60; y < 140; y++)
        {
            for (int x = 60; x < 180; x++)
            {
                byte value = pixels[((y * width) + x) * 4];
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        // Full black-on-white contrast (255) collapses to a near-flat grey.
        Assert.True(max - min <= 4, $"Residual contrast {max - min} is still legible.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    public void BoxRadii_ApproximateTheRequestedGaussianVariance(int sigma)
    {
        int[] radii = GaussianBlurKernel.BoxRadii(sigma);

        Assert.Equal(3, radii.Length);
        // A centred box of width w has variance (w^2 - 1) / 12; three passes add up.
        double variance = radii.Sum(r => (((2.0 * r) + 1) * ((2.0 * r) + 1) - 1) / 12);
        Assert.InRange(variance, sigma * sigma * 0.8, sigma * sigma * 1.05);
        Assert.Equal(radii.Sum(), GaussianBlurKernel.Reach(sigma));
    }

    [Fact]
    public void Kernel_RejectsRegionsOutsideTheImageAndHonoursCancellation()
    {
        byte[] image = new byte[10 * 10 * 4];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GaussianBlurKernel.BlurRegion(image, 10, 10, 40, 5, 5, 6, 2, 4));
        Assert.Throws<ArgumentException>(() =>
            GaussianBlurKernel.BlurRegion(image, 10, 11, 40, 0, 0, 2, 2, 4));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            GaussianBlurKernel.BlurInPlace(image, 10, 10, 4, cancelled.Token));
    }

    private static byte[] Fill(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte b, byte g, byte r, byte a) = color(x, y);
                int i = ((y * width) + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        return pixels;
    }
}
