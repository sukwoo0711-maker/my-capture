using System.Diagnostics;
using System.Text;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecoverableMetadataReadinessTests
{
    [Fact]
    public async Task ExactMetadataSharingLock_ReleasedAfterFirstReadFailure_AllowsVerifiedRead()
    {
        using var fixture = new MetadataFixture();
        using var locked = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        Task holder = Task.Factory.StartNew(() =>
        {
            using var stream = new FileStream(fixture.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.None);
            locked.Set();
            releaseLock.Wait();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(locked.Wait(TimeSpan.FromSeconds(5)));
            int retries = 0;
            var removal = new RecoverableFileRemoval
            {
                DiagnosticStageForTest = stage =>
                {
                    if (stage != "recycle:metadata-sharing-retry") return;
                    retries++;
                    releaseLock.Set();
                },
            };

            Assert.Equal(fixture.OriginalPath, removal.ReadDeletedOriginalPathWhenReady(fixture.RecycledPath));
            Assert.True(retries > 0);
        }
        finally
        {
            releaseLock.Set();
            await holder;
        }
        fixture.AssertUnchanged();
    }

    [Fact]
    public void ExactMetadataSharingLock_HeldBeyondReadinessBudget_FailsAndPreservesFixture()
    {
        using var fixture = new MetadataFixture();
        int retries = 0;
        var removal = new RecoverableFileRemoval
        {
            DiagnosticStageForTest = stage =>
            {
                if (stage == "recycle:metadata-sharing-retry") retries++;
            },
        };
        using (var stream = new FileStream(fixture.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var elapsed = Stopwatch.StartNew();
            IOException error = Assert.Throws<IOException>(() => removal.ReadDeletedOriginalPathWhenReady(fixture.RecycledPath));
            Assert.Equal(unchecked((int)0x80070020), error.HResult);
            Assert.True(retries > 0);
            Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(3));
        }
        fixture.AssertUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataMissingOrMalformed_IsNotRetried(bool malformed)
    {
        using var fixture = new MetadataFixture(writeMetadata: malformed, metadataVersion: 3);
        int retries = 0;
        var removal = new RecoverableFileRemoval
        {
            DiagnosticStageForTest = stage =>
            {
                if (stage == "recycle:metadata-sharing-retry") retries++;
            },
        };

        IOException error = Assert.ThrowsAny<IOException>(() => removal.ReadDeletedOriginalPathWhenReady(fixture.RecycledPath));
        Assert.NotEqual(unchecked((int)0x80070020), error.HResult);
        Assert.Equal(0, retries);
        fixture.AssertUnchanged();
    }

    private sealed class MetadataFixture : IDisposable
    {
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mc-recycle-metadata-").FullName;
        private readonly byte[] _payload = [1, 2, 3, 128, 255];
        private readonly byte[]? _metadata;

        internal MetadataFixture(bool writeMetadata = true, long metadataVersion = 2)
        {
            // These are ordinary files in an owned test directory, never system Recycle Bin entries.
            RecycledPath = Path.Combine(_root, "$Rfixture.bin");
            MetadataPath = Path.Combine(_root, "$Ifixture.bin");
            OriginalPath = Path.Combine(_root, "original.bin");
            File.WriteAllBytes(RecycledPath, _payload);
            if (writeMetadata)
            {
                using (var writer = new BinaryWriter(new FileStream(MetadataPath, FileMode.CreateNew, FileAccess.Write), Encoding.Unicode))
                {
                    writer.Write(metadataVersion);
                    writer.Write((long)_payload.Length);
                    writer.Write(DateTime.UtcNow.ToFileTimeUtc());
                    string original = OriginalPath + '\0';
                    writer.Write(original.Length);
                    writer.Write(original.ToCharArray());
                }
                _metadata = File.ReadAllBytes(MetadataPath);
            }
        }

        internal string RecycledPath { get; }
        internal string MetadataPath { get; }
        internal string OriginalPath { get; }

        internal void AssertUnchanged()
        {
            Assert.Equal(_payload, File.ReadAllBytes(RecycledPath));
            Assert.False(File.Exists(OriginalPath));
            if (_metadata is null) Assert.False(File.Exists(MetadataPath));
            else Assert.Equal(_metadata, File.ReadAllBytes(MetadataPath));
        }

        public void Dispose() => TestRecycleBin.DeleteDirectory(_root, recursive: true);
    }
}
