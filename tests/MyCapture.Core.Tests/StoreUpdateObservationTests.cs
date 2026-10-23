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
        Assert.False(state.ObserveLaunch("4.1.1.0", true));
        Assert.Null(state.CompletionNoticeVersion);
    }
    [Fact]
    public void InstalledVersionIncreaseIsObservedAfterRestartIncludingAnExternalStoreUpdate()
    {
        var state = new StoreUpdateState { LastSeenVersion = "4.1.1.0" };
        Assert.True(state.ObserveLaunch("4.1.2.0", true));
        Assert.Equal("4.1.2.0", state.CompletionNoticeVersion);
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

    [Fact]
    public void OwnedGitHubInstall_ObservesTheVersionIncreaseAfterRestart()
    {
        var state = new StoreUpdateState();
        Assert.True(state.ObserveLaunch("4.5.2", packaged: false, ownedInstall: true));
        Assert.Null(state.CompletionNoticeVersion);
        Assert.True(state.ObserveLaunch("4.5.3", packaged: false, ownedInstall: true));
        Assert.Equal("4.5.3", state.CompletionNoticeVersion);
        Assert.False(state.ObserveLaunch("4.5.3", packaged: false, ownedInstall: true));
    }

    [Fact]
    public void PortableAndDevelopmentLaunches_KeepTheExistingObservation()
    {
        var state = new StoreUpdateState { LastSeenVersion = "4.5.2" };
        Assert.False(state.ObserveLaunch("4.5.3", packaged: false, ownedInstall: false));
        Assert.Equal("4.5.2", state.LastSeenVersion);
        Assert.Null(state.CompletionNoticeVersion);
    }
}
