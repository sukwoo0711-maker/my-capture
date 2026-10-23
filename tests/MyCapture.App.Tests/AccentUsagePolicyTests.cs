using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AccentUsagePolicyTests
{
    private static readonly Regex AccentKey = new(@"\b(?:Accent\.(?:Default|Hover|Pressed|Subtle|Indicator|Foreground|Cool|Gradient)|Border\.Accent|Text\.OnAccent|Pin\.Frame|Recording\.Live)\b");
    // These files define palettes and their system-contrast mappings, rather than UI consumers.
    private static readonly HashSet<string> PaletteFiles =
    [
        "src/MyCapture.App/Themes/Tokens.xaml", "src/MyCapture.App/Themes/ThemeService.cs",
        "src/MyCapture.App/Themes/WorkspaceTheme.cs", "src/MyCapture.Core/Themes/AppAccent.cs",
        "src/MyCapture.Core/Themes/AppTheme.cs", "src/MyCapture.Core/Themes/HighContrastPalette.cs",
    ];

    [Fact]
    public void AccentConsumersAreLimitedToPrimaryActionsPinFramesRecordingAndUserArtwork()
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
        string stage = Path.Combine(root, "build", "tools", "TutorialVideoGenerator", "Stage.cs");
        failures.AddRange(FindViolations("build/tools/TutorialVideoGenerator/Stage.cs", File.ReadAllText(stage)));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("Accent.Default")]
    [InlineData("Accent.Hover")]
    [InlineData("Accent.Pressed")]
    [InlineData("Accent.Subtle")]
    [InlineData("Accent.Indicator")]
    [InlineData("Accent.Foreground")]
    [InlineData("Accent.Cool")]
    [InlineData("Border.Accent")]
    [InlineData("Text.OnAccent")]
    [InlineData("Pin.Frame")]
    [InlineData("Recording.Live")]
    public void SharedThemeFileDoesNotAllowAnAccentInAnUnrelatedStyle(string key)
    {
        string source = $$"""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="Button.Primary"><Setter Property="Background" Value="{DynamicResource Accent.Default}" /></Style>
              <Style x:Key="Unexpected.Selection"><Setter Property="Background" Value="{DynamicResource {{key}}}" /></Style>
            </ResourceDictionary>
            """;
        string failure = Assert.Single(FindViolations("src/MyCapture.App/Themes/Controls.xaml", source));
        Assert.Contains(":3:", failure);
        Assert.Contains(key, failure);
    }

    [Theory]
    [InlineData("Brushes.MediumPurple")]
    [InlineData("Color.FromRgb(0x7C, 0x6C, 0xFF)")]
    [InlineData("\"#A390FF\"")]
    [InlineData("\"#FFE5483B\"")]
    public void LiteralBrandColorsCannotReappearAsSelectionFallbacks(string expression) =>
        Assert.Single(FindViolations("src/MyCapture.App/Unexpected.cs", "var selection = " + expression + ";"));

    private static IEnumerable<string> FindViolations(string file, string source)
    {
        if (PaletteFiles.Contains(file)) yield break;
        if (file.EndsWith(".xaml", StringComparison.Ordinal))
        {
            XDocument document = XDocument.Parse(source, LoadOptions.SetLineInfo);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (XAttribute attribute in document.Descendants().Attributes())
            {
                string? style = attribute.Parent!.AncestorsAndSelf().FirstOrDefault(e => e.Name.LocalName == "Style")?.Attribute(x + "Key")?.Value;
                bool primary = file == "src/MyCapture.App/Themes/Controls.xaml" && style is "Button.Primary" or "Icon.Symbol.OnAccent";
                bool brand = file == "src/MyCapture.App/Themes/Controls.xaml" && attribute.Parent.AncestorsAndSelf()
                    .Any(e => ((string?)e.Attribute(x + "Key")) is "Brand.Mark" or "Brand.Mark16");
                if (AccentKey.IsMatch(attribute.Value) && !primary)
                    yield return $"{file}:{((IXmlLineInfo)attribute).LineNumber}: accent outside primary style: {attribute.Value}";
                else if (HasBrandLiteral(attribute.Value) && !brand)
                    yield return $"{file}:{((IXmlLineInfo)attribute).LineNumber}: literal brand color outside brand mark: {attribute.Value}";
            }
            yield break;
        }

        string[] lines = source.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            bool accent = AccentKey.IsMatch(line);
            bool literal = HasBrandLiteral(line);
            if (!accent && !literal) continue;
            // Error/warning semantics are explicitly outside the brand-accent restriction.
            if (!accent && Regex.IsMatch(line, "\"State\\.(?:Danger|DangerHover|Warning)\"")) continue;
            bool allowed = file switch
            {
                // A floating MyCapture pin uses its outline as brand identity over other apps.
                "src/MyCapture.App/Pinning/PinFrameColor.cs" => line == "internal const string AccentKey = \"Pin.Frame\";",
                // Explicit user-selected export background: output artwork, not chrome.
                "src/MyCapture.App/Editing/ImageReductionExportDialog.cs" => line == "Color accent = (TryFindResource(\"Accent.Default\") as SolidColorBrush)?.Color ?? Colors.SteelBlue;",
                // The one recording red: region outline and the compact bar's live dot. Other
                // accent usages in this window still fail.
                "src/MyCapture.App/Recording/RecordingControlWindow.cs" => line is "BorderBrush = TryBrush(\"Recording.Live\", Colors.OrangeRed),"
                    or ": TryBrush(\"Recording.Live\", Colors.OrangeRed);"
                    or "_recordingIndicator.SetResourceReference(SymbolIcon.FillProperty, _paused ? \"Text.Primary\" : \"Recording.Live\");",
                // The recording region selector's cue, the same red as the frame that follows.
                "src/MyCapture.App/Capture/RecordingSelectionCue.cs" => line == "ThemeColor live = ThemeCatalog.ColorsFor(theme)[\"Recording.Live\"];",
                // Diagnostic contrast role classification does not paint a control.
                "src/MyCapture.App/Diagnostics/UxReviewSelfTest.cs" => line == "|| brushKey is \"Text.OnAccent\" or \"Text.Badge\"",
                // User-editable drawing colors and the capture-guide color preference.
                "src/MyCapture.App/Editing/AnnotationEditorControl.PropertyBar.cs" or
                "src/MyCapture.App/Editing/AnnotationEditorController.cs" or
                "src/MyCapture.Core/Annotations/PenAnnotation.cs" or
                "src/MyCapture.Core/Annotations/PolylineAnnotation.cs" or
                "src/MyCapture.Core/Annotations/ShapeAnnotation.cs" or
                "src/MyCapture.Core/Annotations/TextAnnotation.cs" or
                "src/MyCapture.Core/Settings/AppSettings.cs" or
                "src/MyCapture.Core/Settings/CaptureGuideColorValue.cs" => !accent,
                _ => false,
            };
            if (!allowed) yield return $"{file}:{index + 1}: {(accent ? "accent token" : "literal brand color")} outside allow-list: {line}";
        }
    }

    private static bool HasBrandLiteral(string text)
    {
        if (Regex.IsMatch(text, @"\b(?:Brushes|Colors)\.(?:Red|Violet|Purple|MediumPurple|Crimson|Tomato|IndianRed|OrangeRed)\b")) return true;
        foreach (Match match in Regex.Matches(text, @"#(?:[\da-fA-F]{8}|[\da-fA-F]{6})\b"))
        {
            string hex = match.Value[^6..];
            if (IsRedOrViolet(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16))) return true;
        }
        foreach (Match match in Regex.Matches(text, @"(?:FromRgb|Rgb)\(\s*0x([\da-fA-F]{2}),\s*0x([\da-fA-F]{2}),\s*0x([\da-fA-F]{2})\s*\)"))
            if (IsRedOrViolet(Convert.ToInt32(match.Groups[1].Value, 16), Convert.ToInt32(match.Groups[2].Value, 16), Convert.ToInt32(match.Groups[3].Value, 16))) return true;
        return false;
    }

    private static bool IsRedOrViolet(int r, int g, int b) =>
        (r >= 170 && r - g >= 55 && r - b >= 35 && g - b <= 45) ||
        (b >= 170 && b - g >= 45 && r - g >= 8 && r >= 90);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout is required for the accent policy audit.");
    }
}
