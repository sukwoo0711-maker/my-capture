using System.Globalization;

namespace MyCapture.Core.Recording;

/// <summary>
/// The one way the recording editor and players write a time: <c>m:ss.fff</c>, with hours
/// (<c>h:mm:ss.fff</c>) only from one hour on. Input stays lenient: it also reads the formats the
/// editor used before (<c>00:00:00.000</c>, <c>00:00.000</c>, <c>00:00.0</c>), any number of
/// fraction digits, and a plain number of seconds.
/// </summary>
public static class VideoTimecode
{
    private const long MillisecondsPerSecond = 1000;
    private const long MillisecondsPerMinute = 60 * MillisecondsPerSecond;
    private const long MillisecondsPerHour = 60 * MillisecondsPerMinute;

    /// <summary>Formats <paramref name="milliseconds"/>, rounded to the nearest millisecond.</summary>
    public static string Format(double milliseconds)
    {
        long total = WholeMilliseconds(milliseconds);
        long hours = total / MillisecondsPerHour;
        long minutes = total / MillisecondsPerMinute % 60;
        long seconds = total / MillisecondsPerSecond % 60;
        long fraction = total % MillisecondsPerSecond;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}.{fraction:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{total / MillisecondsPerMinute}:{seconds:00}.{fraction:000}");
    }

    /// <summary>
    /// A ruler label in the same form: whole seconds drop the fraction (<c>0:05</c>), others keep
    /// only the digits they need (<c>0:00.5</c>).
    /// </summary>
    public static string FormatTick(double milliseconds)
    {
        string full = Format(milliseconds);
        return full.EndsWith(".000", StringComparison.Ordinal)
            ? full[..^4]
            : full.TrimEnd('0');
    }

    /// <summary>
    /// Reads <c>[h:]m:ss[.fff]</c> or a number of seconds. Seconds must be below 60 after a minute
    /// field and minutes below 60 after an hour field; the leading field is unbounded.
    /// </summary>
    public static bool TryParse(string? text, out double milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (!value.Contains(':', StringComparison.Ordinal))
        {
            if (!(double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double plainSeconds)
                  || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out plainSeconds))
                || !double.IsFinite(plainSeconds) || plainSeconds < 0 || !double.IsFinite(plainSeconds * 1000))
            {
                return false;
            }

            milliseconds = plainSeconds * 1000;
            return true;
        }

        string[] fields = value.Split(':');
        if (fields.Length is not (2 or 3))
        {
            return false;
        }

        if (!TryParseSeconds(fields[^1], out double seconds) || seconds >= 60)
        {
            return false;
        }

        double total = seconds * 1000;
        if (!TryParseWhole(fields[^2], out long minutes))
        {
            return false;
        }

        if (fields.Length == 3)
        {
            if (minutes >= 60 || !TryParseWhole(fields[0], out long hours))
            {
                return false;
            }

            total += hours * (double)MillisecondsPerHour;
        }

        total += minutes * (double)MillisecondsPerMinute;
        if (!double.IsFinite(total))
        {
            return false;
        }

        milliseconds = total;
        return true;
    }

    private static long WholeMilliseconds(double milliseconds) =>
        double.IsFinite(milliseconds) && milliseconds > 0
            ? (long)Math.Min(Math.Round(milliseconds, MidpointRounding.AwayFromZero), long.MaxValue / 2)
            : 0;

    /// <summary>One or more digits only: no sign, space or separator.</summary>
    private static bool TryParseWhole(string field, out long value)
    {
        value = 0;
        if (field.Length is 0 or > 12 || !field.All(char.IsAsciiDigit))
        {
            return false;
        }

        value = long.Parse(field, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Whole seconds with an optional fraction after a dot or comma.</summary>
    private static bool TryParseSeconds(string field, out double seconds)
    {
        seconds = 0;
        int separator = field.IndexOfAny(['.', ',']);
        string whole = separator < 0 ? field : field[..separator];
        string fraction = separator < 0 ? string.Empty : field[(separator + 1)..];
        if (!TryParseWhole(whole, out long wholeSeconds)
            || (separator >= 0 && (fraction.Length is 0 or > 9 || !fraction.All(char.IsAsciiDigit))))
        {
            return false;
        }

        seconds = wholeSeconds + (fraction.Length == 0
            ? 0
            : double.Parse("0." + fraction, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
        return true;
    }
}
