using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using Xunit;
using static MyCapture.App.Tests.RollingFileLogTests;

namespace MyCapture.App.Tests;

public sealed class DiagnosticBundleTests
{
    private const string Summary = "MyCapture: 4.6.0.0\r\nWindows: 10.0.26200.0\r\nErrors: 1";

    [Fact]
    public void BundleHoldsTheSummaryAndOnlyWellFormedLogLinesWithNoPersonalText()
    {
        string root = OwnedTestDirectory.Create("mc-diagnostic-bundle-");
        try
        {
            string logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            // An older file with lines that did not come from the safe formatter: an edited file or another build.
            string planted = SafeLogLine.Format(DateTimeOffset.UnixEpoch, LogLevel.Warning, "MyCapture.App.App", 3, "Older", null);
            File.WriteAllLines(Path.Combine(logs, "mycapture.1.log"),
            [
                $"2026-10-23T04:00:00.000Z ERR {CapturePath}",
                $"Exception: could not open {CapturePath} for {WindowTitle}",
                $"   at MyCapture.App.Clipboard.Read() {ClipboardText}",
                planted,
            ]);

            using (var log = new RollingFileLog(logs))
            {
                using var provider = new RollingFileLogProvider(log);
                ILogger logger = provider.CreateLogger("MyCapture.App.Gallery.GalleryController");
                logger.LogError(new IOException("Access to " + CapturePath + " is denied."), "Could not export {Path} titled {Title} from {Url}",
                    CapturePath, WindowTitle, PrivateUrl);
                logger.LogWarning("Clipboard text {Text}", ClipboardText);

                string output = Path.Combine(root, "out");
                Directory.CreateDirectory(output);
                string bundle = Path.Combine(output, "MyCapture-diagnostics.zip");
                // The log is still open for appending, as it is in the running app.
                DiagnosticBundle.Write(bundle, Summary, log);
                DiagnosticBundle.Write(bundle, Summary, log); // The save dialog already confirmed replacing it.
                Assert.Equal(["MyCapture-diagnostics.zip"], Directory.GetFiles(output).Select(file => Path.GetFileName(file)));

                using ZipArchive zip = ZipFile.OpenRead(bundle);
                Assert.Equal(["support.txt", "logs/mycapture.1.log", "logs/mycapture.log"], zip.Entries.Select(entry => entry.FullName));
                Assert.Equal(Summary, Read(zip, "support.txt"));

                Assert.Equal(planted, Read(zip, "logs/mycapture.1.log"));
                string[] current = Read(zip, "logs/mycapture.log").Split("\r\n");
                Assert.Equal(2, current.Length);
                Assert.Contains(" ERR MyCapture.App.Gallery.GalleryController event=0 msg="
                    + SafeLogLine.TemplateId("Could not export {Path} titled {Title} from {Url}") + " ex=System.IO.IOException hresult=0x80131620",
                    current[0], StringComparison.Ordinal);
                Assert.Contains(" WRN MyCapture.App.Gallery.GalleryController event=0 msg="
                    + SafeLogLine.TemplateId("Clipboard text {Text}"), current[1], StringComparison.Ordinal);

                string everything = string.Join("\n", zip.Entries.Select(entry => Read(zip, entry.FullName)));
                foreach (string fragment in PersonalFragments) Assert.DoesNotContain(fragment, everything, StringComparison.Ordinal);
                Assert.DoesNotContain("Exception:", everything, StringComparison.Ordinal);
                Assert.DoesNotContain("   at ", everything, StringComparison.Ordinal);
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void WithoutAFileLogTheBundleHoldsTheSummaryAlone()
    {
        string root = OwnedTestDirectory.Create("mc-diagnostic-bundle-summary-");
        try
        {
            string bundle = Path.Combine(root, "bundle.zip");
            DiagnosticBundle.Write(bundle, Summary, log: null);
            using ZipArchive zip = ZipFile.OpenRead(bundle);
            Assert.Equal(["support.txt"], zip.Entries.Select(entry => entry.FullName));
            Assert.Equal(Summary, Read(zip, "support.txt"));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
