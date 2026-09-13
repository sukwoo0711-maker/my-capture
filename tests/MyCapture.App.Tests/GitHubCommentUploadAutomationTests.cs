using MyCapture.App.GitHub;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GitHubCommentUploadAutomationTests
{
    private const string Issue = "https://github.com/owner/repo/issues/42";
    private const string Old = "https://github.com/user-attachments/assets/11111111-1111-1111-1111-111111111111";
    private const string New = "https://github.com/user-attachments/assets/22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData("github.com/owner/repo/issues/42", true)]
    [InlineData("https://github.com/owner/repo/issues/42#new_comment_field", true)]
    [InlineData("https://github.com/other/repo/issues/42", false)]
    [InlineData("https://github.com/owner/repo/issues/43", false)]
    [InlineData("https://github.com/owner/repo/issues/new", false)]
    [InlineData("https://github.com.evil.test/owner/repo/issues/42", false)]
    [InlineData("http://github.com/owner/repo/issues/42", false)]
    [InlineData("GitHub", false)]
    [InlineData("", false)]
    public void DestinationMustBeTheConfiguredIssue(string address, bool expected) =>
        Assert.Equal(expected, GitHubCommentUploadAutomation.AddressMatches(address, Issue));

    [Fact]
    public void ExistingAttachmentCannotCompleteANewUpload()
    {
        Assert.False(GitHubCommentUploadAutomation.TryExtractNewUrl(Old, Old + " Uploading image...", out _));
        Assert.True(GitHubCommentUploadAutomation.TryExtractNewUrl(Old, $"{Old}\n<img src=\"{New}\" />", out string url));
        Assert.Equal(New, url);
        Assert.False(GitHubCommentUploadAutomation.TryExtractNewUrl("", Old + New, out _));
    }

    [Fact]
    public void EarlierPendingUploadCannotBeMistakenForCurrentImage()
    {
        Assert.False(GitHubCommentUploadAutomation.TryExtractNewUrl("Uploading old.png...", Old + " Uploading new.png...", out _));
        Assert.False(GitHubCommentUploadAutomation.TryExtractNewUrl("Uploading old.png...", Old, out _));
        Assert.False(GitHubCommentUploadAutomation.TryExtractNewUrl("", New + " 업로드 중", out _));
    }

    [Theory]
    [InlineData("new_comment_field", "", false, true)]
    [InlineData("", "Add a comment", false, true)]
    [InlineData("", "댓글 추가", false, true)]
    [InlineData("issue_body", "", true, true)]
    [InlineData("", "Add a description", true, true)]
    [InlineData("issue_body", "", false, false)]
    [InlineData("", "Search or jump to...", true, false)]
    [InlineData("", "Address and search bar", true, false)]
    [InlineData("", "Title", true, false)]
    public void OnlyCommentOrNewIssueBodyIsEligible(string id, string name, bool newIssue, bool expected) =>
        Assert.Equal(expected, GitHubCommentUploadAutomation.IsCommentIdentity(id, name, newIssue));
}
