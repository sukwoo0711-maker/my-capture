using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.GitHub;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The development repository is private, so the old default issue page is unreachable for users.
/// Saved settings that still hold it move to the public support repository; a page the user chose stays.
/// </summary>
public sealed class GitHubIssueDefaultMigrationTests
{
    [Fact]
    public void DefaultIssuePage_IsThePublicSupportRepository()
    {
        Assert.Equal("https://github.com/sukwoo0711-maker/mycapture-support/issues/new", GitHubIssueImageUrl.DefaultIssueUrl);
        Assert.Equal(GitHubIssueImageUrl.DefaultIssueUrl, new AppSettings().GitHub.IssueUrl);
    }

    [Fact]
    public void Load_MovesTheLegacyDefaultToTheSupportRepository()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        store.Save(new AppSettings { GitHub = new GitHubSettings { IssueUrl = GitHubIssueImageUrl.LegacyDefaultIssueUrl } });

        Assert.Equal(GitHubIssueImageUrl.DefaultIssueUrl, store.Load().GitHub.IssueUrl);
    }

    [Fact]
    public void Load_KeepsAnIssuePageTheUserChose()
    {
        const string chosen = "https://github.com/example/project/issues/42";
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        store.Save(new AppSettings { GitHub = new GitHubSettings { IssueUrl = chosen } });

        Assert.Equal(chosen, store.Load().GitHub.IssueUrl);
    }
}
