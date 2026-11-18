using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MyCapture.Core.Pin;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>
/// Reads how large the copied range looked in the spreadsheet window the user copied it from:
/// the selection's size in points and the window's zoom. Read-only and optional: it never
/// starts, activates or changes the spreadsheet, and gives up within <see cref="Budget"/>.
/// </summary>
public static class ExcelSelectionReader
{
    /// <summary>Longest a read may take; past it the pin opens at its usual size.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

    private const string MainWindowClass = "XLMAIN";
    private const string SheetWindowClass = "EXCEL7";
    private const uint ObjectIdNativeObjectModel = 0xFFFFFFF0;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly BoundedNativeProbe Probe = new(Gate, Budget, ApartmentState.STA, "MyCapture spreadsheet view");

    /// <summary>
    /// The spreadsheet's main window when it is the foreground window and its process owns the
    /// clipboard (the copy came from it); otherwise <see cref="IntPtr.Zero"/>. Cheap enough for the
    /// UI thread: a few window-manager calls and no cross-process COM.
    /// </summary>
    public static IntPtr FindCopySource()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !HasClass(foreground, MainWindowClass)) return IntPtr.Zero;
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return IntPtr.Zero;
        _ = NativeMethods.GetWindowThreadProcessId(foreground, out uint windowProcess);
        _ = NativeMethods.GetWindowThreadProcessId(owner, out uint ownerProcess);
        return windowProcess != 0 && windowProcess == ownerProcess ? foreground : IntPtr.Zero;
    }

    /// <summary>
    /// Reads the selection of <paramref name="mainWindow"/>'s active sheet window on a dedicated
    /// STA thread. Returns null when the read fails, another read is still running, or
    /// <see cref="Budget"/> passes first.
    /// </summary>
    public static Task<SpreadsheetSelectionView?> ReadAsync(IntPtr mainWindow) =>
        mainWindow == IntPtr.Zero
            ? Task.FromResult<SpreadsheetSelectionView?>(null)
            : Probe.RunAsync(cancellation => Read(mainWindow, cancellation), (SpreadsheetSelectionView?)null);

    internal static SpreadsheetSelectionView? Read(IntPtr mainWindow, CancellationToken cancellation)
    {
        // The paste reads the clipboard at the same time, and the spreadsheet rejects calls while
        // it renders the copied picture for that read. Retry until the read's deadline cancels it.
        while (true)
        {
            try
            {
                return ReadOnce(mainWindow, cancellation);
            }
            catch (Exception ex) when (IsBusy(ex) && !cancellation.IsCancellationRequested)
            {
                Thread.Sleep(BusyRetryDelay);
            }
        }
    }

    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(10);

    private static bool IsBusy(Exception ex)
    {
        Exception inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
        // RPC_E_CALL_REJECTED and RPC_E_SERVERCALL_RETRYLATER.
        return inner is COMException com && (uint)com.HResult is 0x80010001 or 0x8001010A;
    }

    private static SpreadsheetSelectionView? ReadOnce(IntPtr mainWindow, CancellationToken cancellation)
    {
        var objects = new List<object>();
        try
        {
            IntPtr sheetWindow = IntPtr.Zero;
            NativeMethods.EnumWindowsProc visitor = (child, _) =>
            {
                if (cancellation.IsCancellationRequested) return false;
                if (!HasClass(child, SheetWindowClass) || !NativeMethods.IsWindowVisible(child)) return true;
                sheetWindow = child;
                return false;
            };
            _ = EnumChildWindows(mainWindow, visitor, IntPtr.Zero);
            GC.KeepAlive(visitor);
            if (sheetWindow == IntPtr.Zero) return null;
            cancellation.ThrowIfCancellationRequested();
            Guid dispatchId = new("00020400-0000-0000-C000-000000000046");
            if (AccessibleObjectFromWindow(sheetWindow, ObjectIdNativeObjectModel, ref dispatchId, out object window) < 0)
                return null;
            objects.Add(window);
            // The sheet window's native object is the spreadsheet's Window: Zoom is its percentage
            // and Selection its selected range (or shape), sized in points at 100%.
            double zoom = Number(Get(window, "Zoom"));
            object selection = Get(window, "Selection");
            var view = new SpreadsheetSelectionView(Number(Get(selection, "Width")), Number(Get(selection, "Height")), zoom);
            return view.IsPlausible ? view : null;

            object Get(object target, string name)
            {
                cancellation.ThrowIfCancellationRequested();
                object value = target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)
                    ?? throw new InvalidOperationException();
                if (Marshal.IsComObject(value)) objects.Add(value);
                return value;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException && !IsBusy(ex))
        {
            return null;
        }
        finally
        {
            for (int index = objects.Count - 1; index >= 0; index--)
                if (Marshal.IsComObject(objects[index])) Marshal.ReleaseComObject(objects[index]);
        }
    }

    private static double Number(object value) => value switch
    {
        double number => number,
        float number => number,
        int number => number,
        short number => number,
        decimal number => (double)number,
        // "Fit selection" and other non-numeric states are not a zoom.
        _ => double.NaN,
    };

    private static bool HasClass(IntPtr window, string className)
    {
        var name = new StringBuilder(32);
        return NativeMethods.GetClassName(window, name, name.Capacity) > 0
            && string.Equals(name.ToString(), className, StringComparison.Ordinal);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, NativeMethods.EnumWindowsProc callback, IntPtr parameter);
}
