using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    // Each later phase adds an attributed callback in its own partial, with its own IDs and
    // cleanup. The runner discovers it and verifies its declared cases without a shared list edit.
    [NativeFeelSceneGroup(25)]
    [NativeFeelScene("foundation-settings-capture", "settings-window")]
    private static void NativeFeelFoundationControls(NativeFeelSceneContext context)
    {
        AppSettings saved = NewInstallDefaults.Create();
        saved.General.Theme = AppThemeNames.ToSetting(ThemeService.Current);
        var window = new SettingsWindow(() => saved,
            _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance,
            defaultCapturesRoot: () => context.Profile);
        try
        {
            context.Desktop.Place(window, 940, 700);
            window.Show();
            Assert.True(window.NavigateToSection(SettingsSection.Capture));
            context.Desktop.Still("foundation-settings-capture", "foundation", window,
                "Fixed Capture category for before/after control comparison. Unlike the existing densest-page scene, this destination does not depend on template element counts.");
        }
        finally { window.CloseForExit(); }
    }
}
