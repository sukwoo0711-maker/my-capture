using MyCapture.Core.Platform;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Storage;

namespace MyCapture.Ocr.Translation;

/// <summary>
/// Bytes of the model already on disk or received so far, out of the whole model.
/// </summary>
internal readonly record struct TranslationDownloadProgress(long ReceivedBytes, long TotalBytes);

/// <summary>
/// Why the translation model could not be prepared or removed.
/// </summary>
internal enum TranslationModelError
{
    /// <summary>The server or the connection failed, or the transfer stalled.</summary>
    Network,

    /// <summary>A file arrived with the wrong size or SHA-256.</summary>
    Corrupt,

    /// <summary>The model folder could not be written or cleaned.</summary>
    Disk,

    /// <summary>The drive lacks room for the missing files.</summary>
    DiskFull,
}

/// <summary>
/// A translation model download or removal failure, classified by <see cref="Error"/>.
/// </summary>
internal sealed class TranslationModelException : Exception
{
    internal TranslationModelException(TranslationModelError error, string message)
        : base(message)
    {
        Error = error;
    }

    internal TranslationModelException(TranslationModelError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    internal TranslationModelError Error { get; }
}

/// <summary>
/// Downloads the offline translation model into its own folder under
/// <see cref="AppPaths.TranslationModelsRoot"/> when the user turns translation on, and removes
/// it again from settings. Every file is pinned by size and SHA-256 in the catalog.
/// </summary>
internal sealed class TranslationModelStore
{
    /// <summary>
    /// Free space kept beyond the missing files, so a download never fills the drive.
    /// </summary>
    internal const long FreeSpaceMargin = 32L * 1024 * 1024;

    private const string TempSuffix = ".download";
    private const int BufferSize = 64 * 1024;
    private const int DiskFullHResult = unchecked((int)0x80070070);       // ERROR_DISK_FULL
    private const int HandleDiskFullHResult = unchecked((int)0x80070027); // ERROR_HANDLE_DISK_FULL

    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly TimeSpan _stallTimeout;
    private readonly Func<string, long> _availableFreeSpace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, VerificationState> _verification = new(StringComparer.OrdinalIgnoreCase);

    internal TranslationModelStore(AppPaths paths, ILogger<TranslationModelStore> log)
        : this(paths, CreateClient(), log)
    {
    }

    /// <summary>The store of one translation direction's model.</summary>
    internal TranslationModelStore(AppPaths paths, ILogger<TranslationModelStore> log, TranslationModelSpec spec)
        : this(paths, CreateClient(), log, (spec ?? throw new ArgumentNullException(nameof(spec))).Files,
            folderName: spec.FolderName)
    {
    }

    /// <summary>
    /// Supplies the transport, the file list, the stall timeout and the free-space probe so tests
    /// can run the store against small synthetic files.
    /// </summary>
    internal TranslationModelStore(AppPaths paths, HttpClient http, ILogger log,
        IReadOnlyList<TranslationModelFile>? files = null, TimeSpan? stallTimeout = null,
        Func<string, long>? availableFreeSpace = null, string? folderName = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        if (_stallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stallTimeout), "The stall timeout must be positive.");
        _availableFreeSpace = availableFreeSpace ?? QueryFreeSpace;
        Files = ValidateFiles(files ?? TranslationModelCatalog.Files);
        TotalBytes = Files.Sum(file => file.Bytes);
        ModelDirectory = Path.Combine(paths.TranslationModelsRoot, folderName ?? TranslationModelCatalog.FolderName);
    }

    internal string ModelDirectory { get; }

    internal IReadOnlyList<TranslationModelFile> Files { get; }

    internal long TotalBytes { get; }

    internal string PathFor(TranslationModelFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateFileName(file.FileName);
        return Path.Combine(ModelDirectory, file.FileName);
    }

    /// <summary>
    /// Every file is present with its exact size and pinned SHA-256. A file is hashed again only
    /// after its length or timestamp changes, so repeated checks are cheap; the first check in a
    /// process hashes the whole model, so callers on the UI thread use <see cref="IsInstalledAsync"/>.
    /// </summary>
    internal bool IsInstalled => AppArchitecture.SupportsNeuralOcr && AreAllVerified(CancellationToken.None);

    /// <summary>
    /// Runs the <see cref="IsInstalled"/> check on a worker.
    /// </summary>
    internal Task<bool> IsInstalledAsync(CancellationToken cancellationToken) =>
        AppArchitecture.SupportsNeuralOcr ? Task.Run(() => AreAllVerified(cancellationToken), cancellationToken) : Task.FromResult(false);

    /// <summary>
    /// Bytes on disk of the catalog files present in the model folder, whatever their size.
    /// </summary>
    internal long InstalledBytes
    {
        get
        {
            long total = 0;
            foreach (TranslationModelFile file in Files)
            {
                try
                {
                    var info = new FileInfo(PathFor(file));
                    if (info.Exists) total += info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file removed or locked while it is measured does not count.
                }
            }
            return total;
        }
    }

    /// <summary>
    /// Downloads every missing or damaged file on a worker, one download at a time. A caller that
    /// waits behind another download then finds the files present and sends no requests.
    /// Cancellation removes the partial file and keeps the files already completed.
    /// </summary>
    internal Task DownloadAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
        AppArchitecture.SupportsNeuralOcr ? Task.Run(() => DownloadCoreAsync(progress, cancellationToken))
            : Task.FromException(new PlatformNotSupportedException("Translation requires the x64 application."));

    /// <summary>
    /// Removes the catalog files and leftover partial downloads on a worker, then the model
    /// folder when nothing else is in it. The caller makes sure nothing holds the files open.
    /// </summary>
    internal Task DeleteAsync(CancellationToken cancellationToken) =>
        Task.Run(() => DeleteCoreAsync(cancellationToken));

    private async Task DownloadCoreAsync(IProgress<TranslationDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var missing = new List<TranslationModelFile>();
            long completed = 0;
            foreach (TranslationModelFile file in Files)
            {
                if (IsVerified(file, cancellationToken)) completed += file.Bytes;
                else missing.Add(file);
            }

            var reporter = new ProgressReporter(progress, TotalBytes);
            if (missing.Count == 0)
            {
                reporter.Boundary(TotalBytes);
                return;
            }

            long needed = missing.Sum(file => file.Bytes) + FreeSpaceMargin;
            long available = _availableFreeSpace(ModelDirectory);
            if (available < needed)
            {
                throw new TranslationModelException(TranslationModelError.DiskFull,
                    $"The translation model needs {needed} bytes of free space; {available} are available.");
            }

            CreateModelDirectory();
            reporter.Boundary(completed);
            foreach (TranslationModelFile file in missing)
            {
                await DownloadFileAsync(file, completed, reporter, cancellationToken).ConfigureAwait(false);
                completed += file.Bytes;
                reporter.Boundary(completed);
                _log.LogInformation("Stored translation model file {File}", file.FileName);
            }
        }
        catch (TranslationModelException ex)
        {
            _log.LogWarning(ex, "Could not prepare the translation model ({Error})", ex.Error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadFileAsync(TranslationModelFile file, long completedBefore, ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        string destination = PathFor(file);
        string temp = destination + TempSuffix;
        // Waiting longer than the stall timeout for headers or for the next bytes cancels the transfer.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            (HttpResponseMessage response, Stream body) = await OpenAsync(file, stall.Token, cancellationToken)
                .ConfigureAwait(false);
            using (response)
            using (body)
            {
                stall.CancelAfter(_stallTimeout);
                await WriteVerifiedAsync(file, body, temp, completedBefore, reporter, stall, cancellationToken)
                    .ConfigureAwait(false);
            }
            Place(file, temp, destination);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationModelException(TranslationModelError.Network,
                $"The download of {file.FileName} stalled.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DiskError($"Could not store {file.FileName}.", ex);
        }
        finally
        {
            // A partial file that cannot be removed now is replaced by the next download and
            // removed with the model.
            _ = TryDeleteFile(temp);
        }
    }

    /// <summary>
    /// Sends the request and checks the response before any byte is written. The address is a
    /// catalog HTTPS address, and the handler never follows a redirect from HTTPS to HTTP.
    /// </summary>
    private async Task<(HttpResponseMessage Response, Stream Body)> OpenAsync(TranslationModelFile file,
        CancellationToken transfer, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await _http.GetAsync(new Uri(file.Uri, UriKind.Absolute),
                HttpCompletionOption.ResponseHeadersRead, transfer).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != file.Bytes)
                throw Corrupt(file, $"was offered with {length} bytes instead of {file.Bytes}");
            Stream body = await response.Content.ReadAsStreamAsync(transfer).ConfigureAwait(false);
            return (response, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            response?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TranslationModelException(TranslationModelError.Network,
                $"Could not download {file.FileName}.", ex);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Streams the body into the temporary file, hashing as it goes and stopping as soon as more
    /// bytes arrive than the catalog pins.
    /// </summary>
    private async Task WriteVerifiedAsync(TranslationModelFile file, Stream body, string temp, long completedBefore,
        ProgressReporter reporter, CancellationTokenSource stall, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = BufferSize,
            PreallocationSize = file.Bytes,
        };
        long received = 0;
        string actual;
        using (var output = new FileStream(temp, options))
        using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = await ReadAsync(file, body, buffer, stall.Token, cancellationToken)
                .ConfigureAwait(false)) != 0)
            {
                if (read > file.Bytes - received)
                    throw Corrupt(file, "is longer than expected");
                stall.CancelAfter(_stallTimeout);
                sha256.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                reporter.Tick(completedBefore + received);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            actual = Convert.ToHexString(sha256.GetHashAndReset());
        }

        if (received != file.Bytes)
            throw Corrupt(file, $"ended after {received} of {file.Bytes} bytes");
        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Corrupt(file, "does not match its pinned SHA-256");
    }

    private static async ValueTask<int> ReadAsync(TranslationModelFile file, Stream body, Memory<byte> buffer,
        CancellationToken transfer, CancellationToken cancellationToken)
    {
        try
        {
            return await body.ReadAsync(buffer, transfer).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TranslationModelException(TranslationModelError.Network,
                $"The download of {file.FileName} was interrupted.", ex);
        }
    }

    /// <summary>
    /// Moves a verified download into place. Its bytes were hashed as they arrived, so the
    /// verification is recorded for the placed file instead of reading it again.
    /// </summary>
    private void Place(TranslationModelFile file, string temp, string destination)
    {
        VerificationState state = VerificationFor(destination);
        lock (state)
        {
            state.Verified = null;
            File.Move(temp, destination, overwrite: true);
            var info = new FileInfo(destination);
            if (info.Exists && info.Length == file.Bytes)
                state.Verified = new VerifiedFile(info.Length, info.LastWriteTimeUtc, file.Sha256);
        }
    }

    private bool AreAllVerified(CancellationToken cancellationToken)
    {
        foreach (TranslationModelFile file in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVerified(file, cancellationToken)) return false;
        }
        return true;
    }

    /// <summary>
    /// Reuses a successful check only while the file's length, timestamp and pinned hash match,
    /// like <see cref="OcrModelStore.IsPresent"/>. An unreadable file counts as missing.
    /// </summary>
    private bool IsVerified(TranslationModelFile file, CancellationToken cancellationToken)
    {
        string path = PathFor(file);
        VerificationState state = VerificationFor(path);
        lock (state)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.Bytes)
                {
                    state.Verified = null;
                    return false;
                }

                var current = new VerifiedFile(info.Length, info.LastWriteTimeUtc, file.Sha256);
                if (state.Verified is VerifiedFile verified && verified.Matches(current))
                    return true;

                state.Verified = null;
                if (!HasExpectedHash(path, file.Sha256, cancellationToken))
                    return false;

                // A file changed during hashing must be checked again before it is trusted.
                info.Refresh();
                if (!info.Exists || info.Length != current.Length || info.LastWriteTimeUtc != current.LastWriteTimeUtc)
                    return false;

                state.Verified = current;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.Verified = null;
                _log.LogWarning(ex, "Could not verify translation model file {File}", file.FileName);
                return false;
            }
            catch
            {
                state.Verified = null;
                throw;
            }
        }
    }

    private static bool HasExpectedHash(string path, string expected, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.SequentialScan);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.AppendData(buffer, 0, read);
        }
        return string.Equals(Convert.ToHexString(sha256.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DeleteCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Every file is tried even after a failure, so as little of the model as possible remains.
            Exception? failure = null;
            foreach (TranslationModelFile file in Files)
            {
                string path = PathFor(file);
                VerificationState state = VerificationFor(path);
                lock (state)
                {
                    state.Verified = null;
                    Exception? error = TryDeleteFile(path);
                    failure ??= error;
                }
            }

            RemoveLeftovers();
            if (failure is not null)
            {
                _log.LogWarning(failure, "Could not delete the translation model");
                throw DiskError("Could not delete the translation model.", failure);
            }
            _log.LogInformation("Deleted the translation model");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes partial downloads and then the model folder if it is empty. Neither is part of the
    /// model, so what cannot be removed now only waits for the next download or removal.
    /// </summary>
    private void RemoveLeftovers()
    {
        try
        {
            if (!Directory.Exists(ModelDirectory)) return;
            foreach (string partial in Directory.EnumerateFiles(ModelDirectory, "*" + TempSuffix))
            {
                if (TryDeleteFile(partial) is Exception error)
                    _log.LogWarning(error, "Could not remove a partial translation model download");
            }
            if (!Directory.EnumerateFileSystemEntries(ModelDirectory).Any())
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(ModelDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not clean the translation model folder");
        }
    }

    private void CreateModelDirectory()
    {
        try
        {
            Directory.CreateDirectory(ModelDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DiskError("Could not create the translation model folder.", ex);
        }
    }

    private VerificationState VerificationFor(string path) =>
        _verification.GetOrAdd(Path.GetFullPath(path), static _ => new VerificationState());

    private static TranslationModelException Corrupt(TranslationModelFile file, string reason) =>
        new(TranslationModelError.Corrupt, $"Translation model file {file.FileName} {reason}.");

    private static TranslationModelException DiskError(string message, Exception ex) =>
        new(ex.HResult is DiskFullHResult or HandleDiskFullHResult ? TranslationModelError.DiskFull : TranslationModelError.Disk,
            message, ex);

    private static Exception? TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
    }

    /// <summary>
    /// Names come only from the catalog. A name that could leave the model folder, alias another
    /// file or collide with a partial download is refused before any path is built.
    /// </summary>
    private static TranslationModelFile[] ValidateFiles(IReadOnlyList<TranslationModelFile> files)
    {
        if (files.Count == 0)
            throw new ArgumentException("The translation model needs at least one file.", nameof(files));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new TranslationModelFile[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            TranslationModelFile file = files[i];
            if (file is null)
                throw new ArgumentException("A translation model file is missing.", nameof(files));
            ValidateFileName(file.FileName);
            if (!names.Add(file.FileName))
                throw new ArgumentException($"Translation model file {file.FileName} is listed twice.", nameof(files));
            if (!Uri.TryCreate(file.Uri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException($"Translation model file {file.FileName} must use HTTPS.", nameof(files));
            if (file.Bytes <= 0)
                throw new ArgumentException($"Translation model file {file.FileName} needs a size.", nameof(files));
            if (file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))
                throw new ArgumentException($"Translation model file {file.FileName} needs a SHA-256.", nameof(files));
            validated[i] = file;
        }
        return validated;
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.IndexOfAny(InvalidNameChars) >= 0
            || fileName[^1] is '.' or ' '
            || fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsafe translation model file name: {fileName}", nameof(fileName));
        }
    }

    /// <summary>
    /// Free bytes on the drive holding <paramref name="directory"/>. A share or an unreadable
    /// volume reports no limit and leaves the check to the writes themselves.
    /// </summary>
    private static long QueryFreeSpace(string directory)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private static HttpClient CreateClient()
    {
        // The stall timer bounds each request instead: a fixed overall timeout would fail a slow
        // but steady connection partway through the model.
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyCapture (translation model download)");
        return client;
    }

    /// <summary>
    /// Reports at every file boundary and at most every <see cref="ProgressInterval"/> inside a file.
    /// </summary>
    private sealed class ProgressReporter(IProgress<TranslationDownloadProgress>? progress, long totalBytes)
    {
        private long _lastReport;

        internal void Boundary(long receivedBytes)
        {
            if (progress is null) return;
            progress.Report(new TranslationDownloadProgress(receivedBytes, totalBytes));
            _lastReport = Stopwatch.GetTimestamp();
        }

        internal void Tick(long receivedBytes)
        {
            if (progress is not null && Stopwatch.GetElapsedTime(_lastReport) >= ProgressInterval)
                Boundary(receivedBytes);
        }
    }

    private sealed class VerificationState
    {
        internal VerifiedFile? Verified { get; set; }
    }

    private readonly record struct VerifiedFile(long Length, DateTime LastWriteTimeUtc, string Sha256)
    {
        internal bool Matches(VerifiedFile other) =>
            Length == other.Length
            && LastWriteTimeUtc == other.LastWriteTimeUtc
            && string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
