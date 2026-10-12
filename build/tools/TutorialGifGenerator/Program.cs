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
    private const string Sample = "npm run build\nBuild completed";
    private static readonly string[] SampleLines = Sample.Split('\n');
    private static readonly int[] Delays = [140, 120, 50, 25, 25, 25, 25, 80, 160, 140, 330];
    private static readonly int[] ReviewFrames = [0, 1, 7, 8, 9, 10];

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
                var frames = RenderStory(language);
                string gif = Path.Combine(output, $"text-copy.{language}.gif");
                using (var stream = new FileStream(gif, FileMode.CreateNew))
                using (var writer = new AnimatedGifWriter(stream, Width, Height, 25,
                    differencing: GifFrameDifferencing.None, encoder: GifImageEncoder.Wpf))
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        BitmapSource indexed = Quantize(frames[i]);
                        byte[] encoded = AnimatedGifWriter.EncodeFrame(indexed).ToArray();
                        writer.AddEncodedFrame(new GifFrameUpdate(new GifPixelRect(0, 0, Width, Height), encoded), Delays[i]);
                    }
                    writer.Complete();
                }
                receipts.Add(Validate(gif, language, out GifSampleFrames preview));
                foreach (int i in ReviewFrames) SavePng(preview.Frames[i], Path.Combine(output, $"text-copy.{language}.frame-{i:00}.png"));
                SaveContactSheet(preview.Frames, language, output);
            }
            var provenance = new
            {
                SourceRevision = "a8ef489 (product 33f6652, QA12)",
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
                Receipts = receipts,
            };
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine($"RESULT=PASS LANGUAGES=2 FRAMES={Delays.Length} SIZE={Width}x{Height} DURATION_MS={Delays.Sum() * 10}");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static List<BitmapSource> RenderStory(string language)
    {
        bool ko = language == "ko";
        BitmapSource desktop = SourceScene(ko);
        var bounds = new RectD(0, 0, SelectorWidth, SelectorHeight);
        var monitor = new MonitorInfo("Tutorial fixture", bounds, bounds, 96, true);
        PointD cursor = new(76 * SceneScale, 226 * SceneScale);
        var view = new CaptureOverlayView(new FrozenFrame(desktop, bounds, monitor, 0), showMagnifier: false, showSelectionGrid: false)
        {
            ReadCursor = () => cursor,
            ReadMonitors = () => [monitor],
            TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
            OffersOtherActions = true,
            DetectWindows = false,
            ShowModeBar = true,
        };
        var frames = new List<BitmapSource>();
        void AddOverlay(string caption, string key = "")
        {
            view.Measure(new Size(SelectorWidth, SelectorHeight));
            view.Arrange(new Rect(0, 0, SelectorWidth, SelectorHeight));
            view.UpdateLayout();
            Invoke(view, "RenderLayers");
            var scene = new RenderTargetBitmap(SelectorWidth, SelectorHeight, 96, 96, PixelFormats.Pbgra32);
            scene.Render(view);
            frames.Add(Compose(scene, caption, key));
        }
        try
        {
            Set(view, "_pointerInitialized", true);
            Set(view, "_cursorPixel", cursor);
            AddOverlay(ko ? "캡처 선택 화면" : "Open the capture selector");
            // T's actual handler needs a presentation source; an offscreen HwndSource is not required:
            // this is the same SelectMode call made by the Text mode bar action, with the fixed T shown.
            view.SelectMode(CaptureSelectorMode.Text);
            Require(view.Mode == CaptureSelectorMode.Text, "Text mode transition");
            AddOverlay(ko ? "T 키로 텍스트 모드 전환" : "Press T for text mode", "T");
            cursor = new PointD(59 * SceneScale, 132 * SceneScale);
            Set(view, "_cursorPixel", cursor);
            AddOverlay(ko ? "복사할 텍스트 영역 선택" : "Drag around the text");
            var interaction = typeof(CaptureOverlayView).GetField("_interaction", PrivateInstance)!;
            interaction.SetValue(view, Enum.Parse(interaction.FieldType, "Create"));
            Set(view, "_dragAnchor", cursor);
            foreach (PointD next in new[] { new PointD(136, 148), new PointD(213, 165), new PointD(290, 182), new PointD(367, 200) })
            {
                cursor = new PointD(next.X * SceneScale, next.Y * SceneScale);
                Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
                AddOverlay(ko ? "복사할 텍스트 영역 선택" : "Drag around the text");
            }
            Require(view.Selection is { } selection && selection.Width >= 384 && selection.Height >= 84, "Drag selects complete two-line sample");
            AddOverlay(ko ? "마우스 버튼을 놓으면 자동 복사" : "Release to recognise and copy");
            frames.Add(Compose(CopiedScene(desktop), ko ? "텍스트 복사됨" : "Text copied automatically"));
            frames.Add(Compose(NoteScene(ko, false), ko ? "메모에 붙여넣기" : "Paste into your note", "Ctrl+V"));
            frames.Add(Compose(NoteScene(ko, true), ko ? "선택한 텍스트 붙여넣기 완료" : "Selected text pasted"));
            Require(frames.Count == Delays.Length, "Storyboard frame count");
            return frames;
        }
        finally { view.ReleaseResources(); }
    }

    private static BitmapSource SourceScene(bool ko) => Draw(SceneHeight, dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(24, 28, 37)), new Pen(Brush("Border.Subtle"), 1), new Rect(38, 91, 564, 198), 8, 8);
        Text(dc, ko ? "터미널" : "Terminal", 54, 103, 14, Brush("Text.Secondary"));
        dc.DrawLine(new Pen(Brush("Border.Subtle"), 1), new Point(38, 125), new Point(602, 125));
        Text(dc, SampleLines[0], 125, 140, 22, Brushes.White, mono: true);
        Text(dc, SampleLines[1], 125, 177, 22, new SolidColorBrush(Color.FromRgb(154, 225, 177)), mono: true);
        Text(dc, ko ? "출력" : "Output", 65, 251, 13, Brush("Text.Secondary"));
    });

    private static BitmapSource CopiedScene(BitmapSource desktop)
    {
        // Mirror the product's notification content only; do not show a Window or run its action.
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = UiText.Get("TextCapture_Copied"), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = UiText.Format("TextCapture_CopiedDetail", TextCaptureService.CountCharacters(Sample)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var advanced = new Button { Content = UiText.Get("TextCapture_OpenAdvanced"), MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
        advanced.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        buttons.Children.Add(advanced);
        buttons.Children.Add(new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36 });
        content.Children.Add(buttons);
        var border = new Border { Width = 360, Padding = new Thickness(20), Child = content, Background = Brush("Surface.Base"), BorderBrush = Brush("Border.Control"), BorderThickness = new Thickness(1) };
        TextElement.SetForeground(border, Brush("Text.Primary"));
        TextElement.SetFontFamily(border, (FontFamily)Application.Current.Resources["Font.Ui"]);
        border.Measure(new Size(360, double.PositiveInfinity));
        border.Arrange(new Rect(new Point(0, 0), border.DesiredSize));
        border.UpdateLayout();
        var card = new RenderTargetBitmap(360, (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        card.Render(border);
        return Draw(SceneHeight, dc =>
        {
            dc.DrawImage(desktop, new Rect(0, 0, Width, SceneHeight));
            dc.DrawImage(card, new Rect(264, SceneHeight - card.PixelHeight - 16, card.PixelWidth, card.PixelHeight));
        });
    }

    private static BitmapSource NoteScene(bool ko, bool pasted) => Draw(SceneHeight, dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Control"), 1), new Rect(38, 47, 564, 238), 8, 8);
        Text(dc, ko ? "메모" : "Note", 57, 63, 16, Brush("Text.Primary"));
        dc.DrawLine(new Pen(Brush("Border.Subtle"), 1), new Point(38, 97), new Point(602, 97));
        Text(dc, ko ? "빌드 기록" : "Build log", 65, 115, 17, Brush("Text.Secondary"));
        if (pasted)
        {
            Text(dc, SampleLines[0], 65, 158, 22, Brush("Text.Primary"), mono: true);
            Text(dc, SampleLines[1], 65, 195, 22, Brush("Text.Primary"), mono: true);
            dc.DrawLine(new Pen(Brush("Text.Primary"), 1.5), new Point(265, 195), new Point(265, 219));
        }
        else dc.DrawLine(new Pen(Brush("Text.Primary"), 1.5), new Point(65, 158), new Point(65, 182));
    });

    private static BitmapSource Compose(BitmapSource scene, string caption, string key = "") => Draw(Height, dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, Height));
        dc.DrawImage(scene, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 21, 29)), null, new Rect(0, SceneHeight, Width, Height - SceneHeight));
        Text(dc, caption, 20, 326, 18, Brushes.White);
        if (key.Length > 0)
        {
            int badgeWidth = key.Length == 1 ? 36 : 84;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(48, 57, 72)), new Pen(new SolidColorBrush(Color.FromRgb(112, 126, 145)), 1), new Rect(Width - badgeWidth - 18, 320, badgeWidth, 32), 5, 5);
            Text(dc, key, Width - badgeWidth - 10, 326, 16, Brushes.White, mono: true);
        }
    });

    private static BitmapSource Draw(int height, Action<DrawingContext> render)
    {
        var visual = new DrawingVisual();
        bool scene = height == SceneHeight;
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (scene) dc.PushTransform(new ScaleTransform(SceneScale, SceneScale));
            render(dc);
            if (scene) dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(scene ? SelectorWidth : Width, scene ? SelectorHeight : height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private static void Text(DrawingContext dc, string value, double x, double y, double size, Brush brush, bool mono = false)
    {
        var family = (FontFamily)Application.Current.Resources[mono ? "Font.Mono" : "Font.Ui"];
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, 1);
        dc.DrawText(text, new Point(x, y));
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static BitmapSource Quantize(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Width * Height * 4];
        bgra.CopyPixels(pixels, Width * 4, 0);
        var histogram = new Dictionary<int, int>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int color = (pixels[i + 2] << 16) | (pixels[i + 1] << 8) | pixels[i];
            histogram[color] = histogram.GetValueOrDefault(color) + 1;
        }
        int[] chosen = histogram.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(256).Select(pair => pair.Key).ToArray();
        var colors = chosen.Select(color => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)).ToList();
        var lookup = new Dictionary<int, byte>();
        foreach (int color in histogram.Keys)
        {
            int best = 0, minimum = int.MaxValue;
            for (int c = 0; c < chosen.Length; c++)
            {
                int dr = ((color >> 16) & 255) - colors[c].R;
                int dg = ((color >> 8) & 255) - colors[c].G;
                int db = (color & 255) - colors[c].B;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < minimum) { minimum = distance; best = c; }
            }
            lookup[color] = (byte)best;
        }
        var indexes = new byte[Width * Height];
        for (int i = 0; i < indexes.Length; i++)
            indexes[i] = lookup[(pixels[i * 4 + 2] << 16) | (pixels[i * 4 + 1] << 8) | pixels[i * 4]];
        BitmapSource result = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Indexed8, new BitmapPalette(colors), indexes, Width);
        result.Freeze();
        return result;
    }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidDataException(detail); }
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

    private static object Validate(string path, string language, out GifSampleFrames decoded)
    {
        decoded = GifSampleDecoder.Decode(path);
        Require(decoded.PixelWidth == Width && decoded.PixelHeight == Height, "Canvas");
        Require(decoded.Frames.Count == Delays.Length && decoded.Frames.Count <= 30, "GIF frame count");
        Require(decoded.DelaysCentiseconds.SequenceEqual(Delays), "Preserved holds and drag delays");
        Require(decoded.Timeline.TotalMs is >= 10000 and <= 12000, "Duration");
        byte[] bytes = File.ReadAllBytes(path);
        Require(bytes.Length <= 1024 * 1024, "GIF budget");
        int netscape = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        Require(netscape >= 0 && bytes[netscape + 11] == 3 && bytes[netscape + 12] == 1
            && bytes[netscape + 13] == 0 && bytes[netscape + 14] == 0, "Infinite GIF loop extension");
        using var stream = new MemoryStream(bytes, false);
        var raw = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        long memory = 0;
        var hashes = new List<string>();
        for (int i = 0; i < decoded.Frames.Count; i++)
        {
            BitmapSource frame = decoded.Frames[i];
            Require(frame.IsFrozen, "Detached frozen playback frame");
            var metadata = (BitmapMetadata)raw.Frames[i].Metadata;
            Require(raw.Frames[i].PixelWidth == Width && raw.Frames[i].PixelHeight == Height
                && Convert.ToInt32(metadata.GetQuery("/imgdesc/Left")) == 0
                && Convert.ToInt32(metadata.GetQuery("/imgdesc/Top")) == 0, "Full frame descriptor");
            Require(Convert.ToInt32(metadata.GetQuery("/grctlext/Disposal")) == 1, "Keep-frame disposal");
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[Width * Height * 4]; bgra.CopyPixels(pixels, Width * 4, 0);
            Require(Enumerable.Range(0, Width * Height).All(p => pixels[p * 4 + 3] == 255), "Full opaque frame");
            hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            memory += (long)((Width * frame.Format.BitsPerPixel + 7) / 8) * Height;
            Require(decoded.Timeline.IndexAt(decoded.Timeline.StartOf(i)) == i, "Timeline boundary");
        }
        Require(memory <= 30 * 1024 * 1024, "Decoded memory");
        Require(hashes.Distinct().Count() >= 9, "Distinct stages and drag frames");
        Require(decoded.Timeline.IndexAt(decoded.Timeline.TotalMs) == 0, "Timeline loops");
        var player = new GifSamplePlayer(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        player.Load(decoded.Timeline);
        for (int i = 0; i < decoded.Frames.Count; i++) { player.Seek(i); Require(player.Index == i, "Player seeks every stage"); }
        player.Play(); Require(player.IsPlaying, "Player plays"); player.Pause(); Require(!player.IsPlaying, "Player pauses");
        return new { Language = language, Bytes = bytes.Length, Sha256 = Hash(path), Frames = decoded.Frames.Count,
            DurationMs = decoded.Timeline.TotalMs, DelaysCentiseconds = decoded.DelaysCentiseconds,
            DecodedFrameBytes = memory, LoopCount = 0, Disposal = 1, FullOpaqueFrames = true, FrameSha256 = hashes,
            PlaybackChecks = "decode all; exact delays; each frame boundary and seek; infinite wrap; play/pause" };
    }
}
