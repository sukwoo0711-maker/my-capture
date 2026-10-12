using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class StoreUpdateObservationTests
{
    [Fact]
    public void FirstLaunchAndUnchangedVersionDoNotInventAnUpdate()
    {
        var state = new StoreUpdateState();
        Assert.True(state.ObserveLaunch("4.1.1.0", true));
        Assert.Null(state.CompletionNoticeVersion);
        state.PendingFromVersion = "4.1.1.0";
        Assert.False(state.ObserveLaunch("4.1.1.0", true));
        Assert.Null(state.CompletionNoticeVersion);
        Assert.Equal("4.1.1.0", state.PendingFromVersion);
    }
    [Fact]
    public void InstalledVersionIncreaseIsObservedAfterRestartIncludingAnExternalStoreUpdate()
    {
        var state = new StoreUpdateState { LastSeenVersion = "4.1.1.0", PendingFromVersion = "4.1.1.0" };
        Assert.True(state.ObserveLaunch("4.1.2.0", true));
        Assert.Equal("4.1.2.0", state.CompletionNoticeVersion);
        Assert.Null(state.PendingFromVersion);
        Assert.False(state.ObserveLaunch("4.1.2.0", true));
        state.CompletionNoticeVersion = null;
        Assert.True(state.ObserveLaunch("4.1.3.0", true));
        Assert.Equal("4.1.3.0", state.CompletionNoticeVersion);
    }
    [Theory]
    [InlineData("bad", true)]
    [InlineData("4.1.2.0", false)]
    [InlineData("4.1.0.0", true)]
    public void InvalidUnpackagedOrDowngradedVersionsDoNotClaimCompletion(string version, bool packaged)
    {
        var state = new StoreUpdateState { LastSeenVersion = "4.1.1.0" };
        state.ObserveLaunch(version, packaged);
        Assert.Null(state.CompletionNoticeVersion);
    }
}
