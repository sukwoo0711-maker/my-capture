using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsInstantSaveTests
{
    [Fact]
    public Task PinSessionCheckboxIsAccessibleAndSavesImmediately() =>
        Isolated(nameof(PinSessionCheckboxIsAccessibleAndSavesImmediately), () =>
        {
            Application app = CreateApplication();
            try
            {
                foreach (string languageName in new[] { "ko-KR", "en" })
                {
                    using var language = UiText.UseLanguage(languageName);
                    string root = TestRecycleBin.CreateTempSubdirectory("mc-pin-session-settings-").FullName;
                    try
                    {
                        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
                        AppSettings saved = new();
                        store.Save(saved);
                        int saves = 0;
                        var window = new SettingsWindow(() => saved, candidate =>
                        {
                            store.Save(candidate);
                            saved = candidate;
                            saves++;
                            return new SettingsApplyResult(true, true, true, false, []);
                        }, NullLogger.Instance);
                        try
                        {
                            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab =>
                                Equals(tab.Header, UiText.Get("SettingsUx_TabEditing")));
                            Render(window, $"pin-session-settings-{languageName}.png", 940, 700);
                            SettingsDraft draft = Assert.IsType<SettingsDraft>(window.DataContext);
                            CheckBox restore = Descendants(window).OfType<CheckBox>().Single(box =>
                                box.GetBindingExpression(CheckBox.IsCheckedProperty)?.ParentBinding.Path.Path
                                    == nameof(SettingsDraft.RestoreOpenPinsOnLaunch));
                            Assert.Equal(UiText.Get("PinSession.RestoreLabel"), restore.Content);
                            Assert.Equal(UiText.Get("PinSession.RestoreLabel"), AutomationProperties.GetName(restore));
                            Assert.Equal(UiText.Get("PinSession.RestoreDescription"), AutomationProperties.GetHelpText(restore));
                            Assert.Same(window.FindResource("Settings.CheckBox"), restore.Style);
                            var group = Assert.IsType<StackPanel>(LogicalTreeHelper.GetParent(restore));
                            int index = group.Children.IndexOf(restore);
                            CheckBox previous = Assert.IsType<CheckBox>(group.Children[index - 1]);
                            Assert.Equal(nameof(SettingsDraft.CloseOnDoubleClick), previous
                                .GetBindingExpression(CheckBox.IsCheckedProperty)!.ParentBinding.Path.Path);
                            TextBlock description = Assert.IsType<TextBlock>(group.Children[index + 1]);
                            Assert.Same(window.FindResource("Settings.Description"), description.Style);
                            Assert.Equal(UiText.Get("PinSession.RestoreDescription"), KoreanWordWrap.Strip(description.Text));
                            Assert.False(restore.IsChecked);

                            restore.IsChecked = true;
                            Pump();
                            Assert.True(draft.RestoreOpenPinsOnLaunch);
                            Assert.True(saved.Pin.RestoreOpenPinsOnLaunch);
                            Assert.True(store.Load().Pin.RestoreOpenPinsOnLaunch);
                            Assert.Equal(1, saves);

                            restore.IsChecked = false;
                            Pump();
                            Assert.False(saved.Pin.RestoreOpenPinsOnLaunch);
                            Assert.False(store.Load().Pin.RestoreOpenPinsOnLaunch);
                            Assert.Equal(2, saves);

                            RenderPinSessionEvidence(languageName, window.Resources);
                        }
                        finally { window.CloseForExit(); }
                    }
                    finally { TestRecycleBin.DeleteDirectory(root, recursive: true); }
                }
            }
            finally { app.Shutdown(); }
        });

    [Fact]
    public Task PinShortcutRowsRestoreTheirNewInstallChordsFromDefault() =>
        Isolated(nameof(PinShortcutRowsRestoreTheirNewInstallChordsFromDefault), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            Application app = CreateApplication();
            string root = TestRecycleBin.CreateTempSubdirectory("mc-pin-default-keys-").FullName;
            try
            {
                var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
                AppSettings defaults = NewInstallDefaults.Create();
                AppSettings saved = NewInstallDefaults.Create();
                saved.Hotkeys.PasteToScreen = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkF3);
                saved.Hotkeys.HideAllPins = Hotkey.None;
                store.Save(saved);
                var window = new SettingsWindow(() => saved, candidate =>
                {
                    store.Save(candidate);
                    saved = candidate;
                    return new SettingsApplyResult(true, true, true, false, []);
                }, NullLogger.Instance);
                try
                {
                    var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab =>
                        Equals(tab.Header, UiText.Get("SettingsUx_TabShortcuts")));
                    Render(window, "pin-default-keys-ko-KR.png", 940, 700);
                    SettingsDraft draft = Assert.IsType<SettingsDraft>(window.DataContext);
                    foreach ((string property, Hotkey expected) in new[]
                    {
                        (nameof(SettingsDraft.PasteToScreenHotkey), defaults.Hotkeys.PasteToScreen),
                        (nameof(SettingsDraft.HideAllPinsHotkey), defaults.Hotkeys.HideAllPins),
                    })
                    {
                        // The row's Default button restores the chord a new installation starts with.
                        HotkeyRecorder recorder = Descendants(window).OfType<HotkeyRecorder>().Single(item =>
                            item.GetBindingExpression(HotkeyRecorder.TextProperty)?.ParentBinding.Path.Path == property);
                        Assert.Equal(expected.ToString(), recorder.DefaultText);
                        Assert.NotEqual(expected.ToString(), recorder.Text);
                        Button reset = Descendants(recorder).OfType<Button>().Single(button =>
                            Equals(button.Content, UiText.Get("Hotkey.Reset")));
                        Assert.Equal(Visibility.Visible, reset.Visibility);
                        reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Pump();
                        Assert.Equal(expected.ToString(), recorder.Text);
                    }

                    Assert.Equal("F3", draft.PasteToScreenHotkey);
                    Assert.Equal("Shift+F3", draft.HideAllPinsHotkey);
                    // Shortcut rows are text settings, saved after the 400 ms typing pause.
                    Pump(450);
                    AppSettings reloaded = store.Load();
                    Assert.Equal(defaults.Hotkeys.PasteToScreen, reloaded.Hotkeys.PasteToScreen);
                    Assert.Equal(defaults.Hotkeys.HideAllPins, reloaded.Hotkeys.HideAllPins);
                }
                finally { window.CloseForExit(); }
            }
            finally
            {
                app.Shutdown();
                TestRecycleBin.DeleteDirectory(root, recursive: true);
            }
        });

    private static void RenderPinSessionEvidence(string languageName, ResourceDictionary resources)
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_L7_EVIDENCE") is not { Length: > 0 }) return;
        using Stream current = typeof(SettingsInstantSaveTests).Assembly.GetManifestResourceStream("SettingsFixture.Window.xaml")!;
        RenderPinSection(XDocument.Load(current), $"pin-session-after-{languageName}.png", resources);
        if (Environment.GetEnvironmentVariable("MYCAPTURE_PIN_SESSION_BASELINE") is { Length: > 0 } baseline)
            RenderPinSection(XDocument.Load(baseline), $"pin-session-before-{languageName}.png", resources);
    }

    private static void RenderPinSection(XDocument source, string name, ResourceDictionary resources)
    {
        // Render the exact pin-group markup from the requested revision, without a live window.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement original = source.Root!;
        XElement heading = original.Descendants(presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{loc:Text Text_33EFD44366A7}");
        var panelMarkup = new XElement(presentation + "StackPanel",
            new XAttribute("Margin", "24"), new XAttribute("Grid.IsSharedSizeScope", "True"));
        foreach (XAttribute declaration in original.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            string value = declaration.Value;
            if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                && !value.Contains(";assembly=", StringComparison.Ordinal)) value += ";assembly=MyCapture";
            panelMarkup.SetAttributeValue(declaration.Name, value);
        }
        panelMarkup.Add(new XElement(heading));
        foreach (XElement element in heading.ElementsAfterSelf()
                     .TakeWhile(element => (string?)element.Attribute("Text") != "{loc:Text SettingsUx_OcrHeading}"))
            panelMarkup.Add(new XElement(element));
        foreach (XElement element in panelMarkup.DescendantsAndSelf())
        {
            string ns = element.Name.NamespaceName;
            if (ns.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                && !ns.Contains(";assembly=", StringComparison.Ordinal))
                element.Name = XName.Get(element.Name.LocalName, ns + ";assembly=MyCapture");
        }
        // Reuse the actual compiled settings styles: parsing their templates as loose XAML
        // loses deferred named-element references. The pin controls themselves remain exact.
        var styles = new ResourceDictionary();
        foreach (object key in resources.Keys) styles.Add(key, resources[key]);
        StackPanel panel;
        Application.Current.Resources.MergedDictionaries.Add(styles);
        try { panel = (StackPanel)XamlReader.Parse(panelMarkup.ToString(SaveOptions.DisableFormatting)); }
        finally { Application.Current.Resources.MergedDictionaries.Remove(styles); }
        panel.Resources = styles;
        var preview = new Window { Content = panel, DataContext = new SettingsDraft(new AppSettings()) };
        preview.SetResourceReference(Window.BackgroundProperty, "Surface.Base");
        preview.SetResourceReference(Window.ForegroundProperty, "Text.Primary");
        preview.SetResourceReference(Window.FontFamilyProperty, "Font.Ui");
        try { Render(preview, name, 740, 530); }
        finally { preview.Close(); }
    }
}
