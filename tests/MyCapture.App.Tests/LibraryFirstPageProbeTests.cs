using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryFirstPageProbeTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1000", 1000)]
    public void StandaloneOptionsPreserveProductionModes(string count, int expected)
    {
        Assert.Equal(expected, GalleryFirstPageProbe.ValidateOptions([GalleryFirstPageProbe.Scenario], false, false,
            new HashSet<string>(), false, false, Args(count)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5000")]
    [InlineData("-1")]
    public void OtherCountsAreRejected(string count) => Assert.Throws<ArgumentException>(() =>
        GalleryFirstPageProbe.ValidateOptions([GalleryFirstPageProbe.Scenario], false, false,
            new HashSet<string>(), false, false, Args(count)));

    [Fact]
    public void DiagnosticVariantsAndAmbiguousArgumentsAreRejected()
    {
        string[] scenarios = [GalleryFirstPageProbe.Scenario];
        var empty = new HashSet<string>();
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions(scenarios, true, false, empty, false, false, Args("0")));
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions(scenarios, false, true, empty, false, false, Args("0")));
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions(scenarios, false, false, new HashSet<string> { "no-idle-gc" }, false, false, Args("0")));
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions(scenarios, false, false, empty, true, false, Args("0")));
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions(scenarios, false, false, empty, false, true, Args("0")));
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions([GalleryFirstPageProbe.Scenario, "library"], false, false, empty, false, false, Args("0")));
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions(scenarios, false, false, empty, false, false,
            [.. Args("0"), GalleryFirstPageProbe.CountSwitch, "0"]));
        Assert.Throws<ArgumentException>(() => GalleryFirstPageProbe.ValidateOptions(scenarios, false, false, empty, false, false,
            [GalleryFirstPageProbe.CountSwitch, "0", GalleryFirstPageProbe.InputShaSwitch, "bad"]));
        Assert.Null(GalleryFirstPageProbe.ValidateOptions(["library"], false, false, empty, false, false, []));
    }

    private static string[] Args(string count) =>
        [GalleryFirstPageProbe.CountSwitch, count, GalleryFirstPageProbe.InputShaSwitch, new string('A', 64)];

    [LibraryFirstPageFixtureFact]
    public void PrepareImmutablePreindexedSyntheticLibraries()
    {
        // A fresh output only: an existing file or directory is preserved and rejected before creation.
        string output = TestDiagnosticArtifacts.NewDirectory(Environment.GetEnvironmentVariable(LibraryFirstPageFixtureFactAttribute.Variable)
            ?? throw new InvalidOperationException("An explicit fresh output is required."));
        foreach (int count in new[] { 0, 1000 })
        {
            // The historical generator keeps its original default behavior. This distinct workload
            // asks it to cache every synthetic OCR index; it uses the real App indexer later.
            string original = ProbeLibrary.Create(count, fullyIndexed: true);
            AppPaths paths = AppPaths.CreateForRoot(original);
            var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load();
            Assert.Equal(OcrQuality.Fast, settings.Ocr.Quality);
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            queue.Load();
            Assert.Equal(count, queue.Count);
            Assert.All(queue.Records, record => Assert.True(record.HasCurrentOcrIndex));
            string destination = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(output, "library-" + count));
            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(destination, "profile"));
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(original, path).Replace('\\', '/');
                string target = profile;
                foreach (string part in relative.Split('/')) target = DiagnosticOutputPaths.Child(target, part);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (FileStream source = File.OpenRead(path))
                using (var copied = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(copied);
                string digest = Hash(path);
                Assert.Equal(digest, Hash(target));
                files.Add("profile/" + relative, digest);
            }
            DateTimeOffset prepared = DateTimeOffset.UtcNow;
            var manifest = new
            {
                Schema = "library-first-page-preindexed-v1", Count = count,
                PreparedUtc = prepared, ValidUntilUtc = prepared.AddHours(12),
                RecordIds = queue.Records.Select(record => record.Id).Order().ToArray(), Files = files,
                SourceGenerator = "ProbeLibrary.Create(count, fullyIndexed: true)",
                HistoricalWorkloadEquivalent = false, Seed = "Original PNG=1; Thumbnail PNG=2; generation identity pinned by complete file hashes",
                OriginalSyntheticRootPreserved = original,
            };
            using var manifestStream = new FileStream(DiagnosticOutputPaths.Child(destination, "library-input.json"), FileMode.CreateNew);
            JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
            manifestStream.Flush(true);
            // The generated source and published fixture are both preserved. No cleanup/deletion.
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed class LibraryFirstPageFixtureFactAttribute : FactAttribute
{
    internal const string Variable = "MYCAPTURE_LIBRARY_FIRST_PAGE_FIXTURE";
    public LibraryFirstPageFixtureFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = "Set MYCAPTURE_LIBRARY_FIRST_PAGE_FIXTURE to a fresh output to prepare the synthetic R2R comparison inputs.";
    }
}
