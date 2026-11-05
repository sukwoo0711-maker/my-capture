using MyCapture.Core.Platform;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using MyCapture.Platform.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace MyCapture.Ocr;

/// <summary>
/// PP-OCRv5 text recognizer for captures and imported images. Detection uses the RapidAI
/// server detector; the Korean mobile recognizer also covers digits and Latin text.
/// </summary>
internal sealed class NeuralOcrEngine : IDisposable
{
    // Opt-in diagnostic boundaries only; the null production path does not read the clock.
    internal enum WholeObservationStage { BeforeTaskRun, WorkerEntered, BeforeDetect, AfterDetect }
    /// <summary>
    /// The row recognizer leaves after this quiet spell. Pictures translated one after another
    /// share it, and it is gone well before a closed translation pin's memory settles.
    /// </summary>
    internal static readonly TimeSpan RowReaderIdleRelease = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The whole-image sessions (detector, angle classifier and recognizer) leave after this quiet
    /// spell. Library indexing reads new captures one after another on them; before, the first
    /// Accurate reading kept them, and everything they had allocated, until the app exited.
    /// </summary>
    internal static readonly TimeSpan EngineIdleRelease = TimeSpan.FromSeconds(20);

    private readonly OcrModelStore _store;
    private readonly ILogger _log;
    private readonly OnnxRuntimeInitialization _runtime;
    private readonly TimeSpan _engineIdleRelease;
    private readonly bool _disableWholeSessionSpinning;
    private readonly Action<SessionOptions, string, string, string, string>? _wholeSessionInitializing;
    private readonly Action<RapidOcrNet.OcrResult>? _wholeResultObserved;
    private readonly IProgress<(int Completed, int Total)>? _wholeProgressObserved;
    private readonly Action<WholeObservationStage, long>? _wholeStageObserved;
    private readonly object _sync = new();
    private readonly object _rowSync = new();
    private readonly Timer _engineIdle;
    private readonly Timer _rowIdle;
    private RapidOcr? _engine;
    private TextRecognizer? _rowReader;
    private bool _initFailed;
    private bool _disposed;

    internal NeuralOcrEngine(
        OcrModelStore store,
        ILogger<NeuralOcrEngine> log,
        OnnxRuntimeInitialization? runtime = null,
        TimeSpan? engineIdleRelease = null,
        bool disableWholeSessionSpinning = false,
        Action<SessionOptions, string, string, string, string>? wholeSessionInitializing = null,
        Action<RapidOcrNet.OcrResult>? wholeResultObserved = null,
        IProgress<(int Completed, int Total)>? wholeProgressObserved = null)
        : this(store, log, runtime, engineIdleRelease, disableWholeSessionSpinning,
            wholeSessionInitializing, wholeResultObserved, wholeProgressObserved, wholeStageObserved: null)
    {
    }

    // Preserve the original constructor signature; only a diagnostic caller selects this overload.
    internal NeuralOcrEngine(
        OcrModelStore store,
        ILogger<NeuralOcrEngine> log,
        OnnxRuntimeInitialization? runtime,
        TimeSpan? engineIdleRelease,
        bool disableWholeSessionSpinning,
        Action<SessionOptions, string, string, string, string>? wholeSessionInitializing,
        Action<RapidOcrNet.OcrResult>? wholeResultObserved,
        IProgress<(int Completed, int Total)>? wholeProgressObserved,
        Action<WholeObservationStage, long>? wholeStageObserved)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _runtime = runtime ?? OnnxRuntimeInitialization.Shared;
        _engineIdleRelease = engineIdleRelease ?? EngineIdleRelease;
        _disableWholeSessionSpinning = disableWholeSessionSpinning;
        _wholeSessionInitializing = wholeSessionInitializing;
        _wholeResultObserved = wholeResultObserved;
        _wholeProgressObserved = wholeProgressObserved;
        _wholeStageObserved = wholeStageObserved;
        _engineIdle = new Timer(_ => ReleaseEngineIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
        _rowIdle = new Timer(_ => ReleaseRowReaderIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
        ModelVerificationTask = _store.PreVerifyInstalledModelsAsync();
    }

    /// <summary>
    /// Installed-model verification runs independently of construction and never downloads weights.
    /// </summary>
    internal Task ModelVerificationTask { get; }

    /// <summary>Raised on the releasing thread after the whole-image sessions have left.</summary>
    internal event Action? EngineReleased;

    /// <summary>Raised on the releasing thread after the row recognizer has left.</summary>
    internal event Action? RowReaderReleased;

    internal bool CanRun => AppArchitecture.SupportsNeuralOcr && _store.HasRequiredModels;

    /// <summary>Whether the whole-image sessions are loaded now; they leave after <see cref="EngineIdleRelease"/>.</summary>
    /// <remarks>Read without the lock, so a question never makes the idle timer miss its turn.</remarks>
    internal bool IsReady => Volatile.Read(ref _engine) is not null;

    internal async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AppArchitecture.SupportsNeuralOcr) return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
        if (!await _store.EnsureAsync(cancellationToken).ConfigureAwait(false))
        {
            return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
        }

        // ONNX init + detect must not run on the WPF dispatcher. A large receipt otherwise
        // freezes the shell long enough to look like a hang or failed launch.
        _wholeStageObserved?.Invoke(WholeObservationStage.BeforeTaskRun, Stopwatch.GetTimestamp());
        return await Task.Run(() => RecognizeOnWorker(request, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private OcrResult RecognizeOnWorker(OcrRequest request, CancellationToken cancellationToken)
    {
        _wholeStageObserved?.Invoke(WholeObservationStage.WorkerEntered, Stopwatch.GetTimestamp());
        if (!TryInitialize())
        {
            return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
        }

        BitmapSource? source = Decode(request);
        if (source is null)
        {
            return OcrResult.Failed(UiText.Get("Text_475D185DC33B"), TimeSpan.Zero);
        }

        if (request.EnhanceContrast)
        {
            source = ImageCodec.StretchContrastForRecognition(source);
        }

        try
        {
            (BitmapSource pixels, SKAlphaType alpha) = ForSkia(source);
            using SKBitmap bitmap = CopyToSkia(pixels, alpha, new Int32Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight));
            lock (_sync)
            {
                // The idle timer may have taken the sessions since the check above; they load again.
                if (!TryInitializeLocked()) return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
                RapidOcr engine = _engine ?? throw new InvalidOperationException("PP-OCR session was not initialized.");
                SetEngineIdle(Timeout.InfiniteTimeSpan);
                try
                {
                    _wholeStageObserved?.Invoke(WholeObservationStage.BeforeDetect, Stopwatch.GetTimestamp());
                    RapidOcrNet.OcrResult raw = _wholeProgressObserved is { } progress
                        ? engine.Detect(bitmap, CreateOptions(), progress, cancellationToken)
                        : engine.Detect(bitmap, CreateOptions(), cancellationToken);
                    _wholeStageObserved?.Invoke(WholeObservationStage.AfterDetect, Stopwatch.GetTimestamp());
                    _wholeResultObserved?.Invoke(raw);
                    return ToResult(raw);
                }
                finally
                {
                    if (!SetEngineIdle(_engineIdleRelease)) ReleaseEngine();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PP-OCR detect failed");
            return OcrResult.Failed(UiText.Get("Text_AA254F35F02E"), TimeSpan.Zero);
        }
    }

    internal static RapidOcrOptions CreateOptions() => RapidOcrOptions.Default with
    {
        ReturnWordBox = true,
        // Most capture text is upright. Requiring stronger evidence avoids flipping short
        // Korean UI lines, while retaining angle correction for imported/edited images and
        // pins. SearchRotatedOrientations controls Windows whole-image search; it does not
        // establish that an input is upright (background indexing disables that search).
        DoAngle = true,
        ClsThresh = 0.99f,
        RecMaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
    };

    internal bool TryInitialize()
    {
        if (!AppArchitecture.SupportsNeuralOcr) return false;
        lock (_sync)
        {
            return TryInitializeLocked();
        }
    }

    private bool TryInitializeLocked()
    {
        if (_engine is not null)
        {
            // An idle timer that found the lock taken gave up its turn; the quiet spell starts again.
            SetEngineIdle(_engineIdleRelease);
            return true;
        }

        if (_disposed || _initFailed || !_store.HasRequiredModels)
        {
            return false;
        }

        string? cls = Bundled(RapidOcr.DefaultClsModelPath);
        string? bundledDet = Bundled(RapidOcr.DefaultDetModelPath);
        string det = _store.IsPresent(OcrModelCatalog.Detection)
            ? _store.PathFor(OcrModelCatalog.Detection)
            : bundledDet ?? string.Empty;
        string rec = _store.PathFor(OcrModelCatalog.Recognition);
        string keys = _store.PathFor(OcrModelCatalog.Dictionary);
        if (cls is null || string.IsNullOrEmpty(det))
        {
            _log.LogWarning(
                "Bundled PP-OCR detector or classifier was not found. BaseDirectory={BaseDirectory}; Process={Process}",
                AppContext.BaseDirectory,
                Environment.ProcessPath);
            _initFailed = true;
            return false;
        }

        try
        {
            _runtime.EnsureInitialized();
            var engine = new RapidOcr();
            using SessionOptions options = RapidOcr.GetDefaultSessionOptions();
            // Every capture has its own size, and the arena kept what the largest reading had needed,
            // grown in powers of two, for as long as the session lived: 2.7 GB after six screen
            // captures. Without it each reading gives its memory back. The memory pattern stays on
            // and packs a reading's tensors into one block; with the pixels no longer passed through
            // a PNG (ForSkia), a size read before takes about 2 % longer than in 5.0.0, a new one 12 %.
            options.EnableCpuMemArena = false;
            if (_disableWholeSessionSpinning)
            {
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            }
            _wholeSessionInitializing?.Invoke(options, det, cls, rec, keys);
            engine.InitModels(det, cls, rec, keys, options);
            Volatile.Write(ref _engine, engine);
            // An engine loaded and then not used, e.g. by an image that fails to decode, still leaves.
            SetEngineIdle(_engineIdleRelease);
            _log.LogInformation("PP-OCR session ready det={Det} rec={Rec}", det, rec);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PP-OCR session could not be created det={Det} rec={Rec} keys={Keys}", det, rec, keys);
            _initFailed = true;
            return false;
        }
    }

    /// <summary>
    /// Reads rows that Windows OCR has already located with the recognizer alone. Image
    /// translation re-reads only a few short rows, so it needs neither the detector nor the
    /// angle classifier, nor the memory a whole-image pass leaves in ONNX Runtime. Each row's
    /// Windows words also mark where its reading splits into words.
    /// </summary>
    internal async Task<OcrResult> ReadRowsAsync(OcrRequest request, IReadOnlyList<OcrLine> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);
        if (!CanRun) return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
        return await Task.Run(() => ReadRowsOnWorker(request, rows, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private OcrResult ReadRowsOnWorker(OcrRequest request, IReadOnlyList<OcrLine> rows, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        BitmapSource? source = Decode(request);
        if (source is null) return OcrResult.Failed(UiText.Get("Text_475D185DC33B"), TimeSpan.Zero);
        if (request.EnhanceContrast) source = ImageCodec.StretchContrastForRecognition(source);
        (BitmapSource pixels, SKAlphaType alpha) = ForSkia(source);

        var lines = new List<OcrLine>(rows.Count);
        lock (_rowSync)
        {
            if (_disposed) return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
            SetRowIdle(Timeout.InfiniteTimeSpan);
            try
            {
                TextRecognizer? reader = _rowReader ??= LoadRowReader();
                if (reader is null) return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
                foreach (OcrLine row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Int32Rect crop = RowCrop(row.Bounds, pixels.PixelWidth, pixels.PixelHeight);
                    if (crop.IsEmpty) continue;
                    using SKBitmap strip = CopyToSkia(pixels, alpha, crop);
                    TextLine read = reader.GetTextLine(strip, cancellationToken);
                    if (RowLine(read.Chars, read.CharScores, read.CharCols, read.ColCount, crop,
                            [.. row.Words.Select(word => word.Bounds)]) is { } line)
                        lines.Add(line);
                }
            }
            finally
            {
                if (!SetRowIdle(RowReaderIdleRelease)) ReleaseRowReader();
            }
        }

        return lines.Count == 0
            ? OcrResult.NoText("ko-KR", watch.Elapsed)
            : OcrResult.Success(OcrPlanner.BuildBlockText(lines.Select(line => line.Text)), "ko-KR", lines, watch.Elapsed);
    }

    /// <summary>
    /// The pixels as PP-OCR's Skia steps should see them: premultiplied when the image has alpha,
    /// and opaque with alpha 255 when it has none. These are the bitmaps a PNG of the image decodes
    /// to; writing and reading that PNG took 15 to 74 ms per screen capture.
    /// </summary>
    /// <remarks>
    /// A capture's Bgr32 fourth byte is undefined, and Skia takes it as alpha even in a bitmap
    /// called opaque: with zeros there, a whole capture gave no line and rows read empty. The
    /// conversion writes 255. Left unfrozen: freezing a conversion can walk a decoder that belongs
    /// to another thread.
    /// </remarks>
    internal static (BitmapSource Pixels, SKAlphaType Alpha) ForSkia(BitmapSource source)
    {
        if (!ImageCodec.HasAlpha(source))
            return (new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0), SKAlphaType.Opaque);
        return source.Format == PixelFormats.Pbgra32
            ? (source, SKAlphaType.Premul)
            : (new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0), SKAlphaType.Premul);
    }

    internal static SKBitmap CopyToSkia(BitmapSource pixels, SKAlphaType alpha, Int32Rect crop)
    {
        var bitmap = new SKBitmap(new SKImageInfo(crop.Width, crop.Height, SKColorType.Bgra8888, alpha));
        try
        {
            pixels.CopyPixels(crop, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private TextRecognizer? LoadRowReader()
    {
        string rec = _store.PathFor(OcrModelCatalog.Recognition);
        if (!_store.HasRequiredModels) return null;
        var reader = new TextRecognizer();
        try
        {
            _runtime.EnsureInitialized();
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1,
                // A row needs a few megabytes; an arena would keep the largest row's for the session.
                EnableCpuMemArena = false,
                EnableMemoryPattern = false,
            };
            reader.InitModel(rec, _store.PathFor(OcrModelCatalog.Dictionary), options);
            _log.LogInformation("PP-OCR row recognizer ready rec={Rec}", rec);
            return reader;
        }
        catch (Exception ex)
        {
            reader.Dispose();
            _log.LogWarning(ex, "PP-OCR row recognizer could not be created rec={Rec}", rec);
            return null;
        }
    }

    /// <summary>
    /// Windows word boxes hug the ink, while the recognizer learned from detector boxes that
    /// keep a margin around the text: pad by a quarter of the row height inside the image.
    /// </summary>
    internal static Int32Rect RowCrop(OcrRect row, int width, int height)
    {
        if (!OcrGeometry.IsValid(row)) return Int32Rect.Empty;
        double pad = Math.Max(2, row.Height * 0.25);
        int left = Math.Max(0, (int)Math.Floor(row.X - pad)), top = Math.Max(0, (int)Math.Floor(row.Y - pad));
        int right = Math.Min(width, (int)Math.Ceiling(row.Right + pad)), bottom = Math.Min(height, (int)Math.Ceiling(row.Bottom + pad));
        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty;
    }

    /// <summary>
    /// One recognized row crop as a line in source coordinates, with the words and boxes that
    /// RapidOcrNet's CalRecBoxes (Apache-2.0) gives detected lines: a word ends at a space, at a
    /// switch between CJK and other characters, or after a gap of more than five CTC columns;
    /// each character is a cell of the average character width centred on its column, and
    /// neighbouring cells split any overlap. Small text often loses its spaces, so a word also
    /// ends where its characters pass from one Windows word box (<paramref name="guides"/>) to the
    /// next. Rows below the pipeline's text score are dropped.
    /// </summary>
    internal static OcrLine? RowLine(string[]? chars, float[]? scores, int[]? cols, int colCount, Int32Rect crop,
        IReadOnlyList<OcrRect> guides)
    {
        if (chars is not { Length: > 0 } || cols is null || scores is null || cols.Length != chars.Length
            || scores.Length != chars.Length || colCount <= 0 || scores.Average() < RapidOcrOptions.Default.TextScore)
            return null;

        float width = crop.Width, columnWidth = width / colCount;
        // The Windows word whose box holds the character's column, or one within a column of it.
        int Guide(int index)
        {
            double centre = crop.X + (cols[index] + 0.5) * columnWidth;
            int best = -1;
            double nearest = columnWidth;
            for (int guide = 0; guide < guides.Count; guide++)
            {
                double distance = Math.Max(0, Math.Max(guides[guide].X - centre, centre - guides[guide].Right));
                if (distance > nearest || distance == nearest && best >= 0) continue;
                (best, nearest) = (guide, distance);
            }
            return best;
        }

        var words = new List<(int First, int Count, bool Cjk)>();
        int first = 0, count = 0, owner = -1;
        bool? cjkState = null;
        for (int index = 0; index < chars.Length; index++)
        {
            string character = chars[index];
            if (character.Length == 0 || character.Length == 1 && char.IsWhiteSpace(character[0]))
            {
                if (count > 0) words.Add((first, count, cjkState ?? false));
                count = 0;
                continue;
            }
            bool cjk = character.Any(IsCjk);
            cjkState ??= cjk;
            int gap = index == 0 ? Math.Min(cjk ? 3 : 2, cols[0]) : cols[index] - cols[index - 1];
            int guide = Guide(index);
            if (cjkState != cjk || gap > 5 || count > 0 && guide != owner)
            {
                if (count > 0) words.Add((first, count, cjkState.Value));
                count = 0;
                cjkState = cjk;
            }
            owner = guide;
            if (count++ == 0) first = index;
        }
        if (count > 0) words.Add((first, count, cjkState ?? false));
        if (words.Count == 0) return null;

        float[] spans = [.. words.Where(word => word.Count > 1)
            .Select(word => (cols[word.First + word.Count - 1] - cols[word.First]) * columnWidth / (word.Count - 1))];
        float charWidth = spans.Length > 0 ? spans.Average() : width / string.Concat(chars).Length;
        (float Left, float Right) Cell(int index)
        {
            float centre = (cols[index] + 0.5f) * columnWidth;
            float left = MathF.Max(centre - charWidth / 2, 0), right = MathF.Min(centre + charWidth / 2, width);
            return (left, right > left ? right : MathF.Min(left + 1, width));
        }

        var cells = new List<(string Text, float Left, float Right)>();
        bool perWord = words.All(word => !word.Cjk);
        foreach ((int start, int length, _) in words)
        {
            if (perWord)
            {
                (float Left, float Right)[] range = [.. Enumerable.Range(start, length).Select(Cell)];
                cells.Add((string.Concat(chars[start..(start + length)]), range.Min(cell => cell.Left), range.Max(cell => cell.Right)));
            }
            else
            {
                cells.AddRange(Enumerable.Range(start, length).Select(index => (chars[index], Cell(index).Left, Cell(index).Right)));
            }
        }
        cells.Sort((a, b) => a.Left.CompareTo(b.Left));
        for (int index = 0; index + 1 < cells.Count; index++)
        {
            float overlap = cells[index].Right - cells[index + 1].Left;
            if (overlap <= 0) continue;
            cells[index] = cells[index] with { Right = cells[index].Right - overlap / 2 };
            cells[index + 1] = cells[index + 1] with { Left = cells[index + 1].Left + (overlap - overlap / 2) };
        }

        int top = crop.Y, height = crop.Height;
        OcrWord[] boxes = [.. cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).Select(cell =>
        {
            int left = (int)MathF.Round(crop.X + cell.Left), right = (int)MathF.Round(crop.X + cell.Right);
            return new OcrWord(cell.Text.Trim(), new OcrRect(left, top, Math.Max(1, right - left), height));
        })];
        string text = string.Concat(chars).Trim();
        return boxes.Length == 0 || text.Length == 0
            ? null
            : new OcrLine(text, new OcrRect(crop.X, crop.Y, crop.Width, crop.Height), boxes);
    }

    private static bool IsCjk(char value) => value is >= '一' and <= '鿿' or >= '　' and <= '〿'
        or >= '＀' and <= '￯';

    private bool SetEngineIdle(TimeSpan dueTime)
    {
        try
        {
            return _engineIdle.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Whoever holds the lock starts the timer again: a reading when it ends, a load check at once.</summary>
    private void ReleaseEngineIfIdle()
    {
        if (!Monitor.TryEnter(_sync)) return;
        try
        {
            ReleaseEngine();
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void ReleaseEngine()
    {
        if (_engine is not { } engine) return;
        Volatile.Write(ref _engine, null);
        engine.Dispose();
        EngineReleased?.Invoke();
    }

    private bool SetRowIdle(TimeSpan dueTime)
    {
        try
        {
            return _rowIdle.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ReleaseRowReaderIfIdle()
    {
        if (!Monitor.TryEnter(_rowSync)) return;
        try
        {
            ReleaseRowReader();
        }
        finally
        {
            Monitor.Exit(_rowSync);
        }
    }

    private void ReleaseRowReader()
    {
        if (_rowReader is null) return;
        _rowReader.Dispose();
        _rowReader = null;
        RowReaderReleased?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _engineIdle.Dispose();
            ReleaseEngine();
        }

        lock (_rowSync)
        {
            _rowIdle.Dispose();
            ReleaseRowReader();
        }
    }

    private static string? Bundled(string fileName)
    {
        foreach (string root in CandidateRoots())
        {
            string path = Path.Combine(root, RapidOcr.ModelsFolderName, RapidOcr.ModelsVersion, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return AppContext.BaseDirectory;
        string? process = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(process))
        {
            yield return process;
        }

        string? assembly = Path.GetDirectoryName(typeof(NeuralOcrEngine).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assembly))
        {
            yield return assembly;
        }
    }

    private static BitmapSource? Decode(OcrRequest request)
    {
        if (request.Bitmap is not null)
        {
            return request.Bitmap;
        }

        if (request.EncodedImage is not null)
        {
            try
            {
                using var stream = new MemoryStream(request.EncodedImage, writable: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            return ImageCodec.TryLoad(request.FilePath);
        }

        return null;
    }

    private static OcrResult ToResult(RapidOcrNet.OcrResult raw, double scale = 1.0)
    {
        if (raw.TextBlocks is null || raw.TextBlocks.Length == 0)
        {
            return OcrResult.NoText("ko-KR", TimeSpan.FromMilliseconds(raw.DetectTime));
        }

        var lines = new List<OcrLine>(raw.TextBlocks.Length);
        var angles = new List<double?>();
        foreach (TextBlock block in raw.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            OcrRect lineBox = ToRect(block.BoxPoints, scale);
            IReadOnlyList<OcrWord> words;
            if (block.WordResults is { Length: > 0 })
            {
                words = block.WordResults
                    .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                    .Select(word => new OcrWord(word.Text.Trim(), ToRect(word.BoxPoints, scale)))
                    .ToArray();
            }
            else
            {
                words = [new OcrWord(block.Text.Trim(), lineBox)];
            }

            if (words.Count == 0)
            {
                continue;
            }

            lines.Add(new OcrLine(block.Text.Trim(), lineBox, words));
            angles.Add(TextAngle(block.BoxPoints, block.AngleIndex));
        }

        if (lines.Count == 0)
        {
            return OcrResult.NoText("ko-KR", TimeSpan.FromMilliseconds(raw.DetectTime));
        }

        string text = OcrPlanner.BuildBlockText(lines.Select(line => line.Text));
        return OcrResult.Success(text, "ko-KR", lines, TimeSpan.FromMilliseconds(raw.DetectTime), OcrResult.MostTilted(angles));
    }

    /// <summary>PP-OCR quadrilaterals run top-left to top-right in the source image.</summary>
    internal static double? TextAngle(SKPointI[]? points, int angleIndex = 0)
    {
        if (points is null || points.Length < 2) return null;
        double x = points[1].X - points[0].X, y = points[1].Y - points[0].Y;
        return x * x + y * y < 4 ? null : Math.Atan2(y, x) * 180 / Math.PI + (angleIndex == 1 ? 180 : 0);
    }

    private static OcrRect ToRect(SKPointI[]? points, double scale = 1.0)
    {
        if (points is null || points.Length == 0)
        {
            return new OcrRect(0, 0, 1, 1);
        }

        int minX = points[0].X;
        int minY = points[0].Y;
        int maxX = points[0].X;
        int maxY = points[0].Y;
        for (int i = 1; i < points.Length; i++)
        {
            minX = Math.Min(minX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxX = Math.Max(maxX, points[i].X);
            maxY = Math.Max(maxY, points[i].Y);
        }

        if (scale > 1.0)
        {
            int unscaledX = (int)Math.Round(minX / scale);
            int unscaledY = (int)Math.Round(minY / scale);
            int unscaledWidth = Math.Max(1, (int)Math.Round((maxX - minX) / scale));
            int unscaledHeight = Math.Max(1, (int)Math.Round((maxY - minY) / scale));
            return new OcrRect(unscaledX, unscaledY, unscaledWidth, unscaledHeight);
        }

        return new OcrRect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }
}
