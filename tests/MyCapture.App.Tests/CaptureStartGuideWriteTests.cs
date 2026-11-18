using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class CaptureStartGuideWriteTests(ITestOutputHelper output)
{
    [Fact]
    public void BothClaimsStayInMemoryUntilCloseAndShareOneIdleSave()
    {
        bool selecting = true;
        var idle = new List<Action>();
        var saved = new List<AppSettings>();
        var settings = new AppSettings();
        settings.Capture.KeyPanelShownCount = -1; // Normalizing an old count also requests a write.
        var scheduler = new CaptureGuideWriteScheduler(() => selecting, idle.Add);
        var bookkeeping = new CaptureStartGuideBookkeeping(() => settings,
            value => saved.Add(value.DeepClone()), scheduler.Schedule, NullLogger.Instance);

        Assert.True(bookkeeping.Claim());
        Assert.False(bookkeeping.ClaimKeyPanel(true));
        Assert.Equal(1, settings.Capture.StartGuideShownCount);
        Assert.Equal(0, settings.Capture.KeyPanelShownCount);
        scheduler.NotifySelectorClosed();
        Assert.Empty(idle);
        Assert.Empty(saved);

        selecting = false;
        scheduler.NotifySelectorClosed();
        scheduler.NotifySelectorClosed();
        Assert.Empty(saved);
        Assert.Single(idle)();
        Assert.Equal(1, Assert.Single(saved).Capture.StartGuideShownCount);
        Assert.Equal(0, saved[0].Capture.KeyPanelShownCount);
        idle.Clear();
        scheduler.NotifySelectorClosed();
        Assert.Empty(idle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedIdleSaveWaitsForTheNextScreenshotOrRecordingSelector(bool recording)
    {
        bool screenshotOpen = true, recordingOpen = false;
        int saves = 0;
        var idle = new Queue<Action>();
        var settings = new AppSettings();
        var scheduler = new CaptureGuideWriteScheduler(() => screenshotOpen || recordingOpen, idle.Enqueue);
        settings.Capture.StartGuideShownCount = CaptureStartGuidePolicy.ShowCount - 1;
        var bookkeeping = new CaptureStartGuideBookkeeping(() => settings, _ => saves++, scheduler.Schedule, NullLogger.Instance);
        Assert.True(bookkeeping.Claim());
        screenshotOpen = false;
        scheduler.NotifySelectorClosed();
        Assert.Single(idle);

        screenshotOpen = !recording;
        recordingOpen = recording;
        idle.Dequeue()();
        Assert.Equal(0, saves);
        Assert.Empty(idle); // No polling or repeated idle turns while the selector is open.
        Assert.True(bookkeeping.ClaimKeyPanel(false));
        screenshotOpen = recordingOpen = false;
        scheduler.NotifySelectorClosed();
        Assert.Single(idle);
        idle.Dequeue()();
        Assert.Equal(1, saves);
    }

    [Fact]
    public void LegacyGuideWriteSchedulesEachClaimImmediatelyWhileSelectorIsOpen()
    {
        var idle = new List<Action>();
        int saves = 0;
        var settings = new AppSettings();
        var scheduler = new CaptureGuideWriteScheduler(() => true, idle.Add, legacyGuideWrite: true);
        settings.Capture.KeyPanelShownCount = -1;
        var bookkeeping = new CaptureStartGuideBookkeeping(() => settings, _ => saves++, scheduler.Schedule, NullLogger.Instance);
        Assert.True(bookkeeping.Claim());
        Assert.False(bookkeeping.ClaimKeyPanel(true));
        Assert.Equal(2, idle.Count);
        Assert.Equal(0, saves);
        foreach (Action write in idle) write();
        Assert.Equal(2, saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstRunNeverCreatesSettingsBeforeWelcomeCompletes(bool legacy)
    {
        string root = OwnedTestDirectory.Create("mc-guide-write-first-run-");
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            AppSettings settings = store.Load();
            bool selecting = true;
            int saves = 0;
            var idle = new Queue<Action>();
            var scheduler = new CaptureGuideWriteScheduler(() => selecting, idle.Enqueue, legacy);
            var bookkeeping = new CaptureStartGuideBookkeeping(() => settings,
                value => { saves++; store.Save(value); }, scheduler.Schedule, NullLogger.Instance);
            Assert.True(settings.General.IsFirstRun);
            Assert.True(bookkeeping.Claim());
            while (idle.TryDequeue(out Action? write)) write();
            selecting = false;
            scheduler.NotifySelectorClosed();
            while (idle.TryDequeue(out Action? write)) write();
            Assert.Equal(0, saves);
            Assert.False(File.Exists(paths.SettingsFile));

            store.CompleteFirstRun(settings);
            Assert.True(File.Exists(paths.SettingsFile));
            Assert.Equal(1, store.Load().Capture.StartGuideShownCount);
            selecting = true;
            Assert.True(bookkeeping.Claim());
            selecting = false;
            scheduler.NotifySelectorClosed();
            while (idle.TryDequeue(out Action? write)) write();
            Assert.Equal(1, saves);
            Assert.Equal(2, store.Load().Capture.StartGuideShownCount);
            output.WriteLine($"Body assertions completed before fixture cleanup: {nameof(FirstRunNeverCreatesSettingsBeforeWelcomeCompletes)} legacy={legacy}");
        }
        catch (Exception error) { output.WriteLine($"Body failure before fixture cleanup: {error}"); throw; }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void NoClaimSchedulesNothingAndFailedSaveDoesNotPreventTheNextSession()
    {
        bool selecting = true;
        int saves = 0;
        var idle = new Queue<Action>();
        var settings = new AppSettings();
        var scheduler = new CaptureGuideWriteScheduler(() => selecting, idle.Enqueue);
        var bookkeeping = new CaptureStartGuideBookkeeping(() => settings,
            _ => { if (++saves == 1) throw new IOException("locked"); }, scheduler.Schedule, NullLogger.Instance);
        selecting = false;
        scheduler.NotifySelectorClosed();
        Assert.Empty(idle);
        for (int session = 0; session < 2; session++)
        {
            selecting = true;
            Assert.True(bookkeeping.Claim());
            selecting = false;
            scheduler.NotifySelectorClosed();
            Assert.Single(idle);
            idle.Dequeue()();
        }
        Assert.Equal(2, saves);
    }
}
