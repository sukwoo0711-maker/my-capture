using System.IO;
using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UpdateSessionCleanupTests
{
    [Fact]
    public void ScannerAcceptsOnlyExactKnownSessionFiles()
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        try
        {
            string session = CreateSession(root);
            foreach (string file in new[] { "SHA256SUMS.txt", "update-session.json", "install-result.json", "update.log" })
                File.WriteAllText(Path.Combine(session, file), "fixture");
            Assert.Equal(session, Assert.Single(UpdateSessionCleanup.FindStaleSessions(root)));
            Assert.Equal(5, Directory.GetFiles(session).Length);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void CleanupDeletesExactKnownFilesAndTheEmptySession()
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        try
        {
            string session = CreateSession(root);
            foreach (string file in new[] { "SHA256SUMS.txt", "update-session.json", "install-result.json", "update.log" })
                File.WriteAllText(Path.Combine(session, file), "fixture");
            string sibling = Path.Combine(root, "unrelated.txt");
            File.WriteAllText(sibling, "keep");

            Assert.Equal(1, UpdateSessionCleanup.Cleanup(root));

            Assert.False(Directory.Exists(session));
            Assert.Equal("keep", File.ReadAllText(sibling));
            Assert.Empty(UpdateSessionCleanup.FindStaleSessions(root));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("update-helper.ps1")]
    [InlineData("MyCapture-9.9.9-win-x64-setup.exe")]
    public void UnknownFilesPreserveTheEntireSession(string name)
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        try
        {
            string session = CreateSession(root);
            File.WriteAllText(Path.Combine(session, name), "keep");
            Assert.Empty(UpdateSessionCleanup.FindStaleSessions(root));
            Assert.Equal(0, UpdateSessionCleanup.Cleanup(root));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(session, name)));
            Assert.Equal(2, Directory.GetFiles(session).Length);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void NestedContentIsNotTraversedOrRemoved()
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        try
        {
            string session = CreateSession(root);
            string nested = Path.Combine(session, "update.log");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "keep.txt"), "keep");
            Assert.Empty(UpdateSessionCleanup.FindStaleSessions(root));
            Assert.Equal(0, UpdateSessionCleanup.Cleanup(root));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(nested, "keep.txt")));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void LockedInstallerKeepsAllSessionFiles()
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        try
        {
            string session = CreateSession(root);
            string installer = Path.Combine(session, "MyCapture-1.8.0-win-x64-setup.exe");
            File.WriteAllText(Path.Combine(session, "update.log"), "keep");
            using (var locked = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Empty(UpdateSessionCleanup.FindStaleSessions(root));
                Assert.Equal(0, UpdateSessionCleanup.Cleanup(root));
                Assert.Equal(2, Directory.GetFiles(session).Length);
            }
            Assert.Equal(session, Assert.Single(UpdateSessionCleanup.FindStaleSessions(root)));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData("update-1.8.0-short")]
    [InlineData("update-1.8.0-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("update-01.8.0-0123456789abcdef0123456789abcdef")]
    [InlineData("update-1.8.0-preview-0123456789abcdef0123456789abcdef")]
    [InlineData("other-1.8.0-0123456789abcdef0123456789abcdef")]
    public void UnrecognizedSessionNamesArePreserved(string name)
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            Assert.Empty(UpdateSessionCleanup.FindStaleSessions(root));
            Assert.Equal(0, UpdateSessionCleanup.Cleanup(root));
            Assert.True(Directory.Exists(Path.Combine(root, name)));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void SessionJunctionAndItsTargetArePreserved()
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        string target = OwnedTestDirectory.Create("mycapture-session-cleanup-target-");
        string link = Path.Combine(root, UpdatePaths.SessionName(new UpdateVersion(1, 8, 0)));
        try
        {
            File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
            UpdatePathsTests.CreateJunction(link, target);
            Assert.Empty(UpdateSessionCleanup.FindStaleSessions(root));
            Assert.Equal(0, UpdateSessionCleanup.Cleanup(root));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            if (Directory.Exists(link)) TestRecycleBin.DeleteDirectory(link);
            OwnedTestDirectory.Delete(root);
            OwnedTestDirectory.Delete(target);
        }
    }

    [Fact]
    public void EmptyRecognizedSessionCanBeCleanedUp()
    {
        string root = OwnedTestDirectory.Create("mycapture-session-cleanup-");
        try
        {
            string session = Path.Combine(root, UpdatePaths.SessionName(new UpdateVersion(1, 8, 0)));
            Directory.CreateDirectory(session);
            Assert.Equal(session, Assert.Single(UpdateSessionCleanup.FindStaleSessions(root)));
            Assert.Equal(1, UpdateSessionCleanup.Cleanup(root));
            Assert.False(Directory.Exists(session));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    private static string CreateSession(string root)
    {
        string session = Path.Combine(root, UpdatePaths.SessionName(new UpdateVersion(1, 8, 0)));
        Directory.CreateDirectory(session);
        File.WriteAllBytes(Path.Combine(session, "MyCapture-1.8.0-win-x64-setup.exe"), [1, 2, 3]);
        return session;
    }
}
