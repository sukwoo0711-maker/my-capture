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

    internal bool HasSuperResolution => IsPresent(OcrModelCatalog.SuperResolution)
        && IsPresent(OcrModelCatalog.SuperResolutionData)
        && File.Exists(SuperResolutionExporterDataPath);

    /// <summary>
    /// The ONNX graph references its weights under the exporter's original name, so that
    /// file must exist beside the model for the session to load.
    /// </summary>
    internal string SuperResolutionExporterDataPath =>
        Path.Combine(_paths.OcrModelsRoot, OcrModelCatalog.ZipMemberNameFor(OcrModelCatalog.SuperResolutionData.FileName));

    /// <summary>
    /// Downloads the optional Real-ESRGAN weights if they are missing. Non-fatal: the caller
    /// keeps nearest-neighbour scaling when this returns false.
    /// </summary>
    internal async Task<bool> EnsureSuperResolutionAsync(CancellationToken cancellationToken)
    {
        if (HasSuperResolution)
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasSuperResolution)
            {
                return true;
            }

            Directory.CreateDirectory(_paths.OcrModelsRoot);
            foreach (OcrModelFile file in (OcrModelFile[])[OcrModelCatalog.SuperResolution, OcrModelCatalog.SuperResolutionData])
            {
                if (IsPresent(file))
                {
                    continue;
                }

                await DownloadAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return HasSuperResolution;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log.LogWarning(ex, "Could not download the Real-ESRGAN model");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

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
        _log.LogInformation("Downloading OCR model {File}", file.FileName);

        byte[] payload = file.Uri.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? await DownloadZipMemberAsync(file, cancellationToken).ConfigureAwait(false)
            : await DownloadBytesAsync(file.Uri, cancellationToken).ConfigureAwait(false);

        if (file.ExpectedSha256 is not null
            && !string.Equals(Convert.ToHexString(SHA256.HashData(payload)), file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException($"OCR model hash mismatch: {file.FileName}");
        }

        if (payload.Length < file.MinimumBytes)
        {
            throw new IOException($"OCR model was truncated: {file.FileName}");
        }

        await File.WriteAllBytesAsync(temp, payload, cancellationToken).ConfigureAwait(false);
        File.Copy(temp, destination, overwrite: true);

        // The ONNX file records its weights under the exporter's original name
        // (real_esrgan_x4plus.data), so the external-data file must exist under both names.
        if (string.Equals(file.FileName, OcrModelCatalog.SuperResolutionData.FileName, StringComparison.Ordinal))
        {
            string exporterName = Path.Combine(_paths.OcrModelsRoot, OcrModelCatalog.ZipMemberNameFor(file.FileName));
            if (!string.Equals(exporterName, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(temp, exporterName, overwrite: true);
            }
        }

        TryDelete(temp);
        _log.LogInformation("Stored OCR model {File} ({Bytes} bytes)", file.FileName, payload.Length);
    }

    private async Task<byte[]> DownloadBytesAsync(string uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the single ONNX member named <c>real_esrgan_x4plus.onnx</c> or
    /// <c>real_esrgan_x4plus.data</c> out of the Qualcomm AI Hub zip. Entries outside those
    /// names are ignored; a missing entry throws and the caller treats the model as absent.
    /// </summary>
    private async Task<byte[]> DownloadZipMemberAsync(OcrModelFile file, CancellationToken cancellationToken)
    {
        string memberName = file.Uri.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? OcrModelCatalog.ZipMemberNameFor(file.FileName)
            : file.FileName;
        byte[] zipBytes = await DownloadBytesAsync(file.Uri, cancellationToken).ConfigureAwait(false);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes), System.IO.Compression.ZipArchiveMode.Read);
        System.IO.Compression.ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, memberName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(e.FullName), memberName, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException($"OCR model archive was missing {memberName}");
        using Stream source = entry.Open();
        using var output = new MemoryStream();
        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyCapture/2.3.8 (OCR model download)");
        return client;
    }
}

