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
    public void ThreeStepsFollowTheRealEventsAndFinishWithTheChoices() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var clipboard = new FakeClipboard();
        WelcomeWindow guide = Create(clipboard);
        int capture = 0, pin = 0, library = 0;
        guide.CaptureRequested += (_, _) => capture++;
        guide.PinRequested += (_, _) => pin++;
        guide.LibraryRequested += (_, _) => library++;
        try
        {
            // 1/3: the configured key as large key caps, one primary button, Skip, and a table to drag over.
            Assert.Equal(WelcomeStep.Capture, guide.CurrentStep);
            Assert.Equal("1 / 3 · 캡처", guide.ProgressText);
            Assert.Equal("화면의 일부를 캡처합니다", guide.TitleText);
            Assert.Equal("이 키나 아래 버튼을 누른 뒤 표 위를 드래그하십시오.", guide.InstructionText);
            Assert.Equal("영역 캡처 시작", guide.TryNowButton.Content);
            Assert.Equal("건너뛰기", guide.SkipButton.Content);
            Assert.True(guide.TryNowButton.IsDefault);
            Assert.True(guide.SkipButton.IsCancel);
            Assert.True(guide.KeyShown && guide.PracticeTableShown && guide.PreviewShown);
            Assert.False(guide.TrayDiagramShown || guide.PreferencesShown);
            WrapPanel caps = Assert.Single(Descendants(guide).OfType<WrapPanel>(), panel => panel.Children.OfType<Border>().Any());
            Assert.Equal("Ctrl+Shift+C", AutomationProperties.GetName(caps));
            Assert.All(caps.Children.OfType<Border>(), cap =>
            {
                Assert.True(cap.MinHeight >= 52);
                Assert.True(((TextBlock)cap.Child).FontSize >= 22);
            });
            Assert.Empty(clipboard.Writes);

            Click(guide.TryNowButton);
            Assert.Equal(1, capture);
            Assert.True(guide.CapturePending);
            Assert.False(guide.TryNowButton.IsEnabled);
            Click(guide.TryNowButton);
            Assert.Equal(1, capture);

            guide.NotifyCaptureCompleted(copied: true);
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
            Assert.Equal("2 / 3 · 핀", guide.ProgressText);
            Assert.Equal("캡처했습니다. 클립보드에도 복사했습니다.", guide.StatusText);
            Assert.True(guide.TryNowButton.IsEnabled);

            // 2/3: nothing is written to the clipboard until the person presses the copy button.
            Assert.Equal("연습용 표 복사", guide.TryNowButton.Content);
            Assert.False(guide.KeyShown);
            Assert.Empty(clipboard.Writes);
            Click(guide.TryNowButton);
            Assert.Single(clipboard.Writes);
            Assert.Equal("항목\t사양\r\n메모리\t16 GB\r\n저장 장치\t512 GB", clipboard.Writes[0]);
            Assert.True(guide.PracticeCopied);
            Assert.Equal("복사했습니다. 이제 핀을 띄울 수 있습니다.", guide.StatusText);
            Assert.True(guide.KeyShown);
            Assert.Equal("F3 키를 누르십시오.", guide.InstructionText);
            Assert.Equal("핀 만들기", guide.TryNowButton.Content);
            WrapPanel pinKey = Assert.Single(Descendants(guide).OfType<WrapPanel>(), panel => panel.Children.OfType<Border>().Any());
            Assert.Equal("F3", AutomationProperties.GetName(pinKey));
            Click(guide.TryNowButton);
            Assert.Equal(1, pin);
            Assert.Single(clipboard.Writes);

            guide.NotifyPinCreated();
            Assert.Equal(WelcomeStep.Library, guide.CurrentStep);
            Assert.Equal("3 / 3 · 다시 찾기", guide.ProgressText);
            Assert.Equal("핀을 띄웠습니다. Esc 키 또는 핀의 닫기로 닫을 수 있습니다.", guide.StatusText);

            // 3/3: the library shortcut, the tray icon picture and the button.
            Assert.Equal("라이브러리 열기", guide.TryNowButton.Content);
            Assert.True(guide.TrayDiagramShown && guide.KeyShown);
            Assert.False(guide.PreviewShown);
            Click(guide.TryNowButton);
            Assert.Equal(1, library);

            guide.NotifyLibraryOpened();
            Assert.Equal(WelcomeStep.Finish, guide.CurrentStep);
            Assert.Equal("시작하기", guide.TryNowButton.Content);
            Assert.Equal(Visibility.Collapsed, guide.SkipButton.Visibility);
            Assert.False(guide.SkipButton.IsTabStop);
            Assert.True(guide.PreferencesShown);
            Assert.True(guide.StartAtSignIn);
            Assert.Contains(Descendants(guide).OfType<TextBlock>(), text =>
                KoreanWordWrap.Strip(text.Text) == "이 안내는 트레이 메뉴의 ‘처음 사용 안내’에서 다시 볼 수 있습니다.");

            Click(guide.TryNowButton);
            Assert.Equal(WelcomeOutcome.Completed, guide.Outcome);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void EnglishMirrorsTheSameStructure() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var clipboard = new FakeClipboard();
        WelcomeWindow guide = Create(clipboard);
        try
        {
            Assert.Equal("1 / 3 · Capture", guide.ProgressText);
            Assert.Equal("Capture part of the screen", guide.TitleText);
            Assert.Equal("Start region capture", guide.TryNowButton.Content);
            Assert.Equal("Skip", guide.SkipButton.Content);
            guide.NotifyCaptureCompleted(copied: true);
            Assert.Equal("Captured. It is also copied to the clipboard.", guide.StatusText);
            Assert.Equal("Copy practice table", guide.TryNowButton.Content);
            Click(guide.TryNowButton);
            Assert.Equal("Item\tSpec\r\nMemory\t16 GB\r\nStorage\t512 GB", clipboard.Writes.Single());
            Assert.Equal("Press F3.", guide.InstructionText);
            guide.NotifyPinCreated();
            Assert.Equal("3 / 3 · Find again", guide.ProgressText);
            Assert.Equal("Open library", guide.TryNowButton.Content);
            guide.NotifyLibraryOpened();
            Assert.Equal("Get started", guide.TryNowButton.Content);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ACapturedButNotCopiedResultIsSaidPlainly() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = Create(new FakeClipboard());
        try
        {
            guide.NotifyCaptureCompleted(copied: false);
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
            Assert.Equal("캡처했습니다. 클립보드에는 복사하지 못했습니다.", guide.StatusText);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void CancellingACaptureOrAPinReturnsToTheSameStep() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = Create(new FakeClipboard());
        try
        {
            Click(guide.TryNowButton);
            Assert.True(guide.CapturePending);
            guide.NotifyCaptureCancelled();
            Assert.Equal(WelcomeStep.Capture, guide.CurrentStep);
            Assert.False(guide.CapturePending);
            Assert.True(guide.TryNowButton.IsEnabled);
            Assert.Equal("캡처하지 않았습니다. 다시 시도하십시오.", guide.StatusText);
            Assert.Equal("영역 캡처 시작", guide.TryNowButton.Content);

            // A capture that ended with nothing for this guide releases the waiting button without a message.
            Click(guide.TryNowButton);
            guide.ResetPendingCapture();
            Assert.True(guide.TryNowButton.IsEnabled);

            // Events for another step change nothing.
            guide.NotifyPinCreated();
            guide.NotifyLibraryOpened();
            Assert.Equal(WelcomeStep.Capture, guide.CurrentStep);
            guide.NotifyCaptureCompleted(copied: true);
            guide.NotifyCaptureCancelled();
            guide.NotifyLibraryOpened();
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void AFailedPracticeCopyStaysOnTheStepAndTellsWhatToDo() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var clipboard = new FakeClipboard { Succeeds = false };
        WelcomeWindow guide = Create(clipboard);
        try
        {
            guide.NotifyCaptureCompleted(copied: true);
            Click(guide.TryNowButton);
            Assert.False(guide.PracticeCopied);
            Assert.Equal("복사하지 못했습니다. 버튼을 다시 누르십시오.", guide.StatusText);
            Assert.Equal("연습용 표 복사", guide.TryNowButton.Content);
            clipboard.Succeeds = true;
            Click(guide.TryNowButton);
            Assert.True(guide.PracticeCopied);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void SkipClosesThePracticeStepsAndEscapeAndWindowCloseStillWorkOnEveryStep() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        foreach (WelcomeStep step in Enum.GetValues<WelcomeStep>())
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
        using var language = UiText.UseLanguage("ko-KR");
        var clipboard = new FakeClipboard();
        WelcomeWindow guide = Create(clipboard);
        try
        {
            guide.UpdateShortcutAvailability(capture: false, pin: false, library: false);
            Assert.False(guide.KeyShown);
            Assert.Equal("아래 버튼을 누른 뒤 표 위를 드래그하십시오.", guide.InstructionText);
            Assert.DoesNotContain(Descendants(guide).OfType<WrapPanel>(), panel => panel.Children.OfType<Border>().Any());
            Click(guide.TryNowButton);
            guide.NotifyCaptureCancelled();

            guide.NotifyCaptureCompleted(copied: true);
            Click(guide.TryNowButton);
            Assert.False(guide.KeyShown);
            Assert.Equal("아래 버튼을 눌러 핀을 띄우십시오.", guide.InstructionText);
            guide.ShowRetryHint();
            Assert.Equal("아래 버튼을 눌러 핀을 띄우십시오.", guide.RetryHintText);
            Assert.Equal("핀 만들기", guide.TryNowButton.Content);
            guide.NotifyPinCreated();
            Assert.False(guide.KeyShown);
            Assert.Equal("작업 표시줄 ^ 안의 MyCapture 아이콘으로 엽니다.", guide.InstructionText);
            Assert.Equal("라이브러리 열기", guide.TryNowButton.Content);

            // The shell reports the shortcuts again after a repair: the keys come back on the step that uses them.
            guide.UpdateShortcutAvailability(capture: true, pin: true, library: true);
            Assert.True(guide.KeyShown);
            Assert.Equal("이 키나 작업 표시줄 ^ 안의 아이콘으로 엽니다.", guide.InstructionText);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "아래 버튼을 누른 뒤 표 위를 드래그하십시오.", "먼저 아래 버튼으로 연습용 표를 복사하십시오.",
        "아래 버튼을 눌러 핀을 띄우십시오.", "작업 표시줄 ^ 안의 MyCapture 아이콘으로 엽니다.")]
    [InlineData("en-US", "Select the button below, then drag over the table.", "First, copy the practice table with the button below.",
        "Select the button below to pin it.", "Open it from the MyCapture icon inside ^ on the taskbar.")]
    public void UnassignedShortcutsUseButtonAndTrayInstructionsInBothLanguages(string culture, string capture, string copy, string pin, string library) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        // Fresh installs leave the library shortcut unassigned.
        HotkeySettings defaults = NewInstallDefaults.Create().Hotkeys;
        Assert.Equal(Hotkey.None, defaults.OpenLibrary);
        defaults.Capture = Hotkey.None;
        defaults.PasteToScreen = Hotkey.None;
        WelcomeWindow guide = Create(new FakeClipboard(), keys: defaults);
        try
        {
            Assert.False(guide.KeyShown);
            Assert.Equal(capture, guide.InstructionText);
            guide.NotifyCaptureCompleted(copied: true);
            Assert.False(guide.KeyShown);
            Assert.Equal(copy, guide.InstructionText);
            Click(guide.TryNowButton);
            Assert.False(guide.KeyShown);
            Assert.Equal(pin, guide.InstructionText);
            guide.ShowRetryHint();
            Assert.Equal(pin, guide.RetryHintText);
            guide.NotifyPinCreated();
            Assert.False(guide.KeyShown);
            Assert.True(guide.TrayDiagramShown);
            Assert.Equal(library, guide.InstructionText);
            guide.UpdateShortcutAvailability(capture: true, pin: true, library: true);
            Assert.False(guide.KeyShown);
            Assert.Equal(library, guide.InstructionText);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ANoticeAppearsAfterFifteenSecondsOfNothingAndNamesTheNextAction() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = Create(new FakeClipboard());
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(15), new WelcomeOptions().HintDelay);
            Assert.Equal(string.Empty, guide.RetryHintText);
            guide.ShowRetryHint();
            Assert.Equal("버튼을 누른 뒤 표 위를 드래그하십시오.", guide.RetryHintText);

            // Moving on clears it; the next step has its own.
            guide.NotifyCaptureCompleted(copied: true);
            Assert.Equal(string.Empty, guide.RetryHintText);
            guide.ShowRetryHint();
            Assert.Equal("‘연습용 표 복사’ 버튼을 누르십시오.", guide.RetryHintText);
            Click(guide.TryNowButton);
            Assert.Equal(string.Empty, guide.RetryHintText);
            guide.ShowRetryHint();
            Assert.Equal("키가 반응하지 않으면 ‘핀 만들기’ 버튼을 누르십시오.", guide.RetryHintText);
            guide.NotifyPinCreated();
            guide.ShowRetryHint();
            Assert.Equal("‘라이브러리 열기’ 버튼을 누르십시오.", guide.RetryHintText);
            guide.NotifyLibraryOpened();
            guide.ShowRetryHint();
            Assert.Equal(string.Empty, guide.RetryHintText);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void TheNoticeTimerFiresAfterTheDelayAndEveryStepStartsItAgain() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = Show(new FakeClipboard(), new WelcomeOptions { HintDelay = TimeSpan.FromMilliseconds(120) });
        try
        {
            Assert.Equal(string.Empty, guide.RetryHintText);
            Pump(guide.Dispatcher, TimeSpan.FromMilliseconds(700));
            Assert.Equal("버튼을 누른 뒤 표 위를 드래그하십시오.", guide.RetryHintText);

            guide.NotifyCaptureCompleted(copied: true);
            Assert.Equal(string.Empty, guide.RetryHintText);
            Pump(guide.Dispatcher, TimeSpan.FromMilliseconds(700));
            Assert.Equal("‘연습용 표 복사’ 버튼을 누르십시오.", guide.RetryHintText);

            // Waiting for a capture to finish is not inactivity: the timer is stopped until the selector ends.
            guide.NotifyCaptureCancelled();
            guide.NotifyPinCreated();
            guide.NotifyLibraryOpened();
            Pump(guide.Dispatcher, TimeSpan.FromMilliseconds(500));
            Assert.Equal(WelcomeStep.Finish, guide.CurrentStep);
            Assert.Equal(string.Empty, guide.RetryHintText);
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
            Assert.Equal("아래 버튼을 누른 뒤 표 위를 드래그하십시오.", first.InstructionText);
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
            guide.NotifyCaptureCompleted(copied: true);
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
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
            animated.NotifyCaptureCompleted(copied: true);
            Assert.EndsWith("pin-clipboard.ko.mp4", motion.OpenedPaths.Last(), StringComparison.Ordinal);
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
        ThemedShellPresenter.MenuEntry entry = Assert.Single(menu, item => item.Label == "처음 사용 안내");
        int index = menu.ToList().IndexOf(entry);
        Assert.Equal("단축키 보기", menu[index - 1].Label);
        entry.Action!();
        Assert.Equal(["Guide"], calls);

        TrayMenuCommands without = commands with { FirstUseGuide = null };
        Assert.DoesNotContain(TrayMenuLayout.Build(without, new HotkeySettings(), false, false), item => item.Label == "처음 사용 안내");

        using var english = UiText.UseLanguage("en-US");
        Assert.Contains(TrayMenuLayout.Build(commands, new HotkeySettings(), false, false), item => item.Label == "First-use guide");
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
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = Create(new FakeClipboard());
        try
        {
            TextBlock progress = Descendants(guide).OfType<TextBlock>().Single(text => text.Text.StartsWith("1 / 3", StringComparison.Ordinal));
            Assert.Equal("3단계 중 1단계: 캡처", AutomationProperties.GetName(progress));
            TextBlock title = Descendants(guide).OfType<TextBlock>().Single(text => KoreanWordWrap.Strip(text.Text) == "화면의 일부를 캡처합니다");
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(title));
            Assert.Equal("영역 캡처 시작", AutomationProperties.GetName(guide.TryNowButton));
            Assert.Equal("건너뛰기", AutomationProperties.GetName(guide.SkipButton));
            Assert.Contains("Enter", Assert.IsType<string>(guide.TryNowButton.ToolTip), StringComparison.Ordinal);
            Assert.Contains("Esc", Assert.IsType<string>(guide.SkipButton.ToolTip), StringComparison.Ordinal);

            guide.NotifyCaptureCompleted(copied: true);
            progress = Descendants(guide).OfType<TextBlock>().Single(text => text.Text.StartsWith("2 / 3", StringComparison.Ordinal));
            Assert.Equal("3단계 중 2단계: 핀", AutomationProperties.GetName(progress));
            TextBlock status = Descendants(guide).OfType<TextBlock>().Single(text => KoreanWordWrap.Strip(text.Text) == guide.StatusText && text.Text.Length > 0);
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
            var actions = Assert.IsType<StackPanel>(LogicalTreeHelper.GetParent(guide.TryNowButton));
            Assert.Same(guide.TryNowButton, Assert.Single(actions.Children.OfType<Button>(), button => button.Visibility == Visibility.Visible));
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
            ? new WelcomeOptions { Clipboard = clipboard, ReducedMotion = true, MediaFactory = () => new FakeMedia() }
            : new WelcomeOptions
            {
                Clipboard = clipboard, ReducedMotion = options.ReducedMotion ?? true, MediaFactory = options.MediaFactory ?? (() => new FakeMedia()),
                Replay = options.Replay, StartAtSignIn = options.StartAtSignIn, UseAccurateOcr = options.UseAccurateOcr, TextScale = options.TextScale,
                MonitorProvider = options.MonitorProvider, HintDelay = options.HintDelay,
            });

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
        if (step >= WelcomeStep.Pin) guide.NotifyCaptureCompleted(copied: true);
        if (step >= WelcomeStep.Library) guide.NotifyPinCreated();
        if (step >= WelcomeStep.Finish) guide.NotifyLibraryOpened();
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
