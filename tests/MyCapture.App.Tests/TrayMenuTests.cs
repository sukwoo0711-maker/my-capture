using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The tray menu puts region capture, screen recording, text capture and the pin commands in its
/// first group, keeps the rarer capture types in one branch, ends with Exit and no longer carries
/// the display language; the tray icon's tooltip says what a click and a right-click do.
/// </summary>
public sealed class TrayMenuTests
{
    [Fact]
    public void TopLevelOrderPutsCaptureRecordingTextAndPinsFirstAndExitLast()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var calls = new List<string>();
        TrayMenuCommands commands = Commands(calls);
        HotkeySettings keys = new();
        IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(commands, keys, canRestoreClosedPin: false, scrollingActive: false);

        Assert.Equal(["영역 캡처", "화면 녹화", "텍스트 복사", "다른 캡처", "클립보드를 핀으로 띄우기", "", "라이브러리", "설정", "단축키 보기", "", "종료"],
            menu.Select(entry => entry.Label));
        Assert.Equal([false, false, false, false, false, true, false, false, false, true, false], menu.Select(entry => entry.IsSeparator));
        Assert.Equal(keys.Capture.ToString(), menu[0].Gesture);
        Assert.Equal(keys.RecordRegion.ToString(), menu[1].Gesture);
        Assert.Equal("F1", menu[8].Gesture);

        // Text capture is a first-group command now, not a branch child.
        ThemedShellPresenter.MenuEntry more = menu[3];
        Assert.Null(more.Action);
        Assert.Equal(["창 캡처", "전체 화면 캡처", "스크롤 캡처", "지연 캡처", "이전 영역 반복"],
            more.Children!.Select(entry => entry.Label));

        // No English-only language block and no "how to use" wording.
        string[] labels = menu.Concat(more.Children!).Select(entry => entry.Label).ToArray();
        Assert.DoesNotContain("Language", labels);
        Assert.DoesNotContain("한국어", labels);
        Assert.DoesNotContain("English", labels);
        Assert.DoesNotContain(UiText.Get("Shell.Help"), labels);

        // Each entry runs the existing command.
        foreach (ThemedShellPresenter.MenuEntry entry in menu.Concat(more.Children!).Where(entry => entry.Action is not null))
            entry.Action!();
        Assert.Equal(["Capture", "Record", "Text", "PinClipboard", "Library", "Settings", "Shortcuts", "Exit",
            "Window", "FullScreen", "Scroll", "Delay", "Repeat"], calls);
    }

    [Fact]
    public void ReopenPinAppearsOnlyWhenAClosedPinCanReturnAndScrollingOffersCancel()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var calls = new List<string>();
        var keys = new HotkeySettings { RestoreClosedPin = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x72) };
        IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(Commands(calls), keys,
            canRestoreClosedPin: true, scrollingActive: true);
        Assert.Equal(["영역 캡처", "화면 녹화", "텍스트 복사", "다른 캡처", "클립보드를 핀으로 띄우기", "닫은 핀 다시 열기", "",
            "라이브러리", "설정", "단축키 보기", "", "종료"], menu.Select(entry => entry.Label));
        Assert.Equal("Ctrl+Alt+F3", menu[5].Gesture);
        Assert.Equal("스크롤 캡처 취소", menu[3].Children![2].Label);
        menu[5].Action!();
        Assert.Equal(["Reopen"], calls);
    }

    [Fact]
    public void PinClipboardIsReachableOnANewInstallAndSaysWhyItIsDisabled()
    {
        var calls = new List<string>();
        // A new installation leaves Paste to screen unassigned; the tray is the way in.
        HotkeySettings keys = NewInstallDefaults.Create().Hotkeys;
        Assert.False(keys.PasteToScreen.IsAssigned);
        using (UiText.UseLanguage("ko-KR"))
        {
            ThemedShellPresenter.MenuEntry ready = PinClipboardEntry(TrayMenuLayout.Build(Commands(calls), keys,
                new TrayMenuState(ClipboardHasContent: true)));
            Assert.True(ready.IsEnabled);
            Assert.Equal(string.Empty, ready.ToolTip);
            Assert.Equal(string.Empty, ready.Gesture);
            ready.Action!();
            Assert.Equal(["PinClipboard"], calls);

            ThemedShellPresenter.MenuEntry empty = PinClipboardEntry(TrayMenuLayout.Build(Commands(calls), keys,
                new TrayMenuState(ClipboardHasContent: false)));
            Assert.False(empty.IsEnabled);
            Assert.Equal("클립보드가 비어 있습니다", empty.ToolTip);
        }

        using (UiText.UseLanguage("en-US"))
        {
            ThemedShellPresenter.MenuEntry empty = TrayMenuLayout.Build(Commands(calls), keys,
                new TrayMenuState(ClipboardHasContent: false)).Single(entry => entry.Label == "Pin clipboard");
            Assert.Equal("Clipboard is empty", empty.ToolTip);
        }

        // An assigned key is shown beside it.
        var assigned = new HotkeySettings { PasteToScreen = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56) };
        using (UiText.UseLanguage("ko-KR"))
            Assert.Equal("Ctrl+Shift+V", PinClipboardEntry(TrayMenuLayout.Build(Commands(calls), assigned, new TrayMenuState())).Gesture);

        static ThemedShellPresenter.MenuEntry PinClipboardEntry(IReadOnlyList<ThemedShellPresenter.MenuEntry> menu) =>
            menu.Single(entry => entry.Label == "클립보드를 핀으로 띄우기");
    }

    [Fact]
    public void HideAllPinsAppearsWhilePinsExistAndOffersShowingThemAgain()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var calls = new List<string>();
        var keys = new HotkeySettings { HideAllPins = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x48) };
        Assert.DoesNotContain(TrayMenuLayout.Build(Commands(calls), keys, new TrayMenuState(PinCount: 0)),
            entry => entry.Label is "핀 모두 숨기기" or "핀 모두 보이기");

        IReadOnlyList<ThemedShellPresenter.MenuEntry> shown = TrayMenuLayout.Build(Commands(calls), keys,
            new TrayMenuState(CanRestoreClosedPin: true, PinCount: 2));
        Assert.Equal(["영역 캡처", "화면 녹화", "텍스트 복사", "다른 캡처", "클립보드를 핀으로 띄우기", "핀 모두 숨기기", "닫은 핀 다시 열기"],
            shown.TakeWhile(entry => !entry.IsSeparator).Select(entry => entry.Label));
        ThemedShellPresenter.MenuEntry hide = shown[5];
        Assert.Equal("Ctrl+Shift+H", hide.Gesture);
        hide.Action!();

        ThemedShellPresenter.MenuEntry show = TrayMenuLayout.Build(Commands(calls), keys,
            new TrayMenuState(PinCount: 2, PinsHidden: true)).Single(entry => entry.Label == "핀 모두 보이기");
        show.Action!();
        Assert.Equal(["HidePins", "HidePins"], calls);

        // The tutorials' way out of click-through names exactly these entries.
        Assert.Contains("핀 모두 숨기기", UiText.Get("Hotkey.TrayRestore"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThemedMenuOpensTheBranchAsASubmenuAndRunsCommandsAfterClosing() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var calls = new List<string>();
        using var presenter = new ThemedShellPresenter(() => false);
        int dismissed = 0;
        presenter.ForegroundDismissed += () => dismissed++;
        var keys = new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC) };
        ContextMenu menu = presenter.CreateMenu(TrayMenuLayout.Build(Commands(calls), keys,
            new TrayMenuState(ClipboardHasContent: false)));

        Assert.Equal(2, menu.Items.OfType<Separator>().Count());
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(["영역 캡처", "화면 녹화", "텍스트 복사", "다른 캡처", "클립보드를 핀으로 띄우기", "라이브러리", "설정", "단축키 보기", "종료"],
            items.Select(item => (string)item.Header));
        Assert.DoesNotContain(items, item => (string)item.Header is "Language" or "✓ 한국어" or "   English");
        Assert.Same(items[^1], menu.Items[^1]);

        // The shared item template has no popup; the branch brings one so its five commands can open.
        MenuItem branch = items[3];
        Assert.Equal(5, branch.Items.Count);
        branch.ApplyTemplate();
        Assert.IsType<Popup>(branch.Template.FindName("PART_Popup", branch));

        ((MenuItem)branch.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(["Window"], calls);
        Assert.Equal(1, dismissed);
        Assert.Equal("Ctrl+Shift+C", AutomationProperties.GetAcceleratorKey(items[0]));

        // A disabled command keeps its place and says why, to the pointer and to screen readers.
        MenuItem pin = items[4];
        Assert.False(pin.IsEnabled);
        Assert.Equal("클립보드가 비어 있습니다", pin.ToolTip);
        Assert.True(ToolTipService.GetShowOnDisabled(pin));
        Assert.Equal("클립보드가 비어 있습니다", AutomationProperties.GetHelpText(pin));
        Assert.Null(items[0].ToolTip);
    });

    [Fact]
    public void ClipboardCheckAnswersWithoutOpeningTheClipboard()
    {
        // The native check runs on any thread and never throws; the answer depends on the desktop.
        _ = ClipboardAvailability.HasPinnableContent();
        Func<bool>? previous = ClipboardAvailability.OverrideForTest;
        try
        {
            ClipboardAvailability.OverrideForTest = () => false;
            Assert.False(ClipboardAvailability.HasPinnableContent());
            ClipboardAvailability.OverrideForTest = () => true;
            Assert.True(ClipboardAvailability.HasPinnableContent());
        }
        finally { ClipboardAvailability.OverrideForTest = previous; }

        // Cheap enough to run on every menu open: well under a millisecond per check.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) _ = ClipboardAvailability.HasPinnableContent();
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500), $"1000 checks took {timer.Elapsed.TotalMilliseconds:0.0} ms");
    }

    [Fact]
    public void TooltipSaysWhatAClickAndARightClickDo()
    {
        using (UiText.UseLanguage("ko-KR"))
        {
            string tip = TrayIconService.ComposeTooltip("MyCapture", "준비됨", 1234);
            Assert.Equal("MyCapture\nMyCapture — 준비됨 · 캡처 1,234개\n클릭: 라이브러리 · 오른쪽 클릭: 메뉴", tip);
        }

        using (UiText.UseLanguage("en-US"))
        {
            string tip = TrayIconService.ComposeTooltip("MyCapture Preview", "Ready", 120000);
            Assert.EndsWith("Click: library · Right-click: menu", tip, StringComparison.Ordinal);
            Assert.True(tip.Length <= 127);
        }
    }

    private static TrayMenuCommands Commands(List<string> calls) => new(
        () => calls.Add("Capture"), () => calls.Add("Record"), () => calls.Add("Window"), () => calls.Add("FullScreen"),
        () => calls.Add("Scroll"), () => calls.Add("Delay"), () => calls.Add("Repeat"), () => calls.Add("Text"),
        () => calls.Add("Reopen"), () => calls.Add("Library"), () => calls.Add("Settings"), () => calls.Add("Shortcuts"),
        () => calls.Add("Exit"), PinClipboard: () => calls.Add("PinClipboard"), HideOrShowAllPins: () => calls.Add("HidePins"));
}
