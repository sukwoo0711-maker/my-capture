using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.Ocr;

/// <summary>
/// Windows OCR sometimes returns a long Latin line as one word without spaces ("Thelibraryopensashortmessage…"),
/// which no translator can use. Such a word is read again in pieces cut at blank columns of its own ink, and the
/// pieces' words replace it only when their letters spell the same word. A second reading by the same engine,
/// never a dictionary guess; no model is involved.
/// </summary>
internal static class OcrLatinLineSplit
{
    private const int MinimumLetters = 24;

    /// <summary>Whether a word looks like a whole Latin line read without its spaces.</summary>
    internal static bool IsJoined(OcrWord word)
    {
        if (!OcrGeometry.IsValid(word.Bounds) || word.Text.Length < MinimumLetters) return false;
        int letters = 0, other = 0;
        foreach (char value in word.Text)
        {
            if (char.IsAsciiLetter(value)) letters++;
            else if (!char.IsAsciiDigit(value) && value is not ('.' or ',' or ';' or ':' or '\'' or '-' or '!' or '?' or '(' or ')')) other++;
        }
        return other == 0 && letters >= MinimumLetters && letters * 10 >= word.Text.Length * 8
            && word.Bounds.Width >= word.Bounds.Height * 12;
    }

    internal static bool HasJoinedWords(OcrResult result) => result.HasText && !result.ReadingOrientation.IsRotated &&
        result.Lines.Any(line => !line.IsCodePayload && line.Words.Any(IsJoined));

    internal static async Task<OcrResult> SplitAsync(OcrResult source, OcrGrayImage image,
        Func<BitmapSource, CancellationToken, Task<OcrResult>> read, CancellationToken cancellationToken)
    {
        if (!HasJoinedWords(source)) return source;
        var lines = new OcrLine[source.Lines.Count];
        bool changed = false;
        for (int index = 0; index < lines.Length; index++)
        {
            OcrLine line = source.Lines[index];
            lines[index] = line;
            if (line.IsCodePayload || !line.Words.Any(IsJoined)) continue;
            var words = new List<OcrWord>();
            var texts = new List<string>();
            bool lineChanged = false;
            foreach (OcrWord word in line.Words)
            {
                IReadOnlyList<OcrWord>? pieces = IsJoined(word)
                    ? await ReadPiecesAsync(word, image, read, cancellationToken).ConfigureAwait(false) : null;
                if (pieces is null) { words.Add(word); texts.Add(word.Text); continue; }
                words.AddRange(pieces);
                texts.AddRange(pieces.Select(piece => piece.Text));
                lineChanged = true;
            }
            if (!lineChanged) continue;
            lines[index] = line with { Text = string.Join(' ', texts), Words = words };
            changed = true;
        }
        return changed ? OcrResult.Success(string.Join('\n', lines.Select(line => line.Text)), source.LanguageTag ?? string.Empty,
            lines, source.Elapsed, source.ReadingOrientation, source.TextAngle) : source;
    }

    private static async Task<IReadOnlyList<OcrWord>?> ReadPiecesAsync(OcrWord word, OcrGrayImage image,
        Func<BitmapSource, CancellationToken, Task<OcrResult>> read, CancellationToken cancellationToken)
    {
        OcrRect bounds = word.Bounds;
        double height = bounds.Height;
        int pad = (int)Math.Ceiling(height * .35);
        int top = Math.Max(0, (int)Math.Floor(bounds.Y) - pad), bottom = Math.Min(image.Height, (int)Math.Ceiling(bounds.Bottom) + pad);
        int left = Math.Max(0, (int)Math.Floor(bounds.X)), right = Math.Min(image.Width, (int)Math.Ceiling(bounds.Right));
        if (bottom - top < 4 || right - left < 16) return null;
        byte background = Background(image, left, top, right, bottom);
        int threshold = 48;

        // Blank columns: no pixel in the band differs from the background by the threshold.
        var blank = new bool[right - left];
        for (int x = left; x < right; x++)
        {
            bool empty = true;
            for (int y = top; y < bottom && empty; y++) empty = Math.Abs(image[x, y] - background) < threshold;
            blank[x - left] = empty;
        }
        // A cut is the middle of a blank run at least a fifth of the line height wide: a space, not a letter gap.
        int minimumGap = Math.Max(2, (int)Math.Round(height * .2));
        var cuts = new List<int>();
        for (int x = 0; x < blank.Length;)
        {
            if (!blank[x]) { x++; continue; }
            int end = x;
            while (end < blank.Length && blank[end]) end++;
            if (end - x >= minimumGap && x > 0 && end < blank.Length) cuts.Add(left + (x + end) / 2);
            x = end;
        }
        if (cuts.Count < 2) return null;

        int limit = (int)Math.Clamp(height * 45, 300, 1100);
        var pieces = new List<(int Left, int Right)>();
        int start = left;
        while (start < right)
        {
            if (right - start <= limit) { pieces.Add((start, right)); break; }
            int cut = cuts.Where(value => value > start + limit / 3 && value <= start + limit).DefaultIfEmpty(-1).Max();
            if (cut < 0) return null;
            pieces.Add((start, cut));
            start = cut;
        }
        if (pieces.Count < 2) return null;

        // Stack the pieces under each other on the picture's own background, one line height apart.
        int bandHeight = bottom - top, gap = Math.Max(4, (int)Math.Ceiling(height));
        int width = pieces.Max(piece => piece.Right - piece.Left) + 2 * gap;
        int canvasHeight = pieces.Count * (bandHeight + gap) + gap;
        byte[] pixels = new byte[checked(width * canvasHeight)];
        Array.Fill(pixels, background);
        for (int index = 0; index < pieces.Count; index++)
        {
            int originY = gap + index * (bandHeight + gap);
            for (int y = 0; y < bandHeight; y++)
                for (int x = pieces[index].Left; x < pieces[index].Right; x++)
                    pixels[(originY + y) * width + gap + x - pieces[index].Left] = image[x, top + y];
        }
        BitmapSource canvas = BitmapSource.Create(width, canvasHeight, 96, 96, PixelFormats.Gray8, null, pixels, width);
        canvas.Freeze();

        OcrResult second = await read(canvas, cancellationToken).ConfigureAwait(false);
        if (!second.HasText) return null;
        var mapped = new List<(int Piece, OcrWord Word)>();
        foreach (OcrWord found in second.Lines.SelectMany(line => line.Words))
        {
            double centre = found.Bounds.Y + found.Bounds.Height / 2;
            int index = (int)Math.Floor((centre - gap) / (bandHeight + gap));
            if (index < 0 || index >= pieces.Count || string.IsNullOrWhiteSpace(found.Text)) continue;
            int originY = gap + index * (bandHeight + gap);
            mapped.Add((index, found with
            {
                Bounds = new OcrRect(found.Bounds.X - gap + pieces[index].Left, found.Bounds.Y - originY + top,
                    found.Bounds.Width, found.Bounds.Height),
            }));
        }
        OcrWord[] words = [.. mapped.OrderBy(item => item.Piece).ThenBy(item => item.Word.Bounds.X).Select(item => item.Word)];
        if (words.Length < 2) return null;

        // The pieces must spell the joined word, apart from a few letters the second reading saw differently.
        string expected = Letters(word.Text), actual = string.Concat(words.Select(piece => Letters(piece.Text)));
        return Distance(expected, actual) <= Math.Max(2, expected.Length / 10) ? words : null;
    }

    private static string Letters(string text) => new([.. text.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static byte Background(OcrGrayImage image, int left, int top, int right, int bottom)
    {
        var border = new List<byte>(2 * (right - left + bottom - top));
        for (int x = left; x < right; x++) { border.Add(image[x, top]); border.Add(image[x, bottom - 1]); }
        for (int y = top; y < bottom; y++) { border.Add(image[left, y]); border.Add(image[right - 1, y]); }
        border.Sort();
        return border[border.Count / 2];
    }

    private static int Distance(string first, string second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (int column = 0; column <= second.Length; column++) previous[column] = column;
        for (int row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= second.Length; column++)
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[second.Length];
    }
}
