using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using MyCapture.Core.Queue;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// <see cref="ParallelFileScan.FindFiles"/> must return exactly what the recursive
/// <c>Directory.EnumerateFiles</c> it replaces at startup returns, in the same order.
/// </summary>
public sealed class ParallelFileScanTests
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly string[] SidecarNames =
        [CaptureFileNames.OriginalPending, CaptureFileNames.VideoPending, CaptureFileNames.Meta];

    [Fact]
    public void FindFiles_ReturnsTheRecursiveEnumerationOfEachName()
    {
        using var workspace = new TempWorkspace();
        string root = workspace.Root;
        BuildLibraryTree(root);

        IReadOnlyList<string>[] found = AssertSameAsRecursiveEnumeration(root, SidecarNames);
        AssertSameAsRecursiveEnumeration(root + Path.DirectorySeparatorChar, SidecarNames);
        AssertSameAsRecursiveEnumeration(root, [CaptureFileNames.Meta, CaptureFileNames.OriginalPending]);

        IReadOnlyList<string> meta = found[2];
        Assert.True(meta.Count >= 50, $"The fixture should find many meta files, found {meta.Count}.");
        // Breadth-first: a file one level down in a later month precedes the captures of the first.
        int shallow = IndexOf(meta, Path.Combine(root, "2026-03", "meta.json"));
        int deep = IndexOf(meta, Path.Combine(root, "2026-01", "capture-00", "meta.json"));
        Assert.True(shallow < deep, $"Expected breadth-first order ({shallow} < {deep}).");
        // Hidden and system entries are not skipped, case differences match, and stage
        // directories and a directory that carries a searched name are walked into.
        Assert.Contains(Path.Combine(root, "2026-02", "capture-04", "meta.json"), meta);
        Assert.Contains(Path.Combine(root, "2026-02", ".hidden", "meta.json"), meta);
        Assert.Contains(Path.Combine(root, "2026-01", "capture-06", "META.JSON"), meta);
        Assert.Contains(Path.Combine(root, "2026-04", "capture-03", ".stage-0403", "meta.json"), meta);
        Assert.Contains(Path.Combine(root, "2026-02", "capture-05", "meta.json", "meta.json"), meta);
        Assert.DoesNotContain(Path.Combine(root, "2026-02", "capture-05", "meta.json"), meta);
        Assert.Contains(
            Path.Combine(root, "2026-01", "capture-03", ".stage-0103", "nested", CaptureFileNames.VideoPending),
            found[1]);
    }

    [Fact]
    public void FindFiles_SkipsAnInaccessibleDirectoryLikeTheRecursiveEnumeration()
    {
        using var workspace = new TempWorkspace();
        string root = workspace.Root;
        BuildLibraryTree(root);
        var locked = new DirectoryInfo(Path.Combine(root, "2026-02", "locked"));
        locked.Create();
        Write(locked.FullName, CaptureFileNames.Meta);
        Write(Directory.CreateDirectory(Path.Combine(locked.FullName, "inner")).FullName, CaptureFileNames.Meta);

        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test needs a user identity.");
        var deny = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        DirectorySecurity security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            bool denied;
            try
            {
                _ = Directory.EnumerateFileSystemEntries(locked.FullName).ToList();
                denied = false;
            }
            catch (UnauthorizedAccessException)
            {
                denied = true;
            }

            IReadOnlyList<string>[] found = AssertSameAsRecursiveEnumeration(root, SidecarNames);
            bool listsLocked = found[2].Any(path =>
                path.StartsWith(locked.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(!denied, listsLocked);
            // An inaccessible root is empty for both, not an error.
            AssertSameAsRecursiveEnumeration(locked.FullName, SidecarNames);
        }
        finally
        {
            DirectorySecurity restored = locked.GetAccessControl();
            restored.RemoveAccessRuleSpecific(deny);
            locked.SetAccessControl(restored);
        }
    }

    [Fact]
    public void FindFiles_DoesNotEnterAJunctionLikeTheRecursiveEnumeration()
    {
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        string root = workspace.Root;
        BuildLibraryTree(root);
        Write(outside.Root, CaptureFileNames.Meta);
        string link = Path.Combine(root, "2026-01", "linked");
        if (!TryCreateJunction(link, outside.Root))
        {
            // mklink is unavailable here; the other cases still compare the walks.
            return;
        }

        try
        {
            Assert.NotEqual(0, (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint));
            IReadOnlyList<string>[] found = AssertSameAsRecursiveEnumeration(root, SidecarNames);
            Assert.DoesNotContain(found[2], path =>
                path.StartsWith(link + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                TestRecycleBin.DeleteDirectory(link);
            }
        }
    }

    [Fact]
    public void FindFiles_ListsADirectoryWhoseNameWin32PathsWouldRewrite()
    {
        using var workspace = new TempWorkspace();
        string root = workspace.Root;
        BuildLibraryTree(root);
        string month = Path.Combine(root, "2026-02");
        string odd = Path.Combine(month, "trailing-dot.");
        Directory.CreateDirectory(@"\\?\" + Path.Combine(odd, "inner"));
        File.WriteAllText(@"\\?\" + Path.Combine(odd, CaptureFileNames.Meta), "{}");
        File.WriteAllText(@"\\?\" + Path.Combine(odd, "inner", CaptureFileNames.Meta), "{}");
        Write(Directory.CreateDirectory(Path.Combine(month, "trailing-dot")).FullName, CaptureFileNames.OriginalPending);
        try
        {
            IReadOnlyList<string>[] found = AssertSameAsRecursiveEnumeration(root, SidecarNames);
            Assert.Contains(Path.Combine(odd, "inner", CaptureFileNames.Meta), found[2]);
        }
        finally
        {
            // A plain name lets the workspace cleanup remove it.
            if (Directory.Exists(@"\\?\" + odd))
            {
                Directory.Move(@"\\?\" + odd, @"\\?\" + Path.Combine(month, "trailing-dot-renamed"));
            }
        }
    }

    [Fact]
    public void FindFiles_FailsForAMissingRootLikeTheRecursiveEnumeration()
    {
        using var workspace = new TempWorkspace();
        string missing = Path.Combine(workspace.Root, "missing");

        Assert.Throws<DirectoryNotFoundException>(() =>
            Directory.EnumerateFiles(missing, CaptureFileNames.Meta, Recursive).ToList());
        Assert.Throws<DirectoryNotFoundException>(() => ParallelFileScan.FindFiles(missing, CaptureFileNames.Meta));
    }

    [Theory]
    [InlineData("*.json")]
    [InlineData("meta.?son")]
    [InlineData(@"2026-01\meta.json")]
    [InlineData("")]
    public void FindFiles_AcceptsOnlyExactFileNames(string fileName)
    {
        using var workspace = new TempWorkspace();
        Assert.ThrowsAny<ArgumentException>(() => ParallelFileScan.FindFiles(workspace.Root, fileName));
    }

    [Fact]
    public void InspectEach_KeepsItemOrderAndRethrowsEachItemsOwnException()
    {
        int[] items = Enumerable.Range(0, 500).ToArray();

        ParallelOutcome<int>[] outcomes = ParallelFileScan.InspectEach(
            items,
            item => item % 97 == 13 ? throw new InvalidDataException($"item {item}") : item * 2);

        Assert.Equal(items.Length, outcomes.Length);
        for (int i = 0; i < items.Length; i++)
        {
            if (i % 97 == 13)
            {
                int item = i;
                InvalidDataException failure = Assert.Throws<InvalidDataException>(() => outcomes[item].GetValue());
                Assert.Equal($"item {item}", failure.Message);
            }
            else
            {
                Assert.Equal(i * 2, outcomes[i].GetValue());
            }
        }
    }

    private static IReadOnlyList<string>[] AssertSameAsRecursiveEnumeration(string root, string[] names)
    {
        IReadOnlyList<string>[] found = ParallelFileScan.FindFiles(root, names);
        Assert.Equal(names.Length, found.Length);
        for (int i = 0; i < names.Length; i++)
        {
            List<string> expected = Directory.EnumerateFiles(root, names[i], Recursive).ToList();
            Assert.Equal(expected, found[i]);
        }

        return found;
    }

    /// <summary>
    /// A small library: files of the searched names at depths 0 to 4, stage and cleanup
    /// directories, hidden and system entries, a name in other casing and decoy names.
    /// </summary>
    private static void BuildLibraryTree(string root)
    {
        Write(root, CaptureFileNames.Meta);
        Write(root, "index.json");
        for (int month = 1; month <= 4; month++)
        {
            string monthDirectory = Directory.CreateDirectory(Path.Combine(root, $"2026-{month:00}")).FullName;
            if (month == 3)
            {
                Write(monthDirectory, CaptureFileNames.Meta);
            }

            for (int capture = 0; capture < 12; capture++)
            {
                string directory = Directory.CreateDirectory(Path.Combine(monthDirectory, $"capture-{capture:00}")).FullName;
                Write(directory, CaptureFileNames.Original);
                Write(directory, "meta.json.bak");
                Write(directory, "xmeta.json");
                if (month == 2 && capture == 5)
                {
                    // A directory carrying a searched name is walked into, never returned.
                    Write(Directory.CreateDirectory(Path.Combine(directory, CaptureFileNames.Meta)).FullName,
                        CaptureFileNames.Meta);
                }
                else
                {
                    Write(directory, month == 1 && capture == 6 ? "META.JSON" : CaptureFileNames.Meta);
                }
                if (capture % 4 == 1)
                {
                    Write(directory, CaptureFileNames.OriginalPending);
                }

                if (capture % 5 == 2)
                {
                    Write(directory, CaptureFileNames.VideoPending);
                }

                if (capture % 6 == 3)
                {
                    string stage = Directory.CreateDirectory(Path.Combine(directory, $".stage-{month:00}{capture:00}")).FullName;
                    Write(stage, CaptureFileNames.Meta);
                    Write(stage, CaptureFileNames.Original);
                    Write(Directory.CreateDirectory(Path.Combine(stage, "nested")).FullName, CaptureFileNames.VideoPending);
                }

                if (capture % 7 == 4)
                {
                    Write(Directory.CreateDirectory(Path.Combine(directory, $".cleanup-{month:00}{capture:00}")).FullName,
                        CaptureFileNames.OriginalPending);
                }
            }
        }

        string hiddenFile = Path.Combine(root, "2026-02", "capture-04", CaptureFileNames.Meta);
        File.SetAttributes(hiddenFile, FileAttributes.Hidden | FileAttributes.System);
        var hiddenDirectory = Directory.CreateDirectory(Path.Combine(root, "2026-02", ".hidden"));
        Write(hiddenDirectory.FullName, CaptureFileNames.Meta);
        hiddenDirectory.Attributes |= FileAttributes.Hidden | FileAttributes.System;
    }

    private static void Write(string directory, string fileName) =>
        File.WriteAllText(Path.Combine(directory, fileName), "{}");

    private static int IndexOf(IReadOnlyList<string> paths, string path)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (string.Equals(paths[i], path, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"{path} was not found.");
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
