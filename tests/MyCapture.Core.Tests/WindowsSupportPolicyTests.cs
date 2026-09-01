using System;
using MyCapture.Core.Platform;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The supported-host contract. MyCapture 1.3.0 targets Windows 11 only; every Windows 10
/// build is out of support, so the policy must reject them by build number rather than by
/// marketing name. These tests are the single behavioural definition of that floor.
/// </summary>
public sealed class WindowsSupportPolicyTests
{
    [Fact]
    public void MinimumBuild_IsTheWindows11Baseline()
    {
        Assert.Equal(22000, WindowsSupportPolicy.MinimumBuild);
        Assert.Equal("Windows 11 version 21H2", WindowsSupportPolicy.MinimumReleaseName);
        Assert.Equal("10.0.22000.0", WindowsSupportPolicy.SupportedOSPlatformVersion);
    }

    [Theory]
    [InlineData(17763)] // Windows 10 1809
    [InlineData(18362)] // Windows 10 1903
    [InlineData(19041)] // Windows 10 2004
    [InlineData(19044)] // Windows 10 21H2
    [InlineData(19045)] // Windows 10 22H2 (final Windows 10 build)
    [InlineData(21996)] // pre-release build below the floor
    public void Windows10Builds_AreNotSupported(int build)
    {
        Assert.False(WindowsSupportPolicy.IsSupportedBuild(build));
        Assert.False(WindowsSupportPolicy.IsSupportedHost(new Version(10, 0, build, 0)));
    }

    [Theory]
    [InlineData(22000)] // Windows 11 21H2
    [InlineData(22621)] // Windows 11 22H2
    [InlineData(22631)] // Windows 11 23H2
    [InlineData(26100)] // Windows 11 24H2
    [InlineData(26200)] // Windows 11 25H2
    public void Windows11Builds_AreSupported(int build)
    {
        Assert.True(WindowsSupportPolicy.IsSupportedBuild(build));
        Assert.True(WindowsSupportPolicy.IsSupportedHost(new Version(10, 0, build, 0)));
    }

    [Fact]
    public void PreWindows10MajorVersions_AreNotSupported()
    {
        // Windows 8.1 reports 6.3 with a build number far above the Windows 11 floor once
        // the major version is ignored, so the major version must be checked as well.
        Assert.False(WindowsSupportPolicy.IsSupportedHost(new Version(6, 3, 9600, 0)));
        Assert.False(WindowsSupportPolicy.IsSupportedHost(new Version(6, 1, 7601, 0)));
    }

    [Fact]
    public void IsSupportedHost_RejectsNull()
    {
        Assert.False(WindowsSupportPolicy.IsSupportedHost(null));
    }

    [Fact]
    public void DescribeRequirement_NamesWindows11AndTheBuildFloor()
    {
        string text = WindowsSupportPolicy.DescribeRequirement();
        Assert.Contains("Windows 11", text, StringComparison.Ordinal);
        Assert.Contains("22000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows 10", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeUnsupportedHost_ReportsDetectedBuildAndRequirement()
    {
        string text = WindowsSupportPolicy.DescribeUnsupportedHost(new Version(10, 0, 19045, 0));
        Assert.Contains("19045", text, StringComparison.Ordinal);
        Assert.Contains("Windows 11", text, StringComparison.Ordinal);
        Assert.Contains("22000", text, StringComparison.Ordinal);
    }

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
