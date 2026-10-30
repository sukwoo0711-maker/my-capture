using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Keeps the focused library checks selectable by class without running the unrelated
/// settings/media evidence tests in the shared isolated-window fixture class.
/// </summary>
public sealed class GalleryWindowsMenuR2Tests
{
    [Fact]
    public Task SourceActionsShareAGroupAndPropertiesIsLastAlone() =>
        new SettingsThemePreviewTests().LibraryOverhaul_CardMenuHasTheDecidedGroupsAndIconsAndRefreshesOnEveryOpen();

    [Fact]
    public Task BadgeOpeningAndCardInputKeepTheirTargets() =>
        new SettingsThemePreviewTests().LibraryWindowsMenu_BadgeAndMenuOpenSourcesAndKeepFallbackAndCardInput();

    [Fact]
    public Task MenuAndCardEvidence() =>
        new SettingsThemePreviewTests().LibraryWindowsMenu_RendersCardAndMenuWithoutShowingAWindow();
}
