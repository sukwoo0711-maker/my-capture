using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ValidatedFilePathsWindowsTests
{
    [Fact]
    public async Task SelectedImageStillDecodesAndMarkedSizeErrorsKeepTheirMessage()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        string image = ValidatedFilePaths.FileWithinRoot(root, "selected.png", ".png");
        await StaThreadTask.RunAsync(() =>
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            ImageCodec.SavePng(bitmap, image);
            Assert.Equal(1, VideoLayerAssets.ReadImage(image, CancellationToken.None).PixelWidth);
            return true;
        }, "Selected image path validation");
        string large = ValidatedFilePaths.FileWithinRoot(root, "large.png", ".png");
        using (var file = new FileStream(large, FileMode.CreateNew, FileAccess.Write)) file.SetLength(25 * 1024 * 1024);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => VideoLayerAssets.ReadImage(large, CancellationToken.None));
        Assert.Equal(UiText.Get("Text_9C4458907087"), UserFacingError.GetMessage(error));
    }

    [Fact]
    public void JunctionAncestorsAndOutputRootsAreRejectedBeforeAnyOutsideWrites()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string outside = ValidatedFilePaths.CreateTemporaryDirectory();
        string link = Path.Combine(owner, "linked");
        UpdatePathsTests.CreateJunction(link, outside);
        Assert.Throws<IOException>(() => ValidatedFilePaths.CreateDirectory(Path.Combine(link, "new-output")));
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(owner, Path.Combine(link, "report.txt"), ".txt"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.SelectedFile(Path.Combine(link, "image.png"), ".png"));
        var store = new SettingsStore(AppPaths.CreateForRoot(link), NullLogger<SettingsStore>.Instance);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Equal(UiText.Get("Text_2CD093B673E5"), error.Message);
        Assert.Equal(1, HotfixUxSelfTest.Run(Path.Combine(link, "diagnostic")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [WindowsFileSymlinkFact]
    public void LinkedSelectedFileAndSettingsFileAreRejectedAndTargetPreserved()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string outside = ValidatedFilePaths.CreateTemporaryDirectory();
        string target = ValidatedFilePaths.FileWithinRoot(outside, "keep.png", ".png");
        File.WriteAllText(target, "unrelated");
        string imageLink = Path.Combine(owner, "image.png");
        File.CreateSymbolicLink(imageLink, target);
        Assert.Throws<IOException>(() => ValidatedFilePaths.SelectedFile(imageLink, ".png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(owner, imageLink, ".png"));
        var paths = AppPaths.CreateForRoot(owner);
        File.CreateSymbolicLink(paths.SettingsFile, target);
        Assert.Throws<InvalidDataException>(() => new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load());
        Assert.Equal("unrelated", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("missing.png")]
    [InlineData("blocked.exe")]
    [InlineData("directory.png")]
    public void InvalidImagePathsUseExistingLocalizedUserMessage(string name)
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        string path = Path.Combine(root, name);
        if (name == "blocked.exe") File.WriteAllText(ValidatedFilePaths.FileWithinRoot(root, name), "invalid");
        if (name == "directory.png") ValidatedFilePaths.CreateDirectory(path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => VideoLayerAssets.ReadImage(path, CancellationToken.None));
        Assert.Equal(UiText.Get("Video.InvalidImage"), UserFacingError.GetMessage(error));
    }
}
