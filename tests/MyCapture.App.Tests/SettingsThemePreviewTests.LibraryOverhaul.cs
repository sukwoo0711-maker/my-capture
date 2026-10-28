using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The library overhaul: keys and menu commands follow the selection, the card carries its own
/// actions, and the toolbar, notices and menus follow the card. Each test drives the real
/// GalleryWindow BAML.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Theory]
    [InlineData(Key.P, ModifierKeys.None, GalleryKeyCommand.Float)]
    [InlineData(Key.K, ModifierKeys.None, GalleryKeyCommand.Keep)]
    [InlineData(Key.M, ModifierKeys.None, GalleryKeyCommand.Move)]
    [InlineData(Key.T, ModifierKeys.None, GalleryKeyCommand.Recognize)]
    [InlineData(Key.G, ModifierKeys.None, GalleryKeyCommand.ExportGif)]
    [InlineData(Key.Enter, ModifierKeys.None, GalleryKeyCommand.Open)]
    [InlineData(Key.Space, ModifierKeys.None, GalleryKeyCommand.Preview)]
    [InlineData(Key.Space, ModifierKeys.Control, GalleryKeyCommand.ToggleSelect)]
    [InlineData(Key.Space, ModifierKeys.Shift, GalleryKeyCommand.ToggleSelect)]
    [InlineData(Key.Delete, ModifierKeys.None, GalleryKeyCommand.Delete)]
    [InlineData(Key.C, ModifierKeys.Control, GalleryKeyCommand.Copy)]
    [InlineData(Key.A, ModifierKeys.Control, GalleryKeyCommand.SelectAll)]
    [InlineData(Key.F10, ModifierKeys.Shift, GalleryKeyCommand.ContextMenu)]
    [InlineData(Key.Apps, ModifierKeys.None, GalleryKeyCommand.ContextMenu)]
    [InlineData(Key.Left, ModifierKeys.Shift, GalleryKeyCommand.Navigate)]
    // A modifier that is not the key's own turns the command off.
    [InlineData(Key.T, ModifierKeys.Control, GalleryKeyCommand.None)]
    [InlineData(Key.G, ModifierKeys.Shift, GalleryKeyCommand.None)]
    [InlineData(Key.P, ModifierKeys.Alt, GalleryKeyCommand.None)]
    [InlineData(Key.K, ModifierKeys.Control | ModifierKeys.Shift, GalleryKeyCommand.None)]
    [InlineData(Key.M, ModifierKeys.Windows, GalleryKeyCommand.None)]
    [InlineData(Key.Enter, ModifierKeys.Control, GalleryKeyCommand.None)]
    [InlineData(Key.Enter, ModifierKeys.Alt, GalleryKeyCommand.Info)]
    [InlineData(Key.Enter, ModifierKeys.Alt | ModifierKeys.Control, GalleryKeyCommand.None)]
    [InlineData(Key.Delete, ModifierKeys.Shift, GalleryKeyCommand.None)]
    [InlineData(Key.C, ModifierKeys.None, GalleryKeyCommand.None)]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, GalleryKeyCommand.None)]
    [InlineData(Key.Left, ModifierKeys.Alt, GalleryKeyCommand.None)]
    public void LibraryKeyMap_RequiresExactlyTheModifiersOfEachCommand(Key key, ModifierKeys modifiers, GalleryKeyCommand expected) =>
        Assert.Equal(expected, GalleryKeyMap.Resolve(key, modifiers));

    [Fact]
    public Task LibraryOverhaul_InfoShortcutUsesSelectionAndKeepsTextInputUntouched() =>
        RunIsolated(nameof(LibraryOverhaul_InfoShortcutUsesSelectionAndKeepsTextInputUntouched), () =>
        {
            Application application = CreateLibraryMotionApplication();
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-info-");
            GalleryWindow window = f.Window;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                CaptureRecord a = f.Records[0], b = f.Records[2], outside = f.Records[3];
                f.ViewModel.Select(a.Id); f.ViewModel.Select(b.Id, control: true);
                GalleryItemViewModel outsideTile = f.ViewModel.FindTile(outside.Id)!;
                Assert.Equal(window.KeyboardDetailsSources(null), window.KeyboardDetailsSources(outsideTile));
                Assert.Equal(2, window.KeyboardDetailsSources(outsideTile).Count);
                Assert.Single(window.DetailsSources(outsideTile));
                f.ViewModel.ClearSelection();
                Assert.Single(window.KeyboardDetailsSources(outsideTile));
                Assert.Empty(window.KeyboardDetailsSources(null));
                f.ViewModel.Select(a.Id);
                var commands = new List<GalleryKeyCommand>();
                window.KeyCommandHookForTest = command => { commands.Add(command); return true; };
                ListBoxItem card = RealizedCard(rows, a.Id);
                card.Focus();
                using (var modifiers = new ThreadKeyboardModifierScope(ModifierKeys.Alt))
                    Assert.True(PressLibraryKey(card, Key.Enter).Handled);
                Assert.Equal([GalleryKeyCommand.Info], commands);
                var search = (TextBox)window.FindName("SearchBox");
                Assert.True(search.Focus());
                using (var modifiers = new ThreadKeyboardModifierScope(ModifierKeys.Alt))
                    Assert.False(PressLibraryKey(search, Key.Enter).Handled);
                Assert.Single(commands);
                MenuItem info = Assert.Single(window.TileMenu.Items.OfType<MenuItem>(), item => item.Tag as string == "MediaInfo");
                Assert.Equal("Alt+Enter", info.InputGestureText);
                Assert.Equal(ShortcutCatalog.Get(ShortcutScope.Library, "Info").Hint, info.InputGestureText);
            }
            finally { window.KeyCommandHookForTest = null; window.CloseForExit(); application.Shutdown(); }
        });

    [Theory]
    [InlineData(Key.P, GalleryKeyCommand.Float)]
    [InlineData(Key.K, GalleryKeyCommand.Keep)]
    [InlineData(Key.M, GalleryKeyCommand.Move)]
    [InlineData(Key.T, GalleryKeyCommand.Recognize)]
    [InlineData(Key.G, GalleryKeyCommand.ExportGif)]
    public void LibraryKeyMap_ReadsHangulModeLettersAsTheirPhysicalKeys(Key physical, GalleryKeyCommand expected)
    {
        // A Korean IME in Hangul mode reports every letter as ImeProcessed.
        Key resolved = ShortcutInput.Resolve(Key.ImeProcessed, physical, textInputFocused: false);
        Assert.Equal(expected, GalleryKeyMap.Resolve(resolved, ModifierKeys.None));
        Assert.Equal(GalleryKeyCommand.None, GalleryKeyMap.Resolve(
            ShortcutInput.Resolve(Key.ImeProcessed, physical, textInputFocused: true), ModifierKeys.None));
    }

    [Fact]
    public Task LibraryOverhaul_LettersWorkInHangulModeAndStaySilentInTextInput() =>
        RunIsolated(nameof(LibraryOverhaul_LettersWorkInHangulModeAndStaySilentInTextInput), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-hangul-");
            GalleryWindow window = f.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                CaptureRecord image = f.Records[0], video = f.Records[2];
                video.MediaKind = CaptureMediaKind.Video;
                f.ViewModel.Refresh(); SettleGalleryLayout(window);
                ListBoxItem imageCard = RealizedCard(rows, image.Id), videoCard = RealizedCard(rows, video.Id);

                var commands = new List<GalleryKeyCommand>();
                window.KeyCommandHookForTest = command => { commands.Add(command); return true; };
                f.ViewModel.Select(image.Id);
                foreach (Key letter in new[] { Key.P, Key.K, Key.M, Key.T })
                    Assert.True(PressLibraryKey(imageCard, letter, ime: true).Handled, $"Hangul mode {letter}");
                f.ViewModel.Select(video.Id);
                Assert.True(PressLibraryKey(videoCard, Key.G, ime: true).Handled);
                Assert.Equal([GalleryKeyCommand.Float, GalleryKeyCommand.Keep, GalleryKeyCommand.Move,
                    GalleryKeyCommand.Recognize, GalleryKeyCommand.ExportGif], commands);

                // The same keys typed into a text box stay with the IME.
                commands.Clear();
                var search = (TextBox)window.FindName("SearchBox");
                Assert.True(search.Focus());
                Assert.False(PressLibraryKey(imageCard, Key.P, ime: true).Handled);
                Assert.False(PressLibraryKey(imageCard, Key.K, ime: false).Handled);
                Assert.Empty(commands);
            }
            finally
            {
                window.KeyCommandHookForTest = null;
                window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });

    [Fact]
    public Task LibraryOverhaul_DeleteKeepAndMoveFollowTheSelectionByMenuAndKey() =>
        RunIsolated(nameof(LibraryOverhaul_DeleteKeepAndMoveFollowTheSelectionByMenuAndKey), () =>
        {
            Application application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-scope-");
            GalleryWindow window = f.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                CaptureRecord a = f.Records[0], b = f.Records[2], outside = f.Records[3];
                var deletes = new List<int>();
                window.ConfirmDelete = (count, _) => { deletes.Add(count); return false; };

                // Delete by key: the selection, even when the card with focus is not part of it.
                f.ViewModel.Select(a.Id); f.ViewModel.Select(b.Id, control: true);
                Assert.True(PressLibraryKey(RealizedCard(rows, b.Id), Key.Delete).Handled);
                Assert.True(PressLibraryKey(RealizedCard(rows, outside.Id), Key.Delete).Handled);
                Assert.Equal([2, 2], deletes);
                // Nothing selected: the card with focus is the target.
                f.ViewModel.ClearSelection();
                Assert.True(PressLibraryKey(RealizedCard(rows, outside.Id), Key.Delete).Handled);
                Assert.Equal([2, 2, 1], deletes);

                // Delete by menu: the whole selection when the clicked card belongs to it, else that card.
                deletes.Clear();
                f.ViewModel.Select(a.Id); f.ViewModel.Select(b.Id, control: true);
                MenuItem delete = TileMenuItem(window, "Text_6139B6C3ED73");
                ClickTileMenuItem(delete, f.ViewModel.FindTile(b.Id)!);
                ClickTileMenuItem(delete, f.ViewModel.FindTile(outside.Id)!);
                Assert.Equal([2, 1], deletes);

                // Keep by key and menu: kept and released together.
                var changes = 0;
                window.CaptureChanged += (_, _) => changes++;
                CaptureRecord kept = f.Records[1];
                Assert.True(kept.IsManuallyPinned);
                f.ViewModel.ClearSelection();
                f.ViewModel.Select(a.Id); f.ViewModel.Select(b.Id, control: true);
                Assert.True(PressLibraryKey(RealizedCard(rows, a.Id), Key.K).Handled);
                PumpLibraryUntil(window, () => changes == 1, "keep both");
                Assert.True(a.IsManuallyPinned && b.IsManuallyPinned);
                ClickTileMenuItem(TileMenuItem(window, "LibraryUx_KeepToggleName"), f.ViewModel.FindTile(b.Id)!);
                PumpLibraryUntil(window, () => changes == 2, "release both");
                Assert.False(a.IsManuallyPinned || b.IsManuallyPinned);
                // A mixed selection keeps what is not kept yet and releases nothing.
                f.ViewModel.ClearSelection();
                f.ViewModel.Select(kept.Id); f.ViewModel.Select(a.Id, control: true);
                Assert.True(PressLibraryKey(RealizedCard(rows, a.Id), Key.K).Handled);
                PumpLibraryUntil(window, () => changes == 3, "keep the rest");
                Assert.True(kept.IsManuallyPinned && a.IsManuallyPinned);
                // A card outside the selection is its own target in the menu.
                ClickTileMenuItem(TileMenuItem(window, "LibraryUx_KeepToggleName"), f.ViewModel.FindTile(b.Id)!);
                PumpLibraryUntil(window, () => changes == 4, "keep one outside the selection");
                Assert.True(b.IsManuallyPinned);
                Assert.True(kept.IsManuallyPinned && a.IsManuallyPinned);
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });

    [Fact]
    public Task LibraryOverhaul_RightClickSelectsAnUnselectedCardOnly() =>
        RunIsolated(nameof(LibraryOverhaul_RightClickSelectsAnUnselectedCardOnly), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-rightclick-");
            GalleryWindow window = f.Window;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                CaptureRecord a = f.Records[0], b = f.Records[2], c = f.Records[3];
                f.ViewModel.Select(a.Id); f.ViewModel.Select(b.Id, control: true);

                RightClick(RealizedCard(rows, a.Id));
                Assert.Equal(new[] { a.Id, b.Id }.Order(), f.ViewModel.Selection.SelectedIds.Order());
                RightClick(RealizedCard(rows, c.Id));
                Assert.Equal([c.Id], f.ViewModel.Selection.SelectedIds);

                // The Menu key and the More button follow the same rule as the right click.
                ContextMenu menu = window.TileMenu;
                f.ViewModel.Select(a.Id); f.ViewModel.Select(b.Id, control: true);
                Assert.True(PressLibraryKey(RealizedCard(rows, a.Id), Key.Apps).Handled);
                Assert.True(menu.IsOpen);
                Assert.Equal(new[] { a.Id, b.Id }.Order(), f.ViewModel.Selection.SelectedIds.Order());
                menu.IsOpen = false;
                PumpLibraryUntil(window, () => menu.PlacementTarget is null, "the closed menu lets go of its card");
                Assert.True(PressLibraryKey(RealizedCard(rows, c.Id), Key.Apps).Handled);
                Assert.Equal([c.Id], f.ViewModel.Selection.SelectedIds);
                menu.IsOpen = false;
                f.ViewModel.Select(a.Id); f.ViewModel.Select(b.Id, control: true);
                Button more = Descendants(RealizedCard(rows, c.Id)).OfType<Button>().Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == UiText.Get("Text_5E8959D18390"));
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal([c.Id], f.ViewModel.Selection.SelectedIds);
                menu.IsOpen = false;
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryOverhaul_FolderCardsTakeMoveAndDeleteKeysForTheFocusedCard() =>
        RunIsolated(nameof(LibraryOverhaul_FolderCardsTakeMoveAndDeleteKeysForTheFocusedCard), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-folderkeys-");
            // A folder inside the open folder: the commands must reach this card, not the folder the library shows.
            CaptureFolder child = f.Queue.CreateFolderAsync("child-folder", f.Design.Id).GetAwaiter().GetResult();
            f.ViewModel.Refresh();
            GalleryWindow window = f.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                window.Show(); SettleGalleryLayout(window);
                f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
                Button card = PumpLibraryUntil(window, () => FolderCardButtons(window)
                    .FirstOrDefault(button => ((GalleryFolderCardViewModel)button.DataContext).FolderId == child.Id), found => found is not null, "child folder card");
                string childPath = f.Queue.GetFolderDisplayPath(child.Id);

                void Drive(Key key, bool ime, Action<Window> inspect)
                {
                    bool inspected = false;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        Window dialog = application.Windows.Cast<Window>().Single(candidate => candidate is GalleryFolderEditDialog or GalleryFolderDeletionDialog);
                        inspect(dialog);
                        inspected = true;
                        dialog.DialogResult = false;
                    }));
                    Assert.True(PressLibraryKey(card, key, ime).Handled);
                    PumpLibraryUntil(window, () => inspected, key + " dialog");
                }

                // Move: the destinations leave out the card's own folder but offer the folder the library shows.
                foreach (bool ime in new[] { false, true })
                {
                    Drive(Key.M, ime, dialog =>
                    {
                        var choices = ((ListBox)((GalleryFolderEditDialog)dialog).Input).Items.Cast<object>()
                            .Select(item => item.GetType().GetProperty("Name")!.GetValue(item)!.ToString()!).ToArray();
                        Assert.DoesNotContain(choices, name => name.EndsWith(childPath, StringComparison.Ordinal));
                        Assert.Contains(choices, name => name.EndsWith(f.Queue.GetFolderDisplayPath(f.Design.Id), StringComparison.Ordinal));
                    });
                }
                // Delete: the confirmation names the card's folder.
                Drive(Key.Delete, ime: false, dialog =>
                    Assert.Contains(childPath, string.Concat(Descendants(dialog).OfType<TextBlock>().Select(text => text.Text))));
            }
            finally
            {
                window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });

    [Fact]
    public Task LibraryOverhaul_RenameBoxKeepsEveryKeyAndContentKeysSkipTheToolbar() =>
        RunIsolated(nameof(LibraryOverhaul_RenameBoxKeepsEveryKeyAndContentKeysSkipTheToolbar), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-renamebox-");
            GalleryWindow window = f.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                GalleryItemViewModel tile = f.ViewModel.FindTile(f.Records[0].Id)!;
                var commands = new List<GalleryKeyCommand>();
                var deletes = new List<int>();
                window.KeyCommandHookForTest = command => { commands.Add(command); return true; };
                window.ConfirmDelete = (count, _) => { deletes.Add(count); return false; };
                f.ViewModel.Select(tile.Id);
                var begin = window.BeginCaptureRenameAsync(tile);
                PumpLibraryUntil(window, () => begin.IsCompleted, "rename begins");
                TextBox input = PumpLibraryUntil(window, () => Descendants(rows).OfType<TextBox>()
                    .FirstOrDefault(box => box.Name == "TileRenameInput" && ReferenceEquals(box.DataContext, tile)), box => box is { IsVisible: true }, "rename box");

                // Every key a text box uses stays with it: nothing is handled, deleted or run as a command.
                foreach (Key key in new[] { Key.Delete, Key.F2, Key.Space, Key.A, Key.C, Key.P, Key.K, Key.M, Key.T, Key.G,
                    Key.Left, Key.Right, Key.Up, Key.Down, Key.Apps })
                    Assert.False(PressLibraryKey(input, key).Handled, key.ToString());
                Assert.False(PressLibraryKey(input, Key.P, ime: true).Handled);
                Assert.Empty(commands);
                Assert.Empty(deletes);
                Assert.True(tile.IsRenaming);
                // Enter and Esc are the box's own: it commits or cancels, and the content handler never saw them.
                Assert.True(PressLibraryKey(input, Key.Escape).Handled);
                Assert.False(tile.IsRenaming);
                Assert.Empty(commands);
                Assert.Empty(deletes);

                // Keys that start in the content toolbar belong to its controls.
                var toolbar = (FrameworkElement)window.FindName("LibraryContentCommandBar");
                var preview = (Button)window.FindName("PreviewSelectedButton");
                Assert.False(PressLibraryKey(preview, Key.Delete).Handled);
                Assert.False(PressLibraryKey(preview, Key.P).Handled);
                Assert.Empty(commands);
                Assert.Empty(deletes);
                // The same keys from a card do run.
                window.KeyCommandHookForTest = null;
                Assert.True(PressLibraryKey(RealizedCard(rows, tile.Id), Key.Delete).Handled);
                Assert.Equal([1], deletes);
            }
            finally
            {
                window.KeyCommandHookForTest = null; window.ConfirmDelete = null;
                window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });

    [Fact]
    public Task LibraryOverhaul_NoticesLiveOnTheStatusBarInTheirOwnSlot() =>
        RunIsolated(nameof(LibraryOverhaul_NoticesLiveOnTheStatusBarInTheirOwnSlot), () =>
        {
            Application application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var scope = UiText.UseLanguage(language);
                LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-notices-");
                GalleryWindow window = f.Window;
                try
                {
                    window.Show(); SettleGalleryLayout(window);
                    var notice = new MyCapture.App.Store.LibraryEngagementNotice(window);
                    int reviews = 0, installs = 0;
                    var consumed = new List<MyCapture.App.Store.EngagementNoticeKind>();
                    notice.ReviewChosen = () => reviews++;
                    notice.UpdateChosen = () => installs++;
                    notice.ClosedByUser = kind => consumed.Add(kind);
                    var status = (TextBlock)window.FindName("LibraryActionStatus");
                    var version = (TextBlock)window.FindName("CurrentVersionLabel");
                    var update = (Button)window.FindName("UpdateAvailableButton");
                    var rows = (ListBox)window.FindName("RowsList");
                    var bar = (FrameworkElement)window.FindName("LibraryStatusBar");
                    var slot = (FrameworkElement)window.FindName("LibraryNotice");
                    var noticeText = (TextBlock)window.FindName("LibraryNoticeText");
                    string UpdateLabel() => ((TextBlock)update.Content).Text;

                    // No card is ever added over the library's content, and the bar keeps its height.
                    double barHeight = bar.ActualHeight;
                    void AssertNoCardCoversTheLibrary(bool sameHeight = true)
                    {
                        SettleGalleryLayout(window);
                        Assert.Empty(Descendants(window).OfType<MyCapture.App.Store.EngagementNotice>());
                        Rect rowsBounds = new(rows.TranslatePoint(new Point(), window), rows.RenderSize);
                        Rect barBounds = new(bar.TranslatePoint(new Point(), window), bar.RenderSize);
                        foreach (FrameworkElement shown in new FrameworkElement[] { slot, update, version }.Where(element => element.IsVisible))
                        {
                            Rect bounds = new(shown.TranslatePoint(new Point(), window), shown.RenderSize);
                            Assert.False(bounds.IntersectsWith(rowsBounds), $"{shown.Name} covers the cards");
                            Assert.True(barBounds.Contains(bounds), $"{shown.Name}: {bounds} in {barBounds}");
                        }
                        // A result's Undo button is taller than the bar's line; notices and the update button are not.
                        if (sameHeight) Assert.Equal(barHeight, bar.ActualHeight, 1);
                    }

                    // "Updated to X": a neutral one-line notice that goes away by itself and is consumed once shown.
                    Assert.True(notice.ShowUpdateCompleted("4.9.0.0"));
                    Assert.Equal(UiText.Format("StoreUpdate_CompletedVersion", "4.9.0"), window.NoticeTextForTest);
                    Assert.Equal([MyCapture.App.Store.EngagementNoticeKind.UpdateCompleted], consumed);
                    Assert.True(notice.IsShown);
                    Assert.Equal(string.Empty, status.Text);
                    Assert.Same(window.FindResource("Text.Secondary"), noticeText.Foreground);
                    Assert.Equal(TextWrapping.NoWrap, noticeText.TextWrapping);
                    AssertNoCardCoversTheLibrary();
                    window.AdvanceNoticeForTest(GalleryWindow.UpdateDoneLifetime);
                    Assert.False(window.IsNoticeVisible);
                    Assert.False(notice.IsShown);

                    // The rating invitation: its own slot, with its action beside it.
                    notice.ShowInvitation();
                    Assert.Equal(UiText.Get("StoreRating_InviteMessage"), window.NoticeTextForTest);
                    Assert.Equal(UiText.Get("LibraryUx_RateAction"), window.NoticeActionForTest);
                    AssertNoCardCoversTheLibrary();
                    window.InvokeNoticeActionForTest();
                    Assert.Equal(1, reviews);
                    Assert.False(window.IsNoticeVisible);

                    // A result with its follow-up wins the line: the notice waits and Undo stays intact.
                    notice.ShowInvitation();
                    Assert.True(window.IsNoticeVisible);
                    int undone = 0;
                    window.ShowEngagementStatusForTest("3개를 휴지통으로 옮김", "실행 취소", () => { undone++; return Task.CompletedTask; });
                    Assert.False(window.IsNoticeVisible);
                    Assert.True(notice.IsShown, "the invitation is still pending");
                    Assert.Equal("실행 취소", window.StatusActionLabelForTest);
                    AssertNoCardCoversTheLibrary(sameHeight: false);
                    // A notice that arrives while the line is busy waits too, and does not erase the follow-up.
                    Assert.False(notice.ShowUpdateCompleted("4.9.2.0"));
                    Assert.Equal([MyCapture.App.Store.EngagementNoticeKind.UpdateCompleted], consumed);
                    Assert.Equal("실행 취소", window.StatusActionLabelForTest);
                    window.InvokeStatusActionForTest().GetAwaiter().GetResult();
                    Assert.Equal(1, undone);
                    // When the line empties, the waiting notice appears and is consumed only then.
                    window.ExpireActionStatus();
                    Assert.True(window.IsNoticeVisible);
                    Assert.Equal(UiText.Format("StoreUpdate_CompletedVersion", "4.9.2"), window.NoticeTextForTest);
                    Assert.Equal([MyCapture.App.Store.EngagementNoticeKind.UpdateCompleted, MyCapture.App.Store.EngagementNoticeKind.UpdateCompleted], consumed);
                    notice.Hide();

                    // Long text is trimmed to one line inside the bar, never growing it.
                    window.ShowNotice(new string('W', 400), "action", () => { }, GalleryWindow.InvitationLifetime);
                    AssertNoCardCoversTheLibrary();
                    Assert.True(slot.ActualWidth <= bar.ActualWidth * 0.8);
                    window.ClearNotice();

                    // An available update: a persistent, bounded button in place of the version text.
                    Assert.True(version.IsVisible);
                    notice.ShowUpdateAvailable("4.9.1.0");
                    Assert.True(notice.IsShown && update.IsVisible && !version.IsVisible);
                    Assert.Equal(UiText.Format("LibraryUx_UpdateAvailable", "4.9.1"), UpdateLabel());
                    Assert.Equal(UiText.Format("Update_AvailableVersion", "4.9.1"), update.ToolTip);
                    Assert.Equal(UpdateLabel(), System.Windows.Automation.AutomationProperties.GetName(update));
                    AssertNoCardCoversTheLibrary();
                    update.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(1, installs);
                    // Installing: the status replaces the label and the button waits; a long one stays bounded.
                    notice.ShowUpdateAvailable("4.9.1.0", busy: true, status: new string('X', 300));
                    Assert.False(update.IsEnabled);
                    AssertNoCardCoversTheLibrary();
                    Assert.True(update.ActualWidth <= 200.5);
                    notice.Hide();
                    SettleGalleryLayout(window);
                    Assert.True(version.IsVisible && !update.IsVisible);
                    Assert.Equal(MyCapture.App.ApplicationVersion.DisplayText, version.Text);
                }
                finally { window.CloseForExit(); }
            }
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            application.Shutdown();
        });

    [Fact]
    public Task LibraryOverhaul_OneSharedMenuAndNoPerCardWork() =>
        RunSharedLibraryMenuTest(nameof(LibraryOverhaul_OneSharedMenuAndNoPerCardWork), constrainWindow: false);

    [Fact]
    public Task LibraryOverhaul_SharedMenuAndMetadataChecksWhenTheDesktopCapsTheWindow() =>
        RunSharedLibraryMenuTest(nameof(LibraryOverhaul_SharedMenuAndMetadataChecksWhenTheDesktopCapsTheWindow), constrainWindow: true);

    private Task RunSharedLibraryMenuTest(string testName, bool constrainWindow) =>
        RunIsolated(testName, () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-shared-menu-");
            GalleryWindow window = f.Window;
            if (constrainWindow) window.MaxWidth = 1044;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                CaptureRecord a = f.Records[0], b = f.Records[2];
                a.SourcePageUrl = "https://github.com/example/my-capture";
                f.ViewModel.FindTile(a.Id)!.RaiseMetaChanged();
                SettleGalleryLayout(window);
                ListBoxItem first = RealizedCard(rows, a.Id), second = RealizedCard(rows, b.Id);

                // No card, and no card's More button, carries a menu of its own.
                Assert.All(Descendants(rows).OfType<ListBoxItem>().Where(item => item.DataContext is GalleryItemViewModel),
                    item => Assert.Null(item.ContextMenu));
                Assert.All(Descendants(rows).OfType<Button>().Where(button => button.DataContext is GalleryItemViewModel
                        && System.Windows.Automation.AutomationProperties.GetName(button) == UiText.Get("Text_5E8959D18390")),
                    button => Assert.Null(button.ContextMenu));
                // A source badge builds its menu when it is first used.
                Button badge = Descendants(first).OfType<Button>().Single(button => button.Name == "SourceBadge");
                Assert.Null(badge.ContextMenu);

                // A right click, the Menu key and the More button all open the same instance, aimed at their card.
                ContextMenu shared = window.TileMenu;
                Assert.Same(shared, window.FindResource("Gallery.TileContextMenu"));
                foreach ((ListBoxItem card, GalleryItemViewModel tile) in new[]
                         { (first, f.ViewModel.FindTile(a.Id)!), (second, f.ViewModel.FindTile(b.Id)!) })
                {
                    ContextMenuEventArgs opening = OpeningArgs(card, keyboard: false);
                    card.RaiseEvent(opening);
                    Assert.True(opening.Handled);
                    Assert.True(shared.IsOpen);
                    Assert.Same(card, shared.PlacementTarget);
                    Assert.Same(tile, shared.DataContext);
                    shared.IsOpen = false;
                    Button more = Descendants(card).OfType<Button>().Single(button =>
                        System.Windows.Automation.AutomationProperties.GetName(button) == UiText.Get("Text_5E8959D18390"));
                    more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(shared.IsOpen);
                    Assert.Same(more, shared.PlacementTarget);
                    Assert.Same(tile, shared.DataContext);
                    shared.IsOpen = false;
                }

                // The badge menu appears on demand.
                Assert.NotNull(GallerySourceBadge.EnsureMenu(badge));

                // The card text widths are measured when the window is shown, resized or rescaled, not on
                // every layout pass: scrolling and relayout never add a check.
                SettleGalleryLayout(window);
                int checks = window.MetadataWidthChecks;
                Assert.True(checks > 0);
                for (int pass = 0; pass < 12; pass++)
                {
                    rows.InvalidateMeasure();
                    rows.InvalidateArrange();
                    SettleGalleryLayout(window);
                    Descendants(rows).OfType<ScrollViewer>().First().ScrollToVerticalOffset(pass * 7);
                }
                Assert.Equal(checks, window.MetadataWidthChecks);
                double grantedWidth = window.ActualWidth;
                window.Width += 120;
                SettleGalleryLayout(window);
                if (Math.Abs(window.ActualWidth - grantedWidth) < 0.5)
                {
                    // A request beyond the desktop limit is not a resize and must not do work.
                    Assert.Equal(checks, window.MetadataWidthChecks);
                    window.Width = Math.Max(window.MinWidth, grantedWidth - 120);
                    SettleGalleryLayout(window);
                }
                Assert.True(Math.Abs(window.ActualWidth - grantedWidth) > 0.5,
                    $"The resize probe did not change the granted width {grantedWidth}.");
                Assert.True(window.MetadataWidthChecks > checks, "A real window resize must recheck metadata widths.");
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    // The framework builds these itself when the mouse or the Menu key asks for a menu.
    private static ContextMenuEventArgs OpeningArgs(UIElement source, bool keyboard)
    {
        ConstructorInfo constructor = typeof(ContextMenuEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(object), typeof(bool), typeof(double), typeof(double)])!;
        var args = (ContextMenuEventArgs)constructor.Invoke([source, true, keyboard ? -1d : 10d, keyboard ? -1d : 10d]);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        return args;
    }

    private static ListBoxItem RealizedCard(ListBox rows, Guid id) => PumpLibraryUntil(
        (Window)Window.GetWindow(rows)!,
        () => Descendants(rows).OfType<ListBoxItem>().FirstOrDefault(item => item.DataContext is GalleryItemViewModel tile && tile.Id == id),
        item => item is not null, "realized card");

    private static KeyEventArgs PressLibraryKey(UIElement target, Key key, bool ime = false)
    {
        KeyEventArgs args = EditorKeysTests.KeyArgs(target, key, ime, Keyboard.PreviewKeyDownEvent);
        target.RaiseEvent(args);
        return args;
    }

    private static void RightClick(UIElement target)
    {
        // PreviewMouseRightButtonDown is promoted from the preview mouse-down that tunnels through the tree.
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = target };
        target.RaiseEvent(args);
    }

    /// <summary>The shared card menu's item by its localized name key.</summary>
    private static MenuItem TileMenuItem(GalleryWindow window, string nameKey)
    {
        ContextMenu menu = (ContextMenu)window.FindResource("Gallery.TileContextMenu");
        return Assert.Single(menu.Items.OfType<MenuItem>(),
            item => System.Windows.Automation.AutomationProperties.GetName(item) == UiText.Get(nameKey));
    }

    private static void ClickTileMenuItem(MenuItem item, GalleryItemViewModel tile)
    {
        item.CommandParameter = tile;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
}
