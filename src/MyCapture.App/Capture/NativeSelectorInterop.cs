using System.Runtime.InteropServices;

namespace MyCapture.App.Capture;

internal static class NativeSelectorInterop
{
    internal delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal WindowProc Procedure;
        internal int ClassExtra, WindowExtra;
        internal IntPtr Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
        internal IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal Rect(int x, int y, int width, int height) { Left = x; Top = y; Right = x + width; Bottom = y + height; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Paint
    {
        internal IntPtr Dc;
        internal int Erase;
        internal Rect Rectangle;
        internal int Restore, Incremental;
        internal fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapObject
    {
        internal int Type, Width, Height, WidthBytes;
        internal ushort Planes, BitsPixel;
        internal IntPtr Bits;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nuint SetTimer(IntPtr hwnd, nuint id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, ref Rect rectangle, bool erase);
    [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr hwnd, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr hwnd, ref Paint paint);
    [DllImport("user32.dll")] internal static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr GetCapture();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern uint GetObjectType(IntPtr value);
    [DllImport("gdi32.dll")] internal static extern IntPtr GetCurrentObject(IntPtr dc, uint type);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] internal static extern int GetObject(IntPtr value, int size, out BitmapObject bitmap);
    [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] internal static extern IntPtr GetStockObject(int item);
    [DllImport("gdi32.dll")] internal static extern bool Rectangle(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("msimg32.dll", SetLastError = true)] internal static extern bool AlphaBlend(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint blend);
    [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
