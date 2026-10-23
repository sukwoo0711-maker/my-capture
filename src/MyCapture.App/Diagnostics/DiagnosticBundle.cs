using System.IO;
using System.IO.Compression;
using System.Text;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// One ZIP for a support request: the support summary and the rotating log files. Saved only where
/// the user chooses and never sent anywhere by the app.
/// </summary>
/// <remarks>
/// Log lines are copied only when they still match <see cref="SafeLogLine.IsWellFormed"/>, so a line
/// that reached the files any other way (an older build, an edited file) cannot carry text out.
/// </remarks>
internal static class DiagnosticBundle
{
    internal const string SummaryEntry = "support.txt";
    internal const string LogsFolder = "logs/";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    internal static void Write(string destination, string summary, RollingFileLog? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(summary);
        string target = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("A destination directory is required.", nameof(destination));
        log?.Flush(FlushTimeout);

        string stage = Path.Combine(parent, $".mycapture-{Guid.NewGuid():N}.zip.partial");
        bool ownsStage = false;
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsStage = true;
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                WriteText(zip, SummaryEntry, summary);
                if (log is not null)
                {
                    foreach (string file in log.ExistingFiles())
                        WriteText(zip, LogsFolder + Path.GetFileName(file), string.Join("\r\n", SafeLines(file, log.MaximumFileBytes)));
                }
            }
            // The save dialog already asked before replacing an existing file.
            File.Move(stage, target, overwrite: true);
            ownsStage = false;
        }
        finally
        {
            if (ownsStage)
            {
                try { File.Delete(stage); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Well-formed lines of one log file, reading at most about twice its size cap.</summary>
    internal static IEnumerable<string> SafeLines(string path, long maximumFileBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
        long budget = 2 * maximumFileBytes;
        while (budget > 0 && reader.ReadLine() is { } line)
        {
            budget -= line.Length + 2;
            if (SafeLogLine.IsWellFormed(line)) yield return line;
        }
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        output.Write(Utf8.GetBytes(text));
    }
}
