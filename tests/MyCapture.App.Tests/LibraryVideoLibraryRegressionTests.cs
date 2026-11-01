using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Runs only the affected library methods from the shared isolated visual test class.</summary>
public sealed class LibraryVideoLibraryRegressionTests
{
    [Fact]
    public Task ExistingCardMenuOrderAndPropertiesRemainCorrect() =>
        new SettingsThemePreviewTests().LibraryOverhaul_CardMenuHasTheDecidedGroupsAndIconsAndRefreshesOnEveryOpen();

    [Fact]
    public Task ExistingCardLayoutKeepsTimeFolderAndPinAtLargerTextSizes() =>
        new SettingsThemePreviewTests().Library453_TwoLineCardsFitKoreanEnglishAndLargerFonts();

    [Fact]
    public Task ExistingToolbarFitsItsWidthMatrixWithPdfIncluded() =>
        new SettingsThemePreviewTests().LibraryOverhaul_ToolbarMirrorsTheCardMenuAndFitsOneRowAtEveryWidth();

    [Fact]
    public Task ExistingCardFooterMatrixKeepsRemainingPartsInsideCards() =>
        new SettingsThemePreviewTests().LibraryOverhaul_CardFooterNeverClipsOrOverlapsAtEveryTextSizeDpiThemeAndLanguage();
}
