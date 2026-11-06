using System.IO;
using System.Security.Cryptography;
using MyCapture.App.Diagnostics;
using Xunit;
using Xunit.Sdk;

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
    [InlineData(@"\\.\C:\reports")]
    public void RootRejectsDriveNetworkAndDevicePaths(string root)
    {
        // Every output and input form fails on the path text alone, before any filesystem access.
        Assert.Throws<IOException>(() => DiagnosticOutputPaths.Create(root));
        Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingDirectory(root));
        Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingFile(root, "report.json"));
        Assert.Throws<IOException>(() => TestDiagnosticArtifacts.ExistingFile(Path.Combine(root, "report.json")));
        Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(Path.Combine(root, "fresh")));
    }

    [Theory]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.Fixed, true)]
    [InlineData(DriveType.Removable, true)]
    public void MappedNetworkDriveIsNotALocalDiagnosticRoot(DriveType type, bool local) =>
        Assert.Equal(local, DiagnosticOutputPaths.IsLocalDriveType(type));

    [Fact]
    public void ExistingInputsAreValidatedWithoutCreatingAnything()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        try
        {
            string input = Path.Combine(owner, "input");
            string report = Path.Combine(input, "report.json");
            Directory.CreateDirectory(Path.Combine(input, "nested.json"));
            File.WriteAllText(report, "{}");
            string[] before = Entries(owner);

            Assert.Equal(input, DiagnosticOutputPaths.ExistingDirectory(input + Path.DirectorySeparatorChar));
            Assert.Equal(input, DiagnosticOutputPaths.ExistingDirectory(Path.GetRelativePath(Environment.CurrentDirectory, input)));
            Assert.Equal(report, DiagnosticOutputPaths.ExistingFile(input, "report.json"));
            Assert.Equal(report, TestDiagnosticArtifacts.ExistingFile(report));
            Assert.Equal(report, TestDiagnosticArtifacts.ExistingFile(Path.Combine(input, "nested.json", "..", "report.json")));

            // Missing inputs are refused, never created; a directory is not an input file and a file
            // is not an input directory; traversal names cannot leave the input directory.
            Assert.Throws<DirectoryNotFoundException>(() => DiagnosticOutputPaths.ExistingDirectory(Path.Combine(owner, "missing")));
            Assert.Throws<FileNotFoundException>(() => DiagnosticOutputPaths.ExistingFile(input, "source.mp4"));
            Assert.Throws<DirectoryNotFoundException>(() => DiagnosticOutputPaths.ExistingFile(Path.Combine(owner, "missing"), "report.json"));
            Assert.Throws<FileNotFoundException>(() => TestDiagnosticArtifacts.ExistingFile(Path.Combine(input, "source.mp4")));
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingFile(input, "nested.json"));
            Assert.Throws<DirectoryNotFoundException>(() => DiagnosticOutputPaths.ExistingDirectory(report));
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingFile(Path.Combine(input, "nested.json"), @"..\report.json"));
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingFile(input, ".."));
            Assert.Equal(before, Entries(owner));
            Assert.Equal("{}", File.ReadAllText(report));
        }
        finally { OwnedTestDirectory.Delete(owner); }
    }

    [Fact]
    public void ExistingInputsAndNewOutputsThroughJunctionAreRejectedAndTargetPreserved()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        string target = OwnedTestDirectory.Create("mycapture-diagnostics-target-");
        string link = Path.Combine(owner, "linked");
        string keep = Path.Combine(target, "report.json");
        try
        {
            File.WriteAllText(keep, "unrelated");
            Directory.CreateDirectory(Path.Combine(target, "inner"));
            UpdatePathsTests.CreateJunction(link, target);
            string[] before = Entries(target);

            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingDirectory(link));
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingDirectory(Path.Combine(link, "inner")));
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingFile(link, "report.json"));
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingFile(owner, "linked"));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.ExistingFile(Path.Combine(link, "report.json")));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(Path.Combine(link, "fresh")));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(Path.Combine(link, "inner", "fresh"), link));
            Assert.Equal(before, Entries(target));
            Assert.Equal("unrelated", File.ReadAllText(keep));
        }
        finally
        {
            if (Directory.Exists(link)) TestRecycleBin.DeleteDirectory(link);
            OwnedTestDirectory.Delete(owner);
            OwnedTestDirectory.Delete(target);
        }
    }

    [WindowsFileSymlinkFact]
    public void ExistingInputFileSymlinkIsRejectedAndTargetPreserved()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        string target = OwnedTestDirectory.Create("mycapture-diagnostics-target-");
        string keep = Path.Combine(target, "source.mp4");
        string link = Path.Combine(owner, "source.mp4");
        try
        {
            File.WriteAllText(keep, "unrelated");
            File.CreateSymbolicLink(link, keep);
            Assert.Throws<IOException>(() => DiagnosticOutputPaths.ExistingFile(owner, "source.mp4"));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.ExistingFile(link));
            Assert.Equal("unrelated", File.ReadAllText(keep));
        }
        finally
        {
            if (File.Exists(link)) TestRecycleBin.DeleteFile(link);
            OwnedTestDirectory.Delete(owner);
            OwnedTestDirectory.Delete(target);
        }
    }

    [Fact]
    public void NewDirectoryCreatesOnlyAFreshChildOfItsRequiredRoot()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        try
        {
            string artifacts = Path.Combine(owner, "artifacts");
            string existing = Path.Combine(artifacts, "existing");
            string occupied = Path.Combine(artifacts, "occupied");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.json"), "kept");
            File.WriteAllText(occupied, "kept file");
            string fresh = Path.Combine(artifacts, "fresh", "run");
            Assert.Equal(fresh, TestDiagnosticArtifacts.NewDirectory(fresh, artifacts));
            Assert.True(Directory.Exists(fresh));
            string[] before = Entries(owner);

            // Existing evidence is preserved and rejected; a new output must stay strictly below its root.
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(existing, artifacts));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(occupied));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(artifacts, artifacts));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(Path.Combine(owner, "elsewhere"), artifacts));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(Path.Combine(artifacts, "..", "escape"), artifacts));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.NewDirectory(Path.Combine(owner, "artifacts-sibling", "run"), artifacts));
            Assert.Equal(before, Entries(owner));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(existing, "keep.json")));
            Assert.Equal("kept file", File.ReadAllText(occupied));
        }
        finally { OwnedTestDirectory.Delete(owner); }
    }

    [Fact]
    public void PinnedInputHashMismatchFailsBeforeItsBytesAreParsed()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        try
        {
            byte[] content = "not an mp4"u8.ToArray();
            string path = Path.Combine(owner, "source.mp4");
            File.WriteAllBytes(path, content);
            string hash = Convert.ToHexString(SHA256.HashData(content));

            Assert.Equal(content, TestDiagnosticArtifacts.ReadVerified(path, hash));
            // Parsing these bytes fails, so the hash failure below proves the check ran first.
            Assert.Throws<InvalidDataException>(() => Mp4SampleTable.Parse(TestDiagnosticArtifacts.ReadVerified(path, hash)));
            Assert.Throws<EqualException>(() => Mp4SampleTable.Parse(TestDiagnosticArtifacts.ReadVerified(path, new string('0', 64))));
        }
        finally { OwnedTestDirectory.Delete(owner); }
    }

    [Fact]
    public void RetainedCopyIsNewAndVerified()
    {
        string owner = OwnedTestDirectory.Create("mycapture-diagnostics-");
        try
        {
            byte[] content = [0, 1, 2, 254, 255];
            string hash = Convert.ToHexString(SHA256.HashData(content));
            string retained = TestDiagnosticArtifacts.Retain(owner, "source.mp4", content, hash);
            Assert.Equal(Path.Combine(owner, "source.mp4"), retained);
            Assert.Equal(content, File.ReadAllBytes(retained));

            // An existing copy is never replaced, and unverified bytes are never written.
            byte[] other = [9, 9];
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.Retain(owner, "source.mp4", other, Convert.ToHexString(SHA256.HashData(other))));
            Assert.Equal(content, File.ReadAllBytes(retained));
            Assert.Throws<EqualException>(() => TestDiagnosticArtifacts.Retain(owner, "other.mp4", content, new string('0', 64)));
            Assert.False(File.Exists(Path.Combine(owner, "other.mp4")));
            Assert.Throws<IOException>(() => TestDiagnosticArtifacts.Retain(owner, @"..\escape.mp4", content, hash));
        }
        finally { OwnedTestDirectory.Delete(owner); }
    }

    private static string[] Entries(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

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
            if (Directory.Exists(link)) TestRecycleBin.DeleteDirectory(link);
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
            if (Directory.Exists(link)) TestRecycleBin.DeleteDirectory(link);
            OwnedTestDirectory.Delete(owner);
            OwnedTestDirectory.Delete(target);
        }
    }
}
