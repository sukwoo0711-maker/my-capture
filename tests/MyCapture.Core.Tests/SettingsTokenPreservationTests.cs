using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Editing and applying unrelated settings must keep the saved GitHub token; only exports
/// leave it out.
/// </summary>
public sealed class SettingsTokenPreservationTests
{
    private const string Token = "ghp_PreservedToken0123456789";

    [Fact]
    public void DeepClone_CopiesTheTokenIndependently()
    {
        var source = new AppSettings { GitHub = { Token = Token } };

        AppSettings clone = source.DeepClone();
        clone.GitHub.Token = string.Empty;

        Assert.Equal(Token, source.DeepClone().GitHub.Token);
        Assert.Equal(Token, source.GitHub.Token);
    }

    [Fact]
    public void Draft_ShowsAndMapsTheSavedToken_WhenOtherValuesChange()
    {
        var draft = new SettingsDraft(new AppSettings { GitHub = { Token = Token } });
        Assert.Equal(Token, draft.GitHubToken);

        draft.MaxItems = "450";
        AppSettings mapped = draft.ToAppSettings();

        Assert.Equal(Token, mapped.GitHub.Token);
        Assert.Equal(450, mapped.Queue.MaxItems);
    }

    [Fact]
    public void SaveAfterAnUnrelatedEdit_KeepsTheToken_AndExportStillOmitsIt()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        store.Save(new AppSettings { GitHub = { Token = Token } });

        AppSettings loaded = store.Load();
        var draft = new SettingsDraft(loaded) { MaxItems = "450" };
        store.Save(draft.ToAppSettings());
        string exported = Path.Combine(workspace.Root, "exported.json");
        store.ExportTo(store.Load(), exported);

        Assert.Equal(Token, store.Load().GitHub.Token);
        Assert.DoesNotContain(Token, File.ReadAllText(workspace.Paths.SettingsFile), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, File.ReadAllText(exported), StringComparison.Ordinal);
        Assert.Equal(string.Empty, store.ImportFrom(exported).GitHub.Token);
    }
}
