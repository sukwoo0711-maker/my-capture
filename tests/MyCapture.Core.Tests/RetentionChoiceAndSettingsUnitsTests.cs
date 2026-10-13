using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Automatic image deletion keeps its seven-day default but can be turned off, a stored hour count
/// from an earlier version survives, the highlighter is edited in percent over the stored alpha,
/// and ranges are written "1–64".
/// </summary>
public sealed class RetentionChoiceAndSettingsUnitsTests
{
    [Fact]
    public void Never_KeepsEveryImage_AndShowsNoCountdown()
    {
        using var language = UiText.UseLanguage("ko-KR");
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { ImageRetentionHours = CaptureRetention.NeverHours };
        var queue = new CaptureQueue(workspace.Paths, limits, NullLogger<CaptureQueue>.Instance);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ancient = new CaptureRecord { Id = Guid.NewGuid(), CreatedAt = now.AddYears(-3), UpdatedAt = now.AddYears(-3), TotalBytes = 10 };
        queue.Add(ancient);

        Assert.Equal(0, queue.ExpireHistory(now));
        Assert.NotNull(queue.Find(ancient.Id));
        TimeSpan ttl = CaptureRetention.TimeToLive(limits);
        Assert.True(CaptureRetention.IsIndefinite(ttl));
        Assert.Equal(UiText.Get("Gallery.RetentionNever"),
            CaptureRetention.FormatCountdown(ancient.CreatedAt, ttl, now, isImage: true, isPinned: false));
        // The default is still seven days, and a stored hour count still means hours.
        Assert.Equal(TimeSpan.FromDays(7), CaptureRetention.TimeToLive(new QueueSettings()));
        Assert.Equal(TimeSpan.FromHours(36), CaptureRetention.TimeToLive(new QueueSettings { ImageRetentionHours = 36 }));
        Assert.Equal([24, 168, 720, 2160, 8760, CaptureRetention.NeverHours], CaptureRetention.PresetHours);
    }

    [Theory]
    [InlineData(CaptureRetention.NeverHours, CaptureRetention.NeverHours)]
    [InlineData(36, 36)]
    [InlineData(9000, 8760)]
    [InlineData(0, 1)]
    public void SettingsFile_KeepsNeverAndCustomHours_AndStillClampsOthers(int stored, int loaded)
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings();
        settings.Queue.ImageRetentionHours = stored;
        store.Save(settings);
        Assert.Equal(loaded, store.Load().Queue.ImageRetentionHours);
    }

    [Fact]
    public void Draft_AcceptsNever_AndMapsItBack()
    {
        var draft = new SettingsDraft(new AppSettings());
        draft.ImageRetentionHours = CaptureRetention.NeverHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(draft.HasErrors);
        Assert.True(draft.HasUnappliedChanges);
        Assert.Equal(CaptureRetention.NeverHours, draft.ToAppSettings().Queue.ImageRetentionHours);
        draft.ImageRetentionHours = "8761";
        Assert.True(draft.HasErrors);
    }

    [Fact]
    public void HighlighterPercent_ConvertsAtTheBoundary_WithoutRewritingAnUntouchedAlpha()
    {
        var saved = new AppSettings();
        saved.Annotation.HighlighterAlpha = 90;
        var draft = new SettingsDraft(saved);
        Assert.Equal("35", draft.HighlighterOpacityPercent);

        // Typing the percentage the stored alpha already shows changes nothing.
        draft.HighlighterOpacityPercent = "3";
        draft.HighlighterOpacityPercent = "35";
        Assert.False(draft.HasUnappliedChanges);
        Assert.Equal(90, draft.ToAppSettings().Annotation.HighlighterAlpha);

        draft.HighlighterOpacityPercent = "50";
        Assert.Equal("128", draft.HighlighterAlpha);
        Assert.Equal(128, draft.ToAppSettings().Annotation.HighlighterAlpha);

        draft.HighlighterOpacityPercent = "101";
        Assert.True(draft.HasErrors);
        Assert.True(draft.HasUnappliedChanges);
        Assert.Equal("128", draft.HighlighterAlpha);
        draft.HighlighterOpacityPercent = "abc";
        Assert.True(draft.HasErrors);
        draft.HighlighterOpacityPercent = "100";
        Assert.False(draft.HasErrors);
        Assert.Equal(255, draft.ToAppSettings().Annotation.HighlighterAlpha);

        // The stored alpha (an imported file, a reset) updates the percentage.
        draft.HighlighterAlpha = "0";
        Assert.Equal("0", draft.HighlighterOpacityPercent);
        for (int percent = 0; percent <= 100; percent++)
            Assert.Equal(percent, SettingsDraft.AlphaToPercent(SettingsDraft.PercentToAlpha(percent)));
    }

    [Fact]
    public void Ranges_AreWrittenWithAnEnDash()
    {
        Assert.Equal("1–64", SettingsRanges.StrokeThickness.Describe());
        using var language = UiText.UseLanguage("ko-KR");
        var draft = new SettingsDraft(new AppSettings()) { MaxItems = "5" };
        Assert.Contains(UiText.Format("Text_CF32FE576AE7", "10–5000"), draft.GetErrors(nameof(SettingsDraft.MaxItems)).Cast<string>());
    }

    [Fact]
    public async Task RestoreRecordsAsync_IndexesABatchInOnePublication_AndSkipsWhatIsPresentOrMissing()
    {
        using var workspace = new TempWorkspace();
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings { MaxItems = 50 }, NullLogger<CaptureQueue>.Instance);
        DateTimeOffset now = DateTimeOffset.Now;
        CaptureRecord present = Add(queue, now.AddMinutes(-1));
        queue.Save();
        // Captures whose directories are back in place but which the index no longer names (an index
        // removal with no file handler leaves the files), plus one whose directory is gone.
        var returning = new List<CaptureRecord>();
        for (int index = 0; index < 5; index++)
        {
            CaptureRecord record = Add(queue, now.AddMinutes(-10 - index));
            record.RetentionStartedAt = now.AddDays(-30);
            returning.Add(record);
        }
        CaptureRecord missing = Add(queue, now.AddMinutes(-30));
        foreach (CaptureRecord record in returning.Append(missing)) Assert.True(queue.Remove(record.Id));
        TestRecycleBin.DeleteDirectory(queue.GetDirectory(missing), recursive: true);
        queue.Save();
        int writes = 0;
        queue.BeforePublicationWriteForTest = (path, _) => { if (path == workspace.Paths.IndexFile) writes++; };

        IReadOnlyList<CaptureRecord> restored = await queue.RestoreRecordsAsync([.. returning, present, missing]);

        Assert.Equal(returning.Select(record => record.Id), restored.Select(record => record.Id));
        Assert.Equal(1, writes);
        Assert.Equal(6, queue.Count);
        Assert.All(returning, record => Assert.True(record.RetentionStartedAt > now.AddMinutes(-1)));
        Assert.All(returning, record => Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Meta))));
        Assert.All(queue.Records, record => Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Original)),
            $"{record.Id} present={record.Id == present.Id} dir={record.RelativeDirectory}"));
        var reloaded = new CaptureQueue(workspace.Paths, new QueueSettings { MaxItems = 50 }, NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        Assert.Equal(queue.Records.Select(record => record.Id).Order(), reloaded.Records.Select(record => record.Id).Order());
    }

    private static CaptureRecord Add(CaptureQueue queue, DateTimeOffset created)
    {
        var record = new CaptureRecord
        {
            Id = Guid.NewGuid(), CreatedAt = created, UpdatedAt = created, TotalBytes = 10, Width = 16, Height = 9,
        };
        queue.Add(record);
        Directory.CreateDirectory(queue.GetDirectory(record));
        File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.Original), [9]);
        return record;
    }
}
