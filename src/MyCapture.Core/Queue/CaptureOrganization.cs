namespace MyCapture.Core.Queue;

/// <summary>Bounded user labels, kept separate from captured pixels and OCR content.</summary>
public static class CaptureOrganization
{
    public const int MaximumTitleLength = 140;
    public const int MaximumTags = 12;
    public const int MaximumTagLength = 32;

    public static string NormalizeTitle(string? title)
    {
        string value = (title ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length > MaximumTitleLength || value.Any(char.IsControl))
            throw new ArgumentException("The title must be at most 140 characters without control characters.", nameof(title));
        return value;
    }

    public static string NormalizeTags(string? tags)
    {
        if (tags is { Length: > 1024 })
            throw new ArgumentException("Tag input is too long.", nameof(tags));
        string[] values = (tags ?? string.Empty)
            .Split([',', ';', '\uFF0C', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.TrimStart('#').Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length > MaximumTags || values.Any(tag => tag.Length > MaximumTagLength || tag.Any(char.IsControl)))
            throw new ArgumentException("Use at most 12 tags, each at most 32 characters without control characters.", nameof(tags));
        return string.Join(", ", values);
    }

    public static IReadOnlyList<string> SplitTags(string? tags) => (tags ?? string.Empty)
        .Split([',', ';', '\uFF0C', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(tag => tag.TrimStart('#').Trim()).Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Counts captures, not occurrences; case variants name the same tag.</summary>
    public static IReadOnlyList<CaptureTagCount> CountTags(IEnumerable<string?> captureTags) => captureTags
        .SelectMany(SplitTags).GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
        .Select(group => new CaptureTagCount(group.Order(StringComparer.Ordinal).First(), group.Count()))
        .OrderByDescending(tag => tag.Count).ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<CaptureTagSelection> SelectionTags(IEnumerable<string?> captureTags)
    {
        string?[] captures = captureTags.ToArray();
        return CountTags(captures).Select(tag => new CaptureTagSelection(tag.Name,
            tag.Count == captures.Length ? true : null)).ToArray();
    }

    public static bool? TagState(IEnumerable<string?> captureTags, string tag)
    {
        string?[] captures = captureTags.ToArray();
        int count = captures.Count(tags => SplitTags(tags).Contains(tag, StringComparer.OrdinalIgnoreCase));
        return count == 0 ? false : count == captures.Length ? true : null;
    }

    /// <summary>Validates each intent independently; a selection may contain more than twelve tags.</summary>
    public static IReadOnlyList<CaptureTagChange> NormalizeTagChanges(IEnumerable<CaptureTagChange> changes)
    {
        var normalized = new Dictionary<string, CaptureTagChange>(StringComparer.OrdinalIgnoreCase);
        foreach (CaptureTagChange change in changes)
        {
            string tag = NormalizeTags(change.Name);
            if (SplitTags(tag).Count != 1)
                throw new ArgumentException("A tag change must name exactly one tag.", nameof(changes));
            normalized[tag] = new CaptureTagChange(tag, change.Add);
        }
        return normalized.Values.ToArray();
    }

    /// <summary>Removals free space first; additions that do not fit are skipped without losing existing tags.</summary>
    public static CaptureTagApplication ApplyTagChanges(string? tags, IEnumerable<CaptureTagChange> changes)
    {
        IReadOnlyList<CaptureTagChange> intents = NormalizeTagChanges(changes);
        var removed = intents.Where(change => !change.Add).Select(change => change.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = SplitTags(tags).Where(tag => !removed.Contains(tag)).ToList();
        bool limitReached = false;
        foreach (CaptureTagChange change in intents.Where(change => change.Add))
        {
            if (result.Contains(change.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (result.Count >= MaximumTags) { limitReached = true; continue; }
            result.Add(change.Name);
        }
        return new CaptureTagApplication(NormalizeTags(string.Join(", ", result)), limitReached);
    }
}

public sealed record CaptureTagCount(string Name, int Count);
public sealed record CaptureTagSelection(string Name, bool? IsChecked);
public sealed record CaptureTagChange(string Name, bool Add);
public sealed record CaptureTagApplication(string Tags, bool LimitReached);
public sealed record CaptureBatchTagResult(int UpdatedCount, int LimitReachedCount, int MissingCount);
