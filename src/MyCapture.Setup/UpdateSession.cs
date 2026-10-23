using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MyCapture.Setup;

internal static class UpdateParent
{
    internal static void Wait(UpdaterSession session, Action<string> info, int timeoutMilliseconds = 60000)
    {
        if (session.ParentId <= 0) throw new SetupException(14, "Invalid updater parent ID.");
        if (session.WaitForParent && session.ParentStartedUtc <= 0)
            throw new SetupException(14, "WaitForParent requires a positive ParentStartedUtc.");
        if (timeoutMilliseconds < 0 || timeoutMilliseconds > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        Process parent;
        try { parent = Process.GetProcessById(session.ParentId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (!session.WaitForParent) throw new SetupException(15, "Updater parent is still running.");
            try
            {
                if (parent.HasExited) return;
                if (parent.StartTime.ToUniversalTime().Ticks != session.ParentStartedUtc)
                { info("Updater parent PID has been reused; the original parent has exited."); return; }
                info("Waiting for updater parent to exit. PID=" + session.ParentId + "; timeout=" + timeoutMilliseconds + " ms.");
                if (!parent.WaitForExit(timeoutMilliseconds))
                    throw new SetupException(15, "Updater parent did not exit within " + timeoutMilliseconds + " ms.");
                info("Updater parent has exited.");
            }
            catch (InvalidOperationException) { /* The verified process exited while querying it. */ }
        }
    }
}

internal sealed class UpdateRestart
{
    internal const string TestArgumentsVariable = "MYCAPTURE_SETUP_TEST_RESTART_ARGUMENTS";
    private readonly Action<string> info, warning;
    private readonly Func<ProcessStartInfo, Process?> starter;
    internal UpdateRestart(Action<string> info, Action<string> warning, Func<ProcessStartInfo, Process?>? starter = null)
    { this.info = info; this.warning = warning; this.starter = starter ?? Process.Start; }

    internal static void ValidateTestArguments(string? value)
    {
        if (value != null && value != "--selftest-ux-review")
            throw new SetupException(21, "The restart test argument must be exactly --selftest-ux-review.");
    }

    internal static ProcessStartInfo CreateStartInfo(string installRoot, string sessionDirectory, string? testArguments)
    {
        ValidateTestArguments(testArguments);
        var start = new ProcessStartInfo(Path.Combine(installRoot, "MyCapture.exe"))
        {
            WorkingDirectory = installRoot, UseShellExecute = false, Arguments = testArguments ?? "",
            RedirectStandardOutput = testArguments != null, RedirectStandardError = testArguments != null,
            CreateNoWindow = testArguments != null
        };
        start.EnvironmentVariables.Remove("MYCAPTURE_UPDATE_SESSION");
        start.EnvironmentVariables.Remove(TestArgumentsVariable);
        return start;
    }

    internal bool Start(string installRoot, string sessionDirectory, string? testArguments)
    {
        FileStream? stdout = null, stderr = null;
        Process? process = null;
        try
        {
            var start = CreateStartInfo(installRoot, sessionDirectory, testArguments);
            PathSafety.NoLinks(start.FileName);
            if (!File.Exists(start.FileName)) { warning("MyCapture restart skipped because the installed executable is missing."); return false; }
            if (testArguments != null)
            {
                string output = Path.Combine(sessionDirectory, "restart.stdout.txt"), error = Path.Combine(sessionDirectory, "restart.stderr.txt");
                PathSafety.NoLinks(output); PathSafety.NoLinks(error);
                stdout = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
                stderr = new FileStream(error, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            process = starter(start);
            if (process == null) { warning("MyCapture restart did not return a process."); return false; }
            info("Restarted MyCapture. PID=" + process.Id + "; executable=" + start.FileName + "; working directory=" + start.WorkingDirectory + ".");
            var observed = process; var outputStream = stdout; var errorStream = stderr;
            // The window can close immediately. Keep this process alive long enough to record
            // an early exit and, for the allowlisted diagnostic, drain both redirected streams.
            var observer = new Thread(() => Observe(observed, outputStream, errorStream));
            observer.Start(); process = null; stdout = null; stderr = null;
            return true;
        }
        catch (Exception error) { warning("MyCapture could not restart: " + error.Message); return false; }
        finally { process?.Dispose(); stdout?.Dispose(); stderr?.Dispose(); }
    }

    private void Observe(Process process, FileStream? stdout, FileStream? stderr)
    {
        using (process)
        using (stdout)
        using (stderr)
        {
            try
            {
                Task? output = stdout == null ? null : process.StandardOutput.BaseStream.CopyToAsync(stdout);
                Task? error = stderr == null ? null : process.StandardError.BaseStream.CopyToAsync(stderr);
                if (process.WaitForExit(3000)) warning("Restarted MyCapture exited within 3 seconds. Exit code=" + process.ExitCode + ".");
                if (output != null && error != null) Task.WaitAll(output, error);
            }
            catch (Exception error) { warning("Could not observe restarted MyCapture: " + error.Message); }
        }
    }
}

internal static class UpdateCompletion
{
    internal static bool ShouldRestart(UpdaterSession session, int exitCode, bool receiptWritten, bool verifyOnly) =>
        session.RestartAfterInstall && !verifyOnly && (exitCode == 0 ? receiptWritten : !session.ShowProgress);

    internal static void Finish(UpdaterSession session, string receiptPath, string? installRoot, SetupLog log,
        int exitCode, bool verifyOnly, bool validated, string? testArguments, UpdateRestart restart)
    {
        bool receiptWritten = false;
        try
        {
            new InstallReceipt { Token = session.Token, Version = session.Version, ExitCode = exitCode, InstallRoot = installRoot, LogPath = log.Path }.Write(receiptPath);
            receiptWritten = true;
            log.Write("INFO", "Updater receipt written: " + receiptPath + ".");
        }
        catch (Exception error) { log.Write("ERROR", "Could not write updater receipt: " + error.Message); }
        if (validated && ShouldRestart(session, exitCode, receiptWritten, verifyOnly))
            restart.Start(installRoot ?? session.InstallRoot, Path.GetDirectoryName(receiptPath)!, testArguments);
    }
}
