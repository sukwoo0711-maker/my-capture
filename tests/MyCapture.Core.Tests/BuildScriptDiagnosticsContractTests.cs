using System.Diagnostics;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// What build\build.ps1 prints when a step fails. On 2026-11-12 a CI test step ended "FAILED (1)" and printed
/// 240 passing lines whose parameters read "failed: False", but not the failed test: the failure was further
/// down the log than the first 240 matching lines. These tests run the real PowerShell helper.
/// </summary>
public sealed class BuildScriptDiagnosticsContractTests
{
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void ALateFailureIsPrintedWithItsErrorAheadOfPassingLinesThatMentionFailure()
    {
        var log = new List<string>();
        for (int i = 0; i < 600; i++)
            log.Add($"    Passed MyCapture.Core.Tests.StoreRatingPolicyTests.Counting(count: {i}, failed: False, expected: True) [< 1 ms]");
        log.AddRange(
        [
            "  [xUnit.net 00:22:42.07]     MyCapture.App.Tests.LateTests.TheRealFailure [FAIL]",
            "    Failed MyCapture.App.Tests.LateTests.TheRealFailure [332 ms]",
            "    Error Message:",
            "     Assert.Equal() Failure: Values differ",
            "    Stack Trace:",
            "       at MyCapture.App.Tests.LateTests.TheRealFailure() in LateTests.cs:line 12",
            "Test Run Failed.",
        ]);

        string[] printed = Run(log);

        int failures = Array.IndexOf(printed, "Failures (4 line(s)):");
        int context = Array.IndexOf(printed, "Context:");
        Assert.Equal(0, failures);
        Assert.True(context > failures, string.Join("\n", printed));
        // The failure lines themselves come first, before any context.
        Assert.Equal(
            [
                "  [xUnit.net 00:22:42.07]     MyCapture.App.Tests.LateTests.TheRealFailure [FAIL]",
                "    Failed MyCapture.App.Tests.LateTests.TheRealFailure [332 ms]",
                "    Error Message:",
                "Test Run Failed.",
            ],
            printed[1..context]);
        // The context carries the error text, and passing lines that only mention "failed" do not crowd it out.
        Assert.Contains("     Assert.Equal() Failure: Values differ", printed[context..]);
        Assert.Contains("       at MyCapture.App.Tests.LateTests.TheRealFailure() in LateTests.cs:line 12", printed[context..]);
        Assert.True(printed.Length < 40, string.Join("\n", printed));
    }

    [Fact]
    public void ManyFailuresAreAllNamedEvenWhenTheirContextIsCut()
    {
        var log = new List<string>();
        for (int test = 0; test < 30; test++)
        {
            log.Add($"    Failed MyCapture.Core.Tests.Many.Test{test:00} [1 ms]");
            log.Add("    Error Message:");
            for (int line = 0; line < 20; line++) log.Add($"       at Frame{line}()");
        }

        string[] printed = Run(log);

        Assert.Equal("Failures (60 line(s)):", printed[0]);
        for (int test = 0; test < 30; test++)
            Assert.Contains($"    Failed MyCapture.Core.Tests.Many.Test{test:00} [1 ms]", printed[1..61]);
        Assert.Equal("Context:", printed[61]);
        Assert.Equal(240, printed.Length - 62);
    }

    [Fact]
    public void BuildErrorsAreFailuresAndALogWithoutAnyHintPrintsItsEnd()
    {
        string[] build = Run(
        [
            "  Restored C:\\repo\\src\\MyCapture.Core\\MyCapture.Core.csproj.",
            "C:\\repo\\src\\A.cs(3,5): error CS0103: The name 'x' does not exist in the current context",
            "Build FAILED.",
        ]);
        Assert.Equal(
            ["Failures (2 line(s)):",
                "C:\\repo\\src\\A.cs(3,5): error CS0103: The name 'x' does not exist in the current context",
                "Build FAILED."],
            build[..3]);

        string[] plain = Run(Enumerable.Range(1, 200).Select(i => $"line {i}"));
        Assert.Equal(Enumerable.Range(81, 120).Select(i => $"line {i}"), plain);
    }

    [Fact]
    public void BuildScriptPrintsItsStepDiagnosticsThroughTheHelper()
    {
        string script = File.ReadAllText(Path.Combine(Root(), "build", "build.ps1"));
        Assert.Contains(". (Join-Path $PSScriptRoot 'step-diagnostics.ps1')", script, StringComparison.Ordinal);
        Assert.Contains("Get-StepFailureDiagnostics -Lines @(Get-Content $log)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Select-Object -First 240", script, StringComparison.Ordinal);
    }

    private static string[] Run(IEnumerable<string> log)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            ". $env:MYCAPTURE_STEP_DIAGNOSTICS; Get-StepFailureDiagnostics -Lines @($input) | ForEach-Object { [Console]::Out.WriteLine($_) }" })
            start.ArgumentList.Add(argument);
        start.Environment["MYCAPTURE_STEP_DIAGNOSTICS"] = Path.Combine(Root(), "build", "step-diagnostics.ps1");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        foreach (string line in log) process.StandardInput.WriteLine(line);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(60_000), "PowerShell did not finish in 60 seconds.");
        string stdout = output.GetAwaiter().GetResult();
        string stderr = errors.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0 && stderr.Length == 0, $"exit {process.ExitCode}: {stderr}");
        return stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    }
}
