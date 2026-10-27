using System.Diagnostics;
using System.Text.Json;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentProbeMetricsTests
{
    [Fact]
    public void PrivateWorkingSetCountsNonsharedPagesRatherThanCommitOrShareCount()
    {
        nuint[] entries = [0x12345004, 0x23456104, 0x34567024, 0x456781E4];
        Assert.Equal(8192, ResidentProbeWorkingSet.CountPrivateBytes(entries, 4096));
        Assert.Equal(0, ResidentProbeWorkingSet.CountPrivateBytes([], 4096));
    }

    [Fact]
    public void PrivateWorkingSetCanBeReadForThisProcessWithoutScreenAccess()
    {
        using Process process = Process.GetCurrentProcess();
        var reader = new ResidentProbeWorkingSet();
        (long? bytes, string? error) = reader.Read(process.Handle, process.WorkingSet64);
        Assert.Null(error);
        Assert.NotNull(bytes);
        Assert.True(bytes > 0);
    }

    [Fact]
    public void RenderCompletionRemainsSeparateFromFirstRenderingAndUnknownIsNull()
    {
        var first = new ResidentProbeRecorder.Breakdown("capture", 1, 29, 800, []);
        string unavailable = JsonSerializer.Serialize(first with { RenderThreadCompleteError = "unavailable" });
        using JsonDocument missing = JsonDocument.Parse(unavailable);
        Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("RenderThreadCompleteMs").ValueKind);
        var complete = first with { RenderThreadCompleteMs = 51.2 };
        Assert.Equal(29, complete.SelectorMs);
        Assert.Equal(51.2, complete.RenderThreadCompleteMs);
    }
}
