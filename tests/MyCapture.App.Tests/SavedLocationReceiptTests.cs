using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SavedLocationReceiptTests
{
    [Theory]
    [InlineData("ko", false, "라이브러리에 저장됨", "알림 영역의 MyCapture 아이콘에서 라이브러리를 엽니다.")]
    [InlineData("ko", true, "라이브러리에 저장됨", "Ctrl+Shift+L 키로 라이브러리를 엽니다.")]
    [InlineData("en", false, "Saved to the library", "Open the library from the MyCapture icon in the notification area.")]
    [InlineData("en", true, "Saved to the library", "Press Ctrl+Shift+L to open the library.")]
    public void ReceiptNamesTheLibraryAndOpensTheSameCapture(string language, bool assigned, string title, string hint)
        => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var settings = new AppSettings();
        settings.Hotkeys.OpenLibrary = assigned ? new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4C) : Hotkey.None;
        Guid capture = Guid.NewGuid(), opened = Guid.Empty;
        using var presenter = Presenter();
        Assert.True(SavedLocationReceipt.TryShow(settings, capture, false, false,
            (message, open, tip) => presenter.ShowCaptureReceipt(message, open, tip, answersCommand: true), id => opened = id));
        Window receipt = Assert.IsAssignableFrom<Window>(presenter.NotificationWindowForTest);
        var texts = FirstRunWelcomeTests.Descendants(receipt).OfType<TextBlock>().Select(text => KoreanWordWrap.Strip(text.Text));
        Assert.Contains(title, texts);
        Assert.Contains(hint, texts);
        Button action = Assert.Single(FirstRunWelcomeTests.Descendants(receipt).OfType<Button>(),
            button => AutomationProperties.GetAutomationId(button) == "MyCapture.Receipt.Open");
        action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(capture, opened);
        Assert.Equal(1, settings.General.SavedLocationTipCount);
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BusySurfaceSkipsWithoutConsumingOrQueuing(bool selecting, bool receiptVisible)
    {
        var settings = new AppSettings();
        int calls = 0;
        Assert.False(SavedLocationReceipt.TryShow(settings, Guid.NewGuid(), selecting, receiptVisible,
            (_, _, _) => { calls++; return true; }, _ => { }));
        Assert.Equal(0, calls);
        Assert.Equal(0, settings.General.SavedLocationTipCount);
    }

    [Fact]
    public void PresenterRefusalDoesNotConsumeTip()
    {
        var settings = new AppSettings();
        Assert.False(SavedLocationReceipt.TryShow(settings, Guid.NewGuid(), false, false, (_, _, _) => false, _ => { }));
        Assert.Equal(0, settings.General.SavedLocationTipCount);
    }

    [Theory]
    [InlineData("Close", true)]
    [InlineData("Copy", true)]
    [InlineData("Save", true)]
    [InlineData("Edit", false)]
    [InlineData("Pin", false)]
    [InlineData("Library", false)]
    [InlineData("Replaced", false)]
    public void ClosingCardOnlyIntroducesLocationWithoutADestinationChoice(string choice, bool expected) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var presenter = Presenter();
        var settings = new AppSettings();
        App app = Shell(settings, presenter);
        QuickAccessWindow card = QuickAccessLabelFixture.NewCard();
        try
        {
            card.Closed += (_, _) => Invoke(app, "ShowSavedLocationAfterCard", card, new CaptureRecord());
            Choose(card, choice);
            if (choice != "Replaced") card.Close();
            Assert.Equal(expected, presenter.HasNotification);
            Assert.Equal(expected ? 1 : 0, settings.General.SavedLocationTipCount);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData("Edit")]
    [InlineData("Pin")]
    [InlineData("Library")]
    public void ChoosingADestinationIsRememberedEvenIfItsActionFails(string choice) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var card = new QuickAccessWindow(QuickAccessLabelFixture.Sample(), () => Task.FromResult(true),
            () => throw new InvalidOperationException("edit"), () => Task.FromException(new InvalidOperationException("pin")),
            () => Task.FromResult(true), () => throw new InvalidOperationException("library"), () => Task.FromResult("unused.png"));
        try
        {
            try { Choose(card, choice); }
            catch (InvalidOperationException) { }
            Assert.True(card.ChoseLibraryEditOrPin);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void EditorAndCardUseTheSameFivePresentationBudget() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var presenter = Presenter();
        var settings = new AppSettings();
        App app = Shell(settings, presenter);
        for (int index = 0; index < 7; index++)
        {
            if (index % 2 == 0) Invoke(app, "ShowSavedLocationReceipt", Guid.NewGuid());
            else
            {
                QuickAccessWindow card = QuickAccessLabelFixture.NewCard();
                try
                {
                    card.Closed += (_, _) => Invoke(app, "ShowSavedLocationAfterCard", card, new CaptureRecord());
                    card.Close();
                }
                finally { card.Close(); }
            }
            Assert.Equal(index < 5, presenter.HasNotification);
            Assert.Equal(Math.Min(index + 1, 5), settings.General.SavedLocationTipCount);
            presenter.DismissForCapture();
        }
    });

    [Fact]
    public void ExistingReceiptIsKeptAndCapturePreparationIsSkipped() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var presenter = Presenter();
        var settings = new AppSettings();
        App app = Shell(settings, presenter);
        presenter.ShowCaptureReceipt("Existing", () => { }, answersCommand: true);
        Window? existing = presenter.NotificationWindowForTest;
        Invoke(app, "ShowSavedLocationReceipt", Guid.NewGuid());
        Assert.Same(existing, presenter.NotificationWindowForTest);
        Assert.Equal(0, settings.General.SavedLocationTipCount);
        presenter.Dismiss();
        Set(app, "_preparingAdvancedCapture", true);
        Invoke(app, "ShowSavedLocationReceipt", Guid.NewGuid());
        Set(app, "_preparingAdvancedCapture", false);
        Assert.False(presenter.HasNotification);
        Assert.Equal(0, settings.General.SavedLocationTipCount);
    });

    [Fact]
    public void SelectingOverlaySkipsWithoutADeferredReceipt() => StaTestHost.Run(() =>
    {
        using var presenter = Presenter();
        var settings = new AppSettings();
        App app = Shell(settings, presenter);
        var coordinator = (CaptureOverlayCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(CaptureOverlayCoordinator));
        typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", Flags)!.SetValue(coordinator,
            RuntimeHelpers.GetUninitializedObject(typeof(CaptureOverlayWindow)));
        Set(app, "_overlay", coordinator);
        Invoke(app, "ShowSavedLocationReceipt", Guid.NewGuid());
        typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", Flags)!.SetValue(coordinator, null);
        Assert.False(presenter.HasNotification);
        Assert.Equal(0, settings.General.SavedLocationTipCount);
    });

    [Fact]
    public void ReceiptStillFadingOutBlocksTheSavedLocationTip() => StaTestHost.Run(() =>
    {
        using var exclusion = new ExclusionMode(true);
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        using var presenter = Presenter();
        try
        {
            var settings = new AppSettings();
            App app = Shell(settings, presenter);
            presenter.ShowCaptureReceipt("Existing", () => { }, answersCommand: true);
            presenter.Dismiss();
            Assert.NotEmpty(presenter.LeavingCardsForTest);
            Assert.True(presenter.HasVisibleNotification);
            Invoke(app, "ShowSavedLocationReceipt", Guid.NewGuid());
            Assert.False(presenter.HasNotification);
            Assert.Equal(0, settings.General.SavedLocationTipCount);
            foreach (Window card in presenter.LeavingCardsForTest.ToArray()) FluidMotion.CompleteExit(card);
            Assert.False(presenter.HasVisibleNotification);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    private static void Choose(QuickAccessWindow card, string choice)
    {
        if (choice == "Library") card.LibraryButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        else if (choice == "Replaced") card.CloseNow();
        else if (choice != "Close") card.ShortcutActions[choice]();
    }

    private static ThemedShellPresenter Presenter() => new(() => false)
    {
        NotificationMonitor = () => new MonitorInfo("TEST", new RectD(-20000, -20000, 1080, 1920),
            new RectD(-20000, -20000, 1080, 1880), 96, false),
    };

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static App Shell(AppSettings settings, ThemedShellPresenter presenter)
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        Set(app, "_settings", settings);
        Set(app, "_shellPresenter", presenter);
        return app;
    }

    private static void Set(App app, string field, object value) => typeof(App).GetField(field, Flags)!.SetValue(app, value);
    private static void Invoke(App app, string method, params object[] args) => typeof(App).GetMethod(method, Flags)!.Invoke(app, args);
}
