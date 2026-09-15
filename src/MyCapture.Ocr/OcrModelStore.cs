using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Storage;

namespace MyCapture.Ocr;

/// <summary>
/// Downloads RapidAI PP-OCR weights into <see cref="AppPaths.OcrModelsRoot"/> once and
/// reuses them. Failures are non-fatal: Accurate OCR then falls back to Windows OCR.
/// </summary>
internal sealed class OcrModelStore
{
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal OcrModelStore(AppPaths paths, ILogger<OcrModelStore> log)
        : this(paths, CreateClient(), log)
    {
    }

    internal OcrModelStore(AppPaths paths, HttpClient http, ILogger log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal bool HasRequiredModels => OcrModelCatalog.Required.All(IsPresent);

    internal string PathFor(OcrModelFile file) => Path.Combine(_paths.OcrModelsRoot, file.FileName);

    internal async Task EnsureFileAsync(OcrModelFile file, CancellationToken cancellationToken)
    {
        if (IsPresent(file))
        {
            return;
        }

        Directory.CreateDirectory(_paths.OcrModelsRoot);
        await DownloadAsync(file, cancellationToken).ConfigureAwait(false);
    }

    internal bool IsPresent(OcrModelFile file)
    {
        string path = PathFor(file);
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length < file.MinimumBytes)
        {
            return false;
        }

        return file.ExpectedSha256 is null || HasExpectedHash(path, file.ExpectedSha256);
    }

    internal async Task<bool> EnsureAsync(CancellationToken cancellationToken)
    {
        if (HasRequiredModels)
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasRequiredModels)
            {
                return true;
            }

            Directory.CreateDirectory(_paths.OcrModelsRoot);
            foreach (OcrModelFile file in OcrModelCatalog.Required)
            {
                if (IsPresent(file))
                {
                    continue;
                }

                await DownloadAsync(file, cancellationToken).ConfigureAwait(false);
            }

            foreach (OcrModelFile file in OcrModelCatalog.Optional)
            {
                if (IsPresent(file))
                {
                    continue;
                }

                try
                {
                    await DownloadAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or CryptographicException)
                {
                    _log.LogWarning(ex, "Optional OCR model {File} was skipped", file.FileName);
                }
            }

            return HasRequiredModels;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log.LogWarning(ex, "Could not download PP-OCR receipt models");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(OcrModelFile file, CancellationToken cancellationToken)
    {
        string destination = PathFor(file);
        string temp = destination + ".download";
        _log.LogInformation("Downloading OCR model {File} from RapidAI", file.FileName);

        using HttpResponseMessage response = await _http.GetAsync(
                file.Uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (FileStream target = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (file.ExpectedSha256 is not null && !HasExpectedHash(temp, file.ExpectedSha256))
        {
            TryDelete(temp);
            throw new CryptographicException($"OCR model hash mismatch: {file.FileName}");
        }

        if (new FileInfo(temp).Length < file.MinimumBytes)
        {
            TryDelete(temp);
            throw new IOException($"OCR model was truncated: {file.FileName}");
        }

        File.Copy(temp, destination, overwrite: true);
        TryDelete(temp);
        _log.LogInformation("Stored OCR model {File} ({Bytes} bytes)", file.FileName, new FileInfo(destination).Length);
    }

    private static bool HasExpectedHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        string actual = Convert.ToHexString(hash);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyCapture/2.3.4 (OCR model download)");
        return client;
    }
}
