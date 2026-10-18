using System;
using MyCapture.Core.Platform;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The supported-host contract uses the 22H2 build floor separately from the shared API contract.
/// </summary>
public sealed class WindowsSupportPolicyTests
{
    [Fact]
    public void MinimumBuild_IsTheWindows10Baseline()
    {
        Assert.Equal(19045, WindowsSupportPolicy.MinimumBuild);
        Assert.Equal("Windows 10 version 22H2", WindowsSupportPolicy.MinimumReleaseName);
        Assert.Equal("10.0.19041.0", WindowsSupportPolicy.SupportedOSPlatformVersion);
        Assert.Equal(22000, WindowsSupportPolicy.MinimumArm64EmulationBuild);
    }

    [Theory]
    [InlineData(17763)] // Windows 10 1809
    [InlineData(18362)] // Windows 10 1903
    [InlineData(19041)] // Windows 10 2004
    [InlineData(19044)] // Windows 10 21H2
    public void BuildsBeforeWindows10Version22H2_AreNotSupported(int build)
    {
        Assert.False(WindowsSupportPolicy.IsSupportedBuild(build));
        Assert.False(WindowsSupportPolicy.IsSupportedHost(new Version(10, 0, build, 0)));
    }

    [Theory]
    [InlineData(19045)] // Windows 10 22H2
    [InlineData(22000)] // Windows 11 21H2
    [InlineData(22621)] // Windows 11 22H2
    [InlineData(22631)] // Windows 11 23H2
    [InlineData(26100)] // Windows 11 24H2
    [InlineData(26200)] // Windows 11 25H2
    public void Windows10Version22H2AndLater_AreSupported(int build)
    {
        Assert.True(WindowsSupportPolicy.IsSupportedBuild(build));
        Assert.True(WindowsSupportPolicy.IsSupportedHost(new Version(10, 0, build, 0)));
    }

    [Fact]
    public void PreWindows10MajorVersions_AreNotSupported()
    {
        Assert.False(WindowsSupportPolicy.IsSupportedHost(new Version(6, 3, 9600, 0)));
        Assert.False(WindowsSupportPolicy.IsSupportedHost(new Version(6, 1, 7601, 0)));
        Assert.False(WindowsSupportPolicy.IsSupportedHost(new Version(6, 3, 26000, 0)));
    }

    [Fact]
    public void IsSupportedHost_RejectsNull()
    {
        Assert.False(WindowsSupportPolicy.IsSupportedHost(null));
    }

    [Fact]
    public void DescribeRequirement_NamesWindows10AndTheBuildFloor()
    {
        string text = WindowsSupportPolicy.DescribeRequirement();
        Assert.Contains("Windows 10 version 22H2", text, StringComparison.Ordinal);
        Assert.Contains("19045", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeUnsupportedHost_ReportsDetectedBuildAndRequirement()
    {
        string text = WindowsSupportPolicy.DescribeUnsupportedHost(new Version(10, 0, 19044, 0));
        Assert.Contains("19044", text, StringComparison.Ordinal);
        Assert.Contains("19045", text, StringComparison.Ordinal);
        Assert.Contains("Windows 10", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(19045, false)]
    [InlineData(21999, false)]
    [InlineData(22000, true)]
    [InlineData(22621, true)]
    public void Arm64X64Emulation_RequiresWindows11(int build, bool expected) =>
        Assert.Equal(expected, WindowsSupportPolicy.IsArm64EmulationSupportedBuild(build));

    [Fact]
    public void CurrentHost_MatchesTheRuntimeApiForTheSameBuild()
    {
        // Sanity: the policy must agree with the framework helper it stands in for.
        Version os = Environment.OSVersion.Version;
        bool expected = OperatingSystem.IsWindows()
            && OperatingSystem.IsWindowsVersionAtLeast(10, 0, WindowsSupportPolicy.MinimumBuild);
        Assert.Equal(expected, OperatingSystem.IsWindows() && WindowsSupportPolicy.IsSupportedHost(os));
    }
}
