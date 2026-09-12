using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Settings;

/// <summary>
/// How the colour picker reports the sampled pixel.
/// </summary>
public enum ColorFormat
{
    Hex,
    Rgb,
    Hsl,
}

/// <summary>
/// Everything the user can configure, persisted as <c>settings.json</c>.
/// </summary>
/// <remarks>
/// A plain mutable class with defaults on every property. Deserialising a file
/// written by an older version leaves new properties at their defaults, so no
/// migration code is needed for additive changes — which covers every change made
/// so far.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>
    /// Incremented only for changes that cannot be handled by defaulting.
    /// </summary>
    public int SchemaVersion { get; set; } = 3;

    public HotkeySettings Hotkeys { get; set; } = new();

    public QueueSettings Queue { get; set; } = new();

    public ExportSettings Export { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();

    public MyCapture.Core.Recording.RecordingSettings Recording { get; set; } = new();

    public PinSettings Pin { get; set; } = new();

    public AnnotationDefaults Annotation { get; set; } = new();

    public OcrSettings Ocr { get; set; } = new();

    public GitHubSettings GitHub { get; set; } = new();

    public GeneralSettings General { get; set; } = new();
}

public sealed class HotkeySettings
{
    /// <summary>
    /// Starts a region capture. Registered globally for as long as the app runs.
    /// </summary>
    public Hotkey Capture { get; set; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);

    /// <summary>Opens the unified image/video library.</summary>
    public Hotkey OpenLibrary { get; set; } = new(
        HotkeyModifiers.Control | HotkeyModifiers.Shift,
        Hotkey.VkZ);

    /// <summary>
    /// Pins the clipboard contents to the screen as a floating window.
    /// </summary>
    public Hotkey PasteToScreen { get; set; } = new(HotkeyModifiers.None, Hotkey.VkF3);

    /// <summary>
    /// Hides or reveals every pinned window at once.
    /// </summary>
    public Hotkey HideAllPins { get; set; } = new(HotkeyModifiers.Shift, Hotkey.VkF3);

    /// <summary>
    /// Toggles click-through on the pinned window under the cursor.
    /// </summary>
    /// <remarks>
    /// Unassigned by default. There is no obvious free chord for it, and silently
    /// claiming one would be worse than requiring a deliberate choice.
    /// </remarks>
    public Hotkey ToggleClickThrough { get; set; } = Hotkey.None;

    /// <summary>Repeats the previous capture region without showing the overlay.</summary>
    public Hotkey RepeatLastRegion { get; set; } = Hotkey.None;

    /// <summary>Captures the window currently under the cursor.</summary>
    public Hotkey CaptureWindow { get; set; } = Hotkey.None;

    /// <summary>Captures the entire monitor under the cursor.</summary>
    public Hotkey CaptureFullScreen { get; set; } = Hotkey.None;

    /// <summary>
    /// Starts (or stops) a region video recording. Ctrl+Shift+X by default.
    /// </summary>
    /// <remarks>
    /// A distinct default chord from region capture (Ctrl+Shift+C) so recording and
    /// still capture never collide. Pressing it again while recording stops it.
    /// </remarks>
    public Hotkey RecordRegion { get; set; } = new(
        HotkeyModifiers.Control | HotkeyModifiers.Shift,
        Hotkey.VkX);

    /// <summary>
    /// Uploads the clipboard image to the configured GitHub issue and copies the
    /// resulting user-attachments URL.
    /// </summary>
    public Hotkey UploadGitHubImage { get; set; } = new(HotkeyModifiers.None, Hotkey.VkF4);
}

public sealed class QueueSettings
{
    /// <summary>
    /// Maximum number of retained captures. Oldest unpinned entries are evicted.
    /// </summary>
    public int MaxItems { get; set; } = 300;

    /// <summary>
    /// Maximum total bytes on disk. Whichever limit is reached first triggers
    /// eviction.
    /// </summary>
    /// <remarks>
    /// 2 GiB. Without a byte cap a user capturing 4K screens fills the disk
    /// silently, because 300 items says nothing about their size.
    /// </remarks>
    public long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Overrides the capture storage directory. Empty means the default location.
    /// </summary>
    public string CapturesDirectoryOverride { get; set; } = string.Empty;

    /// <summary>
    /// Long edge of the generated gallery thumbnail, in pixels.
    /// </summary>
    public int ThumbnailLongEdge { get; set; } = 320;

    /// <summary>
    /// Hours after creation before an unpinned library image is expired.
    /// Default is 168 (7 days), matching the previous hard-coded policy.
    /// </summary>
    public int ImageRetentionHours { get; set; } = MyCapture.Core.Queue.CaptureRetention.DefaultImageRetentionHours;
}

public sealed class ExportSettings
{
    /// <summary>
    /// Destination for quick save. Empty means the default location.
    /// </summary>
    public string QuickSaveDirectoryOverride { get; set; } = string.Empty;

    /// <summary>
    /// Legacy persisted preference retained for settings-file compatibility. Edited images are
    /// now always copied to the clipboard after a successful commit.
    /// </summary>
    public bool CopyToClipboardOnQuickSave { get; set; } = true;

    /// <summary>
    /// Filename pattern for quick save, excluding the extension.
    /// </summary>
    public string FileNamePattern { get; set; } = "capture_{yyyyMMdd}_{HHmmss}";

    /// <summary>
    /// Preserve the alpha channel when the annotated result has transparency.
    /// </summary>
    public bool PreserveTransparency { get; set; } = true;
}

public sealed class CaptureSettings
{
    /// <summary>Include the mouse cursor in the captured bitmap.</summary>
    public bool IncludeCursor { get; set; }

    /// <summary>Highlight the window under the cursor as a snap candidate.</summary>
    public bool AutoDetectWindows { get; set; } = true;

    /// <summary>Show the pixel magnifier during selection.</summary>
    public bool ShowMagnifier { get; set; } = true;

    /// <summary>Format used when the colour picker copies a value.</summary>
    public ColorFormat ColorFormat { get; set; } = ColorFormat.Hex;

    /// <summary>Delay in seconds applied by the delayed-capture command.</summary>
    public int DelaySeconds { get; set; } = 3;

    /// <summary>
    /// Abort the capture when another application activates.
    /// </summary>
    /// <remarks>
    /// Matches Snipaste, where this is on by default and switchable. Leaving a
    /// full-screen overlay behind after focus loss is worse than losing a capture.
    /// </remarks>
    public bool AbortOnFocusLoss { get; set; } = true;

    /// <summary>Number of previous selection regions kept for replay.</summary>
    public int RegionHistoryLimit { get; set; } = 20;
}

public sealed class PinSettings
{
    /// <summary>Close the pinned window on double-click.</summary>
    public bool CloseOnDoubleClick { get; set; } = true;

    /// <summary>
    /// Legacy copy-gesture delay retained for settings-file compatibility, in milliseconds.
    /// </summary>
    /// <remarks>
    /// No longer controls a UI gesture. A focused pin uses <c>Ctrl+C</c> to copy its image;
    /// <c>Ctrl</c>+double-click copies retained source text or locally recognized image text.
    /// Preserve the serialized field and draft value when reading and saving older settings.
    /// </remarks>
    public int CtrlClickDebounceMs { get; set; } = 250;

    /// <summary>Opacity applied when a pinned window is first shown (0..1).</summary>
    public double InitialOpacity { get; set; } = 1.0;

    /// <summary>
    /// How many closed pinned windows can be restored by pressing the paste hotkey.
    /// </summary>
    /// <remarks>
    /// Snipaste defaults this to 1. 20 is kept here because the restore stack costs
    /// only metadata: the images are already in the capture queue on disk.
    /// </remarks>
    public int ClosedWindowRestoreLimit { get; set; } = 20;

    /// <summary>Scale step applied per mouse-wheel notch.</summary>
    public double ZoomStep { get; set; } = 0.1;
}

public sealed class AnnotationDefaults
{
    public string LastTool { get; set; } = "Rectangle";
    public MyCapture.Core.Annotations.AnnotationStrokeStyle StrokeStyle { get; set; }
    public double FillTransparency { get; set; } = 100;
    public ColorRgba StrokeColor { get; set; } = ColorRgba.FromRgb(0xEF, 0x44, 0x44);

    public double StrokeThickness { get; set; } = 3;

    public ColorRgba TextColor { get; set; } = ColorRgba.FromRgb(0xEF, 0x44, 0x44);

    public double FontSize { get; set; } = 18;

    public string FontFamily { get; set; } = "Malgun Gothic";

    /// <summary>Block size used by the mosaic tool, in image pixels.</summary>
    public int MosaicBlockSize { get; set; } = 12;

    /// <summary>Alpha applied by the highlighter (0..255).</summary>
    public byte HighlighterAlpha { get; set; } = 90;

    /// <summary>
    /// Recently used colours, most recent first. Surfaced in the toolbar swatches.
    /// </summary>
    public List<ColorRgba> RecentColors { get; set; } = [];
}

public sealed class OcrSettings
{
    /// <summary>
    /// BCP-47 tags to try, in order. Empty means use the system's preferences.
    /// </summary>
    public List<string> PreferredLanguages { get; set; } = ["ko-KR", "en-US"];

    /// <summary>
    /// Upscale factor applied before recognition.
    /// </summary>
    /// <remarks>
    /// Windows OCR accuracy drops sharply on small text, and UI screenshots are
    /// frequently 11-13px. Upscaling 2x before recognition is a large accuracy win
    /// for a small time cost.
    /// </remarks>
    public double UpscaleFactor { get; set; } = 2.0;

    /// <summary>Cache the recognised text on the capture record.</summary>
    public bool CacheResults { get; set; } = true;
}

public sealed class GeneralSettings
{
    public bool LaunchAtLogin { get; set; }

    /// <summary>
    /// Show a tray notification after a successful quick save.
    /// </summary>
    public bool NotifyOnQuickSave { get; set; } = true;

    /// <summary>
    /// Play the system shutter sound on a successful capture.
    /// </summary>
    public bool PlayCaptureSound { get; set; }

    /// <summary>UI language tag. Empty follows the OS.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Chrome palette id: midnight, daylight, or high-contrast.</summary>
    public string Theme { get; set; } = MyCapture.Core.Themes.AppThemeNames.Midnight;

    [JsonIgnore]
    public bool IsFirstRun { get; set; }
}

/// <summary>GitHub issue used as a clipboard-image host for F4.</summary>
public sealed class GitHubSettings
{
    /// <summary>
    /// Issue URL opened by F4. Empty is treated as
    /// <see cref="MyCapture.Core.GitHub.GitHubIssueImageUrl.DefaultIssueUrl"/>.
    /// </summary>
    public string IssueUrl { get; set; } = MyCapture.Core.GitHub.GitHubIssueImageUrl.DefaultIssueUrl;
}
