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
}
