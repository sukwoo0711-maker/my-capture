using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Gallery;
using MyCapture.App.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioMediaDetailsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DetailsProbeActualFileAndFallbackWithoutAudioDecoder(bool audio, bool fallback)
    {
        string directory = OwnedTestDirectory.Create("mc-audio-details-");
        string path = Path.Combine(directory, "synthetic.mp4");
        RecordingAudioEditingTests.Encode(path, audio ? 3 : 0);
        long decoders = MediaFoundationAudioReader.OpenedReaders;
        int leases = MediaFoundationPlatform.Lifetime.ActiveLeases;
        var source = fallback
            ? new MediaDetailsSource(Path.Combine(directory, "missing.mp4"), 64, 64, 10, FallbackFilePath: path)
            : new MediaDetailsSource(path, 64, 64, 3000);
        MediaDetails details = Assert.Single(await MediaDetailsReader.ReadAsync([source]));
        Assert.Equal(path, details.FilePath);
        Assert.False(details.FileUnavailable);
        Assert.NotNull(details.Audio);
        Assert.Equal(audio, details.Audio.Format is not null);
        if (audio)
        {
            Assert.Equal(new Guid("00001610-0000-0010-8000-00AA00389B71"), details.Audio.Format!.Subtype);
            Assert.Equal(48_000, details.Audio.Format.SampleRate);
            Assert.Equal(2, details.Audio.Format.Channels);
        }
        Assert.Equal(3000, details.Source.DurationMs);
        Assert.Equal(decoders, MediaFoundationAudioReader.OpenedReaders);
        Assert.Equal(leases, MediaFoundationPlatform.Lifetime.ActiveLeases);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Retained under the owned run root; this round does not remove its generated files.
    }

    [Fact]
    public async Task UnreadableTrackRemainsUnknownRatherThanNoAudioAndImageHasNoAudioFact()
    {
        string directory = OwnedTestDirectory.Create("mc-audio-details-unreadable-");
        string damaged = Path.Combine(directory, "damaged.mp4");
        File.WriteAllBytes(damaged, [0, 0, 0]);
        MediaDetails details = MediaDetailsReader.Read(new(damaged, 64, 64, 3000));
        Assert.Null(details.Audio);
        Assert.False(details.FileUnavailable);
        Assert.Equal(3, details.SizeBytes);
        using var language = UiText.UseLanguage("en-US");
        Assert.Equal(UiText.Get("MediaInfo.Unavailable"), MediaDetailsWindow.AudioText(details));
        Assert.NotEqual(UiText.Get("Recording_NoAudio"), MediaDetailsWindow.AudioText(details));
        MediaDetails missing = Assert.Single(await MediaDetailsReader.ReadAsync([new(Path.Combine(directory, "missing.mp4"))]));
        Assert.True(missing.FileUnavailable);
        Assert.Null(missing.Audio);
        MediaDetails image = MediaDetailsReader.Read(new(Path.Combine(directory, "missing.png"), 64, 64));
        Assert.Null(image.Audio);
        Assert.Equal("PNG", image.Format);
    }

    [Theory]
    [InlineData("ko-KR", "AAC 48 kHz 스테레오", "소리 없음", "모노", "6채널")]
    [InlineData("en-US", "AAC 48 kHz stereo", "No audio", "mono", "6 channels")]
    public void AudioFactUsesProbedCodecRateAndChannels(string language, string expected, string silent,
        string mono, string multichannel)
    {
        using var scope = UiText.UseLanguage(language);
        var audio = new AudioSourceInfo(1, new Guid("00001610-0000-0010-8000-00AA00389B71"), 48_000, 2, 0);
        var details = new MediaDetails(new(null), "", null, null, null, 64, 64, "MP4", false, new(audio));
        Assert.Equal(expected, MediaDetailsWindow.AudioText(details));
        Assert.Equal(silent, MediaDetailsWindow.AudioText(details with { Audio = new(null) }));
        Assert.Equal("AAC 44.1 kHz " + mono, MediaDetailsWindow.AudioText(details with
            { Audio = new(audio with { SampleRate = 44_100, Channels = 1 }) }));
        Assert.Equal("AAC 48 kHz " + multichannel, MediaDetailsWindow.AudioText(details with
            { Audio = new(audio with { Channels = 6 }) }));
        Assert.Equal(UiText.Get("MediaInfo.AudioPresent"), MediaDetailsWindow.AudioText(details with
            { Audio = new(audio with { Subtype = Guid.Empty, SampleRate = 0, Channels = 0 }) }));
    }

    [Fact]
    public Task DetailsWindowAndLibraryRowsWithSyntheticAudioAndSilentVideo() =>
        SettingsThemePreviewTests.RunIsolated(nameof(DetailsWindowAndLibraryRowsWithSyntheticAudioAndSilentVideo), () =>
        {
            string root = OwnedTestDirectory.Create("mc-audio-details-evidence-");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            ThemeService.Apply(AppTheme.Daylight);
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var scope = UiText.UseLanguage(language);
                    ThemeService.Apply(AppTheme.Daylight);
                    AppPaths paths = AppPaths.CreateForRoot(Path.Combine(root, language));
                    paths.EnsureCreated();
                    var settings = new AppSettings();
                    using ProbeLibraryServices services = ProbeLibraryServices.Initialize(paths, settings);
                    CaptureRecord[] records = [AddVideo(true), AddVideo(false)];
                    services.EnsureWindow();
                    GalleryWindow gallery = services.Window!;
                    gallery.ShowActivated = false;
                    gallery.ShowInTaskbar = false;
                    gallery.WindowStartupLocation = WindowStartupLocation.Manual;
                    gallery.Left = -10000;
                    gallery.Top = -10000;
                    gallery.Show();
                    gallery.SetDetailsPaneOpen(true);
                    try
                    {
                        foreach (CaptureRecord record in records)
                        {
                            bool hasAudio = ReferenceEquals(record, records[0]);
                            string state = hasAudio ? "audio" : "silent";
                            string expected = hasAudio ? (language == "ko-KR" ? "AAC 48 kHz 스테레오" : "AAC 48 kHz stereo")
                                : UiText.Get("Recording_NoAudio");
                            services.ViewModel!.Select(record.Id);
                            gallery.UpdateLayout();
                            PumpUntil(() => gallery.PendingDetailsAudioRead.IsCompleted);
                            gallery.PendingDetailsAudioRead.GetAwaiter().GetResult();
                            FrameworkElement panel = Assert.IsType<Grid>(gallery.FindName("GalleryDetailsContent"));
                            TextBlock row = Descendants(panel).OfType<TextBlock>().Single(element =>
                                AutomationProperties.GetAutomationId(element) == "LibraryDetailsAudio");
                            Assert.True(row.IsVisible);
                            Assert.Equal(UiText.Get("MediaInfo.Audio") + ": " + expected, row.Text);
                            Assert.Equal(row.Text, new TextBlockAutomationPeer(row).GetName());
                            Assert.True(row.ActualWidth > 0 && row.ActualHeight > 0);
                            Save(root, $"details-panel-{language}-{state}-100.png", panel);

                            MediaDetails details = MediaDetailsReader.Read(new(services.Queue.GetFilePath(record,
                                CaptureFileNames.VideoSource), 64, 64, 3000));
                            var window = new MediaDetailsWindow([details]);
                            try
                            {
                                FrameworkElement content = Assert.IsType<ScrollViewer>(window.Content);
                                content.Measure(new Size(500, double.PositiveInfinity));
                                content.Arrange(new Rect(0, 0, 500, content.DesiredSize.Height));
                                content.UpdateLayout();
                                TextBox fact = Descendants(content).OfType<TextBox>().Single(text =>
                                    AutomationProperties.GetName(text) == UiText.Get("MediaInfo.Audio"));
                                Assert.Equal(expected, fact.Text);
                                Assert.True(fact.ActualWidth > 0 && fact.ExtentWidth <= fact.ViewportWidth);
                                Save(root, $"details-window-{language}-{state}-100.png", content);
                            }
                            finally { window.Close(); }
                        }
                        // Two reads requested without yielding must finish with the current record's text.
                        services.ViewModel!.Select(records[0].Id);
                        services.ViewModel.Select(records[1].Id);
                        gallery.UpdateLayout();
                        PumpUntil(() => gallery.PendingDetailsAudioRead.IsCompleted);
                        TextBlock current = Descendants((FrameworkElement)gallery.FindName("GalleryDetailsContent"))
                            .OfType<TextBlock>().Single(row => AutomationProperties.GetAutomationId(row) == "LibraryDetailsAudio");
                        Assert.EndsWith(UiText.Get("Recording_NoAudio"), current.Text);
                        VerifySameRecordEditAndLateRead(services, gallery, records[1], current);
                        gallery.SetDetailsPaneOpen(false);
                        Assert.Empty(current.Text);
                        Assert.Empty(new TextBlockAutomationPeer(current).GetName());
                    }
                    finally { gallery.CloseForExit(); services.Presenter.Dispose(); }

                    CaptureRecord AddVideo(bool audio)
                    {
                        var record = new CaptureRecord { MediaKind = CaptureMediaKind.Video,
                            RelativeDirectory = Guid.NewGuid().ToString("N"), Width = 64, Height = 64,
                            DurationMs = 3000, Title = "Synthetic recording", CreatedAt = DateTimeOffset.Now };
                        Directory.CreateDirectory(services.Queue.GetDirectory(record));
                        RecordingAudioEditingTests.Encode(services.Queue.GetFilePath(record, CaptureFileNames.VideoSource), audio ? 3 : 0);
                        var thumbnail = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null,
                            Enumerable.Repeat((byte)150, 64 * 64 * 4).ToArray(), 64 * 4);
                        TestDiagnosticArtifacts.SavePng(services.Queue.GetDirectory(record), CaptureFileNames.Thumbnail, thumbnail);
                        services.Queue.Add(record);
                        return record;
                    }
                }
                Assert.Equal(8, Directory.GetFiles(root, "*.png").Length);
                Console.WriteLine("Recording audio details evidence: " + root);
            }
            finally { application.Shutdown(); FluidMotion.AnimationsEnabledOverrideForTest = null; }
        }, typeof(RecordingAudioMediaDetailsTests));

    private static void VerifySameRecordEditAndLateRead(ProbeLibraryServices services, GalleryWindow gallery,
        CaptureRecord record, TextBlock row)
    {
        GalleryItemViewModel selected = services.ViewModel!.SingleSelectedTile!;
        Assert.Same(record, selected.Record);
        Assert.EndsWith(UiText.Get("Recording_NoAudio"), row.Text);
        var readFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gallery.SetDetailsPaneOpen(false);
        gallery.BeforeDetailsAudioPublishForTest = () =>
        {
            readFinished.TrySetResult();
            return releaseOld.Task;
        };
        gallery.SetDetailsPaneOpen(true);
        gallery.UpdateLayout();
        Assert.Equal(UiText.Get("MediaInfo.Audio") + ": " + UiText.Get("LibraryRecovery_Loading"),
            new TextBlockAutomationPeer(row).GetName());
        Task oldRead = gallery.PendingDetailsAudioRead;
        PumpUntil(() => readFinished.Task.IsCompleted);
        Assert.False(oldRead.IsCompleted);

        // The actual native read has finished on the source-only, silent MP4. Publish a new real
        // AAC render on the same selected record before that earlier read can publish its facts.
        gallery.BeforeDetailsAudioPublishForTest = null;
        string render = services.Queue.GetFilePath(record, CaptureFileNames.VideoRendered);
        RecordingAudioEditingTests.Encode(render);
        record.ContentRevision++;
        selected.RaiseMetaChanged();
        Task editedRead = gallery.PendingDetailsAudioRead;
        Assert.NotSame(oldRead, editedRead);
        Assert.Same(selected, services.ViewModel.SingleSelectedTile);
        PumpUntil(() => editedRead.IsCompleted);
        editedRead.GetAwaiter().GetResult();
        string expected = UiText.Get("MediaInfo.Audio") + ": AAC 48 kHz " + UiText.Get("MediaInfo.AudioStereo");
        Assert.Equal(expected, row.Text);
        Assert.Equal(expected, new TextBlockAutomationPeer(row).GetName());
        releaseOld.SetResult();
        PumpUntil(() => oldRead.IsCompleted);
        oldRead.GetAwaiter().GetResult();
        Assert.Equal(expected, row.Text);

        // Retargeting the same record to another local source also refreshes without a revision
        // increment; unrelated caption/tag notifications leave the native read count unchanged.
        string previousDirectory = record.RelativeDirectory;
        record.RelativeDirectory = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(services.Queue.GetDirectory(record));
        RecordingAudioEditingTests.Encode(services.Queue.GetFilePath(record, CaptureFileNames.VideoSource), 0);
        selected.RaiseMetaChanged();
        Task movedRead = gallery.PendingDetailsAudioRead;
        Assert.NotSame(editedRead, movedRead);
        PumpUntil(() => movedRead.IsCompleted);
        movedRead.GetAwaiter().GetResult();
        Assert.Equal(UiText.Get("MediaInfo.Audio") + ": " + UiText.Get("Recording_NoAudio"), row.Text);
        Assert.Equal(row.Text, new TextBlockAutomationPeer(row).GetName());
        record.Tags = "updated tag";
        selected.RaiseMetaChanged();
        Assert.Same(movedRead, gallery.PendingDetailsAudioRead);
        Assert.NotEqual(previousDirectory, record.RelativeDirectory);
    }

    private static void Save(string root, string name, FrameworkElement visual)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // These are cropped content roots. Their window/panel owns the raised surface,
        // so composite that same theme brush before drawing the actual visual contents.
        Brush background = Assert.IsAssignableFrom<Brush>(visual.TryFindResource("Surface.Raised"));
        var surface = new DrawingVisual();
        using (DrawingContext drawing = surface.RenderOpen())
        {
            var bounds = new Rect(visual.RenderSize);
            drawing.DrawRectangle(background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(surface);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.True(Enumerable.Range(0, bitmap.PixelWidth * bitmap.PixelHeight).All(index => pixels[index * 4 + 3] == 255),
            "Daylight details evidence must retain its opaque product surface.");
        TestDiagnosticArtifacts.SavePng(root, name, bitmap);
    }

    private static void PumpUntil(Func<bool> complete)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!complete() && DateTime.UtcNow < deadline) RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(complete(), "The details metadata read did not finish.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
