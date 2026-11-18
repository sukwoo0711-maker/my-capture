using System.IO;
using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeFeelSafetyTests
{
    [Fact]
    public void NativeFeel_BackdropCannotAcquireProductChromeOrEntranceFromThemeResources() => StaTestHost.Run(() =>
    {
        var bounds = new RectD(0, 0, 1920, 1080);
        Window window = NativeFeelDesktopCapture.CreateBackdrop(new MonitorInfo("fixture", bounds, bounds, 144, true));
        try
        {
            window.Resources = SystemHighContrastThemeTests.LoadTheme();
            window.ApplyTemplate();
            Assert.Null(window.ReadLocalValue(FrameworkElement.StyleProperty));
            Assert.Null(window.Style);
            Assert.False(ModernWindowChrome.GetEnabled(window));
            Assert.False(FluidMotion.GetWindowEntrance(window));
            Assert.False(window.AllowsTransparency);
            Assert.Equal(1, window.Opacity);
            Assert.Equal(Color.FromRgb(128, 128, 128), Assert.IsType<SolidColorBrush>(window.Background).Color);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal(1280, window.Width);
            Assert.Equal(720, window.Height);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NativeFeel_OutputRejectsAnExistingDirectoryWithoutChangingItsContent()
    {
        string root = OwnedTestDirectory.Create("mc-native-feel-path-");
        string marker = TestDiagnosticArtifacts.Confined(Path.Combine(root, "keep.txt"));
        File.WriteAllText(marker, "preserve this fixture");
        Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(root));
        Assert.Equal("preserve this fixture", File.ReadAllText(marker));
        Assert.Single(Directory.GetFiles(root));
        // Retained in the isolated test temp root; no cleanup API or shell is invoked.
    }

    [Fact]
    public void NativeFeel_OutputRejectsAncestorTraversalBeforeCreatingTheDirectory()
    {
        string root = OwnedTestDirectory.Create("mc-native-feel-path-");
        string outside = Path.GetFullPath(Path.Combine(root, "..", "native-feel-escape-" + Guid.NewGuid().ToString("N")));
        Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(outside, root));
        Assert.False(Directory.Exists(outside));
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1080, -1900, 20, 500, 500, true)]
    [InlineData(-1920, 0, 1920, 1080, -1940, 20, 500, 500, false)]
    [InlineData(0, 0, 1920, 1080, 1900, 20, 40, 40, false)]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1080, true)]
    [InlineData(0, 0, 1920, 1080, 0, 0, 0, 20, false)]
    public void NativeFeel_ShadowCropCannotEscapeNeutralBackdrop(double x, double y, double w, double h,
        double cropX, double cropY, double cropW, double cropH, bool expected) =>
        Assert.Equal(expected, NativeFeelDesktopCapture.Contains(new RectD(x, y, w, h), new RectD(cropX, cropY, cropW, cropH)));

    [Theory]
    [InlineData(-100, -100, 150, 150, true)]
    [InlineData(100, 0, 30, 30, false)]
    [InlineData(99, 99, 1, 1, true)]
    [InlineData(-30, 0, 30, 30, false)]
    public void NativeFeel_ForeignWindowOverlapIncludesEvenOnePixel(double x, double y, double w, double h, bool expected) =>
        Assert.Equal(expected, NativeFeelDesktopCapture.Intersects(new RectD(0, 0, 100, 100), new RectD(x, y, w, h)));

    [Fact]
    public void NativeFeel_EveryRequestedCaseHasAnUnambiguousManifestSurface()
    {
        Assert.Equal(48, NativeFeelCoverage.BaselineRequested.Length);
        Assert.Equal(48, NativeFeelCoverage.BaselineRequested.Distinct(StringComparer.Ordinal).Count());
        Assert.All(NativeFeelCoverage.BaselineRequested, id => Assert.Contains(id, NativeFeelCoverage.Requested));
        Assert.Equal(NativeFeelCoverage.Requested.Length, NativeFeelCoverage.Requested.Distinct(StringComparer.Ordinal).Count());
        Assert.All(NativeFeelCoverage.Requested, id => Assert.False(string.IsNullOrWhiteSpace(NativeFeelCoverage.SurfaceOf(id))));
        Assert.Throws<ArgumentException>(() => NativeFeelCoverage.SurfaceOf("unknown-case"));
    }

    [Fact]
    public void NativeFeel_AreaDiscoveryNeedsNoSharedRegistration()
    {
        NativeFeelSceneGroup group = Assert.Single(NativeFeelSceneRegistry.Discover(typeof(AdditionalArea)));
        Assert.Equal("phase2-editor-inspector", Assert.Single(group.Scenes).Id);
        Assert.Equal("image-editor", Assert.Single(group.Scenes).Surface);
        Assert.False(group.Baseline);
    }

    [Fact]
    public void NativeFeel_DuplicateSceneIdsFailBeforeCapture() =>
        Assert.Throws<InvalidOperationException>(() => NativeFeelSceneRegistry.Discover(typeof(DuplicateArea)));

    [Fact]
    public void NativeFeel_InvalidCaptureSignaturesFailBeforeCapture() =>
        Assert.Throws<InvalidOperationException>(() => NativeFeelSceneRegistry.Discover(typeof(InvalidArea)));

    [Theory]
    [InlineData("100", 1)]
    [InlineData("150", 1.5)]
    public void NativeFeel_RequestedScaleIsAnExplicitNativePrerequisite(string value, double expected) =>
        Assert.Equal(expected, NativeFeelCoverage.RequestedScale(value));

    [Theory]
    [InlineData("125")]
    [InlineData("1.5")]
    [InlineData("150%")]
    public void NativeFeel_InvalidScaleCannotLabelDesktopPixelsAsAnotherScale(string value) =>
        Assert.Throws<ArgumentException>(() => NativeFeelCoverage.RequestedScale(value));

    [Fact]
    public void NativeFeel_FoundationMatrixKeepsUncapturedScalesAndThemesVisible()
    {
        NativeFeelCase[] captured = [new("settings-general", "settings-window", "settings-general", "dark", 1, "captured", "fixture")];
        IReadOnlyList<NativeFeelMatrixCase> matrix = NativeFeelCoverage.FoundationMatrix(captured);
        Assert.Equal(16, matrix.Count);
        NativeFeelMatrixCase one = Assert.Single(matrix, item => item.Status == "captured");
        Assert.Equal("settings-general", one.Id);
        Assert.Equal("dark", one.Theme);
        Assert.Equal(1, one.Scale);
        Assert.Equal(15, matrix.Count(item => item.Status == "not-run"));
        Assert.All(matrix.Where(item => item.Scale == 1.5), item => Assert.Equal("not-run", item.Status));
    }

    private static class AdditionalArea
    {
        [NativeFeelSceneGroup(200)]
        [NativeFeelScene("phase2-editor-inspector", "image-editor")]
        private static void Capture(NativeFeelSceneContext context) { }
    }

    private static class DuplicateArea
    {
        [NativeFeelSceneGroup(0)]
        [NativeFeelScene("duplicate", "image-editor")]
        private static void First(NativeFeelSceneContext context) { }
        [NativeFeelSceneGroup(1)]
        [NativeFeelScene("duplicate", "settings-window")]
        private static void Second(NativeFeelSceneContext context) { }
    }

    private static class InvalidArea
    {
        [NativeFeelSceneGroup(0)]
        [NativeFeelScene("invalid", "image-editor")]
        private static void Capture() { }
    }
}
