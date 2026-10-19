using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsInstantSaveTests
{
    [Fact]
    public Task ModeChangesSaveDisplayedDependentValuesAtomically() => Isolated(nameof(ModeChangesSaveDisplayedDependentValuesAtomically), () =>
    {
        Application app = CreateApplication();
        AppSettings saved = NewInstallDefaults.Create();
        saved.Recording.PresetWidth = 1920;
        saved.Recording.PresetHeight = 1080;
        saved.General.CaptureGuideColor = "#12ABEF";
        int saves = 0;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            saved = candidate;
            saves++;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance);
        try
        {
            var draft = (SettingsDraft)window.DataContext;
            draft.UseRecordingFixedSize = false;
            Pump();
            Assert.Null(saved.Recording.PresetWidth);
            Assert.Equal("1920", draft.RecordingPresetWidth);
            draft.UseRecordingFixedSize = true;
            Pump();
            Assert.Equal(1920, saved.Recording.PresetWidth);
            Assert.Equal(1080, saved.Recording.PresetHeight);
            draft.CaptureGuideColorMode = "theme";
            Pump();
            Assert.Equal(string.Empty, saved.General.CaptureGuideColor);
            Assert.Equal("#12ABEF", draft.CaptureGuideColorHex);
            draft.CaptureGuideColorMode = "custom";
            Pump();
            Assert.Equal("#12ABEF", saved.General.CaptureGuideColor);

            draft.UseRecordingFixedSize = false;
            Pump();
            draft.RecordingPresetWidth = "2048";
            draft.RecordingPresetHeight = "1152";
            int beforePendingSize = saves;
            draft.UseRecordingFixedSize = true;
            Pump();
            Assert.Equal(beforePendingSize + 1, saves);
            Assert.Equal(2048, saved.Recording.PresetWidth);
            Assert.Equal(1152, saved.Recording.PresetHeight);

            draft.UseRecordingFixedSize = false;
            Pump();
            draft.RecordingPresetWidth = string.Empty;
            draft.UseRecordingFixedSize = true;
            int beforeInvalidSize = saves;
            Pump();
            Assert.Equal(beforeInvalidSize, saves);
            Assert.Null(saved.Recording.PresetWidth);
            Assert.True(draft.HasErrors);
            draft.PlayCaptureSound = !draft.PlayCaptureSound;
            Pump();
            Assert.Equal(draft.PlayCaptureSound, saved.General.PlayCaptureSound);
            Assert.Null(saved.Recording.PresetWidth);
            Assert.True(draft.HasErrors);
            draft.RecordingPresetWidth = "2560";
            draft.RecordingPresetHeight = "1440";
            Pump(450);
            Assert.Equal(2560, saved.Recording.PresetWidth);
            Assert.Equal(1440, saved.Recording.PresetHeight);

            draft.CaptureGuideColorMode = "theme";
            Pump();
            draft.CaptureGuideColorHex = "#";
            draft.CaptureGuideColorMode = "custom";
            int beforeInvalidColor = saves;
            Pump();
            Assert.Equal(beforeInvalidColor, saves);
            Assert.Equal(string.Empty, saved.General.CaptureGuideColor);
            draft.CaptureGuideColorHex = "#ABCDEF";
            Pump(450);
            Assert.Equal("#ABCDEF", saved.General.CaptureGuideColor);
            Assert.False(draft.HasErrors);

            draft.CaptureGuideColorMode = "theme";
            Pump();
            draft.CaptureGuideColorHex = "#13579B";
            draft.CaptureGuideColorMode = "custom";
            Pump();
            Assert.Equal("#13579B", saved.General.CaptureGuideColor);
        }
        finally { window.CloseForExit(); app.Shutdown(); }
    });

    [Fact]
    public Task InvalidImportRetainsPrivatePayloadUntilFullReplacementSucceeds() => Isolated(nameof(InvalidImportRetainsPrivatePayloadUntilFullReplacementSucceeds), () =>
    {
        Application app = CreateApplication();
        AppSettings saved = NewInstallDefaults.Create();
        const long exactBytes = 5L * 1024 * 1024 * 1024 + 12345;
        saved.Queue.MaxBytes = exactBytes;
        AppSettings imported = saved.DeepClone();
        Assert.True(Hotkey.TryParse("Ctrl+Alt+F7", out Hotkey duplicate));
        imported.Hotkeys.Capture = imported.Hotkeys.OpenLibrary = duplicate;
        imported.General.PlayCaptureSound = !saved.General.PlayCaptureSound;
        imported.Export.LastImageExport = ImageExportOptions.FromPreset(ImageExportPreset.Document) with { LongEdge = 2560 };
        imported.Annotation.StrokeColor = new ColorRgba(255, 21, 67, 123);
        ImageExportOptions originalExport = saved.Export.LastImageExport;
        ColorRgba originalColor = saved.Annotation.StrokeColor;
        var replacementFlags = new List<bool>();
        bool failFullReplacement = true;
        var window = new SettingsWindow(() => saved, _ => throw new InvalidOperationException("Use the draft apply seam"), NullLogger.Instance,
            applyDraft: (candidate, _, replaceExport) =>
            {
                replacementFlags.Add(replaceExport);
                if (replaceExport && failFullReplacement)
                {
                    failFullReplacement = false;
                    return SettingsApplyResult.NotSaved(["Synthetic import save failure"]);
                }
                if (!replaceExport) candidate.Export.LastImageExport = saved.Export.LastImageExport;
                saved = candidate;
                return new SettingsApplyResult(true, true, true, false, []);
            });
        try
        {
            var draft = (SettingsDraft)window.DataContext;
            draft.ReplaceValues(imported);
            Pump();
            Assert.Equal(imported.General.PlayCaptureSound, saved.General.PlayCaptureSound);
            Assert.Equal(originalExport, saved.Export.LastImageExport);
            Assert.Equal(originalColor, saved.Annotation.StrokeColor);
            Assert.Equal(exactBytes, saved.Queue.MaxBytes);
            Assert.True(draft.ExportOptionsReplaced);
            Assert.Equal([false], replacementFlags);
            draft.OpenLibraryHotkey = "Ctrl+Alt+F8";
            Pump(450);
            Assert.True(draft.ExportOptionsReplaced);
            Assert.Equal(originalExport, saved.Export.LastImageExport);
            Assert.Equal(originalColor, saved.Annotation.StrokeColor);
            Assert.Equal(exactBytes, saved.Queue.MaxBytes);
            Assert.True(window.FlushPendingSettings());
            Assert.Equal(imported.Export.LastImageExport, saved.Export.LastImageExport);
            Assert.Equal(imported.Annotation.StrokeColor, saved.Annotation.StrokeColor);
            Assert.False(draft.ExportOptionsReplaced);
            Assert.Equal([false, true, true], replacementFlags);
            ImageExportOptions runtimeExport = ImageExportOptions.FromPreset(ImageExportPreset.Messenger) with { LongEdge = 800 };
            saved.Export.LastImageExport = runtimeExport;
            draft.NotifyOnQuickSave = !draft.NotifyOnQuickSave;
            Pump();
            Assert.Equal(runtimeExport, saved.Export.LastImageExport);
            Assert.False(replacementFlags.Last());
        }
        finally { window.CloseForExit(); app.Shutdown(); }
    });

    [Fact]
    public void ApplyAcceptanceHandlesGroupBooleanPathsWithoutNameConventions()
    {
        var result = new SettingsApplyResult(true, false, true, false, ["Synthetic group rollback"]);
        var requested = new Dictionary<string, object?>
        {
            ["Hotkeys.SyntheticSwitch"] = false, ["Hotkeys.Capture"] = "Ctrl+1",
            ["Hotkeys.OpenLibrary"] = "Ctrl+2", ["General.PlayCaptureSound"] = true,
        };
        var actual = new Dictionary<string, object?>
        {
            ["Hotkeys.SyntheticSwitch"] = true, ["Hotkeys.Capture"] = "Ctrl+3",
            ["Hotkeys.OpenLibrary"] = "Ctrl+4", ["General.PlayCaptureSound"] = true,
        };
        IReadOnlySet<string> rejected = SettingsApplyAcceptance.RejectedEdits(result, requested, actual);
        Assert.Equal(3, rejected.Count);
        Assert.Contains("Hotkeys.SyntheticSwitch", rejected);
        Assert.Contains("Hotkeys.Capture", rejected);
        Assert.Contains("Hotkeys.OpenLibrary", rejected);
        Assert.DoesNotContain("General.PlayCaptureSound", rejected);
        Assert.Empty(SettingsApplyAcceptance.RejectedEdits(result with { HotkeysApplied = true }, requested, requested));
        Assert.Equal(requested.Count, SettingsApplyAcceptance.RejectedEdits(
            SettingsApplyResult.NotSaved(["Synthetic persistence failure"]), requested, actual).Count);
    }

    [Fact]
    public Task GroupRollbackRejectsBothHotkeysButAcceptsIndependentToggle() => Isolated(nameof(GroupRollbackRejectsBothHotkeysButAcceptsIndependentToggle), () =>
    {
        Application app = CreateApplication();
        AppSettings saved = NewInstallDefaults.Create();
        Hotkey originalCapture = saved.Hotkeys.Capture, originalLibrary = saved.Hotkeys.OpenLibrary;
        int saves = 0;
        bool reject = true;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            saves++;
            if (reject) candidate.Hotkeys = saved.DeepClone().Hotkeys;
            saved = candidate;
            return new SettingsApplyResult(true, !reject, true, false, reject ? ["Synthetic group rollback"] : []);
        }, NullLogger.Instance);
        try
        {
            var draft = (SettingsDraft)window.DataContext;
            draft.CaptureHotkey = "Ctrl+Alt+F7";
            draft.OpenLibraryHotkey = "Ctrl+Alt+F8";
            draft.PlayCaptureSound = !draft.PlayCaptureSound;
            Assert.False(window.FlushPendingSettings());
            Assert.Equal(originalCapture, saved.Hotkeys.Capture);
            Assert.Equal(originalLibrary, saved.Hotkeys.OpenLibrary);
            Assert.Equal(draft.PlayCaptureSound, saved.General.PlayCaptureSound);
            Assert.Contains("Synthetic group rollback", draft.GetErrors(nameof(SettingsDraft.CaptureHotkey)).Cast<string>());
            Assert.Contains("Synthetic group rollback", draft.GetErrors(nameof(SettingsDraft.OpenLibraryHotkey)).Cast<string>());
            int afterRejectedSave = saves;
            Assert.False(window.FlushPendingSettings());
            Assert.Equal(afterRejectedSave, saves); // The accepted boolean is not retried.
            reject = false;
            draft.CaptureHotkey = "Ctrl+Alt+F9";
            draft.OpenLibraryHotkey = "Ctrl+Alt+F10";
            Assert.True(window.FlushPendingSettings());
            Assert.False(draft.HasErrors);
        }
        finally { window.CloseForExit(); app.Shutdown(); }
    });

    [Fact]
    public Task SearchUsesOneNeutralHighlightAndVisibleConditionalTargets() => Isolated(nameof(SearchUsesOneNeutralHighlightAndVisibleConditionalTargets), () =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application app = CreateApplication();
        AppSettings saved = NewInstallDefaults.Create();
        var window = new SettingsWindow(() => saved, candidate => { saved = candidate; return new SettingsApplyResult(true, true, true, false, []); }, NullLogger.Instance);
        try
        {
            Render(window, "review-search-before.png", 940, 700);
            var tabs = (TabControl)window.FindName("CategoryTabs");
            var search = (TextBox)tabs.Template.FindName("SettingsSearchBox", tabs);
            var general = (TabItem)tabs.SelectedItem;
            search.Text = "HEX";
            Render(window, "review-search-hex-theme.png", 940, 700);
            Assert.NotSame(general, tabs.SelectedItem);
            FrameworkElement target = Assert.IsAssignableFrom<FrameworkElement>(window.FirstSearchMatch);
            Assert.Same(window.FindName("CaptureGuideColorModeSelector"), target);
            Assert.Equal("theme", ((SettingsDraft)window.DataContext).CaptureGuideColorMode);
            Assert.True(target.ActualWidth > 0 && target.ActualHeight > 0);
            AdornerLayer layer = Assert.IsType<AdornerLayer>(AdornerLayer.GetAdornerLayer(target));
            Adorner adorner = Assert.Single(layer.GetAdorners(target)!);
            DrawingGroup drawing = Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(adorner));
            GeometryDrawing outline = Assert.Single(drawing.Children.OfType<GeometryDrawing>());
            Assert.Equal(2, outline.Pen.Thickness);
            Assert.Equal(((SolidColorBrush)target.FindResource("Text.Primary")).Color, ((SolidColorBrush)outline.Pen.Brush).Color);
            search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump();
            Assert.Empty(layer.GetAdorners(target) ?? []);
            Assert.Equal(string.Empty, window.SearchQuery);
            var draft = (SettingsDraft)window.DataContext;
            draft.CaptureGuideColorHex = "#123456";
            draft.CaptureGuideColorMode = "custom";
            Pump();
            search.Text = "HEX";
            Render(window, "review-search-hex-custom.png", 940, 700);
            FrameworkElement custom = Assert.IsAssignableFrom<FrameworkElement>(window.FirstSearchMatch);
            Assert.NotSame(target, custom);
            Assert.Equal(Visibility.Visible, custom.Visibility);
            Assert.True(custom.ActualHeight > 0);
            AdornerLayer customLayer = Assert.IsType<AdornerLayer>(AdornerLayer.GetAdornerLayer(custom));
            Assert.Single(customLayer.GetAdorners(custom)!);
            search.Clear();
            Pump();
            Assert.Empty(layer.GetAdorners(target) ?? []);
            Assert.Empty(customLayer.GetAdorners(custom) ?? []);
        }
        finally { window.CloseForExit(); app.Shutdown(); }
    });

    [Fact]
    public Task SupportRefreshesSnapshotsAndUsesSpecificLocalizedCopy() => Isolated(nameof(SupportRefreshesSnapshotsAndUsesSpecificLocalizedCopy), () =>
    {
        Application app = CreateApplication();
        try
        {
            foreach (string culture in new[] { "ko-KR", "en" })
            {
                using var language = UiText.UseLanguage(culture);
                SupportDiagnosticSnapshot current = Snapshot(true) with { DisplayScale = 1, Theme = "Glass" };
                var actions = new FakeSupportActions();
                var support = new SupportWindow(actions, _ => current);
                try
                {
                    Assert.Equal(DependencyProperty.UnsetValue, support.DiagnosticsBox.ReadLocalValue(TextBox.SelectionBrushProperty));
                    Assert.Equal(DependencyProperty.UnsetValue, support.MessageBox.ReadLocalValue(TextBox.SelectionBrushProperty));
                    current = current with { DisplayScale = 1.5, Theme = "Daylight" };
                    support.CopyDiagnostics();
                    Assert.Contains(SupportReport.Format(current), actions.Copied);
                    current = current with { DisplayScale = 2, Theme = "Midnight" };
                    support.OpenGitHub();
                    string uri = Assert.Single(actions.Opened);
                    Assert.Contains(SupportReport.Format(current), Uri.UnescapeDataString(uri[(uri.IndexOf("&body=", StringComparison.Ordinal) + 6)..]));
                    Assert.Equal(UiText.Get("Support_GitHubOpened"), support.StatusText);
                    Assert.Equal(culture == "ko-KR" ? "GitHub 이슈 작성 페이지 열림" : "GitHub issue page opened", support.StatusText);
                    support.OpenStoreReview();
                    Assert.Equal(UiText.Get("Support_StoreOpened"), support.StatusText);
                    Assert.Equal(culture == "ko-KR" ? "Microsoft Store 리뷰 열림" : "Microsoft Store review opened", support.StatusText);
                    Assert.Equal(culture == "ko-KR" ? "문의 창 닫기 · Esc" : "Close support · Esc", support.CloseButton.ToolTip);
                    current = current with { DisplayScale = 1.25, Theme = "Workspace" };
                    typeof(SupportWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(support, [new DpiScale(2, 2), new DpiScale(1.25, 1.25)]);
                    Assert.Equal(SupportReport.Format(current), support.DiagnosticsBox.Text);
                    Render(support, $"review-support-{culture}.png", 680, 640);
                }
                finally { support.Close(); }

                AppSettings saved = NewInstallDefaults.Create();
                var settings = new SettingsWindow(() => saved, candidate => { saved = candidate; return new SettingsApplyResult(true, true, true, false, []); }, NullLogger.Instance);
                try
                {
                    var about = (TabItem)settings.FindName("UpdateTab");
                    Assert.DoesNotContain(Descendants(about).OfType<TextBlock>(), text => text.Text == UiText.Get("About_ReportNote"));
                    ((TabControl)settings.FindName("CategoryTabs")).SelectedItem = about;
                    Render(settings, $"review-about-{culture}.png", 940, 700);
                }
                finally { settings.CloseForExit(); }
            }
        }
        finally { app.Shutdown(); }
    });
}
