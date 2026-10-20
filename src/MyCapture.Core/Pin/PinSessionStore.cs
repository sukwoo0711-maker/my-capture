using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
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
    long OpenedOrder = 0);

/// <param name="PngBytes">Null reuses this content's previously saved PNG.</param>
public sealed record PinSessionSnapshot(PinSessionPin Pin, byte[]? PngBytes);

/// <summary>Persists the opt-in open-pin session without depending on WPF.</summary>
public sealed class PinSessionStore
{
    // Bound disk usage and launch work while retaining the most recently opened pins.
    public const int DefaultMaxPins = 50;
    public const long DefaultMaxPngBytes = 256L * 1024 * 1024;
    private const int SchemaVersion = 1;
    private const string ManifestName = "session.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter<PinSessionContentKind>() }
    };
    private static readonly uint[] CrcTable = CreateCrcTable();
    private readonly string _root;
    private readonly ILogger _log;
    private readonly int _maxPins;
    private readonly long _maxPngBytes;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ImageStamp> _knownImages = [];

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

    public IReadOnlyList<PinSessionSnapshot> Load()
    {
        lock (_gate)
        {
            try
            {
                string manifestPath = FilePath(ManifestName, ".json");
                if (!File.Exists(manifestPath)) return [];
                using var stream = File.OpenRead(manifestPath);
                SessionManifest? manifest = JsonSerializer.Deserialize<SessionManifest>(stream, JsonOptions);
                if (manifest is null || manifest.SchemaVersion != SchemaVersion || manifest.Pins is null)
                    throw new JsonException("The pin session schema is invalid or unsupported.");

                var restored = new List<(int Index, PinSessionSnapshot Snapshot)>();
                var images = new Dictionary<Guid, byte[]>();
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
                    try
                    {
                        if (!images.TryGetValue(pin.Id, out byte[]? bytes))
                        {
                            string path = ImagePath(pin.Id);
                            long length = new FileInfo(path).Length;
                            if (length > _maxPngBytes - totalBytes)
                            {
                                budgetSkipped++;
                                continue;
                            }
                            bytes = File.ReadAllBytes(path);
                            if (!IsPng(bytes)) throw new InvalidDataException("Invalid PNG data.");
                            images.Add(pin.Id, bytes);
                            totalBytes += bytes.LongLength;
                            RememberImage(pin.Id, path);
                        }
                        restored.Add((i, new PinSessionSnapshot(pin, bytes)));
                    }
                    catch (Exception ex) when (IsStorageFailure(ex))
                    {
                        _log.LogWarning(ex, "Skipped missing or corrupt saved pin {PinId}.", pin.Id);
                    }
                }
                LogBudgetSkipped(budgetSkipped);
                return restored.OrderBy(item => item.Index).Select(item => item.Snapshot).ToList();
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning(ex, "Could not read the saved pin session; no pins were restored.");
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<PinSessionSnapshot> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        lock (_gate)
        {
            try
            {
                ValidatedFilePaths.CreateDirectory(_root);
                var saved = new List<(int Index, PinSessionPin Pin)>();
                var retainedImages = new HashSet<Guid>();
                long totalBytes = 0;
                int budgetSkipped = 0;
                foreach (int i in NewestFirst(pins.Select(snapshot => snapshot.Pin).ToList()))
                {
                    PinSessionSnapshot snapshot = pins[i];
                    PinSessionPin pin = snapshot.Pin;
                    if (!IsValid(pin))
                    {
                        _log.LogWarning("Skipped an invalid open pin record.");
                        continue;
                    }
                    if (saved.Count >= _maxPins)
                    {
                        budgetSkipped++;
                        continue;
                    }
                    try
                    {
                        if (!retainedImages.Contains(pin.Id))
                        {
                            bool reuse = TryGetExistingImageLength(pin.Id, out long length);
                            if (!reuse)
                            {
                                if (snapshot.PngBytes is null || !IsPng(snapshot.PngBytes))
                                    throw new InvalidDataException("The pin has no valid PNG to save.");
                                length = snapshot.PngBytes.LongLength;
                            }
                            if (length > _maxPngBytes - totalBytes)
                            {
                                budgetSkipped++;
                                continue;
                            }
                            if (!reuse)
                            {
                                string path = ImagePath(pin.Id);
                                AtomicFile.WriteExportBytes(path, snapshot.PngBytes!);
                                RememberImage(pin.Id, path);
                            }
                            totalBytes += length;
                            retainedImages.Add(pin.Id);
                        }
                        saved.Add((i, pin));
                    }
                    catch (Exception ex) when (IsStorageFailure(ex))
                    {
                        _log.LogWarning(ex, "Could not save pin {PinId}; skipped this pin.", pin.Id);
                    }
                }
                List<PinSessionPin> orderedPins = saved.OrderBy(item => item.Index).Select(item => item.Pin).ToList();
                // Commit the references only after their images exist; clean orphans only
                // after the atomic replacement succeeds, keeping the old session recoverable.
                AtomicFile.WriteExportBytes(FilePath(ManifestName, ".json"),
                    JsonSerializer.SerializeToUtf8Bytes(new SessionManifest(SchemaVersion, orderedPins), JsonOptions));
                LogBudgetSkipped(budgetSkipped);
                CleanupFiles(retainedImages, clear: false);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning(ex, "Could not save the open-pin session.");
            }
        }
    }

    public bool HasImage(Guid id)
    {
        lock (_gate)
        {
            try { return id != Guid.Empty && TryGetExistingImageLength(id, out _); }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning(ex, "Could not reuse saved pin image {PinId}.", id);
                return false;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                CleanupFiles([], clear: true);
                _knownImages.Clear();
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning(ex, "Could not clear the saved pin session.");
            }
        }
    }

    private bool TryGetExistingImageLength(Guid id, out long length)
    {
        string path = ImagePath(id);
        var info = new FileInfo(path);
        length = info.Exists ? info.Length : 0;
        if (length <= 0 || length > _maxPngBytes) return false;
        var stamp = new ImageStamp(length, info.LastWriteTimeUtc);
        if (_knownImages.TryGetValue(id, out ImageStamp known) && known == stamp) return true;
        if (!IsPng(File.ReadAllBytes(path))) return false;
        _knownImages[id] = stamp;
        return true;
    }

    private void RememberImage(Guid id, string path)
    {
        var info = new FileInfo(path);
        _knownImages[id] = new ImageStamp(info.Length, info.LastWriteTimeUtc);
    }

    private void CleanupFiles(HashSet<Guid> retainedImages, bool clear)
    {
        string root = ValidatedFilePaths.DirectoryRoot(_root);
        if (!Directory.Exists(root)) return;
        foreach (string candidate in Directory.EnumerateFiles(root))
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
                File.Delete(path);
                if (image) _knownImages.Remove(Guid.ParseExact(Path.GetFileNameWithoutExtension(name), "N"));
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log.LogWarning(ex, "Could not remove an obsolete saved pin file.");
            }
        }
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
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

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
        && (pin.Placement is not { } placement || (IsFinite(placement.Bounds) && IsFinite(placement.DisplayBounds)));

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

    private sealed record SessionManifest(int SchemaVersion, List<PinSessionPin> Pins);
    private readonly record struct ImageStamp(long Length, DateTime LastWriteTimeUtc);
}
