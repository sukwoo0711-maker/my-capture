using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Korean UI copy in real window content, laid out offscreen without showing a window.
/// Content exclusions live in this fixture instead of adding test-only flags to product controls.
/// </summary>
public sealed class KoreanWordWrapSweepTests
{
    [Fact]
    public Task KoreanWindows_DefaultAndMinimumSizes_HaveNoOrphansOrUnjoinedCopy() =>
        SettingsThemePreviewTests.RunIsolated(nameof(KoreanWindows_DefaultAndMinimumSizes_HaveNoOrphansOrUnjoinedCopy),
            () => Run(null, verify: true), typeof(KoreanWordWrapSweepTests));

    /// <summary>
    /// Opt-in evidence: MYCAPTURE_KOREAN_WRAP_EVIDENCE=before|after|after-consolas renders every
    /// surface into artifacts/korean-wrap/before, /after or /after-consolas (the switch hands on a
    /// literal, so the variable chooses between fixed directories and never supplies a path).
    /// "after-consolas" draws the monospace text in Consolas: the "before" set came from a run in
    /// which Cascadia Mono was not available, so key caps and time readouts are wider in "after".
    /// </summary>
    [Fact]
    public Task RenderBeforeAndAfterEvidence()
    {
        string? phase = Environment.GetEnvironmentVariable("MYCAPTURE_KOREAN_WRAP_EVIDENCE") switch
        {
            "before" => "before",
            "after" => "after",
            "after-consolas" => "after-consolas",
            _ => null,
        };
        if (phase is null) return Task.CompletedTask;
        return SettingsThemePreviewTests.RunIsolated(nameof(RenderBeforeAndAfterEvidence),
            () => Run(phase, verify: false), typeof(KoreanWordWrapSweepTests));
    }

    private static void Run(string? phase, bool verify)
    {
        string evidence = Path.Combine(OffscreenSurfaceSweep.RepositoryRoot(), "artifacts", "korean-wrap");
        string? output = phase is null ? null : DiagnosticOutputPaths.Create(Path.Combine(evidence, phase));
        using var language = UiText.UseLanguage("ko-KR");
        UiText.Configure("ko-KR");
        Application application = OffscreenSurfaceSweep.CreateApplication();
        if (phase == "after-consolas") application.Resources["Font.Mono"] = new FontFamily("Consolas, Malgun Gothic, monospace");
        ThemeService.Apply(AppTheme.Midnight);
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var failures = new List<string>();
        int wrappingCopy = 0;
        var sweep = new OffscreenSurfaceSweep(application, layout => wrappingCopy += OffscreenSurfaceSweep.CheckKoreanWrap(layout, failures))
        { Output = output, ConsolePrefix = "KOREAN WRAP", LanguageName = "Korean" };
        // All persistence services point at this fixture root; no real application profile is read.
        using var fixtures = new OffscreenSurfaceSweep.Fixtures(evidence, Path.Combine(evidence, "fixtures"),
            OffscreenSurfaceSweep.FixtureText.Korean);

        try
        {
            sweep.DefaultSurfaces(fixtures, verify);
            Console.WriteLine($"KOREAN WRAP SWEEP: {sweep.Layouts} layouts; {wrappingCopy} wrapping Korean UI blocks; {failures.Count} findings.");
            foreach (string failure in failures) Console.WriteLine(failure);
            if (output is not null)
                File.WriteAllText(DiagnosticOutputPaths.Child(output, "findings.json"), JsonSerializer.Serialize(new
                { phase, surfaces = sweep.Layouts, wrappingCopy, findings = failures }, new JsonSerializerOptions { WriteIndented = true }));
            if (verify) Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
            application.Shutdown();
        }
    }
}
