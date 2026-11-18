using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task FocusCue_PointerTabPointerUpdatesButtonsCheckBoxesCategoriesAndLibraryCards() =>
        RunIsolated(nameof(FocusCue_PointerTabPointerUpdatesButtonsCheckBoxesCategoriesAndLibraryCards), () =>
        {
            Application application = CreateLibraryMotionApplication();
            bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            using var language = UiText.UseLanguage("ko-KR");
            string profile = OwnedTestDirectory.Create("mc-focus-cue-");
            // Match the application dispatcher context used by the established mixed-grid
            // Tab re-entry test. Nested focus and input processing must remain on this dispatcher.
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight, AppTheme.HighContrast })
                {
                    RecordIsolatedProgress("Focus cue theme " + theme);
                    ThemeService.Apply(theme);
                    var button = new Button { Content = "검토 / Review" };
                    var check = new CheckBox { Content = "선택 / Select" };
                    var panel = new StackPanel();
                    panel.Children.Add(new Button { Content = "이전 / Previous" });
                    panel.Children.Add(button);
                    panel.Children.Add(check);
                    panel.Children.Add(new Button { Content = "다음 / Next" });
                    var host = FocusCueWindow(panel);
                    try
                    {
                        RecordIsolatedProgress("Focus cue host show");
                        host.Show(); host.Activate(); SettleGalleryLayout(host);
                        RecordIsolatedProgress("Focus cue button pointer round trip");
                        FocusCuePointerRoundTrip(host, button, visible => FocusCueAssertRing(button, visible));
                        RecordIsolatedProgress("Focus cue checkbox pointer round trip");
                        FocusCuePointerRoundTrip(host, check, visible => FocusCueAssertRing(check, visible));
                    }
                    finally
                    {
                        RecordIsolatedProgress("Focus cue host close start");
                        host.Close();
                        RecordIsolatedProgress("Focus cue host close completed");
                    }

                    RecordIsolatedProgress("Focus cue settings construct");
                    AppSettings saved = NewInstallDefaults.Create();
                    var settings = new SettingsWindow(() => saved,
                        _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance,
                        defaultCapturesRoot: () => profile)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -24000, Top = -14000, Width = 940, Height = 700, ShowInTaskbar = false,
                    };
                    FluidMotion.SetWindowEntrance(settings, false);
                    try
                    {
                        RecordIsolatedProgress("Focus cue settings show");
                        settings.ShowSettings(); SettleGalleryLayout(settings);
                        RecordIsolatedProgress("Focus cue settings category round trip");
                        var categories = (TabControl)settings.FindName("CategoryTabs");
                        var category = Assert.IsType<TabItem>(categories.SelectedItem);
                        FocusCuePointerRoundTrip(settings, category, visible =>
                        {
                            var border = Assert.IsType<Border>(category.Template.FindName("Bd", category));
                            Brush focus = Assert.IsAssignableFrom<Brush>(settings.FindResource("Border.Focus"));
                            Assert.Equal(visible, ReferenceEquals(focus, border.BorderBrush));
                        });
                    }
                    finally
                    {
                        RecordIsolatedProgress("Focus cue settings close start");
                        settings.CloseForExit();
                        RecordIsolatedProgress("Focus cue settings close completed");
                    }

                    RecordIsolatedProgress("Focus cue library construct");
                    NativeFeelLibraryFixture fixture = NativeFeelNewLibrary(profile, "library-" + theme, 3);
                    GalleryWindow library = fixture.Window;
                    library.WindowStartupLocation = WindowStartupLocation.Manual;
                    library.Left = -24000; library.Top = -14000;
                    library.Width = 1180; library.Height = 720; library.ShowInTaskbar = false;
                    FluidMotion.SetWindowEntrance(library, false);
                    try
                    {
                        RecordIsolatedProgress("Focus cue library show");
                        library.Show(); library.Activate();
                        PumpLibraryUntil(library, () => library.RealizedCards().Count() == 3, "focus-cue fixture cards");
                        ListBoxItem card = library.RealizedCards().First();
                        RecordIsolatedProgress("Focus cue library card round trip");
                        FocusCuePointerRoundTrip(library, card, visible =>
                        {
                            RecordIsolatedProgress("Card focus state before assert: focused=" + card.IsKeyboardFocused
                                + "; within=" + card.IsKeyboardFocusWithin + "; cue=" + KeyboardFocusCue.GetIsVisible(card)
                                + "; tracked=" + GalleryCardInput.GetTrack(card) + "; shown=" + GalleryCardInput.GetShowsKeyboardFocus(card));
                            Assert.Equal(visible, GalleryCardInput.GetShowsKeyboardFocus(card));
                            RecordIsolatedProgress("Card shared state asserted " + visible);
                            FocusCueAssertRing(card, visible);
                        });
                    }
                    finally
                    {
                        RecordIsolatedProgress("Focus cue library close start");
                        library.CloseForExit();
                        RecordIsolatedProgress("Focus cue library close completed");
                    }
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
                SynchronizationContext.SetSynchronizationContext(previousContext);
                RecordIsolatedProgress("Focus cue application shutdown start");
                application.Shutdown();
                RecordIsolatedProgress("Focus cue application shutdown completed");
            }
        });

    [Fact]
    public Task FocusCue_InitialProgrammaticFocusIsQuietAndAKeyOpenedWindowKeepsKeyboardCues() =>
        RunIsolated(nameof(FocusCue_InitialProgrammaticFocusIsQuietAndAKeyOpenedWindowKeepsKeyboardCues), () =>
        {
            Application application = CreateLibraryMotionApplication();
            Window? opened = null;
            var first = new Button { Content = "열기 / Open" };
            var host = FocusCueWindow(first);
            try
            {
                // No input is supplied before this first window assigns its initial focus.
                host.Show(); host.Activate(); Assert.True(first.Focus()); SettleGalleryLayout(host);
                Assert.Same(first, Keyboard.FocusedElement);
                Assert.False(KeyboardFocusCue.GetIsVisible(first));
                FocusCueAssertRing(first, false);

                var next = new Button { Content = "다음 / Next" };
                first.Click += (_, _) =>
                {
                    opened = FocusCueWindow(next);
                    opened.Show(); opened.Activate(); Assert.True(next.Focus());
                };
                ProcessLibraryFocusKey(host, Key.Enter);
                Assert.NotNull(opened);
                SettleGalleryLayout(opened!);
                Assert.Same(next, Keyboard.FocusedElement);
                Assert.True(KeyboardFocusCue.GetIsVisible(next));
                FocusCueAssertRing(next, true);

                using var mouse = new QueuedMouse(opened!);
                mouse.Click(next);
                Assert.Same(next, Keyboard.FocusedElement);
                Assert.False(KeyboardFocusCue.GetIsVisible(next));
                FocusCueAssertRing(next, false);
                Assert.False(KeyboardFocusCue.GetIsVisible(first));
            }
            finally { opened?.Close(); host.Close(); application.Shutdown(); }
        });

    private static Window FocusCueWindow(UIElement content)
    {
        var window = new Window
        {
            Content = content, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -24000, Top = -14000, Width = 320, Height = 240,
            ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        return window;
    }

    private static void FocusCuePointerRoundTrip(Window window, Control target, Action<bool> assertVisual)
    {
        using var mouse = new QueuedMouse(window);
        try
        {
            RecordIsolatedProgress("Pointer click " + target.GetType().Name);
            mouse.Click(target);
            RecordIsolatedProgress("Pointer click completed " + target.GetType().Name);
            Assert.Same(target, Keyboard.FocusedElement);
            RecordIsolatedProgress("First pointer focus asserted");
            Assert.False(KeyboardFocusCue.GetIsVisible(target));
            RecordIsolatedProgress("First pointer cue asserted");
            assertVisual(false);
            RecordIsolatedProgress("First pointer visual asserted");

            // These existing helpers exercise the WPF input manager and its real Tab traversal.
            // They do not call the cue helper, replace an input predicate, or move the OS cursor.
            RecordIsolatedProgress("Tab forward " + target.GetType().Name);
            ProcessLibraryFocusKey(window, Key.Tab);
            Assert.NotSame(target, Keyboard.FocusedElement);
            RecordIsolatedProgress("Tab exit focus asserted");
            if (window is GalleryWindow)
            {
                var rows = (ListBox)window.FindName("RowsList");
                Assert.False(rows.IsKeyboardFocusWithin);
                RecordIsolatedProgress("Tab exit list boundary asserted");
            }
            RecordIsolatedProgress("Tab exit target " + FocusTargetDescription(Keyboard.FocusedElement));
            RecordIsolatedProgress("Tab backward " + target.GetType().Name);
            ProcessLibraryFocusKey(window, Key.Tab, ModifierKeys.Shift);
            RecordIsolatedProgress("Tab entry target " + FocusTargetDescription(Keyboard.FocusedElement)
                + "; expected=" + FocusTargetDescription(target));
            Assert.Same(target, Keyboard.FocusedElement);
            RecordIsolatedProgress("Tab entry focus asserted");
            Assert.True(KeyboardFocusCue.GetIsVisible(target));
            RecordIsolatedProgress("Tab entry cue asserted");
            assertVisual(true);
            RecordIsolatedProgress("Tab entry visual asserted");

            RecordIsolatedProgress("Second pointer click " + target.GetType().Name);
            // A separate press inside the same control must not become its double-click command.
            mouse.Click(target, new Point(target.ActualWidth / 4, target.ActualHeight / 2));
            RecordIsolatedProgress("Second pointer click completed " + target.GetType().Name);
            Assert.Same(target, Keyboard.FocusedElement);
            RecordIsolatedProgress("Second pointer focus asserted");
            Assert.False(KeyboardFocusCue.GetIsVisible(target));
            RecordIsolatedProgress("Second pointer cue asserted");
            assertVisual(false);
            RecordIsolatedProgress("Second pointer visual asserted");
        }
        catch (Exception ex)
        {
            RecordIsolatedProgress("ASSERTFAIL " + ex);
            throw;
        }
    }

    private static void FocusCueAssertRing(Control control, bool visible)
    {
        var ring = Assert.IsAssignableFrom<UIElement>(control.Template.FindName("FocusRing", control));
        RecordIsolatedProgress("Focus ring found " + control.GetType().Name + "; opacity=" + ring.Opacity + "; expected=" + visible);
        Assert.Equal(visible ? 1d : 0d, ring.Opacity);
        RecordIsolatedProgress("Focus ring opacity asserted " + control.GetType().Name);
    }
}
