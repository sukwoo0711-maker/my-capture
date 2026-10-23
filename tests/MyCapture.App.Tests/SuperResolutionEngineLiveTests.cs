using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

// The retired engine's live tests are replaced by catalog compatibility checks. No real
// profile or model weights are opened; the legacy source filename is retained deliberately.
public sealed class SuperResolutionRetirementTests
{
    [Fact]
    public void LegacyCleanupNames_AreTheThreePreviouslyInstalledFiles()
    {
        Assert.Equal(new[] { "realesrgan_x4.onnx", "realesrgan_x4.onnx.data", "real_esrgan_x4plus.data" },
            OcrModelCatalog.LegacySuperResolutionFiles);
        Assert.All(OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional), file =>
            Assert.DoesNotContain(file.FileName, OcrModelCatalog.LegacySuperResolutionFiles));
    }

    [Fact]
    public void LegacyQuality_UsesTheSameDownloadSizeAsAccurate()
    {
        Assert.Equal(101_654_967, OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate));
        Assert.Equal(102, OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Accurate));
        Assert.Equal(OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate),
            OcrModelCatalog.DownloadBytesFor(OcrQuality.Enhanced));
        Assert.Equal(102, OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Enhanced));
        Assert.Equal(0, OcrModelCatalog.DownloadBytesFor(OcrQuality.Fast));
        Assert.Equal(0, OcrModelCatalog.DownloadBytesFor(OcrQuality.Balanced));
    }
}
