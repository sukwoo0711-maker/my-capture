using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TypeScalePolicyTests
{
    private const string NumericLiteral = @"[+-]?(?:0[xX][\da-fA-F_]+|0[bB][01_]+|(?:\d[\d_]*(?:\.[\d_]*)?|\.[\d_]+)(?:[eE][+-]?[\d_]+)?)[dDfFmMuUlL]*(?![\w.])";
    private static readonly Regex Assignment = new(@"\b\w*FontSize\w*\s*(?:\{[^{}]*\}\s*)?=(?!=|>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumericSize = new(@"(?:^|[?:]|=>)\s*(?:(?:\(\s*(?:double|float|decimal|int)\s*\)|\()\s*)*(?<number>" + NumericLiteral + ")", RegexOptions.CultureInvariant);
    private static readonly Regex FormattedTextConstructor = new(@"\bnew\s+(?:global::)?(?:\w+\.)*FormattedText\s*\(");
    private static readonly Regex SetValueCall = new(@"\bSetValue\s*\(");
    private static readonly Regex NonCode = new("""
        (?sx)
        // [^\r\n]* | /\* .*? \*/
        | (?<raw>"{3,}) .*? \k<raw>
        | @" (?: [^"] | "" )* "
        | " (?: \\. | [^"\\] )* "
        | ' (?: \\. | [^'\\] )* '
        """);

    private static readonly Dictionary<string, double> UiSizes = new()
    {
        [nameof(TypeScale.Micro)] = 11,
        [nameof(TypeScale.Caption)] = 12,
        [nameof(TypeScale.Body)] = 14,
        [nameof(TypeScale.Control)] = 14,
        [nameof(TypeScale.BodyLarge)] = 16,
        [nameof(TypeScale.Subtitle)] = 20,
        [nameof(TypeScale.Title)] = 28,
        [nameof(TypeScale.Display)] = 28,
        [nameof(TypeScale.IconGlyph)] = 16,
    };

    [Fact]
    public void AppFontSizesComeFromTheSharedScale()
    {
        string root = RepositoryRoot();
        var failures = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "MyCapture.App"), "*", SearchOption.AllDirectories)
                     .Where(file => Path.GetExtension(file) is ".cs" or ".xaml"))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "Themes" or "Diagnostics" or "bin" or "obj")) continue;
            failures.AddRange(FindViolations(relative, File.ReadAllText(file)));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void UiRampHasExactlyTheApprovedStepsAndAliases()
    {
        FieldInfo[] fields = typeof(TypeScale).GetFields(BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(UiSizes.Keys.Order(), fields.Select(field => field.Name).Order());
        Assert.All(fields, field =>
        {
            Assert.True(field.IsLiteral, field.Name + " must remain a constant for default parameters and XAML.");
            Assert.Equal(UiSizes[field.Name], Assert.IsType<double>(field.GetRawConstantValue()));
        });
        Assert.Equal(new double[] { 11, 12, 14, 16, 20, 28 }, fields.Select(field => (double)field.GetRawConstantValue()!).Distinct().Order());
        Assert.Equal(TypeScale.Body, TypeScale.Control);
        Assert.Equal(TypeScale.Title, TypeScale.Display);
    }

    [Fact]
    public void EveryFontSizeResourceUsesItsSharedConstant()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement dictionary = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "MyCapture.App", "Themes", "Tokens.xaml")).Root!;
        XElement[] tokens = dictionary.Elements()
            .Where(element => ((string?)element.Attribute(x + "Key"))?.StartsWith("FontSize.", StringComparison.Ordinal) == true).ToArray();
        string[] expected = UiSizes.Keys.Where(name => name != nameof(TypeScale.IconGlyph)).Select(name => "FontSize." + name).Order().ToArray();
        Assert.Equal(expected, tokens.Select(element => (string)element.Attribute(x + "Key")!).Order());
        Assert.All(tokens, token =>
        {
            string member = ((string)token.Attribute(x + "Key")!)["FontSize.".Length..];
            Assert.Equal(x + "Static", token.Name);
            Assert.Equal("themes:TypeScale." + member, (string?)token.Attribute("Member"));
            Assert.True(string.IsNullOrWhiteSpace(token.Value));
        });
        XElement icon = Assert.Single(dictionary.Elements(), element => (string?)element.Attribute(x + "Key") == "Size.IconGlyph");
        Assert.Equal(x + "Static", icon.Name);
        Assert.Equal("themes:TypeScale.IconGlyph", (string?)icon.Attribute("Member"));
    }

    [Fact]
    public void CompiledResourcesResolveToTheApprovedSizes() => StaTestHost.Run(() =>
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml"),
        };
        foreach ((string member, double expected) in UiSizes)
        {
            string key = member == nameof(TypeScale.IconGlyph) ? "Size.IconGlyph" : "FontSize." + member;
            Assert.Equal(expected, Assert.IsType<double>(resources[key]));
        }
    });

    [Theory]
    [InlineData("MarkdownBody", 15)]
    [InlineData("MarkdownCode", 14)]
    [InlineData("MarkdownFallback", 13)]
    [InlineData("MarkdownRuleSpacer", 1)]
    [InlineData("PlainText", 16)]
    [InlineData("TableText", 14)]
    [InlineData("WallClockMinimum", 10)]
    [InlineData("WallClockMaximum", 28)]
    [InlineData("WallClockDefault", 13)]
    [InlineData("TextStyleGlyph", 18)]
    public void ExistingContentSizesArePreserved(string member, double expected)
    {
        FieldInfo? field = typeof(TypeScale.Content).GetField(member);
        Assert.NotNull(field);
        Assert.True(field.IsLiteral);
        Assert.Equal(expected, Assert.IsType<double>(field.GetRawConstantValue()));
    }

    [Theory]
    [InlineData("FontSize = 12;")]
    [InlineData("var label = new TextBlock { FontSize = 12 }; ")]
    [InlineData("x.FontSize = 13 * scale;")]
    [InlineData("const double PlainFontSize = 16;")]
    [InlineData("private double _fontSize = 14d;")]
    [InlineData("void Layout(double captionFontSize = 12) { }")]
    [InlineData("public double FontSize { get; set; } = 14;")]
    [InlineData("FontSize = (double)(12.5);")]
    [InlineData("FontSize = .5e1;")]
    [InlineData("FontSize = paused ? 12 : 13;")]
    [InlineData("FontSize = resource ?? 12;")]
    [InlineData("FontSize = TryFindResource(\"FontSize.Caption\") is double size ? size : 12;")]
    [InlineData("FontSize = level switch { 1 => 28, _ => 16 };")]
    [InlineData("label.SetValue(TextBlock.FontSizeProperty, 12);")]
    [InlineData("label.SetValue(Control.FontSizeProperty, (13d) * scale);")]
    [InlineData("new FormattedText(\"text, text\", culture, direction, new Typeface(family, style, weight, stretch), 12, brush, 1);")]
    [InlineData("new System.Windows.Media.FormattedText(\"text\", culture, direction, typeface, 1.2e1d, brush, 1);")]
    [InlineData("new FormattedText(emSize: 12, textToFormat: \"text\", culture: culture, flowDirection: direction, typeface: typeface, foreground: brush, pixelsPerDip: 1);")]
    public void ScannerRejectsEveryNumericCodePattern(string source)
    {
        string failure = Assert.Single(FindViolations("sample.cs", "// leading line\n" + source));
        Assert.StartsWith("sample.cs:2:", failure);
    }

    [Theory]
    [InlineData("<TextBlock FontSize=\"12\" />")]
    [InlineData("<Panel BaseTitleFontSize=\"12\" />")]
    [InlineData("<Setter Property=\"TextElement.FontSize\" Value=\"12\" />")]
    [InlineData("<TextBlock.FontSize>12</TextBlock.FontSize>")]
    public void ScannerRejectsNumericXamlSizes(string source)
    {
        string failure = Assert.Single(FindViolations("sample.xaml", "\n" + source));
        Assert.StartsWith("sample.xaml:2:", failure);
    }

    [Theory]
    [InlineData("FontSize = TypeScale.Body * _textScale;")]
    [InlineData("FontSize = paused ? TypeScale.Caption : TypeScale.Body;")]
    [InlineData("FontSize = level switch { 1 => TypeScale.Title, _ => TypeScale.Body };")]
    [InlineData("// FontSize = 12;\n/* FontSize = 13; */ var example = \"FontSize = 14;\";")]
    [InlineData("var example = @\"FontSize = 12; \"\"quoted\"\"\";")]
    [InlineData("label.SetValue(TextBlock.FontSizeProperty, TypeScale.Caption);")]
    [InlineData("new FormattedText(\"text\", culture, direction, new Typeface(family, style, weight, stretch), TypeScale.Body, brush, 1);")]
    [InlineData("FontSize = Math.Max(1, annotation.FontSize * dipPerPixel);")]
    public void ScannerAllowsSharedSizesAndDerivedDocumentGeometry(string source) =>
        Assert.Empty(FindViolations("sample.cs", source));

    [Fact]
    public void ScannerKeepsMultilineLiteralLocationsAndSkipsRawStringExamples()
    {
        const string source = "FontSize =\n    /* size */\n    12;";
        Assert.StartsWith("sample.cs:3:", Assert.Single(FindViolations("sample.cs", source)));
        Assert.Empty(FindViolations("sample.cs", "var example = \"\"\"FontSize = 12;\"\"\";"));
        Assert.Empty(FindViolations("sample.xaml", "<TextBlock FontSize=\"{DynamicResource FontSize.Body}\" />"));
    }

    private static IEnumerable<string> FindViolations(string file, string source)
    {
        if (file.EndsWith(".xaml", StringComparison.Ordinal))
        {
            XElement root = XDocument.Parse(source, LoadOptions.SetLineInfo).Root!;
            foreach (XElement element in root.DescendantsAndSelf())
            {
                foreach (XAttribute attribute in element.Attributes())
                    if (attribute.Name.LocalName.Contains("FontSize", StringComparison.OrdinalIgnoreCase) && IsXamlNumber(attribute.Value))
                        yield return $"{file}:{((IXmlLineInfo)attribute).LineNumber}: numeric font size: {attribute.Value}";
                if (element.Name.LocalName == "Setter" && ((string?)element.Attribute("Property"))?.Contains("FontSize", StringComparison.OrdinalIgnoreCase) == true
                    && element.Attribute("Value") is { } value && IsXamlNumber(value.Value))
                    yield return $"{file}:{((IXmlLineInfo)value).LineNumber}: numeric font size: {value.Value}";
                if (element.Name.LocalName.Contains("FontSize", StringComparison.OrdinalIgnoreCase) && IsXamlNumber(element.Value))
                    yield return $"{file}:{((IXmlLineInfo)element).LineNumber}: numeric font size: {element.Value}";
            }
            yield break;
        }

        // Keep offsets and newlines intact; examples in comments and strings are not UI code.
        string code = NonCode.Replace(source, match => new string(match.Value.Select(character => character is '\r' or '\n' ? character : ' ').ToArray()));
        foreach (Match assignment in Assignment.Matches(code))
        {
            int start = assignment.Index + assignment.Length;
            if (NumericOffset(code, start, ExpressionEnd(code, start)) is { } offset)
                yield return Failure(file, source, offset);
        }
        foreach (Match call in SetValueCall.Matches(code))
        {
            (int Start, int End)[] arguments = Arguments(code, call.Index + call.Length).ToArray();
            if (arguments.Length >= 2 && Regex.IsMatch(code[arguments[0].Start..arguments[0].End], @"\b\w*FontSizeProperty\b", RegexOptions.IgnoreCase)
                && NumericOffset(code, arguments[1].Start, arguments[1].End) is { } offset)
                yield return Failure(file, source, offset);
        }
        foreach (Match constructor in FormattedTextConstructor.Matches(code))
        {
            (int Start, int End)[] arguments = Arguments(code, constructor.Index + constructor.Length).ToArray();
            for (int index = 0; index < arguments.Length; index++)
            {
                (int start, int end) = arguments[index];
                Match named = Regex.Match(code[start..end], @"^\s*(?<name>\w+)\s*:");
                if (named.Success ? named.Groups["name"].Value != "emSize" : index != 4) continue;
                if (named.Success) start += named.Length;
                if (NumericOffset(code, start, end) is { } offset) yield return Failure(file, source, offset);
            }
        }
    }

    private static bool IsXamlNumber(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static int? NumericOffset(string code, int start, int end)
    {
        Match number = NumericSize.Match(code[start..end]);
        return number.Success ? start + number.Groups["number"].Index : null;
    }

    private static string Failure(string file, string source, int offset) =>
        $"{file}:{1 + source[..offset].Count(character => character == '\n')}: numeric font size must use TypeScale";

    private static IEnumerable<(int Start, int End)> Arguments(string code, int start)
    {
        while (start < code.Length)
        {
            int end = ExpressionEnd(code, start);
            yield return (start, end);
            if (end == code.Length || code[end] != ',') yield break;
            start = end + 1;
        }
    }

    private static int ExpressionEnd(string code, int start)
    {
        int depth = 0;
        for (int index = start; index < code.Length; index++)
        {
            char character = code[index];
            if (depth == 0 && character is ',' or ';' or ')' or ']' or '}') return index;
            if (character is '(' or '[' or '{') depth++;
            else if (character is ')' or ']' or '}') depth--;
        }
        return code.Length;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout is required for the type scale policy audit.");
    }
}
