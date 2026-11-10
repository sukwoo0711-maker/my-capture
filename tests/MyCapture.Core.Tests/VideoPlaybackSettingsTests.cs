using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class VideoPlaybackSettingsTests
{
    [Fact]
    public void OlderSettingsDefaultToSoundOn()
    {
        Assert.False(new AppSettings().Recording.PlaybackMuted);
        AppSettings older = JsonSerializer.Deserialize<AppSettings>("{\"recording\":{}}", JsonDefaults.Readable)!;
        Assert.False(older.Recording.PlaybackMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MuteChoiceSurvivesCloneDraftAndSerialization(bool muted)
    {
        var original = new AppSettings { Recording = { PlaybackMuted = muted } };
        AppSettings clone = original.DeepClone();
        Assert.Equal(muted, clone.Recording.PlaybackMuted);
        var draft = new SettingsDraft(clone);
        draft.RecordingIncludeCursor = false;
        AppSettings edited = draft.ToAppSettings();
        Assert.Equal(muted, edited.Recording.PlaybackMuted);
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(edited, JsonDefaults.Readable), JsonDefaults.Readable)!;
        Assert.Equal(muted, loaded.Recording.PlaybackMuted);
        clone.Recording.PlaybackMuted = !muted;
        Assert.Equal(muted, original.Recording.PlaybackMuted);
    }
}
