using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ValidatedFilePathsTests
{
    [Fact]
    public void DirectoryChildNormalizesContainedTraversalBeforeCreation()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        string child = ValidatedFilePaths.DirectoryWithinRoot(root, @"nested\..\approved\");
        Assert.Equal(Path.Combine(root, "approved"), child);
        Directory.CreateDirectory(child);
        Assert.Equal(child, ValidatedFilePaths.DirectoryWithinRoot(root + Path.DirectorySeparatorChar, child));
        Assert.True(Directory.Exists(child));
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"nested\..\..\outside")]
    [InlineData("C:relative")]
    [InlineData(@"\volume-relative")]
    [InlineData("blocked:stream")]
    [InlineData("blocked.")]
    [InlineData("blocked ")]
    [InlineData("NUL")]
    [InlineData(".")]
    [InlineData(@".\")]
    public void DirectoryChildRejectsEscapesAmbiguityAndRootItselfBeforeCreation(string candidate)
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();

        Assert.Throws<IOException>(() => Directory.CreateDirectory(ValidatedFilePaths.DirectoryWithinRoot(root, candidate)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void DirectoryChildRejectsAbsoluteAndTraversalSiblingPrefixesAndPreservesSentinel()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string root = ValidatedFilePaths.CreateDirectory(Path.Combine(owner, "approved"));
        string sibling = ValidatedFilePaths.CreateDirectory(Path.Combine(owner, "approved-sibling"));
        string sentinel = ValidatedFilePaths.FileWithinRoot(sibling, "keep.txt", ".txt");
        File.WriteAllText(sentinel, "outside remains untouched");

        Assert.Throws<IOException>(() => Directory.CreateDirectory(
            ValidatedFilePaths.DirectoryWithinRoot(root, Path.Combine(sibling, "must-not-be-created"))));
        Assert.Throws<IOException>(() => Directory.CreateDirectory(
            ValidatedFilePaths.DirectoryWithinRoot(root, Path.Combine("..", "approved-sibling", "must-not-be-created"))));
        Assert.Throws<IOException>(() => ValidatedFilePaths.DirectoryWithinRoot(root, sibling));

        Assert.Equal("outside remains untouched", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(sibling));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"nested\..\..\outside")]
    [InlineData("C:relative")]
    [InlineData(@"\volume-relative")]
    [InlineData("blocked:stream")]
    [InlineData("blocked.")]
    [InlineData("NUL")]
    [InlineData(".")]
    public void ValidatedRootChildRejectsTheSameEscapesAndAmbiguity(string candidate)
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        Assert.Throws<IOException>(() => ValidatedFilePaths.DirectoryWithinValidatedRoot(root, candidate));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void ValidatedRootChildAllowsMissingFoldersAndRejectsFilesOnItsPath()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        Assert.Equal(Path.Combine(root, "2026-10", "capture"), ValidatedFilePaths.DirectoryWithinValidatedRoot(root, @"2026-10\capture"));
        File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
        Assert.Throws<IOException>(() => ValidatedFilePaths.DirectoryWithinValidatedRoot(root, "keep.txt"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.DirectoryWithinValidatedRoot(root, @"keep.txt\child"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "keep.txt")));
    }

    [Fact]
    public void DirectoryChildRejectsExistingFileLeaf()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        string file = ValidatedFilePaths.FileWithinRoot(root, "keep.txt", ".txt");
        File.WriteAllText(file, "keep");

        Assert.Throws<IOException>(() => ValidatedFilePaths.DirectoryWithinRoot(root, file));

        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void TemporaryRootsAreDistinctGuidDirectoriesUnderDedicatedOsTempRoot()
    {
        string first = ValidatedFilePaths.CreateTemporaryDirectory();
        string second = ValidatedFilePaths.CreateTemporaryDirectory();
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MyCapture", "Diagnostics"));
        Assert.NotEqual(first, second);
        Assert.Equal(parent, Path.GetDirectoryName(first));
        Assert.True(Guid.TryParseExact(Path.GetFileName(first), "N", out _));
        Assert.True(Directory.Exists(first));
    }

    [Fact]
    public void ExplicitRelativeDirectoryAndContainedDotDotRemainSupported()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        string desired = Path.Combine(root, "selected-output");
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, desired);
        Assert.Equal(desired, ValidatedFilePaths.CreateDirectory(relative));
        string file = ValidatedFilePaths.FileWithinRoot(desired, @"nested\..\report.txt", ".txt");
        Assert.Equal(Path.Combine(desired, "report.txt"), file);
        File.WriteAllText(file, "report");
        Assert.Equal("report", File.ReadAllText(ValidatedFilePaths.FileWithinRoot(desired, file, ".txt")));
    }

    [Theory]
    [InlineData(@"..\outside.png")]
    [InlineData(@"nested\..\..\outside.png")]
    [InlineData("C:relative.png")]
    [InlineData("image.png:stream")]
    [InlineData("image.png.")]
    [InlineData("image.png ")]
    [InlineData("NUL.png")]
    [InlineData("COM1.png")]
    public void RejectsEscapesAndAmbiguousFileNames(string path)
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(root, path, ".png"));
    }

    [Fact]
    public void RejectsAbsoluteOutsideAndSiblingPrefixPaths()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(root, root + "-sibling\\image.png", ".png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(root, Path.Combine(Path.GetDirectoryName(root)!, "image.png"), ".png"));
    }

    [Fact]
    public void SelectedFileRequiresExistingRegularFileAndAllowedExtension()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        string file = ValidatedFilePaths.FileWithinRoot(root, "image.PNG", ".png");
        File.WriteAllText(file, "fixture");
        Assert.Equal(file, ValidatedFilePaths.SelectedFile(file, ".png"));
        Assert.Equal(file, ValidatedFilePaths.SelectedFile(Path.Combine(root, "sub", "..", "image.PNG"), ".png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.SelectedFile("image.PNG", ".png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.SelectedFile(file, ".jpg"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.SelectedFile(Path.Combine(root, "missing.png"), ".png"));
        string folder = ValidatedFilePaths.CreateDirectory(Path.Combine(root, "folder.png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.SelectedFile(folder, ".png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(root, folder, ".png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.FileWithinRoot(root, "payload.exe", ".png"));
        Assert.Throws<IOException>(() => ValidatedFilePaths.CreateDirectory(file));
    }

    [Theory]
    [InlineData(@"\\?\C:\image.png")]
    [InlineData(@"\\.\C:\image.png")]
    public void RejectsDeviceNamespaces(string path) =>
        Assert.Throws<IOException>(() => ValidatedFilePaths.SelectedFile(path, ".png"));

    [Theory]
    [InlineData(@"..\settings.json")]
    [InlineData(@"nested\..\..\settings.json")]
    [InlineData("settings.exe")]
    [InlineData("settings.json:stream")]
    [InlineData("C:settings.json")]
    public void SettingsPathsStillRejectEscapesAndInvalidExtensions(string path)
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        Assert.Throws<IOException>(() => ValidatedFilePaths.SettingsFileWithinRoot(root, path));
        Assert.Throws<IOException>(() => ValidatedFilePaths.SettingsFileWithinRoot(root, root + "-sibling\\settings.json"));
    }

    [Fact]
    public void SettingsPathsAllowNormalizedContainedTraversalAndWindowsCasing()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        string file = Path.Combine(root, "settings.JSON");
        Assert.Equal(file, ValidatedFilePaths.SettingsFileWithinRoot(root, @"nested\..\settings.JSON"));
        if (OperatingSystem.IsWindows())
            Assert.Equal(file.ToUpperInvariant(), ValidatedFilePaths.SettingsFileWithinRoot(root, file.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Archive, true)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00400000, true)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Directory, false)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Device, false)]
    public void SelectedFileTypeAllowsCloudPlaceholderAttributesButRejectsDirectoriesAndDevices(FileAttributes attributes, bool allowed) =>
        Assert.Equal(allowed, ValidatedFilePaths.IsSelectedFileType(attributes));
}
