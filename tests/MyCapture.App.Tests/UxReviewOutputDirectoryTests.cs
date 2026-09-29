using System.IO;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UxReviewOutputDirectoryTests
{
    [Fact]
    public void Create_AllocatesDistinctExistingEmptyDirectories()
    {
        DirectoryInfo first = TestRecycleBin.TrackNewDirectory(UxReviewOutputDirectory.Create);
        DirectoryInfo second = TestRecycleBin.TrackNewDirectory(UxReviewOutputDirectory.Create);
        try
        {
            Assert.NotEqual(first.FullName, second.FullName);
            Assert.StartsWith("MyCapture-ux-review-", first.Name);
            Assert.StartsWith("MyCapture-ux-review-", second.Name);
            Assert.True(first.Exists);
            Assert.True(second.Exists);
            Assert.Empty(first.GetFileSystemInfos());
            Assert.Empty(second.GetFileSystemInfos());
        }
        finally
        {
            TestRecycleBin.DeleteDirectory(first.FullName, recursive: true);
            TestRecycleBin.DeleteDirectory(second.FullName, recursive: true);
        }
    }

    [Fact]
    public void GeneratedDirectory_CleanupDoesNotAffectAnotherRun()
    {
        DirectoryInfo first = TestRecycleBin.TrackNewDirectory(UxReviewOutputDirectory.Create);
        DirectoryInfo second = TestRecycleBin.TrackNewDirectory(UxReviewOutputDirectory.Create);
        try
        {
            string evidence = Path.Combine(second.FullName, "evidence.txt");
            File.WriteAllText(evidence, "independent review evidence");
            TestRecycleBin.DeleteDirectory(first.FullName);
            Assert.True(second.Exists);
            Assert.Equal("independent review evidence", File.ReadAllText(evidence));
        }
        finally
        {
            first.Refresh();
            if (first.Exists) TestRecycleBin.DeleteDirectory(first.FullName, recursive: true);
            TestRecycleBin.DeleteDirectory(second.FullName, recursive: true);
        }
    }
}
