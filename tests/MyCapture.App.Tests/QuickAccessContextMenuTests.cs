using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class QuickAccessContextMenuTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void CardMenuHasEveryExistingActionInButtonOrderWithItsGesture(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        QuickAccessWindow card = CreateCard();
        try
        {
            MenuItem[] actions = card.CardMenu.Items.OfType<MenuItem>().Take(5).ToArray();
            Assert.Equal(QuickAccessWindow.ActionOrder, actions.Select(item => (string)item.Tag));
            for (int index = 0; index < actions.Length; index++)
            {
                string action = QuickAccessWindow.ActionOrder[index];
                Assert.Equal(AutomationProperties.GetName(card.ActionButtonsForTest[index]), actions[index].Header);
                Assert.Equal(ShortcutCatalog.Get(ShortcutScope.QuickAccess, action).Hint, actions[index].InputGestureText);
                Assert.Equal(actions[index].Header, AutomationProperties.GetName(actions[index]));
                Assert.Equal(actions[index].InputGestureText, AutomationProperties.GetAcceleratorKey(actions[index]));
            }
            Assert.IsType<Separator>(card.CardMenu.Items[5]);
            Assert.Equal("OpenWith", Assert.IsAssignableFrom<MenuItem>(card.CardMenu.Items[6]).Tag);
            Assert.Equal(7, card.CardMenu.Items.Count);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData("Edit")]
    [InlineData("Pin")]
    [InlineData("Save")]
    [InlineData("CopyText")]
    [InlineData("Copy")]
    public void EachMenuActionRunsTheSameBoundOperationAsItsButton(string action) => StaTestHost.Run(() =>
    {
        foreach (bool useMenu in new[] { false, true })
        {
            var called = new List<string>();
            QuickAccessWindow card = CreateCard(called.Add);
            try
            {
                if (useMenu)
                    card.CardMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, action))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                else card.ActionButtonsForTest[QuickAccessWindow.ActionOrder.ToList().IndexOf(action)]
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal([action], called);
            }
            finally { card.Close(); }
        }
    });

    [Fact]
    public Task PointerContextMenuOpeningPausesCountdownAndCloseResumesIt() => RunWithApplicationTheme(
        nameof(PointerContextMenuOpeningPausesCountdownAndCloseResumesIt),
        application => VerifyContextMenuOpening(application, keyboard: false, PlacementMode.MousePoint));

    [Fact]
    public Task KeyboardContextMenuOpeningPausesCountdownAndCloseResumesIt() => RunWithApplicationTheme(
        nameof(KeyboardContextMenuOpeningPausesCountdownAndCloseResumesIt),
        application => VerifyContextMenuOpening(application, keyboard: true, PlacementMode.Bottom));

    private static void VerifyContextMenuOpening(Application application, bool keyboard, PlacementMode placement)
    {
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        QuickAccessWindow card = CreateCard(router: router);
        try
        {
            AssertOpenWithStyle(application, card);
            Show(card);
            card.Advance(TimeSpan.FromSeconds(1), interacting: false);
            TimeSpan before = card.RemainingForTest;
            FrameworkElement preview = card.PreviewForTest;
            Assert.Same(card.ContextMenu, card.CardMenu);
            // WPF chooses the target using the real pointer. Exercise the routed opening event
            // that reaches the product after that framework step, without moving the cursor.
            ContextMenuEventArgs opening = OpeningArgs(preview, keyboard);
            Assert.Equal(keyboard ? -1d : 12d, opening.CursorLeft);
            Assert.Equal(keyboard ? -1d : 8d, opening.CursorTop);
            preview.RaiseEvent(opening);
            OpenWithTestSupport.PumpUntil(() => card.CardMenu.IsOpen);
            Assert.True(opening.Handled);
            Assert.Equal(placement, card.CardMenu.Placement);
            card.Advance(TimeSpan.FromMinutes(1), interacting: false);
            Assert.Equal(before, card.RemainingForTest);
            Assert.False(FluidMotion.IsExiting(card));
            card.CardMenu.IsOpen = false;
            card.Advance(TimeSpan.FromSeconds(1), interacting: false);
            Assert.Equal(before - TimeSpan.FromSeconds(1), card.RemainingForTest);
        }
        finally { card.CardMenu.IsOpen = false; card.Close(); }
    }

    [Fact]
    public Task ActiveCardMenuKeyOpensKeyboardReachableSubmenu() => RunWithApplicationTheme(
        nameof(ActiveCardMenuKeyOpensKeyboardReachableSubmenu),
        application => VerifyKeyboardSubmenu(application, Key.Apps, ModifierKeys.None));

    [Fact]
    public Task ActiveCardShiftF10OpensKeyboardReachableSubmenu() => RunWithApplicationTheme(
        nameof(ActiveCardShiftF10OpensKeyboardReachableSubmenu),
        application => VerifyKeyboardSubmenu(application, Key.F10, ModifierKeys.Shift));

    private static void VerifyKeyboardSubmenu(Application application, Key key, ModifierKeys modifiers)
    {
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        QuickAccessWindow card = CreateCard(router: router);
        var source = new OpenWithTestSource(1); card.OpenWithSource = source;
        try
        {
            AssertOpenWithStyle(application, card);
            Show(card);
            _ = card.ActivateForKeyboard();
            IntPtr handle = new WindowInteropHelper(card).Handle;
            _ = SetActiveWindow(handle);
            Assert.Equal(handle, GetActiveWindow());
            Assert.True(card.IsActive);
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(card), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Assert.True(card.HandleKeyDown(args, modifiers));
            OpenWithTestSupport.PumpUntil(() => card.CardMenu.IsOpen);
            Assert.Equal(0, source.Queries);
            var openWith = (MenuItem)card.CardMenu.Items[6];
            Assert.True(openWith.Focusable);
            Assert.True(openWith.Focus());
            var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(openWith), Environment.TickCount, Key.Right)
                { RoutedEvent = Keyboard.KeyDownEvent };
            openWith.RaiseEvent(right);
            Assert.True(right.Handled);
            OpenWithTestSupport.PumpUntil(() => openWith.IsSubmenuOpen && source.Queries == 1);
            Assert.True(((MenuItem)openWith.Items[0]).Focusable);
            Assert.Equal(source.Set.Handlers[0].Name, AutomationProperties.GetName((MenuItem)openWith.Items[0]));
        }
        finally { card.CardMenu.IsOpen = false; card.Close(); }
    }

    [Fact]
    public Task MouseMenuLeavesSyntheticDestinationForegroundAndCardNonactivating() => RunWithApplicationTheme(
        nameof(MouseMenuLeavesSyntheticDestinationForegroundAndCardNonactivating), application =>
    {
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        var destination = new Window { Content = new Button { Content = "Synthetic destination" }, Width = 150, Height = 100,
            Left = -30000, Top = -30000, ShowInTaskbar = false };
        QuickAccessWindow card = CreateCard(router: router);
        try
        {
            AssertOpenWithStyle(application, card);
            destination.Show();
            Show(card);
            IntPtr destinationHandle = new WindowInteropHelper(destination).Handle;
            _ = SetActiveWindow(destinationHandle);
            Assert.Equal(destinationHandle, GetActiveWindow());
            IntPtr before = GetForegroundWindow();
            Assert.False(card.ShowActivated);
            card.OpenCardMenu(PlacementMode.MousePoint);
            OpenWithTestSupport.PumpUntil(() => card.CardMenu.IsLoaded);
            Assert.Equal(before, GetForegroundWindow());
            Assert.Equal(destinationHandle, GetActiveWindow());
            Assert.False(card.IsActive);
            Assert.False(card.CardMenu.Focusable);
            Assert.All(card.CardMenu.Items.OfType<MenuItem>(), item => Assert.False(item.Focusable));
        }
        finally { card.CardMenu.IsOpen = false; card.Close(); destination.Close(); }
    });

    [Fact]
    public Task ApplicationThemeBeforeTheCardRealPopupOpens() => RunWithApplicationTheme(
        nameof(ApplicationThemeBeforeTheCardRealPopupOpens),
        application => VerifyRealPopup(application, excludeFromCapture: false));

    [Fact]
    public Task ApplicationThemeBeforeTheExcludedCardRealPopupOpens() => RunWithApplicationTheme(
        nameof(ApplicationThemeBeforeTheExcludedCardRealPopupOpens),
        application => VerifyRealPopup(application, excludeFromCapture: true));

    private static void VerifyRealPopup(Application application, bool excludeFromCapture)
    {
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        QuickAccessWindow card = CreateCard(router: router, excludeFromCapture: excludeFromCapture);
        var source = new OpenWithTestSource(1); card.OpenWithSource = source;
        try
        {
            MenuItem item = AssertOpenWithStyle(application, card);
            Show(card);
            card.OpenCardMenu(PlacementMode.Bottom);
            OpenWithTestSupport.PumpUntil(() => card.CardMenu.IsLoaded);
            Assert.True(card.CardMenu.IsOpen);
            Assert.NotNull(PresentationSource.FromVisual(card.CardMenu));
            Assert.NotNull(item.Style);
            Assert.Same(application.FindResource(typeof(MenuItem)), item.Style);
            // Open each popup once; immediate parent-menu reopen hangs WPF on pen/touch hosts.
            item.IsSubmenuOpen = true;
            OpenWithTestSupport.PumpUntil(() => item.IsSubmenuOpen && source.Queries == 1);
            var popup = Assert.IsType<Popup>(item.Template.FindName("PART_Popup", item));
            Assert.True(popup.IsOpen);
            Assert.NotNull(PresentationSource.FromVisual(Assert.IsAssignableFrom<Visual>(popup.Child)));
        }
        finally { card.CardMenu.IsOpen = false; card.Close(); }
    }

    [Fact]
    public Task OpenWithUsesBoundExportAndFailureStaysOnExistingStatusWithoutRetry() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        int exports = 0;
        QuickAccessWindow card = CreateCard(export: () => { exports++; return Task.FromResult("owned-export.png"); });
        var source = new OpenWithTestSource(1) { Invoke = _ => Task.FromException(new InvalidOperationException("synthetic launch failure")) };
        card.OpenWithSource = source;
        try
        {
            var item = (MenuItem)card.CardMenu.Items[6];
            item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
            ((MenuItem)item.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await OpenWithTestSupport.WaitUntilAsync(() => card.StatusForTest.Kind == FeedbackKind.ActionRequired);
            Assert.Equal(1, exports);
            Assert.Equal(["owned-export.png"], source.InvokedPaths);
            Assert.Equal(UiText.Format("OpenWith.Failed", UiText.Get("OpenWith.Unavailable")), card.StatusForTest.Text);
            Assert.True(card.StatusForTest.Visible);
            TimeSpan before = card.RemainingForTest;
            card.Advance(TimeSpan.FromMinutes(1), interacting: false);
            Assert.Equal(before, card.RemainingForTest);
            Assert.Equal(1, exports);
            Assert.Single(source.InvokedPaths);
        }
        finally { card.Close(); }
    });

    [Fact]
    public Task SlowExportUsesExistingProgressLineAndKeepsTheCardAlive() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var exported = new TaskCompletionSource<string>();
        QuickAccessWindow card = CreateCard(export: () => exported.Task);
        var source = new OpenWithTestSource(1); card.OpenWithSource = source;
        try
        {
            var item = (MenuItem)card.CardMenu.Items[6];
            item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
            ((MenuItem)item.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(UiText.Get("OpenWith.Preparing"), card.StatusForTest.Text);
            Assert.Equal(FeedbackKind.Progress, card.StatusForTest.Kind);
            Assert.Empty(source.InvokedPaths);
            card.ElapseProgressDelayForTest();
            Assert.True(card.StatusForTest.Progress);
            exported.SetResult("owned-export.png");
            await OpenWithTestSupport.WaitUntilAsync(() => card.StatusForTest.Kind == FeedbackKind.Confirmation);
            Assert.Equal(["owned-export.png"], source.InvokedPaths);
            Assert.False(card.StatusForTest.Progress);
        }
        finally { card.Close(); }
    });

    [Fact]
    public Task CancellingChooserEndsProgressWithoutSuccessOrFailure() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        QuickAccessWindow card = CreateCard();
        var source = new OpenWithTestSource(0) { Choose = _ => Task.FromException(new OperationCanceledException()) };
        card.OpenWithSource = source;
        try
        {
            var item = (MenuItem)card.CardMenu.Items[6];
            item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
            Assert.IsType<MenuItem>(Assert.Single(item.Items)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await OpenWithTestSupport.WaitUntilAsync(() => source.Set.Disposed);
            Assert.Single(source.ChosenPaths);
            Assert.NotEqual(UiText.Get("OpenWith.Opened"), card.StatusForTest.Text);
            Assert.NotEqual(FeedbackKind.ActionRequired, card.StatusForTest.Kind);
            Assert.False(card.StatusForTest.Progress);
        }
        finally { card.Close(); }
    });

    [Fact]
    public Task OpaqueCardAlsoMakesSubmenuOpaqueBeforeItCreatesAHwnd() => SettingsThemePreviewTests.RunIsolated(
        nameof(OpaqueCardAlsoMakesSubmenuOpaqueBeforeItCreatesAHwnd), () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        Func<bool> previous = CaptureWindowExclusion.LayeredWindowProbe;
        CaptureWindowExclusion.LayeredWindowProbe = () => false;
        QuickAccessWindow? card = null;
        try
        {
            card = CreateCard(excludeFromCapture: true);
            Assert.IsType<OpaqueCaptureContextMenu>(card.CardMenu);
            card.CardMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            card.CardMenu.Arrange(new Rect(card.CardMenu.DesiredSize));
            card.CardMenu.UpdateLayout();
            var item = (MenuItem)card.CardMenu.Items[6];
            item.ApplyTemplate();
            var popup = Assert.IsType<Popup>(item.Template.FindName("PART_Popup", item));
            Assert.False(popup.AllowsTransparency);
            Assert.Equal(PopupAnimation.None, popup.PopupAnimation);
            var border = Assert.IsType<Border>(popup.Child);
            Assert.Equal(new CornerRadius(0), border.CornerRadius);
            Assert.Null(border.Effect);
            Assert.Equal((byte)255, Assert.IsType<SolidColorBrush>(border.Background).Color.A);
        }
        finally { card?.Close(); CaptureWindowExclusion.LayeredWindowProbe = previous; application.Shutdown(); }
    }, typeof(QuickAccessContextMenuTests));

    [Fact]
    public void RequestedOpenMenuPausesCountdownAndClosingResumesWithoutPointerInput() => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = CreateCard();
        try
        {
            card.Advance(TimeSpan.FromSeconds(1), interacting: false);
            TimeSpan before = card.RemainingForTest;
            // This proves the countdown's menu lifecycle separately from physical mouse input.
            // The disconnected target keeps native popup creation pending; no pointer is needed.
            card.OpenCardMenu(PlacementMode.Bottom);
            Assert.True(card.CardMenu.IsOpen);
            Assert.Null(PresentationSource.FromVisual(card.CardMenu));
            card.Advance(TimeSpan.FromMinutes(1), interacting: false);
            Assert.Equal(before, card.RemainingForTest);
            Assert.False(FluidMotion.IsExiting(card));
            card.CardMenu.IsOpen = false;
            card.Advance(TimeSpan.FromSeconds(1), interacting: false);
            Assert.Equal(before - TimeSpan.FromSeconds(1), card.RemainingForTest);
        }
        finally { card.CardMenu.IsOpen = false; card.Close(); }
    });

    internal static QuickAccessWindow CreateCard(Action<string>? called = null, Func<Task<string>>? export = null,
        ReceiptKeyboardRouter? router = null, bool excludeFromCapture = false) =>
        new(OpenWithTestSupport.Image(), () => { called?.Invoke("Copy"); return Task.FromResult(true); },
            () => called?.Invoke("Edit"), () => { called?.Invoke("Pin"); return Task.CompletedTask; },
            () => { called?.Invoke("Save"); return Task.FromResult(true); }, () => { },
            export ?? (() => Task.FromResult("unused-synthetic.png")), excludeFromCapture: excludeFromCapture,
            keyboardRouter: router, copyText: () =>
            { called?.Invoke("CopyText"); return Task.FromResult(new TextCaptureOutcome(TextCaptureStatus.Copied, null, 1)); });

    [Fact]
    public void MissingPresentationSourceClearsRequestedSubmenuOpenState() => StaTestHost.Run(() =>
    {
        var item = new MenuItem { Header = "Synthetic submenu" };
        item.Items.Add(new MenuItem { Header = "Synthetic program" });
        item.IsSubmenuOpen = true;
        // WPF coerces the effective value while the detached item is not loaded. The guard must
        // also clear its requested state so it cannot reopen if the item later acquires a source.
        Assert.True(Assert.IsType<bool>(item.ReadLocalValue(MenuItem.IsSubmenuOpenProperty)));
        var popup = new Popup { Child = new Border() };
        Assert.Null(PresentationSource.FromVisual(popup.Child));
        CaptureWindowExclusion.ExcludeSubmenuPopup(item, popup);
        Assert.False(Assert.IsType<bool>(item.ReadLocalValue(MenuItem.IsSubmenuOpenProperty)));
        Assert.False(item.IsSubmenuOpen);
    });

    [Fact]
    public void ExclusionFailureClosesSubmenuWithoutAPresentationSource() => StaTestHost.Run(() =>
    {
        var item = new MenuItem { Header = "Synthetic submenu" };
        item.Items.Add(new MenuItem { Header = "Synthetic program" });
        var menu = new Menu(); menu.Items.Add(item);
        var host = new Window { Content = menu, ShowActivated = false, ShowInTaskbar = false, Left = -30000, Top = -30000,
            Width = 180, Height = 100 };
        try
        {
            host.Show();
            OpenWithTestSupport.PumpUntil(() => item.IsLoaded);
            item.IsSubmenuOpen = true;
            Assert.True(item.IsSubmenuOpen);
            var popup = new Popup { Child = new Border() };
            Assert.Null(PresentationSource.FromVisual(popup.Child));
            CaptureWindowExclusion.ExcludeSubmenuPopup(item, popup);
            Assert.False(item.IsSubmenuOpen);
        }
        finally { item.IsSubmenuOpen = false; host.Close(); }
    });

    private static Task RunWithApplicationTheme(string testName, Action<Application> verify) =>
        SettingsThemePreviewTests.RunIsolated(testName, () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
                verify(application);
            }
            finally { application.Shutdown(); }
        }, typeof(QuickAccessContextMenuTests));

    private static MenuItem AssertOpenWithStyle(Application application, QuickAccessWindow card)
    {
        var item = Assert.IsAssignableFrom<MenuItem>(card.CardMenu.Items[6]);
        var style = Assert.IsType<Style>(application.FindResource(typeof(MenuItem)));
        Assert.True(ReferenceEquals(style, item.Style),
            "Open with must resolve the application's MenuItem style before its popup is created.");
        return item;
    }

    private static ContextMenuEventArgs OpeningArgs(UIElement source, bool keyboard)
    {
        ConstructorInfo constructor = typeof(ContextMenuEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(object), typeof(bool), typeof(double), typeof(double)])
            ?? throw new InvalidOperationException(
                "WPF ContextMenuEventArgs constructor shape changed: expected (object source, bool opening, double left, double top).");
        var args = (ContextMenuEventArgs)constructor.Invoke([source, true, keyboard ? -1d : 12d, keyboard ? -1d : 8d]);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        return args;
    }

    private static void Show(QuickAccessWindow card)
    {
        FluidMotion.SetWindowEntrance(card, false);
        card.Show(); card.UpdateLayout();
    }

    internal sealed class NoHotkeys : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 1409; errorMessage = "Synthetic conflict"; return false; }
        public void Unregister(int id) { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
