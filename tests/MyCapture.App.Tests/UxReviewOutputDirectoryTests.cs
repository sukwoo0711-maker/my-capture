using System.IO;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UxReviewOutputDirectoryTests
{
    [Fact]
    public void Create_AllocatesDistinctExistingEmptyDirectories()
    {
        DirectoryInfo first = UxReviewOutputDirectory.Create();
        DirectoryInfo second = UxReviewOutputDirectory.Create();
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
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void GeneratedDirectory_CleanupDoesNotAffectAnotherRun()
    {
        DirectoryInfo first = UxReviewOutputDirectory.Create();
        DirectoryInfo second = UxReviewOutputDirectory.Create();
        try
        {
            string evidence = Path.Combine(second.FullName, "evidence.txt");
            File.WriteAllText(evidence, "independent review evidence");
            first.Delete();
            Assert.True(second.Exists);
            Assert.Equal("independent review evidence", File.ReadAllText(evidence));
        }
        finally
        {
            first.Refresh();
            if (first.Exists) first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }
}
