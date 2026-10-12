using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using static TutorialDrawing;

internal static class Program
{
    internal static readonly string[] ImageFeatures = ["image-rectangle", "image-ellipse", "image-arrow", "image-pen", "image-highlighter", "image-text", "image-step-number", "image-mosaic", "image-blur", "image-crop", "image-rotate", "image-save", "image-copy"];
    internal static readonly string[] VideoFeatures = ["video-play", "video-frame-step", "video-trim", "video-gif-export"];
    internal static readonly string[] AllFeatures = ["text-copy", ..CaptureSceneRenderer.Features, ..WorkspaceSceneRenderer.Features, ..ImageFeatures, ..VideoFeatures];
    public static int Start(string[] args)
    {
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Use a new output folder to preserve every authoring attempt.");
        string[] features = args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : AllFeatures;
        if (features.Length == 0 || features.Distinct().Count() != features.Length || features.Except(AllFeatures).Any()) throw new ArgumentException("Unknown or duplicate feature ID.");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
            ThemeService.Apply(AppTheme.Midnight);
            var receipts = new List<object>();
            foreach (string feature in features)
                foreach (string language in new[] { "ko", "en" })
                {
                    receipts.Add(ProcessAsset(feature, language, args[0], output));
                    // No decoded asset or renderer survives this boundary. A catalog does not preload GIFs.
                    GC.Collect(); GC.WaitForPendingFinalizers();
                }
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { SourceRevision = "2bb6cd6 (product b4ae87e, QA13)", Payload = Path.GetFullPath(args[0]), PayloadSha256 = Hash(Path.Combine(args[0], "MyCapture.dll")), Framework = RuntimeInformation.FrameworkDescription, Features = features, Count = receipts.Count, Receipts = receipts }, JsonOptions), new UTF8Encoding(false));
            Console.WriteLine($"RESULT=PASS FEATURES={features.Length} LANGUAGES=2 ASSETS={receipts.Count} SIZE={Width}x{Height}");
            return 0;
        }
        finally { app.Shutdown(); }
    }
    internal static ITutorialSceneRenderer CreateRenderer(string feature, string language)
    {
        if (feature == "text-copy") return new TextCopySceneRenderer(language);
        if (CaptureSceneRenderer.Features.Contains(feature)) return new CaptureSceneRenderer(feature, language);
        if (WorkspaceSceneRenderer.Features.Contains(feature)) return new WorkspaceSceneRenderer(feature, language);
        string type = ImageFeatures.Contains(feature) ? "EditorSceneRenderer" : "VideoSceneRenderer";
        return (ITutorialSceneRenderer)Activator.CreateInstance(typeof(Program).Assembly.GetType(type, true)!, feature, language)!;
    }
    private static object ProcessAsset(string feature, string language, string payload, string output)
    {
        using var languageScope = UiText.UseLanguage(language);
        using var renderer = CreateRenderer(feature, language);
        if (renderer.Id != feature) throw new InvalidDataException("Renderer identity");
        string gif = Path.Combine(output, $"{feature}.{language}.gif");
        TutorialGifPipeline.Generate(renderer, gif);
        object validation = TutorialGifPipeline.Validate(gif, language, renderer.Storyboard, out GifSampleFrames preview);
        int[] keys = renderer.KeyFrames.Distinct().ToArray();
        foreach (int i in keys) SavePng(preview.Frames[i], Path.Combine(output, $"{feature}.{language}.frame-{i:000}.png"));
        SaveSheet(preview.Frames, keys, 2, Path.Combine(output, $"{feature}.{language}.contact-sheet.png"));
        int[] motion = renderer.Storyboard.Frames.Select((frame, index) => (frame, index)).Where(item => item.frame.DelayCentiseconds == 4).GroupBy(item => item.frame.Segment).SelectMany(group => new[] { group.First().index, group.ElementAt(group.Count() / 2).index, group.Last().index }).Distinct().ToArray();
        SaveSheet(preview.Frames, motion, 3, Path.Combine(output, $"{feature}.{language}.motion-sheet.png"));
        var receipt = new
        {
            Feature = feature, Language = language, Validation = validation,
            Payload = Path.GetFullPath(payload), PayloadSha256 = Hash(Path.Combine(payload, "MyCapture.dll")), Framework = RuntimeInformation.FrameworkDescription,
            Provenance = feature == "text-copy" ? "Actual CaptureOverlayView + CaptureModeBar + product notification content/styles; synthetic terminal and note." : CaptureSceneRenderer.Features.Contains(feature) ? "Actual CaptureOverlayView/ModeBar/selection cues where applicable; product CountdownWindow.Content and paused-frame brush; synthetic desktop, recording strip/result viewer and scrolling document." : WorkspaceSceneRenderer.Features.Contains(feature) ? "Actual PinWindow.Content, PinHoverBar.Content, ClipboardTextRenderer, QuickAccessWindow.Content and GalleryFolderEditDialog.Content; product themed controls; condensed library fixture follows GalleryWindow.xaml header/folder rail/cards, transaction results illustrated." : "Feature renderer uses product drawing/annotation/video UI resources over synthetic media; see renderer source and development LOG.",
            Fixture = "Offscreen illustrative tutorial; no Window.Show/App.Run, native input/desktop acquisition, OCR/clipboard transaction, user profile/persistence or actual file removal. UI state and results are scripted fixture data. Captions contain usage instructions only.",
            Sample = TextCopySceneRenderer.Sample, ProductCharacterCount = TextCaptureService.CountCharacters(TextCopySceneRenderer.Sample),
            CaptureHotkey = "Global customizable chords omitted; current values appear in the product tutorial UI.",
            SelectionMargin = feature == "text-copy" ? "Logical selection [59,132]–[367,212]; second line y177,22px mono font; actual FormattedText.Height+4px asserted before render." : "Feature-specific fixture target.",
            BufferPolicy = "Render, quantize and encode one frame at a time. Decode only the current completed GIF; release renderer/preview before the next asset. Receipts retain metadata and hashes only.",
            Motion = renderer.Storyboard.Frames.Select((frame, index) => new { Index = index, frame.Segment, frame.DelayCentiseconds, frame.Progress, X = frame.Cursor.X, Y = frame.Cursor.Y, frame.Pressed }),
        };
        File.WriteAllText(Path.Combine(output, $"{feature}.{language}.receipt.json"), JsonSerializer.Serialize(receipt, JsonOptions), new UTF8Encoding(false));
        Console.WriteLine($"ASSET=PASS ID={feature} LANGUAGE={language} FRAMES={preview.Frames.Count} DURATION_MS={preview.Timeline.TotalMs} BYTES={new FileInfo(gif).Length}");
        return receipt;
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew); encoder.Save(stream);
    }
    private static void SaveSheet(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> indices, int columns, string path)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            for (int i = 0; i < indices.Count; i++) dc.DrawImage(frames[indices[i]], new Rect(i % columns * Width, i / columns * Height, Width, Height));
        var sheet = new RenderTargetBitmap(Width * columns, Height * ((indices.Count + columns - 1) / columns), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual); SavePng(sheet, path);
    }
}