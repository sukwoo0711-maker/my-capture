using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The crash worker receives its fixture root and the child runner's host from the parent. Both are
/// validated before use: the root must be a prepared, link-free direct child of the owned directory,
/// and the runner is the dotnet host of the running runtime, never a path from the environment.
/// </summary>
public sealed partial class StoragePublicationFaultTests
{
    [Fact]
    public void ChildRoot_AcceptsOnlyAnExistingPreparedDirectChild()
    {
        using var workspace = new TempWorkspace();
        string owned = Path.Combine(workspace.Root, "storage-fault-tests");
        string child = PreparedDirectory(Path.Combine(owned, "process-kill-0"));
        string grandchild = PreparedDirectory(Path.Combine(child, "nested"));
        string unprepared = Directory.CreateDirectory(Path.Combine(owned, "unprepared")).FullName;
        string folderReceipt = Directory.CreateDirectory(Path.Combine(owned, "folder-receipt", "prepared.json")).Parent!.FullName;
        File.WriteAllText(Path.Combine(owned, "prepared.json"), "{}");
        string[] before = EntriesBelow(workspace.Root);

        Assert.Equal(child, Fixture.ChildRoot(owned, child));
        Assert.Equal(child, Fixture.ChildRoot(owned + Path.DirectorySeparatorChar, child + Path.DirectorySeparatorChar));
        Assert.Throws<InvalidOperationException>(() => Fixture.ChildRoot(owned, Path.Combine(owned, "missing")));
        Assert.Throws<InvalidOperationException>(() => Fixture.ChildRoot(owned, unprepared));
        Assert.Throws<InvalidOperationException>(() => Fixture.ChildRoot(owned, grandchild));
        Assert.Throws<IOException>(() => Fixture.ChildRoot(owned, folderReceipt));
        Assert.Throws<IOException>(() => Fixture.ChildRoot(owned, owned));
        // Validation reads attributes only: nothing is created, including the missing child.
        Assert.Equal(before, EntriesBelow(workspace.Root));
    }

    [Fact]
    public void ChildRoot_RejectsEscapesDeviceNetworkAndDriveRootPathsBeforeReadingThem()
    {
        using var workspace = new TempWorkspace();
        string owned = Path.Combine(workspace.Root, "storage-fault-tests");
        string child = PreparedDirectory(Path.Combine(owned, "process-kill-0"));
        string outside = PreparedDirectory(Path.Combine(workspace.Root, "outside"));
        string sibling = PreparedDirectory(owned + "-sibling");
        string drive = Path.GetPathRoot(child)!;
        string[] before = EntriesBelow(workspace.Root);

        foreach (string requested in new[]
        {
            Path.Combine(owned, "..", "outside"),
            outside,
            sibling,
            drive,
            drive[..2] + "process-kill-0",
            @"\\server\share\storage-fault-tests\process-kill-0",
            // The device and administrative-share spellings of the accepted child are refused too.
            @"\\localhost\" + drive[0] + "$" + child[2..],
            @"\\?\" + child,
            @"\\.\" + child,
            @"\\?\" + outside,
        })
            Assert.Throws<IOException>(() => Fixture.ChildRoot(owned, requested));
        Assert.Equal(before, EntriesBelow(workspace.Root));
    }

    [Fact]
    public void ChildRoot_RejectsALinkedChildOrOwnedRootWithoutUsingItsTarget()
    {
        using var workspace = new TempWorkspace();
        string owned = Directory.CreateDirectory(Path.Combine(workspace.Root, "storage-fault-tests")).FullName;
        string outside = PreparedDirectory(Path.Combine(workspace.Root, "outside"));
        PreparedDirectory(Path.Combine(outside, "nested"));
        string linked = Path.Combine(owned, "linked");
        try
        {
            CreateDirectoryJunction(linked, outside);
            string[] before = EntriesBelow(outside);
            Assert.Throws<IOException>(() => Fixture.ChildRoot(owned, linked));
            Assert.Throws<IOException>(() => Fixture.ChildRoot(owned, Path.Combine(linked, "nested")));
            Assert.Throws<IOException>(() => Fixture.ChildRoot(linked, Path.Combine(linked, "nested")));
            Assert.Equal(before, EntriesBelow(outside));
        }
        finally
        {
            if (Directory.Exists(linked)) TestRecycleBin.DeleteDirectory(linked);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0\", @"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0", @"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"D:\tools\dotnet\shared\microsoft.netcore.app\10.0.1\", @"D:\tools\dotnet\dotnet.exe")]
    [InlineData(@"C:\app\bin\", null)]
    [InlineData(@"C:\dotnet\shared\Microsoft.AspNetCore.App\10.0.0\", null)]
    [InlineData(@"C:\dotnet\other\Microsoft.NETCore.App\10.0.0\", null)]
    [InlineData(@"C:\Microsoft.NETCore.App\10.0.0\", null)]
    public void ChildDotnetHost_IsDerivedOnlyFromASharedRuntimeLayout(string runtimeDirectory, string? expected) =>
        Assert.Equal(expected, DotnetHostCandidate(runtimeDirectory));

    [Fact]
    public void ChildDotnetHost_IsTheRunningRuntimeHostAndTheOverrideCannotReplaceIt()
    {
        string host = DotnetHostCandidate(RuntimeEnvironment.GetRuntimeDirectory())
            ?? throw new InvalidOperationException("These tests run on a shared runtime beside its dotnet host.");
        Assert.True(File.Exists(host), host);
        Assert.Equal(host, ChildDotnetHost(host, null));
        Assert.Equal(host, ChildDotnetHost(host, " "));
        Assert.Equal(host, ChildDotnetHost(host, host.ToUpperInvariant()));
        Assert.Throws<InvalidOperationException>(() => ChildDotnetHost(host, @"C:\elsewhere\dotnet.exe"));
        Assert.Throws<InvalidOperationException>(() => ChildDotnetHost(host, "dotnet"));
        // Without a host beside the runtime the previous PATH lookup remains, and still cannot be overridden.
        string missing = Path.Combine(Path.GetDirectoryName(host)!, "missing-host", "dotnet.exe");
        Assert.Equal("dotnet", ChildDotnetHost(missing, null));
        Assert.Equal("dotnet", ChildDotnetHost(null, null));
        Assert.Throws<InvalidOperationException>(() => ChildDotnetHost(missing, host));
    }

    private static string PreparedDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "prepared.json"), "{\"status\":\"PREPARED\"}");
        return directory;
    }

    private static string[] EntriesBelow(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private static void CreateDirectoryJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        // Junctions need no privilege; a failure here is a real failure, never a skip.
        if (process.ExitCode != 0) throw new InvalidOperationException("mklink /J failed: " + output);
        Assert.NotEqual(0, (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint));
    }
}
