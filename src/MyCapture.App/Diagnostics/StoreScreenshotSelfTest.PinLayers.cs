using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Diagnostics;

internal static partial class StoreScreenshotSelfTest
{
    internal const string CodePinOriginalText = "#if defined(BOARD_REV_B)\r\n#define PERIPH_CLK_HZ  84000000u\r\n#else\r\n#define PERIPH_CLK_HZ  42000000u\r\n#endif";
    // Dark editor palette without red or violet, so sample art never competes with the product accent.
    private const string PinSampleStringColor = "#D7BA7D";
    private const string PinSamplePreprocessorColor = "#9B9B9B";
    private static readonly Typeface PinSampleMono = new("Consolas");
    private static readonly double[] RegisterColumnWidths = [126, 90, 100, 180];
    private const int RegisterWidth = 496;
    private const int RegisterHeight = 192;
    private const double RegisterRowHeight = 32;

    // The producer fixtures are detached IDataObjects. No clipboard owner is queried or changed.
    internal static DataObject CreateCodePinClipboardFixture()
    {
        var fragment = new StringBuilder("<div style=\"color: #d4d4d4;background-color: #1e1e1e;font-family: Consolas, 'Courier New', monospace;font-weight: normal;font-size: 14px;line-height: 19px;white-space: pre;\">");
        foreach (string line in CodePinOriginalText.Split("\r\n", StringSplitOptions.None))
        {
            fragment.Append("<div>");
            foreach ((string value, string color) in CodeRuns(line))
                fragment.Append("<span style=\"color: ").Append(color).Append(";\">")
                    .Append(WebUtility.HtmlEncode(value)).Append("</span>");
            fragment.Append("</div>");
        }
        fragment.Append("</div>");
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, CodePinOriginalText);
        data.SetData(DataFormats.Html, BuildPinCfHtml(fragment.ToString()));
        return data;
    }

    internal static DataObject CreateTablePinClipboardFixture(bool english)
    {
        string[][] rows = RegisterRows(english);
        var fragment = new StringBuilder("<table>");
        foreach (string[] row in rows)
        {
            fragment.Append("<tr>");
            foreach (string cell in row)
                fragment.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
            fragment.Append("</tr>");
        }
        fragment.Append("</table>");
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(ImageCodec.EncodePng(CreateTablePinPicture(english)), writable: false));
        data.SetData(DataFormats.UnicodeText, GetTablePinOriginalText(english));
        data.SetData(DataFormats.Html, BuildPinCfHtml(fragment.ToString()));
        return data;
    }

    internal static string GetTablePinOriginalText(bool english) =>
        string.Join("\r\n", RegisterRows(english).Select(row => string.Join('\t', row))) + "\r\n";

    internal static BitmapSource CreateTablePinPicture(bool english) =>
        DrawPinFixture(RegisterWidth, RegisterHeight, 3, dc => DrawRegisterTable(dc, english, 0, 0));

    private static string[][] RegisterRows(bool english) =>
    [
        english ? ["Register", "Offset", "Reset", "Description"] : ["레지스터", "오프셋", "리셋 값", "설명"],
        ["UART_SR", "0x00", "0x00C0", english ? "Status" : "상태"],
        ["UART_DR", "0x04", "0x0000", english ? "Data" : "데이터"],
        ["UART_BRR", "0x08", "0x0000", english ? "Baud divisor" : "보율 분주"],
        ["UART_CR1", "0x0C", "0x0000", english ? "Control 1" : "제어 1"],
        ["UART_CR2", "0x10", "0x0000", english ? "Control 2" : "제어 2"],
    ];

    private static string BuildPinCfHtml(string fragment)
    {
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string format = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        int startHtml = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, format, 0, 0, 0, 0));
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        return string.Format(CultureInfo.InvariantCulture, format, startHtml, endHtml, startFragment, endFragment)
               + prefix + fragment + suffix;
    }

    private static IEnumerable<(string Value, string Color)> CodeRuns(string line)
    {
        const string pattern = "//.*|\"[^\"]*\"|#[A-Za-z]+|0x[0-9A-Fa-f]+[uU]?|\\b[0-9]+[uU]?\\b|[A-Za-z_][A-Za-z_0-9]*";
        int position = 0;
        foreach (Match match in Regex.Matches(line, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            if (match.Index > position) yield return (line[position..match.Index], "#D4D4D4");
            string value = match.Value;
            string color = value.StartsWith("//", StringComparison.Ordinal) ? "#6A9955"
                : value.StartsWith('"') ? PinSampleStringColor
                : value.StartsWith('#') || value is "defined" ? PinSamplePreprocessorColor
                : value is "if" or "return" or "static" or "void" or "uint32_t" or "const" ? "#569CD6"
                : char.IsDigit(value[0]) ? "#B5CEA8"
                : value.Any(char.IsLetter) && value.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? "#4FC1FF"
                : line[(match.Index + match.Length)..].TrimStart().StartsWith('(') ? "#DCDCAA"
                : "#D4D4D4";
            yield return (value, color);
            position = match.Index + match.Length;
        }
        if (position < line.Length) yield return (line[position..], "#D4D4D4");
    }

    private static Brush PinBrush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static BitmapSource DrawPinFixture(int width, int height, double scale, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            draw(dc);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        // These are opaque sample pages, including the spreadsheet producer's copy picture.
        // WPF antialiasing can otherwise leave alpha 254 even over an opaque background.
        var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    private static void DrawRegisterTable(DrawingContext dc, bool english, double left, double top)
    {
        string[][] rows = RegisterRows(english);
        var normal = new Typeface(english ? "Segoe UI" : "Malgun Gothic");
        var bold = new Typeface(normal.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        for (int row = 0; row < rows.Length; row++)
        {
            double x = left;
            for (int column = 0; column < RegisterColumnWidths.Length; column++)
            {
                var cell = new Rect(x, top + row * RegisterRowHeight, RegisterColumnWidths[column], RegisterRowHeight);
                dc.DrawRectangle(row == 0 ? PinBrush("#E2EFDA") : Brushes.White, null, cell);
                dc.DrawText(Text(rows[row][column], row == 0 ? bold : normal, 44d / 3, PinBrush("#202020")),
                    new Point(x + 6, cell.Top + 6));
                x += RegisterColumnWidths[column];
            }
        }
        // One display pixel at the picture's 300% rendering scale; outer lines stay inside its bounds.
        var grid = new Pen(PinBrush("#D4D4D4"), 1d / 3);
        double edge = 1d / 6;
        for (int row = 0; row <= rows.Length; row++)
        {
            double y = top + Math.Clamp(row * RegisterRowHeight, edge, RegisterHeight - edge);
            dc.DrawLine(grid, new Point(left, y), new Point(left + RegisterWidth, y));
        }
        double columnX = left;
        for (int column = 0; column <= RegisterColumnWidths.Length; column++)
        {
            double x = Math.Clamp(columnX, left + edge, left + RegisterWidth - edge);
            dc.DrawLine(grid, new Point(x, top), new Point(x, top + RegisterHeight));
            if (column < RegisterColumnWidths.Length) columnX += RegisterColumnWidths[column];
        }
    }

    private sealed partial class LayerExport
    {
        // The numbered request contains 17 mandatory new paths and one optional opacity path.
        private int ExpectedLayerCount => 30;
        private static readonly Rect ApiParameterTable = new(72, 224, 760, 176);
        // The captured region: the endpoint heading, its description and the parameter table.
        private static readonly Rect ApiCaptureRegion = new(56, 92, 792, 324);

        private void ExportPinLayers()
        {
            Scene("UART editor", () => Emit("samples/editor-uart.png", UartEditor(), 1440, 900, scale: 2));
            Scene("paste editor", () => Emit("samples/editor-paste.png", PasteEditor(pasted: true), 1440, 900, scale: 2));
            Scene("before paste editor", () => Emit("film/editor-before-paste.png", PasteEditor(pasted: false), 1440, 900, scale: 2));
            Scene("terminal sample", () => Emit("samples/terminal.png", PinTerminalSample(), 1440, 900, scale: 2));
            Scene("API samples", ExportApiLayers);
            Scene("code pin", ExportCodePinLayers);
            Scene("table pin", ExportTablePinLayers);
            Scene("datasheet OCR pin", ExportDatasheetLayers);
            Scene("spreadsheet sample", () => Emit("film/sheet.png", SheetSample(), 1440, 900, scale: 2));
            Scene("pin tray menu", ExportPinTrayMenu);
        }

        private void AssertPinLayer(bool condition, string description)
        {
            if (!condition) throw new InvalidDataException(description);
            _report.AppendLine("PASS assertion: " + description);
        }

        private static PinContent ReadPinFixture(DataObject data)
        {
            ClipboardImageReader.CapturedAttempt captured = ClipboardImageReader.CaptureFrom(data, 1, () => null);
            // Same decoder and STA renderer as ReadPinAsync; the fallback is the same detached fixture.
            Task<ClipboardImageReader.PinReadAttempt> pending = ClipboardImageReader.CompletePinAttemptAsync(captured,
                bytes => ClipboardImageReader.TryDecodePngPayload(bytes),
                (text, isTabular) => StaThreadTask.RunAsync(() => ClipboardTextRenderer.Render(text, isTabular),
                    "MyCapture layer clipboard text renderer"),
                sequence => Task.FromResult(ClipboardImageReader.CaptureFrom(data, sequence, () => null)));
            while (!pending.IsCompleted) Pump(10);
            return pending.GetAwaiter().GetResult().Content
                   ?? throw new InvalidDataException("The detached clipboard fixture produced no pin content.");
        }

        private PinWindow NewFixturePin(PinContent content, double pixelsPerDip, double opacity = 1)
        {
            var state = new PinViewState(content.Image.PixelWidth / pixelsPerDip, content.Image.PixelHeight / pixelsPerDip, 1, opacity, .1);
            return new PinWindow(content, state, Offscreen, Offscreen, () => NewInstallDefaults.Create().Pin);
        }

        private void EmitPin(string path, PinWindow pin)
        {
            FrameworkElement client = LayerClient(pin);
            Emit(path, RenderLayerWindow(pin), client.ActualWidth, client.ActualHeight, windowOpacity: Math.Round(pin.Opacity, 6));
        }

        private void ExportCodePinLayers()
        {
            PinContent content = ReadPinFixture(CreateCodePinClipboardFixture());
            AssertPinLayer(content.Kind == PinContentKind.Text && content.OriginalText == CodePinOriginalText,
                "Code CF_HTML is a Text pin and retains all five original CRLF lines.");
            byte[] pixel = new byte[4];
            new FormatConvertedBitmap(content.Image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            AssertPinLayer(pixel[0] == 0x1E && pixel[1] == 0x1E && pixel[2] == 0x1E && pixel[3] == 255,
                "Code pin uses the code renderer's opaque #1E1E1E background.");
            var pin = NewFixturePin(content, 1);
            string? copied = null;
            pin.OriginalTextCopyRequested += (_, text) => copied = text;
            try
            {
                OpenLayerWindow(pin, content.Image.PixelWidth, content.Image.PixelHeight, 150);
                pin.SetChromeActiveForTest(true);
                pin.SimulateCtrlDoubleClickForTest();
                AssertPinLayer(copied == CodePinOriginalText, "Code Ctrl+double-click delivers the exact retained original text to the recording delegate.");
                pin.ReportOriginalTextCopyResult(true);
                AssertPinLayer(pin.FeedbackForTest.Text == UiText.Get("Text_5C9B3C62EC41"), "Code pin shows the product text-copy confirmation.");
                EmitPin("pins/code-define.png", pin);
                pin.ForceFeedbackTimeoutForTest();
                pin.SetChromeActiveForTest(false);
                EmitPin("film/code-plain.png", pin);
            }
            finally { CloseLayerWindow(pin); }
        }

        private void ExportTablePinLayers()
        {
            PinContent content = ReadPinFixture(CreateTablePinClipboardFixture(_english));
            AssertPinLayer(content.Kind == PinContentKind.Table && content.OriginalText == GetTablePinOriginalText(_english),
                "Spreadsheet fixture is a Table pin and preserves TSV including the trailing CRLF.");
            AssertPinLayer(SamePinPixels(content.Image, CreateTablePinPicture(_english)),
                "Table pin pixels equal the producer PNG, including fills, fonts and grid; no fallback grid renderer.");
            var pin = NewFixturePin(content, 3, .88);
            try
            {
                OpenLayerWindow(pin, RegisterWidth, RegisterHeight, 150);
                pin.SetChromeActiveForTest(false);
                EmitPin("pins/table.png", pin);
                pin.Opacity = .72;
                pin.ApplyClickThrough(true);
                // The recovery hint follows the new-install keys: the toggle key, the hide-all key twice, or the tray menu.
                HotkeySettings keys = NewInstallDefaults.Create().Hotkeys;
                string expectedHint = keys.ToggleClickThrough.IsAssigned ? keys.ToggleClickThrough.ToString()
                    : keys.HideAllPins.IsAssigned ? UiText.Format("Hotkey.Twice", keys.HideAllPins)
                    : UiText.Get("Hotkey.TrayRestore");
                AssertPinLayer(PinWindow.ClickThroughRecoveryHint() == expectedHint,
                    "Click-through recovery hint reflects the new-install keys: " + expectedHint);
                AssertPinLayer(pin.FeedbackForTest.Text == UiText.Format("Onboard_PinClickThroughOn", PinWindow.ClickThroughRecoveryHint()),
                    "Click-through pin shows the product recovery feedback.");
                EmitPin("pins/table-clickthrough.png", pin);
                pin.ApplyClickThrough(false);
                pin.ForceFeedbackTimeoutForTest();
                pin.Opacity = 1;
                EmitPin("film/table-plain.png", pin);

                // The pin's own right-click menu, with a closed pin available to reopen.
                pin.CanRestoreClosedPin = () => true;
                pin.RefreshRestoreClosedMenuItem();
                AssertPinLayer(pin.ContextMenu.Items.OfType<MenuItem>()
                        .Any(item => Equals(item.Header, UiText.Get("Onboard_PinAllowClickThrough"))),
                    "Pin context menu offers click-through to windows behind.");
                EmitDetachedMenu("menus/pin-context.png", pin.ContextMenu);

                // Invoke the real context-menu opacity command, without opening a popup or reflecting input handlers.
                // The state starts at .88, so first use the opaque command to clamp it to 1.
                MenuItem moreOpaque = pin.ContextMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, UiText.Get("Onboard_PinMoreOpaque")));
                moreOpaque.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                moreOpaque.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                MenuItem moreTransparent = pin.ContextMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, UiText.Get("Onboard_PinMoreTransparent")));
                for (int index = 0; index < 4; index++) moreTransparent.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                AssertPinLayer(Math.Abs(pin.Opacity - .6) < .00001
                               && pin.FeedbackForTest.Text == UiText.Format("Text_C740003A2150", 60d),
                    "Real opacity menu commands produce 60% and the product opacity feedback.");
                EmitPin("film/table-opacity.png", pin);
            }
            finally { CloseLayerWindow(pin); }
        }

        private static bool SamePinPixels(BitmapSource left, BitmapSource right)
        {
            if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight) return false;
            int stride = left.PixelWidth * 4;
            byte[] a = new byte[stride * left.PixelHeight], b = new byte[a.Length];
            new FormatConvertedBitmap(left, PixelFormats.Bgra32, null, 0).CopyPixels(a, stride, 0);
            new FormatConvertedBitmap(right, PixelFormats.Bgra32, null, 0).CopyPixels(b, stride, 0);
            return a.AsSpan().SequenceEqual(b);
        }

        private void ExportDatasheetLayers()
        {
            var lines = new List<OcrLine>();
            BitmapSource image = DatasheetSample(lines);
            string expected = string.Join("\r\n", lines.Select(line => line.Text));
            OcrResult result = OcrResult.Success(expected, "en", lines, TimeSpan.Zero);
            var pin = NewFixturePin(PinContent.FromImage(image), 3);
            string? copiedText = null;
            Task<bool>? copy = null;
            async Task<bool> CopyMeasuredOcr(BitmapSource source)
            {
                bool copied = await PinTextCopyService.CopyAsync(source, new LayerOcr(result), text =>
                {
                    copiedText = text;
                    return Task.FromResult(true);
                }, new OcrSettings());
                pin.ReportOriginalTextCopyResult(copied);
                return copied;
            }
            pin.OcrRequested += (_, source) => copy = CopyMeasuredOcr(source);
            try
            {
                OpenLayerWindow(pin, 520, 210, 150);
                pin.SetChromeActiveForTest(true);
                pin.SimulateCtrlDoubleClickForTest();
                if (copy is null) throw new InvalidDataException("The datasheet image did not request OCR.");
                while (!copy.IsCompleted) Pump(10);
                bool copied = copy.GetAwaiter().GetResult();
                AssertPinLayer(copied && copiedText == expected, "Datasheet Ctrl+double-click uses PinTextCopyService and copies the measured fixture OCR text.");
                AssertPinLayer(pin.FeedbackForTest.Text == UiText.Get("Text_5C9B3C62EC41"), "Datasheet pin shows the product text-copy confirmation.");
                EmitPin("pins/datasheet.png", pin);
            }
            finally { CloseLayerWindow(pin); }

            // The editor comment is built only from what the copy delegate actually received.
            string[] body = copiedText!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            string[] editor = ["// usart_timing.h", "", "/*", .. body, "*/", "", "#define USART_FCK_MAX_HZ  84000000u"];
            Emit("samples/editor-ocr.png", DrawEditor("usart_timing.h", "uart_init.c", editor, 3 + body.Length, 2), 1440, 900, scale: 2);
            _report.AppendLine("Datasheet OCR fixture (copied text): " + copiedText.Replace("\r\n", " | ", StringComparison.Ordinal));
        }

        private static BitmapSource DatasheetSample(List<OcrLine> lines) => DrawPinFixture(520, 210, 3, dc =>
        {
            Brush ink = PinBrush("#20252B");
            FormattedText title = Text("Table 12. USART characteristics", new Typeface("Segoe UI Semibold"), 17, ink);
            dc.DrawText(title, new Point(24, 17));
            var titleBounds = new OcrRect(72, 51, Math.Ceiling(title.WidthIncludingTrailingWhitespace * 3), Math.Ceiling(title.Height * 3));
            lines.Add(new OcrLine("Table 12. USART characteristics", titleBounds, [new OcrWord("Table 12. USART characteristics", titleBounds)]));
            string[][] rows =
            [
                ["Symbol", "Parameter", "Min", "Max", "Unit"],
                ["fCK", "USART clock frequency", "—", "84", "MHz"],
                ["tSU", "Data setup time", "20", "—", "ns"],
                ["tH", "Data hold time", "10", "—", "ns"],
                ["BRR", "Divider at 115200 bit/s", "0x02D9", "—", "—"],
            ];
            double[] columns = [52, 228, 70, 62, 60];
            for (int row = 0; row < rows.Length; row++)
            {
                double y = 53 + row * 28, x = 24;
                dc.DrawRectangle(row == 0 ? PinBrush("#EEF0F2") : Brushes.White, null, new Rect(24, y, 472, 28));
                var words = new List<OcrWord>();
                double bottom = y, right = x;
                for (int column = 0; column < columns.Length; column++)
                {
                    FormattedText text = Text(rows[row][column], new Typeface(row == 0 ? "Segoe UI Semibold" : "Segoe UI"), 11.5, ink);
                    dc.DrawText(text, new Point(x + 5, y + 6));
                    var bounds = new OcrRect((x + 5) * 3, (y + 6) * 3,
                        Math.Ceiling(text.WidthIncludingTrailingWhitespace * 3), Math.Ceiling(text.Height * 3));
                    words.Add(new OcrWord(rows[row][column], bounds));
                    right = x + 5 + text.WidthIncludingTrailingWhitespace;
                    bottom = Math.Max(bottom, y + 6 + text.Height);
                    x += columns[column];
                }
                dc.DrawLine(new Pen(PinBrush("#BAC1C8"), .6), new Point(24, y + 28), new Point(496, y + 28));
                lines.Add(new OcrLine(string.Join("  ", rows[row]), new OcrRect(29 * 3, (y + 6) * 3,
                    Math.Ceiling((right - 29) * 3), Math.Ceiling((bottom - y - 6) * 3)), words));
            }
        });

        private BitmapSource UartEditor()
        {
            string[] lines =
            [
                _english ? "// uart_init.c — spec v1.3, table 4-2" : "// uart_init.c — 사양서 v1.3 표 4-2",
                "#include \"board.h\"", "",
                "#define UART_BASE      0x40011000u", "#define UART_SR        (UART_BASE + 0x00)",
                "#define UART_DR        (UART_BASE + 0x04)", "#define UART_BRR       (UART_BASE + 0x08)",
                "#define UART_CR1       (UART_BASE + 0x0C)", "",
                "#define CR1_UE         (1u << 13)", "#define CR1_TE         (1u << 3)", "#define CR1_RE         (1u << 2)", "",
                "static void uart_init(uint32_t baud)", "{", "    reg_write(UART_CR1, 0);",
                "    if (baud == 115200) {", "        reg_write(UART_BRR, 0x02D9);", "    }",
                "    reg_write(UART_CR1, CR1_UE | CR1_TE | CR1_RE);", "}",
            ];
            return DrawEditor("uart_init.c", "board.h", lines, 7, lines[7].Length);
        }

        private static BitmapSource PasteEditor(bool pasted)
        {
            string[] tail = ["static void spi_init(void)", "{", "    spi_set_clock(PERIPH_CLK_HZ / 4);", "}"];
            string[] lines = pasted
                ? ["// spi_init.c", "#include \"board.h\"", "", .. CodePinOriginalText.Split("\r\n", StringSplitOptions.None), "", .. tail]
                : ["// spi_init.c", "#include \"board.h\"", "", "", .. tail];
            return DrawEditor("spi_init.c", "uart_init.c", lines, pasted ? 8 : 3, 0);
        }

        private static BitmapSource DrawEditor(string active, string inactive, string[] lines, int current, int caretColumn) =>
            DrawPinFixture(1440, 900, 2, dc =>
            {
                dc.DrawRectangle(PinBrush("#1E1E1E"), null, new Rect(0, 0, 1440, 900));
                dc.DrawRectangle(PinBrush("#252526"), null, new Rect(0, 0, 1440, 36));
                dc.DrawRectangle(PinBrush("#1E1E1E"), null, new Rect(0, 0, 198, 36));
                dc.DrawText(Text(active, new Typeface("Segoe UI"), 13, PinBrush("#D4D4D4")), new Point(22, 9));
                dc.DrawText(Text(inactive, new Typeface("Segoe UI"), 13, PinBrush("#969696")), new Point(220, 9));
                dc.DrawRectangle(PinBrush("#2A2D2E"), null, new Rect(56, 56 + current * 22, 1384, 22));
                bool comment = false;
                for (int index = 0; index < lines.Length; index++)
                {
                    double y = 56 + index * 22;
                    FormattedText number = Text((index + 1).ToString(CultureInfo.InvariantCulture), PinSampleMono, 15,
                        PinBrush(index == current ? "#C6C6C6" : "#858585"));
                    dc.DrawText(number, new Point(46 - number.WidthIncludingTrailingWhitespace, y));
                    if (lines[index].StartsWith("/*", StringComparison.Ordinal)) comment = true;
                    double x = 80;
                    foreach ((string value, string color) in CodeRuns(lines[index]))
                    {
                        FormattedText run = Text(value, PinSampleMono, 15, PinBrush(comment ? "#6A9955" : color));
                        dc.DrawText(run, new Point(x, y));
                        x += run.WidthIncludingTrailingWhitespace;
                    }
                    if (lines[index].Contains("*/", StringComparison.Ordinal)) comment = false;
                }
                string beforeCaret = lines[current][..Math.Min(caretColumn, lines[current].Length)];
                double caretX = 80 + (beforeCaret.Length == 0 ? 0 : Text(beforeCaret, PinSampleMono, 15, Brushes.White).WidthIncludingTrailingWhitespace);
                dc.DrawRectangle(PinBrush("#AEAFAD"), null, new Rect(caretX, 57 + current * 22, 2, 19));
            });

        private BitmapSource PinTerminalSample() => DrawPinFixture(1440, 900, 2, dc =>
        {
            dc.DrawRectangle(PinBrush("#0C0C0C"), null, new Rect(0, 0, 1440, 900));
            dc.DrawRectangle(PinBrush("#1F1F1F"), null, new Rect(0, 0, 1440, 36));
            dc.DrawText(Text(_english ? "Terminal" : "터미널", new Typeface("Segoe UI"), 13, PinBrush("#CCCCCC")), new Point(20, 9));
            string[] lines =
            [
                "PS C:\\work\\fw> .\\build.ps1", "[build] uart_init.c", "[build] spi_init.c", "[link]  fw.elf  48.2 KB",
                _english ? "Build succeeded." : "빌드 성공.", "PS C:\\work\\fw> .\\flash.ps1 -Port COM5 -Baud 115200",
            ];
            for (int row = 0; row < lines.Length; row++)
                dc.DrawText(Text(lines[row], PinSampleMono, 15, PinBrush("#CCCCCC")), new Point(24, 58 + row * 22));
            double x = 24 + Text(lines[^1], PinSampleMono, 15, Brushes.White).WidthIncludingTrailingWhitespace;
            dc.DrawRectangle(PinBrush("#CCCCCC"), null, new Rect(x, 58 + (lines.Length - 1) * 22, 9, 19));
        });

        private BitmapSource SheetSample() => DrawPinFixture(1440, 900, 2, dc =>
        {
            dc.DrawRectangle(PinBrush("#F3F3F3"), null, new Rect(0, 0, 1440, 64));
            dc.DrawText(Text(_english ? "Spec v1.3" : "사양서 v1.3", UiBold, 18, PinBrush("#202020")), new Point(22, 18));
            dc.DrawRectangle(Brushes.White, new Pen(PinBrush("#D4D4D4"), 1), new Rect(258, 14, 110, 34));
            dc.DrawText(Text("A1:D6", new Typeface("Segoe UI"), 14, PinBrush("#202020")), new Point(272, 21));
            double[] sheetColumns = [.. RegisterColumnWidths, 180, 180, 180, 180];
            double x = 56;
            for (int column = 0; column < sheetColumns.Length; column++)
            {
                dc.DrawRectangle(PinBrush("#F3F3F3"), new Pen(PinBrush("#D4D4D4"), 1), new Rect(x, 64, sheetColumns[column], 32));
                FormattedText header = Text(((char)('A' + column)).ToString(), new Typeface("Segoe UI"), 14, PinBrush("#505050"));
                dc.DrawText(header, new Point(x + (sheetColumns[column] - header.Width) / 2, 71));
                x += sheetColumns[column];
            }
            for (int row = 0; row < 14; row++)
            {
                double y = 96 + row * RegisterRowHeight;
                dc.DrawRectangle(PinBrush("#F3F3F3"), new Pen(PinBrush("#D4D4D4"), 1), new Rect(0, y, 56, RegisterRowHeight));
                FormattedText number = Text((row + 1).ToString(CultureInfo.InvariantCulture), new Typeface("Segoe UI"), 14, PinBrush("#505050"));
                dc.DrawText(number, new Point(40 - number.Width, y + 7));
                x = 56;
                foreach (double width in sheetColumns)
                {
                    dc.DrawRectangle(Brushes.White, new Pen(PinBrush("#D4D4D4"), 1), new Rect(x, y, width, RegisterRowHeight));
                    x += width;
                }
            }
            DrawRegisterTable(dc, _english, 56, 96);
            dc.DrawRectangle(null, new Pen(PinBrush("#2B6CB0"), 2) { DashStyle = DashStyles.Dash }, new Rect(56, 96, RegisterWidth, RegisterHeight));
        });

        private void DrawApiPage(DrawingContext dc)
        {
            Brush ink = PinBrush("#24292F");
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1440, 900));
            dc.DrawRectangle(PinBrush("#F6F8FA"), null, new Rect(0, 0, 1440, 64));
            dc.DrawText(Text(_english ? "API reference" : "API 참조", UiBold, 18, ink), new Point(72, 20));
            dc.DrawText(Text("POST /v1/devices/{id}/config", new Typeface("Segoe UI Semibold"), 26, ink), new Point(72, 106));
            dc.DrawText(Text(_english ? "Changes the device's serial port settings." : "장치의 직렬 포트 설정을 바꿉니다.", Ui, 17, ink), new Point(72, 160));
            string[][] rows =
            [
                _english ? ["Name", "Type", "Description"] : ["이름", "형식", "설명"],
                ["baud_rate", "integer", "9600–921600"], ["parity", "string", "none, even, odd"], ["stop_bits", "integer", "1, 2"],
            ];
            double[] widths = [230, 180, 350];
            for (int row = 0; row < rows.Length; row++)
            {
                double x = ApiParameterTable.X, y = ApiParameterTable.Y + row * 44;
                for (int column = 0; column < widths.Length; column++)
                {
                    dc.DrawRectangle(row == 0 ? PinBrush("#F6F8FA") : Brushes.White,
                        new Pen(PinBrush("#D0D7DE"), 1), new Rect(x + .5, y + .5, widths[column] - 1, 43));
                    dc.DrawText(Text(rows[row][column], row == 0 ? UiBold : Ui, 16, ink), new Point(x + 14, y + 12));
                    x += widths[column];
                }
            }
            dc.DrawRectangle(PinBrush("#1E1E1E"), null, new Rect(72, 440, 1040, 108));
            dc.DrawText(Text("{\"baud_rate\": 115200, \"parity\": \"none\", \"stop_bits\": 1}", PinSampleMono, 17, PinBrush("#D4D4D4")), new Point(96, 478));
        }

        private BitmapSource ApiCapture(double scale) => DrawPinFixture((int)ApiCaptureRegion.Width, (int)ApiCaptureRegion.Height, scale, dc =>
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ApiCaptureRegion.Width, ApiCaptureRegion.Height)));
            dc.PushTransform(new TranslateTransform(-ApiCaptureRegion.X, -ApiCaptureRegion.Y));
            DrawApiPage(dc);
            dc.Pop();
            dc.Pop();
        });

        private void ExportApiLayers()
        {
            Emit("samples/api-doc.png", DrawPinFixture(1440, 900, 2, DrawApiPage), 1440, 900, scale: 2);
            _report.AppendLine($"API capture DIP rectangle: x={ApiCaptureRegion.X}, y={ApiCaptureRegion.Y}, width={ApiCaptureRegion.Width}, height={ApiCaptureRegion.Height}; shared drawing for page, card and pin.");
            var area = new RectD(Offscreen, Offscreen, 1920, 1080);
            var monitor = new MonitorInfo("StorePinLayersFixture", area, area, 96, false);
            var card = new QuickAccessWindow(ApiCapture(1), () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                () => Task.FromResult(true), () => { }, () => Task.FromResult("api-reference.png"), targetMonitor: monitor) { IsKept = true };
            try
            {
                OpenLayerWindow(card, 344, 215, 150);
                Emit("quick-access/api-doc.png", RenderLayerWindow(card), 344, 215);
            }
            finally { CloseLayerWindow(card); }
            PinContent content = PinContent.FromImage(ApiCapture(2));
            var pin = NewFixturePin(content, 2);
            try
            {
                OpenLayerWindow(pin, ApiCaptureRegion.Width, ApiCaptureRegion.Height, 150);
                pin.SetChromeActiveForTest(false);
                EmitPin("pins/api-doc.png", pin);
            }
            finally { CloseLayerWindow(pin); }
        }

        private void ExportPinTrayMenu()
        {
            static void None() { }
            var commands = new TrayMenuCommands(None, None, None, None, None, None, None, None, None, None,
                None, None, None, PinClipboard: None, HideOrShowAllPins: None, ClearClickThrough: None, ContactSupport: None);
            var state = new TrayMenuState(PinCount: 2, PinsHidden: false, ClickThroughPinCount: 1,
                CanRestoreClosedPin: true, ClipboardHasContent: true);
            IReadOnlyList<ThemedShellPresenter.MenuEntry> entries = TrayMenuLayout.Build(commands, NewInstallDefaults.Create().Hotkeys, state);
            ContextMenu menu = new ThemedShellPresenter(() => true).CreateMenu(entries);
            string[] required = ["Tray_PinClipboard", "Tray_HideAllPins", "Tray_ClearClickThrough", "Onboard_TrayReopenPin"];
            string[] actual = menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray();
            int previous = -1;
            foreach (string key in required)
            {
                int index = Array.IndexOf(actual, UiText.Get(key));
                AssertPinLayer(index > previous, "Tray menu ordered label: " + key);
                previous = index;
            }
            EmitDetachedMenu("menus/tray-pins.png", menu);

            // The same menu while the pins are hidden: the command reads Show all pins.
            var hidden = state with { PinsHidden = true };
            ContextMenu hiddenMenu = new ThemedShellPresenter(() => true).CreateMenu(
                TrayMenuLayout.Build(commands, NewInstallDefaults.Create().Hotkeys, hidden));
            AssertPinLayer(hiddenMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, UiText.Get("Tray_ShowAllPins"))),
                "Hidden-pins tray menu offers Show all pins.");
            EmitDetachedMenu("film/tray-pins-hidden.png", hiddenMenu);
        }

        private void EmitDetachedMenu(string path, ContextMenu menu)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                menu.Arrange(new Rect(menu.DesiredSize));
                menu.UpdateLayout();
                if (pass == 0) Pump(20);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Round(menu.ActualWidth * Scale), (int)Math.Round(menu.ActualHeight * Scale),
                96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            bitmap.Freeze();
            Emit(path, bitmap, menu.ActualWidth, menu.ActualHeight);
            _report.AppendLine($"Menu {path} logical size: {menu.ActualWidth:0.###}x{menu.ActualHeight:0.###} DIP; detached, measured and arranged twice; transparent outside menu.");
        }

        private void ExportRedactionAnnotated(LayerEditor window)
        {
            var editor = (AnnotationEditorControl)window.Content;
            var controller = (AnnotationEditorController)(typeof(AnnotationEditorControl)
                .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor)
                ?? throw new MissingFieldException(nameof(AnnotationEditorControl), "_controller"));
            AnnotationItem[] original = controller.Document.Items.ToArray();
            RectD[] covers = original.Select(item => item.Bounds).ToArray();
            controller.DefaultFontSize = 24;
            controller.StrokeThickness = 5;
            controller.StrokeColor = NewInstallDefaults.Create().Annotation.StrokeColor;
            // Step 1 follows the first HTTP 500 line and step 2 the failed retry; the note points at the retry.
            controller.AddStepNumber(new PointD(700, 546));
            controller.AddStepNumber(new PointD(680, 650));
            controller.Tool = EditorTool.Arrow;
            controller.PointerDown(new PointD(990, 622));
            controller.PointerMove(new PointD(745, 664));
            controller.PointerUp(new PointD(745, 664));
            TextAnnotation text = controller.BeginTextAnnotation(new PointD(1000, 590), 300, 40);
            controller.ApplyTextBold(true);
            controller.CommitTextEdit(text, _english ? "Fails after retry" : "재시도 후 실패");
            controller.SetSelected(null);
            AnnotationItem[] added = controller.Document.Items.Except(original).ToArray();
            AssertPinLayer(added.Length == 4 && added.OfType<TextAnnotation>().Count(item => item.IsStepNumber) == 2
                           && added.OfType<PolylineAnnotation>().Count() == 1 && text.Bold,
                "Shared redaction editor receives two step numbers, one arrow and bold text through its real controller.");
            AssertPinLayer(added.All(item => item.Bounds.Top >= 520 && item.Bounds.Bottom <= 732)
                           && covers.Select((rect, index) => rect == original[index].Bounds).All(value => value),
                "Share annotations stay inside the log box and preserve all original redaction cover bounds.");
            ClearLayerSelection(editor);
            Pump(100);
            Emit("share/redact-annotated.png", RenderLayerWindow(window), 1240, 780);
        }
    }
}
