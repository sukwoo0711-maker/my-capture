using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Microsoft.Extensions.Logging;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Library failures say why and reach the log; boundary text follows the display language.</summary>
public sealed class LibraryFailureCauseTests
{
    [Fact]
    public void WithCauseAddsAStorageCauseButNeverRepeatsTheGenericFallback()
    {
        using var language = UiText.UseLanguage("ko-KR");
        string general = UiText.Get("Library.Import.Failed");
        var diskFull = new IOException("full", UserFacingError.HandleDiskFullHResult);
        Assert.Equal(general + " " + UiText.Get("Dialog.DiskFullUnknownDrive"), ThemedDialog.WithCause(general, diskFull));
        Assert.Equal(general + " " + UiText.Get("Dialog.Access"), ThemedDialog.WithCause(general, new UnauthorizedAccessException("x")));
        Assert.Equal(general, ThemedDialog.WithCause(general, new IOException("unclassified")));
        Assert.Equal(general, ThemedDialog.WithCause(general, new InvalidOperationException("odd")));
    }

    [Fact]
    public void SavedSearchSaveFailureIsLoggedAndNamesTheCause() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var log = new FailureLog();
        var raw = new IOException("full", UserFacingError.HandleDiskFullHResult);
        var dialog = new GallerySavedSearchDialog("search", _ => Task.FromException(raw), log);
        Button apply = Logical(dialog).OfType<Button>().Single(button => button.IsDefault);
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        string expected = UiText.Get("Library.SavedSearch.Failed") + " " + UiText.Get("Dialog.DiskFullUnknownDrive");
        // Show() writes the block's own Text with word joiners in it; the attached property runs the other way.
        Assert.Contains(Logical(dialog).OfType<TextBlock>(), text => KoreanWordWrap.Strip(text.Text) == expected);
        Assert.Same(raw, Assert.Single(log.Errors));
    });

    [Theory]
    [InlineData(false, "MyCapture 시작 중")]
    [InlineData(true, "MyCapture가 시작하는 중입니다.\r\n잠시 뒤 라이브러리가 열립니다.")]
    public void LaunchNoticeFollowsTheResolvedLanguage(bool secondary, string korean)
    {
        Assert.Equal(korean, NativeLaunchSurface.MessageFor(secondary, CultureInfo.GetCultureInfo("ko-KR")));
        string english = NativeLaunchSurface.MessageFor(secondary, UiText.ResolveCulture("en", CultureInfo.GetCultureInfo("ko-KR")));
        Assert.DoesNotMatch(new Regex("[가-힣]"), english);
        Assert.StartsWith("MyCapture is starting", english);
    }

    [Fact]
    public void EnglishMarkdownPinNotesAndMermaidFailuresContainNoKorean() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en");
        const string source = "```mermaid\r\nflowchart TD\r\n A --> B\r\n```\r\n```mermaid\r\nbroken\r\n```";
        FlowDocument pending = ClipboardMarkdownRenderer.CreateDocument(source);
        string pendingText = new TextRange(pending.ContentStart, pending.ContentEnd).Text;
        Assert.Contains(UiText.Get("Pin_MarkdownDrawing"), pendingText);
        var results = new Dictionary<int, MermaidRenderResult>
        {
            [0] = new(null, UiText.Get("Pin_MermaidSyntax")),
            [1] = new(null, UiText.Get("Pin_MermaidUnavailable")),
        };
        FlowDocument failed = ClipboardMarkdownRenderer.CreateDocument(source, results);
        string failedText = new TextRange(failed.ContentStart, failed.ContentEnd).Text;
        Assert.Contains(UiText.Get("Pin_MarkdownDiagramFallback"), failedText);
        var korean = new Regex("[가-힣]");
        Assert.DoesNotMatch(korean, pendingText);
        Assert.DoesNotMatch(korean, failedText);
    });

    [Fact]
    public void NewPinKeysExistInBothLanguagesAndTheEnglishValuesHaveNoKorean()
    {
        string[] keys = ["Pin_MarkdownContinued", "Pin_MarkdownDrawing", "Pin_MarkdownDiagramFallback", "Pin_MermaidSyntaxLength",
            "Pin_MermaidSyntax", "Pin_MermaidTooLarge", "Pin_MermaidNoWebView", "Pin_MermaidUnavailable"];
        foreach (string key in keys)
        {
            using (UiText.UseLanguage("ko-KR")) Assert.Matches("[가-힣]", UiText.Get(key));
            using (UiText.UseLanguage("en")) Assert.DoesNotMatch("[가-힣]", UiText.Get(key));
        }
    }

    [Fact]
    public void KoreanTextSaysDownloadOnlyAsNaeryeobatgi()
    {
        var resources = new ResourceManager("MyCapture.Core.Localization.Strings", typeof(UiText).Assembly);
        ResourceSet set = resources.GetResourceSet(CultureInfo.GetCultureInfo("ko-KR"), createIfNotExists: true, tryParents: true)!;
        string[] offenders = set.Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Value is string text && text.Contains("다운로드", StringComparison.Ordinal))
            .Select(entry => (string)entry.Key).ToArray();
        Assert.Empty(offenders);
    }

    private static IEnumerable<object> Logical(object root)
    {
        if (root is not DependencyObject element) yield break;
        foreach (object child in LogicalTreeHelper.GetChildren(element))
        {
            yield return child;
            foreach (object nested in Logical(child)) yield return nested;
        }
    }

    private sealed class FailureLog : ILogger
    {
        public List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Errors.Add(exception);
        }
    }
}
