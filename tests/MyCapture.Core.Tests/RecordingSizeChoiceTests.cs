using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The recording size is an explicit choice between the dragged size and a fixed size, stored
/// the way the recorder already reads it: a preset size, or no preset.
/// </summary>
public sealed class RecordingSizeChoiceTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(1280, 0)]
    [InlineData(0, 720)]
    [InlineData(-5, 720)]
    public void SettingsWithoutAFullPreset_LoadAsTheDraggedSize_AndSaveNoPreset(int? width, int? height)
    {
        var draft = new SettingsDraft(new AppSettings { Recording = { PresetWidth = width, PresetHeight = height } });

        Assert.False(draft.UseRecordingFixedSize);
        Assert.True(draft.UseRecordingDraggedSize);
        Assert.False(draft.HasErrors);
        // The disabled boxes offer a usable size instead of "0".
        Assert.Equal(width > 0 ? width.Value.ToString() : "1280", draft.RecordingPresetWidth);
        Assert.Equal(height > 0 ? height.Value.ToString() : "720", draft.RecordingPresetHeight);
        Assert.False(draft.HasUnappliedChanges);

        AppSettings saved = draft.ToAppSettings();
        Assert.Null(saved.Recording.PresetWidth);
        Assert.Null(saved.Recording.PresetHeight);
    }

    [Fact]
    public void ASavedPreset_LoadsAsTheFixedSize_AndRoundTrips()
    {
        var draft = new SettingsDraft(new AppSettings { Recording = { PresetWidth = 1920, PresetHeight = 1080 } });

        Assert.True(draft.UseRecordingFixedSize);
        Assert.False(draft.UseRecordingDraggedSize);
        Assert.Equal("1920", draft.RecordingPresetWidth);
        Assert.Equal("1080", draft.RecordingPresetHeight);
        Assert.False(draft.HasErrors);

        AppSettings saved = draft.ToAppSettings();
        Assert.Equal(1920, saved.Recording.PresetWidth);
        Assert.Equal(1080, saved.Recording.PresetHeight);
        Assert.True(new SettingsDraft(saved).UseRecordingFixedSize);
    }

    [Fact]
    public void ChoosingTheFixedSize_SavesTheBoxes_AndChoosingTheDraggedSizeSavesNoPreset()
    {
        var draft = new SettingsDraft(new AppSettings());
        var changed = new List<string?>();
        draft.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        draft.UseRecordingFixedSize = true;
        Assert.Contains(nameof(SettingsDraft.UseRecordingFixedSize), changed);
        Assert.Contains(nameof(SettingsDraft.UseRecordingDraggedSize), changed);
        Assert.True(draft.HasUnappliedChanges);
        draft.RecordingPresetWidth = "800";
        draft.RecordingPresetHeight = "600";
        AppSettings fixedSize = draft.ToAppSettings();
        Assert.Equal(800, fixedSize.Recording.PresetWidth);
        Assert.Equal(600, fixedSize.Recording.PresetHeight);

        // The second radio button writes the same state from the other side.
        draft.UseRecordingDraggedSize = true;
        Assert.False(draft.UseRecordingFixedSize);
        AppSettings dragged = draft.ToAppSettings();
        Assert.Null(dragged.Recording.PresetWidth);
        Assert.Null(dragged.Recording.PresetHeight);
    }

    [Fact]
    public void TheBoxesAreValidatedOnlyWhileTheFixedSizeIsChosen()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var draft = new SettingsDraft(new AppSettings());
        string range = SettingsRanges.RecordingPresetSize.Describe();

        // Ignored while the dragged size is chosen: the boxes are disabled then.
        draft.RecordingPresetWidth = "50";
        Assert.False(draft.HasErrors);

        draft.UseRecordingFixedSize = true;
        Assert.Equal([UiText.Format("Text_CF32FE576AE7", range)],
            draft.GetErrors(nameof(SettingsDraft.RecordingPresetWidth)).Cast<string>());
        Assert.Throws<InvalidOperationException>(() => draft.ToAppSettings());

        draft.RecordingPresetHeight = "abc";
        Assert.Equal([UiText.Format("Text_9A6259D8B7CF", range)],
            draft.GetErrors(nameof(SettingsDraft.RecordingPresetHeight)).Cast<string>());

        // Leaving the fixed size clears both errors so they never block Apply.
        draft.UseRecordingDraggedSize = true;
        Assert.False(draft.HasErrors);
        draft.UseRecordingFixedSize = true;
        Assert.True(draft.HasErrors);
        draft.RecordingPresetWidth = "1280";
        draft.RecordingPresetHeight = "720";
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void TheFixedSizeAcceptsCommonVideoSizes_AndKeepsTheOldMinimum()
    {
        // 1280×720 and 1920×1080 are the sizes the preset strings name; the old 96 floor stays.
        Assert.True(SettingsRanges.RecordingPresetSize.Contains(1920));
        Assert.True(SettingsRanges.RecordingPresetSize.Contains(3840));
        Assert.Equal(96, SettingsRanges.RecordingPresetSize.Min);

        var draft = new SettingsDraft(new AppSettings()) { UseRecordingFixedSize = true };
        draft.RecordingPresetWidth = "95";
        Assert.True(draft.HasErrors);
        draft.RecordingPresetWidth = "1920";
        draft.RecordingPresetHeight = "1080";
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void ResetToDefaults_ReturnsToTheDraggedSize()
    {
        var draft = new SettingsDraft(new AppSettings { Recording = { PresetWidth = 800, PresetHeight = 600 } });
        draft.ResetToDefaults();

        Assert.True(draft.UseRecordingDraggedSize);
        Assert.Null(draft.ToAppSettings().Recording.PresetWidth);
        Assert.True(draft.HasUnappliedChanges);
    }
}
