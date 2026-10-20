using System.Text;
using System.Text.RegularExpressions;

namespace MyCapture.Ocr;

internal static class OcrCodeReconstructor
{
    internal static OcrCode Build(IReadOnlyList<OcrLayoutRow> rows, double origin,
        IReadOnlyList<OcrLayoutRow>? referenceRows = null)
    {
        if (rows.Count == 0) return new("", null, null, 1, []);
        referenceRows ??= rows;
        var words = referenceRows.SelectMany(row => row.Words).ToArray();
        double width = Math.Max(.5, OcrGeometry.CharacterWidth(words));
        double[] samples = words.Where(word => OcrGeometry.CharacterColumns(word.Text) >= 3)
            .Select(word => word.Bounds.Width / OcrGeometry.CharacterColumns(word.Text)).ToArray();
        bool? monospace = samples.Length < 3 ? null
            : samples.Count(sample => Math.Abs(sample - width) / width <= .16) >= samples.Length * .8;
        var output = new StringBuilder();
        // Glyph heights vary (for example, parentheses versus lowercase letters). Use the
        // baseline pitch to retain actual blank lines without inventing them between normal rows.
        double height = OcrGeometry.Median(referenceRows.Select(row => row.Bounds.Height));
        double[] steps = referenceRows.Skip(1).Select((row, index) =>
            row.Bounds.Y - referenceRows[index].Bounds.Y).Where(step => step > 0).Order().ToArray();
        double pitch = steps.Length == 0 ? height * 1.5 : steps[(steps.Length - 1) / 4];
        if (steps.Length == 1 && pitch > height * 2.5) pitch = height * 1.5;
        for (int index = 0; index < rows.Count; index++)
        {
            OcrLayoutRow row = rows[index];
            if (index > 0)
            {
                output.Append('\n');
                int blankLines = (int)Math.Clamp(Math.Round(
                    (row.Bounds.Y - rows[index - 1].Bounds.Y) / pitch,
                    MidpointRounding.AwayFromZero) - 1, 0, 16);
                output.Append('\n', blankLines);
            }
            double previousRight = origin;
            foreach (OcrWord word in row.Words)
            {
                int spaces = (int)Math.Clamp(Math.Round((word.Bounds.X - previousRight) / width,
                    MidpointRounding.AwayFromZero), 0, 4096);
                output.Append(' ', spaces);
                output.Append(word.Text);
                previousRight = word.Bounds.Right;
            }
        }
        string text = output.ToString();
        string? language = EstimateLanguage(text)
            ?? EstimateLanguage(string.Join("\n", referenceRows.Select(row => row.Text)));
        var regions = new List<OcrRect>();
        for (int index = 0; index < rows.Count; index++)
        {
            if (index == 0 || rows[index].Bounds.Y - rows[index - 1].Bounds.Y > pitch * 2.5)
                regions.Add(rows[index].Bounds);
            else regions[^1] = OcrRect.Union(regions[^1], rows[index].Bounds);
        }
        return new(text, language, monospace, width, regions);
    }

    internal static string? EstimateLanguage(string text)
    {
        try
        {
            return EstimateLanguageCore(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // The language is only a label; a pathological sample leaves it unknown instead of
            // failing the recognized text it accompanies.
            return null;
        }
    }

    private static string? EstimateLanguageCore(string text)
    {
        // Bounded, local keyword inference; ties deliberately remain unknown.
        string sample = text.Length > 100_000 ? text[..100_000] : text;
        const RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        TimeSpan timeout = TimeSpan.FromMilliseconds(200);
        // OCR can separate # from a directive, but English comments need a code-shaped suffix.
        // Spaced #error has no distinctive suffix, so it remains comment evidence.
        var directives = new Regex(
            @"^[^\S\r\n]*#(?:(include|define|undef|if|ifdef|ifndef|elif|else|endif|pragma|error)\b|"
            + @"[^\S\r\n]+(?:"
            + @"include[^\S\r\n]*(?:<[^>\r\n]+>|""[^""\r\n]+"")|"
            + @"define[^\S\r\n]+(?:[A-Za-z_]\w*\(|"
            + @"(?:[A-Z_][A-Z0-9_]*|(?=\w*[_0-9])[A-Za-z_]\w*)(?=[^\S\r\n]|\r?$))|"
            + @"(?:ifdef|ifndef|undef)[^\S\r\n]+[A-Za-z_]\w*[^\S\r\n]*(?=\r?$)|"
            + @"endif[^\S\r\n]*(?://[^\r\n]*|/\*[^\r\n]*\*/[^\S\r\n]*)?(?=\r?$)|"
            + @"else[^\S\r\n]*(?=\r?$)|"
            + @"(?:if|elif)[^\S\r\n]+(?:defined\b|[(!0-9]|[A-Z_][A-Z0-9_]*\b)|"
            + @"pragma[^\S\r\n]+(?:once[^\S\r\n]*(?=\r?$|//|/\*)|"
            + @"[a-z_][a-z_0-9]*[^\S\r\n]*\(|warning[^\S\r\n]+(?:disable|restore)\b)"
            + @"))[^\r\n]*",
            options, timeout);
        int directiveCount = directives.Matches(sample).Count;
        // Directive bodies are not Python comments or independent language keywords.
        string codeSample = directives.Replace(sample, "");
        // A complete directive-only snippet is enough evidence, even for one #define.
        if (directiveCount > 0 && string.IsNullOrWhiteSpace(codeSample)) return "C/C++";
        (string Name, string Pattern)[] languages =
        [
            ("C/C++", @"\bstd\s*::|\b(nullptr|typedef)\b|\bprintf\s*\(|\bint\s+main\s*\("),
            ("C#", @"\b(namespace|using|public|private|internal|protected|async|Task|Console|IEnumerable)\b|=>|^[^\S\r\n]*#[^\S\r\n]*(region|endregion|nullable)\b"),
            ("JavaScript", @"\b(function|const|let|var|console|document|require|export|import)\b|===|!=="),
            ("Python", @"\b(def|elif|lambda|self|None|True|False|print|import|from)\b|^[^\S\r\n]*#(?![^\S\r\n]*(region|endregion|nullable)\b)|^[^\S\r\n]*for\s+[^\r\n]+\s+in\s+[^\r\n]+:[^\S\r\n]*\r?$"),
            ("SQL", @"\b(SELECT|FROM|WHERE|JOIN|INSERT|UPDATE|DELETE|CREATE|GROUP\s+BY|ORDER\s+BY|VALUES)\b"),
        ];
        var scores = languages.Select(language => (language.Name, Score: Regex.Matches(codeSample,
            language.Pattern, options | (language.Name == "SQL" ? RegexOptions.IgnoreCase : RegexOptions.None),
            timeout).Count + (language.Name == "C/C++" ? directiveCount : 0)))
            .OrderByDescending(value => value.Score).ToArray();
        return scores[0].Score >= 2 && scores[0].Score > scores[1].Score ? scores[0].Name : null;
    }
}
