using System.Text.Json;
using MyCapture.Core.Recording;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingAudioSettingsTests
{
    [Fact]
    public void DefaultAndOlderSettingsLeaveBothSourcesOff()
    {
        Assert.False(new RecordingSettings().RecordSystemAudio);
        Assert.False(new RecordingSettings().RecordMicrophone);
        Assert.False(NewInstallDefaults.Create().Recording.RecordSystemAudio);
        Assert.False(NewInstallDefaults.Create().Recording.RecordMicrophone);
        var older = JsonSerializer.Deserialize<AppSettings>(
            """{"schemaVersion":4,"recording":{"frameRate":"fps60","includeCursor":false}}""", JsonDefaults.Readable)!;
        Assert.Equal(RecordingFrameRate.Fps60, older.Recording.FrameRate);
        Assert.False(older.Recording.IncludeCursor);
        Assert.False(older.Recording.RecordSystemAudio);
        Assert.False(older.Recording.RecordMicrophone);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SourcePreferencesSurviveCloneDraftAndJson(bool system, bool microphone)
    {
        var source = new AppSettings { Recording = { RecordSystemAudio = system, RecordMicrophone = microphone } };
        AppSettings clone = source.DeepClone();
        var draft = new SettingsDraft(clone);
        Assert.Equal(system, draft.RecordSystemAudio);
        Assert.Equal(microphone, draft.RecordMicrophone);
        Assert.False(draft.HasErrors);
        string json = JsonSerializer.Serialize(draft.ToAppSettings(), JsonDefaults.Readable);
        using JsonDocument document = JsonDocument.Parse(json);
        var recording = document.RootElement.GetProperty("recording");
        Assert.Equal(system, recording.GetProperty("recordSystemAudio").GetBoolean());
        Assert.Equal(microphone, recording.GetProperty("recordMicrophone").GetBoolean());
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;
        Assert.Equal(system, loaded.Recording.RecordSystemAudio);
        Assert.Equal(microphone, loaded.Recording.RecordMicrophone);
        clone.Recording.RecordSystemAudio = !system;
        clone.Recording.RecordMicrophone = !microphone;
        Assert.Equal(system, source.Recording.RecordSystemAudio);
        Assert.Equal(microphone, source.Recording.RecordMicrophone);
    }

    [Fact]
    public void DraftTracksEachPreferenceWithoutChangingLiveSettings()
    {
        var live = new AppSettings();
        var draft = new SettingsDraft(live);
        var changes = new List<string?>();
        draft.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert.False(draft.HasUnappliedChanges);
        draft.RecordSystemAudio = true;
        Assert.True(draft.HasUnappliedChanges);
        Assert.Contains(nameof(SettingsDraft.RecordSystemAudio), changes);
        Assert.False(live.Recording.RecordSystemAudio);
        draft.RecordSystemAudio = false;
        Assert.False(draft.HasUnappliedChanges);
        draft.RecordMicrophone = true;
        Assert.True(draft.HasUnappliedChanges);
        Assert.Contains(nameof(SettingsDraft.RecordMicrophone), changes);
        Assert.False(live.Recording.RecordMicrophone);
        AppSettings saved = draft.ToAppSettings();
        draft.AcceptSaved(saved);
        Assert.False(draft.HasUnappliedChanges);
        draft.RecordSystemAudio = true;
        draft.LoadFrom(saved);
        Assert.False(draft.RecordSystemAudio);
        Assert.True(draft.RecordMicrophone);
        Assert.False(draft.HasUnappliedChanges);
    }

    [Fact]
    public void ResetTurnsBothSourcesOffAndCountsAsUnapplied()
    {
        var source = new AppSettings { Recording = { RecordSystemAudio = true, RecordMicrophone = true } };
        var draft = new SettingsDraft(source);
        draft.ResetToDefaults();
        Assert.False(draft.RecordSystemAudio);
        Assert.False(draft.RecordMicrophone);
        Assert.True(draft.HasUnappliedChanges);
        Assert.True(source.Recording.RecordSystemAudio);
        Assert.True(source.Recording.RecordMicrophone);
        Assert.False(draft.ToAppSettings().Recording.RecordSystemAudio);
        Assert.False(draft.ToAppSettings().Recording.RecordMicrophone);
    }
}
