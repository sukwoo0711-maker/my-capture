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
}