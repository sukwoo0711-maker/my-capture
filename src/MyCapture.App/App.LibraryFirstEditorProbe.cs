using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Annotations;
using MyCapture.Core.Queue;

namespace MyCapture.App;

public partial class App
{
    private const string LibraryFirstEditorVariable = "MYCAPTURE_LIBRARY_FIRST_EDITOR";
    private bool _libraryFirstEditorRequested;

    private sealed record LibraryEditorPoint(string Stage, long Ticks, int ManagedThreadId,
        bool GalleryVisible, bool GalleryEnabled, bool EditorVisible, bool EditorLoaded,
        bool ImageEditorOpened, long EditorHandle);

    // This diagnostic enters the same command used by the gallery's Edit action. The
    // editor remains an ordinary GalleryEditorWindow/ShowDialog; no replacement loader,
    // prewarm, layout flush, synthetic input, model call or capture is introduced here.
    private async Task RunLibraryFirstEditorProbeAsync(ResidentProbeOptions options,
        LibraryFirstPageInput input, LibraryFirstPageTiming.Observation firstPageTiming)
    {
        Dispatcher.VerifyAccess();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var events = new List<LibraryEditorPoint>(16);
        var errors = new List<object>();
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GalleryWindow gallery = _galleryWindow ?? throw new InvalidOperationException("The measured gallery is missing.");
        CaptureQueue queue = _queue ?? throw new InvalidOperationException("The synthetic queue is missing.");
        GalleryEditorWindow? editor = null;
        DispatcherOperation? request = null;
        bool observing = true, hookInstalled = false, hookRestored = false, identityVerified = false;
        int sourceCount = 0;
        long requested = 0, returned = 0, initialized = 0, loaded = 0, contentRendered = 0;
        long inputTick = 0, rendering = 0, identityChecked = 0, closeRequested = 0, closedTick = 0;
        string? originalRelative = null, originalPath = null, pinnedOriginal = null, observedOriginal = null;
        string? expectedPixels = null, actualPixels = null, expectedDocument = null, actualDocument = null;
        string? recordBefore = null, recordAfter = null;
        object? editorLayout = null;
        Guid recordId = input.RecordIds[0];
        CaptureRecord? record = null;
        LibraryFirstPageTiming.Observation? transitionTiming = null;
        Action<Window>? priorHook = ResidentProbeWindowHook.SourceInitialized;

        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        void Error(Exception error)
        {
            if (errors.Count < 16) errors.Add(new { Type = error.GetType().FullName, error.HResult, error.Message });
            rendered.TrySetException(error);
        }
        void Observe(Action action)
        {
            if (!observing) return;
            try { action(); }
            catch (Exception error) { Error(error); }
        }
        void Mark(string stage)
        {
            Require(events.Count < 16, "Editor observation capacity exceeded.");
            events.Add(new(stage, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
                gallery.IsVisible, gallery.IsEnabled, editor?.IsVisible == true, editor?.IsLoaded == true,
                _imageEditorOpened, editor is null ? 0 : new WindowInteropHelper(editor).Handle.ToInt64()));
        }
        void RequireOwned(bool requireReady)
        {
            Require(editor is not null && ReferenceEquals(editor.Owner, gallery)
                && ReferenceEquals(_galleryWindow, gallery) && gallery.IsVisible
                && string.Equals(editor.Editor.SourceFilePath, originalPath, StringComparison.OrdinalIgnoreCase),
                "The observed editor does not belong to the exact synthetic gallery record.");
            if (requireReady)
                Require(editor!.IsVisible && editor.IsLoaded && editor.IsEnabled
                    && editor.WindowState != WindowState.Minimized && editor.IsMeasureValid && editor.IsArrangeValid
                    && editor.ActualWidth > 0 && editor.ActualHeight > 0
                    && editor.Editor.DisplaySurfaceSize.Width > 0 && editor.Editor.DisplaySurfaceSize.Height > 0
                    && new WindowInteropHelper(editor).Handle != 0 && _imageEditorOpened,
                    "The owned editor is not ready or App did not observe its Loaded event.");
        }
        EventHandler? onRendering = null;
        EventHandler onClosed = (_, _) =>
        {
            closedTick = Stopwatch.GetTimestamp();
            Observe(() => Mark("closed"));
            closed.TrySetResult();
        };
        RoutedEventHandler onLoaded = (_, _) => Observe(() =>
        {
            if (loaded != 0) return;
            loaded = Stopwatch.GetTimestamp();
            Mark("loaded");
        });
        EventHandler onContentRendered = (_, _) => Observe(() =>
        {
            if (contentRendered != 0) return;
            RequireOwned(requireReady: true);
            contentRendered = Stopwatch.GetTimestamp();
            Mark("content-rendered");
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Observe(() =>
            {
                RequireOwned(requireReady: true);
                inputTick = Stopwatch.GetTimestamp();
                Mark("input");
                CompositionTarget.Rendering += onRendering;
            })));
        });
        onRendering = (_, _) => Observe(() =>
        {
            if (rendering != 0 || inputTick == 0) return;
            RequireOwned(requireReady: true);
            rendering = Stopwatch.GetTimestamp();
            Mark("rendering");
            CompositionTarget.Rendering -= onRendering;
            rendered.TrySetResult();
        });
        Action<Window> hook = window => Observe(() =>
        {
            if (window is not AnnotationEditorWindow) return;
            Require(window is GalleryEditorWindow candidate && ReferenceEquals(candidate.Owner, gallery)
                && string.Equals(candidate.Editor.SourceFilePath, originalPath, StringComparison.OrdinalIgnoreCase),
                "An unexpected annotation editor entered the transition.");
            Require(sourceCount == 0, "More than one editor was initialized.");
            editor = (GalleryEditorWindow)window;
            sourceCount++;
            initialized = Stopwatch.GetTimestamp();
            editor.Loaded += onLoaded;
            editor.ContentRendered += onContentRendered;
            editor.Closed += onClosed;
            Mark("source-initialized");
        });
        try
        {
            Require(input.Count == 1000 && options.LibraryFirstPageCount == 1000 && priorHook is null
                && !_imageEditorOpened && !Windows.OfType<AnnotationEditorWindow>().Any()
                && gallery.DataContext is GalleryViewModel, "First-editor cold ownership prerequisites failed.");
            record = queue.Records.Single(item => item.Id == recordId);
            Require(record.IsImage && record.HasCurrentOcrIndex && !record.HasAnnotations
                && record.Width == 1920 && record.Height == 1080, "Pinned fixture record metadata differs.");
            originalPath = queue.GetFilePath(record, CaptureFileNames.Original);
            originalRelative = Path.GetRelativePath(options.Output, originalPath).Replace('\\', '/');
            Require(input.Files.TryGetValue(originalRelative, out pinnedOriginal), "Original is outside the pinned synthetic input.");
            Require(!File.Exists(queue.GetFilePath(record, CaptureFileNames.Layers)), "The synthetic record unexpectedly has layers.");
            recordBefore = JsonSerializer.Serialize(record);
            // Do not decode or hash the original here: the real product loader pays that cost.
            ResidentProbeWindowHook.SourceInitialized = hook;
            hookInstalled = true;
            request = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Observe(() =>
            {
                requested = Stopwatch.GetTimestamp();
                Mark("request");
                gallery.OpenCapture(recordId, edit: true);
                returned = Stopwatch.GetTimestamp();
                Mark("request-callback-returned");
            })));
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Require(requested > 0 && initialized >= requested && loaded >= initialized
                && contentRendered >= loaded && inputTick >= contentRendered && rendering >= inputTick,
                "Actual editor timestamp chronology failed.");
            RequireOwned(requireReady: true);
            GalleryEditorWindow actualEditor = editor ?? throw new InvalidOperationException("The editor disappeared.");
            transitionTiming = _libraryFirstPageTiming?.Snapshot();
            DpiScale dpi = VisualTreeHelper.GetDpi(actualEditor);
            editorLayout = new { actualEditor.ActualWidth, actualEditor.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY,
                SurfaceWidth = actualEditor.Editor.DisplaySurfaceSize.Width, SurfaceHeight = actualEditor.Editor.DisplaySurfaceSize.Height,
                OwnerHandle = new WindowInteropHelper(gallery).Handle.ToInt64(),
                EditorHandle = new WindowInteropHelper(actualEditor).Handle.ToInt64() };
            // These identity checks are intentionally outside the measured request/render span.
            observedOriginal = HashLibraryInput(originalPath);
            Require(string.Equals(observedOriginal, pinnedOriginal, StringComparison.OrdinalIgnoreCase), "Original bytes changed.");
            var original = ResidentLongRunInputs.Load(originalPath);
            expectedPixels = ResidentLongRunInputs.Pixels(original);
            actualPixels = ResidentLongRunInputs.Pixels(actualEditor.Editor.DisplayedBitmap);
            // The historical fixture deliberately has 1920x1080 metadata but 64x36 PNGs.
            // The real re-edit loader sizes the document from decoded pixels, not metadata.
            expectedDocument = AnnotationDocument.CreateFor(64, 36).ToJson();
            actualDocument = actualEditor.Editor.CreateCurrentImageSnapshot().ToJson();
            Require(original.PixelWidth == 64 && original.PixelHeight == 36
                && actualEditor.Editor.DisplayedBitmap.PixelWidth == 64
                && actualEditor.Editor.DisplayedBitmap.PixelHeight == 36
                && expectedPixels == actualPixels && expectedDocument == actualDocument,
                "The actual editor image/document differs from the pinned original and empty layer.");
            identityVerified = true;
            identityChecked = Stopwatch.GetTimestamp();
        }
        catch (Exception error) { Error(error); }
        finally
        {
            CompositionTarget.Rendering -= onRendering;
            try
            {
                if (editor is not null && closedTick == 0)
                {
                    RequireOwned(requireReady: false);
                    closeRequested = Stopwatch.GetTimestamp();
                    Mark("close-requested");
                    editor.Close(); // Only this owned, unedited diagnostic window. No save or discard override.
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                if (request is not null) await request.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.InvokeAsync(() =>
                {
                    Require(sourceCount == 1 && closedTick > 0 && gallery.IsVisible && gallery.IsEnabled
                        && _imageEditorOpened, "Editor cleanup or App prewarm suppression was not observed.");
                    recordAfter = record is null ? null : JsonSerializer.Serialize(record);
                    Require(recordBefore == recordAfter && queue.Count == 1000
                        && queue.Records.All(item => item.HasCurrentOcrIndex), "Synthetic queue changed during editing.");
                }, DispatcherPriority.ContextIdle);
            }
            catch (Exception error) { Error(error); }
            observing = false;
            if (editor is not null)
            {
                editor.Loaded -= onLoaded;
                editor.ContentRendered -= onContentRendered;
                editor.Closed -= onClosed;
            }
            if (hookInstalled)
            {
                if (ReferenceEquals(ResidentProbeWindowHook.SourceInitialized, hook))
                {
                    ResidentProbeWindowHook.SourceInitialized = priorHook;
                    hookRestored = true;
                }
                else Error(new InvalidOperationException("The scoped diagnostic window hook was replaced."));
            }
            bool success = errors.Count == 0 && identityVerified && hookRestored && closedTick > 0;
            var report = new
            {
                Schema = "library-first-editor-v1", Status = success ? "MEASURED" : "FAILED",
                StartedUtc = startedUtc, FinishedUtc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
                StopwatchFrequency = Stopwatch.Frequency, Count = input.Count, RecordId = recordId,
                RecordMetadataWidth = record?.Width, RecordMetadataHeight = record?.Height,
                ExpectedOriginalPixelWidth = 64, ExpectedOriginalPixelHeight = 36,
                OriginalRelativePath = originalRelative, PinnedOriginalSha256 = pinnedOriginal,
                ObservedOriginalSha256 = observedOriginal, ExpectedPixelSha256 = expectedPixels, ActualPixelSha256 = actualPixels,
                ExpectedDocument = expectedDocument, ActualDocument = actualDocument,
                RecordBeforeSha256 = Digest(recordBefore), RecordAfterSha256 = Digest(recordAfter),
                IdentityVerified = identityVerified, SourceInitializedCount = sourceCount, HookRestored = hookRestored,
                Requested = requested, RequestCallbackReturned = returned, SourceInitialized = initialized, Loaded = loaded,
                ContentRendered = contentRendered, Input = inputTick, Rendering = rendering,
                IdentityChecked = identityChecked, CloseRequested = closeRequested, Closed = closedTick,
                ObservationFinished = Stopwatch.GetTimestamp(), Events = events, Errors = errors,
                FirstPageTimingCutoff = firstPageTiming.ObservedThroughTicks, TransitionTiming = transitionTiming,
                EditorLayout = editorLayout,
                GalleryVisibleAfterClose = gallery.IsVisible, GalleryEnabledAfterClose = gallery.IsEnabled,
                AppLoadedEditorOpened = _imageEditorOpened, NativeInputSent = 0, ClipboardWrites = "INTERCEPTED",
                ThresholdGate = "NOT_EVALUATED",
                Interpretation = "Actual gallery Edit command including ShowGallery refresh, load, constructor and ShowDialog. ContentRendered/Input/Rendering are WPF observations, not physical Present. Request callback may return before or after modal close. Identity IO follows Rendering.",
            };
            using var stream = new FileStream(DiagnosticOutputPaths.Child(options.Output, "library-first-editor.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        if (errors.Count != 0 || !identityVerified || !hookRestored)
            throw new InvalidOperationException("Actual gallery first-editor observation failed; preserved report contains the boundary.");

        static string? Digest(string? value) => value is null ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
