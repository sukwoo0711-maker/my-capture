using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private static readonly int[] Phase3SampleTimes = [0, 83, 167, 250];

    [Fact]
    public void Phase3_EvidenceDeclaresBothLanguagesAndEverySelectionDetailsPair()
    {
        NativeFeelSceneGroup[] groups = NativeFeelSceneRegistry.Discover(typeof(SettingsThemePreviewTests))
            .Where(group => group.Name is nameof(NativeFeelPhase3Library) or nameof(NativeFeelPhase3Settings)).ToArray();
        Assert.Equal(2, groups.Length);
        string[] actual = groups.SelectMany(group => group.Scenes).Select(scene => scene.Id).Order().ToArray();
        string[] expected = (from language in new[] { "ko", "en" }
                from selection in new[] { "none", "one", "many" }
                from details in new[] { "closed", "open" }
                select $"phase3-library-{language}-{selection}-{details}")
            .Concat(from language in new[] { "ko", "en" }
                from state in new[] { "general", "densest", "category-switch" }
                select $"phase3-settings-{language}-{state}")
            .Order().ToArray();
        Assert.Equal(expected, actual);
        Assert.All(groups, group => Assert.False(group.Baseline));
    }

    [NativeFeelSceneGroup(31)]
    [NativeFeelScene("phase3-library-ko-none-closed", "library-window")]
    [NativeFeelScene("phase3-library-ko-none-open", "library-window")]
    [NativeFeelScene("phase3-library-ko-one-closed", "library-window")]
    [NativeFeelScene("phase3-library-ko-one-open", "library-window")]
    [NativeFeelScene("phase3-library-ko-many-closed", "library-window")]
    [NativeFeelScene("phase3-library-ko-many-open", "library-window")]
    [NativeFeelScene("phase3-library-en-none-closed", "library-window")]
    [NativeFeelScene("phase3-library-en-none-open", "library-window")]
    [NativeFeelScene("phase3-library-en-one-closed", "library-window")]
    [NativeFeelScene("phase3-library-en-one-open", "library-window")]
    [NativeFeelScene("phase3-library-en-many-closed", "library-window")]
    [NativeFeelScene("phase3-library-en-many-open", "library-window")]
    private static void NativeFeelPhase3Library(NativeFeelSceneContext context)
    {
        foreach (string language in new[] { "ko", "en" })
        {
            using var culture = UiText.UseLanguage(language);
            NativeFeelLibraryFixture fixture = NativeFeelNewLibrary(context.Profile,
                $"phase3-library-{context.Theme}-{language}", 3);
            try
            {
                context.Desktop.Place(fixture.Window, 1180, 720);
                fixture.Window.Show();
                Phase3PrepareLibrary(fixture);
                Phase3LibraryStates(fixture, (state, selection, details) =>
                    context.Desktop.Still($"phase3-library-{language}-{state}", "phase3-library", fixture.Window,
                        $"Language={language}; synthetic search query=Review; exactly three results; selected={selection}; details={details}."));
            }
            finally { fixture.Window.CloseForExit(); }
        }
    }

    [NativeFeelSceneGroup(26)]
    [NativeFeelScene("phase3-settings-ko-general", "settings-window")]
    [NativeFeelScene("phase3-settings-ko-densest", "settings-window")]
    [NativeFeelScene("phase3-settings-ko-category-switch", "settings-window")]
    [NativeFeelScene("phase3-settings-en-general", "settings-window")]
    [NativeFeelScene("phase3-settings-en-densest", "settings-window")]
    [NativeFeelScene("phase3-settings-en-category-switch", "settings-window")]
    private static void NativeFeelPhase3Settings(NativeFeelSceneContext context)
    {
        foreach (string language in new[] { "ko", "en" })
        {
            using var culture = UiText.UseLanguage(language);
            var window = Phase3SettingsWindow(context.Profile);
            try
            {
                context.Desktop.Place(window, 940, 700);
                window.Show();
                var tabs = (TabControl)window.FindName("CategoryTabs");
                Assert.True(window.NavigateToSection(SettingsSection.General));
                context.Desktop.Still($"phase3-settings-{language}-general", "phase3-settings", window,
                    $"Language={language}; real General page with synthetic defaults and an isolated storage path.");
                (int target, string density) = Phase3DensestCategory(tabs);
                tabs.SelectedIndex = target;
                context.Desktop.Still($"phase3-settings-{language}-densest", "phase3-settings", window,
                    $"Language={language}; {density}");
                int source = tabs.Items.OfType<TabItem>().ToList().FindIndex(tab => tab.Name == "GeneralTab");
                Assert.InRange(source, 0, tabs.Items.Count - 1);
                if (source == target) source = target == 0 ? 1 : 0;
                tabs.SelectedIndex = source;
                NativeFeelDesktopCapture.Pump(350);
                string id = $"phase3-settings-{language}-category-switch";
                context.Desktop.Sequence(id, "phase3-settings-motion", context.Desktop.Bounds(window),
                    () => tabs.SelectedIndex = target,
                    $"Language={language}; real category switch; host motion preference retained. {density}");
                NativeFeelFrame[] frames = context.Desktop.Frames.Where(frame => frame.Id == id).ToArray();
                string samples = string.Join("; ", Phase3SampleTimes.Select(targetMs =>
                {
                    NativeFeelFrame frame = frames.MinBy(sample => Math.Abs(sample.BeginMs - targetMs))!;
                    return $"target={targetMs}ms: {frame.File}, measured={frame.BeginMs:F2}..{frame.EndMs:F2}ms";
                }));
                int index = context.Desktop.Cases.FindIndex(item => item.Id == id);
                NativeFeelCase captured = context.Desktop.Cases[index];
                context.Desktop.Cases[index] = captured with
                {
                    Note = captured.Note + " Nearest actual samples (a negative sample is before the trigger): " + samples,
                };
            }
            finally { window.CloseForExit(); }
        }
    }

    private static SettingsWindow Phase3SettingsWindow(string profile)
    {
        AppSettings saved = NewInstallDefaults.Create();
        saved.General.Theme = AppThemeNames.ToSetting(ThemeService.Current);
        return new SettingsWindow(() => saved,
            _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance,
            defaultCapturesRoot: () => profile);
    }

    private static void Phase3PrepareLibrary(NativeFeelLibraryFixture fixture)
    {
        fixture.ViewModel.SearchQuery = "Review";
        PumpLibraryUntil(fixture.Window, () => fixture.Window.RealizedCards().Count() == 3
            && fixture.Window.RealizedCards().All(card => FeelPicture(card).Source is not null
                && !FeelPicture(card).IsFading), "phase 3: three loaded search results");
        Assert.Equal(3, fixture.ViewModel.VisibleCount);
    }

    private static void Phase3LibraryStates(NativeFeelLibraryFixture fixture, Action<string, int, bool> capture)
    {
        foreach ((string state, int count) in new[] { ("none", 0), ("one", 1), ("many", 2) })
        {
            fixture.ViewModel.ClearSelection();
            for (int i = 0; i < count; i++) fixture.ViewModel.Select(fixture.Records[i].Id, control: i > 0);
            Assert.Equal(count, fixture.ViewModel.Selection.Count);
            foreach (bool details in new[] { false, true })
            {
                fixture.Window.SetDetailsPaneOpen(details);
                fixture.Window.UpdateLayout();
                Assert.Equal(details, ((FrameworkElement)fixture.Window.FindName("GalleryDetailsPanel")).IsVisible);
                capture($"{state}-{(details ? "open" : "closed")}", count, details);
            }
        }
    }

    private static (int Index, string Note) Phase3DensestCategory(TabControl tabs)
    {
        TabItem[] items = tabs.Items.OfType<TabItem>().ToArray();
        Assert.Equal(9, items.Length);
        var counts = new List<int>();
        for (int i = 0; i < items.Length; i++)
        {
            tabs.SelectedIndex = i;
            NativeFeelDesktopCapture.Pump(30);
            tabs.UpdateLayout();
            DependencyObject content = Assert.IsAssignableFrom<DependencyObject>(items[i].Content);
            int count = Descendants(content).OfType<FrameworkElement>().Count(element =>
                element.IsVisible && element is TextBlock or TextBox or Button or CheckBox or ComboBox or Slider);
            Assert.True(count > 0, $"No visible content elements in {items[i].Name}.");
            counts.Add(count);
        }
        Assert.True(counts.Distinct().Count() > 1);
        int target = counts.IndexOf(counts.Max());
        return (target, $"Densest page={items[target].Name}; visible content element counts: "
            + string.Join("; ", items.Select((item, i) => $"{item.Name}={counts[i]}")) + ".");
    }

    [Phase3OffscreenEvidenceFact]
    public Task Phase3_RenderOffscreenEvidence() => RunIsolated(nameof(Phase3_RenderOffscreenEvidence),
        Phase3RenderOffscreen, hungAfter: TimeSpan.FromMinutes(3));

    private static void Phase3RenderOffscreen()
    {
        string requested = Environment.GetEnvironmentVariable(Phase3OffscreenEvidenceFactAttribute.OutputVariable)
            ?? throw new InvalidOperationException("The offscreen evidence output was not requested.");
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("Choose a new absolute output directory.");
        string output = TestDiagnosticArtifacts.NewDirectory(requested);
        string profile = OwnedTestDirectory.Create("mc-phase3-offscreen-");
        var application = CreateLibraryApplication();
        var captures = new List<object>();
        string? failure = null;
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            foreach (string language in new[] { "ko", "en" })
            {
                using var culture = UiText.UseLanguage(language);
                ThemeService.Apply(theme);
                string palette = theme == AppTheme.Glass ? "dark" : "light";
                string suffix = $"{language}-{palette}";
                NativeFeelLibraryFixture fixture = NativeFeelNewLibrary(profile, "library-" + suffix, 3);
                try
                {
                    Phase3PlaceOffscreen(fixture.Window, 1180, 720);
                    fixture.Window.Show();
                    Phase3PrepareLibrary(fixture);
                    Phase3LibraryStates(fixture, (state, selection, details) =>
                    {
                        NativeFeelDesktopCapture.Pump(350);
                        string file = $"library-{state}-{suffix}.png";
                        TestDiagnosticArtifacts.SavePng(output, file, Phase3RenderWindow(fixture.Window));
                        captures.Add(new { scene = "library", language, theme = palette, file, selection, details,
                            search = "Review", results = fixture.ViewModel.VisibleCount });
                    });
                }
                finally { fixture.Window.CloseForExit(); }
                var window = Phase3SettingsWindow(profile);
                try
                {
                    Phase3PlaceOffscreen(window, 940, 700);
                    window.Show();
                    var tabs = (TabControl)window.FindName("CategoryTabs");
                    Assert.True(window.NavigateToSection(SettingsSection.General));
                    NativeFeelDesktopCapture.Pump(350);
                    SaveSettings("general", "General page.");
                    (int target, string density) = Phase3DensestCategory(tabs);
                    tabs.SelectedIndex = target;
                    NativeFeelDesktopCapture.Pump(350);
                    SaveSettings("densest", density);
                    int source = tabs.Items.OfType<TabItem>().ToList().FindIndex(tab => tab.Name == "GeneralTab");
                    Assert.InRange(source, 0, tabs.Items.Count - 1);
                    if (source == target) source = target == 0 ? 1 : 0;
                    tabs.SelectedIndex = source;
                    NativeFeelDesktopCapture.Pump(350);
                    var samples = new List<(int Target, double Begin, double End, BitmapSource Image)>();
                    var clock = Stopwatch.StartNew();
                    tabs.SelectedIndex = target;
                    double triggerEnd = clock.Elapsed.TotalMilliseconds;
                    foreach (int targetMs in Phase3SampleTimes)
                    {
                        if (targetMs > 0) NativeFeelDesktopCapture.Pump(Math.Max(0, targetMs - clock.Elapsed.TotalMilliseconds));
                        double begin = clock.Elapsed.TotalMilliseconds;
                        BitmapSource image = Phase3RenderWindow(window);
                        samples.Add((targetMs, begin, clock.Elapsed.TotalMilliseconds, image));
                    }
                    // Encoding comes after all frames. Target labels never replace measured timestamps.
                    foreach (var sample in samples)
                    {
                        string file = $"settings-switch-{sample.Target:000}ms-{suffix}.png";
                        TestDiagnosticArtifacts.SavePng(output, file, sample.Image);
                        captures.Add(new { scene = "settings-category-switch", language, theme = palette, file,
                            targetMs = sample.Target, beginMs = sample.Begin, endMs = sample.End,
                            triggerEndMs = triggerEnd, motionEnabled = FluidMotion.AnimationsEnabled,
                            source = ((TabItem)tabs.Items[source]).Name, target = ((TabItem)tabs.Items[target]).Name });
                    }

                    void SaveSettings(string state, string note)
                    {
                        string file = $"settings-{state}-{suffix}.png";
                        TestDiagnosticArtifacts.SavePng(output, file, Phase3RenderWindow(window));
                        captures.Add(new { scene = "settings", language, theme = palette, file, state, note });
                    }
                }
                finally { window.CloseForExit(); }
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try
            {
                TestDiagnosticArtifacts.WriteJson(output, "phase3-offscreen-manifest.json", new
                {
                    schema = 1, capture = "WPF window RenderTargetBitmap at 96 dpi; not desktop capture",
                    nativeScaleEvidence = false, nativeTitleBarAndShadow = false,
                    content = "Synthetic identical three-image search fixture and new-install settings defaults.",
                    timings = "Actual measured time after category selection; targets may be missed on a slow host.",
                    motionEnabled = FluidMotion.AnimationsEnabled, captures, failure,
                    fixtureProfile = profile, fixtureRetention = "Retained; no real profile or cleanup is used.",
                });
            }
            finally { application.Shutdown(); }
        }
    }

    private static void Phase3PlaceOffscreen(Window window, double width, double height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Width = width;
        window.Height = height;
    }

    private static BitmapSource Phase3RenderWindow(Window window)
    {
        window.UpdateLayout();
        Assert.True(window.ActualWidth > 0 && window.ActualHeight > 0);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Render the complete WPF window so root margins cannot crop the bottom/right controls.
        // Native title bars and shadows are absent; the real background brush fills that region.
        var composed = new DrawingVisual();
        var bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        using (DrawingContext drawing = composed.RenderOpen())
            drawing.DrawRectangle(window.Background, null, bounds);
        bitmap.Render(composed);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class Phase3OffscreenEvidenceFactAttribute : FactAttribute
{
    internal const string OutputVariable = "MYCAPTURE_PHASE3_OFFSCREEN_EVIDENCE";
    public Phase3OffscreenEvidenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputVariable)))
            Skip = "Set MYCAPTURE_PHASE3_OFFSCREEN_EVIDENCE to a new absolute output directory for retained synthetic WPF window renders.";
    }
}
