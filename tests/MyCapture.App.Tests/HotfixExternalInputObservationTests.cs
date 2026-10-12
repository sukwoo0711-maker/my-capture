using MyCapture.App.Diagnostics;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Pure parsing of the owned external fixture's native key observations; no input is injected.</summary>
public sealed class HotfixExternalInputObservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteUnmodifiedNativeSequence_ReportsActualCapsLock(bool capsLock)
    {
        string trace = Trace(capsLock);
        Assert.True(HotfixUxSelfTest.TryObserveExternalKeyBatch(trace, out bool actual, out string observation));
        Assert.Equal(capsLock, actual);
        Assert.Contains("received once in order", observation);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("reordered")]
    [InlineData("duplicate")]
    [InlineData("shift")]
    [InlineData("control")]
    [InlineData("focus")]
    [InlineData("caps-changed")]
    [InlineData("caps-missing")]
    [InlineData("caps-malformed")]
    [InlineData("caps-duplicate")]
    public void InvalidOrUnstableObservation_DoesNotRelaxTheCaseAndDeliveryContract(string kind)
    {
        string trace = Trace(false);
        trace = kind switch
        {
            "missing" => string.Join('\n', trace.Split('\n').Take(4)),
            "reordered" => trace.Replace("key=C;", "key=S;").Replace("key=E;", "key=C;"),
            "duplicate" => trace + "\n" + trace.Split('\n')[0],
            "shift" => trace.Replace("modifiers=None", "modifiers=Shift"),
            "control" => trace.Replace("control=False", "control=True"),
            "focus" => trace.Replace("focused=True", "focused=False"),
            "caps-changed" => trace.Replace("key=P; modifiers=None; control=False; focused=True; capsLock=False",
                "key=P; modifiers=None; control=False; focused=True; capsLock=True"),
            "caps-missing" => trace.Replace("; capsLock=False", ""),
            "caps-malformed" => trace.Replace("capsLock=False", "capsLock=unknown"),
            "caps-duplicate" => trace.Replace("capsLock=False", "capsLock=False; capsLock=False"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.False(HotfixUxSelfTest.TryObserveExternalKeyBatch(trace, out _, out string observation));
        Assert.NotEmpty(observation);
    }

    [Fact]
    public void ShownAndKeyUpTrace_DoNotCountAsAdditionalKeyDowns()
    {
        string trace = "2026-10-12T16:51:16Z; PID=25232; Shown; capsLock=True\r\n" + Trace(true)
            + "\r\n2026-10-12T16:51:17Z; PID=25232; KeyUp key=Escape; modifiers=None; focused=True";
        Assert.True(HotfixUxSelfTest.TryObserveExternalKeyBatch(trace, out bool actual, out _));
        Assert.True(actual);
    }

    [Fact]
    public void TraceReader_AllowsFixtureAppendWhileOpen_AndPreservesEveryKeyObservation()
    {
        string directory = OwnedTestDirectory.Create("MyCapture-HotfixTraceSharing-");
        string path = Path.Combine(directory, "external-input-events.txt");
        var encoding = new UTF8Encoding(false);
        string prefix = "2026-10-12T17:16:33Z; PID=47300; Shown; capsLock=True\n";
        string events = Trace(true);
        File.WriteAllText(path, prefix, encoding);

        // ReadAllText's former read-only sharing prevents the child's AppendAllText writer.
        // Hold that same sharing mode open to reproduce the collision deterministically.
        using (var oldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => File.AppendAllText(path, events, encoding));
        Assert.Equal(prefix, File.ReadAllText(path, encoding));

        using (var stream = HotfixUxSelfTest.OpenExternalInputTrace(path))
        {
            File.AppendAllText(path, events, encoding);
            using var reader = new StreamReader(stream, encoding);
            string observed = reader.ReadToEnd();
            Assert.Equal(prefix + events, observed);
            Assert.True(HotfixUxSelfTest.TryObserveExternalKeyBatch(observed, out bool capsLock, out _));
            Assert.True(capsLock);
        }
        Assert.Equal(prefix + events, File.ReadAllText(path, encoding));
        // Retain the unique synthetic fixture as failure/success evidence; close handles only.
    }

    [Theory]
    [InlineData(true, 150, false, "Pass")]
    [InlineData(true, 2000, false, "Pass")]
    [InlineData(true, 2001, false, "Fail")]
    [InlineData(true, 19934, false, "Fail")]
    [InlineData(false, 2050, false, "Fail")]
    [InlineData(true, 150, true, "Pass")]
    [InlineData(true, 19934, true, "Warning")]
    [InlineData(false, 30050, true, "Fail")]
    public void ForegroundAcquisitionTime_IsPartOfTheVerdict(bool acquired, int milliseconds, bool attended, string expected)
    {
        Assert.Equal(Enum.Parse<HotfixUxSelfTest.ForegroundVerdict>(expected),
            HotfixUxSelfTest.EvaluateForeground(acquired, TimeSpan.FromMilliseconds(milliseconds), attended));
        // An unattended run never waits past its budget; only an attended run waits for an operator.
        Assert.Equal(attended ? TimeSpan.FromSeconds(30) : HotfixUxSelfTest.UnattendedForegroundBudget,
            HotfixUxSelfTest.ForegroundWaitLimit(attended));
    }

    [Fact]
    public void WarningsKeepTheReportFromTheExactPassThePackagedGateRequires()
    {
        Assert.Equal("RESULT: PASS", HotfixUxSelfTest.ResultLine(passed: true, warnings: 0));
        Assert.Equal("RESULT: PASS WITH WARNINGS (1)", HotfixUxSelfTest.ResultLine(passed: true, warnings: 1));
        Assert.Equal("RESULT: FAIL", HotfixUxSelfTest.ResultLine(passed: false, warnings: 1));
        Assert.NotEqual("RESULT: PASS", HotfixUxSelfTest.ResultLine(passed: true, warnings: 1));
    }

    [Fact]
    public void FixtureTracePath_WithEveryPowerShellQuote_ReachesTheScriptAsData()
    {
        // PowerShell ends single-quoted strings at ' and at U+2018-U+201B; a path such as
        // C:\Users\D’Angelo must reach the fixture unchanged and never become script text.
        string root = OwnedTestDirectory.Create("MyCapture-HotfixQuotes-");
        string directory = Directory.CreateDirectory(Path.Combine(root, "D\u2019Angelo \u2018x\u201A\u201B 'y")).FullName;
        string path = Path.Combine(directory, "external-input-events.txt");
        ProcessStartInfo start = HotfixUxSelfTest.CreateExternalInputStartInfo(path,
            HotfixUxSelfTest.ExternalInputTracePrologue + "Write-InputTrace 'probe'");
        Assert.DoesNotContain(directory, Assert.Single(start.ArgumentList, argument => argument.Contains("Write-InputTrace", StringComparison.Ordinal)));
        Assert.Equal(path, start.Environment[HotfixUxSelfTest.InputTraceVariable]);
        start.RedirectStandardError = true;
        using Process process = Process.Start(start)!;
        string errors = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "The fixture prologue did not finish.");
        Assert.True(process.ExitCode == 0, errors);
        string trace = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("; PID=" + process.Id + "; probe", trace, StringComparison.Ordinal);
        // Retain the unique synthetic fixture as evidence; nothing outside it was written.
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    private static string Trace(bool capsLock) => string.Join('\n', new[] { "C", "E", "P", "S", "Escape" }
        .Select(key => $"2026-10-12T16:51:16Z; PID=25232; KeyDown key={key}; modifiers=None; control=False; focused=True; capsLock={capsLock}; text=synthetic"));
}
