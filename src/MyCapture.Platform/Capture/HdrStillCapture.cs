using System.Diagnostics;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>What the HDR correction did during one still capture.</summary>
/// <param name="Mode">The mode in effect.</param>
/// <param name="ToneOperator">Treatment of pixels brighter than SDR white.</param>
/// <param name="Displays">Colour state of every display when the capture was taken.</param>
/// <param name="Outputs">One entry per display the capture touched and tried to read in scRGB.</param>
/// <param name="DetectMs">Reading the display configuration and the monitor layout.</param>
/// <param name="ReadMs">The scRGB read and conversion, as the capture waited for it.</param>
/// <param name="PatchMs">Copying the converted pixels over the GDI copy.</param>
public sealed record HdrCaptureReport(
    HdrCaptureMode Mode,
    HdrToneOperator ToneOperator,
    IReadOnlyList<HdrDisplayState> Displays,
    IReadOnlyList<HdrOutputRead> Outputs,
    double DetectMs,
    double ReadMs,
    double PatchMs)
{
    /// <summary>Time the capture spent on the correction.</summary>
    public double TotalMs => DetectMs + ReadMs + PatchMs;

    public bool AnyApplied
    {
        get
        {
            foreach (HdrOutputRead output in Outputs)
            {
                if (output.Applied)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Replaces the pixels a GDI copy holds for HDR displays with an scRGB read converted to SDR.
/// </summary>
/// <remarks>
/// <para>
/// The 8-bit picture GDI hands back for an HDR desktop is reported as brighter than the SDR
/// content it shows, with everything above 80 nits clipped (docs/adr/0009; no HDR display was at
/// hand to observe it). A clipped copy cannot be repaired, so the correction does not depend on
/// what GDI returned: the display is read again in floating point (<see cref="HdrDesktopReader"/>)
/// and converted (<see cref="HdrToneMap"/>). The GDI copy stays underneath: it is the result for
/// every display without HDR, bit for bit as before, and for an HDR display whose read fails or
/// is late.
/// </para>
/// <para>
/// A system without an HDR display pays one display-configuration query (0.012 ms measured) and
/// returns; no thread, no DXGI, no D3D.
/// </para>
/// </remarks>
internal static unsafe class HdrStillCapture
{
    /// <summary>
    /// Longest a capture waits for the scRGB read of all its displays when the D3D device is
    /// already there. Such a read measured 8-20 ms at 3440x1440; past this bound the capture keeps
    /// the GDI copy.
    /// </summary>
    internal static readonly TimeSpan ReadBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The bound for a read that has to create the D3D device first: at startup (the prewarm, off
    /// the hotkey path) and after the device's idle release. Creating it measured 72-137 ms and
    /// 202 ms on a loaded PC, once more than 300 ms. Giving up there would hand back the washed-out
    /// GDI copy exactly when the display is HDR, so this one read may take longer.
    /// </summary>
    internal static readonly TimeSpan FirstReadBudget = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Corrects the top-down BGRX rows at <paramref name="bits"/>, which hold the GDI copy of the
    /// virtual-desktop rectangle at (<paramref name="originX"/>, <paramref name="originY"/>).
    /// Returns <see langword="null"/> when no display the capture touches needs the correction.
    /// </summary>
    internal static HdrCaptureReport? Apply(
        IntPtr bits, int width, int height, int originX, int originY, HdrCaptureMode mode, HdrToneOperator toneOperator)
    {
        long mark = Stopwatch.GetTimestamp();
        IReadOnlyList<HdrDisplayState> displays = HdrDisplayDetector.Detect();
        bool force = mode == HdrCaptureMode.ForceFloatRead;
        if (!force && !AnyHdr(displays))
        {
            return null;
        }

        List<HdrReadRequest>? requests = null;
        foreach (MonitorInfo monitor in MonitorEnumerator.GetAll())
        {
            HdrDisplayState? state = Find(displays, monitor.DeviceName);
            if (!force && state?.IsHdrActive != true)
            {
                continue;
            }

            RectD bounds = monitor.Bounds;
            double left = Math.Max(bounds.Left, originX), top = Math.Max(bounds.Top, originY);
            double right = Math.Min(bounds.Right, originX + (double)width), bottom = Math.Min(bounds.Bottom, originY + (double)height);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            (requests ??= []).Add(new HdrReadRequest(
                monitor.DeviceName, bounds, new RectD(left, top, right - left, bottom - top),
                state?.SdrWhiteNits ?? HdrToneMap.NitsPerScRgbUnit, ConfirmHdr: !force));
        }

        if (requests is null)
        {
            return null;
        }

        double detectMs = Lap(ref mark);
        TimeSpan budget = HdrDesktopReader.HasKeptDevice ? ReadBudget : FirstReadBudget;
        using HdrReadResult result = HdrDesktopReader.Read(requests, force, toneOperator, budget);
        double readMs = Lap(ref mark);
        if (result.Tiles.Count > 0)
        {
            // GDI batches per thread: the copy must have landed before its memory is written directly.
            _ = NativeMethods.GdiFlush();
            foreach (HdrTile tile in result.Tiles)
            {
                Patch(bits, width, height, originX, originY, tile);
            }
        }

        return new HdrCaptureReport(mode, toneOperator, displays, result.Outputs.ToArray(), detectMs, readMs, Lap(ref mark));
    }

    private static void Patch(IntPtr bits, int width, int height, int originX, int originY, HdrTile tile)
    {
        int x = tile.Left - originX, y = tile.Top - originY;
        if (x < 0 || y < 0 || tile.Width > width - x || tile.Height > height - y || tile.Pixels == IntPtr.Zero)
        {
            return;
        }

        byte* source = (byte*)tile.Pixels;
        byte* target = (byte*)bits + ((((long)y * width) + x) * 4);
        long targetStride = (long)width * 4;
        for (int row = 0; row < tile.Height; row++)
        {
            Buffer.MemoryCopy(source + ((long)row * tile.Stride), target + (row * targetStride), tile.Stride, tile.Stride);
        }
    }

    private static bool AnyHdr(IReadOnlyList<HdrDisplayState> displays)
    {
        for (int index = 0; index < displays.Count; index++)
        {
            if (displays[index].IsHdrActive)
            {
                return true;
            }
        }

        return false;
    }

    private static HdrDisplayState? Find(IReadOnlyList<HdrDisplayState> displays, string deviceName)
    {
        for (int index = 0; index < displays.Count; index++)
        {
            if (string.Equals(displays[index].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return displays[index];
            }
        }

        return null;
    }

    private static double Lap(ref long mark)
    {
        long now = Stopwatch.GetTimestamp();
        double milliseconds = Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds;
        mark = now;
        return milliseconds;
    }
}
