using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

internal sealed class StoreRatingTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class StoreRatingCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public readonly AppSettings Settings = new() { StoreRating = { FirstSeenUtc = Start } };
        public readonly StoreRatingTestClock Clock = new(Start.AddDays(4));
        public readonly List<string> Events = [];
        public bool Busy;
        public Func<Task<string>> Request = () => Task.FromResult("Succeeded");

        public StoreRatingCoordinator Create(bool isPackaged = true) => new(
            () => Settings,
            saved => Events.Add($"save count={saved.StoreRating.SuccessfulCaptureCount} attempted={saved.StoreRating.RequestAttemptedUtc is not null}"),
            write => write(),
            () => Busy,
            new Requester(this),
            NullLogger.Instance,
            isPackaged,
            Clock);

        public int Requests => Events.Count(e => e == "request");

        private sealed class Requester(Harness owner) : IStoreRatingRequester
        {
            public Task<string> RequestRateAndReviewAsync()
            {
                owner.Events.Add("request");
                return owner.Request();
            }
        }
    }

    private static void Succeed(StoreRatingCoordinator coordinator, int times)
    {
        for (int i = 0; i < times; i++) coordinator.RecordCapture(succeeded: true);
    }

    [Fact]
    public void StoreRating_UnpackagedBuild_NeverCountsSavesOrAsks()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create(isPackaged: false);
        harness.Clock.Now = Start.AddYears(1);
        Succeed(coordinator, 25);
        coordinator.NotifyFlowEnded();
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.TryRequestNow());
        Assert.Empty(harness.Events);
        Assert.Equal(0, harness.Settings.StoreRating.SuccessfulCaptureCount);
    }

    [Fact]
    public void StoreRating_TenthSuccessArmsAndTheIdleFlowEndAsksExactlyOnce()
    {
        StaTestHost.Run(() =>
        {
            var harness = new Harness();
            StoreRatingCoordinator coordinator = harness.Create();

            Succeed(coordinator, 9);
            Assert.False(coordinator.IsPending);
            coordinator.NotifyFlowEnded();
            Assert.False(coordinator.IsSettleTimerRunning);

            coordinator.RecordCapture(succeeded: true);
            Assert.True(coordinator.IsPending);
            coordinator.NotifyFlowEnded();
            Assert.True(coordinator.IsSettleTimerRunning);

            // An overlay, editor, recording or card on screen defers it to the next flow end.
            harness.Busy = true;
            Assert.False(coordinator.TryRequestNow());
            Assert.True(coordinator.IsPending);
            Assert.Equal(0, harness.Requests);

            harness.Busy = false;
            Assert.True(coordinator.TryRequestNow());
            Assert.Equal(1, harness.Requests);
            Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.RequestAttemptedUtc);
            // The attempt is persisted before the dialog is requested.
            int request = harness.Events.IndexOf("request");
            Assert.Equal("save count=10 attempted=True", harness.Events[request - 1]);

            int eventsAfterAttempt = harness.Events.Count;
            harness.Clock.Now = harness.Clock.Now.AddDays(30);
            Succeed(coordinator, 20);
            coordinator.NotifyFlowEnded();
            Assert.False(coordinator.IsPending);
            Assert.False(coordinator.TryRequestNow());
            Assert.Equal(1, harness.Requests);
            Assert.Equal(eventsAfterAttempt, harness.Events.Count);
        });
    }

    [Fact]
    public void StoreRating_WaitsThreeDaysFromFirstSeen()
    {
        var harness = new Harness();
        harness.Clock.Now = Start.AddDays(3) - TimeSpan.FromMinutes(1);
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 10);
        Assert.False(coordinator.IsPending);

        harness.Clock.Now = Start.AddDays(3);
        coordinator.RecordCapture(succeeded: true);
        Assert.True(coordinator.IsPending);
    }

    [Fact]
    public void StoreRating_AnErrorCancelsTheArmedRequestUntilTheNextSuccess()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 10);
        Assert.True(coordinator.IsPending);

        coordinator.RecordCapture(succeeded: false);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.TryRequestNow());
        Assert.Equal(0, harness.Requests);

        coordinator.RecordCapture(succeeded: true);
        Assert.True(coordinator.IsPending);
        Assert.True(coordinator.TryRequestNow());
        Assert.Equal(1, harness.Requests);
    }

    [Fact]
    public async Task StoreRating_AFailedRequestIsOnlyLoggedAndNeverRetried()
    {
        var harness = new Harness { Request = () => Task.FromException<string>(new InvalidOperationException("Store unavailable")) };
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 10);
        Assert.True(coordinator.TryRequestNow());
        await coordinator.LastRequest!;
        Assert.NotNull(harness.Settings.StoreRating.RequestAttemptedUtc);

        Succeed(coordinator, 3);
        Assert.False(coordinator.TryRequestNow());
        Assert.Equal(1, harness.Requests);
    }

    [Fact]
    public void StoreRating_FirstRunCountsWithoutCreatingTheSettingsFile()
    {
        var harness = new Harness();
        harness.Settings.General.IsFirstRun = true;
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 3);
        Assert.Equal(3, harness.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.DoesNotContain(harness.Events, e => e.StartsWith("save", StringComparison.Ordinal));
    }

    [Fact]
    public void StoreRating_OpeningTheReviewPageManuallyRetiresTheAutomaticRequest()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 4);
        coordinator.NoteManualReviewOpened();
        Assert.NotNull(harness.Settings.StoreRating.RequestAttemptedUtc);
        Assert.Equal("save count=4 attempted=True", harness.Events[^1]);

        Succeed(coordinator, 10);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.TryRequestNow());
        Assert.Equal(0, harness.Requests);
    }
}

public sealed class StoreRatingAndPrtScGuideApplyTests : KoreanCaptionTest
{
    private sealed class BlockingRegistrar(params Hotkey[] blocked) : IHotkeyRegistrar
    {
        private readonly Dictionary<int, Hotkey> _held = [];

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            if (blocked.Contains(hotkey) || _held.ContainsValue(hotkey))
            {
                errorCode = 1409;
                errorMessage = "Hot key is already registered.";
                return false;
            }

            _held[id] = hotkey;
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id) => _held.Remove(id);
    }

    private sealed class MemoryRunKeyStore : IRunKeyStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? GetValue(string name) => _values.GetValueOrDefault(name);
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }

    private static void WithApply(IHotkeyRegistrar registrar, Action<SettingsStore, Func<AppSettings>, SettingsApplyService> body)
    {
        StaTestHost.Run(() =>
        {
            string root = TestRecycleBin.CreateTempSubdirectory("mycapture-store-rating-apply-").FullName;
            var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
            AppSettings published = store.Load();
            store.CompleteFirstRun(published);
            using var window = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
            hotkeys.Initialize(published.Hotkeys);
            var startup = new StartupRegistrationService(new MemoryRunKeyStore(), @"C:\Apps\MyCapture.exe");
            var apply = new SettingsApplyService(store, () => published, next => published = next, null, hotkeys, startup, NullLogger.Instance);
            body(store, () => published, apply);
        });
    }

    [Fact]
    public void StoreRating_ApplyKeepsTheLiveStateRecordedWhileTheWindowWasOpen()
    {
        WithApply(new BlockingRegistrar(), (store, live, apply) =>
        {
            live().StoreRating.SuccessfulCaptureCount = 3;
            var draft = new SettingsDraft(live()) { MaxItems = "450" };

            // Captures taken while the window is open keep counting on the live object.
            DateTimeOffset attempted = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
            live().StoreRating.SuccessfulCaptureCount = 8;
            live().StoreRating.RequestAttemptedUtc = attempted;

            SettingsApplyResult result = apply.Apply(draft.ToAppSettings());
            Assert.True(result.Saved);
            Assert.Equal(450, live().Queue.MaxItems);
            Assert.Equal(8, live().StoreRating.SuccessfulCaptureCount);
            Assert.Equal(attempted, live().StoreRating.RequestAttemptedUtc);
            StoreRatingState persisted = store.Load().StoreRating;
            Assert.Equal(8, persisted.SuccessfulCaptureCount);
            Assert.Equal(attempted, persisted.RequestAttemptedUtc);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrtScGuide_ApplyFailureExplainsTheWindowsPrintScreenSetting(bool windowsClaimsKey)
    {
        Assert.True(Hotkey.TryParse("PrintScreen", out Hotkey printScreen));
        WithApply(new BlockingRegistrar(printScreen), (_, live, apply) =>
        {
            apply.PrintScreenCaptureKeyEnabled = () => windowsClaimsKey;
            var draft = new SettingsDraft(live()) { CaptureFullScreenHotkey = "PrintScreen" };

            SettingsApplyResult result = apply.Apply(draft.ToAppSettings());

            Assert.True(result.Saved);
            Assert.False(result.HotkeysApplied);
            Assert.Equal(Hotkey.None, live().Hotkeys.CaptureFullScreen);
            string notice = UiText.Get("PrtScGuide_FailureNotice");
            if (windowsClaimsKey) Assert.Contains(notice, result.Messages);
            else Assert.DoesNotContain(notice, result.Messages);
        });
    }

    [Fact]
    public void PrtScGuide_DefaultApplyServiceNeverReadsTheMachineSetting()
    {
        Assert.True(Hotkey.TryParse("PrintScreen", out Hotkey printScreen));
        WithApply(new BlockingRegistrar(printScreen), (_, live, apply) =>
        {
            var draft = new SettingsDraft(live()) { CaptureFullScreenHotkey = "PrintScreen" };
            SettingsApplyResult result = apply.Apply(draft.ToAppSettings());
            Assert.DoesNotContain(UiText.Get("PrtScGuide_FailureNotice"), result.Messages);
            Assert.Equal(PrintScreenKeyGuide.VkSnapshot, printScreen.VirtualKey);
        });
    }
}
