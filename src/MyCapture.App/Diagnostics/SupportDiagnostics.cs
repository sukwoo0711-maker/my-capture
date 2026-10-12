using System.Globalization;
using System.Text;
using MyCapture.Core.Platform;

namespace MyCapture.App.Diagnostics;

internal static class SupportDiagnostics
{
    internal const string IssueUri = "https://github.com/sukwoo0711-maker/mycapture-support/issues/new";

    internal static string Format(string version, string windowsVersion, bool packaged, double dpiX, double dpiY,
        IReadOnlyList<SafeErrorRecord> errors)
    {
        var text = new StringBuilder();
        text.AppendLine($"{AppIdentity.WindowLabel}: {version}");
        text.AppendLine($"Windows: {windowsVersion}");
        text.AppendLine($"Store: {packaged}");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"DPI: {dpiX:0.##} × {dpiY:0.##}"));
        text.AppendLine($"Errors: {errors.Count}");
        foreach (SafeErrorRecord error in errors)
            text.AppendLine($"{error.Utc:O} Event={error.EventId} HRESULT={(error.HResult is { } code ? $"0x{code:X8}" : "—")}");
        return text.ToString().TrimEnd();
    }
}
