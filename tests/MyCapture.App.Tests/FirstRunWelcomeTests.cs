using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A first run focuses on trying a capture; the library follows
/// once it closes, and later launches behave as before.
/// </summary>
public sealed class FirstRunWelcomeTests
{
    [Fact]
    public void FirstRunShowsOnlyTheWelcomeAndLaterLaunchesKeepTheirWindows()
    {
        LaunchPresentation first = FirstRunPlan.ForLaunch(isFirstRun: true, quietLaunch: false, openSettings: false);
        Assert.True(first.ShowWelcome);
        Assert.False(first.OpenLibrary);

        LaunchPresentation later = FirstRunPlan.ForLaunch(isFirstRun: false, quietLaunch: false, openSettings: false);
        Assert.Equal(new LaunchPresentation(false, true), later);
        // A sign-in launch stays quiet and the settings switch opens settings instead.
        Assert.False(FirstRunPlan.ForLaunch(false, quietLaunch: true, openSettings: false).OpenLibrary);
        Assert.False(FirstRunPlan.ForLaunch(false, quietLaunch: false, openSettings: true).OpenLibrary);

        // Closing the welcome is what opens the library; the other ways out open their own thing.
        Assert.Equal(AfterWelcome.OpenLibrary, FirstRunPlan.Next(WelcomeOutcome.Closed));
        Assert.Equal(AfterWelcome.StartCapture, FirstRunPlan.Next(WelcomeOutcome.TryCapture));
        Assert.Equal(AfterWelcome.OpenSettings, FirstRunPlan.Next(WelcomeOutcome.OpenSettings));
        Assert.Equal(AfterWelcome.Nothing, FirstRunPlan.Next(WelcomeOutcome.DismissedForCapture));

        // "Ready" follows the welcome, unless a capture starts instead; a later launch never shows it.
        Assert.True(FirstRunPlan.AnnouncesReady(WelcomeOutcome.Closed));
        Assert.True(FirstRunPlan.AnnouncesReady(WelcomeOutcome.OpenSettings));
        Assert.False(FirstRunPlan.AnnouncesReady(WelcomeOutcome.TryCapture));
        Assert.False(FirstRunPlan.AnnouncesReady(WelcomeOutcome.DismissedForCapture));
    }

    [Fact]
    public void WelcomeFocusesOnTheConfiguredCaptureAndKeepsBothChoicesInTheFooter() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var keys = new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x43) };
        var welcome = new WelcomeWindow(keys, AfterCaptureAction.Editor);
        try
        {
            Assert.Equal("MyCapture 시작", welcome.Title);
            Assert.DoesNotContain(Descendants(welcome).OfType<TextBlock>(), text => text.Text == welcome.Title);
            Assert.Contains(Descendants(welcome).OfType<TextBlock>(), text => text.Text == "한 번 캡처해 보십시오");

            WrapPanel caps = Descendants(welcome).OfType<WrapPanel>().Single();
            Assert.Equal("Ctrl+Shift+C", AutomationProperties.GetName(caps));
            Assert.Equal(["Ctrl", "Shift", "C"], caps.Children.OfType<Border>().Select(cap => ((TextBlock)cap.Child).Text));
            Assert.All(caps.Children.OfType<Border>(), cap =>
            {
                Assert.True(cap.MinHeight >= 52);
                Assert.True(((TextBlock)cap.Child).FontSize >= 22);
            });

            Button[] buttons = Descendants(welcome).OfType<Button>().ToArray();
            Assert.Equal([welcome.TryNowButton, welcome.SkipButton], buttons);
            Assert.Equal("지금 해 보기", welcome.TryNowButton.Content);
            Assert.Equal("건너뛰기", welcome.SkipButton.Content);
            Assert.True(welcome.TryNowButton.IsDefault);
            Assert.True(welcome.SkipButton.IsCancel);
            Assert.Equal("지금 해 보기", AutomationProperties.GetName(welcome.TryNowButton));
            Assert.Equal("건너뛰기", AutomationProperties.GetName(welcome.SkipButton));
            Assert.Equal(UiText.Get("Onboard_TryNowHelp"), AutomationProperties.GetHelpText(welcome.TryNowButton));
            Assert.Contains("Enter", Assert.IsType<string>(welcome.TryNowButton.ToolTip), StringComparison.Ordinal);
            Assert.DoesNotContain("Ctrl+Shift+C", Assert.IsType<string>(welcome.TryNowButton.ToolTip), StringComparison.Ordinal);
            Assert.Contains("Esc", Assert.IsType<string>(welcome.SkipButton.ToolTip), StringComparison.Ordinal);
            Assert.All(buttons, button => Assert.True(button.MinHeight >= 24));

            Assert.True(welcome.StartAtSignIn);
            Assert.Equal("Windows에 로그인하면 MyCapture 실행", welcome.StartAtSignInCheck.Content);
            Assert.Equal("Windows에 로그인하면 MyCapture 실행", AutomationProperties.GetName(welcome.StartAtSignInCheck));
            Assert.Equal("단축키 변경", new TextRange(welcome.ShortcutsLink.ContentStart, welcome.ShortcutsLink.ContentEnd).Text);
            Assert.Equal("단축키 변경", AutomationProperties.GetName(welcome.ShortcutsLink));
            TextBlock shortcuts = Descendants(welcome).OfType<TextBlock>().Single(text => text.Inlines.Contains(welcome.ShortcutsLink));
            Assert.Same(LogicalTreeHelper.GetParent(welcome.StartAtSignInCheck), LogicalTreeHelper.GetParent(shortcuts));
            Assert.Equal(1, Grid.GetColumn(shortcuts));
            Assert.True(welcome.StartAtSignInCheck.MinHeight >= 24);
            Assert.True(shortcuts.LineHeight >= 24);
        }
        finally { welcome.Close(); }

        // An unassigned shortcut remains honest, and the primary button still offers a capture.
        using var english = UiText.UseLanguage("en-US");
        var unassigned = new WelcomeWindow(new HotkeySettings { Capture = Hotkey.None, OpenLibrary = Hotkey.None }, AfterCaptureAction.Editor);
        try
        {
            Assert.Equal("Try it now", unassigned.TryNowButton.Content);
            Assert.Equal("Skip", unassigned.SkipButton.Content);
            Assert.Empty(Descendants(unassigned).OfType<WrapPanel>());
            Assert.Contains(Descendants(unassigned).OfType<TextBlock>(), text => text.Text == "Try your first capture");
            Assert.Contains(Descendants(unassigned).OfType<TextBlock>(), text => text.Text == UiText.Get("Hotkey.Unassigned"));
            Assert.DoesNotContain(Descendants(unassigned).OfType<TextBlock>(), text => text.Text == UiText.Get("Onboard_CaptureFromTray"));
        }
        finally { unassigned.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", AfterCaptureAction.Editor)]
    [InlineData("ko-KR", AfterCaptureAction.QuickAccess)]
    [InlineData("ko-KR", AfterCaptureAction.ClipboardOnly)]
    [InlineData("en-US", AfterCaptureAction.Editor)]
    [InlineData("en-US", AfterCaptureAction.QuickAccess)]
    [InlineData("en-US", AfterCaptureAction.ClipboardOnly)]
    public void WelcomeDefersTheCaptureAndTrayExplanations(string locale, AfterCaptureAction action) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(locale);
        var welcome = new WelcomeWindow(new HotkeySettings(), action);
        try
        {
            string[] paragraphs = ["Onboard_AfterCaptureEditor", "Onboard_AfterCaptureQuickAccess", "Onboard_AfterCaptureClipboard", "Onboard_TrayBody"];
            foreach (string key in paragraphs)
                Assert.DoesNotContain(Descendants(welcome).OfType<TextBlock>(), text =>
                    text.Text == UiText.Get(key) || AutomationProperties.GetName(text) == UiText.Get(key));
            Assert.Equal(2, Descendants(welcome).OfType<Button>().Count());
        }
        finally { welcome.Close(); }
    });

    [Fact]
    public void EveryWayOutOfTheWelcomeNamesWhatOpensNext() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            Assert.Equal(WelcomeOutcome.TryCapture, Run(window => window.TryNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Assert.Equal(WelcomeOutcome.Closed, Run(window => window.SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Assert.Equal(WelcomeOutcome.OpenSettings, Run(window => window.ShortcutsLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent))));
            Assert.Equal(WelcomeOutcome.Closed, Run(window => window.Close()));
            Assert.Equal(WelcomeOutcome.Closed, Run(window =>
            {
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                window.RaiseEvent(escape);
            }));
            Assert.Equal(WelcomeOutcome.DismissedForCapture, Run(window => window.DismissForCapture()));
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }

        static WelcomeOutcome Run(Action<WelcomeWindow> leave)
        {
            var window = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            };
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            leave(window);
            Assert.True(closed);
            return window.Outcome;
        }
    });

    /// <summary>Logical descendants: the window need not be shown for its content to be found.</summary>
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject descendant in Descendants(node)) yield return descendant;
        }
    }
}
