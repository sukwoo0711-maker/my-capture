using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class VideoFrameStillImageTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Width = 640, Height = 360;
    private const double Time = 500;

    public static TheoryData<int, bool, string> StillActions
    {
        get
        {
            var cases = new TheoryData<int, bool, string>();
            foreach (int turns in new[] { 0, 1, 2 })
            foreach (bool annotated in new[] { false, true })
            foreach (string action in new[] { "Done", "Copy", "Save", "SaveAs", "Reduced", "CopyOnly" })
                cases.Add(turns, annotated, action);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(StillActions))]
    public void RealFrameHandler_AllStillOutputsContainOneUprightSubtitle(int turns, bool annotated, string action) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-frame-still-");
        SynchronizationContext? previous = SynchronizationContext.Current;
        VideoEditorWindow? video = null;
        AnnotationEditorWindow? window = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var paths = AppPaths.CreateForRoot(root);
            var settings = new AppSettings();
            settings.Export.ShowSourceWindowTitle = false;
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            var copied = new List<BitmapSource>();
            BitmapSource? reduced = null;
            string exportPath = Path.Combine(root, "save-as.png");
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance,
                (image, _) => { copied.Add(image); return Task.FromResult(true); })
            {
                SaveAsPrompt = _ => exportPath,
                ExportOptionsPrompt = options => options,
                ReducedExportPrompt = (image, _) =>
                {
                    reduced = image;
                    File.WriteAllBytes(exportPath, ImageCodec.EncodePng(image));
                    return true;
                },
            };
            // Invoke the actual App save handler, with its real persistence/commit services.
            // Only the clipboard destination and interactive export dialogs are injected.
            var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
            typeof(App).GetField("_queue", Private)!.SetValue(app, queue);
            typeof(App).GetField("_persistence", Private)!.SetValue(app, persistence);
            typeof(App).GetField("_commit", Private)!.SetValue(app, commit);
            VideoEditDocument document = VideoDocument();
            BitmapSource input = FrameWithEarlierLayer();
            video = new VideoEditorWindow(new RecordingResult(Path.Combine(root, "unused.mp4"), 1000, 30, 30, Width, Height),
                paths, NullLoggerFactory.Instance, document);
            typeof(VideoEditorWindow).GetField("_durationMs", Private)!.SetValue(video, 1000.0);
            video.FrameImageCommitHandlerFactory = () => (FrameImageCommitSession)typeof(App)
                .GetMethod("CreateRecordedFrameCommitHandler", Private)!.Invoke(app, null)!;
            window = video.CreateFrameEditor(input, Time, document);
            Assert.Equal(0, BrightMask(input).Count(value => value)); // Editor input has no baked text.
            if (annotated)
            {
                var controller = (AnnotationEditorController)typeof(AnnotationEditorControl)
                    .GetField("_controller", Private)!.GetValue(window.Editor)!;
                controller.Document.Add(new RectangleAnnotation
                {
                    Rect = new RectD(200, 0, 240, 300), Fill = ColorRgba.FromRgb(160, 40, 40),
                    Stroke = ColorRgba.FromRgb(160, 40, 40), StrokeThickness = 0,
                });
            }
            for (int i = 0; i < turns; i++) window.Editor.ShortcutActions["RotateRight"]();
            AnnotationEditingResult? videoResult = null;
            window.Committed += (_, result) => videoResult = result;
            // A changing video document/playhead cannot change the subtitle captured at 500ms.
            document.TextOverlays[0].Text = "CHANGED AFTER EXTRACTION";
            document.TextOverlays.Clear();
            BitmapSource expected = ExpectedStill(input, turns, annotated);
            if (action is "Reduced" or "CopyOnly")
                typeof(AnnotationEditorControl).GetMethod("Commit", Private)!.Invoke(window.Editor,
                    [action == "CopyOnly" ? EditorCommitAction.CopyToClipboard : EditorCommitAction.SaveAs, action == "Reduced"]);
            else window.Editor.ShortcutActions[action]();
            Wait(() => !window.Editor.IsCommitInProgress);
            AssertStill(expected, Assert.Single(copied), "clipboard");
            int expectedCount = BrightMask(expected).Count(value => value);
            output.WriteLine($"turns={turns}, annotated={annotated}, action={action}, expected subtitle pixels={expectedCount}");
            if (action == "CopyOnly")
            {
                Assert.Empty(queue.Records);
                Assert.Null(videoResult);
                Assert.False(window.WasCommitted);
            }
            else
            {
                Assert.True(window.WasCommitted);
                Assert.NotNull(videoResult);
                Assert.Equal(0, BrightMask(videoResult.SelectedBitmap).Count(value => value));
                Assert.Equal(annotated ? 1 : 0, videoResult.Document.Items.Count);
                CaptureRecord record = Assert.Single(queue.Records);
                AssertStill(expected, ReadPng(queue.GetFilePath(record, CaptureFileNames.Original)), "original PNG");
                AssertStill(expected, ReadPng(queue.GetFilePath(record, CaptureFileNames.Rendered)), "rendered PNG");
                // The saved still is a complete snapshot: reopening its sidecar cannot replay
                // annotations over the caption or composite its translucent background twice.
                string layersPath = queue.GetFilePath(record, CaptureFileNames.Layers);
                if (File.Exists(layersPath))
                    Assert.Empty(AnnotationDocument.TryFromJson(File.ReadAllText(layersPath))!.Items);
            }
            if (action == "Save")
                AssertStill(expected, ReadPng(Assert.Single(Directory.GetFiles(paths.QuickSaveRoot, "*.png"))), "quick-save PNG");
            if (action is "SaveAs" or "Reduced") AssertStill(expected, ReadPng(exportPath), "export PNG");
            if (action == "Reduced") AssertStill(expected, Assert.IsAssignableFrom<BitmapSource>(reduced), "reduction input");
        }
        finally
        {
            window?.Close();
            video?.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(399, false)]
    [InlineData(400, true)]
    [InlineData(500, true)]
    [InlineData(600, false)]
    public void DraftSnapshot_PreservesFrameTimeAndHalfOpenSubtitleInterval(double time, bool active) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-frame-still-draft-");
        VideoEditorWindow? video = null;
        AnnotationEditorWindow? editor = null;
        try
        {
            BitmapSource input = FrameWithEarlierLayer();
            video = new VideoEditorWindow(new RecordingResult(Path.Combine(root, "unused.mp4"), 1000, 30, 30, Width, Height),
                AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
            editor = video.CreateFrameEditor(input, time, VideoDocument());
            Task<DraftSnapshot?> task = editor.CaptureDraftSnapshotAsync();
            Wait(() => task.IsCompleted);
            DraftSnapshot snapshot = Assert.IsType<DraftSnapshot>(task.GetAwaiter().GetResult());
            VideoFrameStillImage recovered = JsonSerializer.Deserialize<VideoFrameStillImage>(
                snapshot.Metadata[AnnotationEditorWindow.DraftFrameStillImageKey])!;
            Assert.Equal(time, recovered.SourceTimeMs);
            Assert.Equal(active ? 1 : 0, recovered.TextOverlays.Count);
            var region = new RectD(0, 0, Width, Height);
            var result = new AnnotationEditingResult(new MyCapture.Platform.Capture.FrozenFrame(input, region, null, 0),
                region, input, AnnotationDocument.CreateFor(Width, Height), EditorCommitAction.Done,
                new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
            Task<AnnotationEditingResult> rendering = recovered.CreateResultAsync(result);
            Wait(() => rendering.IsCompleted);
            BitmapSource actual = rendering.GetAwaiter().GetResult().SelectedBitmap;
            if (active) AssertStill(ExpectedStill(input, 0, false), actual, "recovered frame");
            else Assert.Equal(Pixels(input), Pixels(actual));
        }
        finally { editor?.Close(); video?.Close(); OwnedTestDirectory.Delete(root); }
    });

    private static VideoEditDocument VideoDocument()
    {
        var document = VideoEditDocument.CreateFor(Width, Height, 1000);
        document.TextOverlays.Add(new TimedTextOverlay
        {
            Text = "F L 7", StartMs = 400, EndMs = 600,
            Bounds = new VideoLayerBounds(0.3, 0.03, 0.4, 0.18), Placement = VideoTextPlacement.Top,
        });
        document.TextOverlays.Add(new TimedTextOverlay { Text = "INACTIVE", StartMs = 700, EndMs = 900 });
        return document;
    }

    private static BitmapSource FrameWithEarlierLayer()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int p = (y * Width + x) * 4;
            pixels[p] = (byte)(y < Height / 2 ? 96 : 32);
            pixels[p + 1] = (byte)(x < Width / 2 ? 32 : 128);
            pixels[p + 2] = (byte)(x < Width / 2 ? 160 : 32);
            pixels[p + 3] = 255;
            // A cyan earlier video layer, already composed into the frame-edit input.
            if (x < 96 && y >= 72 && y < 252) { pixels[p] = 160; pixels[p + 1] = 160; pixels[p + 2] = 32; }
        }
        BitmapSource bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ExpectedStill(BitmapSource input, int turns, bool annotated)
    {
        // Independent drawing: apply the known annotation then rotate the entire image.
        // Do not use the product still compositor, annotation renderer or timed-text renderer.
        var baseVisual = new DrawingVisual();
        using (DrawingContext dc = baseVisual.RenderOpen())
        {
            dc.DrawImage(input, new Rect(0, 0, Width, Height));
            if (annotated) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(160, 40, 40)), null, new Rect(200, 0, 240, 300));
        }
        var baseBitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        baseBitmap.Render(baseVisual);
        BitmapSource rotated = turns == 0 ? baseBitmap : new TransformedBitmap(baseBitmap, new RotateTransform(turns * 90));
        int width = rotated.PixelWidth, height = rotated.PixelHeight;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(rotated, new Rect(0, 0, width, height));
            // The export typography contract, rendered directly with WPF. The normalized
            // destination remains at the top at 0/90/180 degrees; only base/annotations rotate.
            double fontSize = Math.Clamp(height * 0.052, 18, 72);
            double paddingX = Math.Max(10, fontSize * 0.45), paddingY = Math.Max(6, fontSize * 0.24);
            double maxWidth = width - 2 * Math.Max(16, width * 0.06);
            double maxHeight = Math.Min(height, Math.Max(height * 0.4, fontSize * 1.4 + 2 * paddingY));
            var text = new FormattedText("F L 7", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                fontSize, Brushes.White, 1)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = maxWidth - 2 * paddingX,
                MaxTextHeight = Math.Max(fontSize * 1.4, maxHeight - 2 * paddingY),
            };
            double boxWidth = Math.Min(maxWidth, text.Width + 2 * paddingX);
            double boxHeight = Math.Min(maxHeight, text.Height + 2 * paddingY);
            dc.PushTransform(new TranslateTransform(width * 0.3, height * 0.03));
            dc.PushTransform(new ScaleTransform(width * 0.4 / boxWidth, height * 0.18 / boxHeight));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 8, 8, 8)), null,
                new Rect(0, 0, boxWidth, boxHeight), paddingY, paddingY);
            dc.DrawText(text, new Point((boxWidth - text.MaxTextWidth) / 2, (boxHeight - text.Height) / 2));
            dc.Pop(); dc.Pop();
        }
        var expected = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        expected.Render(visual);
        expected.Freeze();
        return expected;
    }

    private static void AssertStill(BitmapSource expected, BitmapSource actual, string label)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        bool[] template = BrightMask(expected), observed = BrightMask(actual);
        int expectedCount = template.Count(value => value), actualCount = observed.Count(value => value);
        Assert.True(expectedCount > 80);
        Assert.True(expectedCount == actualCount, $"{label}: subtitle pixels {actualCount}, expected {expectedCount}");
        Assert.Equal(template, observed); // Exact upright location, no rotated/extra glyphs anywhere.
        Assert.Equal(Pixels(expected), Pixels(actual)); // Also checks translucent box, earlier layer and annotation order.
    }

    private static BitmapSource ReadPng(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    private static bool[] BrightMask(BitmapSource bitmap)
    {
        byte[] pixels = Pixels(bitmap);
        bool[] mask = new bool[bitmap.PixelWidth * bitmap.PixelHeight];
        for (int i = 0; i < mask.Length; i++)
            mask[i] = pixels[i * 4] > 200 && pixels[i * 4 + 1] > 200 && pixels[i * 4 + 2] > 200;
        return mask;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Wait(Func<bool> completed) =>
        Assert.True(RangePatternClip.PumpUntil(completed, TimeSpan.FromSeconds(30)), "Frame-image operation timed out.");
}
