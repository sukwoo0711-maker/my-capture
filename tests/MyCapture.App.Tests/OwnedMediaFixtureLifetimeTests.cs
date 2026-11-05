using System.IO;
using System.Windows.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OwnedMediaFixtureLifetimeTests
{
    [Fact]
    public void WaitsForTheMediaHandleToCloseBeforeRecyclingTheFixture() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-media-release-");
        string path = Path.Combine(root, "source.mp4");
        FileStream? holder = null;
        var release = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        bool released = false;
        try
        {
            File.WriteAllText(path, "owned media bytes");
            holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            IOException busy = Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose());
            Assert.Equal(unchecked((int)0x80070020), busy.HResult);
            release.Tick += (_, _) =>
            {
                release.Stop();
                holder.Dispose();
                released = true;
            };
            release.Start();

            OwnedTestDirectory.WaitForMediaFilesReleased(root);

            Assert.True(released, "The dispatcher must finish closing the media handle before fixture cleanup.");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            Assert.Equal("owned media bytes", File.ReadAllText(path));
        }
        finally
        {
            release.Stop();
            holder?.Dispose();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void AHandleThatStaysOpenPreventsCleanupAndPreservesTheFixture() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-media-release-timeout-");
        string path = Path.Combine(root, "source.mp4");
        FileStream? holder = null;
        try
        {
            File.WriteAllText(path, "preserve held media bytes");
            holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool cleanupReached = false;

            TimeoutException timeout = Assert.Throws<TimeoutException>(() =>
            {
                OwnedTestDirectory.WaitForMediaFilesReleased(root);
                cleanupReached = true;
            });

            Assert.False(cleanupReached);
            Assert.Equal(unchecked((int)0x80070020), Assert.IsType<IOException>(timeout.InnerException).HResult);
            Assert.True(Directory.Exists(root));
            Assert.Equal("preserve held media bytes", File.ReadAllText(path));
        }
        finally
        {
            holder?.Dispose();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void RecyclesTheFixtureOnceAFileUnderItCloses() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-held-recycle-");
        string path = Path.Combine(root, "frames", "frame.png");
        FileStream? holder = null;
        var release = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        bool released = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "owned frame bytes");
            // Even a handle that shares delete access stops the Shell moving the folder, as on CI.
            holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            IOException held = Assert.Throws<IOException>(() => TestRecycleBin.DeleteDirectory(root, recursive: true));
            Assert.Equal(unchecked((int)0x80270021), held.InnerException?.HResult);
            release.Tick += (_, _) =>
            {
                release.Stop();
                holder.Dispose();
                released = true;
            };
            release.Start();

            OwnedTestDirectory.Delete(root);

            Assert.True(released, "Cleanup must keep the dispatcher running until the file closes.");
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            release.Stop();
            holder?.Dispose();
            if (Directory.Exists(root)) OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void AFileThatStaysOpenStopsTheRecycleAndNamesIt() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-held-recycle-timeout-");
        string path = Path.Combine(root, "source.mp4");
        FileStream? holder = null;
        try
        {
            File.WriteAllText(path, "preserve held media bytes");
            holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            IOException failure = Assert.Throws<IOException>(() => OwnedTestDirectory.Delete(root, TimeSpan.FromMilliseconds(200)));

            Assert.Contains("source.mp4 (open by", failure.Message);
            // A holder without delete sharing gets the Shell's other in-use code.
            Assert.Equal(unchecked((int)0x80270028), failure.InnerException?.InnerException?.HResult);
            Assert.Equal("preserve held media bytes", File.ReadAllText(path));
        }
        finally
        {
            holder?.Dispose();
            OwnedTestDirectory.Delete(root);
        }
    });
}
