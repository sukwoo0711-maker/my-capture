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
    public void DirectoryChildRejectsJunctionRootsAndAncestorsBeforeAnyOutsideCreation()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string target = ValidatedFilePaths.CreateTemporaryDirectory();
        string sentinel = ValidatedFilePaths.FileWithinRoot(target, "keep.txt", ".txt");
        File.WriteAllText(sentinel, "preserve external target");
        string link = ValidatedFilePaths.DirectoryWithinRoot(owner, "linked");
        UpdatePathsTests.CreateJunction(link, target);

        Assert.Throws<IOException>(() => Directory.CreateDirectory(
            ValidatedFilePaths.DirectoryWithinRoot(owner, Path.Combine(link, "must-not-be-created"))));
        Assert.Throws<IOException>(() => Directory.CreateDirectory(
            ValidatedFilePaths.DirectoryWithinRoot(link, "must-not-be-created")));

        Assert.Equal("preserve external target", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(target));
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
        Assert.Equal(1, HotfixUxSelfTest.Run(Path.Combine(link, "diagnostic")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [WindowsFileSymlinkFact]
    public async Task LinkedSelectedFileAndSettingsFileAreReadableAndTargetsPreserved()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string outside = ValidatedFilePaths.CreateTemporaryDirectory();
        string target = ValidatedFilePaths.FileWithinRoot(outside, "keep.png", ".png");
        await WriteAndReadImage(target);
        byte[] original = File.ReadAllBytes(target);
        string imageLink = Path.Combine(owner, "image.png");
        File.CreateSymbolicLink(imageLink, target);
        Assert.Equal(imageLink, ValidatedFilePaths.SelectedFile(imageLink, ".png"));
        await AssertImageDecodes(imageLink);
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(owner, imageLink, ".png"));
        var paths = AppPaths.CreateForRoot(owner);
        string settings = ValidatedFilePaths.FileWithinRoot(outside, "settings.json", ".json");
        const string json = "{\"schemaVersion\":4,\"general\":{\"isFirstRun\":false},\"capture\":{\"delaySeconds\":17}}";
        File.WriteAllText(settings, json);
        File.CreateSymbolicLink(paths.SettingsFile, settings);
        Assert.Equal(17, new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load().Capture.DelaySeconds);
        Assert.Equal(17, new SettingsStore(paths, NullLogger<SettingsStore>.Instance).ImportFrom(paths.SettingsFile).Capture.DelaySeconds);
        Assert.Equal(json, File.ReadAllText(settings));
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task RedirectedProfileLoadsSettingsAndSelectedImageThroughJunctionAncestor()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string target = ValidatedFilePaths.CreateTemporaryDirectory();
        string link = Path.Combine(owner, "redirected-profile");
        UpdatePathsTests.CreateJunction(link, target);
        string profile = ValidatedFilePaths.CreateDirectory(Path.Combine(target, "MyCapture"));
        string settings = ValidatedFilePaths.FileWithinRoot(profile, "settings.json", ".json");
        File.WriteAllText(settings, "{\"schemaVersion\":4,\"general\":{\"isFirstRun\":false},\"capture\":{\"delaySeconds\":17}}");
        var store = new SettingsStore(AppPaths.CreateForRoot(Path.Combine(link, "MyCapture")), NullLogger<SettingsStore>.Instance);
        Assert.Equal(17, store.Load().Capture.DelaySeconds);
        Assert.Equal(17, store.ImportFrom(Path.Combine(link, "MyCapture", "settings.json")).Capture.DelaySeconds);
        Assert.Empty(store.LastLoadWarnings);
        await WriteAndReadImage(ValidatedFilePaths.FileWithinRoot(target, "image.png", ".png"));
        await AssertImageDecodes(Path.Combine(link, "image.png"));
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("blocked:stream")]
    public void InvalidSettingsPathReturnsDefaultsAndWarningWithoutWriting(string name)
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        var store = new SettingsStore(AppPaths.CreateForRoot(Path.Combine(root, name)), NullLogger<SettingsStore>.Instance);
        AppSettings settings = store.Load();
        Assert.False(settings.General.IsFirstRun);
        Assert.False(store.LastLoadMigratedUnsafeHotkeys);
        Assert.Contains(UiText.Get("Text_2CD093B673E5"), store.LastLoadWarnings);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void RedirectedOsTempRootIsResolvedBeforeStrictFixtureChecks()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string target = ValidatedFilePaths.CreateTemporaryDirectory();
        string link = Path.Combine(owner, "redirected-temp");
        UpdatePathsTests.CreateJunction(link, target);
        string fixture = ValidatedFilePaths.CreateTemporaryDirectory(link);
        Assert.StartsWith(Path.Combine(target, "MyCapture", "Diagnostics") + Path.DirectorySeparatorChar, fixture);
        string report = ValidatedFilePaths.FileWithinRoot(fixture, "report.txt", ".txt");
        File.WriteAllText(report, "isolated");
        Assert.Equal("isolated", File.ReadAllText(report));
    }

    private static Task<bool> WriteAndReadImage(string path) => StaThreadTask.RunAsync(() =>
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        ImageCodec.SavePng(bitmap, path);
        Assert.Equal(1, VideoLayerAssets.ReadImage(path, CancellationToken.None).PixelWidth);
        return true;
    }, "Linked image fixture");

    private static Task<bool> AssertImageDecodes(string path) => StaThreadTask.RunAsync(() =>
    {
        Assert.Equal(1, VideoLayerAssets.ReadImage(path, CancellationToken.None).PixelWidth);
        return true;
    }, "Linked selected image");

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
