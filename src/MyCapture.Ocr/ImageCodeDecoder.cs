using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace MyCapture.Ocr;

/// <summary>Managed ZXing core only. Each call owns its pixels and readers; nothing caches images.</summary>
internal static class ImageCodeDecoder
{
    static ImageCodeDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    internal const int MaximumSide = 1600;
    internal static readonly BarcodeFormat[] Formats = [BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC, BarcodeFormat.PDF_417, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
        BarcodeFormat.UPC_A, BarcodeFormat.CODE_128];
    // ZXing accepts a 1-D symbol from one scan row, which misfires on text-heavy UI screenshots
    // (EAN-8 once read 59142362 from G01.clipboard.en.after.png). Every 1-D read must therefore be
    // confirmed on two further scan lines across the bars; see Confirmed.
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Korean = CodePagesEncodingProvider.Instance.GetEncoding(949,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    internal static IReadOnlyList<DecodedImageCode> Decode(BitmapSource image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int originalWidth = image.PixelWidth, originalHeight = image.PixelHeight;
        double scale = Math.Min(1, (double)MaximumSide / Math.Max(originalWidth, originalHeight));
        int width = Math.Max(1, (int)Math.Round(originalWidth * scale));
        int height = Math.Max(1, (int)Math.Round(originalHeight * scale));
        // Sample individual rows, never a second full-size BGRA image. Nearest samples keep modules sharp.
        BitmapSource source = image.Format == PixelFormats.Bgra32 ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] row = new byte[checked(originalWidth * 4)];
        byte[] luminance = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int sy = Math.Min(originalHeight - 1, (int)((y + .5) * originalHeight / height));
            source.CopyPixels(new Int32Rect(0, sy, originalWidth, 1), row, row.Length, 0);
            for (int x = 0; x < width; x++)
            {
                int index = Math.Min(originalWidth - 1, (int)((x + .5) * originalWidth / width)) * 4;
                int gray = (row[index + 2] * 306 + row[index + 1] * 601 + row[index] * 117 + 512) >> 10;
                luminance[y * width + x] = (byte)((gray * row[index + 3] + 255 * (255 - row[index + 3]) + 127) / 255);
            }
        }
        token.ThrowIfCancellationRequested();
        var reader = new MultiFormatReader();
        var otherHints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.POSSIBLE_FORMATS] = Formats.Where(format => format != BarcodeFormat.QR_CODE).ToList(),
            [DecodeHintType.CHARACTER_SET] = "ISO-8859-1",
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.TRY_HARDER_WITHOUT_ROTATION] = true,
        };
        var sourcePixels = new DeadlineLuminance(luminance, width, height, token);
        var verticalReader = new ZXing.OneD.MultiFormatOneDReader(otherHints);
        var found = new List<Result>();
        var qr = new ZXing.Multi.QrCode.QRCodeMultiReader();
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "ISO-8859-1",
        };
        var wholeImage = new Int32Rect(0, 0, width, height);
        foreach (bool inverted in new[] { false, true })
        {
            token.ThrowIfCancellationRequested();
            LuminanceSource polarity = inverted ? sourcePixels.invert() : sourcePixels;
            // The QR multi detector preserves equal payloads at separate locations. The generic
            // recursive multi reader deduplicates by text and would silently lose those symbols.
            var binary = new BinaryBitmap(new HybridBinarizer(polarity));
            found.AddRange(qr.decodeMultiple(binary, hints) ?? []);
            token.ThrowIfCancellationRequested();
            Result? other = reader.decode(binary, otherHints);
            if (other is not null && !Confirmed(other, luminance, width, height, inverted, wholeImage, otherHints, token))
                other = null;
            if (other is null)
            {
                // 2-D detectors resolve orientation themselves and 1-D readers scan both
                // directions. Only the perpendicular scan is needed, not four full copies.
                token.ThrowIfCancellationRequested();
                var rotated = new BinaryBitmap(new HybridBinarizer(polarity.rotateCounterClockwise()));
                Result? vertical = verticalReader.decode(rotated, otherHints);
                if (vertical is not null)
                {
                    other = new Result(vertical.Text, vertical.RawBytes, vertical.ResultPoints.Select(point =>
                        new ResultPoint(width - point.Y - 1, point.X)).ToArray(), vertical.BarcodeFormat);
                    if (vertical.ResultMetadata is { } metadata) other.putAllMetadata(metadata);
                    if (!Confirmed(other, luminance, width, height, inverted, wholeImage, otherHints, token)) other = null;
                }
            }
            if (other is not null) found.Add(other);
            // Data Matrix/Aztec's white-rectangle search starts near the image centre.
            // Find bounded, connected ink tiles for off-centre symbols; only small crops are
            // materialised, from this same bounded luminance image, never from the screen.
            foreach (Int32Rect region in CandidateRegions(binary.BlackMatrix, token))
            {
                token.ThrowIfCancellationRequested();
                Result? candidate = reader.decode(binary.crop(region.X, region.Y, region.Width, region.Height), otherHints);
                if (candidate is null) continue;
                var mapped = new Result(candidate.Text, candidate.RawBytes, candidate.ResultPoints.Select(point =>
                    new ResultPoint(point.X + region.X, point.Y + region.Y)).ToArray(), candidate.BarcodeFormat);
                if (candidate.ResultMetadata is { } metadata) mapped.putAllMetadata(metadata);
                if (!Confirmed(mapped, luminance, width, height, inverted, region, otherHints, token)) continue;
                found.Add(mapped);
            }
        }
        token.ThrowIfCancellationRequested();
        var codes = new List<DecodedImageCode>();
        foreach (Result result in found)
        {
            string text = Payload(result);
            if (text.Length == 0) continue;
            OcrRect bounds = Bounds(result, luminance, width, height);
            if (!OcrGeometry.IsValid(bounds)) continue;
            bounds = new(bounds.X * originalWidth / width, bounds.Y * originalHeight / height,
                bounds.Width * originalWidth / width, bounds.Height * originalHeight / height);
            // Keep equal payloads at different locations; suppress repeated reads of the same symbol.
            if (codes.Any(code => code.Text == text && Near(code.Bounds, bounds))) continue;
            codes.Add(new(text, bounds, result.BarcodeFormat is BarcodeFormat.QR_CODE
                or BarcodeFormat.DATA_MATRIX or BarcodeFormat.AZTEC or BarcodeFormat.PDF_417));
        }
        return codes;
    }

    private static bool Near(OcrRect a, OcrRect b) => Math.Abs(a.X - b.X) < Math.Max(4, a.Width * .2)
        && Math.Abs(a.Y - b.Y) < Math.Max(4, a.Height * .2);

    private static IEnumerable<Int32Rect> CandidateRegions(BitMatrix matrix, CancellationToken token)
    {
        const int tile = 24;
        int columns = (matrix.Width + tile - 1) / tile, rows = (matrix.Height + tile - 1) / tile;
        var occupied = new bool[columns * rows];
        for (int y = 0; y < matrix.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) occupied[y / tile * columns + x / tile] = true;
        }
        int[] queue = new int[occupied.Length];
        int emitted = 0;
        for (int start = 0; start < occupied.Length && emitted < 16; start++)
        {
            if (!occupied[start]) continue;
            int head = 0, tail = 0, left = columns, top = rows, right = 0, bottom = 0;
            queue[tail++] = start; occupied[start] = false;
            while (head < tail)
            {
                int current = queue[head++], x = current % columns, y = current / columns;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(rows - 1, y + 1); ny++)
                for (int nx = Math.Max(0, x - 1); nx <= Math.Min(columns - 1, x + 1); nx++)
                {
                    int next = ny * columns + nx;
                    if (!occupied[next]) continue;
                    occupied[next] = false; queue[tail++] = next;
                }
            }
            int x0 = Math.Max(0, left * tile - 8), y0 = Math.Max(0, top * tile - 8);
            int x1 = Math.Min(matrix.Width, right * tile + 8), y1 = Math.Min(matrix.Height, bottom * tile + 8);
            if (x1 - x0 < 32 || y1 - y0 < 32 || x1 - x0 > MaximumSide / 2 || y1 - y0 > MaximumSide / 2) continue;
            if (x1 - x0 > (y1 - y0) * 8 || y1 - y0 > (x1 - x0) * 8) continue;
            emitted++;
            yield return new(x0, y0, x1 - x0, y1 - y0);
        }
    }

    internal static string Payload(Result result)
    {
        string text = result.Text;
        // The charset hint leaves untagged byte segments losslessly represented as Latin-1.
        // ECI-tagged text is already decoded by ZXing. Replace byte spans, not the whole message:
        // numeric/alphanumeric/Kanji segments surrounding a byte segment must survive.
        if (result.ResultMetadata?.TryGetValue(ResultMetadataType.BYTE_SEGMENTS, out object? segments) == true
            && segments is IEnumerable<byte[]> bytes)
        {
            int offset = 0;
            foreach (byte[] segment in bytes)
            {
                string literal = Encoding.Latin1.GetString(segment);
                int start = text.IndexOf(literal, offset, StringComparison.Ordinal);
                if (start < 0) continue; // Explicit ECI already produced Unicode.
                string decoded = DecodeBytes(segment, literal);
                text = string.Concat(text.AsSpan(0, start), decoded, text.AsSpan(start + literal.Length));
                offset = start + decoded.Length;
            }
        }
        else if (text.Any(c => c >= 128) && text.All(c => c <= 255))
            text = DecodeBytes(Encoding.Latin1.GetBytes(text), text);
        return new string(text.Where(c => !char.IsControl(c) || c is '\t' or '\r' or '\n').ToArray());
    }

    private static string DecodeBytes(byte[] bytes, string fallback)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            try { return Korean.GetString(bytes); }
            catch (DecoderFallbackException) { return fallback; }
        }
    }

    private static OcrRect Bounds(Result result, byte[] pixels, int width, int height)
    {
        ResultPoint[] points = result.ResultPoints?.Where(point => point is not null).ToArray() ?? [];
        if (points.Length < 2) return default;
        double left = points.Min(point => point.X), top = points.Min(point => point.Y);
        double right = points.Max(point => point.X), bottom = points.Max(point => point.Y);
        if (result.BarcodeFormat == BarcodeFormat.QR_CODE && points.Length >= 3)
        {
            // ZXing QR points are finder centres (bottom-left, top-left, top-right), not symbol corners.
            double fourthX = points[0].X + points[2].X - points[1].X;
            double fourthY = points[0].Y + points[2].Y - points[1].Y;
            left = Math.Min(left, fourthX); right = Math.Max(right, fourthX);
            top = Math.Min(top, fourthY); bottom = Math.Max(bottom, fourthY);
            double module = points.Take(3).Select(point => FinderModule(point, pixels, width, height)).Order().ElementAt(1);
            double padding = module * 3.5;
            left -= padding; top -= padding; right += padding; bottom += padding;
        }
        else if (result.BarcodeFormat is BarcodeFormat.EAN_13 or BarcodeFormat.EAN_8 or BarcodeFormat.UPC_A or BarcodeFormat.CODE_128)
        {
            // A 1-D read reports one scan line. Follow the first bar to both ends for highlighting/redaction.
            bool vertical = bottom - top > right - left;
            int scanStart = Math.Max(0, (int)Math.Ceiling(vertical ? top : left));
            int scanEnd = Math.Min(vertical ? height - 1 : width - 1, (int)Math.Floor(vertical ? bottom : right));
            int fixedAxis = Math.Clamp((int)Math.Round(vertical ? left : top), 0, vertical ? width - 1 : height - 1);
            bool ScanDark(int position) => pixels[vertical ? position * width + fixedAxis : fixedAxis * width + position] < 128;
            int narrowest = int.MaxValue, runStart = scanStart;
            for (int position = scanStart + 1; position <= scanEnd; position++)
            {
                if (ScanDark(position) == ScanDark(position - 1)) continue;
                if (runStart != scanStart) narrowest = Math.Min(narrowest, position - runStart);
                runStart = position;
            }
            (int bestStart, int bestEnd) = BarExtent(left, top, right, bottom, vertical, pixels, width, height);
            if (vertical) { left = bestStart; right = bestEnd; }
            else { top = bestStart; bottom = bestEnd; }
            // Endpoints sit at guard-pattern centres. Include the complete start/stop bars
            // (and at most a little quiet zone), especially when privacy redacts the payload.
            double module = narrowest == int.MaxValue ? 1 : narrowest;
            double before = module * (result.BarcodeFormat == BarcodeFormat.CODE_128 ? 8 : 2);
            double after = module * (result.BarcodeFormat == BarcodeFormat.CODE_128 ? 8 : 2);
            if (vertical) { top -= before; bottom += after; }
            else { left -= before; right += after; }
        }
        left = Math.Clamp(left, 0, width); top = Math.Clamp(top, 0, height);
        right = Math.Clamp(right, left, width); bottom = Math.Clamp(bottom, top, height);
        return new(left, top, right - left, bottom - top);
    }

    /// <summary>Perpendicular extent [start, end) of the bars crossed by a 1-D scan line.</summary>
    private static (int Start, int End) BarExtent(double left, double top, double right, double bottom, bool vertical,
        byte[] pixels, int width, int height)
    {
        int bestStart = 0, bestEnd = vertical ? width : height;
        for (int sample = 0; sample < 16; sample++)
        {
            int x = Math.Clamp((int)Math.Round(left + (right - left) * sample / 15), 0, width - 1);
            int y = Math.Clamp((int)Math.Round(top + (bottom - top) * sample / 15), 0, height - 1);
            bool dark = pixels[y * width + x] < 128;
            int a = vertical ? x : y, b = a, size = vertical ? width : height;
            bool Same(int position) => (pixels[vertical ? y * width + position : position * width + x] < 128) == dark;
            while (a > 0 && Same(a - 1)) a--;
            while (b + 1 < size && Same(b + 1)) b++;
            // Gaps extend into the surrounding page; bars terminate at the symbol edge.
            if (b + 1 - a < bestEnd - bestStart && b + 1 - a >= 3) { bestStart = a; bestEnd = b + 1; }
        }
        return (bestStart, bestEnd);
    }

    private static bool IsOneDimensional(BarcodeFormat format) =>
        format is BarcodeFormat.EAN_13 or BarcodeFormat.EAN_8 or BarcodeFormat.UPC_A or BarcodeFormat.CODE_128;

    /// <summary>
    /// A 1-D read comes from a single scan row. Accept it only if the lines at about 25% and 75% of
    /// the bar extent (perpendicular to the scan) each decode to the same format and text. Points are
    /// in <paramref name="pixels"/> coordinates; <paramref name="span"/> limits the scan line the same
    /// way the decoding pass was limited (whole image or the candidate crop).
    /// </summary>
    private static bool Confirmed(Result result, byte[] pixels, int width, int height, bool inverted, Int32Rect span,
        IDictionary<DecodeHintType, object> hints, CancellationToken token)
    {
        if (!IsOneDimensional(result.BarcodeFormat)) return true;
        ResultPoint[] points = result.ResultPoints?.Where(point => point is not null).ToArray() ?? [];
        if (points.Length < 2) return false;
        double left = points.Min(point => point.X), top = points.Min(point => point.Y);
        double right = points.Max(point => point.X), bottom = points.Max(point => point.Y);
        bool vertical = bottom - top > right - left;
        (int start, int end) = BarExtent(left, top, right, bottom, vertical, pixels, width, height);
        int scanStart = vertical ? span.Y : span.X;
        int length = vertical ? span.Height : span.Width;
        if (length < 3 || end - start < 1) return false;
        var confirmHints = new Dictionary<DecodeHintType, object>(hints)
        {
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { result.BarcodeFormat },
        };
        var lineReader = new ZXing.OneD.MultiFormatOneDReader(confirmHints);
        byte[] line = new byte[length];
        foreach (double fraction in new[] { .25, .75 })
        {
            token.ThrowIfCancellationRequested();
            int across = Math.Clamp((int)Math.Round(start + (end - 1 - start) * fraction), 0, (vertical ? width : height) - 1);
            for (int i = 0; i < length; i++)
            {
                byte value = pixels[vertical ? (scanStart + i) * width + across : across * width + scanStart + i];
                line[i] = inverted ? (byte)(255 - value) : value;
            }
            // Same row binarization the 1-D readers use (HybridBinarizer inherits it for rows).
            BitArray? row = new GlobalHistogramBinarizer(new DeadlineLuminance(line, length, 1, token)).getBlackRow(0, null);
            if (row is null) return false;
            Result? again = Matches(lineReader.decodeRow(0, row, confirmHints));
            if (again is null)
            {
                // Like OneDReader, also read the line backwards for symbols rotated by 180 degrees.
                row.reverse();
                again = Matches(lineReader.decodeRow(0, row, confirmHints));
            }
            if (again is null) return false;
        }
        return true;

        Result? Matches(Result? candidate) => candidate is not null && candidate.BarcodeFormat == result.BarcodeFormat
            && candidate.Text == result.Text ? candidate : null;
    }

    private static double FinderModule(ResultPoint point, byte[] pixels, int width, int height)
    {
        int x = Math.Clamp((int)point.X, 0, width - 1), y = Math.Clamp((int)point.Y, 0, height - 1);
        bool dark = pixels[y * width + x] < 128;
        int left = x, right = x;
        while (left > 0 && (pixels[y * width + left - 1] < 128) == dark) left--;
        while (right + 1 < width && (pixels[y * width + right + 1] < 128) == dark) right++;
        return Math.Max(1, (right - left + 1) / 3.0);
    }

    // Cancellation is checked while binarizers request rows/matrices and between rotations.
    // The outer service deadline also bounds delivery during ZXing's non-interruptible math.
    private sealed class DeadlineLuminance(byte[] pixels, int width, int height, CancellationToken token)
        : BaseLuminanceSource(pixels, width, height)
    {
        public override byte[] Matrix { get { token.ThrowIfCancellationRequested(); return base.Matrix; } }
        public override byte[] getRow(int y, byte[]? row)
        { token.ThrowIfCancellationRequested(); return base.getRow(y, row); }
        protected override LuminanceSource CreateLuminanceSource(byte[] newLuminances, int newWidth, int newHeight)
        { token.ThrowIfCancellationRequested(); return new DeadlineLuminance(newLuminances, newWidth, newHeight, token); }
    }
}
