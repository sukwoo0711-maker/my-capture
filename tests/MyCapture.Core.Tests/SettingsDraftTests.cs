using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class AppSettingsCloneTests
{
    [Fact]
    public void DeepClone_ProducesIndependentCopy()
    {
        var source = new AppSettings();
        source.Queue.MaxItems = 123;
        source.Ocr.PreferredLanguages = ["ko-KR", "en-US"];
        source.Annotation.RecentColors.Add(Primitives.ColorRgba.FromRgb(1, 2, 3));
        source.Recording.FrameRate = RecordingFrameRate.Fps60;

        AppSettings clone = source.DeepClone();

        // Mutating the clone must not touch the source: this is what makes Cancel safe.
        clone.Queue.MaxItems = 999;
        clone.Ocr.PreferredLanguages.Add("ja-JP");
        clone.Annotation.RecentColors.Clear();
        clone.Recording.FrameRate = RecordingFrameRate.Fps10;

        Assert.Equal(123, source.Queue.MaxItems);
        Assert.Equal(["ko-KR", "en-US"], source.Ocr.PreferredLanguages);
        Assert.Single(source.Annotation.RecentColors);
        Assert.Equal(RecordingFrameRate.Fps60, source.Recording.FrameRate);
    }

    [Fact]
    public void DeepClone_CopiesEveryGroupByValue()
    {
        var source = new AppSettings();
        source.General.LaunchAtLogin = true;
        source.Capture.DelaySeconds = 7;
        source.Export.FileNamePattern = "shot_{yyyy}";
        source.Pin.ZoomStep = 0.25;
        source.Hotkeys.Capture = new Hotkey(HotkeyModifiers.Alt, Hotkey.VkF1);
        source.Hotkeys.RecordRegion = new Hotkey(HotkeyModifiers.Alt, Hotkey.VkF1 + 1);
        source.Recording.FrameRate = RecordingFrameRate.Fps24;
        source.Recording.UseStartDelay = true;
        source.Recording.StartDelaySeconds = 6;
        source.Recording.IncludeCursor = false;
        source.Recording.BitrateBitsPerSecond = 8_000_000;
        source.Recording.CoarseStepSeconds = 2.5;

        AppSettings clone = source.DeepClone();

        Assert.True(clone.General.LaunchAtLogin);
        Assert.Equal(7, clone.Capture.DelaySeconds);
        Assert.Equal("shot_{yyyy}", clone.Export.FileNamePattern);
        Assert.Equal(0.25, clone.Pin.ZoomStep);
        Assert.Equal("Alt+F1", clone.Hotkeys.Capture.ToString());
        Assert.Equal("Alt+F2", clone.Hotkeys.RecordRegion.ToString());
        Assert.Equal(RecordingFrameRate.Fps24, clone.Recording.FrameRate);
        Assert.True(clone.Recording.UseStartDelay);
        Assert.Equal(6, clone.Recording.StartDelaySeconds);
        Assert.False(clone.Recording.IncludeCursor);
        Assert.Equal(8_000_000, clone.Recording.BitrateBitsPerSecond);
        Assert.Equal(2.5, clone.Recording.CoarseStepSeconds);
    }
}

public sealed class SettingsDraftTests
{
    private static SettingsDraft NewDraft() => new(new AppSettings());

    [Fact]
    public void DefaultDraft_IsValid()
    {
        SettingsDraft draft = NewDraft();
        Assert.False(draft.HasErrors);
        Assert.Empty(draft.AllErrors());
    }

    [Fact]
    public void EditingDraft_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings { Queue = { MaxItems = 300 } };
        var draft = new SettingsDraft(source);

        draft.MaxItems = "1500";
        AppSettings mapped = draft.ToAppSettings();

        Assert.Equal(300, source.Queue.MaxItems);   // source untouched
        Assert.Equal(1500, mapped.Queue.MaxItems);   // mapping reflects the edit
    }

    [Theory]
    [InlineData("5")]     // below floor of 10
    [InlineData("6000")]  // above ceiling of 5000
    [InlineData("abc")]   // not an integer
    [InlineData("")]      // empty
    public void MaxItems_RejectsOutOfRangeOrNonNumeric(string value)
    {
        SettingsDraft draft = NewDraft();
        draft.MaxItems = value;
        Assert.True(draft.HasErrors);
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.MaxItems)).Cast<string>());
    }

    [Fact]
    public void MaxItems_AcceptsInRange()
    {
        SettingsDraft draft = NewDraft();
        draft.MaxItems = "500";
        Assert.False(draft.HasErrors);
    }

    [Theory]
    [InlineData("0.1")]   // below floor 0.2
    [InlineData("1.5")]   // above ceiling 1.0
    [InlineData("NaN")]
    public void InitialOpacity_RejectsOutOfRange(string value)
    {
        SettingsDraft draft = NewDraft();
        draft.InitialOpacity = value;
        Assert.True(draft.HasErrors);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("20")]
    [InlineData("120")]
    [InlineData("abc")]
    public void RecordingFrameRate_RejectsUnsupportedValues(string value)
    {
        SettingsDraft draft = NewDraft();

        draft.RecordingFrameRate = value;

        Assert.True(draft.HasErrors);
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.RecordingFrameRate)).Cast<string>());
    }

    [Fact]
    public void RecordingSettings_RoundTripEditableAndAdvancedValues()
    {
        var source = new AppSettings();
        source.Recording.BitrateBitsPerSecond = 7_500_000;
        source.Recording.CoarseStepSeconds = 2.25;
        var draft = new SettingsDraft(source)
        {
            RecordingFrameRate = "60",
            UseRecordingStartDelay = true,
            RecordingStartDelaySeconds = "7",
            RecordingIncludeCursor = false,
            RecordRegionHotkey = "Alt+F8",
        };

        Assert.False(draft.HasErrors);
        AppSettings mapped = draft.ToAppSettings();

        Assert.Equal(RecordingFrameRate.Fps60, mapped.Recording.FrameRate);
        Assert.True(mapped.Recording.UseStartDelay);
        Assert.Equal(7, mapped.Recording.StartDelaySeconds);
        Assert.False(mapped.Recording.IncludeCursor);
        Assert.Equal(7_500_000, mapped.Recording.BitrateBitsPerSecond);
        Assert.Equal(2.25, mapped.Recording.CoarseStepSeconds);
        Assert.Equal("Alt+F8", mapped.Hotkeys.RecordRegion.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("nope")]
    public void RecordingStartDelay_RejectsInvalidValues(string value)
    {
        SettingsDraft draft = NewDraft();

        draft.RecordingStartDelaySeconds = value;

        Assert.True(draft.HasErrors);
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.RecordingStartDelaySeconds)).Cast<string>());
    }

    [Fact]
    public void MaxGiB_RoundTripsToBytesWithinRange()
    {
        SettingsDraft draft = NewDraft();
        draft.MaxGiB = "4";
        Assert.False(draft.HasErrors);

        AppSettings mapped = draft.ToAppSettings();
        Assert.Equal(4L * 1024 * 1024 * 1024, mapped.Queue.MaxBytes);
    }

    [Fact]
    public void MaxGiB_BelowFloorIsRejected()
    {
        SettingsDraft draft = NewDraft();
        draft.MaxGiB = "0.05"; // ~52 MiB, below the 128 MiB floor
        Assert.True(draft.HasErrors);
    }

    [Fact]
    public void FileNamePattern_EmptyIsRejected()
    {
        SettingsDraft draft = NewDraft();
        draft.FileNamePattern = "   ";
        Assert.True(draft.HasErrors);
    }

    [Fact]
    public void FileNamePattern_ValidPatternAccepted()
    {
        SettingsDraft draft = NewDraft();
        draft.FileNamePattern = "shot_{yyyyMMdd}_{HHmmss}";
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void PreferredLanguages_EmptyIsRejected()
    {
        SettingsDraft draft = NewDraft();
        draft.PreferredLanguages = "  ";
        Assert.True(draft.HasErrors);
    }

    [Fact]
    public void PreferredLanguages_InvalidTagRejected()
    {
        SettingsDraft draft = NewDraft();
        draft.PreferredLanguages = "ko-KR, 12345678901234";
        Assert.True(draft.HasErrors);
    }

    [Fact]
    public void PreferredLanguages_ValidCommaListAccepted()
    {
        SettingsDraft draft = NewDraft();
        draft.PreferredLanguages = "ko-KR, en-US, ja";
        Assert.False(draft.HasErrors);

        AppSettings mapped = draft.ToAppSettings();
        Assert.Equal(["ko-KR", "en-US", "ja"], mapped.Ocr.PreferredLanguages);
    }

    [Fact]
    public void CaptureHotkey_CannotBeEmpty()
    {
        SettingsDraft draft = NewDraft();
        draft.CaptureHotkey = "";
        Assert.True(draft.HasErrors);
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.CaptureHotkey)).Cast<string>());
    }

    [Fact]
    public void Hotkey_InvalidChordRejected()
    {
        SettingsDraft draft = NewDraft();
        draft.PasteToScreenHotkey = "Ctrl+Banana";
        Assert.True(draft.HasErrors);
    }

    [Fact]
    public void DuplicateHotkeys_AreRejectedCaseInsensitively()
    {
        SettingsDraft draft = NewDraft();
        // Capture defaults to Ctrl+Shift+C; assign the same chord to another command in a
        // different case/spacing to prove semantic equality catches it.
        draft.CaptureWindowHotkey = " ctrl + shift + c ";

        Assert.True(draft.HasErrors);
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.CaptureWindowHotkey)).Cast<string>());
    }

    [Fact]
    public void DistinctHotkeys_AreAccepted()
    {
        SettingsDraft draft = NewDraft();
        draft.CaptureWindowHotkey = "Ctrl+Shift+W";
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void EmptyOptionalHotkeys_DoNotCountAsDuplicates()
    {
        SettingsDraft draft = NewDraft();
        // Several optional chords default to empty; empties must never collide with each other.
        draft.RepeatLastRegionHotkey = "";
        draft.CaptureWindowHotkey = "";
        draft.CaptureFullScreenHotkey = "";
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void ResetToDefaults_RestoresDefaultValues()
    {
        SettingsDraft draft = NewDraft();
        draft.MaxItems = "1234";
        draft.CaptureHotkey = "Alt+F1";

        draft.ResetToDefaults();

        Assert.Equal("300", draft.MaxItems);
        Assert.Equal("Ctrl+Shift+C", draft.CaptureHotkey);
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void ToAppSettings_ThrowsWhenDraftHasErrors()
    {
        SettingsDraft draft = NewDraft();
        draft.MaxItems = "bad";
        Assert.Throws<InvalidOperationException>(() => draft.ToAppSettings());
    }

    [Fact]
    public void ToAppSettings_MapsEveryGroup()
    {
        SettingsDraft draft = NewDraft();
        draft.LaunchAtLogin = true;
        draft.NotifyOnQuickSave = false;
        draft.IncludeCursor = true;
        draft.DelaySeconds = "5";
        draft.MaxItems = "400";
        draft.ThumbnailLongEdge = "256";
        draft.CopyToClipboardOnQuickSave = false;
        draft.FontFamily = "Consolas";
        draft.HighlighterAlpha = "120";
        draft.CacheResults = false;

        Assert.False(draft.HasErrors);
        AppSettings s = draft.ToAppSettings();

        Assert.True(s.General.LaunchAtLogin);
        Assert.False(s.General.NotifyOnQuickSave);
        Assert.True(s.Capture.IncludeCursor);
        Assert.Equal(5, s.Capture.DelaySeconds);
        Assert.Equal(400, s.Queue.MaxItems);
        Assert.Equal(256, s.Queue.ThumbnailLongEdge);
        Assert.False(s.Export.CopyToClipboardOnQuickSave);
        Assert.Equal("Consolas", s.Annotation.FontFamily);
        Assert.Equal(120, s.Annotation.HighlighterAlpha);
        Assert.False(s.Ocr.CacheResults);
    }

    [Fact]
    public void ToAppSettings_PreservesNonUiMembers()
    {
        var source = new AppSettings();
        source.Annotation.StrokeColor = Primitives.ColorRgba.FromRgb(10, 20, 30);
        source.Annotation.RecentColors.Add(Primitives.ColorRgba.FromRgb(1, 2, 3));
        source.Capture.ColorFormat = ColorFormat.Rgb;
        source.General.Language = "ko-KR";

        var draft = new SettingsDraft(source);
        AppSettings mapped = draft.ToAppSettings();

        Assert.Equal(source.Annotation.StrokeColor, mapped.Annotation.StrokeColor);
        Assert.Single(mapped.Annotation.RecentColors);
        Assert.Equal(ColorFormat.Rgb, mapped.Capture.ColorFormat);
        Assert.Equal("ko-KR", mapped.General.Language);
    }

    [Fact]
    public void MappedValues_StayWithinSettingsStoreRanges()
    {
        // A draft that passes validation must map to values the SettingsStore will not
        // need to clamp, proving the two range definitions agree.
        SettingsDraft draft = NewDraft();
        draft.MaxItems = SettingsRanges.MaxItems.Min.ToString(System.Globalization.CultureInfo.InvariantCulture);
        draft.CtrlClickDebounceMs = SettingsRanges.CtrlClickDebounceMs.Min.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(draft.HasErrors);

        AppSettings mapped = draft.ToAppSettings();
        Assert.True(SettingsRanges.MaxItems.Contains(mapped.Queue.MaxItems));
        Assert.True(SettingsRanges.CtrlClickDebounceMs.Contains(mapped.Pin.CtrlClickDebounceMs));
    }
}

public sealed class StartupRegistrationServiceTests
{
    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnWrite { get; set; }

        public string? GetValue(string name) => _values.TryGetValue(name, out string? v) ? v : null;

        public void SetValue(string name, string value)
        {
            if (ThrowOnWrite)
            {
                throw new UnauthorizedAccessException("registry write blocked");
            }

            _values[name] = value;
        }

        public void DeleteValue(string name) => _values.Remove(name);
    }

    private const string Exe = @"C:\Program Files\MyCapture\MyCapture.exe";

    private static StartupRegistrationService Create(FakeRunKeyStore store, string? exe = null) =>
        new(store, exe ?? Exe);

    [Fact]
    public void ExpectedCommand_IsExactlyQuotedExecutablePath()
    {
        var store = new FakeRunKeyStore();
        StartupRegistrationService service = Create(store);
        Assert.Equal("\"" + Exe + "\"", service.ExpectedCommand);
    }

    [Fact]
    public void Enable_WritesQuotedCommandUnderMyCaptureValue()
    {
        var store = new FakeRunKeyStore();
        Create(store).Enable();
        Assert.Equal("\"" + Exe + "\"", store.GetValue(StartupRegistrationService.RunValueName));
    }

    [Fact]
    public void IsEnabled_TrueOnlyWhenValueMatchesThisExecutable()
    {
        var store = new FakeRunKeyStore();
        StartupRegistrationService service = Create(store);

        Assert.False(service.IsEnabled());
        service.Enable();
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void IsEnabled_FalseWhenValuePointsAtDifferentExecutable()
    {
        var store = new FakeRunKeyStore();
        store.SetValue(StartupRegistrationService.RunValueName, "\"C:\\Other\\Thing.exe\"");
        Assert.False(Create(store).IsEnabled());
    }

    [Fact]
    public void Disable_RemovesValue()
    {
        var store = new FakeRunKeyStore();
        StartupRegistrationService service = Create(store);
        service.Enable();
        service.Disable();
        Assert.Null(store.GetValue(StartupRegistrationService.RunValueName));
        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Apply_EnableThenVerifiesAndReportsSuccess()
    {
        var store = new FakeRunKeyStore();
        StartupApplyResult result = Create(store).Apply(desiredEnabled: true);
        Assert.True(result.Succeeded);
        Assert.True(result.DesiredEnabled);
    }

    [Fact]
    public void Apply_WhenWriteFails_ReportsFailureAndDoesNotLie()
    {
        var store = new FakeRunKeyStore { ThrowOnWrite = true };
        StartupRegistrationService service = Create(store);

        StartupApplyResult result = service.Apply(desiredEnabled: true);

        Assert.False(result.Succeeded);
        Assert.False(service.IsEnabled()); // system state is honest
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ReconcileOnStartup_RewritesStaleMovedPath()
    {
        var store = new FakeRunKeyStore();
        // A previous install location left a stale quoted command.
        store.SetValue(StartupRegistrationService.RunValueName, "\"C:\\Old\\MyCapture.exe\"");
        StartupRegistrationService service = Create(store);

        bool rewritten = service.ReconcileOnStartup(settingEnabled: true);

        Assert.True(rewritten);
        Assert.True(service.IsEnabled());
        Assert.Equal("\"" + Exe + "\"", store.GetValue(StartupRegistrationService.RunValueName));
    }

    [Fact]
    public void ReconcileOnStartup_RemovesStaleValueWhenSettingDisabled()
    {
        var store = new FakeRunKeyStore();
        store.SetValue(StartupRegistrationService.RunValueName, "\"C:\\Old\\MyCapture.exe\"");
        StartupRegistrationService service = Create(store);

        bool changed = service.ReconcileOnStartup(settingEnabled: false);

        Assert.True(changed);
        Assert.Null(store.GetValue(StartupRegistrationService.RunValueName));
    }

    [Fact]
    public void ReconcileOnStartup_NoChangeWhenAlreadyCorrect()
    {
        var store = new FakeRunKeyStore();
        StartupRegistrationService service = Create(store);
        service.Enable();

        bool changed = service.ReconcileOnStartup(settingEnabled: true);

        Assert.False(changed);
        Assert.True(service.IsEnabled());
    }
}
