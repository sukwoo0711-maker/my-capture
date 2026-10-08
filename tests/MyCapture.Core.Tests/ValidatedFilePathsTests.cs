using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ValidatedFilePathsTests
{
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
}
