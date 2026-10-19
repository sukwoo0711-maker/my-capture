using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MyCapture.Core.Platform;

namespace MyCapture.App.Diagnostics;

internal static class SupportDiagnostics
{
    internal const string IssueUri = "https://github.com/sukwoo0711-maker/mycapture-support/issues/new";

    /// <summary>Opens a URI in the default browser. Tests replace it so nothing is launched.</summary>
    internal static Action<string> OpenUri { get; set; } =
        uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// The "Report a problem" command outside Settings (error dialogs): opens the same public
    /// issue page as Settings → About → Report a problem.
    /// </summary>
    /// <returns><see langword="false"/> when Windows could not open the page.</returns>
    internal static bool TryOpenIssuePage() => TryOpenUri(IssueUri);

    /// <summary>Support destinations only; shared by error dialogs and the contact window.</summary>
    internal static bool TryOpenUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? destination)
            || !(destination.Scheme == Uri.UriSchemeHttps && destination.Host == "github.com"
                && destination.AbsolutePath == "/sukwoo0711-maker/mycapture-support/issues/new")
            && uri != MyCapture.Core.Settings.StoreRatingPolicy.ReviewUri) return false;
        try
        {
            OpenUri(uri);
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

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
