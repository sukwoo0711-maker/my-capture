using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Warning and success words take <c>Text.Warning</c>/<c>Text.Success</c>, which reach 4.5:1 on the
/// surfaces that carry them; <c>State.Warning</c>/<c>State.Success</c> stay for glyphs, fills and
/// outlines, where 3:1 is enough. In the light themes the State hues reach only 2.9-4.1:1 under
/// words, so this audit keeps them off every foreground.
/// </summary>
public sealed class StatusTextColorPolicyTests
{
    private static readonly Regex StatusKey = new(@"\bState\.(?:Warning|Success)\b");
    // ForegroundProperty, then the key before the statement ends: SetResourceReference and Setter.
    private static readonly Regex ForegroundProperty = new(@"ForegroundProperty\s*,[^;]*?""State\.(?:Warning|Success)""");
    // Foreground = <expression up to the next , or ;> "State.X": assignments and initialisers.
    private static readonly Regex ForegroundAssignment = new(@"\bForeground\s*=[^,;=]*""State\.(?:Warning|Success)""");

    [Fact]
    public void NoWordsAreColouredWithAStatusGlyphHue()
    {
        string root = RepositoryRoot();
        var failures = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
                     .Where(file => Path.GetExtension(file) is ".cs" or ".xaml")
                     .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            failures.AddRange(FindViolations(relative, File.ReadAllText(file)));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("Sample.cs", "warning.SetResourceReference(TextBlock.ForegroundProperty, \"State.Warning\");")]
    [InlineData("Sample.cs", "_editCount.SetResourceReference(TextBlock.ForegroundProperty,\"State.Success\");")]
    [InlineData("Sample.cs", "style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(\"State.Warning\")));")]
    [InlineData("Sample.cs", "_statusText.Foreground = TryBrush(\"State.Success\", SuccessFallback);")]
    [InlineData("Sample.cs", "Foreground = TryBrush(\"State.Warning\", Color.FromRgb(0xF5, 0xB9, 0x42)),")]
    [InlineData("Sample.cs", "label.Foreground = (Brush)FindResource(\"State.Success\");")]
    [InlineData("Sample.xaml", "<TextBlock Foreground=\"{DynamicResource State.Success}\" />")]
    [InlineData("Sample.xaml", "<TextBlock Foreground=\"{StaticResource State.Warning}\" />")]
    [InlineData("Sample.xaml", "<Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\" Value=\"{DynamicResource State.Warning}\" /></Style>")]
    [InlineData("Sample.xaml", "<Style TargetType=\"Border\"><Setter Property=\"TextBlock.Foreground\" Value=\"{DynamicResource State.Success}\" /></Style>")]
    [InlineData("Sample.xaml", "<Style TargetType=\"Label\"><Setter Property=\"Foreground\"><Setter.Value><DynamicResource ResourceKey=\"State.Success\" /></Setter.Value></Setter></Style>")]
    public void AStatusHueOnWordsIsFlagged(string file, string source)
    {
        string failure = Assert.Single(FindViolations(file, Sample(file, source)));
        Assert.Contains($"{file}:", failure);
        Assert.Matches(StatusKey, failure);
    }

    [Theory]
    [InlineData("Sample.cs", "icon.SetResourceReference(SymbolIcon.StrokeProperty, \"State.Success\");")]
    [InlineData("Sample.cs", "edited.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(\"State.Success\")));")]
    [InlineData("Sample.cs", "tint.SetResourceReference(Border.BackgroundProperty, \"State.Success\");")]
    [InlineData("Sample.cs", "Fill = TryBrush(\"State.Warning\", Colors.Orange),")]
    [InlineData("Sample.cs", "_recordingIndicator.Stroke = TryBrush(\"State.Success\", SuccessFallback);")]
    [InlineData("Sample.cs", "Foreground = Brushes.White, Stroke = TryBrush(\"State.Success\", SuccessFallback),")]
    [InlineData("Sample.cs", "warning.SetResourceReference(TextBlock.ForegroundProperty, \"Text.Warning\");")]
    [InlineData("Sample.cs", "_statusText.Foreground = TryBrush(\"Text.Success\", SuccessFallback);")]
    [InlineData("Sample.cs", "// _statusText.Foreground = TryBrush(\"State.Success\", SuccessFallback);")]
    [InlineData("Sample.xaml", "<Path Stroke=\"{DynamicResource State.Warning}\" />")]
    [InlineData("Sample.xaml", "<Border BorderBrush=\"{DynamicResource State.Success}\" Background=\"{DynamicResource State.Warning}\" />")]
    [InlineData("Sample.xaml", "<Style TargetType=\"Border\"><Setter Property=\"BorderBrush\" Value=\"{DynamicResource State.Success}\" /></Style>")]
    [InlineData("Sample.xaml", "<Ellipse Fill=\"{DynamicResource State.Success}\" />")]
    [InlineData("Sample.xaml", "<TextBlock Foreground=\"{DynamicResource Text.Warning}\" />")]
    [InlineData("Sample.xaml", "<Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\" Value=\"{DynamicResource Text.Success}\" /></Style>")]
    public void AStatusHueOnGlyphsFillsAndOutlinesOrATextHueOnWordsIsAllowed(string file, string source) =>
        Assert.Empty(FindViolations(file, Sample(file, source)));

    private static IEnumerable<string> FindViolations(string file, string source)
    {
        if (file.EndsWith(".xaml", StringComparison.Ordinal))
        {
            XDocument document = XDocument.Parse(source, LoadOptions.SetLineInfo);
            foreach (XAttribute attribute in document.Descendants().Attributes())
            {
                if (IsForeground(attribute.Name.LocalName) && StatusKey.IsMatch(attribute.Value))
                    yield return $"{file}:{((IXmlLineInfo)attribute).LineNumber}: status hue on words: {attribute.Value}";
            }
            foreach (XElement setter in document.Descendants().Where(element => element.Name.LocalName == "Setter"))
            {
                if (setter.Attribute("Property")?.Value is not { } property || !IsForeground(property)) continue;
                XAttribute? value = setter.DescendantsAndSelf().Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName != "Property" && StatusKey.IsMatch(attribute.Value));
                if (value is not null)
                    yield return $"{file}:{((IXmlLineInfo)value).LineNumber}: status hue on words: {property} = {value.Value}";
            }
            yield break;
        }

        string[] lines = source.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (ForegroundProperty.IsMatch(line) || ForegroundAssignment.IsMatch(line))
                yield return $"{file}:{index + 1}: status hue on words: {line}";
        }
    }

    // Foreground, or an attached one such as TextBlock.Foreground and TextElement.Foreground.
    private static bool IsForeground(string name) =>
        name == "Foreground" || name.EndsWith(".Foreground", StringComparison.Ordinal);

    private static string Sample(string file, string body) => file.EndsWith(".xaml", StringComparison.Ordinal)
        ? "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
          + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" + body + "\n</ResourceDictionary>"
        : body;

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout is required for the status text colour audit.");
    }
}
