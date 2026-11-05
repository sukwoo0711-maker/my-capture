using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed partial class StoragePublicationFaultTests
{
    internal const string CampaignVariable = "MYCAPTURE_STORAGE_CRASH_CAMPAIGN";

    [StorageCrashCampaignFact]
    public async Task SeededProcessCrashCampaignPreservesEveryAcknowledgedGeneration()
    {
        string mode = Environment.GetEnvironmentVariable(CampaignVariable)!;
        Assert.True(mode is "smoke" or "full", "Select the fixed smoke or full campaign.");
        int count = mode == "full" ? 1000 : 8;
        const int seed = 20261104;
        int[] boundaries = Enumerable.Range(0, count).Select(index => index % 4).ToArray();
        var random = new Random(seed);
        for (int index = boundaries.Length - 1; index > 0; index--)
        {
            int other = random.Next(index + 1);
            (boundaries[index], boundaries[other]) = (boundaries[other], boundaries[index]);
        }
        string root = Path.Combine(Fixture.RepositoryRoot(), "artifacts", "storage-crash-campaign",
            $"{mode}-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(root);
        string core = typeof(CaptureQueue).Assembly.Location;
        string test = typeof(StoragePublicationFaultTests).Assembly.Location;
        string coreBefore = FileHash(core), testBefore = FileHash(test);
        var started = DateTimeOffset.UtcNow;
        WriteNew("plan.json", new
        {
            mode, count, seed, boundaries, started, coreBefore, testBefore,
            processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
            scope = "Fresh owned process trees, balanced shuffled publication boundaries; no OS reset or random payload campaign.",
            payload = "Existing one-record Unicode fixture, exact image/layer/OCR/folder/index assertions unchanged.",
        });

        int completed = 0;
        int[] perBoundary = new int[4];
        using var progressFile = new FileStream(Path.Combine(root, "progress.jsonl"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var progress = new StreamWriter(progressFile) { AutoFlush = true };
        Exception? failure = null;
        try
        {
            for (int index = 0; index < boundaries.Length; index++)
            {
                int boundary = boundaries[index];
                long timestamp = Stopwatch.GetTimestamp();
                string fixture = await RunCrashBoundaryAsync(boundary,
                    $"campaign-{seed}-{index:D4}-boundary-{boundary}");
                string receiptPath = Path.Combine(fixture, "result.json");
                using JsonDocument result = JsonDocument.Parse(File.ReadAllText(receiptPath));
                Assert.Equal("PASS", result.RootElement.GetProperty("result").GetProperty("status").GetString());
                Assert.Equal(boundary, result.RootElement.GetProperty("result").GetProperty("boundary").GetInt32());
                Assert.Equal(coreBefore, result.RootElement.GetProperty("coreSha256").GetString());
                Assert.Equal(testBefore, result.RootElement.GetProperty("testSha256").GetString());
                completed++;
                perBoundary[boundary]++;
                progress.WriteLine(JsonSerializer.Serialize(new
                {
                    index, boundary, status = "PASS", fixture, receiptSha256 = FileHash(receiptPath),
                    elapsedMs = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds,
                    utc = DateTimeOffset.UtcNow, completed,
                }));
            }
            Assert.Equal(count, completed);
            Assert.All(perBoundary, value => Assert.Equal(count / 4, value));
            Assert.Equal(coreBefore, FileHash(core));
            Assert.Equal(testBefore, FileHash(test));
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            WriteNew("summary.json", new
            {
                status = failure is null && completed == count ? "PASS" : "FAIL",
                mode, count, completed, perBoundary, seed, started, finished = DateTimeOffset.UtcNow,
                coreBefore, coreAfter = FileHash(core), testBefore, testAfter = FileHash(test),
                failure = failure?.ToString(),
                fullCampaignCompleted = mode == "full" && failure is null && completed == 1000,
            });
        }

        void WriteNew(string name, object value)
        {
            using var stream = new FileStream(Path.Combine(root, name), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed class StorageCrashCampaignFactAttribute : FactAttribute
{
    public StorageCrashCampaignFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(StoragePublicationFaultTests.CampaignVariable) is not ("smoke" or "full"))
            Skip = "Opt-in owned-process crash campaign; smoke=8, full=1000 with four balanced seeded boundaries.";
    }
}
