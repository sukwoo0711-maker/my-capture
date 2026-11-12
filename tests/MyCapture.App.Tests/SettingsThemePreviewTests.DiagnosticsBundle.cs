using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task DiagnosticsBundle_SavesTheSummaryAndLogsOnlyWhereTheUserChooses() =>
        RunIsolated(nameof(DiagnosticsBundle_SavesTheSummaryAndLogsOnlyWhereTheUserChooses), VerifyDiagnosticsBundle);

    private static void VerifyDiagnosticsBundle()
    {
        Application application = CreateThemedApplication();
        string root = OwnedTestDirectory.Create("mc-settings-diagnostics-bundle-");
        UnexpectedExitLog? previousExits = ErrorReporting.Exits;
        try
        {
            // A support request from Settings carries the unexpected-exit records too.
            var exits = new UnexpectedExitLog(Path.Combine(root, "unexpected-exits"), path =>
            {
                Directory.CreateDirectory(Path.Combine(root, "trash"));
                File.Move(path, Path.Combine(root, "trash", Path.GetFileName(path)));
            });
            Assert.True(exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure())));
            ErrorReporting.Exits = exits;
            string exitEntry = "unexpected-exits/" + Assert.Single(exits.SafeEntries()).Name;
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                using var log = new RollingFileLog(Path.Combine(root, locale, "logs"));
                using (var provider = new RollingFileLogProvider(log))
                    provider.CreateLogger("MyCapture.App.Settings.SettingsWindow").LogError(
                        new IOException(RollingFileLogTests.CapturePath), "Could not save {Path}", RollingFileLogTests.CapturePath);
                SettingsWindow window = CreateOffscreenSettingsWindow(new AppSettings());
                window.FileLog = log;
                try
                {
                    window.Show();
                    var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                    tabs.SelectedItem = window.FindName("UpdateTab");
                    Drain(window);

                    // Beside Contact support, labelled and described in both languages.
                    var button = Assert.IsType<Button>(window.FindName("DiagnosticsBundleButton"));
                    Assert.Same(VisualTreeHelper.GetParent(button), VisualTreeHelper.GetParent((Button)window.FindName("SupportReport")));
                    Assert.Equal(UiText.Get("About_DiagnosticsBundle"), button.Content);
                    Assert.Equal(UiText.Get("About_DiagnosticsBundle"), AutomationProperties.GetName(button));
                    Assert.Equal(UiText.Get("About_DiagnosticsBundleHelp"), AutomationProperties.GetHelpText(button));
                    Assert.True(button.IsVisible && button.IsEnabled && button.Focusable);

                    // Cancelling the save dialog writes nothing.
                    string output = Path.Combine(root, locale, "out");
                    Directory.CreateDirectory(output);
                    window.ChooseDiagnosticsBundlePath = () => null;
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Empty(Directory.GetFiles(output));

                    string bundle = Path.Combine(output, "MyCapture-diagnostics.zip");
                    window.ChooseDiagnosticsBundlePath = () => bundle;
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Drain(window);
                    Assert.Equal(UiText.Get("About_DiagnosticsBundleSaved"), KoreanWordWrap.Strip(((TextBlock)window.FindName("SettingsStatus")).Text));

                    using ZipArchive zip = ZipFile.OpenRead(bundle);
                    Assert.Equal(["support.txt", "logs/mycapture.log", exitEntry], zip.Entries.Select(entry => entry.FullName));
                    Assert.StartsWith(UnexpectedExitLog.Header, ReadEntry(zip, exitEntry), StringComparison.Ordinal);
                    string summary = ReadEntry(zip, "support.txt");
                    Assert.Contains("Windows:", summary, StringComparison.Ordinal);
                    Assert.Contains("Errors:", summary, StringComparison.Ordinal);
                    Assert.Contains(" ERR MyCapture.App.Settings.SettingsWindow ", ReadEntry(zip, "logs/mycapture.log"), StringComparison.Ordinal);
                    string everything = string.Join("\n", zip.Entries.Select(entry => ReadEntry(zip, entry.FullName)));
                    foreach (string fragment in RollingFileLogTests.PersonalFragments)
                        Assert.DoesNotContain(fragment, everything, StringComparison.Ordinal);
                }
                finally
                {
                    window.CloseForExit();
                }
            }
        }
        finally
        {
            ErrorReporting.Exits = previousExits;
            application.Shutdown();
            OwnedTestDirectory.Delete(root);
        }
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
