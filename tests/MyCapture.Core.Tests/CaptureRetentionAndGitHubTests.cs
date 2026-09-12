using MyCapture.Core.GitHub;
using MyCapture.Core.Pin;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureRetentionTestsExtras
{
    [Fact]
    public void DefaultTtl_IsSevenDays()
    {
        Assert.Equal(168, CaptureRetention.DefaultImageRetentionHours);
        Assert.Equal(TimeSpan.FromDays(7), CaptureRetention.TimeToLive(new QueueSettings()));
    }

    [Fact]
    public void FormatCountdown_UsesDayPrefixThenHours()
    {
        using (UiText.UseLanguage("en-US"))
        {
            DateTimeOffset created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            TimeSpan ttl = TimeSpan.FromDays(7);
            Assert.Equal("d-7", CaptureRetention.FormatCountdown(created, ttl, created, isImage: true, isPinned: false));
            Assert.Equal("d-5 12h", CaptureRetention.FormatCountdown(created, ttl, created.AddHours(36), isImage: true, isPinned: false));
            Assert.Equal("3h", CaptureRetention.FormatCountdown(created, ttl, created.AddDays(7).AddHours(-3), isImage: true, isPinned: false));
            Assert.Equal("Kept", CaptureRetention.FormatCountdown(created, ttl, created, isImage: true, isPinned: true));
            Assert.Equal("No expiry", CaptureRetention.FormatCountdown(created, ttl, created, isImage: false, isPinned: false));
        }
    }

    [Fact]
    public void ExpireHistory_HonoursConfiguredHours()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { ImageRetentionHours = 7 };
        var queue = new CaptureQueue(workspace.Paths, limits, Microsoft.Extensions.Logging.Abstractions.NullLogger<CaptureQueue>.Instance);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var stale = new CaptureRecord { Id = Guid.NewGuid(), CreatedAt = now.AddHours(-8), UpdatedAt = now, TotalBytes = 4 };
        var fresh = new CaptureRecord { Id = Guid.NewGuid(), CreatedAt = now.AddHours(-6), UpdatedAt = now, TotalBytes = 4 };
        queue.Add(stale);
        queue.Add(fresh);
        Assert.Equal(1, queue.ExpireHistory(now));
        Assert.Null(queue.Find(stale.Id));
        Assert.NotNull(queue.Find(fresh.Id));
    }
}

public sealed class GitHubIssueImageUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_UsesSafeDefault(string? raw)
    {
        Assert.True(GitHubIssueImageUrl.TryNormalize(raw, out string url));
        Assert.Equal(GitHubIssueImageUrl.DefaultIssueUrl, url);
    }

    [Fact]
    public void NumberedIssue_IsCanonicalized()
    {
        Assert.True(GitHubIssueImageUrl.TryNormalize(
            "https://github.com/nexu-io/open-design/issues/80085?foo=1#write",
            out string url));
        Assert.Equal("https://github.com/nexu-io/open-design/issues/80085", url);
    }

    [Theory]
    [InlineData("http://github.com/a/b/issues/1")]
    [InlineData("https://evil.com/a/b/issues/1")]
    [InlineData("https://github.com/a/b/pulls/1")]
    [InlineData("https://github.com/a/b/issues/abc")]
    public void RejectsUnsafeOrNonIssueUrls(string raw) =>
        Assert.False(GitHubIssueImageUrl.TryNormalize(raw, out _));

    [Fact]
    public void ExtractsUserAttachmentsUrl()
    {
        const string html = """<img width="4244" alt="Image" src="https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9"/>""";
        Assert.True(GitHubIssueImageUrl.TryExtractAttachmentUrl(html, out string url));
        Assert.Equal("https://github.com/user-attachments/assets/36e444a2-e629-4e04-b737-ea44dd5c0bd9", url);
    }
}

public sealed class ThemeCatalogTests
{
    [Fact]
    public void ParseAndRoundTripKnownIds()
    {
        Assert.Equal(AppTheme.Midnight, AppThemeNames.Parse(null));
        Assert.Equal(AppTheme.Daylight, AppThemeNames.Parse("light"));
        Assert.Equal(AppTheme.HighContrast, AppThemeNames.Parse("high-contrast"));
        Assert.Equal("daylight", AppThemeNames.ToSetting(AppTheme.Daylight));
        Assert.Contains("Surface.Base", ThemeCatalog.ColorsFor(AppTheme.Daylight).Keys);
        Assert.Contains("Surface.Badge", ThemeCatalog.ColorsFor(AppTheme.Daylight).Keys);
        Assert.Equal(
            ThemeCatalog.ColorsFor(AppTheme.Midnight).Keys.OrderBy(k => k, StringComparer.Ordinal),
            ThemeCatalog.ColorsFor(AppTheme.Daylight).Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(
            ThemeCatalog.ColorsFor(AppTheme.Midnight).Keys.OrderBy(k => k, StringComparer.Ordinal),
            ThemeCatalog.ColorsFor(AppTheme.HighContrast).Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.NotEqual(
            ThemeCatalog.ColorsFor(AppTheme.Midnight)["Surface.Base"],
            ThemeCatalog.ColorsFor(AppTheme.Daylight)["Surface.Base"]);
    }
}

public sealed class PinCascadeTests
{
    [Fact]
    public void TenCascadedPlacements_NeverShareTopLeft()
    {
        var seen = new HashSet<(int Left, int Top)>();
        for (int i = 0; i < 10; i++)
        {
            PinGeometry.Placement p = PinGeometry.CascadedPlacement(
                400, 300, 0, 0, 1920, 1080, 400, 300, i);
            Assert.True(seen.Add(((int)Math.Round(p.Left), (int)Math.Round(p.Top))), $"overlap at index {i}");
            Assert.True(p.Left + p.Width <= 1920 + 0.001);
            Assert.True(p.Top + p.Height <= 1080 + 0.001);
        }
    }
}
