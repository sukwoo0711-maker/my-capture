using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;

namespace MyCapture.App.Diagnostics;

internal sealed record SupportDiagnosticSnapshot(string Version, string Build, bool Packaged,
    string WindowsVersion, double DisplayScale, string Language, string Theme)
{
    internal static SupportDiagnosticSnapshot Capture(Window window) => new(
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty,
        AppPackage.IsPackaged, Environment.OSVersion.Version.ToString(), VisualTreeHelper.GetDpi(window).DpiScaleX,
        UiText.Culture.Name, ThemeService.Current.ToString());
}

internal static class SupportReport
{
    internal const int MaximumIssueUrlLength = 8000;

    // The diagnostic block accepts version/identifier tokens only, never arbitrary system text.
    // It has no access to paths, exception messages, captures, clipboard content or user names.
    private static string Token(string value) => value.Length is > 0 and <= 100
        && Regex.IsMatch(value, @"\A[a-zA-Z0-9.+_-]+\z", RegexOptions.CultureInvariant)
        ? value : UiText.Get("Support_Unknown");

    internal static string Format(SupportDiagnosticSnapshot snapshot)
    {
        var lines = new List<string> { $"MyCapture: {Token(snapshot.Version)}" };
        if (!string.IsNullOrWhiteSpace(snapshot.Build)) lines.Add($"{UiText.Get("Support_Build")}: {Token(snapshot.Build)}");
        lines.Add($"{UiText.Get("Support_Install")}: {UiText.Get(snapshot.Packaged ? "Support_Packaged" : "Support_Unpackaged")}");
        lines.Add($"Windows: {Token(snapshot.WindowsVersion)}");
        double scale = double.IsFinite(snapshot.DisplayScale) && snapshot.DisplayScale > 0 ? snapshot.DisplayScale : 1;
        lines.Add(UiText.Format("Support_DisplayScale", scale * 100));
        lines.Add($"{UiText.Get("Settings.Language")}: {Token(snapshot.Language)}");
        lines.Add($"{UiText.Get("Settings.Theme")}: {Token(snapshot.Theme)}");
        return string.Join(Environment.NewLine, lines);
    }

    internal static string Body(string diagnostics, string message) =>
        $"{diagnostics}{Environment.NewLine}{Environment.NewLine}{message.Trim()}";

    internal static string IssueUrl(string diagnostics, string message)
    {
        string title = UiText.Get("Support_IssueTitle");
        string prefix = SupportDiagnostics.IssueUri + "?title=" + Uri.EscapeDataString(title) + "&body=";
        // Encode one Unicode scalar at a time: never cut UTF-16 pairs or percent escapes.
        var encoded = new StringBuilder();
        foreach (Rune rune in Body(diagnostics, message).EnumerateRunes())
        {
            string piece = Uri.EscapeDataString(rune.ToString());
            if (prefix.Length + encoded.Length + piece.Length > MaximumIssueUrlLength) break;
            encoded.Append(piece);
        }
        return prefix + encoded;
    }
}
