using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Settings retains readable Korean errors after redundant capture and recording callouts are removed.
/// </summary>
public sealed class SettingsCalloutTests
{
    [Fact]
    public Task ErrorSummaryKeepsKoreanWordsWholeWithoutRepeatedCaptureInstructions() =>
        SettingsThemePreviewTests.RunIsolated(nameof(ErrorSummaryKeepsKoreanWordsWholeWithoutRepeatedCaptureInstructions),
            VerifyInfoBoxes, typeof(SettingsCalloutTests));

    private static void VerifyInfoBoxes()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        ThemeService.Apply(AppTheme.Glass);
        AppSettings saved = new();
        var window = new SettingsWindow(() => saved, _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            // The narrowest supported width gives error messages their longest wrapping.
            Width = 780, Height = 700, ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Drain(window);
            var draft = (SettingsDraft)window.DataContext;
            // Capture and recording instructions are available through guides, without repeated callout boxes.
            var infoStyle = (Style)window.FindResource("Settings.InfoCard");
            Assert.DoesNotContain(Descendants(window).OfType<Border>(),
                border => ReferenceEquals(border.Style, infoStyle));

            // The box that lists invalid values keeps its Korean messages whole in the same way.
            draft.DelaySeconds = "abc";
            Drain(window);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("ErrorSummary")).Visibility);
            var errorList = Assert.IsType<ItemsControl>(window.FindName("ErrorList"));
            var heading = Assert.IsType<TextBlock>(window.FindName("ErrorSummaryHeading"));
            Assert.Equal(window.FindResource("FontSize.Body"), heading.FontSize);
            TextBlock[] messages = VisualDescendants(errorList).OfType<TextBlock>().ToArray();
            Assert.Equal(draft.AllErrors(), messages.Select(message => AutomationProperties.GetName(message)));
            Assert.All(messages, message =>
            {
                string plain = AutomationProperties.GetName(message);
                Assert.Equal(KoreanWordWrap.KeepWords(plain), message.Text);
                Assert.Contains(KoreanWordWrap.WordJoiner, message.Text);
                foreach (double width in new[] { 180d, 240d, message.ActualWidth })
                {
                    var probe = new TextBlock
                    {
                        Text = message.Text, TextWrapping = TextWrapping.Wrap, FontFamily = message.FontFamily,
                        FontSize = message.FontSize, FontWeight = message.FontWeight, Width = width,
                    };
                    _ = probe.ContentStart;
                    probe.Measure(new Size(width, double.PositiveInfinity));
                    probe.Arrange(new Rect(probe.DesiredSize));
                    foreach (string line in Lines(probe))
                    {
                        string visible = KoreanWordWrap.Strip(line).TrimEnd();
                        int end = plain.IndexOf(visible, StringComparison.Ordinal) + visible.Length;
                        bool breaksKoreanWord = end > 0 && end < plain.Length
                            && !char.IsWhiteSpace(plain[end - 1]) && !char.IsWhiteSpace(plain[end])
                            && (IsKoreanLetter(plain[end - 1]) || IsKoreanLetter(plain[end]));
                        Assert.False(breaksKoreanWord, $"{width}: '{visible}' breaks inside a Korean word");
                    }
                }
            });
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }

    private static void Drain(Window window) =>
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

    private static bool IsKoreanLetter(char value) =>
        value is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (DependencyObject? parent = LogicalTreeHelper.GetParent(element); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            yield return parent;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Descendants(element)) yield return descendant;
        }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static IEnumerable<string> Lines(TextBlock block)
    {
        TextPointer? cursor = block.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        while (cursor is not null)
        {
            TextPointer? next = cursor.GetLineStartPosition(1);
            yield return new TextRange(cursor, next ?? block.ContentEnd).Text;
            cursor = next;
        }
    }
}
