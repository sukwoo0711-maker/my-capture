using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class DesignGuidelinesSyncTests
{
    private const string TokensBegin = "<!-- tokens:begin -->";
    private const string TokensEnd = "<!-- tokens:end -->";
    private static readonly string[] RequiredTokens =
    [
        "Accent.Default", "Accent.Hover", "Accent.Pressed", "Text.OnAccent",
        "Text.Primary", "Text.Secondary", "Text.Muted",
        "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay",
        "State.Warning", "State.Success", "State.Danger", "Border.Focus", "Focus.RingWidth",
        "Size.HitTarget", "Size.HitTarget.Compact", "Size.RailButton", "Size.IconGlyph",
        "Motion.Fast", "Motion.Normal", "Motion.Deliberate",
        "Space.Xs", "Space.Sm", "Space.Md", "Space.Lg", "Space.Xl", "Space.Xxl", "Space.Hero",
        "Radius.Sm", "Radius.Md", "Radius.Lg", "Radius.Xl",
    ];

    [Fact]
    public void GuidelinesMatchResolvedTokensAndUiFontFamilies() => StaTestHost.Run(() =>
        Assert.Empty(FindErrors(ReadGuidelines(), LoadTokens())));

    [Fact]
    public void ScratchCopyWithChangedTokenIsRejected() => StaTestHost.Run(() =>
    {
        ResourceDictionary tokens = LoadTokens();
        string original = ReadGuidelines();
        Assert.Empty(FindErrors(original, tokens));
        string value = FormatValue(tokens["Space.Lg"]);
        string changed = ((double)tokens["Space.Lg"] + 1).ToString(CultureInfo.InvariantCulture);

        // The scratch copy lives only in memory: the on-disk document remains untouched.
        string scratch = original.Replace($"| Space.Lg | {value} |", $"| Space.Lg | {changed} |", StringComparison.Ordinal);

        Assert.NotEqual(original, scratch);
        string error = Assert.Single(FindErrors(scratch, tokens));
        Assert.Equal($"Space.Lg: document '{changed}', resolved resource '{value}'.", error);
    });

    [Theory]
    [InlineData("Accent.Default")]
    [InlineData("FontSize.Body")]
    public void ScratchCopyMissingARequiredTokenIsRejected(string key) => StaTestHost.Run(() =>
    {
        ResourceDictionary tokens = LoadTokens();
        string original = ReadGuidelines();
        string scratch = original.Replace($"| {key} | {FormatValue(tokens[key])} |", string.Empty, StringComparison.Ordinal);

        Assert.NotEqual(original, scratch);
        Assert.Contains($"Missing token '{key}'.", FindErrors(scratch, tokens));
    });

    [Fact]
    public void NewFontSizeResourcesAlsoRequireDocumentation() => StaTestHost.Run(() =>
    {
        ResourceDictionary tokens = LoadTokens();
        tokens["FontSize.SyncProbe"] = TypeScale.Body;

        Assert.Contains("Missing token 'FontSize.SyncProbe'.", FindErrors(ReadGuidelines(), tokens));
    });

    [Fact]
    public void ScratchCopyMissingAUiFontFamilyIsRejected() => StaTestHost.Run(() =>
    {
        string scratch = ReadGuidelines().Replace("Malgun Gothic", string.Empty, StringComparison.Ordinal);

        Assert.Contains("Missing UI font family 'Malgun Gothic'.", FindErrors(scratch, LoadTokens()));
    });

    private static List<string> FindErrors(string markdown, ResourceDictionary resources)
    {
        Dictionary<string, string> documented = ReadTokenTable(markdown);
        var errors = new List<string>();
        IEnumerable<string> fontKeys = resources.Keys.Cast<object>().OfType<string>()
            .Where(key => key.StartsWith("FontSize.", StringComparison.Ordinal));
        foreach (string key in RequiredTokens.Concat(fontKeys).Distinct(StringComparer.Ordinal))
            if (!documented.ContainsKey(key)) errors.Add($"Missing token '{key}'.");

        foreach ((string key, string value) in documented)
        {
            if (!resources.Contains(key))
            {
                errors.Add($"Unknown resource '{key}'.");
                continue;
            }

            string actual = FormatValue(resources[key]);
            if (!string.Equals(value, actual, StringComparison.Ordinal))
                errors.Add($"{key}: document '{value}', resolved resource '{actual}'.");
        }

        foreach (string source in ThemeFonts.Ui.Source.Split(','))
        {
            // Bundled font references are URI fragments; the reader needs the family name.
            string family = source[(source.LastIndexOf('#') + 1)..].Trim();
            if (!markdown.Contains(family, StringComparison.Ordinal)) errors.Add($"Missing UI font family '{family}'.");
        }
        return errors;
    }

    private static Dictionary<string, string> ReadTokenTable(string markdown)
    {
        int begin = markdown.IndexOf(TokensBegin, StringComparison.Ordinal);
        int end = markdown.IndexOf(TokensEnd, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "MASTER.md must contain the ordered tokens:begin / tokens:end markers.");
        Assert.Equal(-1, markdown.IndexOf(TokensBegin, begin + TokensBegin.Length, StringComparison.Ordinal));
        Assert.Equal(-1, markdown.IndexOf(TokensEnd, end + TokensEnd.Length, StringComparison.Ordinal));
        string[] rows = markdown[(begin + TokensBegin.Length)..end]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        Assert.True(rows.Length >= 3, "The token table must contain a header and at least one value.");
        Assert.Equal("| Token | Value |", rows[0]);
        Assert.Equal("| --- | --- |", rows[1]);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string row in rows.Skip(2))
        {
            string[] cells = row.Split('|', StringSplitOptions.TrimEntries);
            Assert.True(cells.Length == 4 && cells[0].Length == 0 && cells[3].Length == 0,
                $"Expected a two-column token row, got '{row}'.");
            string key = cells[1].Trim('`');
            string value = cells[2].Trim('`');
            Assert.False(string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value), $"Empty token cell in '{row}'.");
            Assert.True(result.TryAdd(key, value), $"Duplicate token '{key}'.");
        }
        return result;
    }

    private static string FormatValue(object value) => value switch
    {
        SolidColorBrush brush when brush.Opacity == 1 => FormatColor(brush.Color),
        Color color => FormatColor(color),
        double number => number.ToString(CultureInfo.InvariantCulture),
        CornerRadius radius when radius.TopLeft == radius.TopRight && radius.TopLeft == radius.BottomRight
            && radius.TopLeft == radius.BottomLeft => radius.TopLeft.ToString(CultureInfo.InvariantCulture),
        Duration duration when duration.HasTimeSpan => duration.TimeSpan.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms",
        _ => throw new InvalidOperationException($"Add an explicit token-table format for '{value.GetType().Name}'."),
    };

    private static string FormatColor(Color color) => color.A == byte.MaxValue
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static ResourceDictionary LoadTokens()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        return new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute),
        };
    }

    private static string ReadGuidelines()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx")))
                return File.ReadAllText(Path.Combine(directory.FullName, "design-system", "mycapture", "MASTER.md"));
        throw new DirectoryNotFoundException("The MyCapture repository root was not found.");
    }
}
