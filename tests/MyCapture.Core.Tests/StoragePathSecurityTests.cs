using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class StoragePathSecurityTests
{
    [Fact]
    public void CaptureQueue_RejectsDirectoriesOutsideCaptureRoot()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        string rootedEscape = Path.Combine(Path.GetPathRoot(workspace.Root)!, "mycapture-escape");

        string caseChangedRoot = Path.Combine(
            "..",
            Path.GetFileName(workspace.Paths.CapturesRoot).ToUpperInvariant(),
            "escape");
        foreach (string relativeDirectory in new[]
                 {
                     @"..\escape",
                     @"..\..\escape",
                     rootedEscape,
                     caseChangedRoot,
                     ".",
                 })
        {
            var record = new CaptureRecord { RelativeDirectory = relativeDirectory };

            Assert.Throws<ArgumentException>(() => queue.Add(record));
            Assert.Throws<InvalidDataException>(() => queue.GetDirectory(record));
        }
    }

    [Fact]
    public void CaptureQueue_LoadDropsEscapingIndexRecordWithoutTouchingOutsideFile()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();

        string outsideDirectory = Path.Combine(workspace.Root, "outside-capture");
        Directory.CreateDirectory(outsideDirectory);
        string sentinel = Path.Combine(outsideDirectory, CaptureFileNames.Original);
        File.WriteAllBytes(sentinel, [0x4D, 0x43]);

        var record = new CaptureRecord
        {
            RelativeDirectory = @"..\outside-capture",
            Width = 1,
            Height = 1,
            TotalBytes = 2,
        };
        string index = JsonSerializer.Serialize(
            new { schemaVersion = 1, records = new[] { record } },
            JsonDefaults.Compact);
        File.WriteAllText(paths.IndexFile, index);

        CaptureQueue queue = NewQueue(workspace);
        queue.Load();

        Assert.Empty(queue.Records);
        Assert.Equal(new byte[] { 0x4D, 0x43 }, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void CaptureQueue_GetFilePathRejectsDirectoryComponents()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        var record = new CaptureRecord();
        queue.Add(record);

        Assert.Throws<ArgumentException>(() => queue.GetFilePath(record, @"..\outside.png"));
        Assert.Throws<ArgumentException>(() => queue.GetFilePath(record, workspace.Root));
        Assert.Throws<ArgumentException>(() => queue.GetFilePath(record, ".."));
        Assert.Throws<ArgumentException>(() => queue.GetFilePath(record, "meta.json:stream"));
    }

    [Fact]
    public void CaptureQueue_RejectsExistingReparsePointBelowCaptureRoot()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();

        string outside = Path.Combine(workspace.Root, "outside-capture");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, CaptureFileNames.Original), [0x4D, 0x43]);

        string link = Path.Combine(paths.CapturesRoot, "linked-capture");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Unprivileged symbolic links are commonly disabled on managed Windows machines;
            // directory junctions exercise the same reparse-point boundary without elevation.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{link}\" \"{outside}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            Assert.NotNull(process);
            string standardOutput = process!.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"Could not create the junction fixture. stdout={standardOutput} stderr={standardError}");
        }

        try
        {
            Assert.NotEqual(
                0,
                (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint));

            CaptureQueue queue = NewQueue(workspace);
            var record = new CaptureRecord { RelativeDirectory = "linked-capture" };

            Assert.Throws<ArgumentException>(() => queue.Add(record));
            Assert.Throws<InvalidDataException>(() => queue.GetDirectory(record));

            string index = JsonSerializer.Serialize(
                new { schemaVersion = 1, records = new[] { record } },
                JsonDefaults.Compact);
            File.WriteAllText(paths.IndexFile, index);
            queue.Load();

            Assert.Empty(queue.Records);
            Assert.Equal(
                new byte[] { 0x4D, 0x43 },
                File.ReadAllBytes(Path.Combine(outside, CaptureFileNames.Original)));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Theory]
    [InlineData("asset-01.png", true)]
    [InlineData("asset-999.PNG", true)]
    [InlineData("asset-.png", false)]
    [InlineData("asset-name.png", false)]
    [InlineData("../asset-01.png", false)]
    [InlineData("C:\\asset-01.png", false)]
    public void AssetFileName_AllowsOnlyCanonicalLeafNames(string value, bool expected) =>
        Assert.Equal(expected, CaptureFileNames.IsSafeAssetFileName(value));

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/escape")]
    [InlineData("folder\\escape")]
    public void QuickSave_RejectsRawStemWithDirectoryComponents(string stem)
    {
        using var workspace = new TempWorkspace();

        Assert.Throws<ArgumentException>(() =>
            QuickSaveNaming.ResolvePath(workspace.Root, stem, ".png"));
        Assert.Throws<ArgumentException>(() =>
            QuickSaveNaming.WriteCollisionFreeExport(workspace.Root, stem, ".png", [0x01]));
    }

    [Theory]
    [InlineData("../png")]
    [InlineData(".png/escape")]
    [InlineData(".png\\escape")]
    public void QuickSave_RejectsExtensionWithDirectoryComponents(string extension)
    {
        using var workspace = new TempWorkspace();

        Assert.Throws<ArgumentException>(() =>
            QuickSaveNaming.ResolvePath(workspace.Root, "capture", extension));
    }

    private static CaptureQueue NewQueue(TempWorkspace workspace) =>
        new(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
}
