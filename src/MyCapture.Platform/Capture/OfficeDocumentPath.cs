using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MyCapture.Core.Capture;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>Reads the object model of an existing Office window; never creates or activates Office.</summary>
internal static class OfficeDocumentPath
{
    internal static string Read(IntPtr window, string appId, CancellationToken cancellation)
    {
        var objects = new List<object>();
        try
        {
            string className = appId switch { "excel" => "EXCEL7", "winword" => "_WwG", _ => "paneClassDC" };
            IntPtr content = IntPtr.Zero;
            NativeMethods.EnumWindowsProc visitor = (child, _) =>
            {
                if (cancellation.IsCancellationRequested) return false;
                var name = new StringBuilder(256);
                _ = NativeMethods.GetClassName(child, name, name.Capacity);
                if (name.ToString() != className || !NativeMethods.IsWindowVisible(child)) return true;
                content = child;
                return false;
            };
            _ = EnumChildWindows(window, visitor, IntPtr.Zero);
            if (content == IntPtr.Zero) return string.Empty;
            cancellation.ThrowIfCancellationRequested();
            Guid dispatchId = new("00020400-0000-0000-C000-000000000046");
            if (AccessibleObjectFromWindow(content, 0xFFFFFFF0, ref dispatchId, out object native) < 0)
                return string.Empty;
            objects.Add(native);
            object document = appId switch
            {
                "excel" => Get(Get(native, "ActiveSheet"), "Parent"),
                "winword" => Get(native, "Document"),
                _ => Get(native, "Presentation"),
            };
            // FullName of an unsaved Office document can be just "Book1". Path must be present.
            string directory = Get(document, "Path") as string ?? string.Empty;
            return directory.Length == 0 ? string.Empty
                : CaptureSourceText.NormalizeLocalPath(Get(document, "FullName") as string);

            object Get(object target, string name)
            {
                cancellation.ThrowIfCancellationRequested();
                object value = target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)
                    ?? throw new InvalidOperationException();
                if (Marshal.IsComObject(value)) objects.Add(value);
                return value;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        { return string.Empty; }
        finally
        {
            for (int index = objects.Count - 1; index >= 0; index--)
                if (Marshal.IsComObject(objects[index])) Marshal.ReleaseComObject(objects[index]);
        }
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, NativeMethods.EnumWindowsProc callback, IntPtr parameter);
}
