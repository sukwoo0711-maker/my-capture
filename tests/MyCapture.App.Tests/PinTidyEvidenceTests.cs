using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTidyEvidenceTests
{
    [Fact]
    public void RenderMenusWhenRequested() => StaTestHost.Run(() =>
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_TIDY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        string root = DiagnosticOutputPaths.Create(output);
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_TIDY_PHASE") == "before" ? "before" : "after";
        using var presenter = new ThemedShellPresenter(() => false);
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            using var culture = UiText.UseLanguage(language);
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.Daylight })
            {
                ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
                ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
                PinWindow[] pins = Fixtures(language);
                try
                {
                    pins[3].State.IsHidden = true;
                    Action noop = () => { };
                    var commands = new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop,
                        noop, noop, noop, noop, noop, HideOrShowAllPins: noop, ActivateOpenPin: _ => { });
                    var entries = TrayMenuLayout.Build(commands, new HotkeySettings(),
                        new TrayMenuState(PinCount: 5, PinsHidden: true, OpenPins: pins));
                    var branch = entries.Single(entry => entry.Label == UiText.Get("Tray_OpenPins"));
                    var rows = branch.Children!.Concat([ThemedShellPresenter.MenuEntry.Separator,
                        entries.Single(entry => entry.Label == UiText.Get("Tray_ShowAllPins"))]);
                    ContextMenu submenu = presenter.CreateMenu(rows, resources);
                    LayoutAndSave(submenu, root, $"tray-{language}-{theme}-{phase}.png");
                    if (theme == AppTheme.Glass)
                    {
                        pins[0].CanShowOnlyPin = () => pins.Skip(1).Any(pin => !pin.State.IsHidden);
                        pins[0].RefreshShowOnlyMenuItem();
                        ContextMenu source = pins[0].ContextMenu;
                        int start = source.Items.OfType<MenuItem>().Select(item => (Item: item, Index: source.Items.IndexOf(item)))
                            .Single(pair => (string)pair.Item.Header == UiText.Get("Onboard_PinAllowClickThrough")).Index;
                        object[] tail = source.Items.Cast<object>().Skip(start).ToArray();
                        var menu = new ContextMenu { Resources = resources, MinWidth = 300 };
                        foreach (object item in tail) { source.Items.Remove(item); menu.Items.Add(item); }
                        LayoutAndSave(menu, root, $"pin-menu-{language}-{phase}.png");
                    }
                }
                finally { foreach (PinWindow pin in pins) pin.Close(); }
            }
        }
    });

    private static PinWindow[] Fixtures(string language)
    {
        bool ko = language == "ko-KR";
        string table = ko ? "구간\t월 요금\t연 요금\t초과 요금\nStarter\t29,000\t290,000\t—" : "Plan\tMonthly\tAnnual\tOverage\nStarter\t29\t290\t—";
        const string code = "export async function verifyToken(token) {\n  return await jwt.verify(token);\n}";
        var document = ClipboardCodeRenderer.TryParse(code, "<pre>" + code + "</pre>");
        return
        [
            Window(PinContent.FromText(Preview(table, false), table, true)),
            Window(PinContent.FromSession(Preview(code, true), PinContentKind.Text, code, "", true, PinTextStyle.Code, document)),
            Window(PinContent.FromImage(Preview(ko ? "릴리스 노트\n새로운 기능" : "Release notes\nNew features", false),
                provenance: CaptureProvenance.Create(windowTitle: ko ? "릴리스 노트 - Chrome" : "Release notes - Chrome"))),
            Window(PinContent.FromImage(Preview("", false, 1280, 720))),
            Window(PinContent.FromText(Preview(ko ? "배포 순서\n태그 확인\n패키지 만들기" : "Release steps\nCheck tag\nBuild package", false),
                ko ? "배포 순서\n태그 확인\n패키지 만들기" : "Release steps\nCheck tag\nBuild package", false)),
        ];
    }

    private static PinWindow Window(PinContent content) =>
        new(content, new PinViewState(320, 180, 1, 1, .1), -10000, -10000) { ShowActivated = false };

    private static BitmapSource Preview(string text, bool code, int width = 320, int height = 180)
    {
        var label = new TextBlock { Text = text, FontSize = 18, Padding = new Thickness(12),
            Foreground = code ? Brushes.White : Brushes.Black, Background = code ? Brushes.DarkSlateGray : Brushes.WhiteSmoke };
        label.Measure(new Size(width, height));
        label.Arrange(new Rect(0, 0, width, height));
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(label);
        image.Freeze();
        return image;
    }

    internal static void LayoutAndSave(FrameworkElement element, string root, string name)
    {
        element.Measure(new Size(680, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(), element.DesiredSize));
        element.UpdateLayout();
        Assert.True(element.ActualWidth > 100 && element.ActualHeight > 100);
        SettingsGuideTrayEvidenceTests.Save(element, root, name);
    }
}
