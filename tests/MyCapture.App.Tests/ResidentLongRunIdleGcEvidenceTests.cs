using System.IO;
using System.Text.Json;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentLongRunIdleGcEvidenceTests
{
    [Fact]
    public void CompletionPublishedBeforeWaitingRetainsItsRecordedPair()
    {
        string root = FreshRoot();
        using var evidence = new ResidentLongRunEvidence(root);
        Assert.Equal(0, evidence.CompletedIdleGcCount);
        evidence.ObserveIdleGc(Row("start", 100, 100));
        Assert.Equal(0, evidence.CompletedIdleGcCount);
        evidence.ObserveIdleGc(Row("finish", 100, 120));
        Assert.Equal(1, evidence.CompletedIdleGcCount);
        Assert.Null(evidence.ObserverError);
        using var reader = new StreamReader(new FileStream(Path.Combine(root, "long-run-events.jsonl"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string[] lines = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[1]);
        using JsonDocument last = JsonDocument.Parse(lines[2]);
        Assert.Equal("start", first.RootElement.GetProperty("data").GetProperty("Phase").GetString());
        Assert.Equal("finish", last.RootElement.GetProperty("data").GetProperty("Phase").GetString());
        Assert.Equal(100, last.RootElement.GetProperty("data").GetProperty("StartTimestamp").GetInt64());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingMismatchedReversedOrFailedFinishCannotSignalCompletion(int defect)
    {
        using var evidence = new ResidentLongRunEvidence(FreshRoot());
        if (defect != 0) evidence.ObserveIdleGc(Row("start", 100, 100));
        evidence.ObserveIdleGc(Row("finish", defect == 1 ? 99 : 100, defect == 2 ? 99 : 120,
            defect == 3 ? "SyntheticCollectionFailure" : null));
        Assert.Equal(0, evidence.CompletedIdleGcCount);
        Assert.Null(evidence.ObserverError);
    }

    [Fact]
    public void DuplicateFinishDoesNotPublishASecondCompletion()
    {
        using var evidence = new ResidentLongRunEvidence(FreshRoot());
        evidence.ObserveIdleGc(Row("start", 100, 100));
        evidence.ObserveIdleGc(Row("finish", 100, 120));
        evidence.ObserveIdleGc(Row("finish", 100, 120));
        Assert.Equal(1, evidence.CompletedIdleGcCount);
        evidence.ObserveIdleGc(Row("start", 200, 200));
        evidence.ObserveIdleGc(Row("finish", 200, 220));
        Assert.Equal(2, evidence.CompletedIdleGcCount);
    }

    [Fact]
    public void FailedEvidenceWriteCannotPublishCompletion()
    {
        using var evidence = new ResidentLongRunEvidence(FreshRoot());
        evidence.ObserveIdleGc(Row("start", 100, 100));
        evidence.Dispose();
        evidence.ObserveIdleGc(Row("finish", 100, 120));
        Assert.Equal(0, evidence.CompletedIdleGcCount);
        Assert.Equal(typeof(ObjectDisposedException).FullName, evidence.ObserverError);
    }

    private static ResidentLongRunEvidence.IdleGcObservation Row(string phase, long start, long timestamp,
        string? error = null) => new(phase, start, timestamp, 0, 0, null, null, error);

    private static string FreshRoot() => DiagnosticOutputPaths.Create(Path.Combine(AppContext.BaseDirectory,
        "RetainedTestFixtures", nameof(ResidentLongRunIdleGcEvidenceTests), Guid.NewGuid().ToString("N")));
}
