using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The settings window's automatic-deletion choice, Apply/Close, storage folder actions, font list
/// and About sections, and the library's delete-with-Undo, restore menu, retention notice and empty
/// state, all on the real window markup with test hooks in place of dialogs and processes.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibrarySettingsUx_RetentionApplyCloseStorageFontsAndAbout() =>
        RunIsolated(nameof(LibrarySettingsUx_RetentionApplyCloseStorageFontsAndAbout), VerifyLibrarySettingsWindow);

    [Fact]
    public Task LibrarySettingsUx_DeleteUndoRestoreMenuNoticeAndEmptyLibrary() =>
        RunIsolated(nameof(LibrarySettingsUx_DeleteUndoRestoreMenuNoticeAndEmptyLibrary), VerifyLibraryUndoWindow);

    private static void VerifyLibrarySettingsWindow()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        AppPackage.IsPackagedOverride = true;
        string never = CaptureRetention.NeverHours.ToString(CultureInfo.InvariantCulture);
        try
        {
            var saved = new AppSettings();
            saved.Queue.ImageRetentionHours = 36;
            saved.Annotation.FontFamily = "Retired Font";
            SettingsUxFixture fixture = CreateSettingsUxWindow(saved);
            SettingsWindow window = fixture.Window;
            string libraryRoot = TestRecycleBin.CreateTempSubdirectory("mc-settings-library-").FullName;
            window.DefaultCapturesRoot = () => libraryRoot;
            var opened = new List<string>();
            window.OpenFolderInShell = opened.Add;
            window.ShowSettings();
            Drain(window);
            var tabs = (TabControl)window.FindName("CategoryTabs");
            Assert.Null(window.FindName("ApplyButton"));
            var cancel = (Button)window.FindName("CancelButton");

            // The one footer action always closes and retains saved changes.
            Assert.Equal(UiText.Get("Dialog.Close"), cancel.Content);
            Assert.Equal(UiText.Get("Dialog.Close"), AutomationProperties.GetName(cancel));
            Assert.Equal(UiText.Get("SettingsUx_CloseTip"), cancel.ToolTip);

            // The saved 36 hours is a custom choice ahead of the presets; nothing changes by opening.
            tabs.SelectedItem = window.FindName("StorageTab");
            Drain(window);
            var retention = (ComboBox)window.FindName("RetentionChoiceBox");
            Assert.Equal(["사용자 지정 (36시간)", "1일", "7일", "30일", "90일", "1년", "삭제 안 함"],
                retention.Items.Cast<RetentionChoice>().Select(choice => choice.Label));
            Assert.Equal("36", retention.SelectedValue);
            Assert.Equal(UiText.Format("SettingsUx_RetentionSummary", SettingsUxText.Duration(36)),
                ((TextBlock)window.FindName("RetentionSummary")).Text);
            Assert.Equal(UiText.Get("Retention_Label"), AutomationProperties.GetName(retention));
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.IsVisible
                && MyCapture.App.Themes.KoreanWordWrap.Strip(text.Text) == UiText.Get("Settings_LibraryMaxItems"));

            // Ctrl+S can flush a choice before its automatic save dispatch.
            retention.SelectedValue = never;
            Assert.Equal(never, Draft(window).ImageRetentionHours);
            Assert.Equal(UiText.Get("Retention_SummaryNever"), ((TextBlock)window.FindName("RetentionSummary")).Text);
            Assert.Equal(UiText.Get("Dialog.Close"), cancel.Content);
            Assert.Equal(7, retention.Items.Count);
            retention.SelectedValue = "36";
            Assert.Equal(UiText.Get("Dialog.Close"), cancel.Content);

            // Applying "never" stores it; reopening offers only the presets.
            retention.SelectedValue = never;
            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.Equal(CaptureRetention.NeverHours, fixture.Saved().Queue.ImageRetentionHours);
            SettingsWindow.CloseCommand.Execute(null, window);
            window.ShowSettings();
            Drain(window);
            Assert.Equal(6, retention.Items.Count);
            Assert.Equal(never, retention.SelectedValue);

            // Capture folder: Open folder, and the Store build's note while the default is used.
            var note = (TextBlock)window.FindName("CaptureFolderDefaultNote");
            var openFolder = (Button)window.FindName("OpenCapturesFolderButton");
            // The box names the default folder (here the fixture's stand-in for it).
            Draft(window).CapturesDirectoryOverride = libraryRoot;
            Drain(window);
            Assert.True(note.IsVisible);
            openFolder.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([Path.GetFullPath(libraryRoot)], opened);
            string elsewhere = TestRecycleBin.CreateTempSubdirectory("mc-settings-elsewhere-").FullName;
            Draft(window).CapturesDirectoryOverride = elsewhere;
            Drain(window);
            Assert.False(note.IsVisible);
            Draft(window).CapturesDirectoryOverride = Path.Combine(elsewhere, "missing");
            openFolder.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Single(opened);
            Assert.Equal(UiText.Get("Storage_OpenFolderFailed"), ((TextBlock)window.FindName("SettingsStatus")).Text);
            AppPackage.IsPackagedOverride = false;
            Draft(window).CapturesDirectoryOverride = libraryRoot;
            Assert.False(note.IsVisible);
            AppPackage.IsPackagedOverride = true;

            // Fonts: only the saved one until the editing page appears, then the installed list.
            var fonts = (ComboBox)window.FindName("FontFamilyBox");
            Assert.Single(fonts.Items);
            tabs.SelectedIndex = 3;
            Drain(window);
            Assert.True(fonts.Items.Count > 5, "installed fonts listed");
            Assert.Equal("Retired Font", fonts.SelectedValue);
            Assert.Equal("Retired Font", ((FontChoice)fonts.Items[0]).Value);
            var installed = (FontChoice)fonts.Items[1];
            fonts.SelectedItem = installed;
            Assert.Equal(installed.Value, Draft(window).FontFamily);
            Assert.Equal("35", ((TextBox)window.FindName("HighlighterOpacityBox")).Text);

            // About: four sections 24 apart.
            tabs.SelectedItem = window.FindName("UpdateTab");
            Drain(window);
            string[] sections = ["AboutUpdatesSection", "AboutPrivacySection", "AboutFeedbackSection", "AboutDataSection"];
            Assert.Equal([UiText.Get("About_UpdatesHeading"), UiText.Get("About_PrivacyHeading"), UiText.Get("About_FeedbackHeading"),
                UiText.Get("About_DataHeading")],
                sections.Select(name => ((TextBlock)((Panel)window.FindName(name)).Children[0]).Text));
            foreach (string name in sections.SkipLast(1)) Assert.Equal(24, ((FrameworkElement)window.FindName(name)).Margin.Bottom);
            string privacy = ((TextBlock)window.FindName("PrivacyModelText")).Text;
            Assert.Contains("13 MB", privacy, StringComparison.Ordinal);
            Assert.Contains("ModelScope", privacy, StringComparison.Ordinal);
            Assert.Contains("67 MB", privacy, StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("StorePageAction")).Visibility);
            Assert.True(((Button)window.FindName("StoreRatingButton")).IsVisible);

            // Opening support does not copy or launch; those actions are explicit in its dialog.
            string? copied = null;
            var pages = new List<string>();
            window.CopySupportText = value => copied = value;
            window.OpenSupportPage = pages.Add;
            SupportWindow? support = null;
            window.OpenSupportDialog = dialog => support = dialog;
            var report = (Button)window.FindName("SupportReport");
            report.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.NotNull(support);
            Assert.Null(copied);
            Assert.Empty(pages);
            support.MessageBox.Text = "fixture support message";
            support.CopyDiagnostics();
            Assert.Contains("Windows", copied, StringComparison.Ordinal);
            Assert.Contains("fixture support message", copied, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\", copied, StringComparison.OrdinalIgnoreCase);
            support.OpenGitHub();
            Assert.StartsWith(SupportDiagnostics.IssueUri + "?", Assert.Single(pages), StringComparison.Ordinal);
            support.Close();
            window.CopySupportText = _ => throw new InvalidOperationException("clipboard busy");
            report.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            support!.CopyDiagnostics(); // Failure is contained inside the dialog.
            Assert.Single(pages); // Clipboard failure never launches a browser by itself.
            support.Close();
            Assert.Null(window.FindName("DiagnosticsCopy"));

            // A check reports its time; a failed one offers the Store listing.
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                window.StoreUpdatePackaged = true;
                window.StoreCheckClock = () => new DateTimeOffset(2026, 10, 13, 14, 2, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 13)));
                var store = new ObservableStoreUpdates();
                window.StoreUpdates = store;
                Assert.True(window.RunStoreUpdateActionAsync().IsCompletedSuccessfully);
                Assert.Equal("마지막 확인 14:02 · 최신 버전", ((TextBlock)window.FindName("UpdateObservedStatus")).Text);
                store.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task check = window.RunStoreUpdateActionAsync();
                store.CheckCompletion.SetException(new IOException("Store unavailable"));
                PumpUntil(window, check);
                Assert.Equal(UiText.Get("StoreUpdate_StateFailed"), ((TextBlock)window.FindName("UpdateObservedStatus")).Text);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("StorePageAction")).Visibility);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            // The library's "Change" lands on the automatic-deletion choice.
            window.Hide();
            window.ShowRetentionSetting();
            Drain(window);
            Assert.True(window.IsVisible);
            Assert.Same(window.FindName("StorageTab"), tabs.SelectedItem);
            Assert.True(retention.IsKeyboardFocusWithin || ReferenceEquals(FocusManager.GetFocusedElement(window), retention));
            window.CloseForExit();
        }
        finally
        {
            AppPackage.IsPackagedOverride = null;
            application.Shutdown();
        }
    }

    private static void VerifyLibraryUndoWindow()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateLibraryApplication();
        try
        {
            VerifyDeleteUndoAndRestoreMenu();
            VerifyRetentionNotice();
            VerifyEmptyLibrary();
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void VerifyDeleteUndoAndRestoreMenu()
    {
        EvidenceLibrary library = CreateEvidenceLibrary(8, "mc-library-undo-window-");
        GalleryWindow window = library.Window;
        try
        {
            window.Show();
            window.ShowGallery();
            SettleLibrary(window);
            var asked = new List<int>();
            window.ConfirmDelete = (count, _) => { asked.Add(count); return true; };
            var status = (TextBlock)window.FindName("LibraryActionStatus");

            // A delete reports on the status line with Undo; the view is rebuilt, no dialog stays up.
            Guid[] three = library.Queue.Records.Take(3).Select(record => record.Id).ToArray();
            PumpUiTask(window, async () => { await window.DeleteCapturesAsync(three); return true; });
            SettleLibrary(window);
            Assert.Equal([3], asked);
            Assert.Equal(5, library.Queue.Count);
            Assert.Equal(5, library.ViewModel.VisibleCount);
            Assert.Empty(Application.Current.Windows.OfType<ThemedDialog>());
            Assert.Equal("3개를 휴지통으로 옮김", status.Text);
            Assert.Equal(UiText.Get("LibraryUndo_Action"), window.StatusActionLabelForTest);
            var action = (Button)window.FindName("LibraryStatusAction");
            Assert.Equal(UiText.Get("LibraryUndo_ActionName"), AutomationProperties.GetName(action));

            // Undo brings the whole batch back.
            PumpUiTask(window, async () => { await window.InvokeStatusActionForTest(); return true; });
            SettleLibrary(window);
            Assert.Equal(8, library.Queue.Count);
            Assert.Equal(8, library.ViewModel.VisibleCount);
            Assert.Equal("라이브러리에 3개 복원됨", status.Text);
            Assert.Empty(window.StatusActionLabelForTest);

            // Ctrl+Z undoes the session's last delete too.
            Guid[] two = library.Queue.Records.Take(2).Select(record => record.Id).ToArray();
            PumpUiTask(window, async () => { await window.DeleteCapturesAsync(two); return true; });
            KeyBinding undoKey = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.Z && binding.Modifiers == ModifierKeys.Control);
            // Input handlers run with WPF's synchronization context, as a real key press does.
            PumpUiTask(window, () => { undoKey.Command.Execute(null); return Task.FromResult(true); });
            PumpLibraryUntil(window, () => status.Text, text => text == "라이브러리에 2개 복원됨", "Ctrl+Z restore");
            Assert.Equal(8, library.Queue.Count);

            // The restore menu: the last delete first, then each capture with its thumbnail and time.
            Guid[] last = library.Queue.Records.Take(2).Select(record => record.Id).ToArray();
            PumpUiTask(window, async () => { await window.DeleteCapturesAsync(last); return true; });
            OpenRestoreMenu(window);
            var restore = (MenuItem)window.FindName("RestoreDeletedMenu");
            var first = (MenuItem)restore.Items[0];
            Assert.Equal("마지막 삭제 되돌리기 (2개)", first.Header);
            Assert.Equal("Ctrl+Z", first.InputGestureText);
            Assert.IsType<Separator>(restore.Items[1]);
            MenuItem[] entries = restore.Items.OfType<MenuItem>().Skip(1).ToArray();
            Assert.Equal(2, entries.Length);
            foreach (MenuItem entry in entries)
            {
                Assert.EndsWith("방금 삭제", AutomationProperties.GetName(entry), StringComparison.Ordinal);
                Image thumbnail = Descendants((DependencyObject)entry.Header).OfType<Image>().Single();
                Assert.NotNull(thumbnail.Source);
                Assert.Equal(32, ((FrameworkElement)VisualTreeHelper.GetParent(thumbnail)).ActualWidth, 1);
            }
            PumpUiTask(window, () => { first.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); return Task.FromResult(true); });
            CloseRestoreMenu(window);
            PumpLibraryUntil(window, () => status.Text, text => text == "라이브러리에 2개 복원됨", "menu undo");
            Assert.Equal(8, library.Queue.Count);

            // With a selection, More (and so restore and the library folder) stays reachable.
            library.ViewModel.Select(library.Queue.Records[0].Id);
            SettleLibrary(window);
            Assert.True(((FrameworkElement)window.FindName("SelectionCommands")).IsVisible);
            Assert.True(((FrameworkElement)window.FindName("MoreCommandsButton")).IsVisible);
            var opened = new List<string>();
            window.OpenFolderInShell = opened.Add;
            ((MenuItem)window.FindName("OpenLibraryFolderMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal([Path.GetFullPath(library.Paths.CapturesRoot)], opened);
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static void VerifyRetentionNotice()
    {
        EvidenceLibrary library = CreateEvidenceLibrary(3, "mc-library-notice-");
        GalleryWindow window = library.Window;
        int opened = 0;
        window.OpenRetentionSettings = () => opened++;
        try
        {
            // Deleted while the library was closed: kept until the library is on screen.
            library.Recovery.NoteRetentionDeletion(4);
            window.RefreshFromQueue();
            Assert.Equal(4, library.Recovery.PendingRetentionNotice);
            window.Show();
            window.ShowGallery();
            SettleLibrary(window);
            var status = (TextBlock)window.FindName("LibraryActionStatus");
            Assert.Equal("보관 기간이 지난 이미지 4개를 삭제했습니다", status.Text);
            Assert.Equal(UiText.Get("Retention_NoticeAction"), window.StatusActionLabelForTest);
            PumpUiTask(window, async () => { await window.InvokeStatusActionForTest(); return true; });
            Assert.Equal(1, opened);

            // Once per installation.
            library.Recovery.NoteRetentionDeletion(2);
            window.RefreshFromQueue();
            Assert.NotEqual("보관 기간이 지난 이미지 2개를 삭제했습니다", status.Text);
            Assert.Null(library.Recovery.PendingRetentionNotice);
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static void VerifyEmptyLibrary()
    {
        EvidenceLibrary library = CreateEvidenceLibrary(0, "mc-library-empty-");
        GalleryWindow window = library.Window;
        try
        {
            window.Show();
            window.ShowGallery();
            SettleLibrary(window);
            Assert.Equal("아직 캡처가 없습니다", library.ViewModel.EmptyStateTitle);
            Assert.Equal("Ctrl+Shift+C로 캡처하면 여기에 표시됩니다.", library.ViewModel.EmptyStateText);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text == library.ViewModel.EmptyStateText);
            Assert.False(((FrameworkElement)window.FindName("GalleryDetailsPanel")).IsVisible);
            Assert.False(((FrameworkElement)window.FindName("GalleryDetailsRail")).IsVisible);

            // The shortcut is the one set now; without one the hint names none.
            var hotkeys = new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkC) };
            window.ReadHotkeys = () => hotkeys;
            window.ShowGallery();
            Assert.Equal("Ctrl+Alt+C로 캡처하면 여기에 표시됩니다.", library.ViewModel.EmptyStateText);
            hotkeys.Capture = Hotkey.None;
            window.ShowGallery();
            Assert.Equal(UiText.Get("Library.Empty.Hint"), library.ViewModel.EmptyStateText);
            using (UiText.UseLanguage("en-US"))
                Assert.Equal("No captures yet", library.ViewModel.EmptyStateTitle);

            // The first capture brings the details pane back.
            var record = new CaptureRecord { Width = 160, Height = 100, CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now };
            library.Queue.Add(record);
            Directory.CreateDirectory(library.Queue.GetDirectory(record));
            File.WriteAllBytes(library.Queue.GetFilePath(record, CaptureFileNames.Original), [1]);
            window.RefreshFromQueue();
            SettleLibrary(window);
            Assert.True(((FrameworkElement)window.FindName("GalleryDetailsPanel")).IsVisible);
            Assert.True(window.IsDetailsPaneOpen);
        }
        finally
        {
            window.CloseForExit();
        }
    }
}
