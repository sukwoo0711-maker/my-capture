using System.Globalization;

namespace MyCapture.Core.Settings;

/// <summary>
/// The single source of truth for the acceptable range of every numeric setting.
/// </summary>
/// <remarks>
/// These bounds are intentionally identical to the ones <see cref="SettingsStore"/>
/// clamps to on load. The settings window validates against the same limits so a value
/// the user types is rejected up front with a clear message, rather than silently
/// clamped on the next launch — the two must never drift apart, so they live in one
/// place and both consumers reference it.
/// </remarks>
public static class SettingsRanges
{
    public static readonly Range<int> MaxItems = new(10, 5000);
    public static readonly Range<long> MaxBytes = new(128L * 1024 * 1024, 512L * 1024 * 1024 * 1024);
    public static readonly Range<int> ThumbnailLongEdge = new(96, 1024);

    public static readonly Range<int> DelaySeconds = new(0, 60);
    public static readonly Range<int> RegionHistoryLimit = new(1, 200);

    /// <summary>Countdown used before a region recording begins.</summary>
    public static readonly Range<int> RecordingStartDelaySeconds = new(1, 10);

    /// <summary>
    /// Frame rates offered by the recording settings UI and accepted from
    /// <c>settings.json</c>. An explicit list prevents unsupported arbitrary values.
    /// </summary>
    public static IReadOnlyList<int> RecordingFrameRates { get; } =
        Array.AsReadOnly([10, 15, 24, 30, 60]);

    public static readonly Range<int> CtrlClickDebounceMs = new(120, 800);
    public static readonly Range<double> InitialOpacity = new(0.2, 1.0);
    public static readonly Range<int> ClosedWindowRestoreLimit = new(0, 100);
    public static readonly Range<double> ZoomStep = new(0.02, 0.5);

    public static readonly Range<double> StrokeThickness = new(1, 64);
    public static readonly Range<double> FontSize = new(6, 400);
    public static readonly Range<int> MosaicBlockSize = new(2, 128);
    public static readonly Range<int> HighlighterAlpha = new(0, 255);

    public static readonly Range<double> UpscaleFactor = new(1.0, 4.0);

    /// <summary>1 GiB expressed in bytes, the unit the storage cap is shown in.</summary>
    public const long BytesPerGiB = 1024L * 1024 * 1024;
}

/// <summary>An inclusive numeric range with a formatted, culture-invariant description.</summary>
public readonly record struct Range<T>(T Min, T Max)
    where T : IComparable<T>
{
    public bool Contains(T value) =>
        value.CompareTo(Min) >= 0 && value.CompareTo(Max) <= 0;

    public string Describe() => string.Format(
        CultureInfo.InvariantCulture, "{0} ~ {1}", Min, Max);
}
