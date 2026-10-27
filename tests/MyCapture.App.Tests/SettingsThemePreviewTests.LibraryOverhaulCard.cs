using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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

/// <summary>The library overhaul's capture card: its actions, selection box, Tab stop and footer layout.</summary>
public sealed partial class SettingsThemePreviewTests
{
    private static T CardPart<T>(DependencyObject owner, string name) where T : FrameworkElement =>
        Descendants(owner).OfType<T>().Single(element => element.Name == name);

    [Fact]
    public Task LibraryOverhaul_CardHasItsActionsAndTheSelectionBoxFollowsItsRules() =>
        RunIsolated(nameof(LibraryOverhaul_CardHasItsActionsAndTheSelectionBoxFollowsItsRules), () =>
        {
            Application application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-card-");
            f.Records[2].MediaKind = CaptureMediaKind.Video;
            f.Records[2].DurationMs = 12_000;
            f.ViewModel.Refresh();
            GalleryWindow window = f.Window;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                GalleryItemViewModel image = f.ViewModel.FindTile(f.Records[0].Id)!, other = f.ViewModel.FindTile(f.Records[3].Id)!,
                    video = f.ViewModel.FindTile(f.Records[2].Id)!;
                ListBoxItem card = RealizedCard(rows, image.Id), otherCard = RealizedCard(rows, other.Id), videoCard = RealizedCard(rows, video.Id);
                CheckBox box = CardPart<CheckBox>(card, "SelectBox");

                // The drag handle is gone: no card part carries the move cursor.
                Assert.DoesNotContain(Descendants(card).OfType<FrameworkElement>(), element => Equals(element.Cursor, Cursors.SizeAll));
                string[] actions = ["PinButton", "CopyButton", "MoreButton"];
                // Pin, copy and more are always on a capture card; a video has only More (play and edit stay on Enter and the menu).
                Assert.All(actions, name => Assert.True(CardPart<Button>(card, name).IsVisible, name));
                Assert.False(CardPart<Button>(videoCard, "PinButton").IsVisible);
                Assert.False(CardPart<Button>(videoCard, "CopyButton").IsVisible);
                Assert.True(CardPart<Button>(videoCard, "MoreButton").IsVisible);
                Assert.True(GalleryCopyFeedback.CanConfirm(CardPart<Button>(card, "CopyButton")));
                Assert.Equal(UiText.Get("LibraryUx_PinHint"), CardPart<Button>(card, "PinButton").ToolTip);
                Assert.Equal(UiText.Get("Gallery.Float"), AutomationProperties.GetName(CardPart<Button>(card, "PinButton")));
                Assert.All(actions, name =>
                {
                    Button button = CardPart<Button>(card, name);
                    Assert.Equal(28, button.ActualWidth); Assert.Equal(28, button.ActualHeight);
                    Assert.False(button.IsTabStop); Assert.True(button.Focusable);
                    Assert.Same(window.FindResource("Text.Secondary"), button.Foreground);
                });
                Assert.False(CardPart<Button>(card, "SourceBadge").IsTabStop);
                Assert.False(card.IsTabStop);
                Assert.True(rows.IsTabStop);

                // The selection box: hidden at rest but still exposed, never a focus target, named after its card.
                Assert.Equal(0, box.Opacity);
                Assert.False(box.IsHitTestVisible);
                Assert.False(box.Focusable); Assert.False(box.IsTabStop);
                Assert.Equal(UiText.Format("LibraryUx_SelectCard", image.ContextLabel), AutomationProperties.GetName(box));
                Assert.Equal(Visibility.Visible, box.Visibility);
                Border preview = CardPart<Border>(card, "TilePreview");
                Assert.Equal(new Size(32, 32), box.RenderSize);
                var inner = (Border)box.Template.FindName("SelectionBox", box);
                Point innerOrigin = inner.TranslatePoint(new Point(), preview);
                Assert.Equal(6, innerOrigin.X, 0.6); Assert.Equal(6, innerOrigin.Y, 0.6);
                Assert.Equal(20, inner.ActualWidth); Assert.Equal(20, inner.ActualHeight);
                Assert.Null(inner.Effect);
                Assert.Same(window.FindResource("Surface.Raised"), inner.Background);

                // Hover fades it in (and makes it clickable); leaving hides it again.
                SetLibraryTileState440(card, "IsMouseOver", true);
                SettleGalleryLayout(window);
                Assert.Equal(1, box.Opacity); Assert.True(box.IsHitTestVisible);
                SetLibraryTileState440(card, "IsMouseOver", false);
                SettleGalleryLayout(window);
                Assert.Equal(0, box.Opacity); Assert.False(box.IsHitTestVisible);

                // Selected: checked and visible; while anything is selected every card shows its empty box.
                f.ViewModel.Select(image.Id);
                SettleGalleryLayout(window);
                Assert.True(box.IsChecked); Assert.Equal(1, box.Opacity);
                CheckBox otherBox = CardPart<CheckBox>(otherCard, "SelectBox");
                Assert.False(otherBox.IsChecked); Assert.Equal(1, otherBox.Opacity); Assert.True(otherBox.IsHitTestVisible);
                Assert.Equal(1, CardPart<CheckBox>(videoCard, "SelectBox").Opacity);
                // UI Automation toggles it like a click: the box on another card adds that card to the selection.
                var toggle = (IToggleProvider)new CheckBoxAutomationPeer(otherBox).GetPattern(PatternInterface.Toggle)!;
                toggle.Toggle();
                Assert.Equal(new[] { image.Id, other.Id }.Order(), f.ViewModel.Selection.SelectedIds.Order());
                f.ViewModel.ClearSelection();
                SettleGalleryLayout(window);
                Assert.Equal(0, box.Opacity);

                // Keyboard focus: the ring and the box appear for the keyboard only.
                var focusRing = (Border)card.Template.FindName("FocusRing", card);
                Assert.Equal(0, focusRing.Opacity);
                GalleryCardInput.SetKeyboardFocusVisibleForTest(card, true);
                SettleGalleryLayout(window);
                Assert.Equal(1, focusRing.Opacity); Assert.Equal(1, box.Opacity);
                Assert.True(GalleryCardInput.GetTrack(card));
                // A mouse press inside the card ends it.
                card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = card });
                SettleGalleryLayout(window);
                Assert.False(GalleryCardInput.GetShowsKeyboardFocus(card));
                Assert.Equal(0, focusRing.Opacity);
                // The press also selected the card, as a click does, so its box shows checked.
                Assert.True(box.IsChecked); Assert.Equal(1, box.Opacity);
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                window.CloseForExit(); application.Shutdown();
            }
        });

    [Fact]
    public Task LibraryOverhaul_CardMenuHasTheDecidedGroupsAndIconsAndRefreshesOnEveryOpen() =>
        RunIsolated(nameof(LibraryOverhaul_CardMenuHasTheDecidedGroupsAndIconsAndRefreshesOnEveryOpen), () =>
        {
            Application application = CreateLibraryMotionApplication();
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var scope = UiText.UseLanguage(language);
                LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-menu-");
                f.Records[2].MediaKind = CaptureMediaKind.Video;
                f.Records[0].SourcePageUrl = "https://example.com/source";
                f.ViewModel.Refresh();
                GalleryWindow window = f.Window;
                try
                {
                    window.Show(); SettleGalleryLayout(window);
                    var rows = (ListBox)window.FindName("RowsList");
                    GalleryItemViewModel image = f.ViewModel.FindTile(f.Records[0].Id)!, kept = f.ViewModel.FindTile(f.Records[1].Id)!,
                        video = f.ViewModel.FindTile(f.Records[2].Id)!, plain = f.ViewModel.FindTile(f.Records[3].Id)!;
                    ContextMenu menu = window.TileMenu;
                    string[] Visible() => menu.Items.Cast<Control>().Where(item => item.Visibility == Visibility.Visible)
                        .Select(item => item is Separator ? "-" : (string)((MenuItem)item).Header).ToArray();
                    void Open(GalleryItemViewModel tile)
                    {
                        window.OpenTileMenu(RealizedCard(rows, tile.Id), System.Windows.Controls.Primitives.PlacementMode.Bottom);
                        menu.IsOpen = false;
                    }
                    string T(string key) => UiText.Get(key);

                    // An image without a source: the five groups in the decided order.
                    f.ViewModel.ClearSelection();
                    Open(plain);
                    Assert.Equal([T("Gallery.Float"), T("Text_42A667BC71B0"), T("Text_854582314EBD"), "-",
                        T("Text_1003CFC78E66"), T("LibraryUx_Preview"), "-",
                        T("LibraryUx_Keep"), T("Library453.Rename"), T("Folder410.MoveCaptures"), T("Library.Tags.Edit"), "-",
                        T("ExportReduction_Title"), "-", T("Text_6139B6C3ED73")], Visible());
                    string[] gestures = menu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible).Select(item => item.InputGestureText).ToArray();
                    Assert.Equal(["P", "Ctrl+C", "T", "Enter", "Space", "K", "F2", "M", "", "", "Del"], gestures);
                    // Icons only in the first group and on Delete; the drag guide is gone.
                    Assert.Equal([T("Gallery.Float"), T("Text_42A667BC71B0"), T("Text_854582314EBD"), T("Text_6139B6C3ED73")],
                        menu.Items.OfType<MenuItem>().Where(item => item.Icon is not null).Select(item => (string)item.Header));
                    Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, T("LibraryUx_DragHelp")));

                    // With a source, its details and "same source" are in the menu for keyboard users.
                    Open(image);
                    Assert.Contains(T("LibraryUx_SourceDetails"), Visible());
                    Assert.Contains(T("Source.ShowSame"), Visible());
                    Open(plain);
                    Assert.DoesNotContain(T("LibraryUx_SourceDetails"), Visible());

                    // A video: no media group, GIF export instead of image export, no Enter gesture on edit.
                    Open(video);
                    Assert.Equal([T("Text_1003CFC78E66"), T("LibraryUx_Preview"), "-",
                        T("LibraryUx_Keep"), T("Library453.Rename"), T("Folder410.MoveCaptures"), T("Library.Tags.Edit"), "-",
                        T("Text_D44617983641"), "-", T("Text_6139B6C3ED73")], Visible());
                    Assert.Equal(string.Empty, menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "Edit")).InputGestureText);

                    // The keep label follows what the command will do to the selection the card belongs to.
                    f.ViewModel.ClearSelection();
                    Assert.True(kept.IsPinned);
                    Open(kept); Assert.Contains(T("LibraryUx_Unkeep"), Visible());
                    Open(plain); Assert.Contains(T("LibraryUx_Keep"), Visible());
                    f.ViewModel.Select(kept.Id); f.ViewModel.Select(plain.Id, control: true);
                    Open(plain); Assert.Contains(T("LibraryUx_Keep"), Visible());        // mixed: keeps the rest
                    f.ViewModel.ClearSelection(); f.ViewModel.Select(kept.Id);
                    Open(kept); Assert.Contains(T("LibraryUx_Unkeep"), Visible());      // all kept: releases
                    Open(plain); Assert.Contains(T("LibraryUx_Keep"), Visible());       // outside the selection: its own target

                    // Compare only for exactly two selected images, on one of them.
                    f.ViewModel.ClearSelection();
                    Open(plain); Assert.DoesNotContain(T("Gallery.Compare"), Visible());
                    f.ViewModel.Select(plain.Id); f.ViewModel.Select(kept.Id, control: true);
                    Open(plain); Assert.Contains(T("Gallery.Compare"), Visible());
                    f.ViewModel.Select(image.Id, control: true);
                    Open(plain); Assert.DoesNotContain(T("Gallery.Compare"), Visible());
                    f.ViewModel.ClearSelection(); f.ViewModel.Select(plain.Id); f.ViewModel.Select(kept.Id, control: true);
                    Open(image); Assert.DoesNotContain(T("Gallery.Compare"), Visible()); // the card is outside the pair

                    // A closed menu holds no card.
                    window.OpenTileMenu(RealizedCard(rows, plain.Id), System.Windows.Controls.Primitives.PlacementMode.Bottom);
                    Assert.NotNull(menu.PlacementTarget);
                    menu.IsOpen = false;
                    PumpLibraryUntil(window, () => menu.PlacementTarget is null, "the closed menu lets go of its card");
                }
                finally { window.CloseForExit(); }
            }
            application.Shutdown();
        });

    [Fact]
    public Task LibraryOverhaul_DayHeadingsReadAsTextButOnlyStayButtons() =>
        RunIsolated(nameof(LibraryOverhaul_DayHeadingsReadAsTextButOnlyStayButtons), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-day-");
            GalleryWindow window = f.Window;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                Button heading = Descendants(rows).OfType<Button>().First(button => button.DataContext is GalleryHeaderRow);
                var row = (GalleryHeaderRow)heading.DataContext;
                // Text to look at: semibold, body size, no fill or edge at rest.
                Assert.Equal(FontWeights.SemiBold, heading.FontWeight);
                Assert.Equal((double)window.FindResource("FontSize.Body"), heading.FontSize);
                Assert.Same(window.FindResource("Text.Primary"), heading.Foreground);
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)heading.Background).Color);
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)heading.BorderBrush).Color);
                Assert.Same(window.FindResource("Gallery.DayHeading"), heading.Style);
                // A button to use: announced as one, named by its heading, activated by Enter/Space (the Invoke pattern
                // is what those keys do), with its tooltip, and it selects the day.
                Assert.Equal(AutomationControlType.Button, UIElementAutomationPeer.CreatePeerForElement(heading).GetAutomationControlType());
                Assert.Equal(row.Heading, AutomationProperties.GetName(heading));
                Assert.Equal(UiText.Get("LibrarySelection_SelectDay"), heading.ToolTip);
                Assert.True(heading.Focusable); Assert.False(heading.IsTabStop);
                f.ViewModel.ClearSelection();
                ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(heading).GetPattern(PatternInterface.Invoke)!).Invoke();
                PumpLibraryUntil(window, () => f.ViewModel.Selection.Count > 0, "the day is selected");
                Assert.All(f.ViewModel.SelectedTiles, tile => Assert.True(tile.IsSelected));
                // Down from the heading reaches that day's first row; Up from the first row reaches the heading.
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(heading), Environment.TickCount, Key.Down)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = heading };
                heading.RaiseEvent(down);
                Assert.True(down.Handled);
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryOverhaul_ToolbarMirrorsTheCardMenuAndFitsOneRowAtEveryWidth() =>
        RunIsolated(nameof(LibraryOverhaul_ToolbarMirrorsTheCardMenuAndFitsOneRowAtEveryWidth), () =>
        {
            Application application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            var failures = new List<string>();
            var densities = new HashSet<string>();
            try
            {
                foreach (string language in new[] { "ko-KR", "en-US" })
                foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.HighContrast })
                foreach (double textScale in new[] { 1d, 1.5d })
                {
                    using var scope = UiText.UseLanguage(language);
                    ThemeService.Apply(theme);
                    LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-toolbar-");
                    GalleryWindow window = f.Window;
                    window.Resources["FontSize.Body"] = 14d * textScale;
                    window.Resources["FontSize.Caption"] = 12d * textScale;
                    try
                    {
                        window.Show();
                        var bar = (FrameworkElement)window.FindName("LibraryContentCommandBar");
                        var rows = (ListBox)window.FindName("RowsList");
                        string[] names = ["FloatSelectedButton", "CopySelectedButton", "EditSelectedButton", "PreviewSelectedButton", "KeepSelectedButton"];
                        foreach (double width in new[] { 980d, 1100d, 1280d, 1500d })
                        foreach (bool details in new[] { true, false })
                        {
                            window.Width = width;
                            window.SetDetailsPaneOpen(details);
                            string label = $"{language}/{theme}/text{textScale}/{width}/details{details}";
                            double top = double.NaN;
                            foreach (int selected in new[] { 0, 1, 2 })
                            {
                                f.ViewModel.ClearSelection();
                                for (int index = 0; index < selected; index++) f.ViewModel.Select(f.Records[index].Id, control: index > 0);
                                SettleGalleryLayout(window);
                                // One row, whatever the selection: the list never moves.
                                double rowsTop = rows.TranslatePoint(new Point(), window).Y;
                                if (double.IsNaN(top)) top = rowsTop;
                                else if (Math.Abs(top - rowsTop) > 0.5) failures.Add($"{label}: the list moved from {top} to {rowsTop} with {selected} selected");
                                if (Math.Abs(bar.ActualHeight - 44) > 0.5) failures.Add($"{label}: the toolbar is {bar.ActualHeight} tall");
                                var buttons = names.Select(name => (Button)window.FindName(name)).ToArray();
                                // The decided order, left to right.
                                double[] lefts = buttons.Select(button => button.TranslatePoint(new Point(), bar).X).ToArray();
                                if (!lefts.SequenceEqual(lefts.Order())) failures.Add($"{label}: the commands are out of order {string.Join(",", lefts)}");
                                var sort = (FrameworkElement)window.FindName("SortBox");
                                var more = (FrameworkElement)window.FindName("MoreCommandsButton");
                                foreach (FrameworkElement part in buttons.Cast<FrameworkElement>().Append(sort).Append(more).Append((FrameworkElement)window.FindName("SelectionCountLabel")))
                                {
                                    Rect bounds = new(part.TranslatePoint(new Point(), bar), part.RenderSize);
                                    if (bounds.Right > bar.ActualWidth + 0.5 || bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Bottom > bar.ActualHeight + 0.5)
                                        failures.Add($"{label}/{selected}: {part.Name} {bounds} leaves the toolbar {bar.RenderSize} (density {window.ContentToolbarDensity})");
                                }
                                if (more.TranslatePoint(new Point(), bar).X < sort.TranslatePoint(new Point(), bar).X + sort.ActualWidth - 0.5)
                                    failures.Add($"{label}: the sort box overlaps More");
                                // Names and tooltips stay whatever is shown.
                                foreach (Button button in buttons)
                                    if (string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button)) || button.ToolTip is null)
                                        failures.Add($"{label}: {button.Name} lost its name or tooltip");
                                densities.Add($"{window.ContentToolbarDensity}");
                                bool labelled = window.ContentToolbarDensity == GalleryWindow.ToolbarDensity.Labelled;
                                if (((FrameworkElement)window.FindName("EditSelectedLabel")).IsVisible != labelled) failures.Add($"{label}: labels and density disagree");
                            }
                        }
                    }
                    finally { window.CloseForExit(); }
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct().Take(30)));
            // The matrix really exercised the collapse: labels at ample widths, icons (and the compact bar) where narrow.
            Assert.True(densities.Count >= 2 && densities.Contains("Labelled"), string.Join(",", densities));
        });

    [Fact]
    public Task LibraryOverhaul_CommandMessagesGoToTheStatusLineNotAModalBox() =>
        RunIsolated(nameof(LibraryOverhaul_CommandMessagesGoToTheStatusLineNotAModalBox), () =>
        {
            Application application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-status-");
            GalleryWindow window = f.Window;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var status = (TextBlock)window.FindName("LibraryActionStatus");
                var show = typeof(GalleryWindow).GetMethod("ShowStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                // A command that could not run: on the status line, kept until dismissed (and read out once).
                foreach (string key in new[] { "Gallery.FloatFailed", "Gallery.Compare.LoadFailed", "Text_75425A6D9BE8", "Text_BCA2A41F7456" })
                {
                    show.Invoke(window, [UiText.Get(key)]);
                    Assert.Equal(UiText.Get(key), status.Text);
                    Assert.Equal(FeedbackKind.ActionRequired, window.ActionStatusKindForTest);
                    Assert.True(window.IsActionStatusDismissibleForTest);
                    Assert.Empty(Application.Current.Windows.OfType<ThemedDialog>());
                    window.AdvanceActionStatusForTest(TimeSpan.FromMinutes(5));
                    Assert.Equal(UiText.Get(key), status.Text);
                }
                // "Already being saved": a hint that leaves with the result time.
                show.Invoke(window, [UiText.Get("Text_E20E28D53DCE")]);
                Assert.Equal(FeedbackKind.Warning, window.ActionStatusKindForTest);
                Assert.False(window.IsActionStatusDismissibleForTest);
                window.AdvanceActionStatusForTest(FeedbackTiming.Result);
                Assert.Equal(string.Empty, status.Text);
                Assert.Empty(Application.Current.Windows.OfType<ThemedDialog>());
                // It is a polite live region.
                Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(status));
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                window.CloseForExit(); application.Shutdown();
            }
        });

    [Fact]
    public Task LibraryOverhaul_GridIsOneTabStopAndEntersWhereTheUserLeft() =>
        RunIsolated(nameof(LibraryOverhaul_GridIsOneTabStopAndEntersWhereTheUserLeft), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-tab-");
            GalleryWindow window = f.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                window.Show(); window.Activate(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                // Every card part skips Tab: the list is the only stop in the grid.
                Assert.All(Descendants(rows).OfType<Control>().Where(control => control is Button or CheckBox or ListBoxItem),
                    control => Assert.False(control.IsTabStop && control.IsVisible && control is not ListBoxItem { DataContext: GalleryRow },
                        control.GetType().Name + " " + control.Name));
                Assert.True(rows.IsTabStop);

                Guid third = f.Records[2].Id;
                GalleryItemViewModel FocusedTile() => (GalleryItemViewModel)((FrameworkElement)Keyboard.FocusedElement).DataContext;
                // Entry: the first card when nothing was visited.
                Assert.True(window.FocusGridEntry());
                PumpLibraryUntil(window, () => Keyboard.FocusedElement is ListBoxItem { DataContext: GalleryItemViewModel }, "entry card");
                Assert.Equal(f.ViewModel.Rows.OfType<GalleryTileRow>().First(row => row.Tiles.Count > 0).Tiles[0].Id, FocusedTile().Id);
                // The last active card afterwards, after the focus went elsewhere.
                Assert.True(RealizedCard(rows, third).Focus());
                Assert.Equal(third, FocusedTile().Id);
                ((TextBox)window.FindName("SearchBox")).Focus();
                Assert.True(window.FocusGridEntry());
                PumpLibraryUntil(window, () => Keyboard.FocusedElement is ListBoxItem { DataContext: GalleryItemViewModel t } && t.Id == third, "last active card");
                // A history that is gone falls back to the selection.
                f.ViewModel.Select(f.Records[3].Id);
                window.ForgetLastActiveForTest();
                Assert.True(window.FocusGridEntry());
                PumpLibraryUntil(window, () => Keyboard.FocusedElement is ListBoxItem { DataContext: GalleryItemViewModel t } && t.Id == f.Records[3].Id, "selection entry");

                // An empty list keeps focus on itself.
                f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Empty.Id));
                PumpLibraryUntil(window, () => f.ViewModel.SelectedScope == CaptureFolderScope.ForFolder(f.Empty.Id), "empty folder");
                window.ForgetLastActiveForTest();
                Assert.False(window.FocusGridEntry());
            }
            finally
            {
                window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });

    [Fact]
    public Task LibraryOverhaul_CardFooterNeverClipsOrOverlapsAtEveryTextSizeDpiThemeAndLanguage() =>
        RunIsolated(nameof(LibraryOverhaul_CardFooterNeverClipsOrOverlapsAtEveryTextSizeDpiThemeAndLanguage), () =>
        {
            Application application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            var failures = new List<string>();
            try
            {
                foreach (string language in new[] { "ko-KR", "en-US" })
                foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Daylight, AppTheme.HighContrast })
                foreach (double textScale in new[] { 1d, 1.5d, 2d })
                foreach (double dpi in new[] { 1d, 1.25d, 1.5d, 2d })
                {
                    using var scope = UiText.UseLanguage(language);
                    ThemeService.Apply(theme);
                    LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-matrix-");
                    f.Records[0].Title = "Capture review - toolbar and keyboard navigation";
                    f.Records[0].HasAnnotations = true;
                    f.Records[0].SourcePageUrl = "https://example.com/source";
                    f.ViewModel.Refresh();
                    GalleryWindow window = f.Window;
                    window.Resources["FontSize.Body"] = 14d * textScale;
                    window.Resources["FontSize.Caption"] = 12d * textScale;
                    string label = $"{language}/{theme}/text{textScale}/dpi{dpi}";
                    try
                    {
                        window.Show();
                        VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi, dpi));
                        foreach (UIElement element in Descendants(window).Prepend(window).OfType<UIElement>()) { element.InvalidateMeasure(); element.InvalidateArrange(); }
                        SettleGalleryLayout(window);
                        var rows = (ListBox)window.FindName("RowsList");
                        foreach (bool compact in new[] { false, true })
                        {
                            if (compact) { f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id)); SettleGalleryLayout(window); }
                            string mode = label + (compact ? "/compact" : "/full");
                            foreach (ListBoxItem item in Descendants(rows).OfType<ListBoxItem>().Where(candidate => candidate.DataContext is GalleryItemViewModel).Take(3))
                            {
                                var tile = (GalleryItemViewModel)item.DataContext;
                                if (!compact && tile.Id != f.Records[0].Id) tile.SetSearchMatch(true, CaptureMatchField.Title);
                                SettleGalleryLayout(window);
                                Border card = Descendants(item).OfType<Border>().Single(border => border.Name == "TileCard");
                                Rect cardBounds = new(card.TranslatePoint(new Point(), window), card.RenderSize);
                                var rects = new List<(string Name, Rect Rect)>();
                                foreach (string name in new[] { "TileTitle", "TileTime", "SourceBadge", "PinButton", "CopyButton", "MoreButton", "TileMetadataText", "KeepBadge", "FolderBadge" })
                                    if (Descendants(item).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name) is { IsVisible: true } part)
                                        rects.Add((name, new Rect(part.TranslatePoint(new Point(), window), part.RenderSize)));
                                foreach (Border badge in Descendants(item).OfType<Border>().Where(b => b.IsVisible && b.Name.Length == 0 && Equals(b.ToolTip, UiText.Get("Text_2C64DDA4D878"))))
                                    rects.Add(("EditedBadge", new Rect(badge.TranslatePoint(new Point(), window), badge.RenderSize)));
                                static Rect Inner(Rect rect) => new(rect.X + 0.5, rect.Y + 0.5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
                                foreach ((string name, Rect rect) in rects)
                                    if (!cardBounds.Contains(Inner(rect)))
                                        failures.Add($"{mode}: {name} {rect} leaves the card {cardBounds}");
                                void Apart(string a, string b)
                                {
                                    foreach (Rect x in rects.Where(r => r.Name == a).Select(r => r.Rect))
                                        foreach (Rect y in rects.Where(r => r.Name == b).Select(r => r.Rect))
                                            if (Inner(x).IntersectsWith(Inner(y))) failures.Add($"{mode}: {a} {x} overlaps {b} {y}");
                                }
                                foreach (string badge in new[] { "SourceBadge", "TileMetadataText", "KeepBadge", "EditedBadge", "FolderBadge", "CopyButton", "TileTime" })
                                    Apart(badge, "PinButton");
                                Apart("TileTitle", "TileTime");
                                Apart("PinButton", "CopyButton");
                                Apart("CopyButton", "MoreButton");
                                Apart("TileTime", "MoreButton");
                                var title = (TextBlock)CardPart<TextBlock>(item, "TileTitle");
                                if (title.ActualWidth < 24) failures.Add($"{mode}: the title has only {title.ActualWidth:0.#} DIP");
                            }
                        }
                    }
                    finally { window.CloseForExit(); }
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct().Take(30)));
        });
}
