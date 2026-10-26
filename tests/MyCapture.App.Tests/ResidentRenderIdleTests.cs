using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentRenderIdleTests
{
    [Theory]
    [InlineData(true, 180, false)]
    [InlineData(false, 179, false)]
    [InlineData(false, 180, true)]
    [InlineData(false, 181, true)]
    public void IdleRequiresNoVisibleWindowAndThresholdWithoutInput(bool visible, int inputSeconds, bool expected)
    {
        TimeSpan now = TimeSpan.Zero;
        int released = 0;
        var controller = new ResidentRenderIdleController(TimeSpan.FromSeconds(180), () => now, () => released++, () => { });
        now = TimeSpan.FromSeconds(180);
        Assert.Equal(expected, controller.Poll(visible, TimeSpan.FromSeconds(inputSeconds)));
        Assert.Equal(expected, controller.IsReleased);
        Assert.Equal(expected ? 1 : 0, released);
    }

    [Fact]
    public void RecentVisibilityAndCommandEachStartANewQuietInterval()
    {
        TimeSpan now = TimeSpan.Zero;
        var controller = new ResidentRenderIdleController(TimeSpan.FromSeconds(10), () => now, () => { }, () => { });
        now = TimeSpan.FromSeconds(10);
        controller.Poll(true, TimeSpan.FromHours(1));
        now = TimeSpan.FromSeconds(19);
        Assert.False(controller.Poll(false, TimeSpan.FromHours(1)));
        controller.BeforeUse();
        now = TimeSpan.FromSeconds(28);
        Assert.False(controller.Poll(false, TimeSpan.FromHours(1)));
        now = TimeSpan.FromSeconds(29);
        Assert.True(controller.Poll(false, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ReleaseAndRestoreAreOncePerCycleAndRestorePrecedesPresentation()
    {
        TimeSpan now = TimeSpan.Zero;
        List<string> events = [];
        var controller = new ResidentRenderIdleController(TimeSpan.FromSeconds(1), () => now,
            () => events.Add("release"), () => events.Add("restore"));
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            now = TimeSpan.FromSeconds(cycle);
            controller.Poll(false, TimeSpan.FromSeconds(1));
            controller.Poll(false, TimeSpan.FromSeconds(1));
            controller.BeforeUse();
            events.Add("show");
            controller.BeforeUse();
        }
        Assert.Equal(new[] { "release", "restore", "show", "release", "restore", "show" }, events);
        Assert.Equal(2, controller.ReleaseCount);
        Assert.Equal(2, controller.RestoreCount);
    }

    [Fact]
    public void FailedTransitionDoesNotClaimReleasedOrRestored()
    {
        TimeSpan now = TimeSpan.Zero;
        bool throwRelease = true;
        var controller = new ResidentRenderIdleController(TimeSpan.FromSeconds(1), () => now,
            () => { if (throwRelease) throw new InvalidOperationException("release"); },
            () => throw new InvalidOperationException("restore"));
        now = TimeSpan.FromSeconds(1);
        Assert.Throws<InvalidOperationException>(() => controller.Poll(false, TimeSpan.FromSeconds(1)));
        Assert.False(controller.IsReleased);
        Assert.Equal(0, controller.ReleaseCount);
        throwRelease = false;
        controller.Poll(false, TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(controller.BeforeUse);
        Assert.True(controller.IsReleased);
        Assert.Equal(0, controller.RestoreCount);
    }

    [Fact]
    public void ReentrantRestoreIsRejectedBeforeNestedPresentation()
    {
        TimeSpan now = TimeSpan.Zero;
        ResidentRenderIdleController? controller = null;
        bool nestedShow = false;
        controller = new ResidentRenderIdleController(TimeSpan.FromSeconds(1), () => now,
            () => { }, () => { controller!.BeforeUse(); nestedShow = true; });
        now = TimeSpan.FromSeconds(1);
        controller.Poll(false, now);
        Assert.Throws<InvalidOperationException>(controller.BeforeUse);
        Assert.False(nestedShow);
        Assert.True(controller.IsReleased);
        Assert.Equal(0, controller.RestoreCount);
    }

    [Fact]
    public void ReentrantReleaseIsRejectedBeforeNestedPresentation()
    {
        TimeSpan now = TimeSpan.Zero;
        ResidentRenderIdleController? controller = null;
        bool nestedShow = false;
        controller = new ResidentRenderIdleController(TimeSpan.FromSeconds(1), () => now,
            () => { controller!.BeforeUse(); nestedShow = true; }, () => { });
        now = TimeSpan.FromSeconds(1);
        Assert.Throws<InvalidOperationException>(() => controller.Poll(false, now));
        Assert.False(nestedShow);
        Assert.False(controller.IsReleased);
        Assert.Equal(0, controller.ReleaseCount);
    }

    [Fact]
    public void OptionsAreIgnoredOutsideResidentProbe()
    {
        Assert.Null(ResidentProbeOptions.TryParse(["--resident-idle-release=keepalive", "--resident-idle-seconds=1"]));
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("processmode", 1)]
    [InlineData("keepalive", 2)]
    public void OptionsParseEqualsAndSeparateForms(string value, int expectedValue)
    {
        var expected = (ResidentIdleReleaseVariant)expectedValue;
        var equals = ResidentProbeOptions.ParseIdleRelease(["--resident-idle-release=" + value]);
        var separate = ResidentProbeOptions.ParseIdleRelease(["--resident-idle-release", value, "--resident-idle-seconds", "2"]);
        Assert.Equal((expected, 180, true), equals);
        Assert.Equal((expected, 2, true), separate);
    }

    [Fact]
    public void DefaultProbeDoesNotInstallOrMeasureIdleRelease()
    {
        Assert.Equal((ResidentIdleReleaseVariant.None, 180, false), ResidentProbeOptions.ParseIdleRelease([]));
    }

    [Theory]
    [InlineData("--resident-idle-seconds=0")]
    [InlineData("--resident-idle-seconds=-1")]
    [InlineData("--resident-idle-seconds=3601")]
    [InlineData("--resident-idle-seconds=oops")]
    [InlineData("--resident-idle-release=unexpected")]
    [InlineData("--resident-idle-release")]
    public void InvalidOptionIsRejected(string argument)
    {
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ParseIdleRelease([argument]));
    }

    [Theory]
    [InlineData("processmode")]
    [InlineData("keepalive")]
    public void SoftwareRenderCannotSilentlyMixWithIdleCandidate(string variant)
    {
        // Validation happens before output-directory creation or any application startup.
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.TryParse([
            "--selftest-resident", "--resident-software-render", "--resident-idle-release=" + variant]));
    }
}
