using MyCapture.Core.GitHub;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The F9 token upload must follow the issue URL's host: github.com talks to api.github.com,
/// any other host is an enterprise server whose REST API lives under /api/v3, and attachment
/// URLs are served by the site host itself.
/// </summary>
public sealed class GitHubIssueTargetTests
{
    [Fact]
    public void GithubComIssue_ParsesAndDerivesDotComApiRoot()
    {
        Assert.True(GitHubIssueImageUrl.TryParseTarget("https://github.com/acme/app/issues/42", out GitHubIssueTarget target));
        Assert.Equal("github.com", target.Host);
        Assert.Equal("acme", target.Owner);
        Assert.Equal("app", target.Repo);
        Assert.Equal(42, target.IssueNumber);
        Assert.Equal("https://api.github.com", target.ApiBase);
        Assert.Equal("https://github.com/user-attachments/assets/", target.AssetUrlBase);
    }

    [Fact]
    public void EnterpriseIssue_ParsesAndDerivesApiV3Root()
    {
        Assert.True(GitHubIssueImageUrl.TryParseTarget(
            "https://github.samsung.com/acme/app/issues/12/",
            out GitHubIssueTarget target));
        Assert.Equal("github.samsung.com", target.Host);
        Assert.Equal("acme", target.Owner);
        Assert.Equal("app", target.Repo);
        Assert.Equal(12, target.IssueNumber);
        Assert.Equal("https://github.samsung.com/api/v3", target.ApiBase);
        Assert.Equal("https://github.samsung.com/user-attachments/assets/", target.AssetUrlBase);
    }

    [Theory]
    [InlineData("https://github.com/acme/app/issues/new")]
    [InlineData("https://github.samsung.com/acme/app/issues/new")]
    [InlineData("https://github.com/acme/app/pulls/12")]
    [InlineData("https://github.com/acme/app/issues/0")]
    [InlineData("")]
    [InlineData("not a url")]
    public void NonNumberedOrInvalidUrls_AreRejected(string raw)
    {
        Assert.False(GitHubIssueImageUrl.TryParseTarget(raw, out _));
    }
}
