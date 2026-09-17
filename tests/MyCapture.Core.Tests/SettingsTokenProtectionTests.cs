using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The GitHub PAT must never sit in plaintext inside settings.json: it is DPAPI-encrypted
/// at rest, legacy plaintext values are migrated on the first load, and exported settings
/// files carry no secret at all.
/// </summary>
public sealed class SettingsTokenProtectionTests
{
    private const string PlaintextToken = "ghp_secret1234567890abcdef";

    private static SettingsStore CreateStore(TempWorkspace workspace) =>
        new(workspace.Paths, NullLogger<SettingsStore>.Instance);

    private static AppSettings SettingsWithToken() => new()
    {
        GitHub = new GitHubSettings { Token = PlaintextToken },
    };

    [Fact]
    public void Save_EncryptsTokenAtRest()
    {
        using var workspace = new TempWorkspace();
        var store = CreateStore(workspace);

        store.Save(SettingsWithToken());

        string onDisk = File.ReadAllText(workspace.Paths.SettingsFile);
        Assert.Contains(DpapiSecretConverter.EncryptedPrefix, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaintextToken, onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DecryptsTokenAfterSave()
    {
        using var workspace = new TempWorkspace();
        var store = CreateStore(workspace);
        store.Save(SettingsWithToken());

        Assert.Equal(PlaintextToken, store.Load().GitHub.Token);
    }

    [Fact]
    public void Load_MigratesLegacyPlaintextToken()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.Paths.DataRoot);
        string legacy = """{"gitHub":{"token":"ghp_legacy1234567890abcdef"}}""";
        File.WriteAllText(workspace.Paths.SettingsFile, legacy);
        var store = CreateStore(workspace);

        Assert.Equal("ghp_legacy1234567890abcdef", store.Load().GitHub.Token);

        string onDisk = File.ReadAllText(workspace.Paths.SettingsFile);
        Assert.Contains(DpapiSecretConverter.EncryptedPrefix, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_legacy1234567890abcdef", onDisk, StringComparison.Ordinal);

        // The migrated file keeps decrypting to the same token.
        Assert.Equal("ghp_legacy1234567890abcdef", store.Load().GitHub.Token);
    }

    [Fact]
    public void Load_DamagedBlob_BecomesEmptyToken()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.Paths.DataRoot);
        File.WriteAllText(workspace.Paths.SettingsFile, """{"gitHub":{"token":"dpapi:v1:not-base64!!"}}""");
        var store = CreateStore(workspace);

        Assert.Equal(string.Empty, store.Load().GitHub.Token);
    }

    [Fact]
    public void EmptyToken_StaysEmptyOnDisk()
    {
        using var workspace = new TempWorkspace();
        var store = CreateStore(workspace);
        store.Save(new AppSettings());

        string onDisk = File.ReadAllText(workspace.Paths.SettingsFile);
        Assert.DoesNotContain(DpapiSecretConverter.EncryptedPrefix, onDisk, StringComparison.Ordinal);
        Assert.Equal(string.Empty, store.Load().GitHub.Token);
    }

    [Fact]
    public void ExportTo_OmitsTheToken()
    {
        using var workspace = new TempWorkspace();
        var store = CreateStore(workspace);
        store.Save(SettingsWithToken());
        string exportPath = Path.Combine(workspace.Root, "export.json");

        store.ExportTo(store.Load(), exportPath);

        string onDisk = File.ReadAllText(exportPath);
        Assert.DoesNotContain(PlaintextToken, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(DpapiSecretConverter.EncryptedPrefix, onDisk, StringComparison.Ordinal);

        AppSettings exported = JsonSerializer.Deserialize<AppSettings>(onDisk, JsonDefaults.Readable)!;
        Assert.Equal(string.Empty, exported.GitHub.Token);
    }

    [Fact]
    public void ImportFrom_YieldsEmptyToken()
    {
        using var workspace = new TempWorkspace();
        var store = CreateStore(workspace);
        store.Save(SettingsWithToken());
        string exportPath = Path.Combine(workspace.Root, "export.json");
        store.ExportTo(store.Load(), exportPath);

        Assert.Equal(string.Empty, store.ImportFrom(exportPath).GitHub.Token);
    }

    [Fact]
    public void ExportTo_DoesNotMutateTheLiveSettings()
    {
        using var workspace = new TempWorkspace();
        var store = CreateStore(workspace);
        AppSettings live = SettingsWithToken();
        string exportPath = Path.Combine(workspace.Root, "export.json");

        store.ExportTo(live, exportPath);

        Assert.Equal(PlaintextToken, live.GitHub.Token);
    }
}
