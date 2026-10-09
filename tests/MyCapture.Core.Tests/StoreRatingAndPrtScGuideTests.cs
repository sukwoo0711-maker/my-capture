using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class StoreRatingPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = Start + StoreRatingPolicy.RequiredAge;

    private static StoreRatingState Eligible() => new()
    {
        FirstSeenUtc = Start,
        SuccessfulCaptureCount = StoreRatingPolicy.RequiredSuccessfulCaptures,
    };

    [Fact]
    public void StoreBuild_AsksOnceEveryConditionHolds() =>
        Assert.True(StoreRatingPolicy.ShouldRequest(Eligible(), isPackaged: true, Due));

    [Fact]
    public void UnpackagedBuild_NeverAsks()
    {
        Assert.False(StoreRatingPolicy.ShouldRequest(Eligible(), isPackaged: false, Due));
        Assert.False(StoreRatingPolicy.ShouldRequest(Eligible(), isPackaged: false, Due.AddYears(1)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public void SuccessfulCaptureBoundary(int count, bool expected)
    {
        StoreRatingState state = Eligible();
        state.SuccessfulCaptureCount = count;
        Assert.Equal(expected, StoreRatingPolicy.ShouldRequest(state, isPackaged: true, Due));
    }

    [Fact]
    public void AgeBoundary_RequiresThreeFullDays()
    {
        Assert.False(StoreRatingPolicy.ShouldRequest(Eligible(), isPackaged: true, Due - TimeSpan.FromTicks(1)));
        Assert.True(StoreRatingPolicy.ShouldRequest(Eligible(), isPackaged: true, Due));
        Assert.False(StoreRatingPolicy.ShouldRequest(Eligible(), isPackaged: true, Start));
    }

    [Fact]
    public void MissingFirstSeen_NeverAsks()
    {
        StoreRatingState state = Eligible();
        state.FirstSeenUtc = null;
        Assert.False(StoreRatingPolicy.ShouldRequest(state, isPackaged: true, Due.AddYears(1)));
    }

    [Fact]
    public void NeverAsksRightAfterAnError_UntilTheNextSuccess()
    {
        StoreRatingState state = Eligible();
        Assert.True(StoreRatingPolicy.RecordCapture(state, succeeded: false));
        Assert.True(state.LastCaptureFailed);
        Assert.False(StoreRatingPolicy.ShouldRequest(state, isPackaged: true, Due));

        Assert.True(StoreRatingPolicy.RecordCapture(state, succeeded: true));
        Assert.False(state.LastCaptureFailed);
        Assert.True(StoreRatingPolicy.ShouldRequest(state, isPackaged: true, Due));
    }

    [Fact]
    public void AnAttempt_IsFinalAndKeepsItsFirstTime()
    {
        StoreRatingState state = Eligible();
        StoreRatingPolicy.MarkAttempted(state, Due);
        Assert.Equal(Due, state.RequestAttemptedUtc);
        Assert.False(StoreRatingPolicy.ShouldRequest(state, isPackaged: true, Due));
        Assert.False(StoreRatingPolicy.ShouldRequest(state, isPackaged: true, Due.AddYears(5)));

        StoreRatingPolicy.MarkAttempted(state, Due.AddDays(9));
        Assert.Equal(Due, state.RequestAttemptedUtc);
    }

    [Fact]
    public void RecordCapture_CountsToTheThresholdAndReportsOnlyRealChanges()
    {
        var state = new StoreRatingState();
        for (int i = 1; i <= StoreRatingPolicy.RequiredSuccessfulCaptures; i++)
        {
            Assert.True(StoreRatingPolicy.RecordCapture(state, succeeded: true));
            Assert.Equal(i, state.SuccessfulCaptureCount);
        }

        // Saturated and still succeeding: nothing to write.
        Assert.False(StoreRatingPolicy.RecordCapture(state, succeeded: true));
        Assert.Equal(StoreRatingPolicy.RequiredSuccessfulCaptures, state.SuccessfulCaptureCount);

        // A failure keeps the count but flips the flag once.
        Assert.True(StoreRatingPolicy.RecordCapture(state, succeeded: false));
        Assert.False(StoreRatingPolicy.RecordCapture(state, succeeded: false));
        Assert.Equal(StoreRatingPolicy.RequiredSuccessfulCaptures, state.SuccessfulCaptureCount);
    }

    [Theory]
    [InlineData(-5, true, 1)]
    [InlineData(-5, false, 0)]
    [InlineData(int.MaxValue, true, 10)]
    public void RecordCapture_RepairsHandEditedCounts(int stored, bool succeeded, int expected)
    {
        var state = new StoreRatingState { SuccessfulCaptureCount = stored };
        Assert.True(StoreRatingPolicy.RecordCapture(state, succeeded));
        Assert.Equal(expected, state.SuccessfulCaptureCount);
    }

    [Fact]
    public void EnsureFirstSeen_StampsMissingKeepsExistingAndRestartsAFutureStamp()
    {
        var state = new StoreRatingState();
        Assert.True(StoreRatingPolicy.EnsureFirstSeen(state, Start));
        Assert.Equal(Start, state.FirstSeenUtc);

        Assert.False(StoreRatingPolicy.EnsureFirstSeen(state, Start.AddDays(10)));
        Assert.Equal(Start, state.FirstSeenUtc);

        // A clock that ran a few hours fast is tolerated; one set years ahead is not.
        state.FirstSeenUtc = Start.AddHours(20);
        Assert.False(StoreRatingPolicy.EnsureFirstSeen(state, Start));
        state.FirstSeenUtc = Start.AddYears(3);
        Assert.True(StoreRatingPolicy.EnsureFirstSeen(state, Start));
        Assert.Equal(Start, state.FirstSeenUtc);
    }

    [Fact]
    public void ReviewUri_TargetsTheMyCaptureListing() =>
        Assert.Equal("ms-windows-store://review/?ProductId=9PB33MC095VR", StoreRatingPolicy.ReviewUri);
}

public sealed class StoreRatingSettingsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static SettingsStore Store(TempWorkspace workspace, DateTimeOffset now) =>
        new(workspace.Paths, NullLogger<SettingsStore>.Instance) { Clock = new ManualClock(now) };

    private static string SettingsText(TempWorkspace workspace) =>
        File.ReadAllText(ValidatedFilePaths.FileWithinRoot(workspace.Paths.DataRoot, "settings.json", ".json"));

    [Fact]
    public void ExistingUserFile_IsStampedOnFirstLoadAndWrittenOnce()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """{"schemaVersion":4,"general":{"launchAtLogin":false}}""");

        AppSettings first = Store(workspace, Start).Load();
        Assert.Equal(Start, first.StoreRating.FirstSeenUtc);
        Assert.Equal(0, first.StoreRating.SuccessfulCaptureCount);
        Assert.Null(first.StoreRating.RequestAttemptedUtc);
        Assert.False(first.General.LaunchAtLogin);
        string persisted = SettingsText(workspace);
        Assert.Contains("\"storeRating\"", persisted, StringComparison.Ordinal);

        for (int launch = 1; launch <= 2; launch++)
        {
            AppSettings later = Store(workspace, Start.AddDays(5 * launch)).Load();
            Assert.Equal(Start, later.StoreRating.FirstSeenUtc);
            Assert.Equal(persisted, SettingsText(workspace));
        }
    }

    [Fact]
    public void NewInstall_IsStampedButWrittenOnlyWithTheWelcomeCompletion()
    {
        using var workspace = new TempWorkspace();
        SettingsStore store = Store(workspace, Start);
        AppSettings fresh = store.Load();
        Assert.True(fresh.General.IsFirstRun);
        Assert.Equal(Start, fresh.StoreRating.FirstSeenUtc);
        Assert.False(File.Exists(workspace.Paths.SettingsFile));

        store.CompleteFirstRun(fresh);
        Assert.Equal(Start, Store(workspace, Start.AddDays(4)).Load().StoreRating.FirstSeenUtc);
    }

    [Fact]
    public void CorruptFile_IsNotOverwrittenJustToRecordTheStamp()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        const string corrupt = "{ \"schemaVersion\": 4, ";
        File.WriteAllText(workspace.Paths.SettingsFile, corrupt);

        AppSettings loaded = Store(workspace, Start).Load();
        Assert.NotNull(loaded.StoreRating);
        Assert.Equal(corrupt, SettingsText(workspace));
    }

    [Fact]
    public void SaveLoadRoundTrip_PreservesEveryField()
    {
        using var workspace = new TempWorkspace();
        var settings = new AppSettings
        {
            StoreRating =
            {
                FirstSeenUtc = Start,
                SuccessfulCaptureCount = 7,
                LastCaptureFailed = true,
                RequestAttemptedUtc = Start.AddDays(6),
            },
        };
        Store(workspace, Start).Save(settings);

        StoreRatingState loaded = Store(workspace, Start.AddDays(30)).Load().StoreRating;
        Assert.Equal(Start, loaded.FirstSeenUtc);
        Assert.Equal(7, loaded.SuccessfulCaptureCount);
        Assert.True(loaded.LastCaptureFailed);
        Assert.Equal(Start.AddDays(6), loaded.RequestAttemptedUtc);
    }

    [Fact]
    public void ExplicitNullSection_ReadsAsNewState()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"storeRating":null}""",
            MyCapture.Core.Serialization.JsonDefaults.Readable)!;
        Assert.NotNull(settings.StoreRating);
        Assert.Equal(0, settings.StoreRating.SuccessfulCaptureCount);
    }

    [Fact]
    public void DeepClone_CopiesTheStateIndependently()
    {
        var source = new AppSettings
        {
            StoreRating = { FirstSeenUtc = Start, SuccessfulCaptureCount = 4, LastCaptureFailed = true, RequestAttemptedUtc = Start },
        };
        AppSettings clone = source.DeepClone();
        Assert.NotSame(source.StoreRating, clone.StoreRating);
        Assert.Equal(Start, clone.StoreRating.FirstSeenUtc);
        Assert.Equal(4, clone.StoreRating.SuccessfulCaptureCount);
        Assert.True(clone.StoreRating.LastCaptureFailed);
        Assert.Equal(Start, clone.StoreRating.RequestAttemptedUtc);

        clone.StoreRating.SuccessfulCaptureCount = 9;
        Assert.Equal(4, source.StoreRating.SuccessfulCaptureCount);
    }
}
