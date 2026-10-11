using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// The real library status line, quick access card and pin follow the shared feedback policy
    /// with the app's resources: a failure stays with a close button until dismissed, and results
    /// leave on their own. With MYCAPTURE_FEEDBACK_EVIDENCE set it keeps a PNG of each state.
    /// </summary>
    [Fact]
    public Task FeedbackSurfaces_FailuresStayUntilDismissedAndResultsLeaveOnTime() =>
        RunIsolated(nameof(FeedbackSurfaces_FailuresStayUntilDismissedAndResultsLeaveOnTime), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                using var scope = UiText.UseLanguage("ko-KR");
                foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Glass })
                {
                    ThemeService.Apply(theme);
                    string label = AppThemeNames.ToSetting(theme);
                    VerifyLibraryStatusFeedback(label);
                    RenderQuickAccessFeedback(label);
                    RenderPinFeedback(label);
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                application.Shutdown();
            }
        });

    private static void VerifyLibraryStatusFeedback(string label)
    {
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        string root = TestRecycleBin.CreateTempSubdirectory("mc-feedback-status-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        using var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
        var window = new GalleryWindow(vm, controller,
            new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
            new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
            NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing,
            new GalleryMarkupPrivacy(), NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            Width = 980, Height = 560,
        };
        try
        {
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            SettleGalleryLayout(window);
            var status = (TextBlock)window.FindName("LibraryActionStatus");
            var bar = (Grid)window.FindName("LibraryStatusBar");
            Button Dismiss() => bar.Children.OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == UiText.Get("Feedback.Dismiss"));

            // A failure stays, in the danger colour, with a close button; Esc dismisses it.
            status.Text = UiText.Get("Library.Archive.Failed");
            SettleGalleryLayout(window);
            Assert.Equal(FeedbackKind.ActionRequired, window.ActionStatusKindForTest);
            Assert.True(window.IsActionStatusDismissibleForTest, label);
            Assert.True(Dismiss().IsVisible, label);
            Assert.Same(window.FindResource("State.Danger"), status.Foreground);
            Assert.Null(window.ActionStatusRemainingForTest);
            window.AdvanceActionStatusForTest(TimeSpan.FromHours(1));
            Assert.Equal(UiText.Get("Library.Archive.Failed"), status.Text);
            SaveFeedbackEvidence(bar, $"library-status-failed-{label}.png");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
            Assert.Equal(string.Empty, status.Text);
            Assert.False(window.IsActionStatusDismissibleForTest);

            // The close button dismisses it as well.
            status.Text = UiText.Get("Folder410.OperationFailed");
            Dismiss().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(string.Empty, status.Text);

            // A plain confirmation leaves after its short time; a result with a count stays longer.
            status.Text = UiText.Get("Library.Source.PathCopied");
            Assert.Equal(FeedbackTiming.Confirmation, window.ActionStatusRemainingForTest);
            window.AdvanceActionStatusForTest(FeedbackTiming.Confirmation);
            Assert.Equal(string.Empty, status.Text);
            status.Text = UiText.Format("Library.Archive.Done", 12);
            Assert.Equal(FeedbackTiming.Result, window.ActionStatusRemainingForTest);
            Assert.Same(window.FindResource("Text.Secondary"), status.Foreground);
            SettleGalleryLayout(window);
            SaveFeedbackEvidence(bar, $"library-status-result-{label}.png");
            window.AdvanceActionStatusForTest(FeedbackTiming.Result);
            Assert.Equal(string.Empty, status.Text);
        }
        finally { window.Close(); }
    }

    private static void RenderQuickAccessFeedback(string label)
    {
        // Real motion, so the progress indicator is captured mid-run.
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var save = new TaskCompletionSource<bool>();
        QuickAccessWindow card = new(FeedbackSample(), () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => save.Task, () => { }, () => Task.FromResult("unused.png"));
        try
        {
            card.Show();
            PumpFor(TimeSpan.FromMilliseconds(200));
            SaveFeedbackEvidence(card, $"quick-access-idle-{label}.png");
            card.ShortcutActions["Save"]();
            Assert.Equal(FeedbackKind.Progress, card.StatusForTest.Kind);
            PumpFor(TimeSpan.FromMilliseconds(700));
            Assert.True(card.StatusForTest.Progress, label);
            SaveFeedbackEvidence(card, $"quick-access-saving-{label}.png");
            save.SetResult(true);
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert.Equal((UiText.Get("QuickAccess.Saved"), FeedbackKind.Confirmation, true, false), card.StatusForTest);
            SaveFeedbackEvidence(card, $"quick-access-saved-{label}.png");
            card.IsKept = true;
            card.RunAsync("Copy", () => Task.FromResult(false), "QuickAccess.Copying", "QuickAccess.Copied").GetAwaiter().GetResult();
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert.Equal(FeedbackKind.ActionRequired, card.StatusForTest.Kind);
            SaveFeedbackEvidence(card, $"quick-access-failed-{label}.png");
        }
        finally { card.Close(); }
    }

    private static void RenderPinFeedback(string label)
    {
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        PinWindow pin = manager.PinImage(FeedbackSample());
        try
        {
            pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Failed));
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert.Equal(FeedbackKind.ActionRequired, pin.FeedbackForTest.Kind);
            SaveFeedbackEvidence(pin, $"pin-save-failed-{label}.png");
            pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Saved, @"C:\Users\test\Pictures\MyCapture\MyCapture_20261011_153012.png"));
            PumpFor(TimeSpan.FromMilliseconds(150));
            SaveFeedbackEvidence(pin, $"pin-saved-{label}.png");
        }
        finally { manager.CloseAll(); }
    }

    private static BitmapSource FeedbackSample()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2F, 0x6F, 0xB5), Color.FromRgb(0xE8, 0xEE, 0xF6), 0), null,
                new Rect(0, 0, 360, 220));
            drawing.DrawRoundedRectangle(Brushes.White, null, new Rect(24, 24, 200, 18), 4, 4);
            drawing.DrawRoundedRectangle(Brushes.White, null, new Rect(24, 54, 280, 12), 4, 4);
            drawing.DrawRoundedRectangle(Brushes.White, null, new Rect(24, 74, 240, 12), 4, 4);
        }

        var bitmap = new RenderTargetBitmap(360, 220, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SaveFeedbackEvidence(FrameworkElement element, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_FEEDBACK_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        // A part of a window is cut out of the whole window's render.
        Window window = element as Window ?? Window.GetWindow(element)!;
        window.UpdateLayout();
        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext drawing = backdrop.RenderOpen())
            drawing.DrawRectangle(window.TryFindResource("Surface.Base") as Brush ?? Brushes.Black, null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(backdrop);
        bitmap.Render(window);
        BitmapSource image = bitmap;
        if (!ReferenceEquals(element, window))
        {
            Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var pixels = new Int32Rect((int)Math.Floor(bounds.X * scale), (int)Math.Floor(bounds.Y * scale),
                (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
            pixels.Width = Math.Min(pixels.Width, bitmap.PixelWidth - pixels.X);
            pixels.Height = Math.Min(pixels.Height, bitmap.PixelHeight - pixels.Y);
            image = new CroppedBitmap(bitmap, pixels);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(output);
    }
}
