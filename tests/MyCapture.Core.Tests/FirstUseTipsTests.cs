using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class FirstUseTipsTests
{
    [Fact]
    public void NewInstancesAndNewInstallDefaults_StartWithQuickAccessAndUnseenTips()
    {
        foreach (GeneralSettings settings in new[]
                 {
                     new GeneralSettings(), new AppSettings().General, NewInstallDefaults.Create().General,
                 })
        {
            Assert.Equal(AfterCaptureAction.QuickAccess, settings.AfterCapture);
            Assert.False(settings.FirstCaptureTipShown);
            Assert.False(settings.FirstPinTipShown);
        }
    }

    [Fact]
    public void MissingSettingsFile_StartsWithQuickAccessAndPersistsItAfterWelcome()
    {
        AppPaths paths = CreateTestPaths();
        var store = Store(paths);

        AppSettings fresh = store.Load();

        Assert.True(fresh.General.IsFirstRun);
        Assert.Equal(AfterCaptureAction.QuickAccess, fresh.General.AfterCapture);
        Assert.False(fresh.General.FirstCaptureTipShown);
        Assert.False(fresh.General.FirstPinTipShown);
        Assert.False(File.Exists(paths.SettingsFile));

        store.CompleteFirstRun(fresh);
        AppSettings reloaded = store.Load();

        Assert.False(reloaded.General.IsFirstRun);
        Assert.Equal(AfterCaptureAction.QuickAccess, reloaded.General.AfterCapture);
        Assert.False(reloaded.General.FirstCaptureTipShown);
        Assert.False(reloaded.General.FirstPinTipShown);
    }

    [Theory]
    [InlineData("editor", AfterCaptureAction.Editor)]
    [InlineData("quickAccess", AfterCaptureAction.QuickAccess)]
    [InlineData("clipboardOnly", AfterCaptureAction.ClipboardOnly)]
    public void ExistingExplicitAfterCaptureChoice_SurvivesLoadAndImportWithoutRewriting(
        string persistedChoice, AfterCaptureAction expected)
    {
        AppPaths paths = CreateTestPaths();
        paths.EnsureCreated();
        string json = $$$"""
            {"schemaVersion":4,
             "hotkeys":{"hideAllPins":"Ctrl+Alt+F3"},
             "general":{"afterCapture":"{{{persistedChoice}}}","launchAtLogin":false,
                        "showShortcutHints":false,"shortcutHintsRevision":1}}
            """;
        File.WriteAllText(paths.SettingsFile, json);
        var store = Store(paths);

        foreach (AppSettings settings in new[] { store.Load(), store.ImportFrom(paths.SettingsFile) })
        {
            Assert.Equal(expected, settings.General.AfterCapture);
            Assert.False(settings.General.FirstCaptureTipShown);
            Assert.False(settings.General.FirstPinTipShown);
            Assert.False(settings.General.LaunchAtLogin);
            Assert.False(settings.General.ShowShortcutHints);
            Assert.Equal("Ctrl+Alt+F3", settings.Hotkeys.HideAllPins.ToString());
        }
        Assert.Equal(json, File.ReadAllText(paths.SettingsFile));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"general":{}}""")]
    [InlineData("""{"general":{"showQuickAccess":false}}""")]
    [InlineData("""{"general":{"showQuickAccess":true}}""")]
    [InlineData("""{"General":{"ShowQuickAccess":true}}""")]
    public void ExistingJsonWithoutAfterCapture_KeepsEditorAndUnseenTips(string json)
    {
        AppPaths paths = CreateTestPaths();
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, json);
        var store = Store(paths);
        AppSettings deserialized = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;

        foreach (AppSettings settings in new[] { deserialized, store.Load(), store.ImportFrom(paths.SettingsFile) })
        {
            Assert.Equal(AfterCaptureAction.Editor, settings.General.AfterCapture);
            Assert.False(settings.General.FirstCaptureTipShown);
            Assert.False(settings.General.FirstPinTipShown);
        }
        Assert.Equal(json, File.ReadAllText(paths.SettingsFile));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TipFlags_SurviveJsonCloneAndDraft(bool captureShown, bool pinShown)
    {
        var source = new AppSettings
        {
            General =
            {
                AfterCapture = AfterCaptureAction.ClipboardOnly,
                FirstCaptureTipShown = captureShown,
                FirstPinTipShown = pinShown,
            },
        };
        AppSettings fromJson = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source, JsonDefaults.Readable), JsonDefaults.Readable)!;
        AppSettings clone = fromJson.DeepClone();
        var draft = new SettingsDraft(clone) { NotifyOnQuickSave = false };
        Assert.False(draft.HasErrors);
        AppSettings applied = draft.ToAppSettings();

        foreach (AppSettings settings in new[] { source, fromJson, clone, applied })
        {
            Assert.Equal(captureShown, settings.General.FirstCaptureTipShown);
            Assert.Equal(pinShown, settings.General.FirstPinTipShown);
            Assert.Equal(AfterCaptureAction.ClipboardOnly, settings.General.AfterCapture);
        }
        Assert.False(applied.General.NotifyOnQuickSave);
        Assert.NotSame(source.General, fromJson.General);
        Assert.NotSame(fromJson.General, clone.General);
        clone.General.FirstCaptureTipShown = !captureShown;
        clone.General.FirstPinTipShown = !pinShown;
        Assert.Equal(captureShown, fromJson.General.FirstCaptureTipShown);
        Assert.Equal(pinShown, fromJson.General.FirstPinTipShown);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TipFlags_SurviveSettingsStoreSaveLoadAndControlLaterPresentation(bool captureShown, bool pinShown)
    {
        AppPaths paths = CreateTestPaths();
        var store = Store(paths);
        var source = new AppSettings
        {
            General = { FirstCaptureTipShown = captureShown, FirstPinTipShown = pinShown },
        };
        store.Save(source);

        AppSettings loaded = store.Load();

        Assert.Equal(captureShown, loaded.General.FirstCaptureTipShown);
        Assert.Equal(pinShown, loaded.General.FirstPinTipShown);
        int capturePresentations = 0, pinPresentations = 0;
        Assert.Equal(!captureShown, FirstUseTips.ShowCaptureOnce(loaded.General, () => capturePresentations++));
        Assert.Equal(!pinShown, FirstUseTips.ShowPinOnce(loaded.General, () => pinPresentations++));
        Assert.Equal(captureShown ? 0 : 1, capturePresentations);
        Assert.Equal(pinShown ? 0 : 1, pinPresentations);

        store.Save(loaded);
        AppSettings completed = store.Load();
        Assert.False(FirstUseTips.ShowCaptureOnce(completed.General, () => capturePresentations++));
        Assert.False(FirstUseTips.ShowPinOnce(completed.General, () => pinPresentations++));
        Assert.Equal(captureShown ? 0 : 1, capturePresentations);
        Assert.Equal(pinShown ? 0 : 1, pinPresentations);
    }

    [Fact]
    public void EachTip_IsPresentedAtMostOnceAndKeepsTheOtherTipAvailable()
    {
        var settings = new GeneralSettings();
        int capturePresentations = 0, pinPresentations = 0;

        Assert.True(FirstUseTips.ShowCaptureOnce(settings, () =>
        {
            Assert.False(settings.FirstCaptureTipShown);
            capturePresentations++;
        }));
        Assert.True(settings.FirstCaptureTipShown);
        Assert.False(settings.FirstPinTipShown);
        Assert.False(FirstUseTips.ShowCaptureOnce(settings, () => capturePresentations++));

        Assert.True(FirstUseTips.ShowPinOnce(settings, () =>
        {
            Assert.False(settings.FirstPinTipShown);
            pinPresentations++;
        }));
        Assert.True(settings.FirstCaptureTipShown);
        Assert.True(settings.FirstPinTipShown);
        Assert.False(FirstUseTips.ShowPinOnce(settings, () => pinPresentations++));
        Assert.Equal(1, capturePresentations);
        Assert.Equal(1, pinPresentations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedPresentation_LeavesTipAvailableForSuccessfulRetry(bool captureTip)
    {
        var settings = new GeneralSettings();
        Func<GeneralSettings, Action, bool> showOnce = captureTip
            ? FirstUseTips.ShowCaptureOnce : FirstUseTips.ShowPinOnce;
        var failure = new InvalidOperationException("Synthetic presentation failure");

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => showOnce(settings, () => throw failure)));
        Assert.False(settings.FirstCaptureTipShown);
        Assert.False(settings.FirstPinTipShown);

        int successfulPresentations = 0;
        Assert.True(showOnce(settings, () => successfulPresentations++));
        Assert.False(showOnce(settings, () => successfulPresentations++));
        Assert.Equal(1, successfulPresentations);
        Assert.Equal(captureTip, settings.FirstCaptureTipShown);
        Assert.Equal(!captureTip, settings.FirstPinTipShown);
    }

    private static SettingsStore Store(AppPaths paths) => new(paths, NullLogger<SettingsStore>.Instance);

    // Keep these isolated fixtures: the shared cleanup helper permanently deletes files.
    private static AppPaths CreateTestPaths() => AppPaths.CreateForRoot(
        TestRecycleBin.CreateTempSubdirectory("mycapture-first-use-").FullName);
}
