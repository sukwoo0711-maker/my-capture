using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.Ocr;

/// <summary>
/// Repairs Latin letters whose upper- and lower-case shapes are the same (c o s u v w x z) and the digits 0 and 1
/// read inside words, from the glyph's own ink in the source pixels, never from a word list. A glyph whose ink
/// rises only to the line's x-height is lower case; one that reaches the height of the line's capitals and
/// ascenders keeps what Windows OCR read, so camelCase, PascalCase and acronyms are kept as drawn.
/// </summary>
/// <remarks>
/// Word boxes, line boxes and the token sequence are unchanged: only characters inside a word are replaced.
/// A word is repaired only when its ink splits into exactly one column run per character, so every decision
/// rests on one isolated glyph, and only when the same line shows both x-height and tall reference letters.
/// </remarks>
internal static class OcrLatinGlyphRepair
{
    private const string ShapeIdentical = "COSUVWXZ";
    private const string XHeightReference = "aemnr";
    private const string XHeightWeakReference = "ocsuvwxz";
    private const string TallReference = "ABDEFGHKLMNPRTbdhkl";
    private const double InkColumn = .35, InkFloor = .2, GapRow = .12;

    /// <summary>Whether any word of <paramref name="result"/> is one the repair could change.</summary>
    internal static bool HasCandidates(OcrResult result) => result.HasText &&
        result.Lines.Any(line => IsLatinLine(line) && line.Words.Any(word => IsCandidate(word.Text)));

    internal static OcrResult Repair(OcrResult source, BitmapSource? bitmap)
    {
        if (bitmap is null || !source.HasText || source.ReadingOrientation.IsRotated || !HasCandidates(source)) return source;
        OcrGrayImage? image = OcrGrayImage.From(bitmap);
        return image is null ? source : Repair(source, image);
    }

    internal static OcrResult Repair(OcrResult source, OcrGrayImage image)
    {
        if (!source.HasText || source.ReadingOrientation.IsRotated) return source;
        bool changed = false;
        var lines = new OcrLine[source.Lines.Count];
        for (int index = 0; index < lines.Length; index++)
        {
            lines[index] = RepairLine(source.Lines[index], image);
            changed |= !ReferenceEquals(lines[index], source.Lines[index]);
        }
        return changed ? OcrResult.Success(string.Join('\n', lines.Select(line => line.Text)), source.LanguageTag ?? string.Empty,
            lines, source.Elapsed, source.ReadingOrientation, source.TextAngle) : source;
    }

    private static OcrLine RepairLine(OcrLine line, OcrGrayImage image)
    {
        if (!IsLatinLine(line) || !line.Words.Any(word => IsCandidate(word.Text))) return line;
        Glyph[]?[] glyphs = [.. line.Words.Select(word => Segment(image, word, line.Bounds))];
        (double XHeight, double Tall)? reference = Reference(line, glyphs);
        if (reference is not { } metrics || metrics.Tall - metrics.XHeight < Math.Max(1.0, metrics.Tall * .12)) return line;

        OcrWord[]? words = null;
        for (int index = 0; index < line.Words.Count; index++)
        {
            OcrWord word = line.Words[index];
            if (!IsCandidate(word.Text) || glyphs[index] is not { } runs) continue;
            string repaired = RepairWord(word.Text, runs, metrics.XHeight, metrics.Tall);
            if (repaired == word.Text) continue;
            words ??= [.. line.Words];
            words[index] = word with { Text = repaired };
        }
        if (words is null) return line;
        return line with { Text = Rebuild(line, words), Words = words };
    }

    // Keep the original separators: the line text is the word texts in order with its own spacing.
    private static string Rebuild(OcrLine line, OcrWord[] words)
    {
        var text = new System.Text.StringBuilder(line.Text.Length);
        int cursor = 0;
        for (int index = 0; index < words.Length; index++)
        {
            string token = line.Words[index].Text;
            int start = line.Text.IndexOf(token, cursor, StringComparison.Ordinal);
            if (start < 0) return string.Join(' ', words.Select(word => word.Text));
            text.Append(line.Text, cursor, start - cursor).Append(words[index].Text);
            cursor = start + token.Length;
        }
        return text.Append(line.Text, cursor, line.Text.Length - cursor).ToString();
    }

    internal static string RepairWord(string text, IReadOnlyList<Glyph> glyphs, double xHeight, double tall)
    {
        if (glyphs.Count != text.Length) return text;
        (int start, int length) = Core(text);
        // The word's own reference letters come first: one line can hold a heading and body text of other sizes.
        // A word with only one kind of reference scales the line's other reference by the same ratio.
        double? localX = LocalReference(text, glyphs, start, length, XHeightReference.Replace("n", "", StringComparison.Ordinal))
            ?? LocalReference(text, glyphs, start, length, XHeightWeakReference);
        double? localTall = LocalReference(text, glyphs, start, length, TallReference);
        (xHeight, tall) = (localX, localTall) switch
        {
            ({ } ownX, { } ownTall) => (ownX, ownTall),
            ({ } ownX, null) => (ownX, tall * ownX / xHeight),
            (null, { } ownTall) => (xHeight * ownTall / tall, ownTall),
            _ => (xHeight, tall),
        };
        if (tall - xHeight < Math.Max(1.0, tall * .12)) return text;
        double lowerLimit = xHeight + .45 * (tall - xHeight), ascenderLimit = xHeight + .7 * (tall - xHeight);
        char[] output = text.ToCharArray();
        if (!text.AsSpan(start, length).ContainsAnyInRange('a', 'z'))
        {
            // No lower-case letter: only a short word of shape-identical capitals (SO, OX) or of the digits 0, 1 and 5
            // (50, 15), and only when every glyph agrees: lining capitals and digits stand at cap height.
            for (int index = start; index < start + length; index++)
            {
                char value = text[index];
                Glyph glyph = glyphs[index];
                char? letter = value switch
                {
                    '0' => glyph.Height <= lowerLimit ? 'o' : null,
                    '5' => glyph.Height <= lowerLimit ? 's' : null,
                    '1' => glyph.HasGap && glyph.Height <= lowerLimit ? 'i' : null,
                    _ when ShapeIdentical.Contains(value, StringComparison.Ordinal) => glyph.Height <= lowerLimit ? char.ToLowerInvariant(value) : null,
                    _ => null,
                };
                if (letter is null) return text;
                output[index] = letter.Value;
            }
            return new string(output);
        }
        bool digits = false;
        for (int index = start; index < start + length; index++) digits |= char.IsAsciiDigit(text[index]);
        for (int index = start; index < start + length; index++)
        {
            char value = text[index];
            Glyph glyph = glyphs[index];
            if (ShapeIdentical.Contains(value, StringComparison.Ordinal) && glyph.Height <= lowerLimit)
                output[index] = char.ToLowerInvariant(value);
            // A dotted stem at x-height is an i, whatever bar Windows read; a word-initial I stays (I and l share no dot).
            else if (!digits && (value == 'l' || value == 'I' && index > start) && glyph.HasGap && glyph.Height <= lowerLimit)
                output[index] = 'i';
            // An n whose ink rises to the ascenders is an h.
            else if (!digits && value == 'n' && glyph.Height >= ascenderLimit)
                output[index] = 'h';
        }

        // A run of 0 and 1 is replaced whole or not at all. A 0 is a letter o only at x-height; a 1 only between
        // letters, as i when its ink has the dot's gap and as l otherwise.
        int letters = 0;
        for (int index = start; index < start + length; index++) letters += char.IsAsciiLetter(text[index]) ? 1 : 0;
        for (int index = start; index < start + length;)
        {
            if (text[index] is not ('0' or '1')) { index++; continue; }
            int end = index;
            while (end < start + length && text[end] is '0' or '1') end++;
            bool interior = index > start && end < start + length && char.IsAsciiLetter(text[index - 1]) && char.IsAsciiLetter(text[end]);
            var replacement = new char[end - index];
            bool accepted = letters >= 1;
            for (int digit = index; digit < end && accepted; digit++)
            {
                if (text[digit] == '0') { replacement[digit - index] = 'o'; accepted = glyphs[digit].Height <= lowerLimit; }
                else { replacement[digit - index] = glyphs[digit].HasGap && glyphs[digit].Height <= lowerLimit ? 'i' : 'l'; accepted = interior; }
            }
            if (accepted) replacement.CopyTo(output, index);
            index = end;
        }
        return new string(output);
    }

    /// <summary>
    /// Words worth measuring: one ASCII letter-digit atom with a lower-case letter and a shape-identical capital,
    /// the digits 0/1 among letters, or a stem (l, I, n) to check; or a short word of shape-identical capitals or of
    /// the digits 0, 1 and 5 only. Other all-capital words, identifiers with separators and other digits are left as read.
    /// </summary>
    internal static bool IsCandidate(string text)
    {
        (int start, int length) = Core(text);
        if (length < 2) return false;
        ReadOnlySpan<char> core = text.AsSpan(start, length);
        if (length <= 4 && !core.ContainsAnyInRange('a', 'z'))
        {
            // SO, OX (shape-identical capitals only) or 50, 15 (0, 1, 5 with a 0 or 5): the ink decides below.
            bool shapeOnly = true, digitsOnly = true;
            foreach (char value in core)
            {
                shapeOnly &= ShapeIdentical.Contains(value, StringComparison.Ordinal);
                digitsOnly &= value is '0' or '1' or '5';
            }
            return shapeOnly || digitsOnly && length <= 3 && core.IndexOfAny('0', '5') >= 0;
        }
        bool lower = false, shape = false, zeroOne = false, otherDigit = false, stem = false;
        for (int index = start; index < start + length; index++)
        {
            char value = text[index];
            if (!char.IsAsciiLetterOrDigit(value)) return false;
            lower |= char.IsAsciiLetterLower(value);
            shape |= ShapeIdentical.Contains(value, StringComparison.Ordinal);
            zeroOne |= value is '0' or '1';
            otherDigit |= value is >= '2' and <= '9';
            stem |= value is 'l' or 'I' or 'n';
        }
        return lower && (shape || zeroOne && !otherDigit || stem && !zeroOne && !otherDigit);
    }

    // The letter-digit atom of a token, without leading and trailing punctuation.
    private static (int Start, int Length) Core(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && !char.IsAsciiLetterOrDigit(text[start])) start++;
        while (end > start && !char.IsAsciiLetterOrDigit(text[end - 1])) end--;
        for (int index = start; index < end; index++)
            if (!char.IsAsciiLetterOrDigit(text[index])) return (0, 0);
        return (start, end - start);
    }

    private static bool IsLatinLine(OcrLine line)
    {
        if (line.IsCodePayload) return false;
        int latin = 0, hangul = 0;
        foreach (char value in line.Text)
        {
            if (char.IsAsciiLetter(value)) latin++;
            else if (value is >= '가' and <= '힣') hangul++;
        }
        return latin >= 4 && hangul * 3 <= latin;
    }

    private static (double, double)? Reference(OcrLine line, Glyph[]?[] glyphs)
    {
        List<double> xHeights = [], weak = [], tall = [];
        for (int index = 0; index < line.Words.Count; index++)
        {
            string text = line.Words[index].Text;
            if (glyphs[index] is not { } runs || runs.Length != text.Length) continue;
            for (int character = 0; character < text.Length; character++)
            {
                Glyph glyph = runs[character];
                if (glyph.HasGap) continue;
                char value = text[character];
                if (XHeightReference.Contains(value, StringComparison.Ordinal)) xHeights.Add(glyph.Height);
                else if (XHeightWeakReference.Contains(value, StringComparison.Ordinal)) weak.Add(glyph.Height);
                else if (TallReference.Contains(value, StringComparison.Ordinal)) tall.Add(glyph.Height);
            }
        }
        if (xHeights.Count < 2) xHeights.AddRange(weak);
        if (xHeights.Count < 2 || tall.Count < 1) return null;
        return (Median(xHeights), Median(tall));
    }

    // The median height of the word's own undotted glyphs read as one of <paramref name="letters"/>, if any.
    private static double? LocalReference(string text, IReadOnlyList<Glyph> glyphs, int start, int length, string letters)
    {
        List<double> heights = [];
        for (int index = start; index < start + length; index++)
            if (!glyphs[index].HasGap && letters.Contains(text[index], StringComparison.Ordinal)) heights.Add(glyphs[index].Height);
        return heights.Count == 0 ? null : Median(heights);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    /// <summary>The word's ink as one entry per column run, or null when the box holds no clear ink.</summary>
    internal static Glyph[]? Segment(OcrGrayImage image, OcrWord word, OcrRect line)
    {
        OcrRect bounds = word.Bounds;
        if (!OcrGeometry.IsValid(bounds)) return null;
        int left = Math.Max(0, (int)Math.Floor(bounds.X) - 1), right = Math.Min(image.Width, (int)Math.Ceiling(bounds.Right) + 1);
        // Vertically the line's own band: a word box can stop short of a capital that its line box holds.
        double upper = OcrGeometry.IsValid(line) ? Math.Min(line.Y, bounds.Y) : bounds.Y;
        double lower = OcrGeometry.IsValid(line) ? Math.Max(line.Bottom, bounds.Bottom) : bounds.Bottom;
        int top = Math.Max(0, (int)Math.Floor(upper) - 1), bottom = Math.Min(image.Height, (int)Math.Ceiling(lower) + 1);
        int width = right - left, height = bottom - top;
        if (width < 2 || height < 4 || (long)width * height > 4_000_000) return null;

        var border = new List<byte>(2 * (width + height));
        for (int x = left; x < right; x++) { border.Add(image[x, top]); border.Add(image[x, bottom - 1]); }
        for (int y = top; y < bottom; y++) { border.Add(image[left, y]); border.Add(image[right - 1, y]); }
        border.Sort();
        double background = border[border.Count / 2];

        var difference = new double[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                difference[y * width + x] = Math.Abs(image[left + x, top + y] - background);
        double[] sorted = (double[])difference.Clone();
        Array.Sort(sorted);
        double peak = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .99))];
        if (peak < 40) return null;

        var coverage = new double[width * height];
        var column = new double[width];
        for (int index = 0; index < coverage.Length; index++)
        {
            double value = Math.Min(1, difference[index] / peak);
            coverage[index] = value < InkFloor ? 0 : value;
            column[index % width] = Math.Max(column[index % width], coverage[index]);
        }

        var glyphs = new List<Glyph>();
        for (int x = 0; x < width;)
        {
            if (column[x] < InkColumn) { x++; continue; }
            int end = x;
            while (end < width && column[end] >= InkColumn) end++;
            glyphs.Add(Measure(coverage, width, height, x, end));
            x = end;
        }
        return [.. glyphs];
    }

    private static Glyph Measure(double[] coverage, int width, int height, int start, int end)
    {
        var rows = new double[height];
        for (int y = 0; y < height; y++)
            for (int x = start; x < end; x++)
                rows[y] = Math.Max(rows[y], coverage[y * width + x]);
        // The glyph body is the longest unbroken run of inked rows. A line box can reach into the ink of the line
        // above or below, so other runs count only as an i's dot: a short run just above the body. The rules that
        // read a dot also require an x-height body, so a tall l under another line's ink stays an l.
        var runs = new List<(int First, int Last)>();
        for (int y = 0; y < height;)
        {
            if (rows[y] < InkColumn) { y++; continue; }
            int stop = y;
            while (stop + 1 < height && rows[stop + 1] >= GapRow) stop++;
            int tail = stop;
            while (rows[tail] < InkColumn) tail--;
            runs.Add((y, tail));
            y = stop + 1;
        }
        if (runs.Count == 0) return new Glyph(0, false);
        (int first, int last) = runs.MaxBy(run => run.Last - run.First);
        int body = last - first + 1;
        // Half a row for each faint edge row: thin antialiased strokes (W, V) never reach full coverage, so summing
        // coverage would make a capital look short.
        double extent = body;
        if (first > 0) extent += Math.Min(rows[first - 1], InkColumn) / InkColumn * .5;
        if (last + 1 < height) extent += Math.Min(rows[last + 1], InkColumn) / InkColumn * .5;
        bool dot = runs.Any(run => run.Last < first && first - run.Last - 1 <= Math.Max(2, body * .6)
            && run.Last - run.First + 1 <= body * .6);
        return new Glyph(extent, dot);
    }

    /// <summary>One column run of ink: the sub-pixel height of its body and whether a dot stands just above the body.</summary>
    internal readonly record struct Glyph(double Height, bool HasGap);
}

/// <summary>An 8-bit luminance copy of a picture, read once for pixel evidence.</summary>
internal sealed class OcrGrayImage
{
    private readonly byte[] _pixels;

    internal OcrGrayImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("The pixel count must match the size.", nameof(pixels));
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal byte this[int x, int y] => _pixels[y * Width + x];

    internal static OcrGrayImage? From(BitmapSource bitmap)
    {
        try
        {
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > 80_000_000) return null;
            // Copy the decoded pixels first: a converter over a decoder frame can touch the decoder's thread.
            BitmapSource source = bitmap;
            if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32 && bitmap.Format != PixelFormats.Bgr32)
            {
                int sourceStride = checked((width * bitmap.Format.BitsPerPixel + 7) / 8);
                byte[] raw = new byte[checked(sourceStride * height)];
                bitmap.CopyPixels(raw, sourceStride, 0);
                BitmapSource copy = BitmapSource.Create(width, height, bitmap.DpiX, bitmap.DpiY, bitmap.Format, bitmap.Palette, raw, sourceStride);
                source = new FormatConvertedBitmap(copy, PixelFormats.Bgra32, null, 0);
            }
            int stride = checked(width * 4);
            byte[] bgra = new byte[checked(stride * height)];
            source.CopyPixels(bgra, stride, 0);
            byte[] gray = new byte[width * height];
            for (int index = 0, offset = 0; index < gray.Length; index++, offset += 4)
                gray[index] = (byte)((bgra[offset] * 29 + bgra[offset + 1] * 150 + bgra[offset + 2] * 77) >> 8);
            return new OcrGrayImage(width, height, gray);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }
}
