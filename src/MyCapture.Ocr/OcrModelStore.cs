using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
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
    private readonly OcrModelFile[] _superResolutionFiles;
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
    internal OcrModelStore(AppPaths paths, HttpClient http, ILogger log, Func<Stream, byte[]> computeHash,
        OcrModelFile? superResolution = null, OcrModelFile? superResolutionData = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _computeHash = computeHash ?? throw new ArgumentNullException(nameof(computeHash));
        _superResolutionFiles = [superResolution ?? OcrModelCatalog.SuperResolution,
            superResolutionData ?? OcrModelCatalog.SuperResolutionData];
    }

    internal bool HasRequiredModels => OcrModelCatalog.Required.All(IsPresent);

    internal bool HasSuperResolution => _superResolutionFiles.All(IsPresent);

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
            await DownloadArchiveAsync(_superResolutionFiles.Where(file => !IsPresent(file)).ToArray(),
                cancellationToken).ConfigureAwait(false);

            return HasSuperResolution;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
        {
            _log.LogWarning(ex, "Could not download the Real-ESRGAN model");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Keep the catalog's legacy identifier, but write and verify the actual ONNX external-data
    // name. Old installs already contain this file alongside their redundant legacy copy.
    internal string PathFor(OcrModelFile file) => Path.Combine(_paths.OcrModelsRoot,
        file.FileName == OcrModelCatalog.SuperResolutionData.FileName
            ? OcrModelCatalog.ZipMemberNameFor(file.FileName) : file.FileName);

    /// <summary>
    /// Checks and prepares one model on a worker, including the initial verification.
    /// </summary>
    internal Task EnsureFileAsync(OcrModelFile file, CancellationToken cancellationToken) =>
        Task.Run(() => EnsureFileCoreAsync(file, cancellationToken));

    private async Task EnsureFileCoreAsync(OcrModelFile file, CancellationToken cancellationToken)
    {
        if (IsPresent(file)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsPresent(file)) return;
            Directory.CreateDirectory(_paths.OcrModelsRoot);
            await DownloadAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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
                if (info.Length < file.MinimumBytes || info.Length > file.ByteLimit)
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
        if (file.Uri.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadArchiveAsync([file], cancellationToken).ConfigureAwait(false);
            return;
        }

        string destination = PathFor(file);
        string temp = destination + ".download";
        InvalidateVerification(destination);
        try
        {
            using HttpResponseMessage response = await OpenDownloadAsync(file.Uri, file.ByteLimit, cancellationToken)
                .ConfigureAwait(false);
            using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await WriteVerifiedAsync(source, temp, file, cancellationToken).ConfigureAwait(false);
            CopyModelFile(temp, destination);
            _log.LogInformation("Stored OCR model {File}", file.FileName);
        }
        finally
        {
            CleanDownload(temp);
        }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(string uri, long cap, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length > cap)
                throw new IOException("OCR download exceeds its byte limit.");
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // Bound both the HTTP transfer and each uncompressed entry. Only the pinned members are
    // extracted, to fixed destinations, and every missing member is verified before publishing.
    private async Task DownloadArchiveAsync(OcrModelFile[] files, CancellationToken cancellationToken)
    {
        if (files.Length == 0) return;
        string archiveTemp = Path.Combine(_paths.OcrModelsRoot, "realesrgan_x4.zip.download");
        try
        {
            using (HttpResponseMessage response = await OpenDownloadAsync(files[0].Uri,
                OcrModelCatalog.SuperResolutionArchiveBytes, cancellationToken).ConfigureAwait(false))
            using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteBoundedAsync(source, archiveTemp, OcrModelCatalog.SuperResolutionArchiveBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            using (var archiveStream = File.OpenRead(archiveTemp))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                foreach (OcrModelFile file in files)
                {
                    string memberName = OcrModelCatalog.ZipMemberNameFor(file.FileName);
                    ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(Path.GetFileName(e.FullName), memberName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new IOException($"OCR model archive was missing {memberName}");
                    if (entry.Length > file.ByteLimit)
                        throw new IOException($"OCR archive entry exceeds its byte limit: {memberName}");
                    using Stream source = entry.Open();
                    await WriteVerifiedAsync(source, PathFor(file) + ".download", file, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            foreach (OcrModelFile file in files)
                CopyModelFile(PathFor(file) + ".download", PathFor(file));
        }
        finally
        {
            CleanDownload(archiveTemp);
            foreach (OcrModelFile file in files)
                CleanDownload(PathFor(file) + ".download");
        }
    }

    private static async Task WriteVerifiedAsync(Stream source, string temp, OcrModelFile file,
        CancellationToken cancellationToken)
    {
        (long length, string hash) = await WriteBoundedAsync(source, temp, file.ByteLimit, cancellationToken)
            .ConfigureAwait(false);
        if (length < file.MinimumBytes || (file.ExpectedBytes is long expected && length != expected))
            throw new IOException($"OCR model was truncated or has an unexpected size: {file.FileName}");
        if (file.ExpectedSha256 is not null && !string.Equals(hash, file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"OCR model hash mismatch: {file.FileName}");
    }

    private static async Task<(long Length, string Hash)> WriteBoundedAsync(Stream source, string temp,
        long cap, CancellationToken cancellationToken)
    {
        using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (read > cap - length)
                throw new IOException("OCR download exceeds its byte limit.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length += read;
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private void CleanDownload(string temp)
    {
        VerificationState tempState = VerificationFor(temp);
        lock (tempState)
        {
            tempState.Verified = null;
            TryDelete(temp);
        }
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

