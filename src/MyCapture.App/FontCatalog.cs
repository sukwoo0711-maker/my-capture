using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace MyCapture.App;

/// <summary>The stored family name is independent of its localized label and resource URI.</summary>
internal sealed record FontChoice(string Value, string Label, bool IsMore = false)
{
    public FontFamily PreviewFamily => FontCatalog.Resolve(IsMore ? "Segoe UI" : Value);
    public override string ToString() => Label;
}

/// <summary>Shared font choices and rendering for settings, annotations and export.</summary>
internal static class FontCatalog
{
    private static readonly ConcurrentDictionary<string, FontFamily> Resolved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<FontChoice>>> Installed = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<FontChoice>>> Recommended = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<FontChoice>>> Available = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> Bundled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Maple Mono"] = "MapleMono",
        ["JetBrains Mono"] = "JetBrainsMono",
        ["D2Coding"] = "D2Coding",
        ["Pretendard"] = "Pretendard",
    };

    internal static FontFamily Resolve(string? name) => Resolved.GetOrAdd(
        string.IsNullOrWhiteSpace(name) ? "Malgun Gothic" : name.Trim(), CreateFamily);

    private static FontFamily CreateFamily(string name)
    {
        if (!Bundled.TryGetValue(name, out string? folder)) return new FontFamily(name);
        string canonical = Bundled.Keys.First(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        // The bundled files sit beside the app (see the project file). A copy missing them falls
        // back to an installed family of the same name rather than to a broken source.
        if (!Directory.Exists(Path.Combine(BundledFontRoot, folder))) return new FontFamily(canonical);
        string source = $"./{folder}/#{canonical}";
        if (canonical is "Maple Mono" or "JetBrains Mono")
            source += ", ./D2Coding/#D2Coding";
        return new FontFamily(new Uri(BundledFontRoot, UriKind.Absolute), source);
    }

    internal static string BundledFontRoot { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts") + Path.DirectorySeparatorChar;

    internal static IReadOnlyList<FontChoice> InstalledFonts(CultureInfo culture) => Installed.GetOrAdd(culture.Name, _ => new(() =>
        Fonts.SystemFontFamilies.Where(font => !string.IsNullOrWhiteSpace(font.Source))
            .DistinctBy(font => font.Source, StringComparer.OrdinalIgnoreCase)
            .Select(font => new FontChoice(font.Source, DisplayName(font, culture)))
            .OrderBy(font => font.Label, StringComparer.Create(culture, ignoreCase: true)).ToArray())).Value;

    private static string DisplayName(FontFamily family, CultureInfo culture)
    {
        for (CultureInfo candidate = culture; candidate.Name.Length > 0; candidate = candidate.Parent)
            if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage(candidate.IetfLanguageTag), out string? localized))
                return localized;
        if (culture.TwoLetterISOLanguageName == "ko")
        {
            if (family.Source.Equals("Malgun Gothic", StringComparison.OrdinalIgnoreCase)) return "맑은 고딕";
            if (family.Source.Equals("NanumGothicCoding", StringComparison.OrdinalIgnoreCase)) return "나눔고딕코딩";
        }
        return family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-US"), out string? english) ? english : family.Source;
    }

    internal static IReadOnlyList<FontChoice> RecommendedFonts(CultureInfo culture) => Recommended.GetOrAdd(culture.Name,
        _ => new(() => BuildRecommended(culture, InstalledFonts(culture)))).Value;

    // Expanding must not strand the user without the bundled families after choosing
    // a system font. All installed families remain available alongside the four resources.
    internal static IReadOnlyList<FontChoice> AvailableFonts(CultureInfo culture) => Available.GetOrAdd(culture.Name,
        _ => new(() => InstalledFonts(culture).Concat(Bundled.Keys.Select(name => new FontChoice(name, name)))
            .DistinctBy(font => font.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(font => font.Label, StringComparer.Create(culture, ignoreCase: true)).ToArray())).Value;

    // The supplied inventory also makes absence of optional families independently testable.
    internal static IReadOnlyList<FontChoice> BuildRecommended(CultureInfo culture, IReadOnlyList<FontChoice> installed)
    {
        var inventory = installed.ToDictionary(font => font.Value, StringComparer.OrdinalIgnoreCase);
        var result = new List<FontChoice>();
        void Add(string name, bool optional = false)
        {
            if (inventory.TryGetValue(name, out FontChoice? font)) result.Add(font);
            else if (!optional) result.Add(new FontChoice(name,
                culture.TwoLetterISOLanguageName == "ko" && name == "Malgun Gothic" ? "맑은 고딕" : name));
        }
        Add("Malgun Gothic");
        Add("Pretendard");
        Add("D2Coding");
        Add("Maple Mono");
        Add("JetBrains Mono");
        Add("Cascadia Mono", optional: true);
        Add("Consolas");
        Add("Segoe UI");
        foreach (string name in new[] { "Fira Code", "Cascadia Code", "Source Code Pro", "NanumGothicCoding", "Noto Sans KR" })
            Add(name, optional: true);
        using var language = UiText.UseLanguage(culture.Name);
        result.Add(new FontChoice(string.Empty, UiText.Get("EditorText_OtherInstalledFonts"), IsMore: true));
        return result.ToArray();
    }

    internal static IReadOnlyList<FontChoice> ChoicesFor(string? saved, IReadOnlyList<FontChoice> choices)
    {
        string name = saved?.Trim() ?? string.Empty;
        if (name.Length == 0) return choices;
        int match = -1;
        for (int index = 0; index < choices.Count; index++)
            if (!choices[index].IsMore && string.Equals(choices[index].Value, name, StringComparison.OrdinalIgnoreCase))
            { match = index; break; }
        if (match >= 0 && choices[match].Value == name) return choices;
        var result = new List<FontChoice>(choices.Count + 1);
        if (match < 0) result.Add(new FontChoice(name, name));
        for (int index = 0; index < choices.Count; index++)
            result.Add(index == match ? choices[index] with { Value = name } : choices[index]);
        return result;
    }

    internal static DataTemplate PreviewTemplate()
    {
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(FontChoice.Label)));
        label.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(FontChoice.PreviewFamily)));
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new DataTemplate { VisualTree = label };
    }
}
