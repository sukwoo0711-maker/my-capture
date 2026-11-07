using System.Diagnostics;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class TranslationWorstCaseScalingTests(ITestOutputHelper output)
{
    private static string Repeat(string value, int n) => TranslationHardLimitTests.Repeat(value, n);

    [Theory]
    [InlineData("Dr")]
    [InlineData("eg")]
    [InlineData("initial")]
    [InlineData("mixed")]
    public Task SentenceAllocationsGrowLinearly(string family) => Watchdog("Sentences/" + family, () =>
    {
        string Input(int n) => family switch
        {
            "Dr" => Repeat("Dr. ", n) + "End.",
            "eg" => Repeat("e.g. A ", n),
            "initial" => Repeat("J. ", n) + "End.",
            _ => Repeat("Mr. Dr. Fig. No. ", n) + "End.",
        };
        long Measure(string text)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(TranslationSegmenter.Sentences(text).ToList());
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        string small = Input(1024), large = Input(4096);
        Measure(small);
        Measure(small);
        long a = Measure(small), b = Measure(large);
        Report("Sentences/" + family, 1024, a, b, 5, "allocated bytes");
    });

    public static IEnumerable<object[]> FindFamilies()
    {
        foreach (string family in new[] { "call", "camel-nested", "camel-upper", "dotted", "snake", "hex", "binary",
            "key", "version", "decimal", "range", "flag", "url", "path", "quoted", "code", "percent-path",
            "field", "id", "alnum", "acronym", "abbreviation", "translatable" }) yield return [family];
    }

    private static string FindInput(string family, int n) => family switch
    {
        "call" => string.Join('.', Enumerable.Repeat("a", n)) + "(!",
        "camel-nested" => "Aa" + new string('A', n) + "_",
        "camel-upper" => new string('A', n) + "a_",
        "dotted" => string.Join('.', Enumerable.Repeat("a", n)) + "(",
        "snake" => Repeat("a_", n) + "_",
        "hex" => "0x" + Repeat("ABCD_", n) + "_",
        "binary" => "0b" + Repeat("1_", n),
        "key" => Repeat("Ctrl+", n) + "x",
        "version" => "v" + Repeat("1.", n) + "1a",
        "decimal" => Repeat("1,", n) + "1",
        "range" => Repeat("1:", n) + "1",
        "flag" => "--a" + Repeat("-a", n),
        "url" => "https://" + new string('.', n),
        "path" => "C:\\" + new string('.', n),
        "quoted" => "'" + Repeat("a.", n),
        "code" => "`" + new string('a', n),
        "percent-path" => Repeat("%a", n),
        "field" => Repeat("a[1", n),
        "id" => "CVE-2024-" + new string('1', n) + "a",
        "alnum" => new string('1', n) + "a" + new string('1', n) + "_",
        "acronym" or "translatable" => Repeat("A1 ", n),
        _ => Repeat("Dr. ", n),
    };

    [Theory]
    [MemberData(nameof(FindFamilies))]
    public Task ProtectedPatternsDoNotGrowQuadratically(string family) => Watchdog("Find/" + family, () =>
    {
        void Run(string text)
        {
            if (family == "translatable") GC.KeepAlive(ProtectedTokens.HasTranslatableWords(text));
            else GC.KeepAlive(ProtectedTokens.Find(text));
        }
        double Once(string text, int repeats)
        {
            long start = Stopwatch.GetTimestamp();
            for (int repeat = 0; repeat < repeats; repeat++) Run(text);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds / repeats;
        }
        // Calibrate the small sample, never an absolute pass/fail time. Each measurement has its own watchdog.
        // The larger input stays within MaxLargeLength characters (4 MB): past the CPU caches the cost per
        // character rises with memory traffic (a 64 MB string of dots took about 1.5 times as long per
        // character as an 8 MB one), which is not what this test measures. A cheap family repeats the call
        // to fill its sample instead of growing further.
        const int MaxLargeLength = 1 << 21;
        int n = family == "camel-nested" ? 16 : family == "translatable" ? 32768 : 1024;
        int repeats = 1;
        string small = FindInput(family, n);
        while (true)
        {
            Run(small);
            Run(small);
            double calibration = Math.Min(Once(small, repeats), Math.Min(Once(small, repeats), Once(small, repeats)));
            if (calibration * repeats >= 3) break;
            string next = FindInput(family, n * 2);
            if (next.Length * 4L <= MaxLargeLength)
            {
                n *= 2;
                small = next;
            }
            else repeats *= 2;
        }
        string large = FindInput(family, checked(n * 4));
        Run(large);
        Run(large);
        double a = double.MaxValue, b = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            a = Math.Min(a, Once(small, repeats));
            b = Math.Min(b, Once(large, repeats));
        }
        Report("Find/" + family, n, a, b, 8, "ms");
    });

    [Theory]
    [InlineData("ascii")]
    [InlineData("hangul")]
    [InlineData("abbreviation")]
    [InlineData("url")]
    [InlineData("digits")]
    [InlineData("tab")]
    public Task RealTokenizerWorkGrowsLinearly(string family) => Watchdog("Split/" + family, () =>
    {
        string Input(int n) => family switch
        {
            "ascii" => new string('a', n),
            "hangul" => Repeat("가나다라마바사", n),
            "abbreviation" => Repeat("Dr. ", n) + "End.",
            "url" => "https://example.com/" + Repeat("path123/", n),
            "digits" => Repeat("1234567890", n),
            _ => Repeat("word\t", n),
        };
        (long Calls, long Characters) Measure(string text)
        {
            long calls = 0, characters = 0;
            int Count(string value)
            {
                calls++;
                characters += value.Length;
                return TranslationHardLimitTests.Count(value);
            }
            GC.KeepAlive(TranslationSegmenter.Split(text, Count, 64, 256));
            return (calls, characters);
        }
        _ = TranslationHardLimitTests.Model.Value;
        var a = Measure(Input(4096));
        var b = Measure(Input(16384));
        Report("Split/" + family + "/calls", 4096, a.Calls, b.Calls, 5, "calls");
        Report("Split/" + family + "/characters", 4096, a.Characters, b.Characters, 5, "characters");
    });

    private void Report(string family, int n, double small, double large, double limit, string units)
    {
        double ratio = large / Math.Max(small, double.Epsilon);
        output.WriteLine($"{family}: n={n}, 4n={4 * n}, small={small:F3}, large={large:F3} {units}, ratio={ratio:F3}, limit={limit}");
        Assert.True(ratio <= limit, $"{family} is superlinear: {small:F3} -> {large:F3} {units}, ratio {ratio:F3} > {limit}.");
    }

    private static async Task Watchdog(string family, Action action)
    {
        Task work = Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(ReferenceEquals(work, finished), $"{family} is superlinear: the background measurement exceeded the 60 s watchdog.");
        await work;
    }
}
