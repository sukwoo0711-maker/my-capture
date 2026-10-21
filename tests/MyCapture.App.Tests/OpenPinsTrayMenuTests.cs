using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OpenPinsTrayMenuTests
{
    [Theory]
    [InlineData("ko-KR", "열린 핀", "텍스트", "코드", "표", "이미지")]
    [InlineData("en-US", "Open pins", "Text", "Code", "Table", "Image")]
    public void TitlesUseFirstNonblankLineTableCellsAndLocalizedKinds(
        string language, string branchName, string textKind, string codeKind, string tableKind, string imageKind) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        const string code = "#define MAX_LEN 256";
        var codeDocument = ClipboardCodeRenderer.TryParse(code, "<pre>#define MAX_LEN 256</pre>");
        Assert.NotNull(codeDocument);
        PinWindow[] pins =
        [
            Window(PinContent.FromText(Image(), "\r\n \t \r\n  First\tline  \r\nsecond", false)),
            Window(PinContent.FromSession(Image(), PinContentKind.Text, code, "", true, PinTextStyle.Code, codeDocument)),
            Window(PinContent.FromText(Image(), "\r\n Name \t Count \r\npencil\t2", true)),
            Window(PinContent.FromTablePicture(Image(), "Product\tPrice\nTea\t3")),
            Window(PinContent.FromImage(Image())),
        ];
        try
        {
            IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(Commands(), new HotkeySettings(),
                new TrayMenuState(PinCount: pins.Length, OpenPins: pins));
            ThemedShellPresenter.MenuEntry branch = Assert.Single(menu, entry => entry.Label == branchName);
            Assert.Null(branch.Action);
            Assert.Equal([
                $"First line · {textKind}", $"{code} · {codeKind}", $"Name | Count · {tableKind}",
                $"Product | Price · {tableKind}", $"32 × 24 px · {imageKind}"],
                branch.Children!.Select(entry => entry.Label));
            Assert.All(branch.Children!, entry => Assert.True(entry.IsEnabled));
            int branchIndex = menu.ToList().IndexOf(branch);
            Assert.Equal(UiText.Get("Tray_PinClipboard"), menu[branchIndex - 1].Label);
            Assert.Equal(UiText.Get("Tray_HideAllPins"), menu[branchIndex + 1].Label);
        }
        finally { foreach (PinWindow pin in pins) pin.Close(); }
    });

    [Fact]
    public void LongTitlesKeepWholeTextElementsAndUseAnEllipsis() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        string source = string.Concat(Enumerable.Repeat("👩‍💻", TrayMenuLayout.MaxPinTitleLength + 1));
        PinWindow pin = Window(PinContent.FromText(Image(), source, false));
        try
        {
            ThemedShellPresenter.MenuEntry entry = Assert.Single(Branch(TrayMenuLayout.Build(Commands(), new HotkeySettings(),
                new TrayMenuState(PinCount: 1, OpenPins: [pin]))).Children!);
            string expected = string.Concat(Enumerable.Repeat("👩‍💻", TrayMenuLayout.MaxPinTitleLength - 1)) + "…";
            Assert.Equal(expected + " · Text", entry.Label);
            Assert.Equal(TrayMenuLayout.MaxPinTitleLength, new StringInfo(expected).LengthInTextElements);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VeryLargeSourcesUseBoundedTitleAllocations(bool isTable) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        string source = new string(' ', 500_000) + "\r\n"
            + (isTable ? "  Name \t  " : "  ") + new string('x', 1_000_000)
            + (isTable ? "\t ignored" : "\tignored");
        PinWindow pin = Window(PinContent.FromText(Image(), source, isTable));
        try
        {
            TrayMenuCommands commands = Commands();
            var keys = new HotkeySettings();
            var state = new TrayMenuState(PinCount: 1, OpenPins: [pin]);
            _ = TrayMenuLayout.Build(commands, keys, state);
            long before = GC.GetAllocatedBytesForCurrentThread();
            IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(commands, keys, state);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            string prefix = isTable ? "Name | " + new string('x', 32) : new string('x', 39);

            Assert.Equal(prefix + "… · " + (isTable ? "Table" : "Text"), Assert.Single(Branch(menu).Children!).Label);
            Assert.True(allocated < 128 * 1024, $"Building a bounded title allocated {allocated:N0} bytes.");
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void TableTitlesKeepEmptyCellsAndTrimOnlyCellWhitespace() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        PinWindow pin = Window(PinContent.FromText(Image(), "\t \t\r\n\t  Name  \t\t Count \t ", true));
        try
        {
            IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(Commands(), new HotkeySettings(),
                new TrayMenuState(PinCount: 1, OpenPins: [pin]));
            Assert.Equal(" | Name |  | Count |  · Table", Assert.Single(Branch(menu).Children!).Label);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void OversizedGraphemesAreOmittedWholeWithoutLargeAllocations() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        PinWindow pin = Window(PinContent.FromText(Image(), "ok " + "e" + new string('\u0301', 1_000_000), false));
        try
        {
            TrayMenuCommands commands = Commands();
            var keys = new HotkeySettings();
            var state = new TrayMenuState(PinCount: 1, OpenPins: [pin]);
            _ = TrayMenuLayout.Build(commands, keys, state);
            long before = GC.GetAllocatedBytesForCurrentThread();
            IReadOnlyList<ThemedShellPresenter.MenuEntry> menu = TrayMenuLayout.Build(commands, keys, state);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal("ok… · Text", Assert.Single(Branch(menu).Children!).Label);
            Assert.True(allocated < 128 * 1024, $"Building a bounded title allocated {allocated:N0} bytes.");
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "그 외 3개")]
    [InlineData("en-US", "3 more")]
    public void NewestPinsComeFirstAndHiddenPinsKeepTheCappedList(string language, string more) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PinManager manager = Manager();
        var opened = new List<PinWindow>();
        try
        {
            for (int i = 1; i <= TrayMenuLayout.MaxOpenPinEntries + 3; i++)
                opened.Add(manager.PinImage(Image(i + 32)));
            Assert.Equal(opened.AsEnumerable().Reverse(), manager.SnapshotOpenPinWindows());
            IReadOnlyList<ThemedShellPresenter.MenuEntry> shown = Branch(Build(manager)).Children!;
            manager.HideOrShowAll();
            Assert.True(manager.AreHidden);
            Assert.All(opened, pin => Assert.False(pin.IsVisible));
            IReadOnlyList<ThemedShellPresenter.MenuEntry> hidden = Branch(Build(manager)).Children!;

            Assert.Equal(TrayMenuLayout.MaxOpenPinEntries + 1, shown.Count);
            Assert.Equal(shown.Select(entry => entry.Label), hidden.Select(entry => entry.Label));
            Assert.Equal(opened.AsEnumerable().Reverse().Take(TrayMenuLayout.MaxOpenPinEntries)
                .Select(pin => $"{pin.PinnedContent.Image.PixelWidth} × 24 px · {UiText.Get("Tray_PinKindImage")}"),
                shown.Take(TrayMenuLayout.MaxOpenPinEntries).Select(entry => entry.Label));
            Assert.Equal(more, shown[^1].Label);
            Assert.False(shown[^1].IsEnabled);
            Assert.Null(shown[^1].Action);

            manager.CloseAllForExit();
            Assert.DoesNotContain(Build(manager), entry => entry.Label == UiText.Get("Tray_OpenPins"));
        }
        finally
        {
            manager.CloseAllForExit();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChoosingAPinShowsHiddenPinsAndClearsOnlyItsClickThrough(bool hidden) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var activated = new List<PinWindow>();
        PinManager manager = Manager(activated.Add);
        try
        {
            PinWindow first = manager.PinImage(Image());
            PinWindow chosen = manager.PinImage(Image(64));
            PinWindow last = manager.PinImage(Image(96));
            first.ApplyClickThrough(true);
            chosen.ApplyClickThrough(true);
            if (hidden) manager.HideOrShowAll();
            Assert.All(new[] { first, chosen, last }, pin => Assert.Equal(!hidden, pin.IsVisible));
            activated.Clear();
            ThemedShellPresenter.MenuEntry entry = Assert.Single(Branch(Build(manager)).Children!,
                item => item.Label == "64 × 24 px · Image");

            entry.Action!();

            Assert.False(manager.AreHidden);
            Assert.All(new[] { first, chosen, last }, pin => Assert.True(pin.IsVisible));
            Assert.False(chosen.State.IsClickThrough);
            Assert.True(first.State.IsClickThrough);
            Assert.False(last.State.IsClickThrough);
            Assert.Equal([chosen], activated);
        }
        finally
        {
            manager.CloseAllForExit();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void ChoosingAnEntryClosedSinceTheMenuOpenedDoesNothing() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var activated = new List<PinWindow>();
        PinManager manager = Manager(activated.Add);
        try
        {
            PinWindow remaining = manager.PinImage(Image());
            PinWindow closed = manager.PinImage(Image(64));
            ThemedShellPresenter.MenuEntry stale = Branch(Build(manager)).Children![0];
            closed.Close();
            remaining.ApplyClickThrough(true);
            manager.HideOrShowAll();
            activated.Clear();

            stale.Action!();

            Assert.Empty(activated);
            Assert.True(manager.AreHidden);
            Assert.False(remaining.IsVisible);
            Assert.True(remaining.State.IsClickThrough);
            Assert.Equal([remaining], manager.SnapshotOpenPinWindows());
            Assert.Single(Branch(Build(manager)).Children!);
        }
        finally
        {
            manager.CloseAllForExit();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void SavedOpeningOrderStaysBehindNewPinsRegardlessOfRestoreOrStackingOrder() => StaTestHost.Run(() =>
    {
        PinManager manager = Manager();
        try
        {
            PinWindow live = manager.PinImage(Image());
            PinWindow newerSaved = manager.RestoreSessionPin(new PinManager.ClosedPin(
                PinContent.FromImage(Image(64)), 64, 24, 1, 1, null, OpenedOrder: 12));
            PinWindow olderSaved = manager.RestoreSessionPin(new PinManager.ClosedPin(
                PinContent.FromImage(Image(96)), 96, 24, 1, 1, null, OpenedOrder: 4));
            manager.ReadPinOrderForTest = _ => [live.Handle, newerSaved.Handle, olderSaved.Handle];
            Assert.Equal([live, newerSaved, olderSaved], manager.SnapshotOpenPinWindows());
            PinWindow newest = manager.PinImage(Image(128));
            Assert.Equal([newest, live, newerSaved, olderSaved], manager.SnapshotOpenPinWindows());
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void OpenPinAutomationNamesPreserveTitleAndKindIncludingCodeUnderscores() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        const string text = "#define MAX_LEN 256";
        var document = ClipboardCodeRenderer.TryParse(text, "<pre>#define MAX_LEN 256</pre>");
        Assert.NotNull(document);
        PinWindow pin = Window(PinContent.FromSession(Image(), PinContentKind.Text, text, "", true, PinTextStyle.Code, document));
        using var presenter = new ThemedShellPresenter(() => false);
        try
        {
            ContextMenu menu = presenter.CreateMenu(TrayMenuLayout.Build(Commands(), new HotkeySettings(),
                new TrayMenuState(PinCount: 1, OpenPins: [pin])));
            MenuItem branch = Assert.Single(menu.Items.OfType<MenuItem>(), item => (string)item.Header == "Open pins");
            MenuItem entry = Assert.IsAssignableFrom<MenuItem>(Assert.Single(branch.Items.Cast<object>()));
            Assert.Equal("Open pins", AutomationProperties.GetName(branch));
            Assert.Equal(text + " · Code", AutomationProperties.GetName(entry));
            MenuItemAutomationPeer peer = Assert.IsAssignableFrom<MenuItemAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(entry));
            Assert.Equal(text + " · Code", peer.GetName());
            Assert.NotNull(entry.HeaderTemplate);
            var header = new ContentPresenter
            {
                Content = entry.Header,
                ContentTemplate = entry.HeaderTemplate,
                RecognizesAccessKey = true,
            };
            header.Measure(new Size(1000, 100));
            header.Arrange(new Rect(0, 0, 1000, 100));
            TextBlock visibleTitle = Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(header, 0));
            Assert.Equal(text + " · Code", visibleTitle.Text);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedMenuReleasesPinsContentAndAutomationPeers(bool chooseEntry) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        using var presenter = new ThemedShellPresenter(() => false);
        try
        {
            WeakReference[] references = CreateClosedMenuReferences(presenter, chooseEntry);
            Assert.Null(presenter.MenuForTest);
            for (int attempt = 0; attempt < 5 && references.Any(reference => reference.IsAlive); attempt++)
            {
                DrainDispatcher();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.All(references, reference => Assert.False(reference.IsAlive));
            GC.KeepAlive(presenter);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });

    [Fact]
    public void ClosingAnEarlierMenuDoesNotReleaseItsReplacement() => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        ContextMenu earlier = presenter.CreateMenu([]);
        ContextMenu current = presenter.CreateMenu([]);
        presenter.TrackMenu(earlier);
        presenter.TrackMenu(current);

        earlier.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));

        Assert.Same(current, presenter.MenuForTest);
        current.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        Assert.Null(presenter.MenuForTest);
    });

    [Fact]
    public void TraySelectionUsesRealShowWithAnExplicitPassiveShowPolicy() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var activated = new List<PinWindow>();
        PinManager manager = Manager(activated.Add);
        try
        {
            Assert.Null(manager.ShowPinForTest);
            PinWindow pin = manager.PinImage(Image());
            Assert.True(pin.IsVisible);
            Assert.False(pin.ShowActivated);
            manager.HideOrShowAll();
            Assert.False(pin.IsVisible);
            activated.Clear();

            manager.ActivateOpenPin(pin);

            Assert.True(pin.IsVisible);
            Assert.False(pin.ShowActivated);
            Assert.Equal([pin], activated);
        }
        finally
        {
            manager.CloseAllForExit();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateClosedMenuReferences(ThemedShellPresenter presenter, bool chooseEntry)
    {
        PinSettings settings = NewInstallDefaults.Create().Pin;
        settings.ClosedWindowRestoreLimit = 0;
        int activations = 0;
        var manager = new PinManager(() => settings, NullLogger.Instance)
        {
            BeforeShowForTest = pin => pin.ShowActivated = false,
            ActivatePinForTest = _ => activations++,
        };
        try
        {
            PinWindow pin = manager.PinImage(Image());
            activations = 0;
            ContextMenu menu = presenter.CreateMenu(Build(manager));
            presenter.TrackMenu(menu);
            MenuItem branch = Assert.Single(menu.Items.OfType<MenuItem>(),
                item => (string)item.Header == UiText.Get("Tray_OpenPins"));
            MenuItem entry = Assert.IsAssignableFrom<MenuItem>(Assert.Single(branch.Items.Cast<object>()));
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(entry);
            Assert.Equal(AutomationProperties.GetName(entry), peer.GetName());
            var references = new[]
            {
                new WeakReference(pin),
                new WeakReference(pin.PinnedContent),
                new WeakReference(pin.PinnedContent.Image),
                new WeakReference(menu),
                new WeakReference(peer),
            };
            if (chooseEntry)
            {
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(1, activations);
            }

            // Exercise the presenter's real close handler without opening a native popup.
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
            pin.Close();
            Assert.Equal(0, manager.ClosedCount);
            Assert.Equal(0, manager.Count);
            return references;
        }
        finally { manager.CloseAllForExit(); }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IReadOnlyList<ThemedShellPresenter.MenuEntry> Build(PinManager manager) =>
        TrayMenuLayout.Build(Commands(manager.ActivateOpenPin), new HotkeySettings(), new TrayMenuState(
            PinCount: manager.Count, PinsHidden: manager.AreHidden, ClickThroughPinCount: manager.ClickThroughCount,
            OpenPins: manager.SnapshotOpenPinWindows()));

    private static ThemedShellPresenter.MenuEntry Branch(IReadOnlyList<ThemedShellPresenter.MenuEntry> menu) =>
        Assert.Single(menu, entry => entry.Label == UiText.Get("Tray_OpenPins"));

    private static TrayMenuCommands Commands(Action<PinWindow>? activate = null) => new(
        () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
        () => { }, () => { }, () => { }, () => { }, () => { },
        PinClipboard: () => { }, HideOrShowAllPins: () => { }, ActivateOpenPin: activate ?? (_ => { }));

    private static PinManager Manager(Action<PinWindow>? activate = null) => new(
        () => NewInstallDefaults.Create().Pin, NullLogger.Instance)
    {
        BeforeShowForTest = pin => pin.ShowActivated = false,
        ActivatePinForTest = activate ?? (_ => { }),
    };

    private static PinWindow Window(PinContent content) =>
        new(content, new PinViewState(80, 60, 1, 1, 0.1), 100, 100) { ShowActivated = false };

    private static BitmapSource Image(int width = 32)
    {
        BitmapSource image = BitmapSource.Create(width, 24, 96, 96, PixelFormats.Bgra32, null, new byte[width * 24 * 4], width * 4);
        image.Freeze();
        return image;
    }
}
