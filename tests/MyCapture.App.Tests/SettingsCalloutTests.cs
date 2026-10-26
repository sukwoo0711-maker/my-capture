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
/// The settings information boxes break Korean text only at spaces and title themselves at the same
/// size as the settings labels around them.
/// </summary>
public sealed class SettingsCalloutTests
{
    [Fact]
    public Task EveryInfoBoxKeepsKoreanWordsWholeAndTitlesItselfLikeASettingsLabel() =>
        SettingsThemePreviewTests.RunIsolated(nameof(EveryInfoBoxKeepsKoreanWordsWholeAndTitlesItselfLikeASettingsLabel),
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
            // The narrowest supported width gives the callouts their longest wrapping.
            Width = 780, Height = 700, ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Drain(window);
            var draft = (SettingsDraft)window.DataContext;
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            var infoStyle = (Style)window.FindResource("Settings.InfoCard");
            var labelStyle = (Style)window.FindResource("Settings.Label");
            Border[] boxes = Descendants(window).OfType<Border>().Where(border => ReferenceEquals(border.Style, infoStyle)).ToArray();
            Assert.True(boxes.Length >= 2, "The Capture and Recording tabs each have an information box.");

            // The heading size every settings item uses, read from a real item label.
            TextBlock itemLabel = Descendants(window).OfType<TextBlock>()
                .First(block => ReferenceEquals(block.Style, labelStyle) && block.Text == UiText.Get("Capture.AfterAction"));
            var checkedBoxes = new List<string>();
            foreach (Border box in boxes)
            {
                TabItem tab = Assert.IsType<TabItem>(Ancestors(box).OfType<TabItem>().First());
                tabs.SelectedItem = tab;
                Drain(window);
                var stack = Assert.IsType<StackPanel>(box.Child);
                TextBlock heading = Assert.IsType<TextBlock>(stack.Children[0]);
                TextBlock body = Assert.IsType<TextBlock>(stack.Children[1]);

                // Same size, weight and style as the labels of the settings below and above it.
                Assert.Same(labelStyle, heading.Style);
                Assert.Equal(itemLabel.FontSize, heading.FontSize);
                Assert.Equal(itemLabel.FontWeight, heading.FontWeight);
                Assert.Equal(window.FindResource("FontSize.Body"), heading.FontSize);

                // The body keeps each Korean word whole; the accessible name is the plain text.
                Assert.Contains(KoreanWordWrap.WordJoiner, body.Text);
                string plain = KoreanWordWrap.Strip(body.Text);
                Assert.Equal(plain, AutomationProperties.GetName(body));
                Assert.Equal(body.Text, KoreanWordWrap.KeepWords(plain));
                if (ReferenceEquals(tab, tabs.Items[1])) Assert.Contains(draft.CaptureHotkey, plain, StringComparison.Ordinal);

                // Every rendered line ends at a space or at the end, at the narrowest and a wider width.
                foreach (double width in new[] { 180d, 240d, body.ActualWidth })
                {
                    var probe = new TextBlock
                    {
                        Text = body.Text, TextWrapping = TextWrapping.Wrap, FontFamily = body.FontFamily, FontSize = body.FontSize,
                        FontWeight = body.FontWeight, Width = width,
                    };
                    _ = probe.ContentStart;
                    probe.Measure(new Size(width, double.PositiveInfinity));
                    probe.Arrange(new Rect(probe.DesiredSize));
                    string[] lines = Lines(probe).ToArray();
                    Assert.True(lines.Length >= 2, $"{width}: the text should wrap");
                    foreach (string line in lines)
                    {
                        string visible = KoreanWordWrap.Strip(line).TrimEnd();
                        int end = plain.IndexOf(visible, StringComparison.Ordinal) + visible.Length;
                        Assert.True(end == plain.Length || plain[end] == ' ', $"{width}: '{visible}' breaks inside a word");
                    }
                }
                checkedBoxes.Add(plain);
            }
            Assert.Equal(boxes.Length, checkedBoxes.Distinct().Count());

            // The box that lists invalid values keeps its Korean messages whole in the same way.
            draft.DelaySeconds = "abc";
            Drain(window);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("ErrorSummary")).Visibility);
            var errorList = Assert.IsType<ItemsControl>(window.FindName("ErrorList"));
            TextBlock[] messages = VisualDescendants(errorList).OfType<TextBlock>().ToArray();
            Assert.Equal(draft.AllErrors(), messages.Select(message => AutomationProperties.GetName(message)));
            Assert.All(messages, message =>
            {
                string plain = AutomationProperties.GetName(message);
                Assert.Equal(KoreanWordWrap.KeepWords(plain), message.Text);
                Assert.Contains(KoreanWordWrap.WordJoiner, message.Text);
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
