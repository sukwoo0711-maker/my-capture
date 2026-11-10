using MyCapture.Core.Platform;
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
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<Stream, byte[]> _computeHash;
    private readonly TimeSpan _stallTimeout;
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
        TimeSpan? stallTimeout = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _computeHash = computeHash ?? throw new ArgumentNullException(nameof(computeHash));
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        if (_stallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stallTimeout), "The stall timeout must be positive.");
    }

    internal bool HasRequiredModels => AppArchitecture.SupportsNeuralOcr && OcrModelCatalog.Required.All(IsPresent);

    internal string PathFor(OcrModelFile file) => ValidateModelPath(Path.Combine(_paths.OcrModelsRoot, file.FileName));

    /// <summary>
    /// Checks and prepares one model on a worker, including the initial verification.
    /// </summary>
    internal Task EnsureFileAsync(OcrModelFile file, CancellationToken cancellationToken) =>
        AppArchitecture.SupportsNeuralOcr ? Task.Run(() => EnsureFileCoreAsync(file, cancellationToken)) : Task.CompletedTask;

    private async Task EnsureFileCoreAsync(OcrModelFile file, CancellationToken cancellationToken)
    {
        if (IsPresent(file)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsPresent(file)) return;
            ValidatedFilePaths.CreateDirectory(_paths.OcrModelsRoot);
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
        string path = Path.GetFullPath(Path.Combine(_paths.OcrModelsRoot, file.FileName));
        VerificationState state = VerificationFor(path);
        lock (state)
        {
            // Recheck even a previously verified file: a directory or leaf can be replaced
            // by a link without changing the cached length or timestamp.
            try { _ = ValidateModelPath(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.Verified = null;
                return false;
            }
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
                try { _ = ValidateModelPath(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
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
    internal Task PreVerifyInstalledModelsAsync() => !AppArchitecture.SupportsNeuralOcr ? Task.CompletedTask : Task.Run(() =>
    {
        foreach (OcrModelFile file in OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional))
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

        RemoveRetiredSuperResolutionFiles();
    });

    /// <summary>
    /// The most accurate quality and its super-resolution model were retired. Their files
    /// (about 70 MB, up to 137 MB with the old duplicate) are never loaded again, so they
    /// are removed once; a file that cannot be removed now is tried again on a later start.
    /// </summary>
    internal void RemoveRetiredSuperResolutionFiles()
    {
        foreach (string name in OcrModelCatalog.LegacySuperResolutionFiles)
        {
            string path = Path.Combine(_paths.OcrModelsRoot, name);
            try
            {
                _ = ValidateModelPath(path);
                if (!File.Exists(path)) continue;
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(path);
                InvalidateVerification(path);
                _log.LogInformation("Removed retired super-resolution model file {File}", name);
            }
            catch (Exception ex)
            {
                try
                {
                    _log.LogWarning(ex, "Could not remove retired super-resolution model file {File}", name);
                }
                catch (Exception)
                {
                    // Same rule as verification: cleanup never faults the background task.
                }
            }
        }
    }

    /// <summary>
    /// Starts preparation on a worker before performing any file or hash work.
    /// </summary>
    internal Task<bool> EnsureAsync(CancellationToken cancellationToken, IProgress<double>? progress = null) =>
        AppArchitecture.SupportsNeuralOcr ? Task.Run(() => EnsureCoreAsync(cancellationToken, progress)) : Task.FromResult(false);

    private async Task<bool> EnsureCoreAsync(CancellationToken cancellationToken, IProgress<double>? progress)
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

            ValidatedFilePaths.CreateDirectory(_paths.OcrModelsRoot);
            long total = OcrModelCatalog.DownloadBytesFor(MyCapture.Core.Settings.OcrQuality.Accurate);
            long completed = 0;
            void ReportFile(long bytes) => progress?.Report(DownloadProgress(completed, bytes, total));
            foreach (OcrModelFile file in OcrModelCatalog.Required)
            {
                if (IsPresent(file))
                {
                    completed += file.ExpectedBytes ?? 0;
                    ReportFile(0);
                    continue;
                }

                await DownloadAsync(file, cancellationToken, ReportFile).ConfigureAwait(false);
                completed += file.ExpectedBytes ?? 0;
            }

            foreach (OcrModelFile file in OcrModelCatalog.Optional)
            {
                if (IsPresent(file))
                {
                    completed += file.ExpectedBytes ?? 0;
                    ReportFile(0);
                    continue;
                }

                try
                {
                    await DownloadAsync(file, cancellationToken, ReportFile).ConfigureAwait(false);
                    completed += file.ExpectedBytes ?? 0;
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
            _log.LogWarning(ex, "Could not download PP-OCR models");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static double DownloadProgress(long completedBytes, long currentFileBytes, long totalBytes) =>
        totalBytes > 0 ? Math.Clamp(((double)completedBytes + currentFileBytes) / totalBytes, 0, 1) : 1;

    private async Task DownloadAsync(OcrModelFile file, CancellationToken cancellationToken, Action<long>? progress = null)
    {
        string destination = PathFor(file);
        string temp = ValidateModelPath(destination + ".download");
        InvalidateVerification(destination);
        // HttpClient.Timeout ends at the headers with ResponseHeadersRead. Bound the wait
        // for headers and each subsequent network read, like TranslationModelStore.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            using HttpResponseMessage response = await OpenDownloadAsync(file.Uri, file.ByteLimit, stall.Token)
                .ConfigureAwait(false);
            using Stream source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            stall.CancelAfter(Timeout.InfiniteTimeSpan);
            await WriteVerifiedAsync(source, temp, file, stall, cancellationToken, progress).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CopyModelFile(temp, destination);
            _log.LogInformation("Stored OCR model {File}", file.FileName);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"The download of OCR model {file.FileName} stalled.", ex);
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

    private async Task WriteVerifiedAsync(Stream source, string temp, OcrModelFile file,
        CancellationTokenSource stall, CancellationToken cancellationToken, Action<long>? progress = null)
    {
        (long length, string hash) = await WriteBoundedAsync(source, temp, file.ByteLimit, stall, cancellationToken, progress)
            .ConfigureAwait(false);
        if (length < file.MinimumBytes || (file.ExpectedBytes is long expected && length != expected))
            throw new IOException($"OCR model was truncated or has an unexpected size: {file.FileName}");
        if (file.ExpectedSha256 is not null && !string.Equals(hash, file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"OCR model hash mismatch: {file.FileName}");
    }

    private async Task<(long Length, string Hash)> WriteBoundedAsync(Stream source, string temp,
        long cap, CancellationTokenSource stall, CancellationToken cancellationToken, Action<long>? progress = null)
    {
        temp = ValidateModelPath(temp);
        using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            // Only waiting for network bytes consumes the stall deadline. Local hashing,
            // disk writes and progress callbacks remain governed by the caller token.
            stall.CancelAfter(_stallTimeout);
            int read = await source.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
            stall.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0) break;
            if (read > cap - length)
                throw new IOException("OCR download exceeds its byte limit.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length += read;
            progress?.Invoke(length);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        try { path = ValidateModelPath(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
            source = ValidateModelPath(source);
            destination = ValidateModelPath(destination);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private sealed class VerificationState
    {
        internal VerifiedFile? Verified { get; set; }
    }

    private readonly record struct VerifiedFile(long Length, DateTime LastWriteTimeUtc, string? ExpectedSha256);

    private string ValidateModelPath(string path) =>
        ValidatedFilePaths.FileWithinRoot(_paths.OcrModelsRoot, path);

    private void TryDelete(string path)
    {
        try
        {
            path = ValidateModelPath(path);
            if (File.Exists(path))
            {
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(path);
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

