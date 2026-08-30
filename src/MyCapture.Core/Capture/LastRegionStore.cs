using MyCapture.Core.Primitives;

namespace MyCapture.Core.Capture;

/// <summary>
/// One confirmed manual selection in canonical virtual-desktop physical pixels, plus enough
/// source-monitor identity to replay it after a display origin or DPI change.
/// </summary>
public sealed record RegionHistoryEntry(
    RectD ScreenRegion,
    string MonitorDeviceName,
    RectD MonitorBounds,
    uint MonitorDpi)
{
    /// <summary>Creates an entry without monitor metadata for compatibility with older callers.</summary>
    public static RegionHistoryEntry Legacy(RectD screenRegion) =>
        new(screenRegion.Normalized(), string.Empty, RectD.Empty, 96);

    /// <summary>
    /// Maps this selection to the current incarnation of the same monitor. Physical offsets
    /// and size are scaled by the DPI ratio, then clamped to the current physical bounds.
    /// </summary>
    public RectD? ResolveForMonitor(RectD currentMonitorBounds, uint currentMonitorDpi)
    {
        RectD current = currentMonitorBounds.Normalized().ToPixelBounds();
        if (current.IsEmpty)
        {
            return null;
        }

        RectD source = ScreenRegion.Normalized().ToPixelBounds();
        RectD previousMonitor = MonitorBounds.Normalized().ToPixelBounds();
        if (source.IsEmpty)
        {
            return null;
        }

        if (previousMonitor.IsEmpty || string.IsNullOrWhiteSpace(MonitorDeviceName))
        {
            RectD legacy = source.ClampTo(current);
            return legacy.IsEmpty ? null : legacy;
        }

        double oldDpi = MonitorDpi == 0 ? 96.0 : MonitorDpi;
        double newDpi = currentMonitorDpi == 0 ? 96.0 : currentMonitorDpi;
        double scale = newDpi / oldDpi;

        var mapped = new RectD(
            current.Left + ((source.Left - previousMonitor.Left) * scale),
            current.Top + ((source.Top - previousMonitor.Top) * scale),
            source.Width * scale,
            source.Height * scale)
            .ToPixelBounds()
            .ClampTo(current);

        return mapped.IsEmpty ? null : mapped;
    }
}

/// <summary>
/// Remembers recently confirmed manual selection regions for repeat-last-region.
/// </summary>
/// <remarks>
/// Entries are newest-first and bounded by the live RegionHistoryLimit setting. Only manual
/// selections should call <see cref="Record(RegionHistoryEntry)"/>; full-monitor, window and
/// stitched captures are not meaningful repeat regions.
/// </remarks>
public sealed class LastRegionStore
{
    private readonly object _gate = new();
    private readonly LinkedList<RegionHistoryEntry> _regions = new();
    private readonly Func<int> _limit;

    public LastRegionStore(Func<int> limit)
    {
        _limit = limit ?? throw new ArgumentNullException(nameof(limit));
    }

    /// <summary>The most recent screen rectangle, retained for source compatibility.</summary>
    public RectD? Last
    {
        get
        {
            lock (_gate)
            {
                return _regions.First?.Value.ScreenRegion;
            }
        }
    }

    /// <summary>The most recent rectangle with monitor identity and DPI metadata.</summary>
    public RegionHistoryEntry? LastEntry
    {
        get
        {
            lock (_gate)
            {
                return _regions.First?.Value;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _regions.Count;
            }
        }
    }

    /// <summary>Records a legacy region without monitor metadata.</summary>
    public void Record(RectD screenRegion) => Record(RegionHistoryEntry.Legacy(screenRegion));

    /// <summary>Records a confirmed manual selection, normalising it and dropping empties.</summary>
    public void Record(RegionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        RectD normalized = entry.ScreenRegion.Normalized().ToPixelBounds();
        if (normalized.IsEmpty)
        {
            return;
        }

        RegionHistoryEntry canonical = entry with
        {
            ScreenRegion = normalized,
            MonitorDeviceName = entry.MonitorDeviceName?.Trim() ?? string.Empty,
            MonitorBounds = entry.MonitorBounds.Normalized().ToPixelBounds(),
            MonitorDpi = entry.MonitorDpi == 0 ? 96u : entry.MonitorDpi,
        };

        lock (_gate)
        {
            if (_regions.First is { } head && RegionsEqual(head.Value.ScreenRegion, canonical.ScreenRegion))
            {
                if (SameMonitorMetadata(head.Value, canonical))
                {
                    return;
                }

                // The same physical rectangle was selected after a topology/DPI change. Keep
                // the newer monitor metadata rather than preserving a stale replay transform.
                _regions.RemoveFirst();
            }

            _regions.AddFirst(canonical);
            Trim();
        }
    }

    /// <summary>A rectangle-only snapshot, newest first, retained for existing consumers.</summary>
    public IReadOnlyList<RectD> Snapshot()
    {
        lock (_gate)
        {
            return _regions.Select(entry => entry.ScreenRegion).ToArray();
        }
    }

    /// <summary>A metadata-preserving snapshot, newest first.</summary>
    public IReadOnlyList<RegionHistoryEntry> SnapshotEntries()
    {
        lock (_gate)
        {
            return [.. _regions];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _regions.Clear();
        }
    }

    private void Trim()
    {
        int max = Math.Max(1, _limit());
        while (_regions.Count > max)
        {
            _regions.RemoveLast();
        }
    }

    private static bool SameMonitorMetadata(RegionHistoryEntry a, RegionHistoryEntry b) =>
        string.Equals(a.MonitorDeviceName, b.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)
        && a.MonitorDpi == b.MonitorDpi
        && RegionsEqual(a.MonitorBounds, b.MonitorBounds);

    private static bool RegionsEqual(RectD a, RectD b)
    {
        RectD pa = a.ToPixelBounds();
        RectD pb = b.ToPixelBounds();
        return Math.Abs(pa.Left - pb.Left) < 0.5
            && Math.Abs(pa.Top - pb.Top) < 0.5
            && Math.Abs(pa.Width - pb.Width) < 0.5
            && Math.Abs(pa.Height - pb.Height) < 0.5;
    }
}
