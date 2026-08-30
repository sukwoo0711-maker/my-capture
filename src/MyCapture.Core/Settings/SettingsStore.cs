using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;

namespace MyCapture.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Writes are debounced by the caller, not here: this type performs the write it is
/// asked to perform. It does, however, guarantee that a write is atomic and that a
/// corrupt file never prevents the app from starting.
/// </para>
/// <para>
/// Values are clamped on load rather than trusted. Settings files are hand-edited
/// and roam between machines, and a <c>MaxItems</c> of 0 or a negative debounce
/// would break the app in ways that are hard to attribute back to a config typo.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>
    /// Indented and camelCase: this file is documented as user-editable.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = JsonDefaults.Readable;

    private readonly AppPaths _paths;
    private readonly ILogger<SettingsStore> _log;

    public SettingsStore(AppPaths paths, ILogger<SettingsStore> log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Non-fatal problems found during the last <see cref="Load"/>, suitable for
    /// showing in the settings window.
    /// </summary>
    public IReadOnlyList<string> LastLoadWarnings { get; private set; } = [];

    public AppSettings Load()
    {
        _paths.EnsureCreated();
        AtomicFile.CleanUpTemp(_paths.SettingsFile);

        var warnings = new List<string>();

        string? text = AtomicFile.ReadAllTextWithRecovery(
            _paths.SettingsFile,
            candidate => TryDeserialize(candidate, out _));

        AppSettings settings;

        if (text is null)
        {
            bool existed = File.Exists(_paths.SettingsFile);
            if (existed)
            {
                warnings.Add("설정 파일을 읽을 수 없어 기본값으로 시작했습니다.");
                _log.LogWarning("Settings file exists but could not be parsed; using defaults");
            }

            settings = new AppSettings { General = { IsFirstRun = !existed } };
        }
        else if (TryDeserialize(text, out AppSettings? parsed) && parsed is not null)
        {
            settings = parsed;
        }
        else
        {
            // ReadAllTextWithRecovery already validated the text, so reaching here
            // means the validator and the real parse disagree. Treat as corrupt.
            warnings.Add("설정 파일이 손상되어 기본값으로 시작했습니다.");
            _log.LogWarning("Settings text passed validation but failed to deserialize; using defaults");
            settings = new AppSettings();
        }

        Sanitize(settings, warnings);
        LastLoadWarnings = warnings;

        return settings;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        AtomicFile.WriteAllText(_paths.SettingsFile, json);

        _log.LogDebug("Settings saved to {Path}", _paths.SettingsFile);
    }

    private static bool TryDeserialize(string text, out AppSettings? settings)
    {
        settings = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(text, SerializerOptions);
            return settings is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Forces every value into a range the app can actually operate in.
    /// </summary>
    private static void Sanitize(AppSettings s, List<string> warnings)
    {
        // --- Queue ---
        // The floor of 10 is not arbitrary: below that, capturing a handful of
        // screenshots in a row would start evicting work the user is still using.
        s.Queue.MaxItems = ClampWithWarning(
            s.Queue.MaxItems, 10, 5000, nameof(QueueSettings.MaxItems), warnings);

        // 128 MiB floor: a single 4K PNG can exceed 10 MiB, so a smaller cap would
        // evict almost immediately and make the queue useless.
        s.Queue.MaxBytes = ClampWithWarning(
            s.Queue.MaxBytes, 128L * 1024 * 1024, 512L * 1024 * 1024 * 1024,
            nameof(QueueSettings.MaxBytes), warnings);

        s.Queue.ThumbnailLongEdge = ClampWithWarning(
            s.Queue.ThumbnailLongEdge, 96, 1024, nameof(QueueSettings.ThumbnailLongEdge), warnings);

        // --- Export ---
        if (string.IsNullOrWhiteSpace(s.Export.FileNamePattern))
        {
            s.Export.FileNamePattern = "capture_{yyyyMMdd}_{HHmmss}";
            warnings.Add("파일명 패턴이 비어 있어 기본값으로 되돌렸습니다.");
        }

        // --- Capture ---
        s.Capture.DelaySeconds = ClampWithWarning(
            s.Capture.DelaySeconds, 0, 60, nameof(CaptureSettings.DelaySeconds), warnings);

        s.Capture.RegionHistoryLimit = ClampWithWarning(
            s.Capture.RegionHistoryLimit, 1, 200, nameof(CaptureSettings.RegionHistoryLimit), warnings);

        // --- Pin ---
        // The upper bound keeps a mistyped value from making Ctrl+click feel broken.
        s.Pin.CtrlClickDebounceMs = ClampWithWarning(
            s.Pin.CtrlClickDebounceMs, 120, 800, nameof(PinSettings.CtrlClickDebounceMs), warnings);

        // 0.2 floor: a fully transparent pinned window is unrecoverable by mouse.
        s.Pin.InitialOpacity = ClampWithWarning(
            s.Pin.InitialOpacity, 0.2, 1.0, nameof(PinSettings.InitialOpacity), warnings);

        s.Pin.ClosedWindowRestoreLimit = ClampWithWarning(
            s.Pin.ClosedWindowRestoreLimit, 0, 100, nameof(PinSettings.ClosedWindowRestoreLimit), warnings);

        s.Pin.ZoomStep = ClampWithWarning(
            s.Pin.ZoomStep, 0.02, 0.5, nameof(PinSettings.ZoomStep), warnings);

        // --- Annotation ---
        s.Annotation.StrokeThickness = ClampWithWarning(
            s.Annotation.StrokeThickness, 1, 64, nameof(AnnotationDefaults.StrokeThickness), warnings);

        s.Annotation.FontSize = ClampWithWarning(
            s.Annotation.FontSize, 6, 400, nameof(AnnotationDefaults.FontSize), warnings);

        s.Annotation.MosaicBlockSize = ClampWithWarning(
            s.Annotation.MosaicBlockSize, 2, 128, nameof(AnnotationDefaults.MosaicBlockSize), warnings);

        if (string.IsNullOrWhiteSpace(s.Annotation.FontFamily))
        {
            s.Annotation.FontFamily = "Malgun Gothic";
        }

        // Trim the recent-colour list so a long-lived settings file cannot grow
        // without bound.
        const int recentColorLimit = 12;
        if (s.Annotation.RecentColors.Count > recentColorLimit)
        {
            s.Annotation.RecentColors = s.Annotation.RecentColors.Take(recentColorLimit).ToList();
        }

        // --- OCR ---
        s.Ocr.UpscaleFactor = ClampWithWarning(
            s.Ocr.UpscaleFactor, 1.0, 4.0, nameof(OcrSettings.UpscaleFactor), warnings);

        if (s.Ocr.PreferredLanguages.Count == 0)
        {
            s.Ocr.PreferredLanguages = ["ko-KR", "en-US"];
        }

        // --- Hotkeys ---
        // A capture hotkey that cannot be pressed makes the app inert, so an
        // unassigned value is restored rather than respected.
        if (!s.Hotkeys.Capture.IsAssigned)
        {
            s.Hotkeys.Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
            warnings.Add("캡처 단축키가 비어 있어 Ctrl+Shift+C로 되돌렸습니다.");
        }
    }

    private static int ClampWithWarning(int value, int min, int max, string name, List<string> warnings)
    {
        int clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            warnings.Add($"{name} 값 {value}이(가) 허용 범위를 벗어나 {clamped}로 조정되었습니다.");
        }

        return clamped;
    }

    private static long ClampWithWarning(long value, long min, long max, string name, List<string> warnings)
    {
        long clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            warnings.Add($"{name} 값 {value}이(가) 허용 범위를 벗어나 {clamped}로 조정되었습니다.");
        }

        return clamped;
    }

    private static double ClampWithWarning(double value, double min, double max, string name, List<string> warnings)
    {
        // NaN would survive Math.Clamp comparisons unpredictably, so it is mapped
        // to the minimum explicitly.
        double sanitized = double.IsNaN(value) ? min : value;
        double clamped = Math.Clamp(sanitized, min, max);

        if (Math.Abs(clamped - value) > double.Epsilon)
        {
            warnings.Add($"{name} 값 {value}이(가) 허용 범위를 벗어나 {clamped}로 조정되었습니다.");
        }

        return clamped;
    }
}
