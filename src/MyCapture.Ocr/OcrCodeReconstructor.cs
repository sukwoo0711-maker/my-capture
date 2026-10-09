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
        // Bounded, local keyword inference; ties deliberately remain unknown.
        string sample = text.Length > 100_000 ? text[..100_000] : text;
        (string Name, string Pattern)[] languages =
        [
            ("C#", @"\b(namespace|using|public|private|internal|protected|async|Task|Console|IEnumerable)\b|=>"),
            ("JavaScript", @"\b(function|const|let|var|console|document|require|export|import)\b|===|!=="),
            ("Python", @"\b(def|elif|lambda|self|None|True|False|print|import|from)\b|^\s*#|^\s*for\s+[^\r\n]+\s+in\s+[^\r\n]+:\s*$"),
            ("SQL", @"\b(SELECT|FROM|WHERE|JOIN|INSERT|UPDATE|DELETE|CREATE|GROUP\s+BY|ORDER\s+BY|VALUES)\b"),
        ];
        var scores = languages.Select(language => (language.Name, Score: Regex.Matches(sample,
            language.Pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline
                | (language.Name == "SQL" ? RegexOptions.IgnoreCase : RegexOptions.None),
            TimeSpan.FromMilliseconds(200)).Count)).OrderByDescending(value => value.Score).ToArray();
        return scores[0].Score >= 2 && scores[0].Score > scores[1].Score ? scores[0].Name : null;
    }
}
