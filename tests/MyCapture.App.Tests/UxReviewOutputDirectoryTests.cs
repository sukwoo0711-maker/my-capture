using System.IO;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UxReviewOutputDirectoryTests
{
    [Fact]
    public void WorkspaceEvidenceChild_IsCanonicalAndAllowed()
    {
        string input = Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "old", "..", "ux-review");
        string expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ux-review"))
            + Path.DirectorySeparatorChar;
        Assert.Equal(expected, UxReviewOutputDirectory.Resolve(input));
    }

    [Fact]
    public void DefaultTemporaryRoot_AndItsChildren_AreAllowed()
    {
        string root = Path.Combine(Path.GetTempPath(), "mycapture-ux-review");
        Assert.Equal(Path.GetFullPath(root) + Path.DirectorySeparatorChar, UxReviewOutputDirectory.Resolve(root));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "run-1")) + Path.DirectorySeparatorChar,
            UxReviewOutputDirectory.Resolve(Path.Combine(root, "run-1")));
    }

    [Fact]
    public void WindowsRootComparison_IsCaseInsensitive()
    {
        string input = Path.Combine(Environment.CurrentDirectory, "ARTIFACTS", "VALIDATION", "ux-case");
        Assert.Equal(Path.GetFullPath(input) + Path.DirectorySeparatorChar, UxReviewOutputDirectory.Resolve(input));
    }

    [Theory]
    [InlineData("..", "outside")]
    [InlineData("..", "validation-other", "lookalike")]
    [InlineData("..", "..", "..", "escape")]
    public void TraversalOutsideWorkspaceBoundary_IsRejected(params string[] segments)
    {
        string input = Path.Combine([Environment.CurrentDirectory, "artifacts", "validation", .. segments]);
        Assert.Throws<ArgumentException>(() => UxReviewOutputDirectory.Resolve(input));
    }

    [Fact]
    public void TemporarySiblingPrefix_IsRejected()
    {
        string input = Path.Combine(Path.GetTempPath(), "mycapture-ux-review-other", "lookalike");
        Assert.Throws<ArgumentException>(() => UxReviewOutputDirectory.Resolve(input));
    }

    [Theory]
    [InlineData(@"\\server\share\diagnostics")]
    [InlineData(@"\\?\C:\diagnostics")]
    [InlineData(@"C:\Windows\diagnostics")]
    public void NetworkDeviceAndUnrelatedAbsoluteRoots_AreRejected(string input) =>
        Assert.Throws<ArgumentException>(() => UxReviewOutputDirectory.Resolve(input));

    [Fact]
    public void InvalidRun_ReturnsFailureWithoutCreatingOutputOrFallbackReport()
    {
        string outside = Path.Combine(Path.GetTempPath(), "mycapture-invalid-output-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(outside));
        Assert.Equal(2, UxReviewSelfTest.Run(outside));
        Assert.False(Directory.Exists(outside));
    }
}
