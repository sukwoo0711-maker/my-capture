using System.Collections.Concurrent;
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
    private readonly Func<Stream, byte[]> _computeHash;
    private readonly ConcurrentDictionary<string, VerificationState> _verification = new(StringComparer.OrdinalIgnoreCase);

    internal OcrModelStore(AppPaths paths, ILogger<OcrModelStore> log)
        : this(paths, CreateClient(), log)
    {
    }

    internal OcrModelStore(AppPaths paths, HttpClient http, ILogger log)
        : this(paths, http, log, SHA256.HashData)
    {
    }

    /// <summary>
    /// Supplies the file hasher so tests can observe verification without changing the catalog.
    /// </summary>
    internal OcrModelStore(AppPaths paths, HttpClient http, ILogger log, Func<Stream, byte[]> computeHash)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _computeHash = computeHash ?? throw new ArgumentNullException(nameof(computeHash));
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
    internal Task<bool> EnsureSuperResolutionAsync(CancellationToken cancellationToken) =>
        Task.Run(() => EnsureSuperResolutionCoreAsync(cancellationToken));

    private async Task<bool> EnsureSuperResolutionCoreAsync(CancellationToken cancellationToken)
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

    /// <summary>
    /// Checks and prepares one model on a worker, including the initial verification.
    /// </summary>
    internal Task EnsureFileAsync(OcrModelFile file, CancellationToken cancellationToken) =>
        Task.Run(() => EnsureFileCoreAsync(file, cancellationToken));

    private async Task EnsureFileCoreAsync(OcrModelFile file, CancellationToken cancellationToken)
    {
        if (IsPresent(file))
        {
            return;
        }

        Directory.CreateDirectory(_paths.OcrModelsRoot);
        await DownloadAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reuses a successful check only while the file metadata and expected hash still match.
    /// Cache misses retain synchronous verification for availability callers.
    /// </summary>
    internal bool IsPresent(OcrModelFile file)
    {
        string path = Path.GetFullPath(PathFor(file));
        VerificationState state = VerificationFor(path);
        lock (state)
        {
            try
            {
                if (!File.Exists(path))
                {
                    state.Verified = null;
                    return false;
                }

                var info = new FileInfo(path);
                if (info.Length < file.MinimumBytes)
                {
                    state.Verified = null;
                    return false;
                }

                var current = new VerifiedFile(info.Length, info.LastWriteTimeUtc, file.ExpectedSha256);
                if (state.Verified is VerifiedFile verified
                    && verified.Length == current.Length
                    && verified.LastWriteTimeUtc == current.LastWriteTimeUtc
                    && string.Equals(verified.ExpectedSha256, current.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                state.Verified = null;
                if (file.ExpectedSha256 is not null && !HasExpectedHash(path, file.ExpectedSha256))
                {
                    return false;
                }

                // A file changed during verification must be checked again before it is reused.
                info.Refresh();
                if (!info.Exists || info.Length != current.Length || info.LastWriteTimeUtc != current.LastWriteTimeUtc)
                {
                    return false;
                }

                state.Verified = current;
                return true;
            }
            catch
            {
                state.Verified = null;
                throw;
            }
        }
    }

    /// <summary>
    /// Warms verification of installed models without downloading or delaying construction.
    /// A missing or unreadable model does not prevent the remaining models from being checked.
    /// </summary>
    internal Task PreVerifyInstalledModelsAsync() => Task.Run(() =>
    {
        foreach (OcrModelFile file in OcrModelCatalog.Required.Concat(
            [OcrModelCatalog.SuperResolution, OcrModelCatalog.SuperResolutionData]))
        {
            try
            {
                _ = IsPresent(file);
            }
            catch (Exception ex)
            {
                try
                {
                    _log.LogWarning(ex, "Could not verify installed OCR model {File}", file.FileName);
                }
                catch (Exception)
                {
                    // Background verification must remain non-fatal even if a logger fails.
                }
            }
        }
    });

    /// <summary>
    /// Starts preparation on a worker before performing any file or hash work.
    /// </summary>
    internal Task<bool> EnsureAsync(CancellationToken cancellationToken) =>
        Task.Run(() => EnsureCoreAsync(cancellationToken));

    private async Task<bool> EnsureCoreAsync(CancellationToken cancellationToken)
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
        InvalidateVerification(destination);
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
        CopyModelFile(temp, destination);

        // The ONNX file records its weights under the exporter's original name
        // (real_esrgan_x4plus.data), so the external-data file must exist under both names.
        if (string.Equals(file.FileName, OcrModelCatalog.SuperResolutionData.FileName, StringComparison.Ordinal))
        {
            string exporterName = Path.Combine(_paths.OcrModelsRoot, OcrModelCatalog.ZipMemberNameFor(file.FileName));
            if (!string.Equals(exporterName, destination, StringComparison.OrdinalIgnoreCase))
            {
                CopyModelFile(temp, exporterName);
            }
        }

        VerificationState tempState = VerificationFor(temp);
        lock (tempState)
        {
            tempState.Verified = null;
            TryDelete(temp);
        }
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

    private bool HasExpectedHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = _computeHash(stream);
        string actual = Convert.ToHexString(hash);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private VerificationState VerificationFor(string path) =>
        _verification.GetOrAdd(Path.GetFullPath(path), static _ => new VerificationState());

    private void InvalidateVerification(string path)
    {
        VerificationState state = VerificationFor(path);
        lock (state)
        {
            state.Verified = null;
        }
    }

    private void CopyModelFile(string source, string destination)
    {
        VerificationState state = VerificationFor(destination);
        lock (state)
        {
            state.Verified = null;
            File.Copy(source, destination, overwrite: true);
        }
    }

    private sealed class VerificationState
    {
        internal VerifiedFile? Verified { get; set; }
    }

    private readonly record struct VerifiedFile(long Length, DateTime LastWriteTimeUtc, string? ExpectedSha256);

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyCapture/2.3.10 (OCR model download)");
        return client;
    }
}

