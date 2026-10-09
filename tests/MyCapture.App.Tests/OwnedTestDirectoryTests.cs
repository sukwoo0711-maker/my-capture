using System.IO;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OwnedTestDirectoryTests
{
    [Fact]
    public void CleanupRejectsUnownedSiblingAndPreservesItsFile()
    {
        string root = OwnedTestDirectory.Create("mycapture-paths-");
        string sibling = OwnedTestDirectory.Create("mycapture-preserve-");
        try
        {
            string nested = Path.Combine(sibling, "unowned");
            Directory.CreateDirectory(nested);
            string file = Path.Combine(nested, "keep.txt");
            File.WriteAllText(file, "keep");
            Assert.Throws<IOException>(() => OwnedTestDirectory.Delete(nested));
            OwnedTestDirectory.Delete(root);
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { OwnedTestDirectory.Delete(sibling); }
    }

    [Fact]
    public void CleanupRefusesToTraverseAJunctionAndPreservesItsTarget()
    {
        string root = OwnedTestDirectory.Create("mycapture-paths-");
        string target = OwnedTestDirectory.Create("mycapture-link-target-");
        string link = Path.Combine(root, "linked");
        try
        {
            string file = Path.Combine(target, "keep.txt");
            File.WriteAllText(file, "keep");
            TestJunction.Create(link, target);
            Assert.Throws<IOException>(() => OwnedTestDirectory.Delete(root));
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(link)) TestRecycleBin.DeleteDirectory(link);
            OwnedTestDirectory.Delete(root);
            OwnedTestDirectory.Delete(target);
        }
    }
}
