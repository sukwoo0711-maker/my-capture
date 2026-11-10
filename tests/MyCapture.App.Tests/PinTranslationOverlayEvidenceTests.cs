using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Explicit, retained PNG evidence from real Windows OCR and the existing offline models.</summary>
public sealed class PinTranslationOverlayEvidenceTests(ITestOutputHelper output)
{
    internal const string OutputVariable = "MYCAPTURE_PIN_OVERLAY_EVIDENCE";
    internal const string ModelsVariable = "MYCAPTURE_PIN_OVERLAY_MODELS";

    [PinOverlayEvidenceFact]
    public void RealOcrAndBothModelsProduceBeforeAfterPairs() => StaTestHost.Run(() =>
    {
        string directory = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
        string? models = Environment.GetEnvironmentVariable(ModelsVariable);
        Assert.False(string.IsNullOrWhiteSpace(models), $"Set {ModelsVariable} to the read-only parent of both model folders.");
        var ocr = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        Assert.True(ocr.IsAvailable, "Windows OCR is unavailable in this execution environment.");
        foreach (bool korean in new[] { false, true })
        {
            string sourceLanguage = korean ? "ko-KR" : "en-US", direction = korean ? "ko-en" : "en-ko";
            // Evidence uses whatever Windows OCR packs this PC has, as the app does; a Korean pack also reads Latin text.
            output.WriteLine($"{direction}: Windows OCR languages {string.Join(", ", ocr.SupportedLanguages)}");
            TranslationModelSpec model = korean ? TranslationModelCatalog.KoreanToEnglish : TranslationModelCatalog.EnglishToKorean;
            string modelDirectory = DiagnosticOutputPaths.Child(models!, model.FolderName);
            foreach (TranslationModelFile file in model.Files)
                Assert.True(File.Exists(DiagnosticOutputPaths.Child(modelDirectory, file.FileName)), "A required offline model file is unavailable.");
            // The store is inert metadata rooted in the selected diagnostic directory. Inference loads
            // directly from the supplied read-only model folder; no profile or model-copy is created.
            using var http = new HttpClient(new RejectingHandler());
            var store = new TranslationModelStore(AppPaths.CreateForRoot(directory), http, NullLogger.Instance,
                files: model.Files, folderName: model.FolderName);
            using var translator = new TextTranslationService(store,
                () => MarianTranslator.Load(modelDirectory, model.ModelFiles, MarianTranslator.DefaultThreads),
                NullLogger.Instance, TimeSpan.FromMinutes(10));
            var service = new PinTranslationService(translator,
                korean ? PinTranslationDirection.KoreanToEnglish : PinTranslationDirection.EnglishToKorean);
            foreach (string scene in new[] { "light-ui", "dark-ui", "table", "columns", "noisy-photo", "dense-settings" })
            {
                BitmapSource before = Draw(scene, korean);
                Save(directory, direction + "-" + scene + "-before.png", before);
                OcrResult recognized = ocr.RecognizeAsync(OcrRequest.FromBitmap(before,
                    preferredLanguages: [sourceLanguage], searchRotatedOrientations: false)).GetAwaiter().GetResult();
                Assert.Equal(OcrStatus.Success, recognized.Status);
                PinTranslationResult result = Assert.IsType<PinTranslationResult>(service.TranslateRecognizedAsync(
                    recognized, PinContent.FromImage(before), CancellationToken.None).GetAwaiter().GetResult());
                Assert.Equal(PinContentKind.Image, result.Content.Kind);
                Assert.NotNull(result.Document);
                Assert.NotSame(before, result.Content.Image);
                Assert.Equal((before.PixelWidth, before.PixelHeight), (result.Content.Image.PixelWidth, result.Content.Image.PixelHeight));
                Save(directory, direction + "-" + scene + "-after.png", result.Content.Image);
                output.WriteLine($"{direction}/{scene}: {recognized.Lines.Count} OCR lines; "
                    + $"{PinTranslationOverlay.BuildBlocks(recognized).Count} blocks; needs review: {result.NeedsReview}; PNG pair saved.");
            }
        }
    });

    private static void Save(string directory, string name, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(file);
    }

    internal static BitmapSource Draw(string scene, bool korean, ICollection<OcrLine>? recognizedLines = null)
    {
        int width = scene == "dense-settings" ? 640 : 1100, height = scene == "dense-settings" ? 520 : 680;
        bool dark = scene == "dark-ui";
        Brush background = dark ? Brush(31, 36, 44) : Brush(247, 248, 250);
        Brush foreground = dark ? Brush(232, 236, 242) : Brush(28, 34, 42);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
            if (scene == "noisy-photo")
            {
                drawing.DrawImage(Noise(width, height), new Rect(0, 0, width, height));
                drawing.DrawEllipse(Brush(179, 192, 158), null, new Point(900, 170), 130, 110);
                drawing.DrawEllipse(Brush(66, 86, 107), null, new Point(960, 500), 100, 120);
                Text(drawing, korean ? "여행의 추억을 사진으로 남깁니다" : "Keep your travel memories in photographs", 34, 42, 110, Brushes.White);
                Text(drawing, korean ? "산과 바다의 풍경을 둘러봅니다" : "Explore the mountains and the coast", 30, 42, 200, Brushes.White);
                Text(drawing, korean ? "좋아하는 장소를 저장합니다" : "Save your favourite places", 30, 42, 290, Brushes.White);
            }
            else if (scene == "table")
            {
                string[,] cells = korean
                    ? new[,] { { "이름", "상태", "수량" }, { "문서", "준비됨", "12" }, { "그림", "저장됨", "24" } }
                    : new[,] { { "Name", "Status", "Count" }, { "Document", "Ready", "12" }, { "Picture", "Saved", "24" } };
                Text(drawing, korean ? "작업 목록" : "Work list", 30, 40, 35, foreground);
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        var rect = new Rect(40 + col * 330, 120 + row * 110, 330, 110);
                        drawing.DrawRectangle(row == 0 ? Brush(225, 232, 240) : Brushes.White, new Pen(Brush(185, 196, 210), 1), rect);
                        Text(drawing, cells[row, col], 30, rect.X + 22, rect.Y + 30, foreground);
                    }
            }
            else if (scene == "columns")
            {
                string[] left = korean
                    ? ["새 문서를 만듭니다.", "작성한 내용을 저장합니다.", "동료와 작업을 검토합니다.", "변경 사항을 확인합니다."]
                    : ["Create a new document.", "Save your work regularly.", "Review the work with your team.", "Check the latest changes."];
                string[] right = korean
                    ? ["그림을 라이브러리에 추가합니다.", "필요한 부분을 선택합니다.", "원본을 안전하게 보관합니다.", "나중에 다시 열 수 있습니다."]
                    : ["Add pictures to your library.", "Select the area you need.", "Keep the original picture safe.", "Open it again later."];
                Text(drawing, korean ? "문서와 그림 관리" : "Working with documents and pictures", 32, 40, 35, foreground);
                for (int row = 0; row < 4; row++)
                {
                    Text(drawing, left[row], 26, 40, 130 + row * 38, foreground);
                    Text(drawing, right[row], 26, 590, 130 + row * 38, foreground);
                }
            }
            else if (scene == "dense-settings")
            {
                Text(drawing, korean ? "캡처 설정" : "Capture settings", 22, 25, 20, foreground);
                string[] labels = korean
                    ? ["파일 이름", "저장 위치", "그림 형식", "클립보드 복사", "마우스 포인터", "알림 표시", "창 테두리", "단축키", "화면 배율", "자동 저장", "원본 보관", "색상 테마"]
                    : ["File name", "Save location", "Image format", "Copy to clipboard", "Mouse pointer", "Show notifications", "Window border", "Keyboard shortcut", "Display scale", "Automatic save", "Keep original", "Colour theme"];
                string[] values = korean
                    ? ["자동", "문서", "원본", "사용", "숨김", "사용", "표시", "기본값", "자동", "사용", "사용", "밝게"]
                    : ["Automatic", "Documents", "Original", "Enabled", "Hidden", "Enabled", "Visible", "Default", "Automatic", "Enabled", "Enabled", "Light"];
                for (int row = 0; row < labels.Length; row++)
                {
                    double y = 72 + row * 32;
                    drawing.DrawLine(new Pen(Brush(224, 228, 234), 1), new Point(25, y + 25), new Point(610, y + 25));
                    Text(drawing, labels[row], 16, 25, y, foreground);
                    Text(drawing, values[row], 16, 380, y, foreground);
                }
            }
            else
            {
                Text(drawing, korean ? "문서 라이브러리" : "Document library", 34, 40, 30, foreground);
                Text(drawing, korean ? "파일     편집     보기" : "File     Edit     View", 26, 40, 100, foreground);
                drawing.DrawRoundedRectangle(dark ? Brush(49, 66, 83) : Brush(222, 233, 246), null, new Rect(40, 180, 340, 70), 6, 6);
                Text(drawing, korean ? "문서 저장" : "Save document", 27, 64, 197, foreground);
                drawing.DrawRoundedRectangle(dark ? Brush(53, 58, 68) : Brushes.White, new Pen(Brush(130, 140, 155), 1), new Rect(420, 180, 340, 70), 6, 6);
                Text(drawing, korean ? "폴더 열기" : "Open folder", 27, 444, 197, foreground);
                drawing.DrawRectangle(dark ? Brush(42, 48, 57) : Brushes.White, new Pen(Brush(130, 140, 155), 1), new Rect(40, 310, 500, 260));
                string[] menu = korean ? ["그림 복사", "선택 영역 번역", "원본 보기", "창 닫기"]
                    : ["Copy picture", "Translate selection", "Show original", "Close window"];
                for (int row = 0; row < menu.Length; row++) Text(drawing, menu[row], 26, 64, 330 + row * 58, foreground);
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;

        void Text(DrawingContext drawing, string text, double size, double x, double y, Brush ink)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                ClipboardTextRenderer.TextTypeface, size, ink, 1);
            drawing.DrawText(formatted, new Point(x, y));
            if (recognizedLines is not null)
            {
                Rect bounds = formatted.BuildGeometry(new Point(x, y)).Bounds;
                var rectangle = new OcrRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                recognizedLines.Add(new OcrLine(text, rectangle, [new OcrWord(text, rectangle)]));
            }
        }
    }

    private static BitmapSource Noise(int width, int height)
    {
        var random = new Random(1741);
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                double shade = 95 + 35 * Math.Sin(x / 90d) + 35 * Math.Cos(y / 70d) + random.Next(-36, 37);
                pixels[offset] = (byte)Math.Clamp(shade + 28, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(shade + 10, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(shade, 0, 255);
                pixels[offset + 3] = 255;
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static Brush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}

public sealed class PinOverlayEvidenceFactAttribute : FactAttribute
{
    public PinOverlayEvidenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)))
            Skip = "Set MYCAPTURE_PIN_OVERLAY_EVIDENCE to generate retained PNG pairs with real OCR and models.";
    }
}
