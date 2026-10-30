using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsGuideTrayEvidenceTests
{
    [Fact]
    public void RenderMainTrayStates() => StaTestHost.Run(() =>
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        root = DiagnosticOutputPaths.Create(root);
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_PHASE") ?? "after";
        using var language = UiText.UseLanguage("ko-KR");
        using var presenter = new ThemedShellPresenter(() => false);
        Action noOp = () => { };
        var commands = new TrayMenuCommands(noOp, noOp, noOp, noOp, noOp, noOp, noOp, noOp,
            noOp, noOp, noOp, noOp, noOp, PinClipboard: noOp, HideOrShowAllPins: noOp,
            ClearClickThrough: noOp, ContactSupport: noOp, InstallUpdate: noOp, FirstUseGuide: noOp);
        foreach ((string name, TrayMenuState state) in new[]
        {
            ("empty", new TrayMenuState(ClipboardHasContent: false)),
            ("pins", new TrayMenuState(CanRestoreClosedPin: true, PinCount: 2, ClickThroughPinCount: 1, UpdateAvailable: true, UpdateVersion: "4.11.0")),
            ("busy", new TrayMenuState(CanRestoreClosedPin: true, ScrollingActive: true, PinCount: 2, PinsHidden: true, UpdateAvailable: true, UpdateBusy: true)),
        })
        {
            ContextMenu menu = presenter.CreateMenu(TrayMenuLayout.Build(commands, NewInstallDefaults.Create().Hotkeys, state));
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Daylight, resources, systemHighContrast: false);
            menu.Resources = resources;
            menu.Measure(new Size(440, double.PositiveInfinity));
            menu.Arrange(new Rect(new Point(), menu.DesiredSize));
            menu.UpdateLayout();
            Assert.True(menu.ActualWidth > 100 && menu.ActualHeight > 100);
            Save(menu, root, $"tray-{name}-ko-{phase}.png");
            foreach (MenuItem branch in menu.Items.OfType<MenuItem>().Where(x => x.HasItems))
            {
                var submenu = presenter.CreateMenu(TrayMenuLayout.Build(commands, new HotkeySettings(), state)
                    .Single(x => x.Label == (string)branch.Header).Children!);
                submenu.Resources = resources;
                submenu.Measure(new Size(440, double.PositiveInfinity));
                submenu.Arrange(new Rect(new Point(), submenu.DesiredSize));
                submenu.UpdateLayout();
                Save(submenu, root, $"tray-{name}-branch{menu.Items.IndexOf(branch)}-ko-{phase}.png");
            }
        }
    });

    internal static void Save(FrameworkElement element, string root, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(root, name));
        encoder.Save(stream);
    }
}
