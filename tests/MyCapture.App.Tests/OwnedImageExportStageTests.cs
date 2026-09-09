using System.IO;
using MyCapture.App.Editing;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OwnedImageExportStageTests
{
    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    public void OwnedStages_AreIsolatedAndDisposeOnlyTheirOwnFile(string extension)
    {
        var result = new ImageExportResult([1, 2, 3], extension, 3, 0, null, false);
        using OwnedImageExportStage first = OwnedImageExportStage.Create(result);
        using OwnedImageExportStage second = OwnedImageExportStage.Create(result);
        Assert.NotEqual(Path.GetDirectoryName(first.FilePath), Path.GetDirectoryName(second.FilePath));
        Assert.StartsWith("MyCapture-reduced-export-", new DirectoryInfo(Path.GetDirectoryName(first.FilePath)!).Name);
        Assert.Equal(extension, Path.GetExtension(first.FilePath));
        Assert.Equal(result.Bytes, File.ReadAllBytes(first.FilePath));
        first.Dispose();
        Assert.False(File.Exists(first.FilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(first.FilePath)));
        Assert.Equal(result.Bytes, File.ReadAllBytes(second.FilePath));
    }

    [Theory]
    [InlineData(".png/../../outside")]
    [InlineData(".png\\..\\outside")]
    [InlineData(".png:stream")]
    [InlineData(".png ")]
    [InlineData(".exe")]
    public void Create_RejectsPathLikeOrUnsupportedExtensions(string extension)
    {
        var result = new ImageExportResult([1], extension, 1, 0, null, false);
        Assert.Throws<ArgumentException>(() => OwnedImageExportStage.Create(result));
    }

    [Fact]
    public void RetainedStage_ExpiresAfterTwoDaysUsingSuppliedClock()
    {
        DateTimeOffset created = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = new ImageExportResult([1, 2, 3], ".png", 3, 0, null, false);
        using OwnedImageExportStage stage = OwnedImageExportStage.Create(result, created);
        stage.RetainForShell();
        OwnedImageExportStage.CleanupRetained(created.AddDays(1));
        Assert.True(File.Exists(stage.FilePath));
        OwnedImageExportStage.CleanupRetained(created.AddDays(3));
        Assert.False(File.Exists(stage.FilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(stage.FilePath)));
    }
}
