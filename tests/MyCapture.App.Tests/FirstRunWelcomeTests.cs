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
/// A first run shows one window, the welcome, with three illustrated steps; the library follows
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
    public void WelcomeHasThreeStepsOneFilledActionAndASmallShortcutLink() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var keys = new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x43) };
        var welcome = new WelcomeWindow(keys, AfterCaptureAction.Editor);
        try
        {
            Assert.Equal("MyCapture 시작", welcome.Title);
            // The window title is not repeated in the body.
            Assert.DoesNotContain(Descendants(welcome).OfType<TextBlock>(), text => text.Text == welcome.Title);

            StackPanel steps = Descendants(welcome).OfType<StackPanel>()
                .Single(panel => AutomationProperties.GetName(panel) == "시작 안내");
            Border[] cards = steps.Children.OfType<Border>().ToArray();
            Assert.Equal(3, cards.Length);
            Assert.Equal(["1단계 · 영역 캡처", "2단계 · 캡처한 다음", "3단계 · 트레이 아이콘"],
                cards.Select(AutomationProperties.GetName));

            // Step 1: the configured key and the only filled button, which really starts a capture.
            Assert.Contains(Descendants(cards[0]).OfType<WrapPanel>(), panel => AutomationProperties.GetName(panel) == "Ctrl+Shift+C");
            Assert.Same(welcome.TryNowButton, Descendants(cards[0]).OfType<Button>().Single());
            Assert.Equal("지금 해 보기", welcome.TryNowButton.Content);
            Button[] buttons = Descendants(welcome).OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Length);
            Assert.Contains(welcome.LibraryButton, buttons);

            // Step 2 says what happens to a capture as the setting makes it true; step 3 where the icon is.
            Assert.Equal("캡처하면 클립보드에 복사되고 라이브러리에 저장되며 편집기가 열립니다.", welcome.AfterCaptureText);
            Assert.Equal("이미지는 7일 뒤 자동 삭제됩니다. 보관하거나 폴더에 넣은 항목은 유지됩니다.", welcome.RetentionText);
            Assert.Contains(Descendants(cards[1]).OfType<TextBlock>(), text => AutomationProperties.GetName(text) == welcome.RetentionText
                || text.Text == welcome.RetentionText);
            // Step 3 offers starting at sign-in, checked by default.
            Assert.Same(welcome.StartAtSignInCheck, Descendants(cards[2]).OfType<CheckBox>().Single());
            Assert.True(welcome.StartAtSignIn);
            Assert.Equal("Windows에 로그인하면 MyCapture 실행", welcome.StartAtSignInCheck.Content);
            Assert.Equal("Windows에 로그인하면 MyCapture 실행", AutomationProperties.GetName(welcome.StartAtSignInCheck));
            Assert.Contains("^", string.Concat(Descendants(cards[2]).OfType<TextBlock>().Select(text => text.Text)), StringComparison.Ordinal);
            Assert.Equal("캡처하면 클립보드에 복사되고 라이브러리에 저장되며 빠른 작업 카드가 나타납니다.",
                WelcomeWindow.AfterCaptureSentence(AfterCaptureAction.QuickAccess));
            Assert.Equal("캡처하면 클립보드에 복사되고 라이브러리에 저장됩니다.",
                WelcomeWindow.AfterCaptureSentence(AfterCaptureAction.ClipboardOnly));

            // Changing shortcuts is a link, not a button.
            Assert.Equal("단축키 변경", new TextRange(welcome.ShortcutsLink.ContentStart, welcome.ShortcutsLink.ContentEnd).Text);
        }
        finally { welcome.Close(); }

        // A key that is not assigned is not shown at all: the tray menu is named instead, and the
        // library is reached by clicking the tray icon, never by a key that may be unassigned.
        using var english = UiText.UseLanguage("en-US");
        var unassigned = new WelcomeWindow(new HotkeySettings { Capture = Hotkey.None, OpenLibrary = Hotkey.None }, AfterCaptureAction.Editor);
        try
        {
            Assert.Equal("Try it now", unassigned.TryNowButton.Content);
            Assert.Empty(Descendants(unassigned).OfType<WrapPanel>());
            string text = string.Join(" ", Descendants(unassigned).OfType<TextBlock>().Select(AutomationProperties.GetName));
            Assert.Contains("Choose Region capture from the tray icon menu.", text, StringComparison.Ordinal);
            Assert.Contains("Click its icon for the library", text, StringComparison.Ordinal);
            Assert.DoesNotContain(UiText.Get("Hotkey.Unassigned"), text, StringComparison.Ordinal);
        }
        finally { unassigned.Close(); }
    });

    [Fact]
    public void EveryWayOutOfTheWelcomeNamesWhatOpensNext() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            Assert.Equal(WelcomeOutcome.TryCapture, Run(window => window.TryNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Assert.Equal(WelcomeOutcome.Closed, Run(window => window.LibraryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
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
