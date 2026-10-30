using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The first-use guide: three things to do with the real app, each following the real event, then the
/// start-at-sign-in and accurate-OCR choices. No test here reads or writes the real clipboard, profile or Run key.
/// </summary>
public sealed class OnboardingGuideTests
{
    private static readonly HotkeySettings Keys = new()
    {
        Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC),
        PasteToScreen = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3),
        OpenLibrary = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4C),
    };

    [Fact]
    public void FiveStepsFollowTheRealEventsAndFinishWithTheChoices() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var clipboard = new FakeClipboard();
        WelcomeWindow guide = Create(clipboard);
        try
        {
            Assert.Equal("1 / 15 · 캡처·편집", guide.ProgressText);
            Assert.Equal("캡처 준비", guide.TitleText);
            Assert.True(guide.KeyShown && guide.PracticeTableShown && guide.PreviewShown);
            Assert.False(guide.PreferencesShown);
            int capture = 0;
            guide.CaptureRequested += (_, _) => capture++;
            Click(guide.TryNowButton);
            guide.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, capture);
            Assert.Equal(PracticeStep.A2, guide.Session.Step);
            Assert.False(guide.TryNowButton.IsEnabled);
            guide.Notify(new(PracticeEventKind.SelectorReady, guide.Session.Id, guide.Session.CaptureRequestId));
            PracticeJourneyDriver.Complete(guide.Session);
            Assert.True(guide.SuccessShown);
            Assert.Equal(PracticeStep.A3, guide.Session.Step);
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B0);
            Assert.Empty(clipboard.Writes);
            Click(guide.TryNowButton);
            Assert.Single(clipboard.Writes);
            Assert.True(guide.PracticeCopied);
            Assert.Equal(PracticeStep.B1, guide.Session.Step);
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.Results);
            Assert.All(guide.Session.Results.Values, value => Assert.Equal(PracticeStepResult.Completed, value));
            Assert.True(guide.PreferencesShown && guide.StartAtSignIn);
            Assert.Equal("연습 종료", guide.TryNowButton.Content);
            Assert.Equal(Visibility.Collapsed, guide.SkipButton.Visibility);
            Click(guide.TryNowButton);
            Assert.Equal(WelcomeOutcome.Completed, guide.Outcome);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void EnglishMirrorsTheSameStructure() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var guide = Create(new());
        try
        {
            Assert.Equal("1 / 15 · Capture and edit", guide.ProgressText);
            Assert.Equal("Start a capture", guide.TitleText);
            Assert.Equal("Start region capture", guide.TryNowButton.Content);
            Assert.Equal("End practice", guide.SkipButton.Content);
            foreach (PracticeStep step in PracticeSession.Steps)
            {
                PracticeJourneyDriver.AdvanceTo(guide.Session, step);
                Assert.Equal(UiText.Get($"Journey.{step}.Title"), guide.TitleText);
                Assert.DoesNotContain("Journey.", guide.InstructionText, StringComparison.Ordinal);
            }
            PracticeJourneyDriver.Complete(guide.Session);
            Assert.Equal("Practice results", guide.TitleText);
            Assert.Equal("End practice", guide.TryNowButton.Content);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ACapturedButNotCopiedResultIsSaidPlainly() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var clipboard = new FakeClipboard { Succeeds = false };
        var guide = Create(clipboard);
        try
        {
            AdvanceTo(guide, WelcomeStep.Draw);
            Assert.StartsWith("그림을 가져왔습니다.", guide.StatusText, StringComparison.Ordinal);
            Assert.Empty(clipboard.Writes);
            Assert.False(guide.PracticeCopied);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void CancellingACaptureOrAPinReturnsToTheSameStep() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var guide = Create(new());
        try
        {
            Click(guide.TryNowButton); guide.NotifyCaptureCancelled();
            Assert.Equal(PracticeStep.A2, guide.Session.Step);
            Assert.False(guide.CapturePending || guide.SuccessShown);
            Assert.True(guide.TryNowButton.IsEnabled);
            Assert.Equal("캡처를 취소했습니다.", guide.StatusText);
            Click(guide.TryNowButton); guide.ResetPendingCapture();
            Assert.True(guide.TryNowButton.IsEnabled);
            guide.NotifyPinCreated(); guide.NotifyLibraryOpened(); guide.NotifyArrowAdded();
            Assert.Equal(PracticeStep.A2, guide.Session.Step);
            Click(guide.TryNowButton);
            PracticeJourneyDriver.Complete(guide.Session);
            guide.NotifyCaptureCancelled();
            Assert.Equal(PracticeStep.A3, guide.Session.Step);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void EditingPracticeDoesNotDependOnOrWriteTheClipboard() => StaTestHost.Run(() =>
    {
        var clipboard = new FakeClipboard { Succeeds = false };
        var guide = Create(clipboard);
        int edit = 0; guide.EditorRequested += (_, _) => edit++;
        try
        {
            AdvanceTo(guide, WelcomeStep.Draw);
            Click(guide.TryNowButton); Click(guide.TryNowButton);
            Assert.Equal(2, edit);
            Assert.False(guide.PracticeCopied);
            Assert.Equal(PracticeStep.A3, guide.Session.Step);
            Assert.Empty(clipboard.Writes);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void SkipClosesThePracticeStepsAndEscapeAndWindowCloseStillWorkOnEveryStep() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        foreach (WelcomeStep step in Enum.GetValues<WelcomeStep>().Where(step => step != WelcomeStep.Consent))
        {
            if (step != WelcomeStep.Finish)
            {
                WelcomeWindow guide = Create(new FakeClipboard());
                AdvanceTo(guide, step);
                guide.Show();
                Assert.True(guide.SkipButton.IsVisible);
                Click(guide.SkipButton);
                Assert.False(guide.IsVisible);
                Assert.Equal(WelcomeOutcome.Closed, guide.Outcome);
                Assert.Equal(AfterWelcome.OpenLibrary, FirstRunPlan.Next(guide.Outcome));
            }

            WelcomeWindow escape = Create(new FakeClipboard());
            AdvanceTo(escape, step);
            escape.Show();
            var escapeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escape)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            escape.RaiseEvent(escapeKey);
            Assert.True(escapeKey.Handled);
            Assert.False(escape.IsVisible);
            Assert.Equal(WelcomeOutcome.Closed, escape.Outcome);
            Assert.Equal(AfterWelcome.OpenLibrary, FirstRunPlan.Next(escape.Outcome));

            WelcomeWindow windowClose = Create(new FakeClipboard());
            AdvanceTo(windowClose, step);
            windowClose.Show();
            windowClose.Close();
            Assert.False(windowClose.IsVisible);
            Assert.Equal(WelcomeOutcome.Closed, windowClose.Outcome);
            Assert.Equal(AfterWelcome.OpenLibrary, FirstRunPlan.Next(windowClose.Outcome));
        }

        WelcomeWindow settings = Create(new FakeClipboard());
        AdvanceTo(settings, WelcomeStep.Finish);
        settings.Show();
        settings.ShortcutsLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(WelcomeOutcome.OpenSettings, settings.Outcome);
    });

    [Fact]
    public void FinishingDoesNotOpenTheLibraryAgainButStillAnnouncesReady()
    {
        Assert.Equal(AfterWelcome.OpenLibrary, FirstRunPlan.Next(WelcomeOutcome.Closed));
        Assert.Equal(AfterWelcome.Nothing, FirstRunPlan.Next(WelcomeOutcome.Completed));
        Assert.True(FirstRunPlan.AnnouncesReady(WelcomeOutcome.Completed));
        Assert.True(FirstRunPlan.AnnouncesReady(WelcomeOutcome.Closed));
    }

    [Fact]
    public void AShortcutWindowsDidNotRegisterIsNeverTheWayForwardAndTheButtonsStayTheOnlyPath() => StaTestHost.Run(() =>
    {
        var guide = Create(new());
        try
        {
            guide.UpdateShortcutAvailability(false, false, false);
            Assert.False(guide.KeyShown);
            Assert.Equal(UiText.Get("Journey.A1.Body"), guide.InstructionText);
            AdvanceTo(guide, WelcomeStep.Pin);
            Assert.False(guide.KeyShown); Assert.True(guide.ConflictShown);
            Assert.Equal(UiText.Get("Journey.B1.NoKey"), guide.InstructionText);
            Assert.Equal(UiText.Get("Journey.ShowPicture"), guide.TryNowButton.Content);
            guide.ShowRetryHint(); Assert.Equal(guide.InstructionText, guide.RetryHintText);
            guide.UpdateShortcutAvailability(true, true, true);
            Assert.True(guide.KeyShown); Assert.False(guide.ConflictShown);
            Assert.Contains("F3", guide.InstructionText, StringComparison.Ordinal);
            AdvanceTo(guide, WelcomeStep.Library);
            Assert.False(guide.KeyShown);
            Assert.Equal(UiText.Get("Journey.C1.Title"), guide.TryNowButton.Content);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void UnassignedShortcutsUseButtonAndTrayInstructionsInBothLanguages(string culture) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        HotkeySettings defaults = NewInstallDefaults.Create().Hotkeys;
        Assert.Equal(Hotkey.None, defaults.OpenLibrary);
        defaults.Capture = Hotkey.None; defaults.PasteToScreen = Hotkey.None;
        var guide = Create(new(), keys: defaults);
        try
        {
            Assert.False(guide.KeyShown);
            Assert.Equal(UiText.Get("Journey.A1.Body"), guide.InstructionText);
            AdvanceTo(guide, WelcomeStep.Pin);
            Assert.False(guide.KeyShown);
            Assert.Equal(UiText.Get("Journey.B1.NoKey"), guide.InstructionText);
            guide.ShowRetryHint(); Assert.Equal(guide.InstructionText, guide.RetryHintText);
            AdvanceTo(guide, WelcomeStep.Library);
            Assert.False(guide.KeyShown);
            Assert.Equal(UiText.Get("Journey.C1.Body"), guide.InstructionText);
            guide.UpdateShortcutAvailability(true, true, true);
            Assert.False(guide.KeyShown);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ANoticeAppearsAfterFifteenSecondsOfNothingAndNamesTheNextAction() => StaTestHost.Run(() =>
    {
        var guide = Create(new());
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(15), guide.HintDelay);
            Assert.Empty(guide.RetryHintText);
            guide.ShowRetryHint(); Assert.Equal(guide.InstructionText, guide.RetryHintText);
            foreach (PracticeStep step in new[] { PracticeStep.A3, PracticeStep.B1, PracticeStep.B2, PracticeStep.C1 })
            {
                PracticeJourneyDriver.AdvanceTo(guide.Session, step);
                Assert.Empty(guide.RetryHintText); guide.ShowRetryHint();
                Assert.Equal(step == PracticeStep.B2 ? UiText.Get("Journey.ResizeHelp") : guide.InstructionText, guide.RetryHintText);
            }
            AdvanceTo(guide, WelcomeStep.Finish); guide.ShowRetryHint(); Assert.Empty(guide.RetryHintText);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void TheNoticeTimerFiresAfterTheDelayAndEveryStepStartsItAgain() => StaTestHost.Run(() =>
    {
        var guide = Show(new(), new WelcomeOptions { HintDelay = TimeSpan.FromMilliseconds(120) });
        try
        {
            Assert.Empty(guide.RetryHintText);
            Pump(guide.Dispatcher, TimeSpan.FromMilliseconds(700));
            Assert.Equal(guide.InstructionText, guide.RetryHintText);
            AdvanceTo(guide, WelcomeStep.Draw);
            Assert.Empty(guide.RetryHintText);
            Pump(guide.Dispatcher, TimeSpan.FromMilliseconds(700));
            Assert.Equal(guide.InstructionText, guide.RetryHintText);
            AdvanceTo(guide, WelcomeStep.Finish);
            Pump(guide.Dispatcher, TimeSpan.FromMilliseconds(500));
            Assert.Empty(guide.RetryHintText);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void TheShellReplaysTheGuideWithTheCurrentChoicesAndWithoutKeysItCannotOffer() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var settings = new AppSettings();
        settings.General.LaunchAtLogin = false;
        settings.Ocr.Quality = OcrQuality.Accurate;
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(App).GetField("_settings", flags)!.SetValue(app, settings);
        MethodInfo create = typeof(App).GetMethod("CreateOnboardingWindow", flags)!;

        var replay = (WelcomeWindow)create.Invoke(app, [true])!;
        var first = (WelcomeWindow)create.Invoke(app, [false])!;
        try
        {
            Assert.True(replay.IsReplay);
            Assert.False(replay.StartAtSignIn);
            Assert.True(replay.UseAccurateOcr);
            Assert.False(first.IsReplay);
            Assert.True(first.StartAtSignIn);
            // No shortcut is registered in this shell (hotkeys never started), so the guide offers the buttons only.
            Assert.False(replay.KeyShown);
            Assert.False(first.KeyShown);
            Assert.Equal(UiText.Get("Journey.A1.Body"), first.InstructionText);
        }
        finally { replay.Close(); first.Close(); }
    });

    private static void Pump(Dispatcher dispatcher, TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void ANoticeIsNotShownWhileACaptureIsWaiting() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = Create(new FakeClipboard());
        try
        {
            Click(guide.TryNowButton);
            guide.ShowRetryHint();
            Assert.Equal(string.Empty, guide.RetryHintText);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ACaptureShortcutOverTheGuideKeepsItOpenForTheLesson() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = Create(new FakeClipboard());
        guide.Show();
        try
        {
            // Every capture start reaches the open guide through the shell's dismissal sweep (ShortcutHelpWindow.DismissForCapture).
            guide.DismissForCapture();
            Assert.True(guide.IsVisible);
            Assert.True(guide.CapturePending);
            Assert.Equal(WelcomeOutcome.Closed, guide.Outcome);
            PracticeJourneyDriver.Complete(guide.Session);
            Assert.Equal(WelcomeStep.Draw, guide.CurrentStep);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ReplayPresetsTheCurrentChoices() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow replay = Create(new FakeClipboard(), new WelcomeOptions { Replay = true, StartAtSignIn = false, UseAccurateOcr = true });
        WelcomeWindow first = Create(new FakeClipboard());
        try
        {
            Assert.True(replay.IsReplay);
            Assert.False(replay.StartAtSignIn);
            Assert.True(replay.UseAccurateOcr);
            // A first run keeps the existing default: start at sign-in on, the OCR choice from the machine's capability.
            Assert.False(first.IsReplay);
            Assert.True(first.StartAtSignIn);
        }
        finally { replay.Close(); first.Close(); }
    });

    [Fact]
    public void OnlyTheMotionPathOpensAVideoAndReducedMotionKeepsTheStill() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var motion = new FakeMedia();
        WelcomeWindow animated = Show(new FakeClipboard(), new WelcomeOptions { ReducedMotion = false, MediaFactory = () => motion });
        try
        {
            Assert.EndsWith("capture-region.ko.mp4", Assert.Single(motion.OpenedPaths), StringComparison.Ordinal);
            PracticeJourneyDriver.AdvanceTo(animated.Session, PracticeStep.A4);
            Assert.EndsWith("image-arrow.ko.mp4", motion.OpenedPaths.Last(), StringComparison.Ordinal);
        }
        finally { animated.Close(); }

        var still = new FakeMedia();
        WelcomeWindow reduced = Show(new FakeClipboard(), new WelcomeOptions { ReducedMotion = true, MediaFactory = () => still });
        try
        {
            Assert.Empty(still.OpenedPaths);
            Assert.True(reduced.Preview.HasPoster);
            Assert.False(reduced.Preview.HasVideo);
            Assert.EndsWith("capture-region.ko.mp4", reduced.Preview.AssetPath, StringComparison.Ordinal);
        }
        finally { reduced.Close(); }
    });

    [Theory]
    [InlineData(96, 1920, 1080, 1920, 1040)]
    [InlineData(144, 2880, 1620, 2880, 1560)]
    public void TheGuideCentersOnTheMonitorUnderThePointerWithinItsWorkArea(uint dpi, double width, double height, double workWidth, double workHeight)
    {
        // A second monitor to the left of the primary one: virtual-desktop coordinates are negative.
        var monitor = new MonitorInfo("\\\\.\\DISPLAY2", new RectD(-width, 0, width, height), new RectD(-width, 0, workWidth, workHeight), dpi, IsPrimary: false);
        double scale = dpi / 96.0;
        RectD placed = WelcomeWindow.CenterOn(monitor, WelcomeWindow.DefaultWidth, WelcomeWindow.DefaultHeight);
        Assert.Equal(WelcomeWindow.DefaultWidth * scale, placed.Width, 3);
        Assert.Equal(Math.Min(WelcomeWindow.DefaultHeight * scale, workHeight), placed.Height, 3);
        Assert.Equal(monitor.WorkArea.Left + ((workWidth - placed.Width) / 2), placed.Left, 3);
        Assert.Equal(monitor.WorkArea.Top + ((workHeight - placed.Height) / 2), placed.Top, 3);
        Assert.True(placed.Left >= monitor.WorkArea.Left && placed.Left + placed.Width <= monitor.WorkArea.Left + workWidth);
        Assert.True(placed.Top >= monitor.WorkArea.Top && placed.Top + placed.Height <= monitor.WorkArea.Top + workHeight);

        // A work area smaller than the window clamps instead of spilling onto the next monitor.
        var tiny = new MonitorInfo("\\\\.\\DISPLAY3", new RectD(0, 0, 800, 600), new RectD(0, 0, 800, 560), 96, IsPrimary: true);
        RectD clamped = WelcomeWindow.CenterOn(tiny, WelcomeWindow.DefaultWidth, WelcomeWindow.DefaultHeight);
        Assert.Equal(560, clamped.Height, 3);
        Assert.Equal(0, clamped.Top, 3);
    }

    [Fact]
    public void LimitToWorkAreaKeepsTheWindowInsideASmallDisplay() => StaTestHost.Run(() =>
    {
        WelcomeWindow guide = Create(new FakeClipboard());
        try
        {
            var small = new MonitorInfo("\\\\.\\DISPLAY1", new RectD(0, 0, 1366, 768), new RectD(0, 0, 1366, 728), 96, IsPrimary: true);
            guide.LimitToWorkArea(small);
            Assert.True(guide.Height <= 728);
            Assert.True(guide.MaxHeight <= 728);
            Assert.True(guide.Width <= 1366);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void TheTrayMenuOffersTheGuideAgainOnlyWhenTheShellProvidesIt()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var calls = new List<string>();
        TrayMenuCommands commands = new(() => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
            () => { }, () => { }, () => { }, () => calls.Add("Guide"), FirstUseGuide: () => calls.Add("Guide"));
        IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(commands, new HotkeySettings(), canRestoreClosedPin: false, scrollingActive: false);
        var help = Assert.Single(menu, item => item.Label == "도움말").Children!;
        ThemedShellPresenter.MenuEntry entry = Assert.Single(help, item => item.Label == "처음 사용 안내");
        int index = help.ToList().IndexOf(entry);
        Assert.Equal("단축키 보기", help[index - 1].Label);
        entry.Action!();
        Assert.Equal(["Guide"], calls);

        TrayMenuCommands without = commands with { FirstUseGuide = null };
        Assert.DoesNotContain(TrayMenuLayout.Build(without, new HotkeySettings(), false, false).Single(item => item.Label == "도움말").Children!, item => item.Label == "처음 사용 안내");

        using var english = UiText.UseLanguage("en-US");
        Assert.Contains(TrayMenuLayout.Build(commands, new HotkeySettings(), false, false).Single(item => item.Label == "Help").Children!, item => item.Label == "First-use guide");
    }

    private static readonly string[] AllKeys =
    [
        "Onboarding.Replay", "Onboarding.Ready", "Onboarding.CaptureLabel", "Onboarding.PinLabel", "Onboarding.LibraryLabel",
        "Onboarding.ProgressName", "Onboarding.CaptureTitle", "Onboarding.CaptureInstruction", "Onboarding.CaptureInstructionKey",
        "Onboarding.PinTitle", "Onboarding.PinInstruction", "Onboarding.PressPin", "Onboarding.PinButtonInstruction",
        "Onboarding.LibraryTitle", "Onboarding.LibraryInstruction", "Onboarding.LibraryInstructionKey", "Onboarding.FinishTitle",
        "Onboarding.FinishInstruction", "Onboarding.ReplayNote", "Onboarding.StartCapture", "Onboarding.CopyPractice",
        "Onboarding.CreatePin", "Onboarding.OpenLibrary", "Onboarding.Start", "Onboarding.CaptureCopied", "Onboarding.Captured",
        "Onboarding.CaptureRetry", "Onboarding.CaptureHint", "Onboarding.CopyRetry", "Onboarding.PracticeCopied",
        "Onboarding.CopyHint", "Onboarding.PinHint", "Onboarding.PinClose", "Onboarding.LibraryHint", "Onboarding.PracticeLabel",
        "Onboarding.TableItem", "Onboarding.TableSpec", "Onboarding.TableMemory", "Onboarding.TableStorage",
        "Onboarding.TrayLocation", "Onboarding.TrayIconName", "Onboarding.AboutBody",
    ];

    [Fact]
    public void EveryGuideStringExistsInBothLanguagesAndFollowsTheProductVoice()
    {
        foreach (string key in AllKeys)
        {
            string korean, english;
            using (UiText.UseLanguage("ko-KR")) korean = UiText.Get(key);
            using (UiText.UseLanguage("en-US")) english = UiText.Get(key);
            Assert.False(string.IsNullOrWhiteSpace(korean), key);
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            foreach (string text in new[] { korean, english })
            {
                Assert.DoesNotContain('!', text);
                Assert.DoesNotContain('~', text);
                Assert.DoesNotContain("...", text, StringComparison.Ordinal);
                Assert.DoesNotMatch(@"[☀-➿\uD83C-\uD83E]", text);
            }

            // 합니다체 and noun forms only: no 해요체 ending, and every Korean sentence is short.
            Assert.DoesNotMatch(@"요\s*[.?]?$", korean);
            Assert.DoesNotMatch(@"(해요|예요|이에요|할게요|드릴게요|네요)", korean);
            foreach (string sentence in Regex.Split(korean, @"(?<=[.])\s+"))
                Assert.True(sentence.Length <= 46, $"{key}: {sentence.Length} characters in '{sentence}'");
            if (key.Contains("Instruction", StringComparison.Ordinal) || key.Contains("Hint", StringComparison.Ordinal))
                Assert.Matches(@"(십시오|니다)\.$", korean);
        }

        // Button labels are nouns, never sentences.
        foreach (string key in new[] { "Onboarding.StartCapture", "Onboarding.CopyPractice", "Onboarding.CreatePin", "Onboarding.OpenLibrary", "Onboarding.Start", "Onboarding.Replay" })
        {
            using var korean = UiText.UseLanguage("ko-KR");
            Assert.DoesNotContain('.', UiText.Get(key));
        }
    }

    [Fact]
    public void TheStepSurfacesCarryAccessibleNamesAndAnnounceTheStepChange() => StaTestHost.Run(() =>
    {
        var guide = Create(new());
        try
        {
            TextBlock progress = Descendants(guide).OfType<TextBlock>().Single(text => text.Text == guide.ProgressText);
            Assert.Equal(guide.ProgressText, AutomationProperties.GetName(progress));
            TextBlock title = Descendants(guide).OfType<TextBlock>().Single(text => KoreanWordWrap.Strip(text.Text) == guide.TitleText);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(title));
            Assert.Equal(guide.TryNowButton.Content, AutomationProperties.GetName(guide.TryNowButton));
            Assert.Equal(guide.SkipButton.Content, AutomationProperties.GetName(guide.SkipButton));
            Assert.Contains("Enter", (string)guide.TryNowButton.ToolTip);
            Assert.Contains("Esc", (string)guide.SkipButton.ToolTip);
            AdvanceTo(guide, WelcomeStep.Draw);
            TextBlock status = Descendants(guide).OfType<TextBlock>().Single(text => KoreanWordWrap.Strip(text.Text) == guide.StatusText);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void TheKeyboardOnlyPathReadsTheContentBeforeTheButtonsAndEnterIsThePrimaryAction() => StaTestHost.Run(() =>
    {
        WelcomeWindow guide = Create(new FakeClipboard());
        try
        {
            AdvanceTo(guide, WelcomeStep.Finish);
            Assert.True(guide.TryNowButton.IsDefault);
            Assert.True(guide.SkipButton.IsCancel);
            Assert.Equal(Visibility.Collapsed, guide.SkipButton.Visibility);
            Assert.False(guide.SkipButton.IsTabStop);
            var actions = Assert.IsType<WrapPanel>(LogicalTreeHelper.GetParent(guide.TryNowButton));
            Assert.Equal(new[] { guide.BackButton, guide.TryNowButton }, actions.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible));
            Assert.All(new Control[] { guide.TryNowButton, guide.StartAtSignInCheck, guide.AccurateOcrCheck },
                control => Assert.True(control.Focusable && control.IsTabStop));
            Assert.True(KeyboardNavigation.GetTabIndex(guide.Scroller) < KeyboardNavigation.GetTabIndex(FooterOf(guide)));
        }
        finally { guide.Close(); }
    });

    private static DependencyObject FooterOf(WelcomeWindow guide) =>
        LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(guide.TryNowButton));

    internal static WelcomeWindow Create(FakeClipboard clipboard, WelcomeOptions? options = null, HotkeySettings? keys = null) =>
        new(keys ?? Keys, AfterCaptureAction.Editor, ocrEnvironment: null, options: options is null
            ? new WelcomeOptions { Clipboard = clipboard, CopyPracticePicture = () => clipboard.TryCopyText("practice image") ? 42u : 0, ReducedMotion = true, MediaFactory = () => new FakeMedia() }
            : new WelcomeOptions
            {
                Clipboard = clipboard, CopyPracticePicture = () => clipboard.TryCopyText("practice image") ? 42u : 0, ReducedMotion = options.ReducedMotion ?? true, MediaFactory = options.MediaFactory ?? (() => new FakeMedia()),
                Replay = options.Replay, ShowCoreShortcutsConsent = options.ShowCoreShortcutsConsent, FeaturePractice = options.FeaturePractice, StartAtSignIn = options.StartAtSignIn, UseAccurateOcr = options.UseAccurateOcr, TextScale = options.TextScale,
                MonitorProvider = options.MonitorProvider, HintDelay = options.HintDelay,
            }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };

    private static WelcomeWindow Show(FakeClipboard clipboard, WelcomeOptions options)
    {
        WelcomeWindow guide = Create(clipboard, options);
        guide.WindowStartupLocation = WindowStartupLocation.Manual;
        guide.Left = -10000; guide.Top = -10000; guide.ShowActivated = false; guide.ShowInTaskbar = false;
        guide.Show();
        guide.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return guide;
    }

    internal static void AdvanceTo(WelcomeWindow guide, WelcomeStep step)
    {
        if (guide.CurrentStep == WelcomeStep.Consent && step != WelcomeStep.Consent) Click(guide.SkipButton);
        if (step == WelcomeStep.Consent) return;
        PracticeStep target = step switch
        {
            WelcomeStep.Capture => PracticeStep.A1, WelcomeStep.Draw => PracticeStep.A3,
            WelcomeStep.Pin => PracticeStep.B1, WelcomeStep.Resize => PracticeStep.B2,
            WelcomeStep.Library => PracticeStep.C1, _ => PracticeStep.Results,
        };
        PracticeJourneyDriver.AdvanceTo(guide.Session, target);
        Assert.Equal(step, guide.CurrentStep);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) => FirstRunWelcomeTests.Descendants(root);

    /// <summary>The clipboard stand-in: the guide writes only through it, so no test touches the real clipboard.</summary>
    internal sealed class FakeClipboard : IWelcomeClipboard
    {
        internal List<string> Writes { get; } = [];
        internal bool Succeeds { get; set; } = true;

        public bool TryCopyText(string text)
        {
            if (!Succeeds) return false;
            Writes.Add(text);
            return true;
        }
    }

    internal sealed class FakeMedia : ITutorialMedia
    {
        internal List<string> OpenedPaths { get; } = [];
        public UIElement View { get; } = new Border();
        public event Action? Opened { add { } remove { } }
        public event Action<Exception?>? Failed { add { } remove { } }
        public event Action? Ended { add { } remove { } }
        public void Open(string path) => OpenedPaths.Add(path);
        public void Play() { }
        public void Pause() { }
        public void Rewind() { }
        public void Close() { }
        public void Dispose() { }
    }
}
