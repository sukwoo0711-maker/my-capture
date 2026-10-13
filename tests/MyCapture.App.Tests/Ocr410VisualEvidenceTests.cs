using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ocr410VisualEvidenceTests
{
    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void Baseline277d904_RendersSameIsolatedTableFixture(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var (image, result) = Ocr410Fixture.Table();
        var window = new Ocr410BaselineWindow { ShowInTaskbar = false, Width = 1040, Height = 720 };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            window.ShowResult(result, "MyCapture OCR");
            window.UpdateLayout();
            Ocr410Fixture.Settle();
            var screenshot = new RenderTargetBitmap(1040, 720, 96, 96, PixelFormats.Pbgra32);
            screenshot.Render(window);
            string? root = Environment.GetEnvironmentVariable("MYCAPTURE_OCR410_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(root))
            {
                Directory.CreateDirectory(root);
                Ocr410Fixture.Save(screenshot, Path.Combine(root, $"baseline-{language}.png"));
            }
        }
        finally { window.CloseForExit(); }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void ResultWindow_RendersIsolatedTableFixture(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { ShowInTaskbar = false, Width = 1040, Height = 720 };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            window.ShowResult(result, "MyCapture OCR");
            // The baseline has no source preview. The same fixture exercises the added surface.
            typeof(OcrResultWindow).GetMethod("SetSourceImage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(window, [image]);
            var selectMode = typeof(OcrResultWindow).GetMethod("SelectExtractionMode",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (selectMode is not null)
                selectMode.Invoke(window, [Enum.ToObject(selectMode.GetParameters()[0].ParameterType, 0)]);
            window.UpdateLayout();
            Ocr410Fixture.Settle();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            Assert.True(bitmap.PixelWidth >= 600);
            string? evidenceRoot = Environment.GetEnvironmentVariable("MYCAPTURE_OCR410_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                Directory.CreateDirectory(evidenceRoot);
                Ocr410Fixture.Save(bitmap, Path.Combine(evidenceRoot, $"table-{language}.png"));
                Ocr410Fixture.Save(image, Path.Combine(evidenceRoot, $"original-{language}.png"));
            }
        }
        finally { window.CloseForExit(); }
    });
}

internal static class Ocr410Fixture
{
    internal static (BitmapSource Image, OcrResult Result) Table()
    {
        string[][] values =
        [
            ["항목", "수량", "금액"],
            ["키보드", "2", "128,400"],
            ["USB 케이블", "12", "9,900"],
            ["모니터", "1", "1,204,000"],
        ];
        var visual = new DrawingVisual();
        var lines = new List<OcrLine>();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 560, 300));
            for (int row = 0; row < values.Length; row++)
            {
                var words = new List<OcrWord>();
                for (int column = 0; column < 3; column++)
                {
                    string value = values[row][column];
                    var text = new FormattedText(value, CultureInfo.GetCultureInfo("ko-KR"),
                        FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 22, Brushes.Black, 1);
                    double x = column switch { 0 => 32, 1 => 280 - text.Width, _ => 520 - text.Width };
                    double y = 35 + row * 60;
                    drawing.DrawText(text, new Point(x, y));
                    words.Add(new OcrWord(value, new OcrRect(x, y, text.Width, text.Height)));
                }
                drawing.DrawLine(new Pen(Brushes.LightGray, 1), new Point(25, 82 + row * 60),
                    new Point(535, 82 + row * 60));
                lines.Add(new OcrLine(string.Join(" ", values[row]),
                    words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words));
            }
        }
        var image = new RenderTargetBitmap(560, 300, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return (image, OcrResult.Success(string.Join("\n", lines.Select(line => line.Text)),
            "ko-KR", lines, TimeSpan.FromMilliseconds(120)));
    }

    internal static void LoadTheme(Window window)
    {
        XElement? combined = null;
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(Ocr410Fixture).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            combined ??= new XElement(dictionary.Name);
            foreach (XAttribute declaration in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                    && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(
            combined!.ToString(SaveOptions.DisableFormatting)));
    }

    internal static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    internal static void Settle()
    {
        // Capture the real final theme state after the window's opening animation.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("The isolated OCR fixture did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
