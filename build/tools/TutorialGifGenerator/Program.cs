using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

internal static class Program
{
    private const int Width = 640, Height = 360, SceneHeight = 312;
    private const int SelectorWidth = 864, SelectorHeight = 421;
    private const double SceneScale = 1.35;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Sample = TextCopySceneRenderer.Sample;
    private static readonly string[] SampleLines = Sample.Split('\n');
    private static readonly TutorialStoryboard Story = TextCopySceneRenderer.CreateStoryboard();
    private static readonly int[] Delays = Story.Frames.Select(frame => frame.DelayCentiseconds).ToArray();
    private static readonly int[] ReviewFrames = [0, 1, 94, 112, 121, 155];

    public static int Start(string[] args)
    {
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Use a new output folder to preserve every authoring attempt.");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
            ThemeService.Apply(AppTheme.Midnight);
            var receipts = new List<object>();
            foreach (string language in new[] { "ko", "en" })
            {
                using var languageScope = UiText.UseLanguage(language);
                string gif = Path.Combine(output, $"text-copy.{language}.gif");
                using var renderer = new TextCopySceneRenderer(language);
                TutorialGifPipeline.Generate(renderer, gif);
                receipts.Add(TutorialGifPipeline.Validate(gif, language, renderer.Storyboard, out GifSampleFrames preview));
                foreach (int i in ReviewFrames) SavePng(preview.Frames[i], Path.Combine(output, $"text-copy.{language}.frame-{i:00}.png"));
                SaveContactSheet(preview.Frames, language, output);
            }
            var provenance = new
            {
                SourceRevision = "2bb6cd6 (product b4ae87e, QA13)",
                Payload = Path.GetFullPath(args[0]),
                PayloadSha256 = Hash(Path.Combine(args[0], "MyCapture.dll")),
                Framework = RuntimeInformation.FrameworkDescription,
                Renderer = "Actual CaptureOverlayView + CaptureModeBar + Midnight product resources; offscreen 864x421 selector reduced to 640x312 plus 48px captions. Full opaque Indexed8 with a per-frame exact-color frequency palette and nearest RGB conversion without dithering; product AnimatedGifWriter/WPF encoder and GifSampleDecoder/Timeline.",
                Fixture = "Synthetic terminal and sample note, scripted pointer/drag, recognition/copy outcome illustration. Notification uses the same content, spacing, localized resources and themed button style as ThemedShellPresenter.ShowActionNotification. Not an OS screen recording or executed OCR/clipboard transaction. No Window.Show, App.Run, desktop acquisition, native input, user profile, persistence, queue, or clipboard access.",
                Flow = "Ordinary selector → T → drag two-line text region → automatic recognition/copy notification (optional advanced action untouched) → Ctrl+V into sample note.",
                Sample,
                SampleUtf16Length = Sample.Length,
                ProductCharacterCount = TextCaptureService.CountCharacters(Sample),
                FixedKeys = new[] { "T (selector-local)", "Ctrl+V (sample note)" },
                CaptureHotkey = "Omitted: customized current value is shown outside the GIF by the tutorial UI. CaptureText default is unassigned.",
                DelaysCentiseconds = Delays,
                Motion = Story.Frames.Select((frame, index) => new { Index = index, frame.Segment, frame.DelayCentiseconds, frame.Progress, X = frame.Cursor.X, Y = frame.Cursor.Y, frame.Pressed }),
                BufferPolicy = "Stream one rendered/quantized/encoded frame into writer; storyboard retains descriptors only, not a list of source bitmaps. Decode final GIF after encoder completion. Static scenes and last overlay only are cached.",
                Receipts = receipts,
            };
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine($"RESULT=PASS LANGUAGES=2 FRAMES={Delays.Length} SIZE={Width}x{Height} DURATION_MS={Delays.Sum() * 10}");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew); encoder.Save(stream);
    }

    private static void SaveContactSheet(IReadOnlyList<BitmapSource> frames, string language, string output)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            for (int i = 0; i < ReviewFrames.Length; i++)
                dc.DrawImage(frames[ReviewFrames[i]], new Rect(i % 2 * Width, i / 2 * Height, Width, Height));
        var sheet = new RenderTargetBitmap(Width * 2, Height * 3, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual); SavePng(sheet, Path.Combine(output, $"text-copy.{language}.contact-sheet.png"));
    }


}
