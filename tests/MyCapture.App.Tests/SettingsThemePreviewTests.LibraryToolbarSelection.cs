using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryToolbar_SelectionBurstsUseLatestStateAcrossHideShowAndClose() =>
        RunIsolated(nameof(LibraryToolbar_SelectionBurstsUseLatestStateAcrossHideShowAndClose), () =>
        {
            Application application = CreateLibraryMotionApplication();
            try
            {
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var languageScope = UiText.UseLanguage(language);
                    LibraryUxFixture fixture = CreateLibraryUxFixture("mc-library-toolbar-selection-");
                    GalleryWindow window = fixture.Window;
                    GalleryViewModel vm = fixture.ViewModel;
                    CaptureRecord kept = fixture.Records[1], normal = fixture.Records[0];
                    bool closed = false;
                    window.Closed += (_, _) => closed = true;
                    try
                    {
                        Assert.True(kept.IsManuallyPinned);
                        Assert.False(normal.IsManuallyPinned);
                        window.Show();
                        SettleGalleryLayout(window);
                        var label = Assert.IsType<TextBlock>(window.FindName("KeepSelectedLabel"));
                        var button = Assert.IsType<Button>(window.FindName("KeepSelectedButton"));
                        var archive = Assert.IsType<MenuItem>(window.FindName("ArchiveOverflowMenu"));
                        var move = Assert.IsType<MenuItem>(window.FindName("MoveOverflowMenu"));
                        string keep = UiText.Get("LibraryUx_Keep");
                        string unkeep = UiText.Get("LibraryUx_Unkeep");
                        string keepHint = UiText.Get("LibraryUx_KeepHint");

                        void AssertToolbar(string expectedLabel, string expectedHint, bool enabled, string phase)
                        {
                            PumpLibraryUntil(window, () =>
                                    label.Text == expectedLabel
                                    && AutomationProperties.GetName(button) == expectedLabel
                                    && button.ToolTip is string hint && hint == expectedHint
                                    && archive.IsEnabled == enabled && move.IsEnabled == enabled,
                                language + ": " + phase);
                            Assert.Equal(expectedLabel, label.Text);
                            Assert.Equal(expectedLabel, AutomationProperties.GetName(button));
                            Assert.Equal(expectedHint, Assert.IsType<string>(button.ToolTip));
                            Assert.Equal(enabled, archive.IsEnabled);
                            Assert.Equal(enabled, move.IsEnabled);
                        }

                        AssertToolbar(keep, keepHint, false, "empty selection");

                        // All four changes occur in the same dispatcher turn, before the pump.
                        vm.Select(kept.Id);
                        vm.Select(normal.Id, control: true);
                        vm.ClearSelection();
                        vm.Select(kept.Id);
                        AssertToolbar(unkeep, unkeep, true, "kept-mixed-clear-kept burst");

                        // A later burst must rearm the update and use the final mixed selection.
                        vm.ClearSelection();
                        vm.Select(kept.Id);
                        vm.Select(normal.Id, control: true);
                        AssertToolbar(keep, keepHint, true, "next mixed burst");
                        vm.ClearSelection();
                        AssertToolbar(keep, keepHint, false, "cleared after a completed burst");

                        window.Hide();
                        Assert.False(window.IsVisible);
                        window.Show();
                        SettleGalleryLayout(window);
                        Assert.True(window.IsVisible);
                        // Change state after Show has settled, so Show's synchronous fit cannot
                        // stand in for the reused window's selection subscription.
                        vm.Select(kept.Id);
                        AssertToolbar(unkeep, unkeep, true, "kept after reopening");
                        vm.Select(normal.Id);
                        AssertToolbar(keep, keepHint, true, "normal after reopening");

                        // Close before another dispatcher turn can process this selection burst.
                        vm.ClearSelection();
                        vm.Select(kept.Id);
                        window.CloseForExit();
                        Assert.True(closed);
                        // A new notification after Closed must not reattach the toolbar. Both
                        // this burst and the aborted earlier burst would change Keep to Unkeep.
                        vm.ClearSelection();
                        vm.Select(kept.Id);
                        // The existing helper pumps before its predicate, including when Closed
                        // was synchronous, so any surviving queued callback is also exercised.
                        PumpLibraryUntil(window, () => closed && !window.IsVisible,
                            language + ": pending selection update followed by final close");
                        Assert.True(closed);
                        Assert.False(window.IsVisible);
                        Assert.Equal(keep, label.Text);
                        Assert.Equal(keep, AutomationProperties.GetName(button));
                        Assert.Equal(keepHint, Assert.IsType<string>(button.ToolTip));
                    }
                    finally
                    {
                        if (!closed) window.CloseForExit();
                    }
                }
            }
            finally
            {
                application.Shutdown();
            }
        });
}
