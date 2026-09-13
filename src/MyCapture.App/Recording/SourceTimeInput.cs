using System.Globalization;

namespace MyCapture.App.Recording;

internal static class SourceTimeInput
{
    internal static string Format(double milliseconds) =>
        milliseconds >= TimeSpan.FromDays(1).TotalMilliseconds
            ? (milliseconds / 1000).ToString("R", CultureInfo.InvariantCulture)
            : TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    internal static bool TryParse(string value, out double milliseconds)
    {
        milliseconds = 0;
        if (value.Contains(':'))
        {
            if (!TimeSpan.TryParseExact(value.Trim(), [@"hh\:mm\:ss\.fff", @"hh\:mm\:ss", @"mm\:ss\.fff", @"mm\:ss"],
                    CultureInfo.InvariantCulture, out TimeSpan time) || time < TimeSpan.Zero) return false;
            milliseconds = time.TotalMilliseconds;
            return true;
        }
        if (!(double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds)
              || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            || !double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(seconds * 1000)) return false;
        milliseconds = seconds * 1000;
        return true;
    }
}
