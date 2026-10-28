using System.Windows;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryOverhaul_MixedRowsReachDayHeadingByUpAndActivateWithEnterAndSpace() =>
        RunIsolated(nameof(LibraryOverhaul_MixedRowsReachDayHeadingByUpAndActivateWithEnterAndSpace), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-day-keys-");
            GalleryWindow window = f.Window;
            try
            {
                window.Show(); window.Activate(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                GalleryHeaderRow header = Assert.IsType<GalleryHeaderRow>(f.ViewModel.Rows[0]);
                GalleryFolderRow folders = Assert.IsType<GalleryFolderRow>(f.ViewModel.Rows[1]);
                GalleryTileRow captures = f.ViewModel.Rows.OfType<GalleryTileRow>().First();
                Guid[] dayIds = f.ViewModel.Groups.Single(group => group.Heading == header.Heading).Items.Select(tile => tile.Id).Order().ToArray();

                foreach (Key activate in new[] { Key.Enter, Key.Space })
                {
                    f.ViewModel.ClearSelection();
                    Assert.True(RealizedCard(rows, captures.Tiles[0].Id).Focus());
                    ProcessLibraryFocusKey(window, Key.Up);
                    PumpLibraryUntil(window, () => Keyboard.FocusedElement is Button { DataContext: GalleryFolderCardViewModel folder }
                        && folder.FolderId == folders.Folders[0].FolderId, "Up from the first capture row reaches its folder row");
                    ProcessLibraryFocusKey(window, Key.Up);
                    PumpLibraryUntil(window, () => Keyboard.FocusedElement is Button { DataContext: GalleryHeaderRow current }
                        && ReferenceEquals(current, header), "Up from the first folder row reaches the day heading");

                    ProcessLibraryFocusKey(window, activate);
                    PumpLibraryUntil(window, () => f.ViewModel.Selection.SelectedIds.Order().SequenceEqual(dayIds),
                        activate + " on the reached heading selects exactly that day's captures");
                    ProcessLibraryFocusKey(window, Key.Down);
                    PumpLibraryUntil(window, () => Keyboard.FocusedElement is Button { DataContext: GalleryFolderCardViewModel folder }
                        && folder.FolderId == folders.Folders[0].FolderId, "Down from the heading returns to its first folder row");
                }
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryOverhaul_TabAndShiftTabRestoreTheLastFolderOrCaptureInAMixedGrid() =>
        RunIsolated(nameof(LibraryOverhaul_TabAndShiftTabRestoreTheLastFolderOrCaptureInAMixedGrid), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-mixed-tab-");
            GalleryWindow window = f.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                window.Show(); window.Activate(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                Guid captureId = f.Records[2].Id;
                Guid selectedId = f.Records[3].Id;
                f.ViewModel.Select(selectedId);
                SettleGalleryLayout(window);
                Button folderCard = Descendants(rows).OfType<Button>()
                    .First(button => button.DataContext is GalleryFolderCardViewModel folder && folder.FolderId == f.Empty.Id);
                ListBoxItem captureCard = RealizedCard(rows, captureId);
                Assert.True(f.ViewModel.Rows.OfType<GalleryFolderRow>().Any());
                Assert.True(f.ViewModel.Rows.OfType<GalleryTileRow>().Any());
                var focusChanges = new List<string>();
                window.GotKeyboardFocus += (_, e) => focusChanges.Add(FocusTargetDescription(e.NewFocus));
                void WaitForTarget(FrameworkElement target, string reason)
                {
                    try { PumpLibraryUntil(window, () => ReferenceEquals(target, Keyboard.FocusedElement), reason); }
                    catch (TimeoutException ex)
                    {
                        throw new TimeoutException(ex.Message + " Expected " + FocusTargetDescription(target)
                            + "; current " + FocusTargetDescription(Keyboard.FocusedElement)
                            + "; input=" + InputManager.Current.MostRecentInputDevice?.GetType().Name
                            + "; transitions=" + string.Join(" -> ", focusChanges), ex);
                    }
                }

                foreach (FrameworkElement target in new FrameworkElement[] { captureCard, folderCard })
                {
                    Assert.True(target.Focus());
                    Assert.Same(target, Keyboard.FocusedElement);
                    // These keys pass through InputManager and WPF KeyboardNavigation. Calling the
                    // product's FocusGridEntry directly would miss both exit direction and re-entry.
                    ProcessLibraryFocusKey(window, Key.Tab);
                    PumpLibraryUntil(window, () => !rows.IsKeyboardFocusWithin, "Tab leaves the whole grid in one step");
                    Assert.NotNull(Keyboard.FocusedElement);
                    ProcessLibraryFocusKey(window, Key.Tab, ModifierKeys.Shift);
                    WaitForTarget(target, "Shift+Tab restores the last active card");
                    ProcessLibraryFocusKey(window, Key.Tab, ModifierKeys.Shift);
                    PumpLibraryUntil(window, () => !rows.IsKeyboardFocusWithin, "Shift+Tab leaves the whole grid in one step");
                    Assert.NotNull(Keyboard.FocusedElement);
                    ProcessLibraryFocusKey(window, Key.Tab);
                    WaitForTarget(target, "Tab restores the last active card");
                }

                // Filtering removes the remembered folder from this grid without deleting it. Only
                // then may re-entry fall back to the selected capture, which is still present.
                ProcessLibraryFocusKey(window, Key.Tab);
                PumpLibraryUntil(window, () => !rows.IsKeyboardFocusWithin, "leave the folder before filtering");
                var afterGrid = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                f.ViewModel.SearchQuery = "Library sample";
                f.ViewModel.Select(selectedId);
                SettleGalleryLayout(window);
                Assert.Empty(f.ViewModel.Rows.OfType<GalleryFolderRow>());
                Assert.True(afterGrid.Focus());
                ProcessLibraryFocusKey(window, Key.Tab, ModifierKeys.Shift);
                PumpLibraryUntil(window, () => Keyboard.FocusedElement is ListBoxItem { DataContext: GalleryItemViewModel tile }
                    && tile.Id == selectedId, "a missing remembered folder falls back to the selected capture");
            }
            finally
            {
                window.CloseForExit(); SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown();
            }
        });

    private static void ProcessLibraryFocusKey(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        using var keyboardState = new ThreadKeyboardModifierScope(modifiers);
        Assert.Equal(modifiers, Keyboard.Modifiers);
        var target = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
        PresentationSource source = PresentationSource.FromVisual(window)!;
        // Direct ProcessInput starts after the native raw-input provider. Supply the device context
        // that provider normally establishes, without global OS input or calling the product's
        // entry helper; Tab traversal and routed button handlers still run inside WPF itself.
        PropertyInfo recentDevice = typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!;
        InputDevice? previousDevice = InputManager.Current.MostRecentInputDevice;
        recentDevice.SetValue(InputManager.Current, Keyboard.PrimaryDevice);
        // ProcessInput promotes PreviewKeyDown to KeyDown and runs the framework's post-processing
        // (including Tab navigation); PreviewKeyUp similarly runs the Button's Space release path.
        try
        {
            Assert.Same(Keyboard.PrimaryDevice, InputManager.Current.MostRecentInputDevice);
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = target });
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent, Source = Keyboard.FocusedElement ?? target });
            SettleGalleryLayout(window);
        }
        finally { recentDevice.SetValue(InputManager.Current, previousDevice); }
    }

    private static string FocusTargetDescription(IInputElement? element) => element is FrameworkElement control
        ? control.GetType().Name + ":" + control.Name + ":" + (control.DataContext switch
        {
            GalleryItemViewModel tile => "capture " + tile.Id,
            GalleryFolderCardViewModel folder => "folder " + folder.FolderId,
            _ => control.DataContext?.GetType().Name ?? "no data",
        })
        : element?.GetType().Name ?? "null";
}
