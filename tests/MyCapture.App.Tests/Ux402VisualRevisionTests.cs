using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ux402VisualRevisionTests
{
    [Theory]
    [InlineData("ko", "복사 (C)", "편집 (E)", "핀 (P)", "저장 (S)", "텍스트 복사 (T)", "카드 유지", "자동으로 닫지 않기")]
    [InlineData("en", "Copy (C)", "Edit (E)", "Pin (P)", "Save (S)", "Copy text (T)", "Keep card open", "Keep open")]
    public void CardActionsExposeMatchingShortTooltipsAndAccessibleToggleState(string language,
        string copy, string edit, string pin, string save, string copyText, string keep, string keepTip) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var card = new QuickAccessWindow(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        try
        {
            IReadOnlyList<Button> actions = card.ActionButtonsForTest;
            Assert.Equal(5, actions.Count);
            foreach (var (button, expected) in actions.Zip(new[] { copy, edit, pin, save, copyText }))
            {
                Assert.Equal(expected, button.ToolTip);
                Assert.Equal(expected[..expected.LastIndexOf(" (", StringComparison.Ordinal)], new ButtonAutomationPeer(button).GetName());
                Assert.Equal(expected[^2].ToString(), AutomationProperties.GetAcceleratorKey(button));
                Assert.InRange(ToolTipService.GetInitialShowDelay(button), 0, 150);
                Assert.Equal(0, ToolTipService.GetBetweenShowDelay(button));
            }
            ToggleButton toggle = card.KeepToggleForTest;
            Assert.Equal(keepTip, toggle.ToolTip);
            Assert.InRange(ToolTipService.GetInitialShowDelay(toggle), 0, 150);
            var peer = new ToggleButtonAutomationPeer(toggle);
            Assert.Equal(keep, peer.GetName());
            var provider = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!;
            Assert.Equal(ToggleState.Off, provider.ToggleState);
            object word = toggle.Content;
            provider.Toggle();
            Assert.True(card.IsKept);
            Assert.Equal(ToggleState.On, provider.ToggleState);
            // The word stays; the toggle's checked state carries the change.
            Assert.Same(word, toggle.Content);
            provider.Toggle();
            Assert.False(card.IsKept);
            Assert.Equal(ToggleState.Off, provider.ToggleState);
        }
        finally { card.Close(); }
    });
}
