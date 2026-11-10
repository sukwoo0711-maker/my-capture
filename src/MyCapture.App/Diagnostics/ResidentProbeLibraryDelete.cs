using System.Globalization;
using System.Runtime.InteropServices;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// The release gate's large library delete (<c>--resident-scenarios library-delete</c>), the shape of
/// the 2026-11-06 report: 124 captures selected in the library window, moved to the Recycle Bin, then
/// brought back with Undo. Three times are measured from the user's command, each on a rendered frame:
/// until no deleted card is on screen, until the result ("124개를 휴지통으로 옮김") is on the status
/// line, and from Undo until its result ("라이브러리에 124개 복원됨") is.
/// </summary>
/// <remarks>
/// The table compares these times with the previous release once that release has the scenario.
/// Until then, and against a slow drift over several releases, the budgets below fail the probe on
/// their own. They lie between the two ways measured on the developer PC with the real Recycle Bin and
/// 124 directories: one Shell operation per capture took 42.6 s to move and 396 s to restore, one
/// operation for all of them 14.9 s and 8.1 s, and the window that waited for the move kept blank
/// cards on screen until it ended. A quick run (<see cref="ResidentProbeOptions.Quick"/>) deletes
/// <see cref="QuickCount"/> captures against the same budgets for that count: with 12 the build that
/// restored one capture at a time took 28.2 s to undo, the batched build 3.0 to 4.1 s.
/// </remarks>
internal static class ResidentProbeLibraryDelete
{
    internal const string Scenario = "library-delete";

    /// <summary>The selection of the report.</summary>
    internal const int ReleaseCount = 124;

    /// <summary>A development run: a few captures through the Recycle Bin, not the release table.</summary>
    internal const int QuickCount = 12;

    internal static int Count(bool quick) => quick ? QuickCount : ReleaseCount;

    /// <summary>
    /// The deleted cards leave the view before their files have moved. The probe's delete is the
    /// first of its process: with 12 captures the cards were gone after 0.6 to 1.2 s, most of it a
    /// window that drew no frame; moving 124 in one operation took 14.9 s.
    /// </summary>
    internal static TimeSpan CardsGoneBudget { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One Shell operation for the whole selection and the library's own work, plus a per-item part
    /// between the two ways measured: 120 ms a capture in one operation, 344 ms one at a time.
    /// </summary>
    internal static TimeSpan ResultBudget(int count) => TimeSpan.FromMilliseconds(3_000 + 200 * count);

    /// <summary>
    /// One Recycle Bin restore for the whole selection and the library's own work, plus a per-item
    /// part: 65 ms a capture in one restore, 3.2 s one at a time.
    /// </summary>
    internal static TimeSpan UndoBudget(int count) => TimeSpan.FromMilliseconds(5_000 + 150 * count);

    internal static string CardsGoneTiming(int count) => $"{Scenario}: {count} selected → cards gone";

    internal static string ResultTiming(int count) => $"{Scenario}: {count} selected → result on the status line";

    internal static string UndoTiming(int count) => $"{Scenario}: undo {count} → result on the status line";

    /// <summary>The measured times of one run.</summary>
    internal sealed record Measurement(int Count, double CardsGoneMs, double ResultMs, double UndoMs);

    /// <summary>One line per time over its budget; empty when the run is within all three.</summary>
    internal static IReadOnlyList<string> OverBudget(Measurement run)
    {
        var over = new List<string>(3);
        void Check(string name, double measured, TimeSpan budget)
        {
            if (measured <= budget.TotalMilliseconds) return;
            over.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}: {measured:0} ms, budget {budget.TotalMilliseconds:0} ms"));
        }
        Check(CardsGoneTiming(run.Count), run.CardsGoneMs, CardsGoneBudget);
        Check(ResultTiming(run.Count), run.ResultMs, ResultBudget(run.Count));
        Check(UndoTiming(run.Count), run.UndoMs, UndoBudget(run.Count));
        return over;
    }

    internal static string RecycleBinState(string when) => $"Recycle Bin {when} {Scenario}";

    /// <summary>
    /// What the Recycle Bin of every drive holds, from SHQueryRecycleBin, which only reads it. The
    /// Shell moves and restores more slowly when the Recycle Bin is large, so the report keeps it with
    /// the run's environment, before and after the step. A failed query is written as its HRESULT.
    /// </summary>
    internal static object RecycleBinContents()
    {
        int result;
        long items, bytes;
        if (Environment.Is64BitProcess)
        {
            var info = new QueryInfo64 { Size = Marshal.SizeOf<QueryInfo64>() };
            result = SHQueryRecycleBin64(null, ref info);
            (items, bytes) = (info.Items, info.Bytes);
        }
        else
        {
            var info = new QueryInfo32 { Size = Marshal.SizeOf<QueryInfo32>() };
            result = SHQueryRecycleBin32(null, ref info);
            (items, bytes) = (info.Items, info.Bytes);
        }
        return result == 0
            ? new { Items = items, Bytes = bytes }
            : new { Error = string.Create(CultureInfo.InvariantCulture, $"SHQueryRecycleBin 0x{result:X8}") };
    }

    // SHQUERYRBINFO. shellapi.h packs its structures to one byte in 32-bit processes only.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct QueryInfo32
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo64
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin32(string? rootPath, ref QueryInfo32 info);

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin64(string? rootPath, ref QueryInfo64 info);
}
