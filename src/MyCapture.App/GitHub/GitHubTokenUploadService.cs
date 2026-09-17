using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.GitHub;
using MyCapture.Core.Settings;

namespace MyCapture.App.GitHub;

internal sealed record GitHubTokenUploadResult(bool Success, string? Url = null, string? Error = null);

/// <summary>
/// Uploads a PNG to the configured GitHub issue through the REST API using a user-supplied
/// personal access token. This bypasses the browser automation entirely: no window opens,
/// no focus is taken, and no clipboard round-trip is needed.
/// </summary>
/// <remarks>
/// The token is stored by the user in the settings file on their own machine and sent only
/// to the API host derived from the configured issue URL (api.github.com, or the enterprise
/// server's <c>/api/v3</c>). Scopes: the token needs <c>repo</c> (private issues) or
/// <c>public_repo</c> (public issues). Note that the user-attachments upload API is
/// currently served by github.com only; when an enterprise server refuses it the caller
/// falls back to the browser flow.
/// </remarks>
internal sealed class GitHubTokenUploadService
{
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;
    private readonly Func<HttpClient> _clientFactory;

    internal GitHubTokenUploadService(Func<AppSettings> settings, ILogger log)
        : this(settings, log, static () => new HttpClient())
    {
    }

    internal GitHubTokenUploadService(Func<AppSettings> settings, ILogger log, Func<HttpClient> clientFactory)
    {
        _settings = settings;
        _log = log;
        _clientFactory = clientFactory;
    }

    internal async Task<GitHubTokenUploadResult> UploadAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        AppSettings settings = _settings();
        if (string.IsNullOrWhiteSpace(settings.GitHub.Token))
        {
            return new GitHubTokenUploadResult(false, Error: "no-token");
        }

        if (!GitHubIssueImageUrl.TryParseTarget(settings.GitHub.IssueUrl, out GitHubIssueTarget target))
        {
            return new GitHubTokenUploadResult(false, Error: "invalid-url");
        }

        byte[] png = EncodePng(image);
        if (png.Length == 0)
        {
            return new GitHubTokenUploadResult(false, Error: "encode-failed");
        }

        try
        {
            using var client = _clientFactory();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MyCapture");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // 1) Request a user-attachments upload policy (GitHub Images API). The token is
            //    set per request: the signed upload URL in step 2 lives on a different host
            //    and must not carry the Authorization header.
            string fileName = $"mycapture-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            using var policyRequest = new HttpRequestMessage(HttpMethod.Post, $"{target.ApiBase}/upload/policies")
            {
                Content = JsonContent.Create(new
                {
                    name = fileName,
                    size = png.Length,
                    content_type = "image/png",
                }),
            };
            policyRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", settings.GitHub.Token.Trim());
            using HttpResponseMessage policyResponse = await client.SendAsync(policyRequest, cancellationToken);
            if (!policyResponse.IsSuccessStatusCode)
            {
                string body = await policyResponse.Content.ReadAsStringAsync(cancellationToken);
                _log.LogWarning("GitHub upload policy failed {Status}: {Body}", policyResponse.StatusCode, body);
                return new GitHubTokenUploadResult(false, Error: $"policy-{(int)policyResponse.StatusCode}");
            }

            using System.Text.Json.JsonDocument policy = System.Text.Json.JsonDocument.Parse(
                await policyResponse.Content.ReadAsStringAsync(cancellationToken));
            string uploadUrl = policy.RootElement.GetProperty("upload_url").GetString() ?? string.Empty;
            string assetId = policy.RootElement.GetProperty("asset_id").GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(uploadUrl) || string.IsNullOrEmpty(assetId))
            {
                return new GitHubTokenUploadResult(false, Error: "policy-missing-fields");
            }

            // 2) Upload the bytes to the signed URL. No Authorization header here: the
            //    URL is pre-signed, and client default headers are not merged (the token
            //    was set on the policy request only).
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
            {
                Content = new ByteArrayContent(png),
            };
            uploadRequest.Content.Headers.Add("Content-Type", "image/png");
            using HttpResponseMessage uploadResponse = await client.SendAsync(uploadRequest, cancellationToken);
            if (!uploadResponse.IsSuccessStatusCode)
            {
                string body = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
                _log.LogWarning("GitHub upload failed {Status}: {Body}", uploadResponse.StatusCode, body);
                return new GitHubTokenUploadResult(false, Error: $"upload-{(int)uploadResponse.StatusCode}");
            }

            string url = $"{target.AssetUrlBase}{assetId}";
            _log.LogInformation("Uploaded image via token: {Url}", url);
            return new GitHubTokenUploadResult(true, Url: url);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Token-based GitHub upload failed");
            return new GitHubTokenUploadResult(false, Error: ex.Message);
        }
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
