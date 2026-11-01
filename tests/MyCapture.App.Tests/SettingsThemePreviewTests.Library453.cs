using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Theory]
    [InlineData("ko-KR", "오후 11:46")]
    [InlineData("en-US", "11:46 PM")]
    public void Library453_CaptureTimeUsesUiCultureAndMetadataPriority(string language, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        var record = new CaptureRecord { CreatedAt = new DateTimeOffset(2026, 10, 13, 23, 46, 0, TimeSpan.FromHours(9)), Title = "Sample" };
        var tile = new GalleryItemViewModel(record, _ => string.Empty, 96)
        { Clock = () => record.CreatedAt, RetentionHours = () => 48 };
        Assert.Equal(expected, tile.TimeCaption);
        // The time is always shown at the title's right, so the second line's caption never includes it.
        Assert.Equal(string.Empty, tile.MetadataCaption);
        Assert.Empty(tile.MetadataCaption);
        Assert.Contains(UiText.Format("Library453.CapturedAt", expected), tile.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(record.CreatedAt.DateTime.ToString("d", UiText.Culture), tile.AccessibleName, StringComparison.Ordinal);
        tile.Clock = () => record.CreatedAt.AddHours(30);
        Assert.True(tile.IsExpiringSoon);
        Assert.Equal(tile.RetentionCaption, tile.MetadataCaption);
        tile.SetSearchMatch(true, CaptureMatchField.Title);
        Assert.Equal(tile.SearchMatchText, tile.MetadataCaption);
        Assert.Equal(expected, tile.TimeCaption);
        tile.SetSearchMatch(false, CaptureMatchField.None);
        Assert.Equal(tile.RetentionCaption, tile.MetadataCaption);
        Assert.DoesNotContain(expected, tile.MetadataCaption, StringComparison.Ordinal);
    }

    [Fact]
    public Task Library453_TwoLineCardsFitKoreanEnglishAndLargerFonts() =>
        RunIsolated(nameof(Library453_TwoLineCardsFitKoreanEnglishAndLargerFonts), () =>
        {
            Application application = CreateGalleryTestApplication();
            try
            {
                var layoutFailures = new List<string>();
                foreach (string language in new[] { "ko-KR", "en-US" })
                foreach (double scale in new[] { 1d, 1.5d, 2d })
                {
                    using var scope = UiText.UseLanguage(language);
                    ThemeService.Apply(AppTheme.Midnight);
                    LibraryUxFixture f = CreateLibraryUxFixture("mc-library453-layout-");
                    f.Window.Resources["FontSize.Body"] = 14d * scale;
                    f.Window.Resources["FontSize.Caption"] = 12d * scale;
                    f.Queue.CreateFolderAsync("Empty child", f.Design.Id).GetAwaiter().GetResult();
                    f.Queue.RenameFolderAsync(f.Design.Id, "Design reference documents and review captures").GetAwaiter().GetResult();
                    f.ViewModel.Refresh();
                    try
                    {
                        f.Window.Width = f.Window.MinWidth;
                        f.Window.Show();
                        SettleGalleryLayout(f.Window);
                        foreach (bool compact in new[] { false, true })
                        {
                            if (compact) f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
                            SettleGalleryLayout(f.Window);
                            var rows = (ListBox)f.Window.FindName("RowsList");
                            ListBoxItem item = Descendants(rows).OfType<ListBoxItem>().First(candidate => candidate.DataContext is GalleryItemViewModel);
                            var tile = (GalleryItemViewModel)item.DataContext;
                            tile.Record.IsPinned = true;
                            if (compact) tile.Record.FolderPinBeforeAssignment = true;
                            tile.Record.HasAnnotations = true;
                            tile.RefreshThumbnail();
                            SettleGalleryLayout(f.Window);
                            Border card = Descendants(item).OfType<Border>().Single(border => border.Name == "TileCard");
                            Assert.Equal(compact, GalleryCardStyle.GetIsCompact(card));
                            FrameworkElement preview = Descendants(item).OfType<FrameworkElement>().Single(element => element.Name == "TilePreview");
                            Assert.DoesNotContain(Descendants(preview).OfType<TextBlock>(), text => text.Text == tile.TimeCaption);
                            TextBlock meta = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "TileTime");
                            TextBlock caption = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "TileMetadataText");
                            Assert.False(caption.IsVisible, "no search or retention message: the second line has no caption");
                            TextBlock title = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "TileTitle");
                            Assert.Equal(tile.TimeCaption, meta.Text);
                            // The time ends where the title row ends, at the card's right inner edge.
                            Assert.True(meta.TranslatePoint(new Point(meta.ActualWidth, 0), card).X >= card.ActualWidth - 9,
                                $"{language}/{compact}/{scale}: the time sits at the card's right edge");
                            Assert.True(meta.TranslatePoint(new Point(), card).X > title.TranslatePoint(new Point(), card).X);
                            Assert.Same(f.Window.FindResource("Text.Secondary"), meta.Foreground);
                            Assert.Equal(12 * scale, meta.FontSize);
                            Assert.Equal((compact ? 12 : 14) * scale, title.FontSize);
                            foreach (TextBlock text in new[] { title, meta })
                            {
                                Point position = text.TranslatePoint(new Point(), card);
                                var probe = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
                                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                Assert.True(text.ActualHeight + 1 >= probe.DesiredSize.Height, $"{language}/{compact}/{scale}: vertical text clipping");
                                Assert.True(position.Y >= 0 && position.Y + text.ActualHeight <= card.ActualHeight + 1);
                            }
                            Border keptBadge = Descendants(item).OfType<Border>().Single(border => border.Name == "KeepBadge");
                            Assert.True(keptBadge.IsVisible, "The kept badge participates in the measured layout");
                            Assert.DoesNotContain(Descendants(item).OfType<Border>(), border => border.Name == "EditedBadge");
                            Assert.Equal(UiText.Get("LibraryUx_KeptBadge"), AutomationProperties.GetName(keptBadge));
                            Assert.DoesNotContain(UiText.Get("Text_2C64DDA4D878"), tile.AccessibleName, StringComparison.Ordinal);
                            foreach (Border status in new[] { keptBadge })
                            {
                                Point left = status.TranslatePoint(new Point(), card);
                                Assert.True(left.X >= -0.5 && left.X + status.ActualWidth <= card.ActualWidth + 0.5,
                                    $"{language}/{compact}/{scale}: status badge must remain inside its card");
                            }
                            AssertNotTrimmed(meta, language + "/compact" + compact + "/font" + scale);
                            GalleryCardRowPanel panel = Descendants(rows).OfType<GalleryCardRowPanel>().First(candidate => Descendants(candidate).Contains(item));
                            Assert.Equal(compact ? 160 : 220, panel.BaseCardHeight);
                            Assert.True(panel.BaseCardHeight < (compact ? 174 : 234));
                            if (scale > 1) Assert.True(card.ActualHeight > GalleryCardRowPanel.CardHeightFor(card.ActualWidth, panel.BaseCardWidth, panel.BaseCardHeight, panel.HeightPerWidth));
                            Assert.NotEmpty(FolderCardButtons(rows));
                            foreach (Button folderCard in FolderCardButtons(rows))
                            {
                                Assert.True(folderCard.ActualHeight < card.ActualHeight);
                                Assert.Empty(Descendants(folderCard).OfType<Image>());
                                foreach (TextBlock label in Descendants(folderCard).OfType<TextBlock>().Where(text => text.IsVisible))
                                {
                                    Point edge = label.TranslatePoint(new Point(0, label.ActualHeight), folderCard);
                                    Assert.True(edge.Y <= folderCard.ActualHeight + 1, "Folder text stays inside its compacted card");
                                }
                                foreach (FrameworkElement part in Descendants(folderCard).OfType<FrameworkElement>()
                                    .Where(element => element is TextBlock or SymbolIcon && element.IsVisible))
                                {
                                    Point top = part.TranslatePoint(new Point(), folderCard);
                                    Assert.True(top.Y >= -1 && top.Y + part.ActualHeight <= folderCard.ActualHeight + 1,
                                        $"{language}/{compact}/{scale}: folder content must fit its strip");
                                }
                            }
                            tile.RenameDraft = UiText.Get("Library453.Rename") + " sample";
                            tile.IsRenaming = true;
                            SettleGalleryLayout(f.Window);
                            TextBox editor = Descendants(item).OfType<TextBox>().Single(input => input.Name == "TileRenameInput");
                            Assert.True(editor.IsVisible);
                            Rect character = editor.GetRectFromCharacterIndex(0);
                            Assert.False(character.IsEmpty);
                            Assert.True(character.Top >= 0 && character.Bottom <= editor.ActualHeight + 1,
                                "The enlarged inline editor keeps the complete text line");
                            Point editorTop = editor.TranslatePoint(new Point(), card);
                            Assert.True(editorTop.Y >= 0 && editorTop.Y + editor.ActualHeight <= card.ActualHeight + 1);
                            tile.IsRenaming = false;
                            SettleGalleryLayout(f.Window);
                            SaveLibraryUxEvidence(f.Window, AppTheme.Midnight, $"library453-{language}-font{scale:0.0}-{(compact ? "compact" : "full")}.png");
                        }
                        // Images/Kept views show filed captures with a folder badge without a
                        // search replacement. Time and both remaining statuses must share the row.
                        f.ViewModel.ShowView(GalleryFilter.Images);
                        var imagesRows = (ListBox)f.Window.FindName("RowsList");
                        Guid filedId = f.Records[4].Id;
                        GalleryTileRow filedRow = f.ViewModel.Rows.OfType<GalleryTileRow>().Single(row => row.Tiles.Any(tile => tile.Id == filedId));
                        imagesRows.ScrollIntoView(filedRow);
                        SettleGalleryLayout(f.Window);
                        ListBoxItem filedItem = Descendants(imagesRows).OfType<ListBoxItem>().Single(item => item.DataContext is GalleryItemViewModel tile && tile.Id == filedId);
                        var filedTile = (GalleryItemViewModel)filedItem.DataContext;
                        filedTile.Record.FolderPinBeforeAssignment = true;
                        filedTile.Record.HasAnnotations = true;
                        filedTile.RefreshThumbnail();
                        SettleGalleryLayout(f.Window);
                        Assert.False(f.ViewModel.IsCompactLayout);
                        Assert.False(filedTile.HasSearchMatch);
                        Assert.True(filedTile.HasFolderBadge);
                        Border filedFolder = Descendants(filedItem).OfType<Border>().Single(border => border.Name == "FolderBadge");
                        Border filedKept = Descendants(filedItem).OfType<Border>().Single(border => border.Name == "KeepBadge");
                        Assert.DoesNotContain(Descendants(filedItem).OfType<Border>(), border => border.Name == "EditedBadge");
                        Assert.True(filedFolder.IsVisible && filedFolder.ActualWidth > 12);
                        Assert.True(filedKept.IsVisible);
                        Border filedCard = Descendants(filedItem).OfType<Border>().Single(border => border.Name == "TileCard");
                        foreach (Border status in new[] { filedFolder, filedKept })
                        {
                            Point left = status.TranslatePoint(new Point(), filedCard);
                            Assert.True(left.X >= -0.5 && left.X + status.ActualWidth <= filedCard.ActualWidth + 0.5,
                                $"{language}/images/{scale}: status badge must remain inside its card");
                        }
                        Assert.Equal(f.Design.Name, filedFolder.ToolTip);
                        TextBlock filedTime = Descendants(filedItem).OfType<TextBlock>().Single(text => text.Name == "TileTime");
                        Assert.Equal(filedTile.TimeCaption, filedTime.Text);
                        AssertNotTrimmed(filedTime, language + "/images/font" + scale);
                        SaveLibraryUxEvidence(f.Window, AppTheme.Midnight, $"library453-{language}-font{scale:0.0}-images.png");
                    }
                    catch (Exception failure) { layoutFailures.Add($"{language}/font{scale}: {failure}"); }
                    finally { f.Window.CloseForExit(); }
                }
                Assert.True(layoutFailures.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, layoutFailures));
            }
            finally { application.Shutdown(); }
        });

    [Fact]
    public Task Library453_RenameInPlaceAndCanvasMenusKeepTheirTargets() =>
        RunIsolated(nameof(Library453_RenameInPlaceAndCanvasMenusKeepTheirTargets), () =>
        {
            Application application = CreateGalleryTestApplication();
            SynchronizationContext? previous = SynchronizationContext.Current;
            using var scope = UiText.UseLanguage("en-US");
            ThemeService.Apply(AppTheme.Midnight);
            LibraryUxFixture f = CreateLibraryUxFixture("mc-library453-actions-");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            GalleryWindow window = f.Window;
            try
            {
                window.Show(); window.Activate(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                CaptureRecord record = f.Records[0];
                record.Tags = "keep, review";
                record.SourceWindowTitle = "Automatic window title";
                GalleryItemViewModel tile = f.ViewModel.FindTile(record.Id)!;
                ListBoxItem Item() => Descendants(rows).OfType<ListBoxItem>().Single(item => item.DataContext == tile);
                TextBox Input() => Descendants(Item()).OfType<TextBox>().Single(input => input.Name == "TileRenameInput");
                KeyBinding f2 = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.F2);
                void Begin()
                {
                    f.ViewModel.Select(tile.Id); SettleGalleryLayout(window); Item().Focus();
                    f2.Command.Execute(null);
                    PumpLibraryUntil(window, () => tile.IsRenaming && Input().IsKeyboardFocused, "inline title focused");
                }
                void Press(Key key)
                {
                    TextBox input = Input();
                    input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                Begin(); Input().Text = "Unique renamed capture"; Press(Key.Enter);
                PumpLibraryUntil(window, () => !tile.IsRenaming, "title saved");
                Assert.Equal("Unique renamed capture", record.Title);
                Assert.Equal("keep, review", record.Tags);
                TextBlock details = Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetAutomationId(text) == "LibraryDetailsTitle");
                Assert.Equal(record.Title, details.ToolTip);
                f.ViewModel.SearchQuery = "Unique renamed capture"; SettleGalleryLayout(window);
                Assert.Contains(f.ViewModel.Rows.OfType<GalleryTileRow>().SelectMany(row => row.Tiles), candidate => candidate.Id == tile.Id);
                f.ViewModel.SearchQuery = string.Empty; SettleGalleryLayout(window);
                Begin(); Input().Text = "Cancelled"; Press(Key.Escape);
                Assert.False(tile.IsRenaming); Assert.Equal("Unique renamed capture", record.Title);
                Begin(); Input().Text = "Saved on blur"; ((TextBox)window.FindName("SearchBox")).Focus();
                PumpLibraryUntil(window, () => !tile.IsRenaming, "blur saved");
                Assert.Equal("Saved on blur", record.Title);
                Begin(); Input().Text = "   "; Press(Key.Enter);
                PumpLibraryUntil(window, () => !tile.IsRenaming, "automatic title restored");
                Assert.Equal(string.Empty, record.Title);
                Assert.Equal("Automatic window title", tile.Caption);
                Assert.Equal("keep, review", record.Tags);
                // A real queue gate keeps persistence pending while two later rename requests
                // arrive. Only the last request may own an editor when the first save finishes.
                Begin(); Input().Text = "Queued first rename";
                var gate = (SemaphoreSlim)typeof(CaptureQueue).GetField("_batchTagMutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Queue)!;
                GalleryItemViewModel second = f.ViewModel.FindTile(f.Records[1].Id)!;
                GalleryItemViewModel third = f.ViewModel.FindTile(f.Records[2].Id)!;
                gate.Wait();
                Task beginSecond;
                Task beginThird;
                try
                {
                    beginSecond = window.BeginCaptureRenameAsync(second);
                    beginThird = window.BeginCaptureRenameAsync(third);
                    Assert.False(beginSecond.IsCompleted); Assert.False(beginThird.IsCompleted);
                }
                finally { gate.Release(); }
                PumpLibraryUntil(window, () => beginSecond.IsCompleted && beginThird.IsCompleted, "queued rename handoff");
                beginSecond.GetAwaiter().GetResult(); beginThird.GetAwaiter().GetResult();
                Assert.False(tile.IsRenaming); Assert.False(second.IsRenaming); Assert.True(third.IsRenaming);
                window.CancelCaptureRename();
                Begin(); Input().Text = "Focus stays in search";
                gate.Wait();
                try
                {
                    Press(Key.Enter);
                    ((TextBox)window.FindName("SearchBox")).Focus();
                    Assert.True(tile.IsRenameSaving);
                }
                finally { gate.Release(); }
                PumpLibraryUntil(window, () => !tile.IsRenameSaving, "pending rename completed");
                Assert.Same(window.FindName("SearchBox"), Keyboard.FocusedElement);
                Begin();
                tile.RenameDraft = "Invalid" + (char)1;
                Task<bool> invalid = window.CommitCaptureRenameAsync();
                PumpLibraryUntil(window, () => invalid.IsCompleted, "invalid rename rejected");
                Assert.False(invalid.GetAwaiter().GetResult());
                Assert.True(tile.IsRenaming); Assert.EndsWith(((char)1).ToString(), tile.RenameDraft, StringComparison.Ordinal);
                Assert.Equal("keep, review", record.Tags);
                window.CancelCaptureRename();
                ContextMenu tileMenu = window.TileMenu;
                tileMenu.PlacementTarget = Item(); tileMenu.IsOpen = true; SettleGalleryLayout(window);
                MenuItem rename = tileMenu.Items.OfType<MenuItem>().Single(menu => menu.InputGestureText == "F2");
                Assert.Equal(UiText.Get("Library453.Rename"), rename.Header);
                rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); tileMenu.IsOpen = false;
                PumpLibraryUntil(window, () => tile.IsRenaming, "menu rename");
                window.CancelCaptureRename();
                Assert.Contains(tileMenu.Items.OfType<MenuItem>(), menu => Equals(menu.Header, UiText.Get("Library.Tags.Edit")));

                // The same menu binding is attached to each child card. A header and the empty
                // canvas inherit only the surface menu; the focused child owns menu-key routing.
                var surface = (Grid)window.FindName("LibraryItemsSurface");
                ContextMenu empty = surface.ContextMenu;
                Assert.NotSame(tileMenu, empty);
                Button folder = FolderCardButtons(rows).First();
                Assert.NotNull(folder.ContextMenu);
                Assert.NotSame(empty, folder.ContextMenu);
                Assert.Same(folder, GalleryWindow.FindLibraryCard(folder));
                Assert.Same(Item(), GalleryWindow.FindLibraryCard(Descendants(Item()).OfType<TextBlock>().First()));
                Button header = Descendants(rows).OfType<Button>().First(button => button.DataContext is GalleryHeaderRow);
                Assert.Null(GalleryWindow.FindLibraryCard(header));
                rows.Focus();
                rows.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Apps)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.True(empty.IsOpen); Assert.Same(rows, empty.PlacementTarget);
                MenuItem[] choices = empty.Items.OfType<MenuItem>().ToArray();
                Assert.Equal([UiText.Get("Folder410.NewFolder"), UiText.Get("Folder410.Sort"), UiText.Get("Library453.SelectAll"), UiText.Get("Library.Import.Command")], choices.Select(choice => (string)choice.Header));
                MenuItem sort = choices[1];
                Assert.True(((MenuItem)sort.Items[f.ViewModel.SortIndex]).IsChecked);
                ((MenuItem)sort.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(1, f.ViewModel.SortIndex);
                choices[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(f.ViewModel.Rows.OfType<GalleryTileRow>().Sum(row => row.Tiles.Count), f.ViewModel.Selection.SelectedIds.Count);
                empty.IsOpen = false;
                Assert.Null(window.FindName("NewFolderButton"));
                Assert.NotNull(window.FindName("NewFolderPaneButton"));
                Assert.Equal("Ctrl+Shift+N", ((MenuItem)window.FindName("NewFolderOverflowMenu")).InputGestureText);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.N && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));

                // Folder F2 still opens the folder dialog and persists the folder's name.
                f.ViewModel.ClearSelection(); SettleGalleryLayout(window);
                folder = FolderCardButtons(rows).First();
                Guid folderId = ((GalleryFolderCardViewModel)folder.DataContext).FolderId;
                folder.Focus();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    var dialog = application.Windows.OfType<GalleryFolderEditDialog>().Single();
                    ((TextBox)dialog.Input).Text = "Renamed folder";
                    dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                f2.Command.Execute(null);
                Assert.Equal("Renamed folder", f.Queue.FindFolder(folderId)!.Name);
            }
            finally
            {
                window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });
    [Fact]
    public Task Library453_AllNewFolderEntriesUseTheCurrentLocation() =>
        RunIsolated(nameof(Library453_AllNewFolderEntriesUseTheCurrentLocation), () =>
        {
            Application application = CreateGalleryTestApplication();
            SynchronizationContext? previous = SynchronizationContext.Current;
            using var scope = UiText.UseLanguage("en-US");
            ThemeService.Apply(AppTheme.Midnight);
            LibraryUxFixture f = CreateLibraryUxFixture("mc-library453-create-");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                f.Window.Show(); f.Window.Activate();
                f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
                SettleGalleryLayout(f.Window);
                Button pane = (Button)f.Window.FindName("NewFolderPaneButton");
                ContextMenu blank = ((Grid)f.Window.FindName("LibraryItemsSurface")).ContextMenu;
                MenuItem canvas = blank.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+Shift+N");
                MenuItem overflow = (MenuItem)f.Window.FindName("NewFolderOverflowMenu");
                KeyBinding shortcut = f.Window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.N
                    && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
                Action[] actions = [
                    () => pane.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                    () => canvas.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)),
                    () => overflow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)),
                    () => shortcut.Command.Execute(null),
                ];
                for (int index = 0; index < actions.Length; index++)
                {
                    string name = "Created through entry " + index;
                    ((ListBox)f.Window.FindName("RowsList")).Focus();
                    f.Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        var dialog = application.Windows.OfType<GalleryFolderEditDialog>().Single();
                        ((TextBox)dialog.Input).Text = name;
                        dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }));
                    actions[index]();
                    CaptureFolder created = Assert.Single(f.Queue.Folders, folder => folder.Name == name);
                    Assert.Equal(f.Design.Id, created.ParentId);
                    SettleGalleryLayout(f.Window);
                }
            }
            finally
            {
                f.Window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });

    [Fact]
    public Task Library453_SaveSearchCommandAppearsOnlyWithAQuery() =>
        RunIsolated(nameof(Library453_SaveSearchCommandAppearsOnlyWithAQuery), () =>
        {
            Application application = CreateGalleryTestApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            ThemeService.Apply(AppTheme.Midnight);
            LibraryUxFixture f = CreateLibraryUxFixture("mc-library453-saved-search-");
            try
            {
                f.Window.Show();
                SettleGalleryLayout(f.Window);
                var save = (Button)f.Window.FindName("SaveSearchButton");
                var hint = (TextBlock)f.Window.FindName("SavedSearchEmptyHint");
                var search = (TextBox)f.Window.FindName("SearchBox");

                // No query: no dead command, a hint instead (the fixture has no saved searches).
                Assert.Equal(Visibility.Collapsed, save.Visibility);
                Assert.False(save.IsEnabled);
                Assert.Equal(Visibility.Visible, hint.Visibility);
                // Korean display text carries invisible line-break joiners; compare the visible letters.
                Assert.Equal(UiText.Get("Library453.SavedSearchHint"),
                    string.Concat(hint.Text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)));

                search.Text = "앱:Edge";
                SettleGalleryLayout(f.Window);
                Assert.Equal(Visibility.Visible, save.Visibility);
                Assert.True(save.IsEnabled);
                Assert.Equal(UiText.Get("Library.SavedSearch.Save"), AutomationProperties.GetName(save));
                Assert.Equal(Visibility.Collapsed, hint.Visibility);

                search.Text = "   ";
                SettleGalleryLayout(f.Window);
                Assert.Equal(Visibility.Collapsed, save.Visibility);
            }
            finally
            {
                f.Window.CloseForExit(); application.Shutdown();
            }
        });
}
