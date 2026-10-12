using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureEditLeaseAdmissionTests
{
    [Fact]
    public void PendingRemoval_RejectsEditSessionWithoutLeavingBusyState() => StaTestHost.Run(() =>
    {
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner));
        try
        {
            Task verification = VerifyAsync();
            Task guarded = verification.WaitAsync(TimeSpan.FromSeconds(30));
            var frame = new DispatcherFrame();
            _ = guarded.ContinueWith(_ => owner.BeginInvoke(new Action(() => frame.Continue = false)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            guarded.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            owner.InvokeShutdown();
        }

        async Task VerifyAsync()
        {
            // Retain this owned fixture: no Shell recycle/restore or native UI is needed.
            AppPaths paths = AppPaths.CreateForRoot(TestRecycleBin.CreateTempSubdirectory("mc-edit-lease-admission-").FullName);
            paths.EnsureCreated();
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance);
            var record = new CaptureRecord { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.Now };
            record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
            queue.Add(record);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.PrepareRemoval = (_, _) =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                return null; // The actual exclusive reservation ends with a rejected move.
            };

            Task<CaptureBatchRemovalResult> removal = queue.RemoveManyAsync([record.Id]);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                owner.VerifyAccess();
                Assert.True(queue.IsRemovalPending(record.Id));
                Assert.Throws<IOException>(() => commit.BeginEditSession(record));
                Assert.False(commit.IsRecordBusy(record.Id));
            }
            finally
            {
                release.TrySetResult();
                await removal.WaitAsync(TimeSpan.FromSeconds(30));
            }

            owner.VerifyAccess();
            Assert.False(queue.IsRemovalPending(record.Id));
            Assert.Same(record, queue.Find(record.Id));
            Assert.False(commit.IsRecordBusy(record.Id));
            using (CaptureEditSession first = commit.BeginEditSession(record))
            {
                Assert.True(commit.IsRecordBusy(record.Id));
                using (CaptureEditSession peer = commit.BeginEditSession(record))
                    Assert.True(commit.IsRecordBusy(record.Id));
                Assert.True(commit.IsRecordBusy(record.Id));
            }
            Assert.False(commit.IsRecordBusy(record.Id));
        }
    });
}
