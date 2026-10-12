using System.IO;
using MyCapture.Platform.Shell;
using System.Text.Json;
using System.Security.Cryptography;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class WindowsRecycleBinTests
{
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
}

public sealed class RecycleBinCanaryFactAttribute : FactAttribute
{
    public RecycleBinCanaryFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_RECYCLE_CANARY") != "1")
            Skip = "Owned Windows Recycle Bin round-trip requires explicit MYCAPTURE_RECYCLE_CANARY=1.";
    }
}
