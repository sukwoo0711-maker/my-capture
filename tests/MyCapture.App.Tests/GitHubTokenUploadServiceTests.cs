using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.GitHub;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The token upload must derive its endpoints from the configured issue URL's host instead
/// of forcing github.com: github.com uses api.github.com, an enterprise server uses its own
/// /api/v3, and the returned asset URL keeps the site host.
/// </summary>
public sealed class GitHubTokenUploadServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Authorization, string? ContentType)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static BitmapSource TinyImage()
    {
        var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Freeze();
        return bitmap;
    }

    private static AppSettings Settings(string issueUrl, string token = "tok") => new()
    {
        GitHub = new GitHubSettings { IssueUrl = issueUrl, Token = token },
    };

    private static GitHubTokenUploadService CreateService(AppSettings settings, StubHandler handler) =>
        new(() => settings, NullLogger.Instance, () => new HttpClient(handler));

    [Fact]
    public async Task EnterpriseIssue_UploadsAgainstApiV3AndBuildsEnterpriseAssetUrl()
    {
        var handler = new StubHandler(request => Json(new
        {
            upload_url = "https://upload.example.com/signed",
            asset_id = "0f1e2d3c-1111-2222-3333-444455556666",
        }));
        GitHubTokenUploadService service = CreateService(
            Settings("https://github.samsung.com/acme/app/issues/12"), handler);

        GitHubTokenUploadResult result = await service.UploadAsync(TinyImage());

        Assert.True(result.Success, result.Error);
        Assert.Equal("https://github.samsung.com/user-attachments/assets/0f1e2d3c-1111-2222-3333-444455556666", result.Url);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://github.samsung.com/api/v3/upload/policies", handler.Requests[0].Uri.ToString());
        Assert.Equal("Bearer tok", handler.Requests[0].Authorization);
        Assert.Equal("https://upload.example.com/signed", handler.Requests[1].Uri.ToString());
        Assert.Equal("image/png", handler.Requests[1].ContentType);
    }

    [Fact]
    public async Task GithubComIssue_StillTargetsApiGithubCom()
    {
        var handler = new StubHandler(request => Json(new
        {
            upload_url = "https://upload.example.com/signed",
            asset_id = "a1b2c3d4-1111-2222-3333-444455556666",
        }));
        GitHubTokenUploadService service = CreateService(
            Settings("https://github.com/acme/app/issues/7"), handler);

        GitHubTokenUploadResult result = await service.UploadAsync(TinyImage());

        Assert.True(result.Success, result.Error);
        Assert.Equal("https://github.com/user-attachments/assets/a1b2c3d4-1111-2222-3333-444455556666", result.Url);
        Assert.Equal("https://api.github.com/upload/policies", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task UnnumberedIssueUrl_IsRejectedWithoutNetwork()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));
        GitHubTokenUploadService service = CreateService(
            Settings("https://github.samsung.com/acme/app/issues/new"), handler);

        GitHubTokenUploadResult result = await service.UploadAsync(TinyImage());

        Assert.False(result.Success);
        Assert.Equal("invalid-url", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingToken_ReturnsNoTokenWithoutNetwork()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("no request expected"));
        GitHubTokenUploadService service = CreateService(
            Settings("https://github.samsung.com/acme/app/issues/12", token: ""), handler);

        GitHubTokenUploadResult result = await service.UploadAsync(TinyImage());

        Assert.False(result.Success);
        Assert.Equal("no-token", result.Error);
        Assert.Empty(handler.Requests);
    }
}
