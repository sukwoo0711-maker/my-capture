using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Opt-in captures of test-owned native notices, without starting the application.</summary>
public sealed class LaunchVisibilityEvidenceTests(ITestOutputHelper output)
{
    internal const string OutputVariable = "MYCAPTURE_LAUNCH_EVIDENCE";
    private const uint PwRenderFullContent = 0x00000002;

    [LaunchEvidenceTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualNoticeHasReadableTextAndSystemColors(bool secondary)
    {
        string folder = Path.GetFullPath(Environment.GetEnvironmentVariable(OutputVariable)!);
        Directory.CreateDirectory(folder);
        string name = secondary ? "secondary-notice" : "starting-notice";
        using var visible = new ManualResetEventSlim();
        var surface = new NativeLaunchSurface(secondary);
        using var status = secondary
            ? LaunchStatusPresentation.Secondary(visible.Set, () => surface)
            : LaunchStatusPresentation.Start(false, visible.Set, () => surface);
        try
        {
            Assert.True(visible.Wait(TimeSpan.FromSeconds(5)), "The native notice did not become visible.");
            IntPtr window = surface.Handle;
            Assert.NotEqual(IntPtr.Zero, window);
            Assert.True(IsWindowVisible(window));
            Assert.NotEqual(IntPtr.Zero,
                SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 2000, out _));

            IntPtr label = FindWindowEx(window, IntPtr.Zero, "STATIC", null);
            Assert.NotEqual(IntPtr.Zero, label);
            var text = new StringBuilder(256);
            Assert.True(GetWindowText(label, text, text.Capacity) > 0);
            Assert.Equal(secondary
                ? "MyCapture가 시작하는 중입니다.\r\n잠시 뒤 라이브러리가 열립니다."
                : "MyCapture 시작 중", text.ToString());
            IntPtr fontHandle = SendMessage(label, 0x0031, IntPtr.Zero, IntPtr.Zero); // WM_GETFONT
            Assert.NotEqual(IntPtr.Zero, fontHandle);
            Assert.True(GetObject(fontHandle, Marshal.SizeOf<LogFont>(), out LogFont font) > 0);
            Assert.Contains(font.FaceName, new[] { "Segoe UI", "Segoe UI Variable Text", "Malgun Gothic" });

            Assert.True(GetWindowRect(window, out Rect bounds));
            Assert.True(GetWindowRect(label, out Rect labelBounds));
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            Assert.True(width > 0 && height > 0);
            BitmapSource captured;
            uint textColor, backgroundColor;
            uint dpi = GetDpiForWindow(window);
            Assert.True(dpi > 0);
            var highContrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
            Assert.True(SystemParametersInfo(0x0042, highContrast.Size, ref highContrast, 0)); // SPI_GETHIGHCONTRAST
            IntPtr source = GetDC(window);
            Assert.NotEqual(IntPtr.Zero, source);
            try
            {
                // Ask the real system dialog for the STATIC colors; no global theme changes.
                IntPtr brush = SendMessage(window, 0x0138, source, label); // WM_CTLCOLORSTATIC
                textColor = GetTextColor(source);
                backgroundColor = GetBkColor(source);
                Assert.Equal(GetSysColorBrush(15), brush); // COLOR_3DFACE
                Assert.Equal(GetSysColor(8), textColor); // COLOR_WINDOWTEXT
                Assert.Equal(GetSysColor(15), backgroundColor);
                Assert.NotEqual(textColor, backgroundColor);

                captured = Capture(window, source, width, height);
            }
            finally { _ = ReleaseDC(window, source); }
            // Snapshot all HWND-dependent data before disk I/O: the secondary notice lasts 2s.
            status.Complete();
            await status.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            string png = Path.Combine(folder, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(captured));
            using (var file = new FileStream(png, FileMode.Create, FileAccess.Write)) encoder.Save(file);
            output.WriteLine("PrintWindow(PW_RENDERFULLCONTENT): {0}", png);

            File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new
            {
                text = text.ToString(), font = font.FaceName, fontHeight = font.Height,
                textColor, backgroundColor, highContrast = (highContrast.Flags & 1) != 0,
                dpi, width, height, printWindowFlags = PwRenderFullContent,
                capturedUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));

            // Inspect only the text control: a painted frame around an empty box must fail.
            var area = new Int32Rect(labelBounds.Left - bounds.Left, labelBounds.Top - bounds.Top,
                labelBounds.Right - labelBounds.Left, labelBounds.Bottom - labelBounds.Top);
            Assert.True(area.X >= 0 && area.Y >= 0 && area.Width > 0 && area.Height > 0);
            Assert.True(area.X + area.Width <= width && area.Y + area.Height <= height);
            var pixels = new byte[area.Width * area.Height * 4];
            var normalized = new FormatConvertedBitmap(captured, PixelFormats.Bgr32, null, 0);
            normalized.CopyPixels(area, pixels, area.Width * 4, 0);
            int foregroundPixels = 0, backgroundPixels = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if (NearColor(pixels, index, textColor)) foregroundPixels++;
                if (NearColor(pixels, index, backgroundColor)) backgroundPixels++;
            }
            output.WriteLine("Label pixels: foreground={0}, background={1}", foregroundPixels, backgroundPixels);
            Assert.True(foregroundPixels >= 20, "Captured label has no visible text; inspect the saved PNG.");
            Assert.True(backgroundPixels >= 20, "Captured label has no system background; inspect the saved PNG.");
        }
        finally
        {
            status.Complete();
            await status.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static bool NearColor(byte[] pixels, int index, uint color) =>
        Math.Abs(pixels[index] - (int)((color >> 16) & 255)) <= 24 &&
        Math.Abs(pixels[index + 1] - (int)((color >> 8) & 255)) <= 24 &&
        Math.Abs(pixels[index + 2] - (int)(color & 255)) <= 24;

    private static BitmapSource Capture(IntPtr window, IntPtr source, int width, int height)
    {
        // WM_NULL proves responsiveness, not completion of child painting/composition.
        // Prepare this test-owned HWND for a full-content capture without a timing sleep.
        Assert.True(RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400));
        Assert.Equal(0, DwmIsCompositionEnabled(out bool compositionEnabled));
        if (compositionEnabled) Assert.Equal(0, DwmFlush());
        IntPtr memory = CreateCompatibleDC(source);
        Assert.NotEqual(IntPtr.Zero, memory);
        try
        {
            IntPtr bitmap = CreateCompatibleBitmap(source, width, height);
            Assert.NotEqual(IntPtr.Zero, bitmap);
            try
            {
                IntPtr old = SelectObject(memory, bitmap);
                Assert.True(old != IntPtr.Zero && old != new IntPtr(-1));
                try
                {
                    Assert.True(PrintWindow(window, memory, PwRenderFullContent));
                    BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                        Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    // Materialize the pixels while the HWND, DC and HBITMAP are still alive.
                    // Freezing alone does not express ownership of an interop bitmap snapshot.
                    int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
                    var pixels = new byte[stride * image.PixelHeight];
                    image.CopyPixels(pixels, stride, 0);
                    BitmapSource snapshot = BitmapSource.Create(image.PixelWidth, image.PixelHeight,
                        image.DpiX, image.DpiY, image.Format, image.Palette, pixels, stride);
                    snapshot.Freeze();
                    return snapshot;
                }
                finally { _ = SelectObject(memory, old); }
            }
            finally { _ = DeleteObject(bitmap); }
        }
        finally { _ = DeleteDC(memory); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public uint Size, Flags; public IntPtr DefaultScheme; }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr updateRegion, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetSysColorBrush(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObject(IntPtr value, int size, out LogFont font);
    [DllImport("gdi32.dll")] private static extern uint GetTextColor(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetBkColor(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

internal sealed class LaunchEvidenceTheoryAttribute : TheoryAttribute
{
    public LaunchEvidenceTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LaunchVisibilityEvidenceTests.OutputVariable)))
            Skip = "Set MYCAPTURE_LAUNCH_EVIDENCE to an output directory to capture the native launch notices.";
    }
}
