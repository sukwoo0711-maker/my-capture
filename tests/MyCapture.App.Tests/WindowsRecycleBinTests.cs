using System.Diagnostics;
using System.IO;
using MyCapture.Platform.Shell;
using System.Text.Json;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class WindowsRecycleBinTests(ITestOutputHelper output)
{
    [Fact]
    public void ShellComFailure_PreservesNativeCauseWithinIOExceptionContract()
    {
        var native = new System.Runtime.InteropServices.COMException("Locked recycle target", unchecked((int)0x80270027));
        var error = Assert.Throws<IOException>(() => WindowsRecycleBin.RunShellOperation<int>(() => throw native));
        Assert.Same(native, error.InnerException);
        Assert.Equal(unchecked((int)0x80270027), error.InnerException!.HResult);
    }

    [Fact]
    public void ExistingFilesystemAndTimeoutFailures_PreserveTheirOriginalContract()
    {
        var io = new IOException("Retain recovery journal");
        Assert.Same(io, Assert.Throws<IOException>(() => WindowsRecycleBin.RunShellOperation<int>(() => throw io)));
        var timeout = new TimeoutException("Bounded lookup deadline");
        Assert.Same(timeout, Assert.Throws<TimeoutException>(() => WindowsRecycleBin.RunShellOperation<int>(() => throw timeout)));
    }

    [Fact]
    public void RecycleFlags_NeverAskBeforeAPermanentDelete()
    {
        Assert.Equal(0u, WindowsRecycleBin.RecycleFlags & 0x00004000); // FOF_WANTNUKEWARNING
        Assert.NotEqual(0u, WindowsRecycleBin.RecycleFlags & 0x00080000); // FOFX_RECYCLEONDELETE
        Assert.NotEqual(0u, WindowsRecycleBin.RecycleFlags & 0x00000400); // FOF_NOERRORUI
        Assert.Equal(0u, WindowsRecycleBin.RecycleFlags & 0x00000040); // never FOF_ALLOWUNDO's fallback
    }

    [Theory]
    [InlineData(0x00000000u, false)]
    [InlineData(0x00000400u, false)] // TSF_MOVE_AS_COPY_DELETE without recycling
    [InlineData(0x00000080u, true)]  // TSF_DELETE_RECYCLE_IF_POSSIBLE
    [InlineData(0x00000480u, true)]
    public void PreDelete_CancelsEveryDeleteTheShellWouldNotRecycle(uint flags, bool allowed)
    {
        int decision = WindowsRecycleBin.PreDeleteDecision(flags);
        if (allowed) Assert.Equal(0, decision);
        else Assert.Equal(unchecked((int)0x800704C7), decision); // HRESULT_FROM_WIN32(ERROR_CANCELLED)
    }

    [Fact]
    public void RecycleBinUnavailable_IsAnIOExceptionThatDeletedNothing()
    {
        var error = new MyCapture.Core.Storage.RecycleBinUnavailableException();
        Assert.IsAssignableFrom<IOException>(error);
    }

    /// <summary>
    /// The Recycle Bin is only for captures the user deletes from the library: the single
    /// product construction is the library deletion service's wiring in App startup.
    /// </summary>
    [Fact]
    public void ProductCode_UsesTheRecycleBinOnlyForLibraryDeletion()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MyCapture.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var uses = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(item => item.line.Contains("WindowsRecycleBin()", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(root, item.file)}:{item.index + 1}")
            .ToArray();
        string use = Assert.Single(uses);
        Assert.StartsWith(Path.Combine("src", "MyCapture.App", "App.xaml.cs"), use);
        Assert.Contains("new RecoverableLibraryService(", File.ReadAllText(Path.Combine(root, "src", "MyCapture.App", "App.xaml.cs")));
    }

    [RecycleBinCanaryFact]
    public void OwnedDirectory_RoundTrip_ConflictsDoNotOverwrite_AndRestoredShellItemIsUnavailable()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-trash-canary-").FullName;
        string original = Path.Combine(root, "capture");
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "canary.txt"), "recoverable capture canary");
        string originalSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(original, "canary.txt"))));
        File.WriteAllText(Path.Combine(root, "original-sha256.txt"), originalSha);
        var trash = new WindowsRecycleBin();
        trash.DiagnosticStageForTest = stage => File.AppendAllText(Path.Combine(root, "stages.txt"), $"{DateTimeOffset.UtcNow:O} {stage}\n");
        RecycledPathReceipt receipt = trash.RecycleDirectory(original);
        File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(receipt));
        Assert.False(Directory.Exists(original));
        Assert.True(Directory.Exists(receipt.RecycledPath));
        Assert.Equal(receipt.Identity, trash.GetIdentity(receipt.RecycledPath));
        string shellMetadata = Path.Combine(Path.GetDirectoryName(receipt.RecycledPath)!, "$I" + Path.GetFileName(receipt.RecycledPath)[2..]);
        Assert.True(File.Exists(shellMetadata));
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "conflict.txt"), "keep this conflict");
        Assert.Throws<IOException>(() => trash.RestoreDirectory(receipt));
        Assert.Equal("keep this conflict", File.ReadAllText(Path.Combine(original, "conflict.txt")));
        Assert.True(Directory.Exists(receipt.RecycledPath));
        // Preserve the independently created collision rather than deleting it.
        Directory.Move(original, Path.Combine(root, "conflict-preserved"));
        trash.RestoreDirectory(receipt);
        Assert.Equal("recoverable capture canary", File.ReadAllText(Path.Combine(original, "canary.txt")));
        string restoredSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(original, "canary.txt"))));
        File.WriteAllText(Path.Combine(root, "restored-sha256.txt"), restoredSha);
        Assert.Equal(originalSha, restoredSha);
        Assert.Equal(receipt.Identity, trash.GetIdentity(original));
        Assert.False(Directory.Exists(receipt.RecycledPath));
        Assert.Contains("restore:namespace-unavailable", File.ReadAllText(Path.Combine(root, "stages.txt")));
        File.WriteAllText(Path.Combine(root, "shell-final-state.json"), JsonSerializer.Serialize(new { MetadataExists = File.Exists(shellMetadata), PayloadExists = Directory.Exists(receipt.RecycledPath) }));
    }

    [RecycleBinCanaryFact]
    public void OwnedFile_RoundTrip_PreservesBytesAndIdentity()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-trash-file-").FullName;
        string original = Path.Combine(root, "canary.txt");
        byte[] bytes = [0, 1, 2, 128, 255];
        File.WriteAllBytes(original, bytes);
        string originalSha = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllText(Path.Combine(root, "original-sha256.txt"), originalSha);
        var trash = new WindowsRecycleBin();
        trash.DiagnosticStageForTest = stage => File.AppendAllText(Path.Combine(root, "stages.txt"), $"{DateTimeOffset.UtcNow:O} {stage}\n");
        RecycledPathReceipt receipt = trash.RecycleFile(original);
        File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(receipt));
        Assert.False(File.Exists(original));
        trash.RestoreFile(receipt);
        Assert.Equal(bytes, File.ReadAllBytes(original));
        string restoredSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        File.WriteAllText(Path.Combine(root, "restored-sha256.txt"), restoredSha);
        Assert.Equal(originalSha, restoredSha);
        Assert.Equal(receipt.Identity, trash.GetIdentity(original));
        Assert.False(File.Exists(receipt.RecycledPath));
        Assert.Contains("restore:namespace-unavailable", File.ReadAllText(Path.Combine(root, "stages.txt")));
        string metadata = Path.Combine(Path.GetDirectoryName(receipt.RecycledPath)!, "$I" + Path.GetFileName(receipt.RecycledPath)[2..]);
        File.WriteAllText(Path.Combine(root, "shell-final-state.json"), JsonSerializer.Serialize(new { MetadataExists = File.Exists(metadata), PayloadExists = File.Exists(receipt.RecycledPath) }));
    }

    /// <summary>
    /// A library delete of many captures moves them with one Shell operation, and Undo brings them
    /// back with one Recycle Bin restore. One operation per capture cost about 0.2 s to move and about
    /// a second to restore each. Both ways run here on the same number of owned directories, the
    /// timings go to the test output and timings.txt, and every directory comes back intact.
    /// MYCAPTURE_RECYCLE_CANARY_COUNT sets how many directories each way moves (20 by default).
    /// </summary>
    [RecycleBinCanaryFact]
    public void OwnedDirectories_MoveAndRestoreTogether_FasterThanOneAtATime()
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_RECYCLE_CANARY_COUNT"), out int requested)
            && requested is >= 2 and <= 500 ? requested : 20;
        string root = TestRecycleBin.CreateTempSubdirectory("mc-trash-batch-").FullName;
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] Create(string kind) => Enumerable.Range(0, count).Select(index =>
        {
            string path = Path.Combine(root, $"{kind}-{index:D2}");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "canary.txt"), contents[path] = $"{kind} capture {index}");
            return path;
        }).ToArray();
        string[] alone = Create("alone"), together = Create("together");
        var trash = new WindowsRecycleBin();
        var identities = alone.Concat(together).ToDictionary(path => path, trash.GetIdentity, StringComparer.OrdinalIgnoreCase);
        var stages = new List<string>();
        trash.DiagnosticStageForTest = stage => { lock (stages) stages.Add(stage); };
        string[] TakeStages() { lock (stages) { string[] taken = stages.ToArray(); stages.Clear(); return taken; } }

        // A move or restore of one directory that throws is kept for the assertions while the rest go
        // on, so that every directory that moved comes back.
        var aloneReceipts = new List<RecycledPathReceipt>(count);
        var aloneFailures = new List<Exception>();
        var clock = Stopwatch.StartNew();
        foreach (string path in alone)
        {
            try { aloneReceipts.Add(trash.RecycleDirectory(path)); }
            catch (Exception ex) { aloneFailures.Add(ex); }
        }
        TimeSpan moveAlone = clock.Elapsed;
        TakeStages();
        var progress = new List<int>();
        clock.Restart();
        IReadOnlyList<RecycleOutcome> outcomes = trash.RecycleDirectories(together, moved => { lock (progress) progress.Add(moved); });
        TimeSpan moveTogether = clock.Elapsed;
        string[] moveStages = TakeStages();
        bool movedAway = alone.Concat(together).All(path => !Directory.Exists(path));

        // Everything comes back before any assertion, so a failure leaves nothing in the Recycle Bin.
        clock.Restart();
        foreach (RecycledPathReceipt receipt in aloneReceipts)
        {
            try { trash.RestoreDirectory(receipt); }
            catch (Exception ex) { aloneFailures.Add(ex); }
        }
        TimeSpan restoreAlone = clock.Elapsed;
        TakeStages();
        RecycledPathReceipt[] togetherReceipts = outcomes.Where(outcome => outcome.Receipt is not null).Select(outcome => outcome.Receipt!).ToArray();
        clock.Restart();
        IReadOnlyList<Exception?> failures = trash.RestoreDirectories(togetherReceipts);
        TimeSpan restoreTogether = clock.Elapsed;
        string[] restoreStages = TakeStages();

        string report = $"{count} directories: move one at a time {moveAlone.TotalSeconds:F2} s, together {moveTogether.TotalSeconds:F2} s; "
            + $"restore one at a time {restoreAlone.TotalSeconds:F2} s, together {restoreTogether.TotalSeconds:F2} s";
        output.WriteLine(report);
        File.WriteAllText(Path.Combine(root, "timings.txt"), report);
        Assert.True(aloneFailures.Count == 0, string.Join(Environment.NewLine, aloneFailures));
        Assert.All(outcomes, outcome => Assert.Null(outcome.Failure));
        Assert.Equal(count, togetherReceipts.Length);
        Assert.True(movedAway);
        Assert.Equal(count, progress[^1]);
        Assert.Equal(1, moveStages.Count(stage => stage == "recycle-batch:performed"));
        Assert.DoesNotContain("recycle:queued", moveStages); // no item fell back to its own operation
        Assert.All(failures, failure => Assert.Null(failure));
        Assert.Equal(1, restoreStages.Count(stage => stage == "restore-batch:undelete-end"));
        Assert.DoesNotContain("restore:undelete-start", restoreStages);
        foreach (string path in alone.Concat(together))
        {
            Assert.Equal(identities[path], trash.GetIdentity(path));
            Assert.Equal(contents[path], File.ReadAllText(Path.Combine(path, "canary.txt")));
        }
        Assert.All(aloneReceipts.Concat(togetherReceipts), receipt => Assert.False(Directory.Exists(receipt.RecycledPath)));
        Assert.True(moveTogether < moveAlone, report);
        Assert.True(restoreTogether < restoreAlone, report);
    }
}

public sealed class RecycleBinCanaryFactAttribute : FactAttribute
{
    public RecycleBinCanaryFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_RECYCLE_CANARY") != "1")
            Skip = "Owned Windows Recycle Bin round-trip requires explicit MYCAPTURE_RECYCLE_CANARY=1.";
    }
}
