using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The closed-pin restore history: LIFO order, the user count limit (0 = off), and the
/// retained-byte budget, all without a window.
/// </summary>
public sealed class ClosedPinHistoryTests
{
    private sealed record Entry(string Name);

    private static ClosedPinHistory<Entry> NewHistory(int limit = 20, long maxBytes = ClosedPinMemory.DefaultMaxRetainedBytes) =>
        new(() => limit, maxBytes);

    private static List<string> PopAll(ClosedPinHistory<Entry> history)
    {
        var names = new List<string>();
        while (history.TryPop(out Entry? entry))
        {
            names.Add(entry.Name);
        }

        return names;
    }

    [Fact]
    public void Pop_ReturnsMostRecentlyClosedFirst()
    {
        ClosedPinHistory<Entry> history = NewHistory();
        Assert.True(history.Push(new Entry("a"), 10));
        Assert.True(history.Push(new Entry("b"), 10));
        Assert.True(history.Push(new Entry("c"), 10));

        Assert.Equal(3, history.Count);
        Assert.Equal(["c", "b", "a"], PopAll(history));
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.RetainedBytes);
    }

    [Fact]
    public void TryPop_OnEmptyHistory_ReturnsFalse()
    {
        ClosedPinHistory<Entry> history = NewHistory();
        Assert.False(history.TryPop(out Entry? entry));
        Assert.Null(entry);
    }

    [Fact]
    public void CountLimit_DropsOldestEntries()
    {
        ClosedPinHistory<Entry> history = NewHistory(limit: 3);
        foreach (string name in new[] { "a", "b", "c", "d", "e" })
        {
            Assert.True(history.Push(new Entry(name), 100));
        }

        Assert.Equal(3, history.Count);
        Assert.Equal(300, history.RetainedBytes);
        Assert.Equal(["e", "d", "c"], PopAll(history));
    }

    [Fact]
    public void LoweringLimit_TrimsOldestOnNextAccess()
    {
        int limit = 5;
        var history = new ClosedPinHistory<Entry>(() => limit);
        foreach (string name in new[] { "a", "b", "c", "d", "e" })
        {
            history.Push(new Entry(name), 1);
        }

        limit = 2;

        Assert.Equal(2, history.Count);
        Assert.Equal(["e", "d"], PopAll(history));
    }

    [Fact]
    public void ZeroLimit_DisablesHistoryAndDiscardsKeptEntries()
    {
        int limit = 5;
        var history = new ClosedPinHistory<Entry>(() => limit);
        history.Push(new Entry("a"), 1);
        history.Push(new Entry("b"), 1);

        limit = 0;

        Assert.Equal(0, history.Count);
        Assert.False(history.Push(new Entry("c"), 1));
        Assert.Equal(0, history.Count);
        Assert.False(history.TryPop(out _));
    }

    [Fact]
    public void Trim_ReleasesEntriesAtOnce_AndRaisingLimitLaterDoesNotBringThemBack()
    {
        int limit = 3;
        var history = new ClosedPinHistory<Entry>(() => limit);
        history.Push(new Entry("a"), 1);
        history.Push(new Entry("b"), 1);

        limit = 0;
        history.Trim();
        limit = 3;

        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.RetainedBytes);
    }

    [Fact]
    public void ByteBudget_DropsOldestUntilWithinBudget()
    {
        ClosedPinHistory<Entry> history = NewHistory(limit: 20, maxBytes: 100);
        history.Push(new Entry("a"), 40);
        history.Push(new Entry("b"), 40);
        history.Push(new Entry("c"), 40);

        Assert.Equal(2, history.Count);
        Assert.Equal(80, history.RetainedBytes);
        Assert.True(history.RetainedBytes <= history.MaxRetainedBytes);
        Assert.Equal(["c", "b"], PopAll(history));
    }

    [Fact]
    public void EntryLargerThanBudget_IsNotRetainedAndKeepsOthers()
    {
        ClosedPinHistory<Entry> history = NewHistory(limit: 20, maxBytes: 100);
        history.Push(new Entry("small"), 30);

        Assert.False(history.Push(new Entry("huge"), 101));

        Assert.Equal(1, history.Count);
        Assert.Equal(30, history.RetainedBytes);
        Assert.True(history.TryPop(out Entry? kept));
        Assert.Equal("small", kept.Name);
    }

    [Fact]
    public void ExactlyFullBudget_IsRetained()
    {
        ClosedPinHistory<Entry> history = NewHistory(limit: 20, maxBytes: 100);
        Assert.True(history.Push(new Entry("a"), 60));
        Assert.True(history.Push(new Entry("b"), 40));

        Assert.Equal(2, history.Count);
        Assert.Equal(100, history.RetainedBytes);
    }

    [Fact]
    public void NegativeEstimate_CountsAsZeroBytes()
    {
        ClosedPinHistory<Entry> history = NewHistory();
        history.Push(new Entry("a"), -5);
        Assert.Equal(0, history.RetainedBytes);
        Assert.Equal(1, history.Count);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(20, 20)]
    [InlineData(100, 100)]
    [InlineData(1000, 100)]
    public void Limit_IsClampedToTheSettingsRange(int configured, int expected)
    {
        Assert.Equal(expected, NewHistory(limit: configured).Limit);
    }

    [Fact]
    public void Clear_DiscardsEverything()
    {
        ClosedPinHistory<Entry> history = NewHistory();
        history.Push(new Entry("a"), 10);
        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.RetainedBytes);
    }

    [Fact]
    public void PixelEstimate_UsesWholeByteStridePerRow()
    {
        Assert.Equal(1920L * 1080 * 4, ClosedPinMemory.EstimatePixelBytes(1920, 1080, 32));
        Assert.Equal(1920L * 1080 * 3, ClosedPinMemory.EstimatePixelBytes(1920, 1080, 24));
        // 9 one-bit pixels need two bytes per row.
        Assert.Equal(2L * 5, ClosedPinMemory.EstimatePixelBytes(9, 5, 1));
        // An unresolved format is budgeted as 32-bit, never as free.
        Assert.Equal(10L * 10 * 4, ClosedPinMemory.EstimatePixelBytes(10, 10, 0));
        Assert.Equal(0, ClosedPinMemory.EstimatePixelBytes(0, 10, 32));
        Assert.Equal(0, ClosedPinMemory.EstimatePixelBytes(10, -1, 32));
    }

    [Fact]
    public void TextEstimate_IsUtf16Size()
    {
        Assert.Equal(0, ClosedPinMemory.EstimateTextBytes(null));
        Assert.Equal(0, ClosedPinMemory.EstimateTextBytes(string.Empty));
        Assert.Equal(6, ClosedPinMemory.EstimateTextBytes("표 1"));
    }

    [Fact]
    public void DefaultBudget_HoldsDefaultCountOfFullHdPins_ButNotTheMaximumOf4KPins()
    {
        long fullHd = ClosedPinMemory.EstimatePixelBytes(1920, 1080, 32);
        long fourK = ClosedPinMemory.EstimatePixelBytes(3840, 2160, 32);

        Assert.Equal(256L * 1024 * 1024, ClosedPinMemory.DefaultMaxRetainedBytes);
        Assert.True(20 * fullHd <= ClosedPinMemory.DefaultMaxRetainedBytes);
        Assert.True(100 * fourK > ClosedPinMemory.DefaultMaxRetainedBytes);

        var history = new ClosedPinHistory<Entry>(() => 100);
        for (int i = 0; i < 100; i++)
        {
            history.Push(new Entry(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), fourK);
        }

        Assert.True(history.RetainedBytes <= ClosedPinMemory.DefaultMaxRetainedBytes);
        Assert.Equal((int)(ClosedPinMemory.DefaultMaxRetainedBytes / fourK), history.Count);
        Assert.True(history.TryPop(out Entry? newest));
        Assert.Equal("99", newest.Name);
    }
}
