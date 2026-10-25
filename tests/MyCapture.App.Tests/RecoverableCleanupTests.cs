using System.IO;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecoverableCleanupTests
{
    [Fact]
    public void FileAndDirectoryMovesHaveRestorableBytesAndIdentity()
    {
        string root = OwnedTestDirectory.Create("mc-cleanup-roundtrip-");
        var trash = new RecoverableFileRemoval();
        string file = Path.Combine(root, "sentinel.txt");
        File.WriteAllText(file, "recoverable bytes");
        CleanupReceipt receipt = trash.RecycleFile(file);
        Assert.False(File.Exists(file));
        Assert.Equal("recoverable bytes", File.ReadAllText(receipt.RecycledPath));
        trash.RestoreFile(receipt);
        Assert.Equal(receipt.Identity, trash.GetIdentity(file));
        Assert.Equal("recoverable bytes", File.ReadAllText(file));
        CleanupReceipt directory = trash.RecycleDirectory(root);
        Assert.False(Directory.Exists(root));
        trash.RestoreDirectory(directory);
        Assert.Equal("recoverable bytes", File.ReadAllText(file));
        OwnedTestDirectory.Delete(root);
    }

    [Fact]
    public void LongPathFileMoveAndRestorePreserveIdentityAndBytes()
    {
        string root = OwnedTestDirectory.Create("mc-cleanup-long-path-");
        try
        {
            string parent = Path.Combine(root, new string('a', 90), new string('b', 90), new string('c', 90));
            Directory.CreateDirectory(parent);
            string file = Path.Combine(parent, "sentinel.txt");
            Assert.True(file.Length > 320);
            File.WriteAllText(file, "long path bytes");
            var trash = new RecoverableFileRemoval();
            CleanupReceipt receipt = trash.RecycleFile(file);
            Assert.False(File.Exists(file));
            Assert.Equal("long path bytes", File.ReadAllText(receipt.RecycledPath));
            trash.RestoreFile(receipt);
            Assert.Equal(receipt.Identity, trash.GetIdentity(file));
            Assert.Equal("long path bytes", File.ReadAllText(file));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void JunctionMoveAndRestoreLeaveItsTargetUntouched()
    {
        string root = OwnedTestDirectory.Create("mc-cleanup-junction-");
        string target = OwnedTestDirectory.Create("mc-cleanup-target-");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep target");
        string link = Path.Combine(root, "link");
        UpdatePathsTests.CreateJunction(link, target);
        var trash = new RecoverableFileRemoval();
        CleanupReceipt receipt = trash.RecycleDirectory(link);
        Assert.False(Directory.Exists(link));
        Assert.Equal("keep target", File.ReadAllText(Path.Combine(target, "keep.txt")));
        trash.RestoreDirectory(receipt);
        Assert.NotEqual((FileAttributes)0, File.GetAttributes(link) & FileAttributes.ReparsePoint);
        Assert.Equal("keep target", File.ReadAllText(Path.Combine(link, "keep.txt")));
        TestRecycleBin.DeleteDirectory(link);
        OwnedTestDirectory.Delete(root);
        OwnedTestDirectory.Delete(target);
    }

    [Fact]
    public void LockedFileIsPreservedAndNonrecursiveRemovalRefusesChildren()
    {
        string root = OwnedTestDirectory.Create("mc-cleanup-lock-");
        string file = Path.Combine(root, "locked.txt");
        File.WriteAllText(file, "keep locked bytes");
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => RecoverableFileRemoval.DeleteFile(file));
        Assert.Equal("keep locked bytes", File.ReadAllText(file));
        Assert.Throws<IOException>(() => RecoverableFileRemoval.DeleteDirectory(root));
        Assert.True(File.Exists(file));
        OwnedTestDirectory.Delete(root);
    }
}
