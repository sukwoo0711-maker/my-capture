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
    [InlineData("Source.Open", "출처 열기", "Open source")]
    [InlineData("Source.AppMissing", "출처 앱을 찾을 수 없습니다.", "The source app could not be found.")]
    [InlineData("Source.AppOpenFailed", "출처 앱을 열 수 없습니다.", "The source app could not be opened.")]
    [InlineData("ExportOptions.SourceUrl", "출처 주소 포함", "Include source URL")]
    [InlineData("Welcome_PinClipboardHint", "복사한 표와 코드는 {0} 키로 핀에 띄웁니다.", "Pin copied tables and code with {0}.")]
    [InlineData("Library.Organize.TagsHint", "쉼표로 구분해 최대 12개, 태그당 32자까지 입력하십시오. 제목과 태그를 붙여도 자동 삭제는 막지 못합니다. 중요한 캡처는 보관함에 넣으십시오.", "Separate tags with commas. Up to 12 tags, 32 characters each. Titles and tags do not prevent automatic deletion. Move important captures to Kept.")]
    public void SettingsAndSourceCopyMatchesTheReviewedMeaning(string key, string korean, string english)
    {
        using (UiText.UseLanguage("ko-KR")) Assert.Equal(korean, UiText.Get(key));
        using (UiText.UseLanguage("en-US")) Assert.Equal(english, UiText.Get(key));
    }

    [Fact]
    public void ArchiveHintQuotesTheCurrentSourceAddressOption()
    {
        using var scope = UiText.UseLanguage("ko-KR");
        string hint = UiText.Get("Library.Archive.Hint");
        Assert.Contains("PNG의 출처 주소는", hint, StringComparison.Ordinal);
        Assert.Contains("'" + UiText.Get("ExportOptions.SourceUrl") + "'", hint, StringComparison.Ordinal);
        Assert.Contains("별도 원본 이미지", hint, StringComparison.Ordinal);
        Assert.Equal("주소 복사", UiText.Get("Source.CopyUrl"));
    }

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
        // A {n:plural:one|other} selector picks an English noun for a count that the item {n}
        // already places, so it is not an argument placement of its own.
        static string[] Items(string value) => Regex.Matches(value, @"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}(?!\})")
            .Select(m => m.Value).Where(item => !item.Contains(":plural:", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void EnglishCountsUseSingularForOneAndKoreanStaysUnchanged()
    {
        using (UiText.UseLanguage("en-US"))
        {
            Assert.Equal("Move 1 selected item to the Recycle Bin?", UiText.Format("LibrarySelection_DeleteConfirm", 1));
            Assert.Equal("Move 3 selected items to the Recycle Bin?", UiText.Format("LibrarySelection_DeleteConfirm", 3));
            Assert.Equal("Move 0 selected items to the Recycle Bin?", UiText.Format("LibrarySelection_DeleteConfirm", 0));
            Assert.Equal("Removed 1 item from the library · Kept 2 unavailable or in-use items",
                UiText.Format("LibrarySelection_Removed", 1, 2));
            Assert.Equal("Removed 2 items from the library · Kept 1 unavailable or in-use item",
                UiText.Format("LibrarySelection_Removed", 2, 1));
            Assert.Equal("1,234 items", UiText.Format("Text_B6D9CAB382C8", 1234));
            Assert.Equal("1 item", UiText.Format("Text_B6D9CAB382C8", 1));
            Assert.Equal("Auto-delete in 1 day 2 hours", UiText.Format("Gallery.RetentionDaysHours", 1, 2));
            Assert.Equal("Auto-delete in 2 days 1 hour", UiText.Format("Gallery.RetentionDaysHours", 2, 1));
        }

        using (UiText.UseLanguage("ko-KR"))
        {
            Assert.Equal("선택한 1개 항목을 휴지통으로 이동하시겠습니까?", UiText.Format("LibrarySelection_DeleteConfirm", 1));
            Assert.Equal("자동 삭제까지 1일 2시간", UiText.Format("Gallery.RetentionDaysHours", 1, 2));
        }

        // Every selector names exactly two forms and refers to an argument the string places.
        var manager = new ResourceManager("MyCapture.Core.Localization.Strings", typeof(UiText).Assembly);
        foreach (DictionaryEntry entry in manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)!)
        {
            string value = (string)entry.Value!;
            foreach (Match selector in Regex.Matches(value, @"\{(\d+):plural:([^{}|]+)\|([^{}|]+)\}"))
                Assert.Contains("{" + selector.Groups[1].Value, value.Replace(selector.Value, string.Empty), StringComparison.Ordinal);
            Assert.Equal(Regex.Matches(value, ":plural:").Count, Regex.Matches(value, @"\{\d+:plural:[^{}|]+\|[^{}|]+\}").Count);
        }

        // Only UiText.Format understands the selector; a string read with Get and formatted
        // elsewhere would show "plural:" literally.
        string[] plural = manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)!.Cast<DictionaryEntry>()
            .Where(entry => ((string)entry.Value!).Contains(":plural:", StringComparison.Ordinal))
            .Select(entry => (string)entry.Key).ToArray();
        Assert.NotEmpty(plural);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "MyCapture.Core", "MyCapture.Core.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            string text = File.ReadAllText(file);
            foreach (string key in plural)
                Assert.DoesNotContain($"UiText.Get(\"{key}\")", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Windows says "고정" for taskbar and Start pins, so the floating image is a "핀" in Korean and a
    /// "pin" in English. "고정" stays for other meanings such as fixed-size capture.
    /// </summary>
    [Fact]
    public void FloatingImageIsCalledAPinInBothLanguages()
    {
        var manager = new ResourceManager("MyCapture.Core.Localization.Strings", typeof(UiText).Assembly);
        foreach (string value in Values("ko-KR"))
            Assert.DoesNotMatch("화면 고정|화면에 고정|고정 창|고정 이미지|고정한 이미지", value);
        foreach (string value in Values("en"))
            Assert.DoesNotMatch("(?i)screen pin|pinned (window|image)", value);
        using (UiText.UseLanguage("ko-KR"))
        {
            Assert.Equal("핀", UiText.Get("Text_33EFD44366A7"));
            Assert.Equal("핀으로 띄우기", UiText.Get("Gallery.Float"));
            Assert.Equal("닫은 핀 다시 열기", UiText.Get("Onboard_TrayReopenPin"));
        }
        using (UiText.UseLanguage("en-US"))
        {
            Assert.Equal("Pins", UiText.Get("Text_33EFD44366A7"));
            Assert.Equal("Pin to screen", UiText.Get("Gallery.Float"));
        }
        IEnumerable<string> Values(string language) => manager.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!
            .Cast<DictionaryEntry>().Select(entry => (string)entry.Value!);
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
