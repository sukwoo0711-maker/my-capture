using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The portable ZIP has no installer preflight, so the process must refuse unsupported hosts by
/// itself. These tests pin that behaviour without starting a WPF message loop.
/// </summary>
public sealed class HostRequirementGateTests
{
    [Theory]
    [InlineData(10, 0, 17763)] // Windows 10 1809
    [InlineData(10, 0, 19045)] // Windows 10 22H2
    [InlineData(6, 3, 9600)]   // Windows 8.1
    public void BlockUnsupportedHost_StopsStartupBelowTheWindows11Floor(int major, int minor, int build)
    {
        string? shown = null;
        int? exitCode = null;

        bool blocked = HostRequirementGate.BlockUnsupportedHost(
            new Version(major, minor, build, 0),
            message => shown = message,
            code => exitCode = code);

        Assert.True(blocked);
        Assert.Equal(HostRequirementGate.UnsupportedHostExitCode, exitCode);
        Assert.NotNull(shown);
        Assert.Contains("Windows 11", shown, StringComparison.Ordinal);
        Assert.Contains(build.ToString(), shown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(22000)] // Windows 11 21H2
    [InlineData(26100)] // Windows 11 24H2
    [InlineData(26200)] // Windows 11 25H2
    public void BlockUnsupportedHost_AllowsWindows11(int build)
    {
        bool shownCalled = false;
        bool shutdownCalled = false;

        bool blocked = HostRequirementGate.BlockUnsupportedHost(
            new Version(10, 0, build, 0),
            _ => shownCalled = true,
            _ => shutdownCalled = true);

        Assert.False(blocked);
        Assert.False(shownCalled);
        Assert.False(shutdownCalled);
    }

    [Fact]
    public void BlockUnsupportedHost_TreatsUnknownVersionAsUnsupported()
    {
        int? exitCode = null;
        bool blocked = HostRequirementGate.BlockUnsupportedHost(
            null,
            _ => { },
            code => exitCode = code);

        Assert.True(blocked);
        Assert.Equal(HostRequirementGate.UnsupportedHostExitCode, exitCode);
    }

    [Fact]
    public void BlockUnsupportedHost_ValidatesCallbacks()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HostRequirementGate.BlockUnsupportedHost(new Version(10, 0, 19045, 0), null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            HostRequirementGate.BlockUnsupportedHost(new Version(10, 0, 19045, 0), _ => { }, null!));
    }
}
