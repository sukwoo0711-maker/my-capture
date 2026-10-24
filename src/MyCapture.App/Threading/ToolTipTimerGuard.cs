using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Threading;

/// <summary>
/// Keeps WPF's tooltip show-duration timer from stopping every other DispatcherTimer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolTipService.ShowDurationProperty"/> defaults to <see cref="int.MaxValue"/>
/// milliseconds, so a tooltip stays open until it is dismissed. After opening the popup,
/// PopupControlService starts a DispatcherTimer with that interval. The dispatcher arms its single
/// Win32 timer for the earliest due time and compares due times by <c>int</c> subtraction, which
/// wraps once two due times lie more than <see cref="int.MaxValue"/> milliseconds apart. A timer
/// that fell due while the popup was opening then compares as later than the tooltip timer, the
/// Win32 timer is armed 24.8 days ahead, and no DispatcherTimer in the process fires until the
/// tooltip closes. Playback position updates, the editor's open fallback and every timed wait stop
/// meanwhile; with no pointer input to close the tooltip, they never resume.
/// </para>
/// <para>
/// Half of that range keeps every difference representable unless a timer is twelve days
/// overdue, and still keeps tooltips open until they are dismissed. Larger explicit values are
/// lowered to it as well.
/// </para>
/// </remarks>
internal static class ToolTipTimerGuard
{
    /// <summary>The longest tooltip show duration whose timer cannot wrap the dispatcher's comparison.</summary>
    internal const int MaximumShowDurationMs = int.MaxValue / 2;

    private static int _installed;

    /// <summary>Applies the bound to every element of this process; later calls do nothing.</summary>
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        foreach (Type owner in new[] { typeof(FrameworkElement), typeof(FrameworkContentElement) })
        {
            ToolTipService.ShowDurationProperty.OverrideMetadata(
                owner,
                new FrameworkPropertyMetadata(MaximumShowDurationMs, null, CoerceShowDuration));
        }
    }

    private static object CoerceShowDuration(DependencyObject element, object value) =>
        value is int duration && duration > MaximumShowDurationMs ? MaximumShowDurationMs : value;
}
