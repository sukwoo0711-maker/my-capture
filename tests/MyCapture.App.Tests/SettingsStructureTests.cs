using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Xml.Linq;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsStructureTests
{
    [Fact]
    public void EvidencePathsStayInsideTheOwnedFixture()
    {
        var paths = SettingsStructureEvidenceTests.FixturePaths;
        string expected = Path.GetFullPath(Path.Combine(SettingsStructureEvidenceTests.Output, "isolated-profile"));
        Assert.Equal(expected, paths.DataRoot);
        foreach (string child in new[] { paths.CapturesRoot, paths.QuickSaveRoot, paths.OcrModelsRoot, paths.TranslationModelsRoot })
            Assert.StartsWith(expected + Path.DirectorySeparatorChar, child, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly (SettingsSection Section, string Fields)[] Expected =
    [
        (SettingsSection.Capture, "AfterCapture PlayCaptureSound NotifyOnQuickSave IncludeCursor ShowMagnifier ShowSelectionGrid AutoDetectWindows CaptureGuideColorMode CaptureGuideColorHex AlwaysShowKeyPanel ColorFormat AbortOnFocusLoss DelaySeconds RegionHistoryLimit"),
        (SettingsSection.Recording, "RecordingFrameRate UseRecordingStartDelay RecordingStartDelaySeconds RecordingIncludeCursor RecordingCursorHighlight UseRecordingDraggedSize UseRecordingFixedSize RecordingPresetWidth RecordingPresetHeight RecordingWatermarkEnabled RecordingWatermark RecordingCaptureMode"),
        (SettingsSection.Editing, "StrokeThickness FontFamily FontSize MosaicBlockSize HighlighterOpacityPercent BlurRadius"),
        (SettingsSection.Pins, "RestoreOpenPinsOnLaunch CloseOnDoubleClick InitialOpacity ZoomStep ClosedWindowRestoreLimit"),
        (SettingsSection.Text, "OcrQuality PreferredLanguages CacheResults"),
        (SettingsSection.Storage, "QuickSaveDirectoryOverride FileNamePattern CapturesDirectoryOverride ImageRetentionHours UseCurrentFolderForNewCaptures FolderFilePrefix MaxItems MaxGiB ThumbnailLongEdge ShowSourceWindowTitle GitHubIssueUrl GitHubToken"),
        (SettingsSection.Shortcuts, "AlwaysShowShortcutHints CaptureHotkey RecordRegionHotkey CaptureTextHotkey PasteToScreenHotkey HideAllPinsHotkey CaptureWindowHotkey CaptureFullScreenHotkey RepeatLastRegionHotkey ScrollingCaptureHotkey DelayedCaptureHotkey PauseRecordingHotkey OpenLibraryHotkey ToggleClickThroughHotkey RestoreClosedPinHotkey UploadGitHubImageHotkey ForceCoreShortcuts"),
        (SettingsSection.General, "Language Theme LaunchAtLogin"),
        (SettingsSection.About, "AutoCheckUpdates"),
    ];

    [Fact]
    public void EveryOriginalBindingAndManualControlSurvives()
    {
        const string original = "AbortOnFocusLoss AfterCapture AlwaysShowKeyPanel AlwaysShowShortcutHints AutoCheckUpdates AutoDetectWindows BlurRadius CacheResults CaptureFullScreenHotkey CaptureGuideColorHex CaptureGuideColorMode CaptureHotkey CaptureTextHotkey CaptureWindowHotkey CapturesDirectoryOverride CloseOnDoubleClick ClosedWindowRestoreLimit ColorFormat DelaySeconds DelayedCaptureHotkey FileNamePattern FolderFilePrefix FontSize ForceCoreShortcuts GitHubIssueUrl Header HideAllPinsHotkey HighlighterOpacityPercent IncludeCursor InitialOpacity IsCustomCaptureGuideColor IsExpanded Language LaunchAtLogin MaxGiB MaxItems MosaicBlockSize NotifyOnQuickSave OcrQuality OpenLibraryHotkey PasteToScreenHotkey PauseRecordingHotkey PlayCaptureSound PreferredLanguages QuickSaveDirectoryOverride RecordRegionHotkey RecordingCaptureMode RecordingCursorHighlight RecordingFrameRate RecordingIncludeCursor RecordingPresetHeight RecordingPresetWidth RecordingStartDelaySeconds RecordingWatermarkEnabled RegionHistoryLimit RepeatLastRegionHotkey RestoreClosedPinHotkey RestoreOpenPinsOnLaunch ScrollingCaptureHotkey ShowMagnifier ShowSelectionGrid ShowSourceWindowTitle StrokeThickness Text Theme ThumbnailLongEdge ToggleClickThroughHotkey UploadGitHubImageHotkey UseCurrentFolderForNewCaptures UseRecordingDraggedSize UseRecordingFixedSize UseRecordingStartDelay ZoomStep";
        using Stream stream = typeof(SettingsStructureTests).Assembly.GetManifestResourceStream("SettingsFixture.Window.xaml")!;
        using var reader = new StreamReader(stream);
        string markup = reader.ReadToEnd();
        string[] actual = Regex.Matches(markup, @"\{Binding ([A-Za-z][A-Za-z0-9_]*)[,}]").Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(original.Split(' ').Order(StringComparer.Ordinal), actual);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = XDocument.Parse(markup).Root!;
        foreach (string name in new[] { "FontFamilyBox", "RetentionChoiceBox", "GitHubTokenBox", "RecordingWatermarkSettingsButton" })
            Assert.Single(root.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
        Assert.Equal(73, SettingsSectionCatalog.Fields.Count);
        Assert.Equal(SettingsSectionCatalog.Fields.Count, SettingsSectionCatalog.Fields.Select(field => field.DraftProperty).Distinct().Count());
    }

    [Fact]
    public Task CategoriesFieldsAndNamedGroupsFollowApprovedOrder() => Isolated(nameof(CategoriesFieldsAndNamedGroupsFollowApprovedOrder), () => WithWindow(window =>
    {
        var tabs = (TabControl)window.FindName("CategoryTabs");
        Assert.Equal(Expected.Select(item => item.Section), SettingsSectionCatalog.Sections.Select(section => section.Section));
        Assert.Equal(9, tabs.Items.Count);
        for (int index = 0; index < Expected.Length; index++)
        {
            var expected = Expected[index];
            SettingsSectionDefinition section = SettingsSectionCatalog.Sections[index];
            var tab = Assert.IsType<TabItem>(window.FindName(section.TabName));
            Assert.Same(tab, tabs.Items[index]);
            Assert.Equal(UiText.Get(section.LabelKey), tab.Header);
            string[] fields = expected.Fields.Split(' ');
            Assert.Equal(fields, SettingsSectionCatalog.Fields.Where(field => field.Section == expected.Section).Select(field => field.DraftProperty));
            var descendants = SettingsStructureEvidenceTests.Logical(tab).ToList();
            int previous = -1;
            foreach (string property in fields)
            {
                FrameworkElement control = Assert.IsAssignableFrom<FrameworkElement>(window.FindSettingControl(property));
                int ordinal = descendants.IndexOf(control);
                Assert.True(ordinal > previous, $"{expected.Section}: {property} is out of order or missing ({ordinal}, {previous}).");
                previous = ordinal;
            }
        }
        foreach (Expander group in SettingsStructureEvidenceTests.Logical(tabs).OfType<Expander>())
        {
            Assert.False(group.IsExpanded);
            Assert.False(string.IsNullOrWhiteSpace(group.Header?.ToString()));
            Assert.DoesNotContain(group.Header?.ToString(), new[] { "고급", "Advanced" });
        }
    }));

    [Fact]
    public Task OutsideEntryAndErrorsRevealEveryFoldWithoutReloadingDraft() => Isolated(nameof(OutsideEntryAndErrorsRevealEveryFoldWithoutReloadingDraft), () => WithWindow(window =>
    {
        var draft = (SettingsDraft)window.DataContext;
        var tabs = (TabControl)window.FindName("CategoryTabs");
        foreach (SettingsFieldDefinition field in SettingsSectionCatalog.Fields.Where(field => field.GroupName is not null).GroupBy(field => field.GroupName).Select(group => group.First()))
        {
            var fold = (Expander)window.FindName(field.GroupName!);
            fold.IsExpanded = false;
            window.FilterSettings("impossible-match");
            Assert.True(window.NavigateToSetting(field.DraftProperty));
            SettingsStructureEvidenceTests.Layout(window);
            Assert.True(fold.IsExpanded, field.DraftProperty);
            Assert.Same(draft, window.DataContext);
            Assert.Empty(window.SearchQuery);
            Assert.Same(window.FindName(SettingsSectionCatalog.Sections.Single(section => section.Section == field.Section).TabName), tabs.SelectedItem);
            fold.IsExpanded = false;
            window.NavigateToSection(SettingsSection.General);
            draft.SetSaveError(field.DraftProperty, "Synthetic validation error");
            SettingsStructureEvidenceTests.Layout(window);
            Assert.True(fold.IsExpanded, field.DraftProperty + " error");
            Assert.Same(window.FindName(SettingsSectionCatalog.Sections.Single(section => section.Section == field.Section).TabName), tabs.SelectedItem);
            Assert.Same(draft, window.DataContext);
        }
        object? selected = tabs.SelectedItem;
        Assert.False(window.NavigateToSetting("unknown-field"));
        Assert.Same(selected, tabs.SelectedItem);
    }));

    [Fact]
    public Task SearchRevealsFoldedRowsAndNeverIndexesSecretValues() => Isolated(nameof(SearchRevealsFoldedRowsAndNeverIndexesSecretValues), () => WithWindow(window =>
    {
        foreach (string property in new[] { "DelaySeconds", "RecordingCaptureMode", "ZoomStep", "MaxItems", "ShowSourceWindowTitle", "GitHubIssueUrl", "CaptureWindowHotkey", "ForceCoreShortcuts" })
        {
            SettingsFieldDefinition field = SettingsSectionCatalog.Find(property)!;
            var fold = (Expander)window.FindName(field.GroupName!);
            fold.IsExpanded = false;
            window.NavigateToSection(SettingsSection.General);
            var control = window.FindSettingControl(property)!;
            string query = AutomationProperties.GetName(control);
            if (string.IsNullOrWhiteSpace(query) && control is ContentControl { Content: string content }) query = content;
            Assert.False(string.IsNullOrWhiteSpace(query), property);
            window.FilterSettings(query);
            SettingsStructureEvidenceTests.Layout(window);
            Assert.True(fold.IsExpanded, $"{property}: {query}; active={window.SearchQuery}; target={window.FirstSearchMatch?.GetType().Name}/{window.FirstSearchMatch?.Name}/{(window.FirstSearchMatch is null ? "" : AutomationProperties.GetName(window.FirstSearchMatch))}; section={((TabControl)window.FindName("CategoryTabs")).SelectedItem}");
            Assert.NotNull(window.FirstSearchMatch);
        }
        var draft = (SettingsDraft)window.DataContext;
        draft.GitHubToken = "synthetic-private-token-427";
        window.FilterSettings(draft.GitHubToken);
        Assert.Null(((TabControl)window.FindName("CategoryTabs")).SelectedItem);
        Assert.Null(window.FirstSearchMatch);
    }));

    [Fact]
    public Task GuideButtonsUseFakePresenterAndLeaveSettingsUntouched() => Isolated(nameof(GuideButtonsUseFakePresenterAndLeaveSettingsUntouched), () => WithWindow(window =>
    {
        var draft = (SettingsDraft)window.DataContext;
        string before = System.Text.Json.JsonSerializer.Serialize(draft.ToAppSettings());
        var presenter = new FakeGuides();
        window.GuidePresenter = presenter;
        var buttons = SettingsStructureEvidenceTests.Logical((TabControl)window.FindName("CategoryTabs")).OfType<Button>()
            .Where(button => button.Tag is string id && Regex.IsMatch(id, @"^G\d\d$")).ToArray();
        Assert.Equal(Enumerable.Range(1, 13).Select(number => $"G{number:00}"), buttons.Select(button => (string)button.Tag).Distinct().Order());
        foreach (Button button in buttons)
        {
            Assert.True(button.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.Equal(UiText.Get((string)button.Tag == "G09" ? "SettingsStructure_Compare" : "SettingsStructure_Guide"), button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(((string)button.Tag, window), presenter.Calls.Last());
        }
        Assert.Equal(buttons.Length, presenter.Calls.Count);
        Assert.Same(draft, window.DataContext);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(draft.ToAppSettings()));
    }));

    [Fact]
    public void KoreanAndEnglishCategoryAndGuideLabelsAreAvailable()
    {
        foreach (string language in new[] { "ko-KR", "en" })
        {
            using var culture = UiText.UseLanguage(language);
            Assert.Equal(language == "ko-KR" ? "핀" : "Pins", UiText.Get("SettingsStructure_TabPins"));
            Assert.Equal(language == "ko-KR" ? "텍스트·번역" : "Text & translation", UiText.Get("SettingsStructure_TabText"));
            Assert.Equal(language == "ko-KR" ? "변화 보기" : "See the difference", UiText.Get("SettingsStructure_Guide"));
            Assert.Equal(language == "ko-KR" ? "결과 비교" : "Compare results", UiText.Get("SettingsStructure_Compare"));
            Assert.Equal(language == "ko-KR" ? "흐림 강도" : "Blur strength", UiText.Get("SettingsStructure_BlurStrength"));
            foreach (SettingsSectionDefinition section in SettingsSectionCatalog.Sections) Assert.False(string.IsNullOrWhiteSpace(UiText.Get(section.LabelKey)));
        }
    }

    [Fact]
    public Task OutsideEntryAtMinimumSizeRevealsSelectedCategory() => Isolated(nameof(OutsideEntryAtMinimumSizeRevealsSelectedCategory), () => WithWindow(window =>
    {
        window.Width = 780; window.Height = 560;
        var tabs = (TabControl)window.FindName("CategoryTabs");
        SettingsStructureEvidenceTests.Layout(window);
        var navigation = (ScrollViewer)tabs.Template.FindName("CategoryScroll", tabs);
        navigation.ScrollToTop();
        Assert.True(window.NavigateToSetting("AutoCheckUpdates"));
        SettingsStructureEvidenceTests.Layout(window);
        var selected = Assert.IsType<TabItem>(tabs.SelectedItem);
        Rect bounds = selected.TransformToAncestor(navigation).TransformBounds(new Rect(selected.RenderSize));
        Assert.True(bounds.Top >= -1 && bounds.Bottom <= navigation.ActualHeight + 1,
            $"Selected category {selected.Header} is clipped: {bounds}, viewport {navigation.ActualHeight}.");
        Assert.True(window.NavigateToSetting("AfterCapture"));
        SettingsStructureEvidenceTests.Layout(window);
        selected = Assert.IsType<TabItem>(tabs.SelectedItem);
        bounds = selected.TransformToAncestor(navigation).TransformBounds(new Rect(selected.RenderSize));
        Assert.True(bounds.Top >= -1 && bounds.Bottom <= navigation.ActualHeight + 1);
    }));
    [Fact]
    public Task PublicEntryKeepsPendingTextAndRevealsAdditionalShortcut() => Isolated(nameof(PublicEntryKeepsPendingTextAndRevealsAdditionalShortcut), () => WithWindow(window =>
    {
        var draft = (SettingsDraft)window.DataContext;
        draft.DelaySeconds = "unfinished-input";
        window.ShowSetting("ZoomStep");
        SettingsStructureEvidenceTests.Layout(window);
        Assert.Same(draft, window.DataContext);
        Assert.Equal("unfinished-input", draft.DelaySeconds);
        Assert.True(((Expander)window.FindName("PinHistoryExpander")).IsExpanded);
        window.ShowShortcut("UploadGitHubImageHotkey");
        SettingsStructureEvidenceTests.Layout(window);
        Assert.True(((Expander)window.FindName("AdditionalShortcutsExpander")).IsExpanded);
        Assert.Same(window.FindName("ShortcutsTab"), ((TabControl)window.FindName("CategoryTabs")).SelectedItem);
        Assert.Same(draft, window.DataContext);
        Assert.Equal("unfinished-input", draft.DelaySeconds);
    }));
    private sealed class FakeGuides : ISettingsGuidePresenter
    {
        internal List<(string, Window)> Calls { get; } = [];
        public void Show(string guideId, Window owner) => Calls.Add((guideId, owner));
    }
    private static Task Isolated(string name, Action action) => SettingsThemePreviewTests.RunIsolated(name, action, typeof(SettingsStructureTests));
    private static void WithWindow(Action<SettingsWindow> action)
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application app = SettingsStructureEvidenceTests.CreateApplication();
        SettingsWindow window = SettingsStructureEvidenceTests.CreateWindow();
        try { window.Show(); SettingsStructureEvidenceTests.Layout(window); action(window); }
        finally { window.CloseForExit(); app.Shutdown(); }
    }
}
