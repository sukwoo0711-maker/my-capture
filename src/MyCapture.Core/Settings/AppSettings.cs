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
public sealed class AppSettings : IJsonOnDeserializing
{
    /// <summary>
    /// Incremented only for changes that cannot be handled by defaulting.
    /// </summary>
    public int SchemaVersion { get; set; } = 4;

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

    // Files predating the general section also retain the legacy editor default.
    void IJsonOnDeserializing.OnDeserializing() => General.AfterCapture = AfterCaptureAction.Editor;

    /// <summary>Local state for the one-time Microsoft Store rating request. An explicit null reads as new.</summary>
    public StoreRatingState StoreRating { get; set => field = value ?? new(); } = new();

    public StoreUpdateState StoreUpdate { get; set => field = value ?? new(); } = new();

    public UpdateSettings Updates { get; set => field = value ?? new(); } = new();
}

/// <summary>Global shortcuts, registered for as long as the app runs.</summary>
/// <remarks>
/// The initialisers are the chords earlier versions shipped with, which an older settings file
/// without a property reads back. A new installation assigns region capture, region recording
/// and clipboard pin shortcuts (<see cref="NewInstallDefaults"/>).
/// </remarks>
public sealed class HotkeySettings
{
    /// <summary>Claims assigned core chords even when another program registered them first.</summary>
    public bool ForceCoreShortcuts { get; set; }

    /// <summary>The one-time core-shortcut question was answered; not edited in Settings.</summary>
    public bool CoreShortcutsAsked { get; set; }

    public Hotkey ScrollingCapture { get; set; } = Hotkey.None;
    public Hotkey DelayedCapture { get; set; } = Hotkey.None;
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
    public Hotkey UploadGitHubImage { get; set; } = new(HotkeyModifiers.None, Hotkey.VkF9);

    /// <summary>Reopens the most recently closed pinned window.</summary>
    /// <remarks>
    /// Unassigned by default, like the other optional commands: a missing property in an
    /// older settings file keeps this value, so upgrading never claims a chord silently.
    /// </remarks>
    public Hotkey RestoreClosedPin { get; set; } = Hotkey.None;

    /// <summary>
    /// Selects a region, recognises its text locally and copies only the text.
    /// </summary>
    /// <remarks>Unassigned by default, like the other optional capture commands.</remarks>
    public Hotkey CaptureText { get; set; } = Hotkey.None;

    /// <summary>
    /// Pauses or resumes the active region recording without clicking its control window.
    /// </summary>
    /// <remarks>
    /// Unassigned by default so a default chord never takes a shortcut from another app;
    /// pressing it while nothing is recording does nothing.
    /// </remarks>
    public Hotkey PauseRecording { get; set; } = Hotkey.None;
}

public sealed class QueueSettings
{
    public string FolderFilePrefix { get; set; } = MyCapture.Core.Queue.CaptureFolderNaming.DefaultPrefix;
    public bool UseCurrentFolderForNewCaptures { get; set; }
    public Guid? DefaultCaptureFolderId { get; set; }

    /// <summary>
    /// Maximum number of retained captures. Oldest eligible unkept entries are evicted.
    /// The initializer preserves legacy files; new installations use <see cref="NewInstallDefaults"/>.
    /// </summary>
    public int MaxItems { get; set; } = 300;

    /// <summary>
    /// Maximum total bytes on disk. Whichever limit is reached first triggers
    /// eviction.
    /// </summary>
    /// <remarks>
    /// The legacy fallback is 2 GiB; new installations use <see cref="NewInstallDefaults"/>.
    /// A byte cap limits disk use independently of the number of captures.
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
    /// The legacy fallback is 168 (7 days), matching the previous hard-coded policy.
    /// New installations use <see cref="NewInstallDefaults"/> and have no time limit.
    /// </summary>
    public int ImageRetentionHours { get; set; } = MyCapture.Core.Queue.CaptureRetention.DefaultImageRetentionHours;
}

public sealed class ExportSettings
{
    // Additive migration: older settings deserialize to URL excluded and original size.
    public ImageExportOptions LastImageExport { get; set => field = (value ?? new()).Normalize(); } = new();

    /// <summary>
    /// Fixed destination for quick save. Empty means the default capture folder.
    /// </summary>
    public string QuickSaveDirectoryOverride { get; set; } = string.Empty;

    /// <summary>The folder of the last successful image Save As export. Only the starting folder of Save As dialogs; quick save ignores it.</summary>
    public string LastSaveDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Legacy persisted preference retained for settings-file compatibility. Edited images are
    /// now always copied to the clipboard after a successful commit.
    /// </summary>
    public bool CopyToClipboardOnQuickSave { get; set; } = true;

    /// <summary>
    /// Filename pattern for quick save, excluding the extension.
    /// </summary>
    public string FileNamePattern { get; set; } = MyCapture.Core.Storage.QuickSaveNaming.DefaultPattern;

    /// <summary>
    /// Preserve the alpha channel when the annotated result has transparency.
    /// </summary>
    public bool PreserveTransparency { get; set; } = true;

    /// <summary>Draw the captured document/window title at the top right of image results.</summary>
    public bool ShowSourceWindowTitle { get; set; }
}

public sealed class CaptureSettings
{
    /// <summary>Include the mouse cursor in the captured bitmap.</summary>
    public bool IncludeCursor { get; set; }

    /// <summary>Highlight the window under the cursor as a snap candidate.</summary>
    public bool AutoDetectWindows { get; set; } = true;

    /// <summary>Show the pixel magnifier during selection.</summary>
    public bool ShowMagnifier { get; set; } = true;

    /// <summary>Show cursor alignment guides before dragging a screenshot or recording region.</summary>
    /// <remarks>The persisted name is retained for compatibility with existing settings.</remarks>
    public bool ShowSelectionGrid { get; set; } = true;

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

    /// <summary>
    /// How many times the capture selector has opened with its short start guide. The guide shows
    /// while this is below <see cref="CaptureStartGuidePolicy.ShowCount"/>; the selector's help
    /// button shows it again at any time. Bookkeeping only: the settings window never edits it.
    /// </summary>
    public int StartGuideShownCount { get; set; }
}

public sealed class PinSettings
{
    /// <summary>First-use recovery hints already shown, shared across pins and launches.</summary>
    public int ClickThroughHintShownCount { get; set => field = Math.Clamp(value, 0, 3); }

    /// <summary>Close the pinned window on double-click.</summary>
    public bool CloseOnDoubleClick { get; set; } = true;

    /// <summary>Save open pins on this PC and reopen them at the next launch.</summary>
    /// <remarks>Legacy settings stay off; new installations enable this through <see cref="NewInstallDefaults"/>.</remarks>
    public bool RestoreOpenPinsOnLaunch { get; set; } = false;

    /// <summary>
    /// Legacy copy-gesture delay retained for settings-file compatibility, in milliseconds.
    /// </summary>
    /// <remarks>
    /// No longer controls a UI gesture. A focused pin uses <c>Ctrl+C</c> to copy its image
    /// together with any retained original text;
    /// <c>Ctrl</c>+double-click copies retained source text or locally recognized image text.
    /// Preserve the serialized field and draft value when reading and saving older settings.
    /// </remarks>
    public int CtrlClickDebounceMs { get; set; } = 250;

    /// <summary>Opacity applied when a pinned window is first shown (0..1).</summary>
    public double InitialOpacity { get; set; } = 1.0;

    /// <summary>
    /// How many closed pinned windows can be reopened, most recent first. 0 turns it off.
    /// </summary>
    /// <remarks>
    /// Snipaste defaults this to 1. 20 is kept here because the restore history is also
    /// bounded by a retained-byte budget (<c>ClosedPinMemory.DefaultMaxRetainedBytes</c>),
    /// so the count never decides memory use on its own. The history is memory-only and is
    /// discarded when the app exits. Open pins may be saved separately when
    /// <see cref="RestoreOpenPinsOnLaunch"/> is enabled.
    /// </remarks>
    public int ClosedWindowRestoreLimit { get; set; } = 20;

    /// <summary>Scale step applied per mouse-wheel notch.</summary>
    public double ZoomStep { get; set; } = 0.1;
}

public sealed class AnnotationDefaults
{
    public const string DefaultFontFamily = "Pretendard";

    public string LastTool { get; set; } = "Rectangle";
    public MyCapture.Core.Annotations.AnnotationStrokeStyle StrokeStyle { get; set; }
    public double FillTransparency { get; set; } = 100;
    public ColorRgba StrokeColor { get; set; } = ColorRgba.FromRgb(0xEF, 0x44, 0x44);

    public double StrokeThickness { get; set; } = 3;

    public ColorRgba TextColor { get; set; } = ColorRgba.FromRgb(0xEF, 0x44, 0x44);

    public double FontSize { get; set; } = 18;

    public string FontFamily { get; set; } = DefaultFontFamily;

    public bool TextBold { get; set; }

    public bool TextBackground { get; set; }

    public bool TextOutline { get; set; } = true;

    /// <summary>Recently used text font families, most recent first.</summary>
    public List<string> RecentFontFamilies { get; set => field = value ?? []; } = [];

    /// <summary>Block size used by the mosaic tool, in image pixels.</summary>
    public int MosaicBlockSize { get; set; } = 12;

    /// <summary>Alpha applied by the highlighter (0..255).</summary>
    public byte HighlighterAlpha { get; set; } = 90;

    /// <summary>
    /// Recently used colours, most recent first. Surfaced in the toolbar swatches.
    /// </summary>
    public List<ColorRgba> RecentColors { get; set; } = [];

    /// <summary>Strength used by the blur tool: a Gaussian radius in image pixels.</summary>
    public int BlurRadius { get; set; } = MyCapture.Core.Annotations.BlurAnnotation.DefaultRadius;

    /// <summary>Where and how the reusable logo stamp is placed. Never null.</summary>
    public StampSettings Stamp { get; set => field = value ?? new(); } = new();
}

/// <summary>
/// How the editor's reusable stamp (a logo or blog name registered once) is placed and drawn.
/// </summary>
/// <remarks>
/// The picture itself lives in a file under the app's data folder; whether one is registered is
/// decided by that file, not by a flag here, so a settings file copied to another PC cannot claim
/// a picture that is not there. The placement is relative to the canvas (see
/// <see cref="MyCapture.Core.Annotations.StampLayout"/>).
/// </remarks>
public sealed class StampSettings
{
    /// <summary>The opacities offered by the stamp menu, in percent.</summary>
    public static readonly IReadOnlyList<int> OpacityChoices = [100, 70, 50, 30];

    public const int MinOpacityPercent = 10;

    public MyCapture.Core.Annotations.StampAnchor Anchor { get; set; } = MyCapture.Core.Annotations.StampAnchor.BottomRight;

    /// <summary>Distance to the left or right edge, in canvas pixels, for anchors on that side.</summary>
    public double MarginX { get; set; } = MyCapture.Core.Annotations.StampLayout.DefaultMargin;

    /// <summary>Distance to the top or bottom edge, in canvas pixels, for anchors on that side.</summary>
    public double MarginY { get; set; } = MyCapture.Core.Annotations.StampLayout.DefaultMargin;

    /// <summary>The stamp's width as a fraction of the canvas width.</summary>
    public double WidthFraction { get; set; } = MyCapture.Core.Annotations.StampLayout.DefaultWidthFraction;

    /// <summary>How opaque the placed stamp is, in percent.</summary>
    public int OpacityPercent { get; set; } = 100;

    /// <summary>
    /// Repairs values a hand-edited or imported file could hold, silently: this is bookkeeping the
    /// editor writes itself, not a setting the user is shown.
    /// </summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Anchor)) Anchor = MyCapture.Core.Annotations.StampAnchor.BottomRight;
        MarginX = Repair(MarginX, 0, MyCapture.Core.Annotations.StampLayout.MaxMargin, MyCapture.Core.Annotations.StampLayout.DefaultMargin);
        MarginY = Repair(MarginY, 0, MyCapture.Core.Annotations.StampLayout.MaxMargin, MyCapture.Core.Annotations.StampLayout.DefaultMargin);
        WidthFraction = Repair(
            WidthFraction,
            MyCapture.Core.Annotations.StampLayout.MinWidthFraction,
            MyCapture.Core.Annotations.StampLayout.MaxWidthFraction,
            MyCapture.Core.Annotations.StampLayout.DefaultWidthFraction);
        OpacityPercent = Math.Clamp(OpacityPercent, MinOpacityPercent, 100);
    }

    public StampSettings Clone() => new()
    {
        Anchor = Anchor,
        MarginX = MarginX,
        MarginY = MarginY,
        WidthFraction = WidthFraction,
        OpacityPercent = OpacityPercent,
    };

    private static double Repair(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class OcrSettings
{
    /// <summary>
    /// BCP-47 tags to try, in order. Empty means use the system's preferences.
    /// </summary>
    public List<string> PreferredLanguages { get; set; } = ["ko-KR", "en-US"];

    /// <summary>
    /// Fast / balanced / accurate recognition stage. Legacy Enhanced is normalized to Accurate.
    /// Upscale and extra preparation
    /// are derived from this value on load and save.
    /// </summary>
    public OcrQuality Quality { get; set; } = OcrQuality.Fast;

    /// <summary>
    /// Upscale factor applied before recognition.
    /// </summary>
    /// <remarks>
    /// Windows OCR accuracy drops sharply on small text, and UI screenshots are
    /// frequently 11-13px. Upscaling 2x before recognition is a large accuracy win
    /// for a small time cost. The settings UI edits <see cref="Quality"/>; this
    /// multiplier is kept in the file so older builds still read a usable value.
    /// </remarks>
    public double UpscaleFactor { get; set; } = 1.0;

    /// <summary>Cache the recognised text on the capture record.</summary>
    public bool CacheResults { get; set; } = true;
}

public enum AfterCaptureAction { Editor, QuickAccess, ClipboardOnly }

public sealed class GeneralSettings : IJsonOnDeserializing
{
    // 4.0.x wrote both values without an explicit post-capture choice. Consume the
    // legacy field without changing the new default or writing it back out.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowQuickAccess { get => null; set { } }
    public AfterCaptureAction AfterCapture { get; set; } = AfterCaptureAction.QuickAccess;

    public bool FirstCaptureTipShown { get; set; }
    public bool FirstPinTipShown { get; set; }

    /// <summary>Legacy file compatibility. Command visibility uses <see cref="AlwaysShowShortcutHints"/>.</summary>
    public bool ShowShortcutHints { get; set; } = true;

    /// <summary>Shows shortcut keys without holding Alt. Existing and new installations start off.</summary>
    public bool AlwaysShowShortcutHints { get; set; }

    /// <summary>One preserves hint choices made after hints became visible by default.</summary>
    public int ShortcutHintsRevision { get; set; } = 1;

    // A missing marker identifies a legacy file; new settings and explicit choices start at one.
    void IJsonOnDeserializing.OnDeserializing()
    {
        ShortcutHintsRevision = 0;
        AfterCapture = AfterCaptureAction.Editor;
    }

    public bool LaunchAtLogin { get; set; } = true;

    /// <summary>
    /// Show a tray notification after a successful quick save.
    /// </summary>
    public bool NotifyOnQuickSave { get; set; } = true;

    /// <summary>
    /// Play the app's short shutter sound at the moment a still capture is taken, before it is
    /// saved or copied. Off by default.
    /// </summary>
    public bool PlayCaptureSound { get; set; }

    /// <summary>UI language tag. Empty follows the OS.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Chrome palette id: glass, glass-light, workspace, midnight, daylight, or high-contrast.</summary>
    public string Theme { get; set; } = MyCapture.Core.Themes.AppThemeNames.Glass;

    /// <summary>Absent in older files: preserve the original violet accent.</summary>
    public string AccentColor { get; set; } = MyCapture.Core.Themes.AppAccentNames.Red;

    /// <summary>Independent capture-guide brand override. Empty follows the theme; otherwise #RRGGBB.</summary>
    public string CaptureGuideColor { get; set; } = string.Empty;

    /// <summary>One preserves theme choices made after the workspace migration.</summary>
    public int ThemeRevision { get; set; }

    [JsonIgnore]
    public bool IsFirstRun { get; set; }
}

/// <summary>GitHub issue used as a clipboard-image host for the GitHub URL hotkey.</summary>
public sealed class GitHubSettings
{
    /// <summary>
    /// Issue URL opened by the GitHub URL hotkey (default F9). Empty is treated as
    /// <see cref="MyCapture.Core.GitHub.GitHubIssueImageUrl.DefaultIssueUrl"/>.
    /// </summary>
    public string IssueUrl { get; set; } = MyCapture.Core.GitHub.GitHubIssueImageUrl.DefaultIssueUrl;

    /// <summary>
    /// Optional personal access token. When set, the GitHub URL hotkey uploads the image
    /// through the REST API without opening a browser or stealing focus. Stored on this PC
    /// only, DPAPI-encrypted at rest by <see cref="DpapiSecretConverter"/>; sent only to the
    /// API host derived from the configured issue URL (api.github.com, or an enterprise
    /// server's /api/v3). Never written to exported settings files.
    /// </summary>
    [JsonConverter(typeof(DpapiSecretConverter))]
    public string Token { get; set; } = string.Empty;
}
