using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ReceiptKeyboardContentTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void CaptureReceiptExposesFocusableOpenAndPinActionsWithStableIds(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var presenter = new ThemedShellPresenter(() => false);
        int opened = 0, pinned = 0;
        string message = UiText.Get("Capture.Saved");
        string tip = UiText.Get("Onboard_FirstCaptureToastTip");
        Window window = presenter.CreateCaptureReceipt(message, () => opened++, tip, () => pinned++);
        try
        {
            Button[] buttons = Descendants(window).OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Length);
            Button open = Assert.Single(buttons, button => AutomationProperties.GetAutomationId(button) == "MyCapture.Receipt.Open");
            Button pin = Assert.Single(buttons, button => AutomationProperties.GetAutomationId(button) == "MyCapture.Receipt.Pin");
            Assert.Equal(message, AutomationProperties.GetName(open));
            Assert.Equal(tip, AutomationProperties.GetName(pin));
            Assert.Equal(UiText.Get("QuickAccess.Library"), open.ToolTip);
            Assert.Equal(UiText.Get("QuickAccess.Pin"), pin.ToolTip);
            Assert.All(buttons, AssertKeyboardTarget);

            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, opened);
            Assert.Equal(0, pinned);
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, opened);
            Assert.Equal(1, pinned);
            AssertUnshown(window);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void FirstPinReceiptUsesOnlyTheSuppliedSettingsAction(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var presenter = new ThemedShellPresenter(() => false);
        int settingsOpened = 0;
        string message = UiText.Get("Onboard_FirstPinTipUnassigned");
        Window window = presenter.CreateCaptureReceipt(message, () => settingsOpened++,
            actionTooltip: UiText.Get("Help.Settings"));
        try
        {
            Button action = Assert.Single(Descendants(window).OfType<Button>());
            Assert.Equal("MyCapture.Receipt.Open", AutomationProperties.GetAutomationId(action));
            Assert.Equal(message, AutomationProperties.GetName(action));
            Assert.Equal(UiText.Get("Help.Settings"), action.ToolTip);
            AssertKeyboardTarget(action);

            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, settingsOpened);
            Assert.DoesNotContain(Descendants(window), element => AutomationProperties.GetAutomationId(element) == "MyCapture.Receipt.Pin");
            AssertUnshown(window);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "Alt+Q로 선택 · Tab 이동 · Enter 실행")]
    [InlineData("en-US", "Alt+Q to focus · Tab to move · Enter to run")]
    public void ReceiptCyclesTabNavigationAndShowsLocalizedKeyboardEntry(string language, string expectedHint) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var presenter = new ThemedShellPresenter(() => false);
        Window window = presenter.CreateCaptureReceipt(UiText.Get("Capture.Saved"), () => { },
            UiText.Get("Onboard_FirstCaptureToastTip"), () => { });
        try
        {
            StackPanel content = Assert.Single(Descendants(window).OfType<StackPanel>());
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(content));
            TextBlock hint = Assert.Single(content.Children.OfType<TextBlock>());
            Assert.Equal(expectedHint, hint.Text);
            Assert.Equal(UiText.Get("Onboard_ReceiptKeyboard"), hint.Text);
            Assert.Contains("Alt+Q", hint.Text, StringComparison.Ordinal);
            Assert.Contains("Tab", hint.Text, StringComparison.Ordinal);
            Assert.Contains("Enter", hint.Text, StringComparison.Ordinal);
            Assert.False(hint.Focusable);
            Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
            AssertUnshown(window);
        }
        finally { window.Close(); }
    });

    private static void AssertKeyboardTarget(Button button)
    {
        Assert.True(button.IsEnabled);
        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);
        Assert.Equal(Visibility.Visible, button.Visibility);
    }

    private static void AssertUnshown(Window window)
    {
        Assert.False(window.IsVisible);
        Assert.False(window.IsLoaded);
        Assert.False(window.ShowActivated);
        Assert.False(window.ShowInTaskbar);
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
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
}
