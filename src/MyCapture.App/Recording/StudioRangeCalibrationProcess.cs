using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Storage;

namespace MyCapture.App.Recording;

internal sealed record VideoRangeMeasurement(int Version, int ProcessId,
    VideoRangeCalibrationResult Result, string? Failure);

/// <summary>Owns one short-lived presenter process; no MediaElement or encoder runs in the caller.</summary>
internal static class StudioRangeCalibrationProcess
{
    internal const string CommandLineSwitch = "--measure-video-range";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    internal static string ResolveExecutable(string baseDirectory, string? processPath,
        Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string executable = Path.Combine(baseDirectory, "MyCapture.exe");
        if (Path.IsPathFullyQualified(executable) && exists(executable)) return executable;
        if (processPath is not null && Path.IsPathFullyQualified(processPath)
            && string.Equals(Path.GetFileName(processPath), "MyCapture.exe", StringComparison.OrdinalIgnoreCase)
            && exists(processPath)) return processPath;
        throw new FileNotFoundException("Cannot locate MyCapture.exe for video range calibration.");
    }

    internal static string ValidateOutputPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Video range measurement requires an absolute JSON output path.");
        string directory = VideoRangeDiagnosticPlan.ValidateOutputDirectory(Path.GetDirectoryName(path)!);
        return DiagnosticOutputPaths.Child(directory, Path.GetFileName(path));
    }

    internal static string ParseArguments(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected --measure-video-range <absolute-output-json>.");
        return ValidateOutputPath(args[1]);
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string outputPath, string dataRoot)
    {
        if (!Path.IsPathFullyQualified(executable)
            || !string.Equals(Path.GetFileName(executable), "MyCapture.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Calibration must launch the absolute MyCapture.exe path.");
        string output = ValidateOutputPath(outputPath);
        string data = VideoRangeDiagnosticPlan.ValidateOutputDirectory(dataRoot);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(output)!,
        };
        start.ArgumentList.Add(CommandLineSwitch);
        start.ArgumentList.Add(output);
        start.Environment["MYCAPTURE_DATA_ROOT"] = data;
        start.Environment["TEMP"] = data;
        start.Environment["TMP"] = data;
        return start;
    }

    internal static Task<VideoRangeCalibrationResult> MeasureAsync(CancellationToken token) =>
        MeasureAsync(ResolveExecutable(AppContext.BaseDirectory, Environment.ProcessPath),
            ValidatedFilePaths.CreateTemporaryDirectory(), token,
            start => new CalibrationChild(Process.Start(start)
                ?? throw new IOException("Video range measurement child could not start.")));

    // The transport is injectable; lifecycle/error tests never launch a media player or kill another process.
    internal static async Task<VideoRangeCalibrationResult> MeasureAsync(string executable, string directory,
        CancellationToken token, Func<ProcessStartInfo, ICalibrationChild> startChild)
    {
        ICalibrationChild? child = null;
        bool stopped = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        try
        {
            token.ThrowIfCancellationRequested();
            string output = DiagnosticOutputPaths.Child(directory, "measurement.json");
            string data = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(directory, "data"));
            child = startChild(BuildStartInfo(executable, output, data));
            stopped = false;
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            stopped = true;
            output = ValidateOutputPath(output);
            if (!File.Exists(output) || new FileInfo(output).Length > 16 * 1024)
                throw new IOException($"Calibration child exit {child.ExitCode}; missing or oversized JSON.");
            string json = await File.ReadAllTextAsync(output, deadline.Token).ConfigureAwait(false);
            return ReadResult(json, child.Id, child.ExitCode);
        }
        finally
        {
            try
            {
                if (child is not null && !stopped)
                {
                    // Only the process returned by our Start; never enumerate by name or PID.
                    try { if (!child.HasExited) child.Kill(); }
                    catch (InvalidOperationException) when (child.HasExited) { } // Exit raced the timeout.
                    await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    stopped = true;
                }
            }
            finally
            {
                child?.Dispose();
                // The child's MF teardown may still hold clips. Reuse the existing retry cleanup,
                // including when a driver timed out before the child could run its own finally.
                if (stopped) _ = Task.Run(() => StudioRangeCalibrationProbe.DeleteClipWithRetriesAsync(directory));
            }
        }
    }

    internal static VideoRangeCalibrationResult ReadResult(string json, int processId, int exitCode)
    {
        VideoRangeMeasurement report = JsonSerializer.Deserialize<VideoRangeMeasurement>(json, JsonOptions)
            ?? throw new IOException("Empty calibration result.");
        if (report.Version != 1 || report.ProcessId != processId || exitCode != 0 || report.Failure is not null
            || report.Result.Studio is not { } studio || !Enum.IsDefined(studio)
            || report.Result.Full is not { } full || !Enum.IsDefined(full))
            throw new IOException($"Invalid calibration result (exit={exitCode}, pid={report.ProcessId}, "
                + $"studio={report.Result.Studio}, full={report.Result.Full}): {report.Failure}");
        return report.Result;
    }

    // Early dispatch runs before ownership/services. The probe creates its own Style=null STA window,
    // so the startup dispatcher can wait without showing a shell, taskbar entry, or activated window.
    internal static int RunChild(string outputPath)
    {
        string output = ValidateOutputPath(outputPath);
        DiagnosticOutputPaths.Create(Path.GetDirectoryName(output)!);
        string data = DiagnosticOutputPaths.Create(VideoRangeDiagnosticPlan.ValidateOutputDirectory(
            Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")
                ?? throw new IOException("Calibration child data root is missing.")));
        Environment.SetEnvironmentVariable("TEMP", data);
        Environment.SetEnvironmentVariable("TMP", data);
        VideoRangeCalibrationResult result = default;
        string? failure = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            result = Task.Run(() => StudioRangeCalibrationProbe.MeasureAsync(cancellation.Token, data))
                .WaitAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(ValidateOutputPath(output), JsonSerializer.Serialize(
            new VideoRangeMeasurement(1, Environment.ProcessId, result, failure), JsonOptions));
        return failure is null ? 0 : 1;
    }

    internal interface ICalibrationChild : IDisposable
    {
        int Id { get; }
        int ExitCode { get; }
        bool HasExited { get; }
        Task WaitForExitAsync(CancellationToken token);
        void Kill();
    }

    private sealed class CalibrationChild(Process process) : ICalibrationChild
    {
        public int Id => process.Id;
        public int ExitCode => process.ExitCode;
        public bool HasExited => process.HasExited;
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void Kill() => process.Kill();
        public void Dispose() => process.Dispose();
    }
}
