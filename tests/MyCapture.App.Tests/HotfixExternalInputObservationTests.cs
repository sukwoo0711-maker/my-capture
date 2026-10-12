using MyCapture.App.Diagnostics;
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

    private static string Trace(bool capsLock) => string.Join('\n', new[] { "C", "E", "P", "S", "Escape" }
        .Select(key => $"2026-10-12T16:51:16Z; PID=25232; KeyDown key={key}; modifiers=None; control=False; focused=True; capsLock={capsLock}; text=synthetic"));
}
