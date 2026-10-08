using System.Globalization;
using System.Text.Json.Serialization;

namespace MyCapture.Core.Queue;

/// <summary>A virtual folder. Capture payloads never move when this metadata changes.</summary>
public sealed class CaptureFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public long NextSequence { get; set; } = 1;

    [JsonIgnore]
    public DateOnly CreatedDate => DateOnly.FromDateTime(CreatedAt.DateTime);

    internal CaptureFolder Copy() => new()
    {
        Id = Id, ParentId = ParentId, Name = Name, CreatedAt = CreatedAt, NextSequence = NextSequence,
    };
}

/// <summary>All captures, a folder subtree, or a date's unassigned captures and folder trees.</summary>
public readonly record struct CaptureFolderScope(Guid? FolderId = null, DateOnly? Date = null)
{
    public static CaptureFolderScope All => default;
    public static CaptureFolderScope ForFolder(Guid id) => new(id);
    public static CaptureFolderScope ForDate(DateOnly date) => new(null, date);
    public bool IsAll => FolderId is null && Date is null;
}

public sealed record CaptureFolderDeletionPreview(
    int FolderCount, int SubfolderCount, int ImageCount, int VideoCount)
{
    public int CaptureCount => ImageCount + VideoCount;
}

public sealed record CaptureFolderDeletionResult(
    int RemovedFolders, int RemovedCaptures, int RemainingCaptures, int CleanupPendingCount);

internal sealed class CaptureFolderState
{
    public int Version { get; set; } = 1;
    public List<CaptureFolder> Folders { get; set; } = [];
    public List<CaptureFolderAssignment> Assignments { get; set; } = [];
    public bool UseCurrentFolderForNewCaptures { get; set; }
    public Guid? DefaultCaptureFolderId { get; set; }
}

internal sealed class CaptureFolderAssignment
{
    public Guid CaptureId { get; set; }
    public Guid FolderId { get; set; }
    public long Sequence { get; set; }
    public bool WasPinned { get; set; }
}

/// <summary>Portable Windows names, with the same rules on every runtime and during import.</summary>
public static class CaptureFolderNaming
{
    public const int MaximumNameLength = 80;
    public const int MaximumPrefixLength = 40;
    public const int MaximumDepth = 16;
    public const int MaximumFolders = 10_000;
    public const string DefaultPrefix = "myCapture";
    private const string InvalidCharacters = "<>:\"/\\|?*";

    public static string NormalizeFolderName(string? value)
    {
        string name = (value ?? string.Empty).Trim();
        if (!IsValidComponent(name, MaximumNameLength))
            throw new ArgumentException("Use a folder name of 1 to 80 characters without Windows path characters.");
        return name;
    }

    public static string NormalizePrefix(string? value)
    {
        string prefix = (value ?? string.Empty).Trim();
        if (!IsValidComponent(prefix, MaximumPrefixLength))
            throw new ArgumentException("Use a prefix of 1 to 40 characters without Windows path characters.");
        return prefix;
    }

    internal static bool IsValidComponent(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
        && value is not "." and not ".." && !value.EndsWith('.') && !value.EndsWith(' ')
        && !value.Any(c => char.IsControl(c) || InvalidCharacters.Contains(c))
        && !IsDeviceName(value);

    private static bool IsDeviceName(string value)
    {
        string stem = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'));
    }

    public static string SafeStem(string? value, string fallback = DefaultPrefix)
    {
        string stem = new((value ?? string.Empty).Select(c =>
            char.IsControl(c) || InvalidCharacters.Contains(c) ? '_' : c).ToArray());
        stem = stem.Trim().TrimEnd('.', ' ');
        if (stem.Length > MaximumNameLength) stem = stem[..MaximumNameLength].TrimEnd('.', ' ');
        if (stem.Length == 0 || stem is "." or "..") stem = fallback;
        if (IsDeviceName(stem)) stem = "_" + stem;
        return stem;
    }

    public static string SequenceName(string? prefix, long sequence)
    {
        string safe;
        try { safe = NormalizePrefix(prefix); }
        catch (ArgumentException) { safe = DefaultPrefix; }
        return safe + Math.Max(1, sequence).ToString("00", CultureInfo.InvariantCulture);
    }

    public static string WithCollisionSuffix(string fileName, int sequence)
    {
        if (sequence == 1) return fileName;
        return Path.GetFileNameWithoutExtension(fileName) + "-"
            + sequence.ToString("00", CultureInfo.InvariantCulture) + Path.GetExtension(fileName);
    }
}
