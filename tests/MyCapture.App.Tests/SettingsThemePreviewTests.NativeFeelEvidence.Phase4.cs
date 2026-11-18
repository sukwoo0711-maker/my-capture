using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string Phase4SourceVariable = "MYCAPTURE_PHASE4_VIDEO_SOURCE";

    [NativeFeelSceneGroup(94)]
    [NativeFeelScene("phase4-video-collapsed-ko", "video-editor")]
    [NativeFeelScene("phase4-video-expanded-ko", "video-editor")]
    [NativeFeelScene("phase4-video-narrow-ko", "video-editor")]
    [NativeFeelScene("phase4-gif-export-ko", "video-export-dialog")]
    [NativeFeelScene("phase4-video-collapsed-en", "video-editor")]
    [NativeFeelScene("phase4-video-expanded-en", "video-editor")]
    [NativeFeelScene("phase4-video-narrow-en", "video-editor")]
    [NativeFeelScene("phase4-gif-export-en", "video-export-dialog")]
    private static void NativeFeelPhase4Video(NativeFeelSceneContext context)
    {
        RecordingResult recording = Phase4Recording(context.Profile);
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            using var culture = new Phase4EvidenceLanguage(language);
            string tag = language[..2];
            VideoEditorWindow editor = Phase4Editor(recording, context.Profile, context.Desktop.Monitor);
            try
            {
                context.Desktop.Place(editor, 1180, 760);
                editor.Show();
                Phase4AwaitEditor(editor);
                context.Desktop.Still($"phase4-video-collapsed-{tag}", "phase4-video", editor, Phase4NativeNote(language));
                editor.SetDetailsExpandedForTest(true);
                context.Desktop.Still($"phase4-video-expanded-{tag}", "phase4-video", editor, Phase4NativeNote(language));
                editor.Width = 760;
                editor.Height = 555;
                context.Desktop.Still($"phase4-video-narrow-{tag}", "phase4-video", editor, Phase4NativeNote(language));
            }
            finally { editor.Close(); }

            VideoExportDialog dialog = Phase4Export(recording, context.Desktop.Monitor);
            try
            {
                context.Desktop.Place(dialog);
                dialog.Show();
                Phase4AwaitGif(dialog);
                context.Desktop.Still($"phase4-gif-export-{tag}", "phase4-video", dialog, Phase4NativeNote(language));
            }
            finally { Phase4CloseExport(dialog); }
        }
    }

    private static string Phase4NativeNote(string language) =>
        $"{language}; retained synthetic 640x360, 15 fps document clip. Actual product windows and native desktop pixels; "
        + "the editor is paused at 1 s and the GIF sample at its first frame. No native input, recording or final export.";

    /// <summary>
    /// Safe client-render comparison when desktop capture is unavailable. Both sides use this exact
    /// partial and the same source clip. These are WPF client renders, not native desktop screenshots.
    /// </summary>
    [Phase4VideoEvidenceFact]
    public Task Phase4Video_RenderComparisonMatrix() => RunIsolated(nameof(Phase4Video_RenderComparisonMatrix),
        RenderPhase4VideoMatrix, hungAfter: TimeSpan.FromMinutes(8));

    private static void RenderPhase4VideoMatrix()
    {
        string requested = Environment.GetEnvironmentVariable(Phase4VideoEvidenceFactAttribute.OutputVariable)
            ?? throw new InvalidOperationException("Phase 4 video evidence was not requested.");
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("Choose a new absolute evidence directory.");
        string output = TestDiagnosticArtifacts.NewDirectory(requested);
        string profile = OwnedTestDirectory.Create("mc-phase4-video-evidence-");
        var images = new List<object>();
        var application = CreateLibraryApplication();
        bool? previousHighContrast = ThemeService.SystemHighContrastOverrideForTest;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string? failure = null;
        RecordingResult? recording = null;
        try
        {
            ThemeService.SystemHighContrastOverrideForTest = false;
            recording = Phase4Recording(output);
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            foreach (string language in new[] { "ko-KR", "en-US" })
            foreach (double scale in new[] { 1d, 1.5d })
            {
                using var culture = new Phase4EvidenceLanguage(language);
                ThemeService.Apply(theme);
                string palette = theme == AppTheme.Glass ? "dark" : "light";
                string suffix = $"{palette}-{language[..2]}-scale{scale * 100:0}";
                RecordIsolatedProgress("phase4 " + suffix);
                // An injected, offscreen work area preserves the real dialog's sizing path without
                // changing the desktop settings or consulting the native cursor position.
                var bounds = new RectD(-32000, -32000, 1920 * scale, 1080 * scale);
                var monitor = new MonitorInfo("phase4-offscreen", bounds, bounds, (uint)(96 * scale), true);
                VideoEditorWindow editor = Phase4Editor(recording, profile, monitor);
                try
                {
                    Phase4PlaceOffscreen(editor, monitor, width: 1180, height: 760);
                    editor.Show();
                    VisualTreeHelper.SetRootDpi(editor, new DpiScale(scale, scale));
                    Phase4AwaitEditor(editor);
                    Save(editor, "video-collapsed");
                    editor.SetDetailsExpandedForTest(true);
                    Save(editor, "video-expanded");
                    editor.Width = 760;
                    editor.Height = 555;
                    Save(editor, "video-narrow");
                }
                finally { editor.Close(); }

                VideoExportDialog dialog = Phase4Export(recording, monitor);
                try
                {
                    Phase4PlaceOffscreen(dialog, monitor);
                    dialog.Show();
                    VisualTreeHelper.SetRootDpi(dialog, new DpiScale(scale, scale));
                    Phase4AwaitGif(dialog);
                    Save(dialog, "gif-export");
                }
                finally { Phase4CloseExport(dialog); }

                void Save(Window window, string scene)
                {
                    window.UpdateLayout();
                    NativeFeelDesktopCapture.Pump(100);
                    var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    Assert.Equal(scale, VisualTreeHelper.GetDpi(root).DpiScaleX);
                    Assert.Equal(scale, VisualTreeHelper.GetDpi(root).DpiScaleY);
                    Assert.True(root.ActualWidth > 0 && root.ActualHeight > 0);
                    BitmapSource image = Phase4RenderClient(window, scale);
                    string name = $"{scene}-{suffix}.png";
                    TestDiagnosticArtifacts.SavePng(output, name, image);
                    images.Add(new
                    {
                        scene, file = name, theme = palette, language, scale,
                        clientWidthDip = root.ActualWidth, clientHeightDip = root.ActualHeight,
                        windowWidthDip = window.ActualWidth, windowHeightDip = window.ActualHeight,
                        contentOffsetX = root.TranslatePoint(new Point(), window).X,
                        contentOffsetY = root.TranslatePoint(new Point(), window).Y,
                        languageProbe = Phase4LanguageProbe(window),
                        pixelWidth = image.PixelWidth, pixelHeight = image.PixelHeight,
                        detailsExpanded = window is VideoEditorWindow video && video.AreDetailsExpandedForTest,
                        estimate = window is VideoExportDialog export ? export.EstimateText : null,
                        sampleInfo = window is VideoExportDialog gif ? gif.SamplePanel.InfoText : null,
                        sampleCompleted = window is VideoExportDialog completed ? completed.SampleCompletion.IsCompletedSuccessfully : (bool?)null,
                    });
                }
            }
            Assert.Equal(32, images.Count);
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try
            {
                TestDiagnosticArtifacts.WriteJson(output, "manifest.json", new
                {
                    schema = 1, capture = "WPF RenderTargetBitmap of the real Window visual over its actual Window.Background; no native title bar or shadow",
                    nativeDesktop = false, dpi = "VisualTreeHelper.SetRootDpi; no OS display changes",
                    source = recording?.OutputPath,
                    sourceSha256 = recording is null ? null : TestDiagnosticArtifacts.HashFile(recording.OutputPath),
                    sourceDescription = "Synthetic document scroll, 640x360, 15 fps, 4 seconds; no user content",
                    fixtureProfile = profile, fixtureRetention = "Retained for reproduction; no cleanup is attempted.",
                    expectedImages = 32, images, failure,
                });
            }
            finally
            {
                foreach (Window window in application.Windows.Cast<Window>().ToArray())
                    if (window is VideoExportDialog export) Phase4CloseExport(export);
                    else window.Close();
                ThemeService.SystemHighContrastOverrideForTest = previousHighContrast;
                application.Shutdown();
            }
        }
    }

    private static RecordingResult Phase4Recording(string directory)
    {
        string? shared = Environment.GetEnvironmentVariable(Phase4SourceVariable);
        if (!string.IsNullOrWhiteSpace(shared))
        {
            string source = TestDiagnosticArtifacts.ExistingFile(shared);
            Assert.InRange(MediaFoundationVideoFrameReader.ReadContainerDurationMs(source), 3999, 4001);
            return new RecordingResult(source, 4000, 15, 60, 640, 360);
        }
        string path = DiagnosticOutputPaths.Child(directory, "phase4-document-scroll.mp4");
        if (File.Exists(path)) return new RecordingResult(path, 4000, 15, 60, 640, 360);
        return GifPreviewFixtureClips.Encode(path, GifPreviewFixtureClips.Kind.DocumentScroll, 640, 360, 15, 4000);
    }

    private static VideoEditDocument Phase4Document(RecordingResult recording)
    {
        var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        document.TextOverlays.Add(new TimedTextOverlay
        {
            Text = "Review 01", StartMs = 0, EndMs = recording.DurationMs,
            Bounds = new VideoLayerBounds(0.2, 0.7, 0.6, 0.2),
        });
        return document;
    }

    private static VideoEditorWindow Phase4Editor(RecordingResult recording, string profile, MonitorInfo monitor)
    {
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(profile), NullLoggerFactory.Instance,
            Phase4Document(recording), pointerMonitor: monitor) { ShowActivated = false, ShowInTaskbar = false };
        FluidMotion.SetWindowEntrance(editor, false);
        return editor;
    }

    private static VideoExportDialog Phase4Export(RecordingResult recording, MonitorInfo monitor)
    {
        var dialog = new VideoExportDialog(recording, Phase4Document(recording), NullLoggerFactory.Instance, gif: true)
        {
            ShowActivated = false, ShowInTaskbar = false, MonitorForTest = () => monitor,
        };
        FluidMotion.SetWindowEntrance(dialog, false);
        return dialog;
    }

    private static void Phase4PlaceOffscreen(Window window, MonitorInfo monitor, double? width = null, double? height = null)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = monitor.WorkArea.Left / monitor.ScaleFactor + 20;
        window.Top = monitor.WorkArea.Top / monitor.ScaleFactor + 20;
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
    }

    private static void Phase4AwaitEditor(VideoEditorWindow editor)
    {
        Assert.True(RangePatternClip.PumpUntil(() => editor.IsMediaReadyForTest && editor.HasMediaOpenedForTest
            || editor.HasMediaFailedForTest, TimeSpan.FromSeconds(30)), "The retained synthetic clip did not open.");
        Assert.False(editor.HasMediaFailedForTest, editor.MediaFailureForTest);
        Assert.Equal(640, editor.MediaForTest.NaturalVideoWidth);
        Assert.Equal(360, editor.MediaForTest.NaturalVideoHeight);
        editor.TimelineForTest.SeekFromOverview(1000);
        Assert.True(RangePatternClip.PumpUntil(() => !editor.PreviewSeekCoordinatorForTest.IsSeeking,
            TimeSpan.FromSeconds(15)), "The editor's one-second seek did not settle.");
        Assert.Null(editor.PreviewSeekCoordinatorForTest.LastError);
        Assert.InRange(editor.PreviewSeekCoordinatorForTest.PresentedPositionMs, 999, 1001);
        Assert.False(editor.AreDetailsExpandedForTest);
        editor.UpdateLayout();
        _ = Phase4LanguageProbe(editor);
    }

    private static void Phase4AwaitGif(VideoExportDialog dialog)
    {
        Assert.True(RangePatternClip.PumpUntil(() => dialog.SamplePanel.HasSample,
            TimeSpan.FromSeconds(45)), "The real GIF sample did not arrive: " + dialog.SamplePanel.MessageText);
        // Publishing the preview precedes the estimate-refinement work. Capture the summary only
        // after that same task settles, so elapsed worker time cannot change the comparison state.
        Assert.True(RangePatternClip.PumpUntil(() => dialog.SampleCompletion.IsCompleted,
            TimeSpan.FromSeconds(45)), "The GIF estimate refinement did not settle.");
        dialog.SampleCompletion.GetAwaiter().GetResult();
        Assert.True(dialog.SampleCompletion.IsCompletedSuccessfully);
        Assert.True(dialog.SamplePanel.FrameCount > 0);
        if (dialog.SamplePanel.IsPlaying) dialog.SamplePanel.TogglePlayback();
        Slider slider = Descendants(dialog.SamplePanel).OfType<Slider>().Single();
        slider.Value = 0;
        Assert.False(dialog.SamplePanel.IsPlaying);
        Assert.Equal(0, dialog.SamplePanel.FrameIndex);
        Assert.True(dialog.CanExport);
        dialog.UpdateLayout();
        _ = Phase4LanguageProbe(dialog);
    }

    private static string Phase4LanguageProbe(Window window)
    {
        if (window is VideoEditorWindow editor)
        {
            var text = Assert.IsType<TextBlock>(typeof(VideoEditorWindow)
                .GetField("_removedLengthLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
            Assert.Equal(UiText.Get("VideoEditorUx_NotTrimmed"), text.Text);
            return text.Text;
        }
        var dialog = Assert.IsType<VideoExportDialog>(window);
        string expectedPrefix = UiText.Get("GifPreview_Info").Split('{')[0];
        Assert.False(string.IsNullOrWhiteSpace(expectedPrefix));
        Assert.StartsWith(expectedPrefix, dialog.SamplePanel.InfoText, StringComparison.Ordinal);
        return dialog.SamplePanel.InfoText;
    }

    private sealed class Phase4EvidenceLanguage : IDisposable
    {
        private readonly string _previousLanguage = CultureInfo.DefaultThreadCurrentUICulture?.Name ?? UiText.Culture.Name;
        private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo? _previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        private readonly IDisposable _scope;

        internal Phase4EvidenceLanguage(string language)
        {
            // Dispatcher/media callbacks need the process setting as well as the calling context.
            // The evidence runs in its own process and restores both settings before another scene.
            UiText.Configure(language);
            _scope = UiText.UseLanguage(language);
        }

        public void Dispose()
        {
            _scope.Dispose();
            UiText.Configure(_previousLanguage);
            CultureInfo.CurrentUICulture = _previousUiCulture;
            CultureInfo.DefaultThreadCurrentUICulture = _previousDefaultUiCulture;
        }
    }

    private static void Phase4CloseExport(VideoExportDialog dialog)
    {
        bool closed = false;
        EventHandler onClosed = (_, _) => closed = true;
        dialog.Closed += onClosed;
        try
        {
            dialog.Close();
            Assert.True(RangePatternClip.PumpUntil(() => closed, TimeSpan.FromSeconds(45)),
                "The export dialog did not finish draining its preview workers and close.");
        }
        finally { dialog.Closed -= onClosed; }
    }

    private static BitmapSource Phase4RenderClient(Window window, double scale)
    {
        // Render the actual Window root, as SaveGifPreviewEvidence does. Rendering Content alone
        // retains its parent offset but omits the Window fill and the room reserved for its margins.
        // The complete WPF root preserves those margins and the rightmost controls without cropping.
        var size = new Size(window.ActualWidth, window.ActualHeight);
        Assert.Equal(scale, VisualTreeHelper.GetDpi(window).DpiScaleX);
        Assert.Equal(scale, VisualTreeHelper.GetDpi(window).DpiScaleY);
        SolidColorBrush background = Assert.IsType<SolidColorBrush>(window.Background);
        Assert.Equal(255, background.Color.A);
        int pixelWidth = (int)Math.Ceiling(size.Width * scale);
        int pixelHeight = (int)Math.Ceiling(size.Height * scale);
        var backdrop = new DrawingVisual();
        VisualTreeHelper.SetRootDpi(backdrop, new DpiScale(scale, scale));
        using (DrawingContext drawing = backdrop.RenderOpen())
        {
            // SizeToContent can end on a fractional pixel. Fill the final raster pixel too;
            // the product visual below retains its exact unrounded dimensions.
            drawing.DrawRectangle(background, null, new Rect(0, 0, pixelWidth / scale, pixelHeight / scale));
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(window);
        byte[] pixel = new byte[4];
        foreach (var point in new[] { new Int32Rect(0, 0, 1, 1), new Int32Rect(bitmap.PixelWidth - 1, 0, 1, 1),
                     new Int32Rect(0, bitmap.PixelHeight - 1, 1, 1), new Int32Rect(bitmap.PixelWidth - 1, bitmap.PixelHeight - 1, 1, 1) })
        {
            bitmap.CopyPixels(point, pixel, 4, 0);
            Assert.Equal(255, pixel[3]);
        }
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class Phase4VideoEvidenceFactAttribute : FactAttribute
{
    internal const string OutputVariable = "MYCAPTURE_PHASE4_VIDEO_EVIDENCE";

    public Phase4VideoEvidenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputVariable)))
            Skip = "Set MYCAPTURE_PHASE4_VIDEO_EVIDENCE to a new absolute output directory for the opt-in WPF client renders.";
    }
}
