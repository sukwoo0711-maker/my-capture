using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Pumps the test's own dispatcher until a real worker or dispatcher operation finishes.</summary>
internal static class PinNativeSpeedTestWait
{
    internal static void Complete(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    internal static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(2), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        Assert.True(condition(), "The actual worker or dispatcher operation did not finish within two seconds.");
    }
}

internal static class PinNativeTestMemory
{
    internal static byte[] Copy(IntPtr pointer, int length)
    {
        byte[] bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }
}
