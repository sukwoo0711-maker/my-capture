using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// The recording editor clips: the real VideoEditorWindow on a synthetic recording created in the
/// output folder, driven through the editor's own shortcuts (Space, comma, period, I, O, G), its
/// trim strip and its buttons. The GIF clip opens the real VideoExportDialog and runs its real
/// export into the output folder; only the Windows save dialog, which is OS UI, is drawn.
/// </summary>
internal sealed class VideoScene : TutorialScene
{
    internal static readonly string[] Features = ["video-play", "video-frame-step", "video-trim", "video-gif-export"];

    /// <summary>The editor is laid out at 1190×560 DIP (its minimum height is 555) and drawn at 80%.</summary>
    private const double EditorScale = 0.8, ClientWidth = 1190, ClientHeight = 560;
    private static readonly Point ClientOrigin = new((Stage.Width - ClientWidth * EditorScale) / 2, 4 + Stage.TitleBarHeight);

    /// <summary>The export dialog keeps its 640 DIP width and the height the scene leaves, drawn at 90%.</summary>
    private const double DialogScale = 0.9, DialogWidth = 640, DialogHeight = 496;
    private static readonly Point DialogOrigin = new((Stage.Width - DialogWidth * DialogScale) / 2, 4 + Stage.TitleBarHeight);

    private const int VideoWidth = 960, VideoHeight = 540, VideoFps = 25, VideoFrames = 200;
    private const double FrameMs = 1000.0 / VideoFps;

    private readonly string _feature;
    private readonly string _work;
    private readonly RecordingResult _recording;
    private readonly VideoEditorWindow _editor;
    private readonly OffscreenWindow _host;
    private readonly MediaElement _media;
    private readonly TwoLineTimeline _timeline;
    private TaskCompletionSource<bool>? _commit;
    private bool _closed;
    private VideoExportDialog? _dialog;
    private OffscreenWindow? _dialogHost;
    private bool _saveAsVisible;
    private string? _exportPath;
    private Task? _export;
    private bool? _motionBefore;
    private double _playStartMs;

    internal VideoScene(string id, string language) : base(id, language)
    {
        _feature = id["video-".Length..];
        _work = Path.Combine(Program.WorkFolder, $"{id}.{language}");
        if (Directory.Exists(_work)) global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(_work, recursive: true);
        Directory.CreateDirectory(_work);
        string name = "MyCapture_20261013_093012.mp4";
        _recording = CreateRecording(Path.Combine(_work, name), Korean);
        VideoEditDocument? document = null;
        if (_feature == "gif-export")
        {
            document = VideoEditDocument.CreateFor(_recording.Width, _recording.Height, _recording.DurationMs);
            document.TrimInMs = 1200;
            document.TrimOutMs = 5200;
        }

        _editor = new VideoEditorWindow(_recording, AppPaths.CreateForRoot(Path.Combine(_work, "profile")), NullLoggerFactory.Instance, document)
        {
            VideoCommitHandler = (_, _, _) => (_commit ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task,
            ExportDialogHostForTest = HostExportDialog,
        };
        _host = new OffscreenWindow(_editor, ClientWidth, ClientHeight);
        if (!WaitUntil(() => _editor.IsMediaReadyForTest || _editor.HasMediaFailedForTest, 12000) || _editor.HasMediaFailedForTest)
            throw new InvalidOperationException($"The video editor did not open the synthetic recording: {_editor.MediaFailureForTest}");
        _media = _host.Find<MediaElement>().Single();
        _timeline = _editor.TimelineForTest;
        // The trim strip draws thumbnails of the recording; give the filmstrip time to decode them.
        Stage.Pump(1500);
        WaitForSeek();
        Story = BuildStory();
    }

    internal override string Provenance =>
        "Real VideoEditorWindow (command bar, preview, transport, trim strip and trim bar, status line) on a synthetic 8 s recording "
        + "written to the output folder, driven through HandleShortcut (Space, comma, period, I, O, G), the trim strip's pointer path "
        + "and the Save to library button; playback position is set per frame for a steady clip. The GIF clip opens the real "
        + "VideoExportDialog and runs its export into the output folder; the Windows save dialog is drawn. Library commits are intercepted.";

    // ---- Synthetic recording ----------------------------------------------------------------

    private static readonly Brush Paper = Frozen(Color.FromRgb(0xF6, 0xF8, 0xFB));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0x1F, 0x24, 0x2E));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x5B, 0x64, 0x73));
    private static readonly Brush Line = Frozen(Color.FromRgb(0xD5, 0xDB, 0xE5));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x2F, 0x6F, 0xEB));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// An 8 s, 25 fps recording with visible motion on every frame: a ball crossing a seconds
    /// track while it bounces, a progress bar and a frame counter, so playback, frame steps and
    /// trimming show what really changes.
    /// </summary>
    private static RecordingResult CreateRecording(string path, bool korean)
    {
        var options = new VideoEncoderOptions(path, VideoWidth, VideoHeight, VideoFps, 2_500_000) { AllowHardwareEncoder = false };
        using var encoder = new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance);
        byte[] pixels = new byte[VideoWidth * VideoHeight * 4];
        for (int index = 0; index < VideoFrames; index++)
        {
            RecordingFrame(index, korean).CopyPixels(pixels, VideoWidth * 4, 0);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            encoder.WriteFrame(new EncoderFrame(pixels, VideoWidth, VideoHeight, VideoWidth * 4, index * FrameMs));
        }

        encoder.Complete();
        return new RecordingResult(path, VideoFrames * FrameMs, VideoFps, VideoFrames, VideoWidth, VideoHeight);
    }

    private static BitmapSource RecordingFrame(int index, bool korean)
    {
        double seconds = index / (double)VideoFps;
        double total = VideoFrames / (double)VideoFps;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Paper, null, new Rect(0, 0, VideoWidth, VideoHeight));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, VideoWidth, 64));
            dc.DrawRectangle(Line, null, new Rect(0, 64, VideoWidth, 1));
            dc.DrawEllipse(Accent, null, new Point(36, 32), 9, 9);
            Fixtures.Text(dc, korean ? "모션 데모" : "Motion demo", 58, 14, 26, Ink, bold: true);

            var chip = new Rect(VideoWidth - 330, 92, 290, 66);
            dc.DrawRoundedRectangle(Ink, null, chip, 12, 12);
            string counter = (korean ? "프레임 " : "Frame ") + (index + 1).ToString("000", CultureInfo.InvariantCulture);
            FormattedText counterText = RecordingText(counter, 34, Brushes.White, bold: true);
            dc.DrawText(counterText, new Point(chip.X + (chip.Width - counterText.Width) / 2, chip.Y + (chip.Height - counterText.Height) / 2));
            FormattedText clock = RecordingText(TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture), 24, Muted, mono: true);
            dc.DrawText(clock, new Point(chip.X + (chip.Width - clock.Width) / 2, chip.Bottom + 10));


            const double left = 70, right = 890, lane = 372;
            dc.DrawRoundedRectangle(Line, null, new Rect(left, lane - 3, right - left, 6), 3, 3);
            for (int second = 0; second <= 8; second++)
            {
                double x = left + (right - left) * second / total;
                dc.DrawRectangle(Muted, null, new Rect(x - 1, lane + 8, 2, 12));
                FormattedText label = RecordingText(second.ToString(CultureInfo.InvariantCulture) + (korean ? "초" : "s"), 24, Muted, bold: true);
                dc.DrawText(label, new Point(x - label.Width / 2, lane + 24));
            }

            double ballX = left + (right - left) * seconds / total;
            double lift = Math.Abs(Math.Sin(seconds * Math.PI * 1.25)) * 150;
            const double radius = 30;
            double shadow = 1 - lift / 220;
            dc.DrawEllipse(Frozen(Color.FromArgb(0x40, 0x1F, 0x24, 0x2E)), null, new Point(ballX, lane), radius * shadow, 7 * shadow);
            dc.DrawEllipse(BallBrush(seconds / total), null, new Point(ballX, lane - radius - 4 - lift), radius, radius);

            var bar = new Rect(left, 468, right - left, 14);
            dc.DrawRoundedRectangle(Line, null, bar, 7, 7);
            dc.DrawRoundedRectangle(Accent, null, new Rect(bar.X, bar.Y, Math.Max(14, bar.Width * (index + 1) / VideoFrames), bar.Height), 7, 7);
            Fixtures.Text(dc, korean ? "진행" : "Progress", left, 428, 24, Muted);
            FormattedText percent = RecordingText(((index + 1) * 100 / VideoFrames).ToString(CultureInfo.InvariantCulture) + "%", 24, Ink, bold: true);
            dc.DrawText(percent, new Point(right - percent.Width, 428));
        }

        var bitmap = new RenderTargetBitmap(VideoWidth, VideoHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The ball changes from blue to green to orange along the recording.</summary>
    private static Brush BallBrush(double progress)
    {
        Color[] stops = [Color.FromRgb(0x2F, 0x6F, 0xEB), Color.FromRgb(0x16, 0xA3, 0x4A), Color.FromRgb(0xF5, 0x9E, 0x0B)];
        double scaled = Math.Clamp(progress, 0, 1) * (stops.Length - 1);
        int i = Math.Min(stops.Length - 2, (int)scaled);
        double f = scaled - i;
        Color a = stops[i], b = stops[i + 1];
        return Frozen(Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f)));
    }

    private static FormattedText RecordingText(string text, double size, Brush brush, bool bold = false, bool mono = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(mono ? Stage.MonoFont : Stage.UiFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, 1);

    // ---- Geometry ---------------------------------------------------------------------------

    private static PointD ToStage(PointD client) => new(ClientOrigin.X + client.X * EditorScale, ClientOrigin.Y + client.Y * EditorScale);

    private PointD Of(FrameworkElement element) => ToStage(_host.CenterOf(element));

    /// <summary>A point on the trim strip's track at <paramref name="ms"/> of the recording.</summary>
    private PointD Strip(double ms) =>
        ToStage(_host.PointOf(_timeline.TrimStripForTest, new Point(_timeline.OverviewPxForTest(ms), _timeline.OverviewTrackMiddleForTest)));

    private bool OverStrip(PointD stage)
    {
        Rect strip = _host.BoundsOf(_timeline.TrimStripForTest);
        var client = new Point((stage.X - ClientOrigin.X) / EditorScale, (stage.Y - ClientOrigin.Y) / EditorScale);
        return !_closed && strip.Contains(client);
    }

    private static PointD DialogPoint(PointD client) => new(DialogOrigin.X + client.X * DialogScale, DialogOrigin.Y + client.Y * DialogScale);

    private Button SaveToLibraryButton => _host.Named<Button>(UiText.Get("MediaExport_SaveEdits"));

    // ---- Storyboard -------------------------------------------------------------------------

    private TutorialStoryboard BuildStory()
    {
        var story = new TutorialStoryboard(ToStage(new PointD(ClientWidth * 0.42, ClientHeight * 0.3)));
        switch (_feature)
        {
            case "play":
                story.Hold("open", 1.4).Hold("play-key", 0.5);
                story.Motion("playing", 130, story.End, story.End);
                story.Hold("pause-key", 0.5);
                break;
            case "frame-step":
                story.Hold("open", 0.8).Hold("play-key", 0.4);
                story.Motion("playing", 50, story.End, story.End);
                story.Hold("pause-key", 0.4).Hold("paused", 0.6);
                for (int i = 0; i < 3; i++) story.Hold("next-key", 0.3).Hold("next", 0.45);
                story.Hold("previous-key", 0.3).Hold("previous", 0.7);
                story.Hold("in-key", 0.4);
                break;
            case "trim":
                story.Hold("open", 0.8);
                story.Move("move-in", Strip(1600)).Click("click-in").Hold("seeked-in", 0.3);
                story.Hold("in-key", 0.5).Hold("in-set", 0.6);
                story.Move("move-out", Strip(6000)).Click("click-out").Hold("seeked-out", 0.3);
                story.Hold("out-key", 0.5).Hold("out-set", 1.0);
                story.Move("move-save", Of(SaveToLibraryButton)).Click("save-click");
                story.Motion("saving", 10, story.End, story.End);
                story.Hold("finalizing", 0.5);
                story.Hold("saved", 0.4);
                break;
            case "gif-export":
                (PointD speed, PointD export) = MeasureDialog();
                story.Hold("open", 1.2).Hold("gif-key", 0.5).Hold("preparing", 0.5).Hold("ready", 0.6);
                story.Move("options", speed, 12).Hold("options-shown", 0.8);
                story.Motion("scroll", 18, story.End, story.End).Hold("scrolled", 0.9);
                story.Move("move-export", export).Click("export-click");
                story.Hold("save-as", 1.3);
                story.Move("move-save", new PointD(SaveAsButton.X + SaveAsButton.Width / 2, SaveAsButton.Y + SaveAsButton.Height / 2)).Click("save-click");
                story.Hold("exporting", 0.2);
                break;
        }

        return story.Finish("result", 11);
    }

    // ---- Frames -----------------------------------------------------------------------------

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        Apply(frame);
        Stage.DrawDesktop(dc);
        if (!_closed) Stage.DrawWindow(dc, _host.Snapshot(EditorScale), ClientOrigin, _editor.Title);
        if (_dialogHost is not null) Stage.DrawWindow(dc, _dialogHost.Snapshot(DialogScale), DialogOrigin, _dialog!.Title);
        if (_saveAsVisible) DrawSaveAs(dc, frame);

        (string caption, string key) = Caption(frame.Segment);
        if (frame.Pressed) Stage.DrawClick(dc, frame.Cursor, frame.Progress);
        if (ShowsPointer(frame.Segment))
            Stage.DrawPointer(dc, frame.Cursor, OverStrip(frame.Cursor) && _dialogHost is null ? Stage.PointerKind.Hand : Stage.PointerKind.Arrow);
        Stage.DrawCaption(dc, caption, key);
    }

    private bool ShowsPointer(string segment) => _feature switch
    {
        "trim" => true,
        "gif-export" => segment is not ("open" or "gif-key" or "preparing" or "ready"),
        // Keyboard clips: the pointer rests out of the way and is not drawn.
        _ => false,
    };

    private void Apply(TutorialFramePlan frame)
    {
        switch (frame.Segment)
        {
            case "play-key" or "pause-key":
                // Playback starts where the playhead is; the steady clock below resumes from there.
                if (frame.Segment == "play-key") _playStartMs = _timeline.PlayheadMs;
                Press("Play");
                if (frame.Segment == "play-key") BeginSteadyPlayback();
                break;
            case "playing":
                ShowPlaybackAt(_playStartMs + (frame.Step + 1) * FrameMs);
                break;
            case "next-key":
                Press("NextFrame", newFrame: true);
                break;
            case "previous-key":
                Press("PreviousFrame", newFrame: true);
                break;
            case "in-key":
                Press("In");
                break;
            case "out-key":
                Press("Out");
                break;
            case "click-in" or "click-out" when frame.IsLast:
                ClickStrip(frame.Cursor);
                break;
            case "save-click" when _feature == "trim" && frame.IsLast:
                string before = StatusText();
                SaveToLibraryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                // The first saving frame shows the save's own status, not the trim message before it.
                WaitUntil(() => StatusText() != before, 3000);
                break;
            case "saving":
                Stage.Pump(40);
                break;
            case "finalizing":
                // The real re-encode runs into the output folder; the library commit is ours to finish.
                if (!WaitUntil(() => _commit is not null || _closed, 20000))
                    throw new InvalidOperationException("Save to library never reached the library commit.");
                Stage.Pump(30);
                break;
            case "saved":
                _commit?.TrySetResult(true);
                if (!WaitUntil(() => !_editor.IsVisible, 5000)) throw new InvalidOperationException("The editor stayed open after saving to the library.");
                _closed = true;
                break;
            case "gif-key":
                Press("Gif");
                if (_dialog is null) throw new InvalidOperationException("G did not open the export dialog.");
                break;
            case "ready":
                if (!WaitUntil(() => _dialog!.SamplePanel.HasSample && _dialog.SampleCompletion.IsCompleted, 30000))
                    throw new InvalidOperationException("The GIF sample did not finish.");
                FluidMotion.AnimationsEnabledOverrideForTest = _motionBefore;
                Stage.Pump(250);
                break;
            case "scroll":
                ScrollViewer settings = SettingsScroll();
                settings.ScrollToVerticalOffset(settings.ScrollableHeight * Stage.Ease(frame.Progress));
                Stage.Pump(15);
                break;
            case "export-click" when frame.IsLast:
                _saveAsVisible = true;
                break;
            case "save-click" when _feature == "gif-export" && frame.IsLast:
                _saveAsVisible = false;
                _exportPath = Path.Combine(_work, VideoExportDialog.DefaultExportName(_recording.OutputPath, ".gif"));
                _dialog!.DestinationChooserForTest = () => _exportPath;
                _export = _dialog.ExportAsync();
                Stage.Pump(30);
                break;
            case "exporting":
                Stage.Pump(30);
                break;
            case "result" when _export is not null:
                if (!WaitUntil(() => _export.IsCompleted, 30000)) throw new InvalidOperationException("The GIF export did not finish.");
                if (_dialog!.SavedPath is null) throw new InvalidOperationException("The GIF export failed: " + _dialog.StatusText);
                Stage.Pump(30);
                break;
            default:
                Stage.Pump(15);
                break;
        }
    }

    /// <summary>Presses the key the editor routes to <paramref name="command"/> (ShortcutCatalog, video scope).</summary>
    private void Press(string command, bool newFrame = false)
    {
        ShortcutDefinition shortcut = ShortcutCatalog.Get(ShortcutScope.Video, command);
        long before = newFrame ? PreviewSignature() : 0;
        if (!_editor.HandleShortcut(shortcut.Key, shortcut.Modifiers))
            throw new InvalidOperationException($"The video editor did not handle {shortcut.Hint} ({command}).");
        Stage.Pump(30);
        WaitForSeek();
        if (newFrame && !WaitForPreviewChange(before, 2000))
            throw new InvalidOperationException($"The preview did not show the frame at {_timeline.PlayheadMs:0} ms after {shortcut.Hint}.");
    }

    /// <summary>The accessible status line; independent of the editor grid's row numbers.</summary>
    private string StatusText() =>
        _host.Find<TextBlock>().Single(text => text.Parent is Border { Parent: Grid } bar
            && ReferenceEquals(bar.Child, text) && text.TextTrimming == TextTrimming.CharacterEllipsis
            && System.Windows.Automation.AutomationProperties.GetLiveSetting(text)
                == System.Windows.Automation.AutomationLiveSetting.Polite).Text;

    /// <summary>A cheap fingerprint of the preview pixels, to wait until a sought frame is on screen.</summary>
    private long PreviewSignature()
    {
        Rect preview = _host.BoundsOf(_media);
        BitmapSource client = _host.Snapshot(0.5);
        int x = (int)(preview.X * 0.5 * Stage.Scale), y = (int)(preview.Y * 0.5 * Stage.Scale);
        int width = Math.Min(client.PixelWidth - x, (int)(preview.Width * 0.5 * Stage.Scale));
        int height = Math.Min(client.PixelHeight - y, (int)(preview.Height * 0.5 * Stage.Scale));
        if (width <= 0 || height <= 0) return 0;
        byte[] pixels = new byte[width * height * 4];
        client.CopyPixels(new Int32Rect(x, y, width, height), pixels, width * 4, 0);
        long hash = 17;
        foreach (byte value in pixels) hash = unchecked(hash * 31 + value);
        return hash;
    }

    /// <summary>
    /// MediaElement decodes a sought frame on its own thread after the seek returns, sometimes
    /// noticeably later; wait until the preview pixels change. Frame steps and trim strip clicks
    /// fail the clip when the picture never changes rather than record a stale preview.
    /// </summary>
    private bool WaitForPreviewChange(long before, int timeoutMs)
    {
        if (!WaitUntil(() => PreviewSignature() != before, timeoutMs)) return false;
        Stage.Pump(40);
        return true;
    }

    /// <summary>
    /// Space started the editor's playback (its play button, status and playback timer). The
    /// generator then holds the media clock and advances it one video frame per clip frame,
    /// so the clip shows playback at its real speed however long a frame takes to render.
    /// </summary>
    private void BeginSteadyPlayback()
    {
        if (!WaitUntil(() => _editor.PlayButtonShowsPauseForTest && IsPlayingStatus(), 5000))
            throw new InvalidOperationException("Space did not start playback.");
        _media.Pause();
        ShowPlaybackAt(_playStartMs);
    }

    private bool IsPlayingStatus() => _host.Find<TextBlock>().Any(text => text.Text == UiText.Get("Text_FD6443988325"));

    private void ShowPlaybackAt(double ms)
    {
        long before = PreviewSignature();
        _media.Position = TimeSpan.FromMilliseconds(ms);
        // The editor's playback timer reads the media position and moves the playhead.
        WaitUntil(() => Math.Abs(_timeline.PlayheadMs - ms) < 0.5, 500);
        WaitForPreviewChange(before, 500);
    }

    private void ClickStrip(PointD stage)
    {
        Point strip = _host.ClientRoot.TranslatePoint(
            new Point((stage.X - ClientOrigin.X) / EditorScale, (stage.Y - ClientOrigin.Y) / EditorScale), _timeline.TrimStripForTest);
        long before = PreviewSignature();
        _timeline.PressForTest(strip.X, strip.Y);
        _timeline.ReleaseForTest(strip.X, strip.Y);
        Stage.Pump(30);
        WaitForSeek();
        if (!WaitForPreviewChange(before, 2000))
            throw new InvalidOperationException($"The preview did not show the frame at {_timeline.PlayheadMs:0} ms after the trim strip click.");
    }

    private void WaitForSeek()
    {
        PreviewSeekCoordinator seeks = _editor.PreviewSeekCoordinatorForTest;
        WaitUntil(() => !seeks.IsInFlightForTest && !seeks.HasPendingForTest, 3000);
        // MediaElement decodes the sought frame asynchronously after the seek returns.
        Stage.Pump(120);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            Stage.Pump(20);
        }

        return true;
    }

    // ---- Export dialog ----------------------------------------------------------------------

    /// <summary>Hosts the editor's real export dialog offscreen instead of its modal ShowDialog.</summary>
    private void HostExportDialog(VideoExportDialog dialog)
    {
        _dialog = dialog;
        _dialogHost = new OffscreenWindow(dialog, DialogWidth, DialogHeight);
        // The generator renders with animations off; the GIF sample, ready a moment after the
        // dialog opens, then plays as it does with Windows animation effects on (the default).
        _motionBefore = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
    }

    private ScrollViewer SettingsScroll() => _dialogHost!.Find<ScrollViewer>().First(scroll => scroll.Content is StackPanel);

    /// <summary>
    /// The dialog exists only after G; an identical one (same recording and trim) measures where
    /// its playback speed list and its Export button are.
    /// </summary>
    private (PointD Speed, PointD Export) MeasureDialog()
    {
        var document = VideoEditDocument.CreateFor(_recording.Width, _recording.Height, _recording.DurationMs);
        document.TrimInMs = 1200;
        document.TrimOutMs = 5200;
        var probe = new VideoExportDialog(_recording, document, NullLoggerFactory.Instance, gif: true);
        using var host = new OffscreenWindow(probe, DialogWidth, DialogHeight);
        PointD speed = host.CenterOf(host.Named<ComboBox>(UiText.Get("MediaExport_Speed")));
        PointD export = host.CenterOf(host.Named<Button>(UiText.Get("MediaExport_Export")));
        return (DialogPoint(speed), DialogPoint(export));
    }

    // ---- Windows save dialog (OS UI, drawn) -----------------------------------------------------

    private static readonly Rect SaveAsClient = new(150, 74, 660, 384);

    private static Rect SaveAsButton => new(SaveAsClient.Right - 218, SaveAsClient.Bottom - 44, 96, 32);

    private void DrawSaveAs(DrawingContext dc, TutorialFramePlan frame)
    {
        Rect client = SaveAsClient;
        var outer = new Rect(client.X, client.Y - 32, client.Width, client.Height + 32);
        for (int i = 1; i <= 8; i++)
        {
            var ring = new Rect(outer.X - i, outer.Y - i + 4, outer.Width + 2 * i, outer.Height + 2 * i);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(40 - i * 4), 0, 0, 0)), 1.2), ring, 8 + i, 8 + i);
        }

        Brush window = Frozen(Color.FromRgb(0x20, 0x20, 0x20));
        Brush pane = Frozen(Color.FromRgb(0x1A, 0x1A, 0x1A));
        Brush field = Frozen(Color.FromRgb(0x2D, 0x2D, 0x2D));
        Brush edge = Frozen(Color.FromRgb(0x3C, 0x3C, 0x3C));
        Brush text = Brushes.White;
        Brush dim = Frozen(Color.FromRgb(0xC5, 0xC5, 0xC5));
        Brush selected = Frozen(Color.FromRgb(0x35, 0x35, 0x35));
        Brush accent = Frozen(Color.FromRgb(0x4C, 0xC2, 0xFF));
        var os = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var glyphs = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        void Write(string value, double x, double y, double size, Brush brush, bool bold = false) =>
            dc.DrawText(new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(os, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, Stage.Scale), new Point(x, y));
        void Glyph(string value, double x, double y, double size, Brush brush) =>
            dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(glyphs, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, Stage.Scale), new Point(x, y));

        dc.PushClip(new RectangleGeometry(outer, 8, 8));
        dc.DrawRectangle(window, null, outer);
        // The product titles the save dialog with its own Export command.
        Write(UiText.Get("MediaExport_Export"), outer.X + 14, outer.Y + 8, 12, text);
        Glyph("", outer.Right - 30, outer.Y + 11, 10, text);

        // Command row: back, forward, up, the folder path and search.
        double row = client.Y + 8;
        Glyph("", client.X + 14, row + 7, 13, dim);
        Glyph("", client.X + 44, row + 7, 13, Frozen(Color.FromRgb(0x6A, 0x6A, 0x6A)));
        Glyph("", client.X + 74, row + 7, 13, dim);
        var path = new Rect(client.X + 104, row, client.Width - 330, 28);
        dc.DrawRoundedRectangle(field, new Pen(edge, 1), path, 4, 4);
        Glyph("", path.X + 10, path.Y + 7, 13, Frozen(Color.FromRgb(0xF2, 0xC6, 0x4D)));
        Write("›  " + L("동영상", "Videos"), path.X + 32, path.Y + 5, 12.5, text);
        var search = new Rect(path.Right + 10, row, client.Right - path.Right - 22, 28);
        dc.DrawRoundedRectangle(field, new Pen(edge, 1), search, 4, 4);
        Write(L("동영상 검색", "Search Videos"), search.X + 10, search.Y + 5, 12.5, dim);
        Glyph("", search.Right - 24, search.Y + 7, 12, dim);

        // Navigation pane and the folder's files.
        var navigation = new Rect(client.X, client.Y + 46, 168, client.Height - 46 - 132);
        dc.DrawRectangle(pane, null, navigation);
        (string Glyph, string Name)[] places =
        [
            ("", L("홈", "Home")), ("", L("바탕 화면", "Desktop")), ("", L("다운로드", "Downloads")),
            ("", L("문서", "Documents")), ("", L("사진", "Pictures")), ("", L("동영상", "Videos")),
        ];
        for (int i = 0; i < places.Length; i++)
        {
            double y = navigation.Y + 8 + i * 30;
            if (i == 5) dc.DrawRoundedRectangle(selected, null, new Rect(navigation.X + 6, y, navigation.Width - 12, 28), 4, 4);
            Glyph(places[i].Glyph, navigation.X + 16, y + 7, 13, dim);
            Write(places[i].Name, navigation.X + 40, y + 5, 12.5, text);
        }

        double listX = navigation.Right + 14;
        Write(L("이름", "Name"), listX, navigation.Y + 6, 12, dim);
        Write(L("수정한 날짜", "Date modified"), listX + 270, navigation.Y + 6, 12, dim);
        Write(L("유형", "Type"), listX + 400, navigation.Y + 6, 12, dim);
        dc.DrawRectangle(edge, null, new Rect(listX, navigation.Y + 28, client.Right - listX - 12, 1));
        (string Name, string Date)[] files =
        [
            ("MyCapture_20261009_141205" + UiText.Get("MediaExport_EditedSuffix") + ".gif", "2026-10-09 14:12"),
            ("MyCapture_20261002_100418" + UiText.Get("MediaExport_EditedSuffix") + ".gif", "2026-10-02 10:04"),
        ];
        for (int i = 0; i < files.Length; i++)
        {
            double y = navigation.Y + 36 + i * 28;
            Glyph("", listX, y + 6, 13, Frozen(Color.FromRgb(0x9C, 0xC9, 0xF2)));
            Write(files[i].Name, listX + 22, y + 4, 12.5, text);
            Write(files[i].Date, listX + 270, y + 4, 12.5, dim);
            Write(L("GIF 파일", "GIF File"), listX + 400, y + 4, 12.5, dim);
        }

        // File name, type and the buttons.
        double bottom = navigation.Bottom;
        dc.DrawRectangle(edge, null, new Rect(client.X, bottom, client.Width, 1));
        double labelX = client.X + 20, fieldX = client.X + 120, fieldWidth = client.Width - 140;
        Write(L("파일 이름(N):", "File name:"), labelX, bottom + 14, 12.5, text);
        var name = new Rect(fieldX, bottom + 10, fieldWidth, 28);
        dc.DrawRoundedRectangle(field, new Pen(edge, 1), name, 4, 4);
        dc.DrawRectangle(accent, null, new Rect(name.X + 1, name.Bottom - 2, name.Width - 2, 2));
        string fileName = VideoExportDialog.DefaultExportName(_recording.OutputPath, ".gif");
        FormattedText typed = new(fileName, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(os, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12.5, text, Stage.Scale);
        dc.DrawRectangle(Frozen(Color.FromRgb(0x00, 0x5F, 0xB8)), null, new Rect(name.X + 8, name.Y + 5, typed.Width + 2, 18));
        dc.DrawText(typed, new Point(name.X + 9, name.Y + 5));
        Write(L("파일 형식(T):", "Save as type:"), labelX, bottom + 48, 12.5, text);
        var type = new Rect(fieldX, bottom + 44, fieldWidth, 28);
        dc.DrawRoundedRectangle(field, new Pen(edge, 1), type, 4, 4);
        Write("GIF (*.gif)", type.X + 9, type.Y + 5, 12.5, text);
        Glyph("", type.Right - 24, type.Y + 9, 10, dim);
        Glyph("", client.X + 20, client.Bottom - 34, 10, dim);
        Write(L("폴더 숨기기", "Hide Folders"), client.X + 38, client.Bottom - 38, 12.5, text);
        Rect save = SaveAsButton;
        bool pressed = frame.Segment == "save-click";
        dc.DrawRoundedRectangle(pressed ? Frozen(Color.FromRgb(0x3D, 0xA8, 0xE0)) : accent, null, save, 4, 4);
        FormattedText saveText = new(L("저장(S)", "Save"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(os, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12.5, Brushes.Black, Stage.Scale);
        dc.DrawText(saveText, new Point(save.X + (save.Width - saveText.Width) / 2, save.Y + (save.Height - saveText.Height) / 2));
        var cancel = new Rect(save.Right + 10, save.Y, 96, 32);
        dc.DrawRoundedRectangle(field, new Pen(edge, 1), cancel, 4, 4);
        FormattedText cancelText = new(L("취소", "Cancel"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(os, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12.5, text, Stage.Scale);
        dc.DrawText(cancelText, new Point(cancel.X + (cancel.Width - cancelText.Width) / 2, cancel.Y + (cancel.Height - cancelText.Height) / 2));
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(Frozen(Color.FromArgb(0x80, 0x60, 0x60, 0x60)), 1), outer, 8, 8);
    }

    // ---- Captions ---------------------------------------------------------------------------

    private (string Caption, string Key) Caption(string segment)
    {
        // Keys as the editor routes them (ShortcutCatalog, video scope).
        string play = Hint(ShortcutScope.Video, "Play"), next = Hint(ShortcutScope.Video, "NextFrame"), previous = Hint(ShortcutScope.Video, "PreviousFrame");
        string trimIn = Hint(ShortcutScope.Video, "In"), trimOut = Hint(ShortcutScope.Video, "Out"), gif = Hint(ShortcutScope.Video, "Gif");
        switch (_feature)
        {
            case "play":
                return segment switch
                {
                    "open" => (L("라이브러리에서 연 녹화 · 동영상 편집", "A recording opened from the library"), ""),
                    "play-key" => (L($"{play}: 재생", $"{play} plays the recording"), play),
                    "playing" => (L("재생 중 · 재생 위치가 타임라인을 따라 이동", "Playing · the playhead moves along the timeline"), ""),
                    "pause-key" => (L($"{play}를 다시 눌러 일시 정지", $"Press {play} again to pause"), play),
                    _ => (L("일시 정지됨 · 지금 장면에서 멈춤", "Paused on the current scene"), ""),
                };
            case "frame-step":
                return segment switch
                {
                    "open" or "play-key" or "playing" => (L($"{play}: 재생", $"{play} plays the recording"), segment == "play-key" ? play : ""),
                    "pause-key" or "paused" => (L($"{play}: 일시 정지", $"{play} pauses playback"), segment == "pause-key" ? play : ""),
                    "next-key" or "next" => (L($"마침표({next}): 다음 프레임", $"Period ({next}) shows the next frame"), next),
                    "previous-key" or "previous" => (L($"쉼표({previous}): 이전 프레임", $"Comma ({previous}) shows the previous frame"), previous),
                    "in-key" => (L($"{trimIn}: 이 프레임부터 남길 구간 시작", $"{trimIn} starts the part to keep at this frame"), trimIn),
                    _ => (L($"시작 지정됨 · 이어서 {trimOut}로 끝 지정", $"Start set · press {trimOut} to set the end"), ""),
                };
            case "trim":
                string save = Ui("MediaExport_SaveEdits");
                return segment switch
                {
                    "open" or "move-in" or "click-in" or "seeked-in" => (L("남길 구간의 시작 위치로 이동", "Move to where the part to keep starts"), ""),
                    "in-key" or "in-set" => (L($"{trimIn}: 남길 구간의 시작 지정", $"{trimIn} sets the start of the part to keep"), trimIn),
                    "move-out" or "click-out" or "seeked-out" => (L("끝 위치로 이동", "Move to where it ends"), ""),
                    "out-key" => (L($"{trimOut}: 남길 구간의 끝 지정", $"{trimOut} sets the end of the part to keep"), trimOut),
                    "out-set" => (L("선택한 구간과 남는 길이 확인", "Review the kept range and its length"), ""),
                    "move-save" or "save-click" => (L($"{save} 선택", $"Click {save}"), ""),
                    "saving" or "finalizing" => (L("편집한 동영상을 라이브러리에 저장하는 중", "Saving the edited video to the library"), ""),
                    _ => (L("저장 완료 · 편집한 동영상이 라이브러리에 저장됨", "Saved · the edited video is in the library"), ""),
                };
            default:
                string export = Ui("MediaExport_Export");
                return segment switch
                {
                    "open" => (L("내보낼 구간 확인", "Review the range to export"), ""),
                    "gif-key" or "preparing" => ($"{gif}: {Ui("Help.Gif")}", gif),
                    "ready" or "options" or "options-shown" => (L("GIF 크기 · 재생 속도 · 공유 목표 크기 확인", "Review GIF size, playback speed and share size limit"), ""),
                    "scroll" or "scrolled" => (L("미리보기 샘플과 크기 확인", "Check the preview sample and its size"), ""),
                    "move-export" or "export-click" => (L($"{export} 선택", $"Click {export}"), ""),
                    "save-as" or "move-save" or "save-click" => (L("저장할 위치와 파일 이름 선택", "Choose where to save and the file name"), ""),
                    _ => (L("GIF 저장됨 · 폴더 열기로 파일 확인", "GIF saved · Open folder shows the file"), ""),
                };
        }
    }

    public override void Dispose()
    {
        if (_dialog is not null) FluidMotion.AnimationsEnabledOverrideForTest = _motionBefore;
        _commit?.TrySetResult(false);
        if (_export is not null) WaitUntil(() => _export.IsCompleted, 30000);
        _dialogHost?.Dispose();
        _host.Dispose();
        Stage.Pump(100);
        try { global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(_work, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
