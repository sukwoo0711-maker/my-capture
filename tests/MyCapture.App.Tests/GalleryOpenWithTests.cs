using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryOpenWithTests
{
    [Fact]
    public Task LazyQuerySelectionAndKeyboard() => new SettingsThemePreviewTests()
        .LibraryOpenWith_IsLazyForParentAndDisabledForMultipleSelectionForBothMediaKinds();
    [Fact]
    public Task ExportCopyAndLaunchFailure() => new SettingsThemePreviewTests()
        .LibraryOpenWith_CloseBeforeClickPassesCopyAndLaunchFailureUsesExistingErrorLine();
    [Fact]
    public Task SyntheticMenuEvidence() => new SettingsThemePreviewTests()
        .LibraryOpenWith_RendersLibraryAndCardMenusWithSyntheticProgramsAt100Percent();
}
