using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>An editor quick save names its file in the shared notification card, with its folder one click away.</summary>
public sealed class QuickSaveNoticeTests
{
    [Theory]
    [InlineData("ko-KR", "저장됨", "폴더 열기")]
    [InlineData("en-US", "Saved", "Open folder")]
    public void TheCardNamesTheFileAndOpensItsFolderEvenDuringACaptureSession(string culture, string title, string open) =>
        StaTestHost.Run(() =>
        {
            using var language = UiText.UseLanguage(culture);
            var monitor = new MonitorInfo("TEST", new RectD(-20000, -20000, 1080, 1920), new RectD(-20000, -20000, 1080, 1880), 96, false);
            // A capture session holds ordinary notices back; the card answers the save command.
            using var presenter = new ThemedShellPresenter(() => true) { NotificationMonitor = () => monitor };
            string folder = Path.Combine(Path.GetTempPath(), "MyCapture quick saves");
            var opened = new List<string>();

            presenter.ShowQuickSaved(Path.Combine(folder, "캡처 2026-10-24.png"), opened.Add);

            Assert.Equal((true, FeedbackKind.Result, (TimeSpan?)FeedbackTiming.Result), presenter.NotificationForTest);
            Window card = presenter.NotificationWindowForTest!;
            Assert.Equal(title, card.Title);
            string[] texts = FirstRunWelcomeTests.Descendants(card).OfType<TextBlock>()
                .Select(text => KoreanWordWrap.Strip(text.Text)).ToArray();
            Assert.Contains(title, texts);
            Assert.Contains("캡처 2026-10-24.png", texts);
            Assert.DoesNotContain(texts, text => text.Contains(folder, StringComparison.Ordinal));
            Button openFolder = FirstRunWelcomeTests.Descendants(card).OfType<Button>().Single(button => Equals(button.Content, open));

            openFolder.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal([folder], opened);
            Assert.False(presenter.HasNotification);
        });
}
