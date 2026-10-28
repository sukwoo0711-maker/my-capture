using System.Globalization;

namespace MyCapture.Core.Capture;

/// <summary>Text-only media facts; reading a file is the caller's explicit action.</summary>
public static class MediaDetailsFormatter
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string FileSize(decimal bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        decimal value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + SizeUnits[unit];
    }

    public static string ExactBytes(decimal bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return bytes.ToString("N0", CultureInfo.CurrentCulture);
    }

    public static string Resolution(int width, int height) => width > 0 && height > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{width} × {height} px") : string.Empty;

    public static string Duration(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return string.Empty;
        double tenths = Math.Floor(milliseconds / 100);
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(tenths / 600):0}:{Math.Floor(tenths / 10) % 60:00}.{tenths % 10:0}");
    }

    public static MediaSizeSummary Summarize(IEnumerable<long?> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        int count = 0, unavailable = 0;
        decimal total = 0;
        foreach (long? size in sizes)
        {
            count++;
            if (size is >= 0) total += size.Value;
            else unavailable++;
        }
        return new(count, total, unavailable);
    }
}

/// <summary>A missing file is not a zero-byte file and cannot silently reduce a selection's total.</summary>
public readonly record struct MediaSizeSummary(int Count, decimal TotalBytes, int UnavailableCount);
