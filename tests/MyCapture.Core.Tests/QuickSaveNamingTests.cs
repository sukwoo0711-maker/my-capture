using System.Globalization;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Quick-save filename expansion and collision handling.
/// </summary>
public sealed class QuickSaveNamingTests
{
    private static readonly DateTimeOffset Sample =
        new(2026, 8, 29, 13, 53, 12, TimeSpan.Zero);

    [Fact]
    public void BuildStem_ExpandsDateTokens()
    {
        string stem = QuickSaveNaming.BuildStem("capture_{yyyyMMdd}_{HHmmss}", Sample);
        Assert.Equal("capture_20260829_135312", stem);
    }

    [Fact]
    public void BuildStem_ExpandsIndividualDateComponents()
    {
        // Each brace group is a .NET date/time format applied to the capture time, so a
        // user can compose any ordering they like from the components.
        string stem = QuickSaveNaming.BuildStem("{yyyy}-{MM}-{dd}", Sample);
        Assert.Equal("2026-08-29", stem);
    }

    [Fact]
    public void BuildStem_KeepsLiteralTextOutsideTokens()
    {
        string stem = QuickSaveNaming.BuildStem("screenshot-{yyyy}", Sample);
        Assert.Equal("screenshot-2026", stem);
    }

    [Fact]
    public void BuildStem_SanitisesIllegalCharacters()
    {
        string stem = QuickSaveNaming.BuildStem("a/b:c*d", Sample);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(invalid, stem);
        }
    }

    [Fact]
    public void BuildStem_FallsBackWhenPatternIsEmpty()
    {
        Assert.Equal("capture_20260829_135312", QuickSaveNaming.BuildStem("", Sample));
    }

    [Fact]
    public void ResolvePath_ReturnsPlainNameWhenNoCollision()
    {
        using var workspace = new TempWorkspace();
        string path = QuickSaveNaming.ResolvePath(workspace.Root, "shot", ".png");
        Assert.Equal(Path.Combine(workspace.Root, "shot.png"), path);
    }

    [Fact]
    public void ResolvePath_AddsNumericSuffixOnCollision()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(Path.Combine(workspace.Root, "shot.png"), []);
        File.WriteAllBytes(Path.Combine(workspace.Root, "shot-2.png"), []);

        string path = QuickSaveNaming.ResolvePath(workspace.Root, "shot", ".png");

        Assert.Equal(Path.Combine(workspace.Root, "shot-3.png"), path);
    }

    [Fact]
    public void ResolvePath_NormalisesExtensionWithoutLeadingDot()
    {
        using var workspace = new TempWorkspace();
        string path = QuickSaveNaming.ResolvePath(workspace.Root, "shot", "png");
        Assert.EndsWith(".png", path);
        Assert.DoesNotContain("..png", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteCollisionFreeExport_ConcurrentWritersNeverOverwrite()
    {
        using var workspace = new TempWorkspace();
        byte[] contents = [0x89, 0x50, 0x4E, 0x47];

        Task<string>[] writes = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => QuickSaveNaming.WriteCollisionFreeExport(
                workspace.Root,
                "shot",
                ".png",
                contents)))
            .ToArray();

        string[] paths = await Task.WhenAll(writes);

        Assert.Equal(16, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(16, Directory.EnumerateFiles(workspace.Root, "*.png").Count());
        Assert.All(paths, path => Assert.Equal(contents, File.ReadAllBytes(path)));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp"));
    }
}
