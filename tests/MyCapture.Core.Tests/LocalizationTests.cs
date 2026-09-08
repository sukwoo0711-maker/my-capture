using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("", "ko-KR", "ko-KR")]
    [InlineData("", "en-US", "en")]
    [InlineData("", "ja-JP", "en")]
    [InlineData("en-US", "ko-KR", "en")]
    [InlineData("ko-KR", "en-US", "ko-KR")]
    [InlineData("unsupported", "ko-KR", "en")]
    public void PreferenceUsesDisplayLanguageAndStableFallback(string preference, string system, string expected)
    {
        var original = CultureInfo.GetCultureInfo(system);
        Assert.Equal(expected, UiText.ResolveCulture(preference, original).Name);
        Assert.Equal("en", UiText.ResolveCulture("en-US", original).Name);
        Assert.Equal(system.StartsWith("ko", StringComparison.Ordinal) ? "ko-KR" : "en", UiText.ResolveCulture("", original).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void DraftCloneAndDiskRoundTripPreserveLanguageWithoutMutatingUserText(string language)
    {
        using var workspace = new TempWorkspace();
        var original = new AppSettings();
        var draft = new SettingsDraft(original) { Language = language, FileNamePattern = "my_기록_{yyyyMMdd}" };
        Assert.Empty(original.General.Language);
        AppSettings next = draft.ToAppSettings().DeepClone();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        store.Save(next);
        AppSettings loaded = store.Load();
        Assert.Equal(language, loaded.General.Language);
        Assert.Equal("my_기록_{yyyyMMdd}", loaded.Export.FileNamePattern);
        Assert.Equal(language, new SettingsDraft(loaded).Language);
    }

    [Fact]
    public async Task ScopedLanguageFlowsAcrossAwaitAndDoesNotLeakAcrossConcurrentContexts()
    {
        string original = UiText.Culture.Name;
        await Task.WhenAll(Check("en-US", "Display language"), Check("ko-KR", "표시 언어"));
        Assert.Equal(original, UiText.Culture.Name);
        static async Task Check(string language, string caption)
        {
            using (UiText.UseLanguage(language))
            {
                await Task.Yield();
                Assert.Equal(caption, UiText.Get("Settings.Language"));
                using (UiText.UseLanguage(language == "ko-KR" ? "en-US" : "ko-KR")) { await Task.Yield(); }
                Assert.Equal(caption, UiText.Get("Settings.Language"));
            }
        }
    }

    [Fact]
    public void BothCatalogsContainEveryKeyAndMatchingValidCompositeFormats()
    {
        var manager = new ResourceManager("MyCapture.Core.Localization.Strings", typeof(UiText).Assembly);
        Dictionary<string, string> korean = Read("ko-KR");
        Dictionary<string, string> english = Read("en");
        Assert.True(korean.Count >= 724);
        Assert.Equal(korean.Keys.Order(), english.Keys.Order());
        foreach ((string key, string source) in korean)
        {
            string translation = english[key];
            Assert.False(string.IsNullOrWhiteSpace(source), key);
            Assert.False(string.IsNullOrWhiteSpace(translation), key);
            Assert.DoesNotMatch("[가-힣]", translation);
            string[] sourceItems = Items(source);
            string[] translatedItems = Items(translation);
            Assert.Equal(sourceItems, translatedItems);
            if (sourceItems.Length == 0) continue; // Some plain hints intentionally show filename tokens.
            CompositeFormat first = CompositeFormat.Parse(source);
            CompositeFormat second = CompositeFormat.Parse(translation);
            Assert.Equal(first.MinimumArgumentCount, second.MinimumArgumentCount);
            object[] arguments = Enumerable.Repeat<object>(new FormatProbe(), first.MinimumArgumentCount).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, first, arguments);
            _ = string.Format(CultureInfo.InvariantCulture, second, arguments);
        }
        Dictionary<string, string> Read(string language) => manager.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!
            .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        static string[] Items(string value) => Regex.Matches(value, @"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}(?!\})")
            .Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class FormatProbe : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => "value";
    }

    [Fact]
    public void EveryExplicitSourceResourceReferenceResolvesInBothLanguages()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "MyCapture.Core", "MyCapture.Core.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".xaml", StringComparison.Ordinal))
                && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "UiText\\.(?:Get|Format)\\(\"([^\"]+)\"|\\{loc:Text ([^}]+)\\}"))
                keys.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        }
        Assert.True(keys.Count >= 724);
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            using var scope = UiText.UseLanguage(language);
            foreach (string key in keys) Assert.False(string.IsNullOrWhiteSpace(UiText.Get(key)), key);
        }
    }
}
