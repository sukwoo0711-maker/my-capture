using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The tray menu puts region capture and screen recording on top, groups the rarer capture types,
/// ends with Exit and no longer carries the display language; the tray icon's tooltip says what a
/// click and a right-click do.
/// </summary>
public sealed class TrayMenuTests
{
    [Fact]
    public void TopLevelOrderPutsCaptureAndRecordingFirstAndExitLast()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var calls = new List<string>();
        TrayMenuCommands commands = Commands(calls);
        HotkeySettings keys = new();
        IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(commands, keys, canRestoreClosedPin: false, scrollingActive: false);

        Assert.Equal(["영역 캡처", "화면 녹화", "다른 캡처", "", "라이브러리", "설정", "단축키 보기", "", "종료"],
            menu.Select(entry => entry.Label));
        Assert.Equal([false, false, false, true, false, false, false, true, false], menu.Select(entry => entry.IsSeparator));
        Assert.Equal(keys.Capture.ToString(), menu[0].Gesture);
        Assert.Equal(keys.RecordRegion.ToString(), menu[1].Gesture);
        Assert.Equal("F1", menu[6].Gesture);

        ThemedShellPresenter.MenuEntry more = menu[2];
        Assert.Null(more.Action);
        Assert.Equal(["창 캡처", "전체 화면 캡처", "스크롤 캡처", "지연 캡처", "이전 영역 반복", "텍스트 복사"],
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
        Assert.Equal(["Capture", "Record", "Library", "Settings", "Shortcuts", "Exit",
            "Window", "FullScreen", "Scroll", "Delay", "Repeat", "Text"], calls);
    }

    [Fact]
    public void ReopenPinAppearsOnlyWhenAClosedPinCanReturnAndScrollingOffersCancel()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var calls = new List<string>();
        var keys = new HotkeySettings { RestoreClosedPin = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x72) };
        IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(Commands(calls), keys,
            canRestoreClosedPin: true, scrollingActive: true);
        Assert.Equal(["영역 캡처", "화면 녹화", "다른 캡처", "닫은 핀 다시 열기", "", "라이브러리", "설정", "단축키 보기", "", "종료"],
            menu.Select(entry => entry.Label));
        Assert.Equal("Ctrl+Alt+F3", menu[3].Gesture);
        Assert.Equal("스크롤 캡처 취소", menu[2].Children![2].Label);
        menu[3].Action!();
        Assert.Equal(["Reopen"], calls);
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
        ContextMenu menu = presenter.CreateMenu(TrayMenuLayout.Build(Commands(calls), keys, false, false));

        Assert.Equal(2, menu.Items.OfType<Separator>().Count());
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(["영역 캡처", "화면 녹화", "다른 캡처", "라이브러리", "설정", "단축키 보기", "종료"], items.Select(item => (string)item.Header));
        Assert.DoesNotContain(items, item => (string)item.Header is "Language" or "✓ 한국어" or "   English");
        Assert.Same(items[^1], menu.Items[^1]);

        // The shared item template has no popup; the branch brings one so its six commands can open.
        MenuItem branch = items[2];
        Assert.Equal(6, branch.Items.Count);
        branch.ApplyTemplate();
        Assert.IsType<Popup>(branch.Template.FindName("PART_Popup", branch));

        ((MenuItem)branch.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(["Window"], calls);
        Assert.Equal(1, dismissed);
        Assert.Equal("Ctrl+Shift+C", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(items[0]));
    });

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
        () => calls.Add("Exit"));
}
