using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Real H.264 regressions. The oracle below reads NV12 directly and implements the documented
/// Y'CbCr equations in double precision, independently of both product colour converters.
/// Patch centres avoid 4:2:0 boundaries. One encode allows 3 grey / 5 colour codes of error;
/// two encodes allow 4 grey / 6 colour codes, including quantisation and RGB/YUV rounding.
/// The product's fixed-point conversion must be within ONE code of the independent oracle.
/// All grey limits reject the observed 16->0, 64->56 and 192->204 full-range expansion.
/// </summary>
public sealed class VideoFrameRangeRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    // Use frame 15's canonical export cadence (500 ms); 15 * (1000/30) has a tiny
    // double remainder, and the existing layer intervals have strict half-open boundaries.
    private static readonly double Time = 15 * (1000.0 / 30);

    [Theory]
    [InlineData(false, 320, 180)]
    [InlineData(true, 320, 180)]
    [InlineData(false, 1280, 720)]
    [InlineData(true, 1280, 720)]
    public void EditInput_RotatedPng_JsonReopen_AndMp4MatchIndependentNv12(
        bool fullRange, int width, int height) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-range-roundtrip-");
        VideoEditorWindow? editor = null;
        VideoEditorWindow? reopenedEditor = null;
        try
        {
            string source = RangePatternClip.Encode(Path.Combine(root, "source.mp4"), width, height, fullRange);
            var recording = new RecordingResult(source, 1000, 30, 30, width, height);
            BitmapSource reference = DecodeReference(source, Time, fullRange, height >= 720);
            AssertPatches(reference, width, height, null, rotated: false, greyTolerance: 3, colourTolerance: 5);
            editor = NewEditor(recording, root);
            BitmapSource input = EditInput(editor);
            Assert.True(input.IsFrozen);
            AssertPatches(input, width, height, null, false, 3, 5);
            AssertPatches(input, width, height, reference, false, 1, 1);

            CommitRotation(editor, input, turns: 2, annotated: false);
            VideoEditDocument saved = SaveReload(root, Document(editor), width, height);
            FrameEditLayer layer = Assert.Single(saved.FrameEditLayers);
            Assert.InRange(layer.StartMs, 499.99, 500.01);
            Assert.InRange(layer.EndMs, 533.32, 533.35);
            BitmapSource png = SaveReadPng(root, layer);
            AssertOpaque(png);
            AssertPatches(png, width, height, null, true, 3, 5);
            AssertPatches(png, width, height, reference, true, 1, 1);

            reopenedEditor = NewEditor(recording, root, saved);
            Assert.Equal(Pixels(png), Pixels(EditInput(reopenedEditor)));
            string output = Path.Combine(root, "export.mp4");
            Mp4RenderStatistics stats = Export(recording, output, saved);
            Assert.Equal(30, stats.Frames);
            if (fullRange) Assert.Equal("rgb-software", stats.FrameSource);
            BitmapSource exported = DecodeReference(output, Time, fullRange: false, bt709: height >= 720);
            AssertPatches(exported, width, height, null, true, 4, 6);
            AssertPatches(exported, width, height, reference, true, 4, 6);

            // The same export also contains unedited frames, so the raw-frame callback cannot
            // silently retain the old full-range RGB32 error.
            BitmapSource plain = DecodeReference(output, 200, fullRange: false, bt709: height >= 720);
            AssertPatches(plain, width, height, null, false, 4, 6);
            AssertPatches(plain, width, height, reference, false, 4, 6);
        }
        finally
        {
            reopenedEditor?.Close();
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(320, 180)]
    [InlineData(1280, 720)]
    public void StudioRange_RemainsBitIdenticalToLegacyRgb32(int width, int height) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-range-studio-");
        try
        {
            string source = RangePatternClip.Encode(Path.Combine(root, "source.mp4"), width, height, fullRange: false);
            using var legacy = new MediaFoundationVideoFrameReader(source);
            using var current = RangeAwareVideoFrameDecoder.Open(source);
            foreach (double time in new[] { 0.0, Time, 900.0 })
            {
                DecodedVideoFrame oldFrame = legacy.ReadFrameAt(time);
                DecodedVideoFrame newFrame = RangeAwareVideoFrameDecoder.ReadBgr32(current, time);
                Assert.Equal(DecodedVideoPixelFormat.Nv12, current.OutputFormat);
                Assert.Equal(new VideoYuvEncoding(height >= 720, false), current.YuvEncoding);
                Assert.Equal(oldFrame.TimestampMs, newFrame.TimestampMs);
                Assert.Equal(oldFrame.SampleDurationMs, newFrame.SampleDurationMs);
                Assert.Equal(Pixels(ToBitmap(oldFrame)), Pixels(ToBitmap(newFrame)));
                Assert.Equal(Pixels(ToBitmap(oldFrame)),
                    Pixels(VideoFrameRenderPipeline.RenderSingleFrame(source, time, width, height)));
            }

            // Preserve WPF composition too, including live text and transparent frame layers.
            var overlays = new[] { new TimedTextOverlay { Text = "F L 7", StartMs = 0, EndMs = 1000 } };
            var layers = new[] { ColourLayer() };
            BitmapSource expected = VideoFrameRenderPipeline.RenderFrame(
                ToBitmap(legacy.ReadFrameAt(Time)), width, height, width, height, overlays, layers,
                FrameEditLayerRenderer.Decode(layers), Time);
            Assert.Equal(Pixels(expected),
                Pixels(VideoFrameRenderPipeline.RenderSingleFrame(source, Time, width, height, overlays, layers)));

            // Compare actual encoded output pixels as well: the legacy RGB32 reader's frames,
            // converted by the export's own studio NV12 converter, feed the product encoder; the
            // new non-passthrough export must yield the very same NV12 samples, with zero
            // tolerance. (The export no longer uses the OS RGB-to-Y'CbCr converter, whose
            // rounding differs between machines.)
            string legacyPath = Path.Combine(root, "legacy.mp4");
            using (var encoder = new MediaFoundationVideoEncoder(
                new VideoEncoderOptions(legacyPath, width, height, 30,
                    VideoEncoderOptions.DeriveBitrate(width, height, 30)) { InputFormat = VideoEncoderInputFormat.Nv12 },
                NullLogger.Instance))
            {
                byte[] nv12 = new byte[BgraToNv12Converter.RequiredLength(width, height)];
                for (int i = 0; i < 30; i++)
                {
                    double time = i * (1000.0 / 30);
                    DecodedVideoFrame frame = legacy.ReadFrameAt(time);
                    BgraToNv12Converter.Convert(frame.Pixels, frame.Stride, width, height,
                        VideoFrameRenderPipeline.DefaultYuvEncoding(height), nv12);
                    encoder.WriteFrame(new EncoderFrame(nv12, width, height, width, time) { Format = VideoEncoderInputFormat.Nv12 });
                }
                encoder.Complete();
            }
            string currentPath = Path.Combine(root, "current.mp4");
            var recording = new RecordingResult(source, 1000, 30, 30, width, height);
            Assert.Equal(30, Export(recording, currentPath, VideoEditDocument.CreateFor(width, height, 1000),
                new Mp4RenderOptions { PassThroughNv12 = false }).Frames);
            using var legacyOutput = RangeAwareVideoFrameDecoder.Open(legacyPath);
            using var currentOutput = RangeAwareVideoFrameDecoder.Open(currentPath);
            Assert.Equal(DecodedVideoPixelFormat.Nv12, legacyOutput.OutputFormat);
            Assert.Equal(DecodedVideoPixelFormat.Nv12, currentOutput.OutputFormat);
            for (int i = 0; i < 30; i++)
                Assert.Equal(legacyOutput.ReadFrameAt(i * (1000.0 / 30)).Pixels,
                    currentOutput.ReadFrameAt(i * (1000.0 / 30)).Pixels);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainTrim_Gif_Poster_AndFilmstripKeepRange(bool fullRange) => StaTestHost.Run(() => RunPlainTrim(fullRange));

    // Hosted CI runners have no GPU. Emulate them: software H.264 encoder and decoder only.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainTrim_Gif_Poster_AndFilmstripKeepRange_SoftwareMedia(bool fullRange) => StaTestHost.Run(() =>
    {
        MediaFoundationVideoFrameReader.ForceSoftwareForTest = true;
        MediaFoundationVideoEncoder.ForceSoftwareForTest = true;
        try { RunPlainTrim(fullRange); }
        finally
        {
            MediaFoundationVideoFrameReader.ForceSoftwareForTest = false;
            MediaFoundationVideoEncoder.ForceSoftwareForTest = false;
        }
    });

    private static void RunPlainTrim(bool fullRange)
    {
        string root = OwnedTestDirectory.Create("mc-range-consumers-");
        try
        {
            const int width = 1280, height = 720;
            string source = RangePatternClip.Encode(Path.Combine(root, "source.mp4"), width, height, fullRange);
            var recording = new RecordingResult(source, 1000, 30, 30, width, height);
            VideoEditDocument document = VideoEditDocument.CreateFor(width, height, 1000);
            BitmapSource reference = DecodeReference(source, Time, fullRange, bt709: true);

            // Force the non-passthrough branch as well as the production MP4 branch above.
            string trimmed = Path.Combine(root, "trim.mp4");
            Mp4RenderStatistics stats = Export(recording, trimmed, document,
                new Mp4RenderOptions { PassThroughNv12 = false });
            Assert.Equal("rgb-software", stats.FrameSource);
            BitmapSource trim = DecodeReference(trimmed, Time, fullRange: false, bt709: true);
            AssertPatches(trim, width, height, null, false, 4, 6);
            AssertPatches(trim, width, height, reference, false, 4, 6);

            BitmapSource poster = VideoFrameRenderPipeline.RenderSingleFrame(source, Time, 320, 180);
            AssertPatchMeans(poster, reference, 1);
            AssertPatches(poster, width, height, null, false, 3, 5);

            string gifPath = Path.Combine(root, "export.gif");
            Assert.Equal(10, AnimatedGifExporter.Export(recording, document, gifPath));
            using (var stream = File.OpenRead(gifPath))
            {
                var gif = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // WIC palettes/dithering differ by OS, even for these flat source colours.
                BitmapSource gifFrame = gif.Frames[0];
                AssertGifPatches(gifFrame, width, height, reference, 3, 3);
                AssertGifPatches(gifFrame, width, height, null, 4, 6);
            }

            FilmstripLayout layout = FilmstripLayout.Create(1000, width, height, 800, 90, 30);
            using TimelineFilmstrip strip = TimelineFilmstrip.Start(
                source, layout, new DpiScale(1, 1), Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            Assert.True(strip.CompletionForTest.Wait(TimeSpan.FromSeconds(30)), "Filmstrip did not finish.");
            Assert.Equal(layout.TileCount, strip.PublishedCount);
            for (int i = 0; i < strip.PublishedCount; i++)
            {
                BitmapSource tile = Assert.IsAssignableFrom<BitmapSource>(strip.Tile(i));
                // Each tile is a different frame. The software encoder quantises frames differently,
                // so compare with the reference decoded at the tile's own sample time.
                AssertPatchMeans(tile, DecodeReference(source, layout.SampleTimeMs(i), fullRange, bt709: true), 1);
                AssertPatches(tile, width, height, null, false, 3, 5);
            }
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void FlattenedRotation_KeepsOneUprightSubtitle_AndOccludesPriorLayers(
        int turns, bool annotated) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-rotation-subtitle-");
        VideoEditorWindow? editor = null;
        VideoEditorWindow? reopenedEditor = null;
        try
        {
            const int width = 640, height = 360;
            string source = EncodeDarkPattern(Path.Combine(root, "source.mp4"), width, height);
            var recording = new RecordingResult(source, 1000, 30, 30, width, height);
            VideoEditDocument initial = VideoEditDocument.CreateFor(width, height, 1000);
            initial.TextOverlays.Add(new TimedTextOverlay
            {
                Text = "F L 7", StartMs = 400, EndMs = 600,
                Bounds = new VideoLayerBounds(0.3, 0.03, 0.4, 0.18),
            });
            initial.FrameEditLayers.Add(ColourLayer());
            editor = NewEditor(recording, root, initial);
            BitmapSource input = EditInput(editor);
            BitmapSource withoutText = VideoFrameRenderPipeline.RenderSingleFrame(
                source, Time, width, height, overlays: null, frameEditLayers: initial.FrameEditLayers);
            Assert.Equal(Pixels(withoutText), Pixels(input));
            Assert.Equal(0, BrightMask(input).Count(value => value));

            CommitRotation(editor, input, turns, annotated);
            VideoEditDocument saved = SaveReload(root, Document(editor), width, height);
            Assert.Single(saved.TextOverlays);
            Assert.Equal(2, saved.FrameEditLayers.Count); // Existing layer survives at other times.
            BitmapSource png = SaveReadPng(root, saved.FrameEditLayers[^1]);
            AssertOpaque(png); // In particular, every 90-degree letterbox pixel is opaque.
            Assert.Equal(0, BrightMask(png).Count(value => value));

            // Check independent locations in the asymmetric source, transformed clockwise
            // and letterboxed. Three points avoid annotations; the fourth tracks the prior layer.
            foreach ((int x, int y) in new[] { (180, 240), (480, 240), (480, 100), (8, 120) })
            {
                (int rx, int ry) = RotatedPoint(x, y, width, height, turns);
                AssertPixelNear(png, rx, ry, RangePatternClip.Sample(input, x, y), 2, "rotated content");
            }
            if (turns == 1)
            {
                // Original cyan layer occupies x=0..96; it must not show through the left bar.
                AssertPixelNear(png, 32, 120, (0, 0, 0), 0, "opaque letterbox");
                Assert.True(RangePatternClip.Sample(input, 32, 120).G > 100);
            }

            reopenedEditor = NewEditor(recording, root, saved);
            Assert.Equal(Pixels(png), Pixels(EditInput(reopenedEditor)));
            BitmapSource expected = VideoFrameRenderPipeline.RenderFrame(
                png, width, height, width, height, saved.TextOverlays, [],
                new Dictionary<Guid, BitmapSource>(), Time);
            BitmapSource reopened = VideoFrameRenderPipeline.RenderSingleFrame(
                source, Time, width, height, Document(reopenedEditor).TextOverlays,
                Document(reopenedEditor).FrameEditLayers);
            Assert.Equal(Pixels(expected), Pixels(reopened));
            AssertSubtitleMask(expected, reopened);
            // Render the reopened editor's actual overlay view as well as the export compositor.
            var preview = (TimedTextPreviewView)typeof(VideoEditorWindow)
                .GetField("_overlayPreview", Private)!.GetValue(reopenedEditor)!;
            preview.SetSourceTime(Time);
            preview.Measure(new Size(width, height));
            preview.Arrange(new Rect(0, 0, width, height));
            var previewBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            previewBitmap.Render(preview);
            Assert.Equal(Pixels(expected), Pixels(previewBitmap));
            AssertSubtitleMask(expected, previewBitmap);

            string output = Path.Combine(root, "export.mp4");
            Assert.Equal(30, Export(recording, output, saved).Frames);
            BitmapSource decoded = DecodeReference(output, Time, fullRange: false, bt709: false);
            AssertSubtitleMask(expected, decoded);
            if (annotated)
            {
                (int ax, int ay) = RotatedPoint(260, 170, width, height, turns);
                AssertPixelNear(png, ax, ay, (160, 40, 40), 0, "rotated annotation");
                AssertPixelNear(decoded, ax, ay, (160, 40, 40), 6, "exported annotation");
            }
            foreach ((int x, int y) in new[] { (180, 240), (480, 240), (480, 100), (8, 120) })
            {
                (int rx, int ry) = RotatedPoint(x, y, width, height, turns);
                AssertPixelNear(decoded, rx, ry, RangePatternClip.Sample(input, x, y), 6, "exported rotated content");
            }
            if (turns == 1) AssertPixelNear(decoded, 32, 120, (0, 0, 0), 4, "exported letterbox");

            // Half-open edited interval: the earlier layer remains before/after this frame.
            BitmapSource outside = DecodeReference(output, 700, fullRange: false, bt709: false);
            AssertPixelNear(outside, 32, 120, (32, 160, 160), 6, "prior layer outside flattened interval");
            Assert.Equal(0, BrightMask(outside).Count(value => value));
        }
        finally
        {
            reopenedEditor?.Close();
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConverterMatchesIndependentEquationsForRangeAndMatrix(bool fullRange, bool bt709)
    {
        var encoding = new VideoYuvEncoding(bt709, fullRange);
        foreach (byte y in new byte[] { 0, 16, 64, 128, 192, 235, 255 })
        foreach ((byte u, byte v) in new (byte, byte)[] { (128, 128), (80, 160), (160, 80), (32, 224), (224, 32) })
        {
            byte[] actual = Nv12FrameConverter.ToBgra([y, y, y, y, u, v], 2, 2, encoding, 2, 2);
            var expected = ReferencePixel(y, u, v, encoding);
            Assert.InRange(Math.Abs(actual[2] - expected.R), 0, 1);
            Assert.InRange(Math.Abs(actual[1] - expected.G), 0, 1);
            Assert.InRange(Math.Abs(actual[0] - expected.B), 0, 1);
            Assert.Equal(255, actual[3]);
        }
    }

    private static BitmapSource DecodeReference(string path, double time, bool fullRange, bool bt709)
    {
        using var reader = new MediaFoundationVideoFrameReader(path,
            new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 });
        DecodedVideoFrame frame = reader.ReadFrameAt(time);
        Assert.Equal(DecodedVideoPixelFormat.Nv12, frame.Format);
        Assert.Equal(new VideoYuvEncoding(bt709, fullRange), reader.YuvEncoding);
        int width = frame.Width, height = frame.Height;
        Assert.Equal(width, frame.Stride);
        int chromaStride = ((width + 1) / 2) * 2;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int uv = width * height + y / 2 * chromaStride + x / 2 * 2;
            var colour = ReferencePixel(frame.Pixels[y * width + x], frame.Pixels[uv],
                frame.Pixels[uv + 1], reader.YuvEncoding);
            int offset = (y * width + x) * 4;
            pixels[offset] = colour.B;
            pixels[offset + 1] = colour.G;
            pixels[offset + 2] = colour.R;
            pixels[offset + 3] = 255;
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static (byte R, byte G, byte B) ReferencePixel(byte y, byte u, byte v, VideoYuvEncoding encoding)
    {
        // ITU-R BT.709: Kr=.2126, Kb=.0722 (BT.601: .299, .114).
        // Y'=(Y-16)*255/219 and C=(C-128)*255/224 for studio;
        // full uses Y'=Y and C=C-128. R'=Y'+2(1-Kr)Cr, B'=Y'+2(1-Kb)Cb,
        // G'=(Y'-Kr*R'-Kb*B')/(1-Kr-Kb). Clamp only after the matrix.
        double kr = encoding.Bt709 ? 0.2126 : 0.299;
        double kb = encoding.Bt709 ? 0.0722 : 0.114;
        double luma = encoding.FullRange ? y : (y - 16) * 255.0 / 219.0;
        double cb = (u - 128) * (encoding.FullRange ? 1 : 255.0 / 224.0);
        double cr = (v - 128) * (encoding.FullRange ? 1 : 255.0 / 224.0);
        double r = luma + 2 * (1 - kr) * cr;
        double b = luma + 2 * (1 - kb) * cb;
        double g = (luma - kr * r - kb * b) / (1 - kr - kb);
        static byte Round(double value) => (byte)Math.Clamp((int)Math.Floor(value + 0.5), 0, 255);
        return (Round(r), Round(g), Round(b));
    }

    [Theory]
    [InlineData(16, 0)]
    [InlineData(64, 56)]
    [InlineData(192, 204)]
    [InlineData(235, 254)]
    public void GifPatchMean_RejectsRangeExpansion(int source, int expanded) => StaTestHost.Run(() =>
    {
        BitmapSource gif = SyntheticGreyGif((byte)expanded, dither: false);
        // Even the looser source-colour allowance (4 codes) must reject each expansion.
        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertGifPatchMean(
            gif, 16, 16, ((byte)source, (byte)source, (byte)source), 4, "range counterexample"));
    });

    [Fact]
    public void GifPatchMean_AcceptsCorrectDitherDespiteBlackPixels() => StaTestHost.Run(() =>
    {
        BitmapSource gif = SyntheticGreyGif(16, dither: true);
        Assert.Equal((byte)0, RangePatternClip.Sample(gif, 16, 18).R);
        Assert.Equal((byte)17, RangePatternClip.Sample(gif, 16, 17).R);
        // One black pixel per 17, the remainder grey 17: the 17x17 mean is exactly 16.
        AssertGifPatchMean(gif, 16, 16, (16, 16, 16), 0, "correct dither");
    });

    private static BitmapSource SyntheticGreyGif(byte grey, bool dither)
    {
        byte[] values = dither ? [0, 17] : [0, grey];
        var palette = new BitmapPalette(values.Select(value => Color.FromRgb(value, value, value)).ToArray());
        byte[] indices = new byte[32 * 32];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
            indices[y * 32 + x] = (byte)(dither && (x + y) % 17 == 0 ? 0 : 1);
        BitmapSource indexed = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Indexed8, palette, indices, 32);
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(indexed));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static void AssertGifPatches(BitmapSource actual, int sourceWidth, int sourceHeight,
        BitmapSource? reference, int greyTolerance, int colourTolerance)
    {
        for (int i = 0; i < RangePatternClip.Patches.Length; i++)
        {
            (int sourceX, int sourceY) = RangePatternClip.Centre(i, sourceWidth, sourceHeight);
            (int x, int y) = RangePatternClip.Centre(i, actual.PixelWidth, actual.PixelHeight);
            var patch = RangePatternClip.Patches[i];
            var expected = reference is null ? (patch.R, patch.G, patch.B) : RangePatternClip.Sample(reference, sourceX, sourceY);
            AssertGifPatchMean(actual, x, y, expected, i < 7 ? greyTolerance : colourTolerance, patch.Name);
        }
    }

    private static void AssertGifPatchMean(BitmapSource actual, int x, int y,
        (byte R, byte G, byte B) expected, int tolerance, string label)
    {
        // Palette quantisation redistributes error spatially: Win10 can use 0 beside 17
        // for source grey 16. A 17x17 flat interior averages complete dither patterns, away
        // from patch/chroma boundaries. Retain the old 3-code decoder / 4-code source grey
        // budgets on each channel's MEAN, both below the smallest range error (64->56 = 8).
        // Non-GIF paths continue using their original per-pixel assertions above/below.
        long r = 0, g = 0, b = 0;
        const int radius = 8, count = (2 * radius + 1) * (2 * radius + 1);
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            var pixel = RangePatternClip.Sample(actual, x + dx, y + dy);
            r += pixel.R; g += pixel.G; b += pixel.B;
        }
        double meanR = (double)r / count, meanG = (double)g / count, meanB = (double)b / count;
        Assert.True(Math.Abs(meanR - expected.R) <= tolerance
            && Math.Abs(meanG - expected.G) <= tolerance && Math.Abs(meanB - expected.B) <= tolerance,
            $"{label} GIF patch mean at ({x},{y}): actual=({meanR:F3},{meanG:F3},{meanB:F3}), expected={expected}, tolerance={tolerance}");
    }

    /// <summary>
    /// A downscaled poster or filmstrip tile averages many source pixels. Compare the mean of each
    /// patch's 5x5 interior with the reference's mean over the same source area, so per-pixel codec
    /// noise (larger from the software H.264 encoder on GPU-less hosts) is not mistaken for a range
    /// difference. Range errors move whole flat patches (16→0, 64→56, black→16) or tint greys and
    /// still exceed the tolerance; see <see cref="PatchMeans_RejectRangeErrorsAndGreyTint"/>.
    /// </summary>
    private static void AssertPatchMeans(BitmapSource actual, BitmapSource reference, double tolerance)
    {
        (string label, double deviation) = MaxPatchMeanDeviation(actual, reference);
        Assert.True(deviation <= tolerance, $"{label}: patch mean differs by {deviation:F2} (tolerance {tolerance})");
    }

    internal static (string Label, double Deviation) MaxPatchMeanDeviation(BitmapSource actual, BitmapSource reference)
    {
        byte[] a = Pixels(actual), r = Pixels(reference);
        int aw = actual.PixelWidth, ah = actual.PixelHeight, rw = reference.PixelWidth, rh = reference.PixelHeight;
        double sx = (double)rw / aw, sy = (double)rh / ah;
        (string Label, double Deviation) worst = ("none", 0);
        for (int i = 0; i < RangePatternClip.Patches.Length; i++)
        {
            (int x, int y) = RangePatternClip.Centre(i, aw, ah);
            double[] am = Mean(a, aw, ah, x - 2, y - 2, x + 3, y + 3);
            double[] rm = Mean(r, rw, rh, (int)Math.Floor((x - 2) * sx), (int)Math.Floor((y - 2) * sy),
                (int)Math.Ceiling((x + 3) * sx), (int)Math.Ceiling((y + 3) * sy));
            for (int c = 0; c < 3; c++)
            {
                double deviation = Math.Abs(am[c] - rm[c]);
                if (deviation > worst.Deviation)
                    worst = ($"{RangePatternClip.Patches[i].Name} at ({x},{y}) channel {"BGR"[c]}: actual mean {am[c]:F2}, reference mean {rm[c]:F2}", deviation);
            }
        }
        return worst;
    }

    private static double[] Mean(byte[] bgra, int width, int height, int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, width - 1); top = Math.Clamp(top, 0, height - 1);
        right = Math.Clamp(right, left + 1, width); bottom = Math.Clamp(bottom, top + 1, height);
        double[] sum = new double[3];
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        for (int c = 0; c < 3; c++) sum[c] += bgra[(y * width + x) * 4 + c];
        int count = (right - left) * (bottom - top);
        return [sum[0] / count, sum[1] / count, sum[2] / count];
    }

    [Fact]
    public void PatchMeans_RejectRangeErrorsAndGreyTint() => StaTestHost.Run(() =>
    {
        BitmapSource reference = PatternBitmap(1280, 720, (r, g, b, _) => (r, g, b));
        Assert.True(MaxPatchMeanDeviation(PatternBitmap(160, 90, (r, g, b, _) => (r, g, b)), reference).Deviation <= 1);
        static byte Expand(byte v) => (byte)Math.Clamp(Math.Round((v - 16) * 255.0 / 219), 0, 255);
        static byte Retain(byte v) => (byte)Math.Round(16 + v * 219.0 / 255);
        var errors = new (string Name, Func<byte, byte, byte, int, (byte, byte, byte)> Map, double Minimum)[]
        {
            ("studio expanded as full (16→0, 64→56)", (r, g, b, _) => (Expand(r), Expand(g), Expand(b)), 8),
            ("full shown as studio (black→16)", (r, g, b, _) => (Retain(r), Retain(g), Retain(b)), 8),
            ("grey tint R/B +5", (r, g, b, i) => i < 7 ? ((byte)Math.Min(255, r + 5), g, (byte)Math.Min(255, b + 5)) : (r, g, b), 5),
        };
        foreach (var error in errors)
        {
            (string label, double deviation) = MaxPatchMeanDeviation(PatternBitmap(160, 90, error.Map), reference);
            Assert.True(deviation >= error.Minimum, $"{error.Name}: {label} deviates only {deviation:F2}");
        }
    });

    private static BitmapSource PatternBitmap(int width, int height, Func<byte, byte, byte, int, (byte R, byte G, byte B)> map)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * 2 / height * 8) + (x * 8 / width);
            var patch = RangePatternClip.Patches[index];
            (byte r, byte g, byte b) = map(patch.R, patch.G, patch.B, index);
            int o = (y * width + x) * 4;
            pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; pixels[o + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void AssertPatches(BitmapSource actual, int sourceWidth, int sourceHeight,
        BitmapSource? reference, bool rotated, int greyTolerance, int colourTolerance)
    {
        for (int i = 0; i < RangePatternClip.Patches.Length; i++)
        {
            (int sourceX, int sourceY) = RangePatternClip.Centre(i, sourceWidth, sourceHeight);
            (int x, int y) = RangePatternClip.Centre(i, actual.PixelWidth, actual.PixelHeight);
            if (rotated) { x = actual.PixelWidth - 1 - x; y = actual.PixelHeight - 1 - y; }
            var patch = RangePatternClip.Patches[i];
            var expected = reference is null ? (patch.R, patch.G, patch.B) : RangePatternClip.Sample(reference, sourceX, sourceY);
            // Inspect a 5x5 interior, not a single favourable pixel or an edge crossing chroma blocks.
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                AssertPixelNear(actual, x + dx, y + dy, expected,
                    i < 7 ? greyTolerance : colourTolerance, patch.Name);
        }
    }

    private static void AssertPixelNear(BitmapSource bitmap, int x, int y,
        (byte R, byte G, byte B) expected, int tolerance, string label)
    {
        var actual = RangePatternClip.Sample(bitmap, x, y);
        Assert.True(Math.Abs(actual.R - expected.R) <= tolerance
            && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance,
            $"{label} at ({x},{y}): actual={actual}, expected={expected}, tolerance={tolerance}");
    }

    private static void AssertOpaque(BitmapSource bitmap)
    {
        byte[] pixels = Pixels(bitmap);
        for (int i = 3; i < pixels.Length; i += 4) Assert.Equal(255, pixels[i]);
    }

    private static bool[] BrightMask(BitmapSource bitmap)
    {
        byte[] pixels = Pixels(bitmap);
        bool[] mask = new bool[bitmap.PixelWidth * bitmap.PixelHeight];
        for (int i = 0; i < mask.Length; i++)
            mask[i] = pixels[i * 4] > 200 && pixels[i * 4 + 1] > 200 && pixels[i * 4 + 2] > 200;
        return mask;
    }

    private static void AssertSubtitleMask(BitmapSource expected, BitmapSource actual)
    {
        bool[] template = BrightMask(expected), observed = BrightMask(actual);
        int width = actual.PixelWidth, height = actual.PixelHeight;
        Assert.Equal(template.Length, observed.Length);
        int expectedCount = template.Count(value => value);
        Assert.True(expectedCount > 80, "The upright subtitle template must contain visible glyphs.");
        int matched = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!observed[y * width + x]) continue;
            bool near = false;
            // H.264 can move a white glyph edge by 1-2 pixels. Every observed white pixel
            // must still belong to the single upright template, including outside its bounds.
            for (int dy = -2; dy <= 2 && !near; dy++)
            for (int dx = -2; dx <= 2 && !near; dx++)
                if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                    near = template[(y + dy) * width + x + dx];
            Assert.True(near, $"Unexpected subtitle pixel at ({x},{y}); possible rotated duplicate.");
            matched++;
        }
        Assert.InRange(matched, (int)(expectedCount * 0.85), (int)(expectedCount * 1.15));
        // Also require the glyph interior at its upright positions; a sparse or rotated glyph
        // within the same bounding box cannot pass merely by having a similar pixel count.
        int retained = template.Where((value, index) => value && observed[index]).Count();
        Assert.True(retained >= expectedCount * 0.85, $"Upright glyph coverage: {retained}/{expectedCount}");
    }

    private static (int X, int Y) RotatedPoint(int x, int y, int width, int height, int turns)
    {
        if (turns == 2) return (width - 1 - x, height - 1 - y);
        double scale = (double)height / width;
        return ((int)((width - height * scale) / 2 + (height - 1 - y + 0.5) * scale),
            (int)((x + 0.5) * scale));
    }

    private static FrameEditLayer ColourLayer()
    {
        BitmapSource colour = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 160, 160, 32, 255 }, 4);
        return new FrameEditLayer
        {
            StartMs = 0, EndMs = 1000, Bounds = new VideoLayerBounds(0, 0.2, 0.15, 0.5),
            OverlayPngBase64 = Convert.ToBase64String(ImageCodec.EncodePng(colour)),
        };
    }

    private static string EncodeDarkPattern(string path, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(y < height / 2 ? 96 : 32);
            pixels[offset + 1] = (byte)(x < width / 2 ? 32 : 128);
            pixels[offset + 2] = (byte)(x < width / 2 ? 160 : 32);
            pixels[offset + 3] = 255;
        }
        using var encoder = new MediaFoundationVideoEncoder(
            new VideoEncoderOptions(path, width, height, 30, VideoEncoderOptions.DeriveBitrate(width, height, 30)),
            NullLogger.Instance);
        for (int i = 0; i < 30; i++)
            encoder.WriteFrame(new EncoderFrame(pixels, width, height, width * 4, i * 1000.0 / 30));
        encoder.Complete();
        return path;
    }

    private static Mp4RenderStatistics Export(RecordingResult recording, string path,
        VideoEditDocument document, Mp4RenderOptions? options = null) =>
        TrimReencoder.ReencodeWithStatistics(recording.OutputPath, path, 0, 1000, recording,
            encoderOptions => new MediaFoundationVideoEncoder(encoderOptions, NullLogger.Instance),
            NullLogger.Instance, document.TextOverlays, document.FrameEditLayers, renderOptions: options);

    private static VideoEditorWindow NewEditor(RecordingResult recording, string root, VideoEditDocument? document = null)
    {
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance, document);
        // Initialise the same duration/timeline state as MediaOpened without presenting a
        // video or invoking calibration: these tests exercise decoded pixels and edit commits.
        typeof(VideoEditorWindow).GetMethod("CompleteOpen", Private)!.Invoke(editor, [recording.DurationMs, true]);
        Assert.True(editor.IsMediaReadyForTest);
        return editor;
    }

    private static VideoEditDocument Document(VideoEditorWindow editor) =>
        (VideoEditDocument)typeof(VideoEditorWindow).GetField("_editDocument", Private)!.GetValue(editor)!;

    private static BitmapSource EditInput(VideoEditorWindow editor)
    {
        Task<BitmapSource?> task = editor.DecodeFrameForEditAsync(Time);
        Assert.True(RangePatternClip.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(30)), "Edit decode timed out.");
        return Assert.IsAssignableFrom<BitmapSource>(task.GetAwaiter().GetResult());
    }

    private static void CommitRotation(VideoEditorWindow editor, BitmapSource input, int turns, bool annotated)
    {
        var region = new RectD(0, 0, input.PixelWidth, input.PixelHeight);
        var document = AnnotationDocument.CreateFor(input.PixelWidth, input.PixelHeight);
        if (annotated)
            document.Add(new RectangleAnnotation
            {
                Rect = new RectD(230, 150, 60, 40), Fill = ColorRgba.FromRgb(160, 40, 40),
                Stroke = ColorRgba.FromRgb(160, 40, 40), StrokeThickness = 2,
            });
        var annotation = new AnnotationEditorControl(new FrozenFrame(input, region, null, 0), region, input,
            document, initialAssets: null);
        for (int i = 0; i < turns; i++) annotation.ShortcutActions["RotateRight"]();
        AnnotationEditingResult? committed = null;
        annotation.EditingCompleted += (_, result) => committed = result;
        annotation.SaveAndClose(); // Only capture hand-off: no persistence/clipboard handler.
        Assert.NotNull(committed);
        Assert.Equal(turns, committed.RotationTurns);
        typeof(VideoEditorWindow).GetMethod("AddFrameEditLayer", Private)!.Invoke(editor, [committed, Time]);
    }

    private static VideoEditDocument SaveReload(string root, VideoEditDocument document, int width, int height)
    {
        string path = Path.Combine(root, "edit.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document));
        return JsonSerializer.Deserialize<VideoEditDocument>(File.ReadAllText(path))!.NormalizeFor(width, height, 1000);
    }

    private static BitmapSource SaveReadPng(string root, FrameEditLayer layer)
    {
        string path = Path.Combine(root, "layer.png");
        File.WriteAllBytes(path, Convert.FromBase64String(layer.OverlayPngBase64));
        using var stream = File.OpenRead(path);
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    private static BitmapSource ToBitmap(DecodedVideoFrame frame) =>
        BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);

    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
