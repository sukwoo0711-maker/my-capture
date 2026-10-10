using System.Reflection;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The settings window loads its draft from <see cref="AppSettingsClone.DeepClone"/>, so a
/// recording setting the clone forgets is shown at its default and written back on Apply.
/// </summary>
public sealed class RecordingSettingsCloneTests
{
    [Fact]
    public void DeepClone_CopiesEveryWritableRecordingSetting()
    {
        var source = new AppSettings
        {
            Recording =
            {
                FrameRate = RecordingFrameRate.Fps60,
                UseStartDelay = true,
                StartDelaySeconds = 7,
                IncludeCursor = false,
                CursorHighlight = false,
                BitrateBitsPerSecond = 12_000_000,
                CoarseStepSeconds = 2.5,
                PresetWidth = 1280,
                PresetHeight = 720,
                CaptureMode = RecordingCaptureMode.Gdi,
            },
        };
        var defaults = new RecordingSettings();
        PropertyInfo[] writable = typeof(RecordingSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .ToArray();

        RecordingSettings clone = source.DeepClone().Recording;

        Assert.NotSame(source.Recording, clone);
        foreach (PropertyInfo property in writable)
        {
            // Every value above differs from its default, so a forgotten member cannot pass.
            Assert.NotEqual(property.GetValue(defaults), property.GetValue(source.Recording));
            Assert.Equal(property.GetValue(source.Recording), property.GetValue(clone));
        }
    }

    [Fact]
    public void Draft_KeepsCursorHighlightOffAndSizePresetAcrossReopen()
    {
        var live = new AppSettings
        {
            Recording = { CursorHighlight = false, PresetWidth = 1280, PresetHeight = 720 },
        };

        var draft = new SettingsDraft(live);
        Assert.False(draft.RecordingCursorHighlight);
        Assert.Equal("1280", draft.RecordingPresetWidth);
        Assert.Equal("720", draft.RecordingPresetHeight);

        RecordingSettings applied = draft.ToAppSettings().Recording;
        Assert.False(applied.CursorHighlight);
        Assert.Equal(1280, applied.PresetWidth);
        Assert.Equal(720, applied.PresetHeight);
    }
}
