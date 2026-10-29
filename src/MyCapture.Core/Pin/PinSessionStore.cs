using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;

namespace MyCapture.Core.Pin;

public enum PinSessionContentKind { Image, Text, Table }

public sealed record PinSessionPin(
    Guid Id,
    PinSessionContentKind Kind,
    string? OriginalText,
    string SourcePageUrl,
    bool UsesDeviceIndependentPixels,
    double ImageWidthDip,
    double ImageHeightDip,
    double Zoom,
    double Opacity,
    PinClosedPlacement? Placement,
    long OpenedOrder = 0,
    PinTextStyle? TextStyle = null,
    PinCodeDocument? CodeDocument = null,
    string? SourceUrl = null,
    string? SourceDocumentPath = null,
    string? SourceAppName = null,
    string? SourceAppExecutablePath = null,
    string? SourceWindowTitle = null);

/// <param name="PngBytes">Null reuses this content's previously saved PNG.</param>
/// <param name="ProtectionPending">The saved image could not be decrypted and must be kept for a later retry.</param>
public sealed record PinSessionSnapshot(PinSessionPin Pin, byte[]? PngBytes, bool ProtectionPending = false);

public sealed record PinSessionLoadResult(IReadOnlyList<PinSessionSnapshot> Pins, bool ManifestUnreadable);

/// <summary>How a session save ended. Only <see cref="Committed"/> replaced the saved session.</summary>
public enum PinSessionSaveOutcome
{
    /// <summary>The manifest was replaced. It can still omit requested records.</summary>
    Committed,
    /// <summary>The caller's commit check withdrew the save; the previous session was kept.</summary>
    Canceled,
    /// <summary>The saved manifest could not be read, so it was kept instead of being overwritten.</summary>
    ManifestUnreadable,
    /// <summary>A storage or encoding failure stopped the save; the previous session was kept.</summary>
    Failed,
    /// <summary>
    /// The app stopped waiting. The previous session normally remains, but a write that had
    /// already passed its final check can still land.
    /// </summary>
    TimedOut,
    /// <summary>Nothing was saved because session restore is off or the app is closing.</summary>
    NotRun,
}

/// <summary>Why a requested record is not in the session a save committed.</summary>
public enum PinSessionMissingReason
{
    /// <summary>The save did not replace the session, so none of its records were saved.</summary>
    NotCommitted,
    Invalid,
    /// <summary>The record fell outside the pin count or image byte limit.</summary>
    OverLimit,
    /// <summary>The record had no valid PNG and no saved image for its content.</summary>
    ImageUnavailable,
    /// <summary>Its image could not be read back for reuse or written.</summary>
    ImageWriteFailed,
    /// <summary>Its image could not be encrypted.</summary>
    ProtectionFailed,
}

public sealed record PinSessionMissingPin(Guid PinId, PinSessionMissingReason Reason);

/// <summary>Exactly which requested records one save left in the session.</summary>
/// <param name="Generation">
/// The store's commit generation after the save. It changes whenever that store replaces or
/// clears the session and is never reused by another store in the process.
/// </param>
/// <param name="RequestedCount">Requested records; windows showing the same content share a pin ID.</param>
/// <param name="SavedPinIds">One ID per saved record in stacking order; empty unless committed.</param>
/// <param name="MissingPins">Every requested record the committed session lacks, with its reason.</param>
public sealed record PinSessionSaveResult(
    PinSessionSaveOutcome Outcome,
    long Generation,
    int RequestedCount,
    IReadOnlyList<Guid> SavedPinIds,
    IReadOnlyList<PinSessionMissingPin> MissingPins)
{
    public bool ManifestCommitted => Outcome == PinSessionSaveOutcome.Committed;

    /// <summary>
    /// Every requested record is in the committed session. Closing or replacing the live pins also
    /// needs <see cref="PinSessionStore.VerifyReadBack"/>; see <see cref="PinSessionStore.SaveDetailed"/>.
    /// </summary>
    public bool IsComplete => ManifestCommitted && MissingPins.Count == 0 && SavedPinIds.Count == RequestedCount;

    /// <summary>A save that left the previous session in place, so none of its records were saved.</summary>
    public static PinSessionSaveResult NotCommitted(PinSessionSaveOutcome outcome, long generation,
        IEnumerable<Guid> requested)
    {
        if (outcome == PinSessionSaveOutcome.Committed) throw new ArgumentOutOfRangeException(nameof(outcome));
        ArgumentNullException.ThrowIfNull(requested);
        PinSessionMissingPin[] missing = requested
            .Select(id => new PinSessionMissingPin(id, PinSessionMissingReason.NotCommitted)).ToArray();
        return new(outcome, generation, missing.Length, [], missing);
    }
}

/// <summary>Persists the opt-in open-pin session without depending on WPF.</summary>
public sealed class PinSessionStore
{
    // Bound disk usage and launch work while retaining the most recently opened pins.
    public const int DefaultMaxPins = 50;
    public const long DefaultMaxPngBytes = 256L * 1024 * 1024;
    // Optional rendering metadata is ignored by the 4.5.0 reader, which still restores the PNG.
    private const int SchemaVersion = 1;
    private const string ManifestName = "session.json";
    // DPAPI adds a small envelope; bound the allocation before reading untrusted files.
    private const int ProtectedImageOverheadLimit = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters =
        {
            new JsonStringEnumConverter<PinSessionContentKind>(),
            new JsonStringEnumConverter<PinTextStyle>()
        }
    };
    private static readonly uint[] CrcTable = CreateCrcTable();
    // One sequence for every store, so a result from one board's store cannot verify against another's.
    private static long s_lastGeneration;
    private readonly string _root;
    private readonly ILogger _log;
    private readonly int _maxPins;
    private readonly long _maxPngBytes;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ImageStamp> _knownImages = [];
    private HashSet<Guid>? _committedReferences;
    private Dictionary<Guid, long> _committedImageLengths = [];
    private bool _manifestUnreadable;
    private bool _manifestProtected;
    private long _generation;

    public PinSessionStore(string root, ILogger log,
        int maxPins = DefaultMaxPins, long maxPngBytes = DefaultMaxPngBytes)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPins);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPngBytes);
        _root = ValidatedFilePaths.DirectoryRoot(root);
        _log = log;
        _maxPins = maxPins;
        _maxPngBytes = maxPngBytes;
    }

    public int MaxPins => _maxPins;
    public long MaxPngBytes => _maxPngBytes;
    /// <summary>Read without the save lock, so a caller that stopped waiting for a save can still report it.</summary>
    public long Generation => Interlocked.Read(ref _generation);
    internal Func<Guid, long>? ReadImageLengthForTest { get; set; }
    internal Action? BeforeManifestCommitForTest { get; set; }
    internal Action<Guid>? BeforeImageProtectForTest { get; set; }
    internal Action<Guid>? BeforeImageWriteForTest { get; set; }

    public IReadOnlyList<PinSessionSnapshot> Load() => LoadDetailed().Pins;

    public PinSessionLoadResult LoadDetailed()
    {
        lock (_gate)
        {
            if (!TryReadManifest(out SessionManifest? manifest)) return new([], ManifestUnreadable: true);
            if (manifest is null) return new([], ManifestUnreadable: false);
            var restored = new List<(int Index, PinSessionSnapshot Snapshot)>();
            var images = new Dictionary<Guid, byte[]?>();
            var protectionPending = new HashSet<Guid>();
            long totalBytes = 0;
            int budgetSkipped = 0;
            foreach (int i in NewestFirst(manifest.Pins))
            {
                PinSessionPin pin = manifest.Pins[i];
                if (!IsValid(pin))
                {
                    _log.LogWarning("Skipped an invalid saved pin record.");
                    continue;
                }
                if (restored.Count >= _maxPins)
                {
                    budgetSkipped++;
                    continue;
                }
                long length = 0;
                try
                {
                    if (!images.TryGetValue(pin.Id, out byte[]? bytes))
                    {
                        string path = ImagePath(pin.Id);
                        // The hook models a metadata failure before any image bytes are read.
                        length = ReadImageLengthForTest?.Invoke(pin.Id) ?? 0;
                        if (length > _maxPngBytes - totalBytes)
                        {
                            budgetSkipped++;
                            continue;
                        }
                        if (length == 0 && _knownImages.TryGetValue(pin.Id, out ImageStamp known))
                        {
                            var info = new FileInfo(path);
                            if (known.DiskLength == info.Length && known.LastWriteTimeUtc == info.LastWriteTimeUtc)
                                length = known.Length;
                        }
                        if (length > _maxPngBytes - totalBytes)
                        {
                            budgetSkipped++;
                            continue;
                        }
                        bytes = PinSessionProtection.Read(ReadImageFile(path), PinSessionFilePurpose.Image,
                            out bool isProtected);
                        if (!IsPng(bytes)) throw new InvalidDataException("Invalid PNG data.");
                        length = bytes.LongLength;
                        if (length > _maxPngBytes - totalBytes)
                        {
                            budgetSkipped++;
                            continue;
                        }
                        images.Add(pin.Id, bytes);
                        totalBytes += bytes.LongLength;
                        RememberImage(pin.Id, path, length, isProtected);
                    }
                    restored.Add((i, new PinSessionSnapshot(pin, bytes, protectionPending.Contains(pin.Id))));
                }
                catch (CryptographicException ex)
                {
                    // A different user profile or an unsupported envelope is not evidence
                    // that the image is corrupt. Keep its record without restoring a window.
                    _knownImages.Remove(pin.Id);
                    images[pin.Id] = null;
                    protectionPending.Add(pin.Id);
                    if (_manifestProtected && _committedImageLengths.TryGetValue(pin.Id, out long savedLength))
                        totalBytes = Math.Min(_maxPngBytes, totalBytes + savedLength);
                    restored.Add((i, new PinSessionSnapshot(pin, null, ProtectionPending: true)));
                    _log.LogWarning("Kept protected saved pin {PinId} for a later decryption retry ({FailureType}).", pin.Id, ex.GetType().Name);
                }
                catch (Exception ex) when (IsTransientReadFailure(ex))
                {
                    // A sharing violation is not evidence that the user's content is gone.
                    // Preserve unmeasurable committed files without evicting other records;
                    // the byte budget bounds measurable images until metadata is readable again.
                    images[pin.Id] = null;
                    if (length == 0 && TryGetImageLength(pin.Id, out long knownLength)) length = knownLength;
                    totalBytes = Math.Min(_maxPngBytes, totalBytes + length);
                    restored.Add((i, new PinSessionSnapshot(pin, null)));
                    _log.LogWarning("Kept temporarily unreadable saved pin {PinId} for a later save ({FailureType}).", pin.Id, ex.GetType().Name);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    _knownImages.Remove(pin.Id);
                    _log.LogWarning("Skipped missing or corrupt saved pin {PinId} ({FailureType}).", pin.Id, ex.GetType().Name);
                }
            }
            LogBudgetSkipped(budgetSkipped);
            return new(restored.OrderBy(item => item.Index).Select(item => item.Snapshot).ToList(), ManifestUnreadable: false);
        }
    }

    /// <summary>Test shorthand: true only means the manifest was replaced, possibly without some pins.</summary>
    internal bool Save(IReadOnlyList<PinSessionSnapshot> pins, Func<bool>? canCommit = null) =>
        SaveDetailed(pins, canCommit).ManifestCommitted;

    /// <summary>
    /// Replaces the saved session with the requested records that can be kept and reports exactly
    /// which ones the committed manifest holds and why any others are missing.
    /// </summary>
    /// <remarks>
    /// A committed save can be partial: invalid records, records over the count or byte limit and
    /// records whose image cannot be written are left out. A caller that would close or replace the
    /// live pins it saved, such as a switch between pin boards, keeps every one of them open unless
    /// the result <see cref="PinSessionSaveResult.IsComplete"/> and <see cref="VerifyReadBack"/> then
    /// confirms it from disk. After any other result the live pins may be the only copy.
    /// </remarks>
    public PinSessionSaveResult SaveDetailed(IReadOnlyList<PinSessionSnapshot> pins, Func<bool>? canCommit = null)
    {
        ArgumentNullException.ThrowIfNull(pins);
        lock (_gate)
        {
            var writtenImages = new HashSet<Guid>();
            var failures = new PinSessionMissingReason?[pins.Count];
            PinSessionSaveResult? result = null;
            bool committed = false;
            try
            {
                if (canCommit?.Invoke() == false) return NotCommitted(pins, PinSessionSaveOutcome.Canceled);
                if (_manifestUnreadable) return NotCommitted(pins, PinSessionSaveOutcome.ManifestUnreadable);
                if (_committedReferences is null && !TryReadManifest(out _))
                    return NotCommitted(pins, PinSessionSaveOutcome.ManifestUnreadable);
                if (canCommit?.Invoke() == false) return NotCommitted(pins, PinSessionSaveOutcome.Canceled);
                ValidatedFilePaths.CreateDirectory(_root);
                var saved = new List<(int Index, PinSessionPin Pin)>();
                var retainedImages = new HashSet<Guid>();
                var retainedLengths = new Dictionary<Guid, long>();
                var imageFailures = new Dictionary<Guid, PinSessionMissingReason>();
                long totalBytes = 0;
                int budgetSkipped = 0;
                foreach (int i in NewestFirst(pins.Select(snapshot => snapshot.Pin).ToList())
                    .OrderByDescending(index => pins[index].ProtectionPending))
                {
                    PinSessionSnapshot snapshot = pins[i];
                    PinSessionPin pin = snapshot.Pin;
                    if (!IsValid(pin))
                    {
                        _log.LogWarning("Skipped an invalid open pin record.");
                        failures[i] = PinSessionMissingReason.Invalid;
                        continue;
                    }
                    if (saved.Count >= _maxPins)
                    {
                        budgetSkipped++;
                        failures[i] = PinSessionMissingReason.OverLimit;
                        continue;
                    }
                    // The step that can still fail, so a skipped pin reports why it was left out.
                    PinSessionMissingReason step = PinSessionMissingReason.ImageWriteFailed;
                    try
                    {
                        if (!retainedImages.Contains(pin.Id))
                        {
                            bool reuse;
                            long length;
                            byte[]? legacyBytes;
                            try { reuse = TryGetExistingImageLength(pin.Id, out length, out legacyBytes); }
                            catch (CryptographicException) when (!snapshot.ProtectionPending && snapshot.PngBytes is not null)
                            {
                                // A live pin still has its original pixels and can repair a damaged envelope.
                                reuse = false;
                                length = 0;
                                legacyBytes = null;
                            }
                            catch (Exception ex) when ((snapshot.PngBytes is null || snapshot.ProtectionPending
                                || _committedReferences?.Contains(pin.Id) == true)
                                && (ex is CryptographicException || IsTransientReadFailure(ex)))
                            {
                                // Carry a pending-read record unchanged instead of making its image
                                // an orphan merely because another process still has it locked.
                                retainedImages.Add(pin.Id);
                                saved.Add((i, pin));
                                // Unknown committed bytes must not consume the entire budget and
                                // silently remove unrelated, readable records from the session.
                                long reserved = ex is CryptographicException
                                    ? (_manifestProtected ? _committedImageLengths.GetValueOrDefault(pin.Id) : 0)
                                    : TryGetImageLength(pin.Id, out long knownLength) ? knownLength : 0;
                                if (reserved > 0) retainedLengths[pin.Id] = reserved;
                                totalBytes = Math.Min(_maxPngBytes, totalBytes + reserved);
                                _log.LogWarning("Kept temporarily unreadable saved pin {PinId} during save ({FailureType}).", pin.Id, ex.GetType().Name);
                                continue;
                            }
                            if (!reuse)
                            {
                                if (snapshot.PngBytes is null || !IsPng(snapshot.PngBytes))
                                {
                                    step = PinSessionMissingReason.ImageUnavailable;
                                    throw new InvalidDataException("The pin has no valid PNG to save.");
                                }
                                length = snapshot.PngBytes.LongLength;
                            }
                            if (length > _maxPngBytes - totalBytes)
                            {
                                budgetSkipped++;
                                failures[i] = PinSessionMissingReason.OverLimit;
                                imageFailures.TryAdd(pin.Id, PinSessionMissingReason.OverLimit);
                                continue;
                            }
                            if (!reuse || legacyBytes is not null)
                            {
                                if (canCommit?.Invoke() == false) return NotCommitted(pins, PinSessionSaveOutcome.Canceled);
                                string path = ImagePath(pin.Id);
                                step = PinSessionMissingReason.ProtectionFailed;
                                BeforeImageProtectForTest?.Invoke(pin.Id);
                                byte[] protectedBytes = PinSessionProtection.Protect(legacyBytes ?? snapshot.PngBytes!,
                                    PinSessionFilePurpose.Image);
                                step = PinSessionMissingReason.ImageWriteFailed;
                                try
                                {
                                    BeforeImageWriteForTest?.Invoke(pin.Id);
                                    AtomicFile.WriteExportBytes(path, protectedBytes);
                                }
                                catch (Exception ex) when (legacyBytes is not null && IsTransientReadFailure(ex))
                                {
                                    // A readable legacy file can still deny replacement. Do not commit
                                    // a manifest that drops its record just because migration is blocked.
                                    _log.LogWarning("Could not migrate a saved pin image ({FailureType}).", ex.GetType().Name);
                                    return NotCommitted(pins, PinSessionSaveOutcome.Failed);
                                }
                                writtenImages.Add(pin.Id);
                                RememberImage(pin.Id, path, length, isProtected: true);
                            }
                            totalBytes += length;
                            retainedImages.Add(pin.Id);
                            retainedLengths[pin.Id] = length;
                        }
                        saved.Add((i, pin));
                    }
                    catch (Exception ex) when (IsStorageFailure(ex))
                    {
                        _log.LogWarning("Could not save pin {PinId}; skipped this pin ({FailureType}).", pin.Id, ex.GetType().Name);
                        // A window sharing content whose image already failed reports that cause, not its missing PNG.
                        if (step == PinSessionMissingReason.ImageUnavailable
                            && imageFailures.TryGetValue(pin.Id, out PinSessionMissingReason earlier)) step = earlier;
                        failures[i] = step;
                        imageFailures.TryAdd(pin.Id, step);
                    }
                }
                List<PinSessionPin> orderedPins = saved.OrderBy(item => item.Index).Select(item => item.Pin).ToList();
                // Commit the references only after their images exist; clean orphans only
                // after the atomic replacement succeeds, keeping the old session recoverable.
                if (canCommit?.Invoke() == false) return NotCommitted(pins, PinSessionSaveOutcome.Canceled);
                if (!AtomicFile.TryWriteExportBytes(FilePath(ManifestName, ".json"),
                    PinSessionProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(
                        new SessionManifest(SchemaVersion, orderedPins, retainedLengths), JsonOptions), PinSessionFilePurpose.Manifest), () =>
                    {
                        BeforeManifestCommitForTest?.Invoke();
                        return canCommit?.Invoke() != false;
                    })) return NotCommitted(pins, PinSessionSaveOutcome.Canceled);
                committed = true;
                _committedReferences = retainedImages;
                _committedImageLengths = retainedLengths;
                _manifestProtected = true;
                PinSessionMissingPin[] missing = Enumerable.Range(0, pins.Count)
                    .Where(index => failures[index] is not null)
                    .Select(index => new PinSessionMissingPin(pins[index].Pin?.Id ?? Guid.Empty, failures[index]!.Value))
                    .ToArray();
                result = new(PinSessionSaveOutcome.Committed, NextGeneration(), pins.Count,
                    orderedPins.Select(pin => pin.Id).ToArray(), missing);
                LogBudgetSkipped(budgetSkipped);
                CleanupFiles(retainedImages, clear: false);
                return result;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning("Could not save the open-pin session ({FailureType}).", ex.GetType().Name);
                return result ?? NotCommitted(pins, PinSessionSaveOutcome.Failed);
            }
            finally
            {
                if (!committed) CleanupFailedSaveImages(writtenImages);
            }
        }
    }

    /// <summary>
    /// Reads the session back with a separate reader. True only when <paramref name="result"/> is a
    /// complete save that is still this store's latest commit and every saved record has a
    /// readable image, so the live pins it describes may be closed or replaced.
    /// </summary>
    /// <remarks>
    /// It decrypts every saved image, so call it off the UI thread. A carried record whose image
    /// cannot be decrypted, for example after a Windows profile change, keeps it false.
    /// </remarks>
    public bool VerifyReadBack(PinSessionSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Checked before the lock too, so a result that cannot verify never waits behind a stalled save.
        if (!result.IsComplete || result.Generation != Generation) return false;
        lock (_gate)
        {
            if (result.Generation != Generation) return false;
            // A failed verification read must not block this store's later saves the way its own reads do,
            // nor log as if a session were being restored.
            PinSessionLoadResult loaded;
            try { loaded = new PinSessionStore(_root, NullLogger.Instance, _maxPins, _maxPngBytes).LoadDetailed(); }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning("Could not read back the saved pin session ({FailureType}).", ex.GetType().Name);
                return false;
            }
            return !loaded.ManifestUnreadable
                && loaded.Pins.Select(entry => entry.Pin.Id).SequenceEqual(result.SavedPinIds)
                && loaded.Pins.All(entry => entry.PngBytes is not null && !entry.ProtectionPending);
        }
    }

    /// <summary>Returns the plaintext PNG length, using validated metadata or decrypting an uncached image.</summary>
    public bool TryGetImageLength(Guid id, out long length)
    {
        lock (_gate)
        {
            length = 0;
            if (id == Guid.Empty) return false;
            try
            {
                if (ReadImageLengthForTest is { } readLength) length = readLength(id);
                else
                {
                    string path = ImagePath(id);
                    var info = new FileInfo(path);
                    long diskLength = info.Length;
                    if (diskLength > ImageReadLimit) return false;
                    if (_knownImages.TryGetValue(id, out ImageStamp known)
                        && known.DiskLength == diskLength && known.LastWriteTimeUtc == info.LastWriteTimeUtc)
                        length = known.Length;
                    else
                    {
                        try
                        {
                            byte[] bytes = PinSessionProtection.Read(ReadImageFile(path), PinSessionFilePurpose.Image,
                                out bool isProtected);
                            if (!IsPng(bytes)) return false;
                            length = bytes.LongLength;
                            RememberImage(id, path, length, isProtected);
                        }
                        catch (Exception ex) when (ex is CryptographicException || IsTransientReadFailure(ex))
                        {
                            // The encrypted manifest carries lengths for locked or protection-pending images.
                            // A legacy manifest has no lengths, and its PNG length equals its disk length.
                            if (_committedReferences is null && !TryReadManifest(out _)) return false;
                            if ((ex is not CryptographicException || _manifestProtected)
                                && _committedImageLengths.TryGetValue(id, out long savedLength)) length = savedLength;
                            else if (IsTransientReadFailure(ex) && !_manifestProtected
                                && _committedReferences?.Contains(id) == true) length = diskLength;
                            else return false;
                        }
                    }
                }
                return length > 0;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                if (IsTransientReadFailure(ex) && _knownImages.TryGetValue(id, out ImageStamp known))
                {
                    length = known.Length;
                    return true;
                }
                return false;
            }
        }
    }

    public bool HasImage(Guid id)
    {
        lock (_gate)
        {
            try { return id != Guid.Empty && TryGetExistingImageLength(id, out _, out _); }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning("Could not reuse saved pin image {PinId} ({FailureType}).", id, ex.GetType().Name);
                return false;
            }
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            // Even a partial clear can remove the manifest, so no earlier commit verifies afterwards.
            NextGeneration();
            try
            {
                bool success = CleanupFiles([], clear: true);
                _knownImages.Clear();
                _committedImageLengths.Clear();
                _manifestProtected = false;
                _committedReferences = success ? [] : null;
                if (success) _manifestUnreadable = false;
                return success;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning("Could not clear the saved pin session ({FailureType}).", ex.GetType().Name);
                return false;
            }
        }
    }

    private bool TryGetExistingImageLength(Guid id, out long length, out byte[]? legacyBytes)
    {
        length = 0;
        legacyBytes = null;
        string path = ImagePath(id);
        var info = new FileInfo(path);
        long diskLength;
        try
        {
            _ = ReadImageLengthForTest?.Invoke(id);
            diskLength = info.Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            length = 0;
            return false;
        }
        if (diskLength > ImageReadLimit) return false;
        if (_knownImages.TryGetValue(id, out ImageStamp known) && known.IsProtected
            && known.DiskLength == diskLength && known.LastWriteTimeUtc == info.LastWriteTimeUtc)
        {
            length = known.Length;
            return length > 0 && length <= _maxPngBytes;
        }
        try
        {
            byte[] bytes = PinSessionProtection.Read(ReadImageFile(path), PinSessionFilePurpose.Image,
                out bool isProtected);
            if (!IsPng(bytes)) return false;
            length = bytes.LongLength;
            if (length <= 0 || length > _maxPngBytes) return false;
            RememberImage(id, path, length, isProtected);
            if (!isProtected) legacyBytes = bytes;
            return true;
        }
        catch (CryptographicException)
        {
            // Save must distinguish an unreadable envelope from a decoded, invalid PNG.
            _knownImages.Remove(id);
            throw;
        }
        catch (InvalidDataException ex)
        {
            _knownImages.Remove(id);
            _log.LogWarning("Could not reuse protected pin image {PinId} ({FailureType}).", id, ex.GetType().Name);
            return false;
        }
    }

    private void RememberImage(Guid id, string path, long length, bool isProtected)
    {
        var info = new FileInfo(path);
        _knownImages[id] = new ImageStamp(info.Length, info.LastWriteTimeUtc, length, isProtected);
    }

    private byte[] ReadImageFile(string path)
    {
        using var stream = File.OpenRead(path);
        long length = stream.Length;
        if (length > ImageReadLimit) throw new InvalidDataException("Saved pin image exceeds its size limit.");
        byte[] bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Saved pin image changed while reading.");
        return bytes;
    }

    private long ImageReadLimit =>
        Math.Min(_maxPngBytes, Array.MaxLength - ProtectedImageOverheadLimit) + ProtectedImageOverheadLimit;

    private bool CleanupFiles(HashSet<Guid> retainedImages, bool clear)
    {
        string root = ValidatedFilePaths.DirectoryRoot(_root);
        string[] files;
        try { files = Directory.GetFiles(root); }
        catch (DirectoryNotFoundException) { return true; }
        bool success = true;
        foreach (string candidate in files)
        {
            string name = Path.GetFileName(candidate);
            bool image = Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _);
            bool manifest = name.Equals(ManifestName, StringComparison.Ordinal);
            bool temporary = IsOwnedTemporaryName(name);
            if (!(image || temporary || (clear && manifest))) continue;
            if (!clear && image && retainedImages.Contains(Guid.ParseExact(Path.GetFileNameWithoutExtension(name), "N")))
                continue;
            try
            {
                string path = FilePath(name, Path.GetExtension(name));
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(path);
                if (image) _knownImages.Remove(Guid.ParseExact(Path.GetFileNameWithoutExtension(name), "N"));
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                success = false;
                _log.LogWarning("Could not remove an obsolete saved pin file ({FailureType}).", ex.GetType().Name);
            }
        }
        return success;
    }

    private bool TryReadManifest(out SessionManifest? manifest)
    {
        manifest = null;
        bool isProtected = false;
        try
        {
            byte[] bytes = PinSessionProtection.Read(File.ReadAllBytes(FilePath(ManifestName, ".json")),
                PinSessionFilePurpose.Manifest, out isProtected);
            if (!isProtected && !IsLegacyManifest(bytes))
            {
                isProtected = true;
                throw new CryptographicException("Unrecognized pin session protection header.");
            }
            // JSON stream parsing accepted a UTF-8 BOM in legacy manifests.
            if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int schemaVersion) || schemaVersion != SchemaVersion
                || !root.TryGetProperty("pins", out JsonElement pins) || pins.ValueKind != JsonValueKind.Array)
                throw new JsonException("The pin session schema is invalid or unsupported.");
            var records = new List<PinSessionPin>();
            foreach (JsonElement item in pins.EnumerateArray())
            {
                try
                {
                    PinSessionPin? pin = item.Deserialize<PinSessionPin>(JsonOptions);
                    if (pin is not null) records.Add(pin);
                }
                catch (JsonException ex)
                {
                    _log.LogWarning("Skipped an invalid saved pin record ({FailureType}).", ex.GetType().Name);
                }
            }
            var imageLengths = new Dictionary<Guid, long>();
            if (root.TryGetProperty("imageLengths", out JsonElement lengths) && lengths.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in lengths.EnumerateObject())
                    if (Guid.TryParse(property.Name, out Guid id) && property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt64(out long length) && length > 0 && length <= Array.MaxLength)
                        imageLengths[id] = length;
            }
            manifest = new SessionManifest(schemaVersion, records, imageLengths);
            _committedReferences = manifest.Pins.Where(pin => pin is not null).Select(pin => pin.Id).ToHashSet();
            _committedImageLengths = imageLengths;
            _manifestProtected = isProtected;
            _manifestUnreadable = false;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _committedReferences = [];
            _committedImageLengths.Clear();
            _manifestProtected = false;
            _manifestUnreadable = false;
            return true;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            manifest = null;
            _manifestUnreadable = isProtected || ex is CryptographicException || IsTransientReadFailure(ex);
            _committedReferences = _manifestUnreadable ? null : [];
            _committedImageLengths.Clear();
            _log.LogWarning("Could not read the saved pin session; no pins were restored ({FailureType}).", ex.GetType().Name);
            return !_manifestUnreadable;
        }
    }

    private long NextGeneration()
    {
        long next = Interlocked.Increment(ref s_lastGeneration);
        Interlocked.Exchange(ref _generation, next);
        return next;
    }

    private PinSessionSaveResult NotCommitted(IReadOnlyList<PinSessionSnapshot> pins, PinSessionSaveOutcome outcome) =>
        PinSessionSaveResult.NotCommitted(outcome, Generation, pins.Select(snapshot => snapshot?.Pin?.Id ?? Guid.Empty));

    private void CleanupFailedSaveImages(HashSet<Guid> writtenImages)
    {
        // Unknown references are never treated as an empty committed session.
        if (_committedReferences is null) return;
        foreach (Guid id in writtenImages)
        {
            if (_committedReferences.Contains(id)) continue;
            try
            {
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(ImagePath(id));
                _knownImages.Remove(id);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning("Could not remove an uncommitted saved pin image {PinId} ({FailureType}).", id, ex.GetType().Name);
            }
        }
    }

    private static bool IsLegacyManifest(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return false; }
        ReadOnlySpan<char> candidate = text.AsSpan().TrimStart();
        if (candidate.Length > 0 && candidate[0] == '\uFEFF') candidate = candidate[1..].TrimStart();
        if (candidate.Length == 0) return false;
        return candidate.IndexOf('\0') < 0 && candidate[0] is '{' or '[' or '"' or 'n' or 't' or 'f' or '-'
            or >= '0' and <= '9';
    }

    private static bool IsOwnedTemporaryName(string name)
    {
        if (!name.EndsWith(".tmp", StringComparison.Ordinal)) return false;
        string stem = name[..^4];
        int separator = stem.LastIndexOf('.');
        if (separator < 0 || !Guid.TryParseExact(stem[(separator + 1)..], "N", out _)) return false;
        string target = stem[..separator];
        return target == ManifestName || (target.EndsWith(".png", StringComparison.Ordinal)
            && Guid.TryParseExact(target[..^4], "N", out _));
    }

    private string FilePath(string name, string extension) =>
        ValidatedFilePaths.FileWithinRoot(_root, name, extension);

    private string ImagePath(Guid id) => FilePath($"{id:N}.png", ".png");

    private void LogBudgetSkipped(int count)
    {
        if (count > 0) _log.LogWarning("Skipped {SkippedPins} older pins to keep the saved session within its budget.", count);
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException
            or NotSupportedException or CryptographicException;

    private static bool IsTransientReadFailure(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && ex is not FileNotFoundException and not DirectoryNotFoundException);

    // Input order is stacking order. Opening age only selects the retained subset;
    // older manifests with no age use their existing input order as the tie-breaker.
    private static IEnumerable<int> NewestFirst(IReadOnlyList<PinSessionPin> pins) =>
        Enumerable.Range(0, pins.Count)
            .OrderByDescending(index => Math.Max(0, pins[index]?.OpenedOrder ?? 0))
            .ThenByDescending(index => index);

    private static bool IsValid(PinSessionPin? pin) => pin is not null && pin.Id != Guid.Empty
        && Enum.IsDefined(pin.Kind) && pin.SourcePageUrl is not null
        && double.IsFinite(pin.ImageWidthDip) && pin.ImageWidthDip > 0
        && double.IsFinite(pin.ImageHeightDip) && pin.ImageHeightDip > 0
        && double.IsFinite(pin.Zoom) && pin.Zoom > 0
        && double.IsFinite(pin.Opacity) && pin.Opacity is >= 0 and <= 1
        && HasValidRenderingMetadata(pin)
        && (pin.Placement is not { } placement || (IsFinite(placement.Bounds) && IsFinite(placement.DisplayBounds)));

    private static bool HasValidRenderingMetadata(PinSessionPin pin)
    {
        if (pin.TextStyle is not { } style) return pin.CodeDocument is null;
        if (!Enum.IsDefined(style) || !pin.UsesDeviceIndependentPixels || pin.OriginalText is null)
            return false;
        return style switch
        {
            PinTextStyle.PlainText => pin.Kind == PinSessionContentKind.Text && pin.CodeDocument is null,
            PinTextStyle.Markdown => pin.Kind == PinSessionContentKind.Text && pin.CodeDocument is null,
            PinTextStyle.Grid => pin.Kind == PinSessionContentKind.Table && pin.CodeDocument is null,
            PinTextStyle.Code => pin.Kind == PinSessionContentKind.Text
                && pin.CodeDocument is { } document && document.IsValid()
                && document.MatchesOriginalText(pin.OriginalText),
            _ => false,
        };
    }

    private static bool IsFinite(RectD rect) => double.IsFinite(rect.X) && double.IsFinite(rect.Y)
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    private static bool IsPng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.StartsWith(signature)) return false;
        int offset = signature.Length;
        bool header = false, data = false;
        while (bytes.Length - offset >= 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > bytes.Length - offset - 12) return false;
            int size = (int)length;
            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            ReadOnlySpan<byte> payload = bytes.Slice(offset + 8, size);
            if (!header)
            {
                if (!type.SequenceEqual("IHDR"u8) || size != 13
                    || BinaryPrimitives.ReadUInt32BigEndian(payload) == 0
                    || BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) == 0) return false;
                header = true;
            }
            uint crc = uint.MaxValue;
            foreach (byte b in bytes.Slice(offset + 4, size + 4))
                crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 8 + size)..])) return false;
            offset += size + 12;
            if (type.SequenceEqual("IDAT"u8)) data = true;
            if (type.SequenceEqual("IEND"u8)) return data && size == 0 && offset == bytes.Length;
        }
        return false;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private sealed record SessionManifest(int SchemaVersion, List<PinSessionPin> Pins, Dictionary<Guid, long> ImageLengths);
    private readonly record struct ImageStamp(long DiskLength, DateTime LastWriteTimeUtc, long Length, bool IsProtected);
}
