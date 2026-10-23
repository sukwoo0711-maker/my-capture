using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

public sealed class VideoFrameEditRangeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static TheoryData<bool, bool, string> CorrectionsAndEdits
    {
        get
        {
            var cases = new TheoryData<bool, bool, string>();
            foreach (bool fullRange in new[] { false, true })
            foreach (bool compress in new[] { false, true })
            foreach (string edit in new[] { "rotation", "blur", "mosaic" }) cases.Add(fullRange, compress, edit);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(CorrectionsAndEdits))]
    public void PreviewCorrectionCannotReachExtractedPngOrExport(bool fullRange, bool compress, string edit) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-frame-edit-range-");
        StudioRangePresentationCalibration previous = StudioRangeVideoEffect.Calibration;
        Func<bool> previousHardware = StudioRangeVideoEffect.HardwareShaderAvailable;
        VideoEditorWindow? editor = null;
        try
        {
            const int width = 320, height = 180;
            const double time = 500;
            VideoRangePresentation presentation = compress ? VideoRangePresentation.ExpandedAsStudio : VideoRangePresentation.StudioRetained;
            StudioRangeVideoEffect.Calibration = new(_ => Task.FromResult(new VideoRangeCalibrationResult(presentation, presentation)));
            StudioRangeVideoEffect.HardwareShaderAvailable = () => true;
            string clip = RangePatternClip.Encode(Path.Combine(root, "source.mp4"), width, height, fullRange);
            var recording = new RecordingResult(clip, 1000, 30, 30, width, height);
            editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance)
            {
                Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false,
            };
            editor.Show();
            Wait(() => editor.IsMediaReadyForTest || editor.HasMediaFailedForTest);
            Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
            RangePatternClip.WaitForCalibration();
            RangePatternClip.AssertEffect(editor.MediaForTest, compress ? VideoRangeCorrection.Compress : VideoRangeCorrection.Expand);
            Task exact = editor.PreviewSeekCoordinatorForTest.RequestExactAsync(time);
            Wait(() => exact.IsCompleted);
            exact.GetAwaiter().GetResult();

            Task<BitmapSource?> decoding = editor.DecodeFrameForEditAsync(time);
            Wait(() => decoding.IsCompleted);
            BitmapSource extracted = Assert.IsAssignableFrom<BitmapSource>(decoding.GetAwaiter().GetResult());
            Assert.True(extracted.IsFrozen);
            BitmapSource decoded = VideoFrameRenderPipeline.RenderSingleFrame(clip, time, width, height);
            Assert.Equal(Pixels(decoded), Pixels(extracted));
            // Also compare against source colours; an export-decoder-only oracle can share
            // the very same range error. The independent NV12 regressions cover tighter bounds.
            RangePatternClip.AssertMatchesSource(extracted, width, height);

            var region = new RectD(0, 0, width, height);
            var document = AnnotationDocument.CreateFor(width, height);
            (int blackX, int blackY) = RangePatternClip.Centre(0, width, height);
            (int whiteX, int whiteY) = RangePatternClip.Centre(6, width, height);
            AssertEndpoints(extracted, blackX, blackY, whiteX, whiteY);
            RectD[] patches = [new(blackX - 10, blackY - 10, 20, 20), new(whiteX - 10, whiteY - 10, 20, 20)];
            if (edit == "blur")
                foreach (RectD patch in patches) document.Add(new BlurAnnotation { Rect = patch, Radius = 2 });
            var annotation = new AnnotationEditorControl(new FrozenFrame(extracted, region, null, 0), region, extracted,
                document, initialAssets: null);
            if (edit == "rotation")
            {
                annotation.ShortcutActions["RotateRight"]();
                annotation.ShortcutActions["RotateRight"]();
            }
            if (edit == "mosaic")
                foreach (RectD patch in patches) annotation.ApplyMosaic(patch, 4);
            EditorMosaicAsyncTests.WaitFor(annotation.MosaicOperationsSettledAsync());
            AnnotationEditingResult? committed = null;
            annotation.EditingCompleted += (_, result) => committed = result;
            annotation.SaveAndClose(); // No persistence/clipboard handler: capture the actual editor hand-off.
            Assert.NotNull(committed);
            typeof(VideoEditorWindow).GetMethod("AddFrameEditLayer", Private)!.Invoke(editor, [committed, time]);
            var videoDocument = (VideoEditDocument)typeof(VideoEditorWindow).GetField("_editDocument", Private)!.GetValue(editor)!;
            FrameEditLayer layer = Assert.Single(videoDocument.FrameEditLayers);
            Assert.InRange(layer.StartMs, 499.99, 500.01);
            Assert.InRange(layer.EndMs, 533.32, 533.35);
            string png = Path.Combine(root, "persisted-layer.png");
            File.WriteAllBytes(png, Convert.FromBase64String(layer.OverlayPngBase64));
            using var stream = File.OpenRead(png);
            BitmapSource persisted = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (edit == "rotation")
            {
                blackX = width - 1 - blackX; blackY = height - 1 - blackY;
                whiteX = width - 1 - whiteX; whiteY = height - 1 - whiteY;
            }
            AssertEndpoints(persisted, blackX, blackY, whiteX, whiteY);

            string output = Path.Combine(root, "export.mp4");
            Assert.Equal(30, TrimReencoder.Reencode(clip, output, 0, 1000, recording,
                options => new MediaFoundationVideoEncoder(options, NullLogger.Instance), NullLogger.Instance,
                frameEditLayers: videoDocument.FrameEditLayers));
            BitmapSource exported = VideoFrameRenderPipeline.RenderSingleFrame(output, time, width, height);
            AssertEndpoints(exported, blackX, blackY, whiteX, whiteY);
        }
        finally
        {
            editor?.Close();
            StudioRangeVideoEffect.Calibration = previous;
            StudioRangeVideoEffect.HardwareShaderAvailable = previousHardware;
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void DecodedEditUsesRequestedTimestampWithoutAPresentedVideo() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-frame-edit-time-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = Path.Combine(root, "indices.mp4");
            FrameIndexClip.Encode(clip, 320, 180, 30, FrameIndexClip.Constant(30, 30));
            editor = new VideoEditorWindow(new RecordingResult(clip, 1000, 30, 30, 320, 180),
                AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
            // Deliberately no presented video at all. Requests must work without its visual,
            // and select the requested frame rather than the playhead (which is still zero).
            foreach (double time in new[] { 100.0, 500.0, 900.0 })
            {
                Task<BitmapSource?> task = editor.DecodeFrameForEditAsync(time);
                Wait(() => task.IsCompleted);
                BitmapSource frame = Assert.IsAssignableFrom<BitmapSource>(task.GetAwaiter().GetResult());
                Assert.Equal((int)(time * 30 / 1000), FrameIndexClip.Read(frame));
            }
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void ExistingLayersUseTheRequestedTimeAndAreSnapshottedBeforeBackgroundDecode() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-frame-edit-layers-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = RangePatternClip.Encode(Path.Combine(root, "source.mp4"), 320, 180, fullRange: false);
            var document = VideoEditDocument.CreateFor(320, 180, 1000);
            var red = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            document.FrameEditLayers.Add(new FrameEditLayer
            {
                StartMs = 400, EndMs = 600, OverlayPngBase64 = Convert.ToBase64String(ImageCodec.EncodePng(red)),
            });
            document.TextOverlays.Add(new TimedTextOverlay { StartMs = 400, EndMs = 600, Text = "timed" });
            editor = new VideoEditorWindow(new RecordingResult(clip, 1000, 30, 30, 320, 180),
                AppPaths.CreateForRoot(root), NullLoggerFactory.Instance, document);
            var live = (VideoEditDocument)typeof(VideoEditorWindow).GetField("_editDocument", Private)!.GetValue(editor)!;
            Task<BitmapSource?> task = editor.DecodeFrameForEditAsync(500);
            live.FrameEditLayers.Clear();
            live.TextOverlays.Clear();
            Wait(() => task.IsCompleted);
            BitmapSource frame = Assert.IsAssignableFrom<BitmapSource>(task.GetAwaiter().GetResult());
            BitmapSource expected = VideoFrameRenderPipeline.RenderSingleFrame(clip, 500, 320, 180,
                overlays: null, frameEditLayers: document.FrameEditLayers);
            Assert.Equal(Pixels(expected), Pixels(frame));
            live.FrameEditLayers.AddRange(document.FrameEditLayers);
            live.TextOverlays.AddRange(document.TextOverlays);
            task = editor.DecodeFrameForEditAsync(600); // Half-open layer end.
            Wait(() => task.IsCompleted);
            frame = Assert.IsAssignableFrom<BitmapSource>(task.GetAwaiter().GetResult());
            Assert.Equal(Pixels(VideoFrameRenderPipeline.RenderSingleFrame(clip, 600, 320, 180)), Pixels(frame));
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    private static void AssertEndpoints(BitmapSource bitmap, int blackX, int blackY, int whiteX, int whiteY)
    {
        var black = RangePatternClip.Sample(bitmap, blackX, blackY);
        var white = RangePatternClip.Sample(bitmap, whiteX, whiteY);
        foreach (byte value in new[] { black.R, black.G, black.B }) Assert.InRange(value, (byte)0, (byte)8);
        foreach (byte value in new[] { white.R, white.G, white.B }) Assert.InRange(value, (byte)247, (byte)255);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Wait(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready() && DateTime.UtcNow < deadline) RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(ready(), "Frame edit operation did not finish within 20 seconds.");
    }
}
