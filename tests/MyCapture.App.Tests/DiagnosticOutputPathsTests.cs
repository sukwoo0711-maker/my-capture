using System.IO;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class DiagnosticOutputPathsTests
{
    [Fact]
    public void ExplicitRelativeRootAndFixedArtifactsRemainAtRequestedLocation()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        try
        {
            string expected = Path.Combine(owner, "chosen", "reports");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, expected);
            string actual = DiagnosticOutputPaths.Create(relative);
            Assert.Equal(expected, actual);
            string report = DiagnosticOutputPaths.Child(actual, "recording-performance-report.txt");
            string clip = DiagnosticOutputPaths.Child(actual, "sustained-320.mp4");
            string sidecar = DiagnosticOutputPaths.Child(actual, Path.GetFileName(clip) + ".json");
            File.WriteAllText(report, "RESULT: PASS");
            File.WriteAllText(sidecar, "{}");
            Assert.Equal(Path.Combine(expected, "recording-performance-report.txt"), report);
            Assert.Equal(Path.Combine(expected, "sustained-320.mp4.json"), sidecar);
            Assert.Equal("RESULT: PASS", File.ReadAllText(report));
            Assert.Equal(actual, DiagnosticOutputPaths.Create(actual + Path.DirectorySeparatorChar));
        }
        finally { OwnedTestDirectory.Delete(owner); }
    }

    [Theory]
    [InlineData("../report.txt")]
    [InlineData(@"..\report.txt")]
    [InlineData(@"C:\report.txt")]
    [InlineData("report.txt:stream")]
    [InlineData("..")]
    [InlineData("report.txt.")]
    [InlineData("report.txt ")]
    public void ArtifactRejectsTraversalAndAmbiguousNames(string name)
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        try { Assert.Throws<IOException>(() => DiagnosticOutputPaths.Child(owner, name)); }
        finally { OwnedTestDirectory.Delete(owner); }
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\reports")]
    [InlineData(@"\\?\C:\reports")]
    public void RootRejectsDriveNetworkAndDevicePaths(string root) =>
        Assert.Throws<IOException>(() => DiagnosticOutputPaths.Create(root));

    [Fact]
    public void MissingRootBelowJunctionIsRejectedBeforeCreation()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        string target = OwnedTestDirectory.Create("mycapture-diagnostics-target-");
        string link = Path.Combine(owner, "linked");
        try
        {
            UpdatePathsTests.CreateJunction(link, target);
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.Create(Path.Combine(link, "must-not-exist", "reports")));
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.Child(link, "report.json"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            OwnedTestDirectory.Delete(owner);
            OwnedTestDirectory.Delete(target);
        }
    }

    [Fact]
    public void ExistingLinkedArtifactIsRejectedAndTargetPreserved()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        string target = OwnedTestDirectory.Create("mycapture-diagnostics-target-");
        string link = Path.Combine(owner, "recording-performance.json");
        string keep = Path.Combine(target, "keep.txt");
        try
        {
            File.WriteAllText(keep, "unrelated");
            UpdatePathsTests.CreateJunction(link, target);
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.Child(owner, "recording-performance.json"));
            Assert.Equal("unrelated", File.ReadAllText(keep));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            OwnedTestDirectory.Delete(owner);
            OwnedTestDirectory.Delete(target);
        }
    }
}
