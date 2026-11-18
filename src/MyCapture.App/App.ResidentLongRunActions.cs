using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App;

public partial class App
{
    private readonly HashSet<Guid> _longRunOwnedRecords = [];
    private static long s_longRunNeuralResults;
    private static ResidentLongRunEvidence? s_longRunCallbackEvidence;

    private static void ObserveLongRunNeuralSession(Microsoft.ML.OnnxRuntime.SessionOptions options,
        string detector, string classifier, string recognition, string dictionary) =>
        s_longRunCallbackEvidence?.Record("neural-real-session", new { detector, classifier, recognition, dictionary,
            options.IntraOpNumThreads, options.InterOpNumThreads, options.EnableCpuMemArena,
            options.EnableMemoryPattern, options.ExecutionMode });

    // Reflection is confined to this opt-in script, like the existing resident selector/editor
    // accessors. It observes actual owners and invokes their existing input paths, not test fakes.
    private static T LongRunField<T>(object owner, string field) where T : class =>
        owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner) as T
        ?? throw new InvalidOperationException("Long-run owner contract changed: " + field);

    private static object? LongRunNullableField(object owner, string name) =>
        (owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Long-run owner contract changed: " + name)).GetValue(owner);

    private static void ObserveLongRunNeural(RapidOcrNet.OcrResult result)
    {
        Interlocked.Increment(ref s_longRunNeuralResults);
        s_longRunCallbackEvidence?.Record("neural-real-result", new
        {
            result.StrRes,
            blocks = result.TextBlocks?.Select(block => new
            {
                block.Text, points = block.BoxPoints?.Select(point => new { point.X, point.Y }).ToArray(),
                block.BoxScore, block.AngleIndex, block.AngleScore, chars = block.Chars?.ToArray(),
                charScores = block.CharScores?.ToArray(), words = block.WordResults?.Select(word => new
                { word.Text, points = word.BoxPoints?.Select(point => new { point.X, point.Y }).ToArray(), word.Score }).ToArray(),
            }).ToArray(),
        });
    }

    private async Task<object> RunLongRunActionAsync(string kind, ResidentLongRunInputs inputs, ResidentLongRunEvidence evidence)
    {
        if (_queue is null || _overlay is null || _settings is null || _pins is null || _recorder is null || _ocrPresenter is null)
            throw new InvalidOperationException("Resident owners unavailable.");
        return kind switch
        {
            "capture" => await LongRunCaptureAsync(evidence),
            "edit" => await LongRunEditAsync(evidence),
            "pin" => await LongRunPinAsync(),
            "library" => await LongRunLibraryAsync(),
            "ocr" => await LongRunOcrAsync(inputs, evidence),
            "recording" => await LongRunRecordingAsync(evidence),
            "translation" => await LongRunTranslationAsync(inputs, evidence),
            _ => throw new InvalidOperationException("Unknown core action."),
        };
    }

    private async Task<object> LongRunCaptureAsync(ResidentLongRunEvidence evidence)
    {
        HashSet<Guid> before = _queue!.Records.Select(record => record.Id).ToHashSet();
        HandleCaptureRequested();
        await ProbeWaitAsync(() => ProbeOverlay is { IsVisible: true }, 30_000, "synthetic selector");
        CaptureOverlayWindow selector = ProbeOverlay!;
        await NextLongRunRenderingAsync();
        selector.CompleteSelection(new RectD(160, 120, 640, 360));
        await ProbeWaitAsync(() => _quickAccess is { IsVisible: true } && _capturePersistenceTask.IsCompleted,
            30_000, "capture receipt and original persistence");
        await _capturePersistenceTask;
        CaptureRecord record = NewLongRunRecord(before, CaptureMediaKind.Image);
        string original = _queue.GetFilePath(record, CaptureFileNames.Original);
        BitmapSource persisted = ResidentLongRunInputs.Load(original);
        RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
        BitmapSource synthetic = ResidentProbeImages.Pattern((int)desktop.Width, (int)desktop.Height, ResidentLongRunContract.Seed);
        var expected = new CroppedBitmap(synthetic, new Int32Rect(160, 120, 640, 360));
        if (record.Width != 640 || record.Height != 360 || persisted.PixelWidth != 640 || persisted.PixelHeight != 360
            || ResidentLongRunInputs.Pixels(persisted) != ResidentLongRunInputs.Pixels(expected))
            throw new InvalidDataException("Capture durable pixels differ from the selected synthetic region.");
        RequireDurableRecord(record);
        _quickAccess!.RequestClose();
        await ProbeWaitAsync(() => _quickAccess is null && !_overlay!.IsActive, 10_000, "capture close");
        return new { record.Id, record.Width, record.Height, pixelSha256 = ResidentLongRunInputs.Pixels(persisted),
            originalSha256 = ResidentLongRunInputs.HashFile(original), completion = "durable original + index + closed receipt" };
    }

    private async Task<object> LongRunEditAsync(ResidentLongRunEvidence evidence)
    {
        HashSet<Guid> before = _queue!.Records.Select(record => record.Id).ToHashSet();
        AfterCaptureAction previous = _settings!.General.AfterCapture;
        _settings.General.AfterCapture = AfterCaptureAction.Editor;
        try
        {
            var frame = ResidentProbeImages.DesktopFrame(new RectD(0, 0, 640, 360), ResidentLongRunContract.Seed);
            if (!_overlay!.StartWithSelection(frame, new RectD(0, 0, 640, 360), "Synthetic resident edit"))
                throw new InvalidOperationException("Real editor path refused the synthetic capture.");
            await ProbeWaitAsync(() => ProbeEditor is { IsVisible: true } && _capturePersistenceTask.IsCompleted,
                30_000, "editor and original persistence");
            await _capturePersistenceTask;
            AnnotationEditorWindow editor = ProbeEditor!;
            var controller = LongRunField<AnnotationEditorController>(editor.Editor, "_controller");
            if (controller.Document.Items.Count != 0) throw new InvalidDataException("Fresh editor contains layers.");
            controller.Tool = EditorTool.Rectangle;
            controller.PointerDown(new PointD(40, 40));
            controller.PointerMove(new PointD(220, 150));
            controller.PointerUp(new PointD(220, 150));
            if (controller.Document.Items.Count != 1 || controller.Document.Items[0] is not RectangleAnnotation rectangle
                || rectangle.Rect != new RectD(40, 40, 180, 110) || !controller.CanUndo)
                throw new InvalidDataException("Actual editor gesture did not create the expected undoable rectangle.");
            string expectedLayers = editor.Editor.CreateCurrentImageSnapshot().ToJson();
            CaptureRecord record = NewLongRunRecord(before, CaptureMediaKind.Image);
            long revisionBefore = record.ContentRevision;
            editor.Editor.SaveAndClose();
            await ProbeWaitAsync(() => ProbeEditor is null && !_overlay.IsActive && _capturePersistenceTask.IsCompleted,
                45_000, "editor save commit and close");
            await _capturePersistenceTask;
            string layersPath = _queue.GetFilePath(record, CaptureFileNames.Layers);
            AnnotationDocument document = AnnotationDocument.TryFromJson(File.ReadAllText(layersPath))
                ?? throw new InvalidDataException("Saved layers cannot be read.");
            if (document.ToJson() != expectedLayers || !record.HasAnnotations || record.ContentRevision <= revisionBefore)
                throw new InvalidDataException("Durable editor layers/revision differ.");
            string rendered = _queue.GetFilePath(record, CaptureFileNames.Rendered);
            string original = _queue.GetFilePath(record, CaptureFileNames.Original);
            BitmapSource image = ResidentLongRunInputs.Load(rendered);
            if (image.PixelWidth != 640 || image.PixelHeight != 360
                || ResidentLongRunInputs.Pixels(image) == ResidentLongRunInputs.Pixels(ResidentLongRunInputs.Load(original)))
                throw new InvalidDataException("Rendered annotation pixels were not committed.");
            RequireDurableRecord(record);
            return new { record.Id, record.ContentRevision, layersSha256 = ResidentLongRunInputs.HashFile(layersPath),
                renderedSha256 = ResidentLongRunInputs.HashFile(rendered), completion = "real gesture + undo + saved layers/render + closed editor" };
        }
        finally { _settings.General.AfterCapture = previous; }
    }

    private async Task<object> LongRunPinAsync()
    {
        BitmapSource image = ResidentProbeImages.Pattern(640, 360, ResidentLongRunContract.Seed);
        string expected = ResidentLongRunInputs.Pixels(image);
        PinWindow pin = _pins!.PinImage(image);
        await ProbeWaitAsync(() => pin.IsShown, 10_000, "pin visible");
        await NextLongRunRenderingAsync();
        if (ResidentLongRunInputs.Pixels(pin.PinnedContent.Image) != expected)
            throw new InvalidDataException("Real pin content differs.");
        pin.Close();
        await ProbeWaitAsync(() => _pins.Count == 0 && !_pins.IsInteractionActive, 15_000, "pin close and history encoding");
        if (_settings!.Pin.ClosedWindowRestoreLimit > 0 && !_pins.CanRestoreClosed)
            throw new InvalidDataException("Closed pin was not retained.");
        return new { pixelSha256 = expected, _pins.ClosedCount, _pins.ClosedRetainedBytes,
            completion = "visible real pin + exact pixels + close + history encoder idle" };
    }

    private async Task<object> LongRunLibraryAsync()
    {
        HandleGalleryRequested();
        await ProbeWaitAsync(() => _galleryWindow is { IsVisible: true }, 15_000, "library visible");
        GalleryWindow window = _galleryWindow!;
        GalleryViewModel vm = LongRunField<GalleryViewModel>(window, "_viewModel");
        await ProbeWaitAsync(() => LibraryImagesReady(window), 20_000, "visible library thumbnail sources and opacity");
        await NextLongRunRenderingAsync();
        CaptureRecord target = _queue!.Records.First(record => !record.IsVideo);
        window.SetDetailsPaneOpen(true);
        vm.Select(target.Id);
        await ProbeWaitAsync(() => vm.SingleSelectedTile?.Id == target.Id
            && window.FindName("GalleryDetailsContent") is FrameworkElement { IsVisible: true } details
            && AncestorsOpaque(details, window),
            10_000, "library actual selection details");
        if (vm.ReachableCount != _queue.Count) throw new InvalidDataException("Library projection differs from live queue.");
        int visible = VisibleLibraryImages(window).Count;
        vm.ClearSelection();
        window.Close();
        await ProbeWaitAsync(() => !window.IsVisible && vm.CachedThumbnailBytes == 0, 10_000, "library hide and decoded cache release");
        return new { selectedId = target.Id, visibleThumbnailCount = visible, vm.ReachableCount,
            completion = "actual visible sources + render + selected details + hide" };
    }

    private async Task<object> LongRunOcrAsync(ResidentLongRunInputs inputs, ResidentLongRunEvidence evidence)
    {
        OcrResult? completed = null;
        long before = Interlocked.Read(ref s_longRunNeuralResults);
        var settings = new OcrSettings { Quality = OcrQuality.Accurate,
            PreferredLanguages = inputs.Spec.PreferredLanguages.ToList(), AccuracySuggestionDismissed = true };
        OcrQualityProfile.Synchronize(settings);
        OcrRequest request = OcrRequestFactory.FromBitmap(inputs.Image, settings);
        evidence.Record("ocr-request", new { settings.Quality, request.UpscaleFactor, request.PreferredLanguages,
            request.SearchRotatedOrientations, request.EnhanceContrast, request.UseNeuralModel,
            request.LocalCorrection, request.UseSuperResolution, request.RecoverLatinIntrusions });
        _ocrPresenter!.Window.SelectExtractionMode(OcrExtractionMode.Text);
        _ocrPresenter!.ShowRecognized(() => request, "Synthetic long-run OCR",
            result => completed = result);
        await ProbeWaitAsync(() => completed is not null, 90_000, "real capture OCR successful callback");
        OcrResult result = completed!;
        evidence.Record("ocr-result", new { result.Status, projection = ResidentLongRunInputs.Projection(result),
            inputPixelSha256 = inputs.PixelSha256, neuralBefore = before, neuralAfter = Interlocked.Read(ref s_longRunNeuralResults) });
        inputs.RequireGolden(result);
        if (Interlocked.Read(ref s_longRunNeuralResults) <= before)
            throw new InvalidDataException("Accurate OCR returned without an actual neural model result.");
        OcrResultWindow window = _ocrPresenter.Window;
        if (!window.IsVisible) throw new InvalidDataException("OCR result was not presented.");
        await NextLongRunRenderingAsync();
        window.Close();
        await ProbeWaitAsync(() => !window.IsVisible && !_ocrMemoryBusy, 10_000, "OCR close and owner idle");
        return new { completion = "real CaptureOcrService + neural callback + pinned text/geometry + presented result",
            inputPixelSha256 = inputs.PixelSha256, result.Text, result.LanguageTag, lineCount = result.Lines.Count };
    }

    private async Task<object> LongRunTranslationAsync(ResidentLongRunInputs inputs, ResidentLongRunEvidence evidence)
    {
        TextTranslationService translator = _services!.GetRequiredService<TextTranslationService>();
        if (!await translator.IsModelInstalledAsync(CancellationToken.None))
            throw new InvalidOperationException("Pinned real translation model is not available.");
        string source = inputs.Spec.TranslationSource;
        Dictionary<string, string> cache = LongRunField<Dictionary<string, string>>(translator, "_cache");
        string[] cachedBefore = cache.Keys.Order(StringComparer.Ordinal).ToArray();
        var bounds = new OcrRect(10, 10, 600, 40);
        OcrResult extraction = OcrResult.Success(source, "en-US",
            [new OcrLine(source, bounds, [new OcrWord(source, bounds)])], TimeSpan.Zero);
        OcrResultWindow window = _ocrPresenter!.Window;
        window.Translator = translator;
        window.ShowResult(extraction, "Synthetic long-run translation", ResidentProbeImages.Text(640, 160, [source]));
        window.SelectExtractionMode(OcrExtractionMode.Translation);
        await window.TranslationWork.WaitAsync(TimeSpan.FromSeconds(90));
        var translated = window.TranslationPanel.Results;
        evidence.Record("translation-result", new { source, translated = translated.Select(row => row.Translation).ToArray(),
            translator.IsEngineLoaded, state = window.TranslationPanel.State.ToString(),
            cacheEntriesBefore = cachedBefore.Length, cacheEntriesAfter = cache.Count,
            sourceWasCached = cachedBefore.Contains(source, StringComparer.Ordinal),
            addedSegments = cache.Keys.Except(cachedBefore, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            nativeInferenceCount = "NOT_DIRECTLY_INSTRUMENTED; ordinary retained cache is preserved" });
        if (window.TranslationPanel.State != OcrTranslationPanel.ViewState.Translated || translated.Count != 1
            || translated[0].Block.Text != source || translated[0].Translation.Source != source
            || translated[0].Translation.Text != inputs.Spec.TranslationExpected || !translator.IsEngineLoaded)
            throw new InvalidDataException("Actual translation completion differs from the pinned model golden.");
        await NextLongRunRenderingAsync();
        window.SelectExtractionMode(OcrExtractionMode.Text);
        window.Close();
        await ProbeWaitAsync(() => !window.IsVisible && window.TranslationWork.IsCompleted, 10_000, "translation closed");
        return new { source, text = translated[0].Translation.Text,
            completion = "actual model translation task + exact golden + presented panel + close" };
    }

    private async Task<object> LongRunRecordingAsync(ResidentLongRunEvidence evidence)
    {
        HashSet<Guid> before = _queue!.Records.Select(record => record.Id).ToHashSet();
        long frames = 0;
        _recorder!.AcquireSelectionFrame = () => s_longRunPrivacy!.AcquireFrame(MonitorEnumerator.GetVirtualDesktopBounds());
        _recorder.SyntheticRecordingFrame = (buffer, width, height, stride) =>
        {
            if (buffer.Length != checked(stride * height) || stride != checked(width * 4))
                throw new InvalidDataException("Recording borrowed BGRA buffer dimensions differ.");
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = y * stride + x * 4;
                    buffer[offset] = (byte)(x % 256); buffer[offset + 1] = (byte)(y % 256);
                    buffer[offset + 2] = 42; buffer[offset + 3] = 255;
                }
            Interlocked.Increment(ref frames);
        };
        RecordingResult? finished = null;
        Exception? failed = null;
        RecordingControlWindow? controls = null;
        EventHandler<RecordingResult> onFinished = (_, result) => finished = result;
        EventHandler<RecordingFailedEventArgs> onFailed = (_, args) => failed = args.Exception;
        try
        {
            _recorder.Toggle();
            await _recorder.LastSelectionPreparation.WaitAsync(TimeSpan.FromSeconds(30));
            CaptureOverlayWindow selector = LongRunField<CaptureOverlayWindow>(_recorder, "_selectionOverlay");
            selector.CompleteSelection(new RectD(0, 0, 640, 360));
            controls = LongRunField<RecordingControlWindow>(_recorder, "_controls");
            controls.RecordingFinished += onFinished; controls.Failed += onFailed;
            Button primary = LongRunField<Button>(controls, "_primaryButton");
            if (!primary.IsEnabled || !controls.IsVisible) throw new InvalidOperationException("Recording start control unavailable.");
            primary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, primary));
            await ProbeWaitAsync(() => failed is not null || (controls.IsRecording && Interlocked.Read(ref frames) >= 30),
                30_000, "actual recorder synthetic-frame producer");
            if (failed is not null) throw new InvalidOperationException("Recording failed.", failed);
            RegionFrameGrabber activeGrabber = LongRunField<RegionFrameGrabber>(LongRunField<RegionRecorder>(controls, "_recorder"), "_grabber");
            if (!activeGrabber.UsesSyntheticFrames || activeGrabber.ActiveBackend is not null || activeGrabber.WgcDiagnostics is not null)
                throw new InvalidDataException("Recording used a native acquisition backend.");
            controls.RequestStop();
            await ProbeWaitAsync(() => finished is not null || failed is not null, 60_000, "real encoder finalize");
            if (failed is not null || finished is null) throw new InvalidOperationException("Recording finalization failed.", failed);
            await ProbeWaitAsync(() => !_recorder.HasRecordingSession && _queue.Records.Any(record => !before.Contains(record.Id) && record.IsVideo),
                60_000, "video queue publication and real editor handoff");
            CaptureRecord record = NewLongRunRecord(before, CaptureMediaKind.Video);
            if (finished.EmittedFrames <= 0 || finished.Width != 640 || finished.Height != 360
                || record.FrameCount != finished.EmittedFrames || record.Width != 640 || record.Height != 360
                || record.DurationMs <= 0 || Interlocked.Read(ref frames) < finished.EmittedFrames)
                throw new InvalidDataException("Recording result/registered media dimensions or frame counts differ.");
            string video = _queue.GetFilePath(record, CaptureFileNames.VideoSource);
            if (new FileInfo(video).Length <= 0 || !File.Exists(_queue.GetFilePath(record, CaptureFileNames.Thumbnail)))
                throw new InvalidDataException("Completed video/thumbnail is absent.");
            RequireDurableRecord(record);
            var editor = LongRunField<VideoEditorWindow>(_recorder, "_editor");
            await NextLongRunRenderingAsync();
            editor.Close();
            await ProbeWaitAsync(() => !_recorder.IsActive, 15_000, "recording editor and session closed");
            evidence.Record("recording-completed", new { record.Id, syntheticFrameCalls = Interlocked.Read(ref frames),
                nativeCaptureBackend = activeGrabber.ActiveBackend?.ToString(), activeGrabber.UsesSyntheticFrames,
                source = "SYNTHETIC_BGRA", finished.EmittedFrames,
                finished.DurationMs, finished.Performance, videoSha256 = ResidentLongRunInputs.HashFile(video) });
            return new { record.Id, finished.EmittedFrames, finished.DurationMs,
                completion = "real coordinator + synthetic grabber + MF encoder finalize + queue + editor close" };
        }
        finally
        {
            if (controls is not null) { controls.RecordingFinished -= onFinished; controls.Failed -= onFailed; }
            // The grabber captured its callback. Do not dispose an active recorder to make failure look complete.
            if (!_recorder.HasRecordingSession) _recorder.SyntheticRecordingFrame = null;
        }
    }

    private CaptureRecord NewLongRunRecord(HashSet<Guid> before, CaptureMediaKind kind)
    {
        CaptureRecord[] added = _queue!.Records.Where(record => !before.Contains(record.Id)).ToArray();
        if (added.Length != 1 || added[0].MediaKind != kind) throw new InvalidDataException("Actual capture admission differs.");
        _longRunOwnedRecords.Add(added[0].Id);
        return added[0];
    }

    private void RequireDurableRecord(CaptureRecord record)
    {
        string index = _services!.GetRequiredService<MyCapture.Core.Storage.AppPaths>().IndexFile;
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(index));
        if (!json.RootElement.GetProperty("records").EnumerateArray().Any(row => row.GetProperty("id").GetGuid() == record.Id))
            throw new InvalidDataException("Capture absent from the durable index.");
        if (!File.Exists(_queue!.GetFilePath(record, CaptureFileNames.Meta))) throw new InvalidDataException("Durable metadata absent.");
    }

    private async Task NormalizeLongRunQueueAsync()
    {
        // Keep two image records and one video. This is a bounded, rotating owned workload, not
        // a 1,000-record library benchmark. Recovery history remains in the normal product store.
        if (_queue!.PrepareRemoval is null) throw new InvalidOperationException("Recoverable removal unavailable.");
        if (_queue.Records.Any(record => !_longRunOwnedRecords.Contains(record.Id)))
            throw new InvalidDataException("Unexpected non-owned record in isolated profile.");
        Guid[] obsolete = _queue.Records.Where(record => !record.IsVideo).Skip(2)
            .Concat(_queue.Records.Where(record => record.IsVideo).Skip(1)).Select(record => record.Id).ToArray();
        if (obsolete.Length == 0) return;
        CaptureBatchRemovalResult result = await _queue.RemoveManyAsync(obsolete);
        if (result.RemovedCount != obsolete.Length || result.CleanupPendingCount != 0 || result.RecycleUnavailableIds.Count != 0)
            throw new IOException("Recycle Bin removal incomplete; no permanent fallback is permitted.");
        foreach (Guid id in obsolete) _longRunOwnedRecords.Remove(id);
    }

    private async Task<ResidentLongRunContract.State> LongRunQuiescentAsync()
    {
        await ProbeWaitAsync(() => !_indexingRunning && !_indexingRequested && !_ocrMemoryBusy
            && _capturePersistenceTask.IsCompleted && _sourceBackfillTask.IsCompleted
            && LongRunField<Task>(_queue!, "_publicationTail").IsCompleted
            && LongRunPinSessionIdle() && LongRunThumbnailWorkIdle()
            && _overlay?.IsActive != true && _quickAccess is null
            && _pins?.Count == 0 && _pins.IsInteractionActive == false && _recorder?.IsActive != true
            && _galleryWindow is not { IsVisible: true } && _ocrPresenter?.Window.IsVisible != true
            && Current.Windows.Cast<Window>().All(window => !window.IsVisible),
            60_000, "all real resident owners quiescent");
        await _capturePersistenceTask;
        await _sourceBackfillTask;
        await LongRunField<Task>(_queue!, "_publicationTail");
        if (_pinSession is not null) await LongRunField<Task>(_pinSession, "_worker");
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        GalleryViewModel? vm = _galleryWindow is null ? null : LongRunField<GalleryViewModel>(_galleryWindow, "_viewModel");
        int cached = vm is null ? 0 : LongRunField<Dictionary<Guid, GalleryItemViewModel>>(vm, "_tileCache")
            .Values.Count(tile => tile.CachedThumbnailBytes > 0);
        IServiceProvider services = _services ?? throw new InvalidOperationException("Resident services are unavailable.");
        NeuralOcrEngine neural = services.GetRequiredService<NeuralOcrEngine>();
        TextTranslationService translation = services.GetRequiredService<TextTranslationService>();
        bool rowReader = LongRunNullableField(neural, "_rowReader") is not null;
        int translationCacheCount = LongRunField<Dictionary<string, string>>(translation, "_cache").Count;
        string windows = string.Join(";", Current.Windows.Cast<Window>().GroupBy(window => window.GetType().FullName ?? window.GetType().Name)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key + ":hidden=" + group.Count()));
        return new(windows.Length == 0 ? "all-windows-closed" : windows, _queue!.Count, cached, vm?.CachedThumbnailBytes ?? 0,
            _pins!.ClosedCount, _pins.ClosedRetainedBytes,
            $"whole-neural:{neural.IsReady};row-neural:{rowReader};translation:{translation.IsEngineLoaded};translation-cache:{translationCacheCount}", 0);
    }

    private bool LongRunPinSessionIdle() => _pinSession is null ||
        (LongRunField<Task>(_pinSession, "_worker").IsCompleted
         && LongRunNullableField(_pinSession, "_pendingRead") is null
         && !LongRunField<DispatcherTimer>(_pinSession, "_saveDelay").IsEnabled
         && !LongRunField<DispatcherTimer>(_pinSession, "_cleanupDelay").IsEnabled);

    private bool LongRunThumbnailWorkIdle() => _galleryWindow is null ||
        LongRunField<Dictionary<Guid, GalleryItemViewModel>>(
            LongRunField<GalleryViewModel>(_galleryWindow, "_viewModel"), "_tileCache").Values
            .All(tile => tile.PendingThumbnailLoad.IsCompleted);

    private static List<GalleryThumbnailImage> VisibleLibraryImages(Window window) =>
        LongRunVisuals((ListBox)window.FindName("RowsList")).OfType<GalleryThumbnailImage>()
            .Where(image => LongRunVisibleArea(LongRunPreviewSlot(image), window)).ToList();

    private static FrameworkElement LongRunPreviewSlot(GalleryThumbnailImage image)
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(image); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Name: "TilePreview" } preview) return preview;
        throw new InvalidOperationException("Thumbnail layout slot is absent.");
    }

    private static bool LongRunVisibleArea(FrameworkElement element, Window window)
    {
        if (!element.IsVisible || !element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        Rect visible = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        visible.Intersect(new Rect(window.RenderSize));
        bool viewport = false;
        for (DependencyObject? node = element; node is not null && node != window; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not UIElement ancestor) continue;
            if (!ancestor.IsVisible) return false;
            GeneralTransform transform = ancestor.TransformToAncestor(window);
            if (ancestor is ScrollContentPresenter) viewport = true;
            if (ancestor.ClipToBounds || ancestor is ScrollContentPresenter)
                visible.Intersect(transform.TransformBounds(new Rect(ancestor.RenderSize)));
            if (ancestor.Clip is { } clip) visible.Intersect(transform.TransformBounds(clip.Bounds));
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
        }
        return viewport && !visible.IsEmpty && visible.Width > 0 && visible.Height > 0;
    }

    private static bool LibraryImagesReady(Window window)
    {
        List<GalleryThumbnailImage> images = VisibleLibraryImages(window);
        return images.Count > 0 && images.All(image => image.Source is BitmapSource && !image.IsFading
            && image.ActualWidth > 0 && image.ActualHeight > 0 && AncestorsOpaque(image, window));
    }

    private static bool AncestorsOpaque(DependencyObject child, Window window)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && (!element.IsVisible || element.Opacity != 1)) return false;
            if (ReferenceEquals(current, window)) return true;
        }
        return false;
    }

    private static IEnumerable<DependencyObject> LongRunVisuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject item in LongRunVisuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }

    private static async Task NextLongRunRenderingAsync()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => rendered.TrySetResult();
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
}
