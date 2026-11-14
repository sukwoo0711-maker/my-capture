#!/usr/bin/env dotnet
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property ImportDirectoryBuildProps=false
#:property ImportDirectoryBuildTargets=false
#:property NuGetAudit=false
#:property UseSharedCompilation=false
#:property TreatWarningsAsErrors=true
#:property PublishAot=false
// Directory.Build.props is imported before these apply: undo its x64 PlatformTarget and Windows
// 10 floor so the probe runs through the invoking muxer with the muxer's architecture.
#:property PlatformTarget=AnyCPU
#:property SupportedOSPlatformVersion=7.0
#:property UseAppHost=false

// Describes the CI desktop the WPF and capture tests run on: render tier, whether WPF's software
// rasterizer (RenderTargetBitmap) applies effects, what the screen shows for the same effect,
// display, input locale, the window that owns the foreground and the windows above the fixture
// point, and the Media Foundation H.264 encoders. It only reads state and shows one small
// unactivated window; every section reports its own failure and the probe exits 0.
// Usage: dotnet run --file runner-desktop-probe.cs -- <folder containing MyCapture.dll>
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

string? appDirectory = args.Length > 0 ? args[0] : null;
var thread = new Thread(() => Probe.Run(appDirectory));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return 0;

static class Probe
{
    // The studio-range shader maps premultiplied grey 16 to 0; an unapplied effect leaves 16.
    private const byte StudioBlack = 16;
    private static readonly Point FixturePoint = new(120, 120);

    internal static void Run(string? appDirectory)
    {
        Section("process", () => $"arch={RuntimeInformation.ProcessArchitecture}, os={RuntimeInformation.OSDescription}, "
            + $"runtime={RuntimeInformation.FrameworkDescription}");
        Section("render", () => $"tier={RenderCapability.Tier >> 16}, ps20Hardware={RenderCapability.IsPixelShaderVersionSupported(2, 0)}, "
            + $"ps20Software={RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0)}, "
            + $"maxHardwareTexture={RenderCapability.MaxHardwareTextureSize}");
        Section("display", () => $"screen={GetSystemMetrics(0)}x{GetSystemMetrics(1)} px, virtual={GetSystemMetrics(78)}x{GetSystemMetrics(79)} px, "
            + $"systemDpi={GetDpiForSystem()}, workArea={SystemParameters.WorkArea}, monitors={GetSystemMetrics(80)}");
        Section("input", () =>
        {
            int count = GetKeyboardLayoutList(0, null);
            var layouts = new IntPtr[Math.Max(count, 0)];
            GetKeyboardLayoutList(layouts.Length, layouts);
            return $"culture={CultureInfo.CurrentCulture.Name}, uiCulture={CultureInfo.CurrentUICulture.Name}, "
                + $"threadLayout=0x{GetKeyboardLayout(0):X8}, layouts=[{string.Join(", ", layouts.Select(l => $"0x{l:X8}"))}], "
                + $"inputLanguage={InputLanguageManager.Current.CurrentInputLanguage?.Name}";
        });
        Section("software effects", () =>
        {
            string shader = "MyCapture.dll not given";
            if (StudioRangeEffect(appDirectory) is { } effect)
            {
                Color pixel = RenderedCentre(effect, 16);
                shader = $"studio-range grey {StudioBlack} -> {pixel.R} ({(pixel.R <= 2 ? "applied" : pixel.R == StudioBlack ? "not applied" : "unexpected")})";
            }
            byte shadow = RenderedShadowAlpha();
            return $"RenderTargetBitmap: {shader}; drop shadow alpha 4 DIP below its element = {shadow} ({(shadow > 8 ? "applied" : "not applied")})";
        });
        Section("on-screen effects", () => OnScreen(appDirectory));
        Section("desktop", Desktop);
        Section("h264 encoders", () => $"all={CountH264Encoders(0x3F)}, hardware={CountH264Encoders(0x04 | 0x20)}");
    }

    private static void Section(string name, Func<string> describe)
    {
        string text;
        try { text = describe(); }
        catch (Exception ex) { text = "probe failed: " + ex.GetType().Name + ": " + ex.Message; }
        Console.WriteLine($"PROBE {name}: {text}");
    }

    private static Effect? StudioRangeEffect(string? appDirectory)
    {
        if (string.IsNullOrEmpty(appDirectory)) return null;
        Assembly app = Assembly.LoadFrom(System.IO.Path.Combine(appDirectory, "MyCapture.dll"));
        Type type = app.GetType("MyCapture.App.Recording.StudioRangeVideoEffect", throwOnError: true)!;
        return (Effect)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private static Color RenderedCentre(Effect effect, int size)
    {
        var rectangle = new Rectangle { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromRgb(StudioBlack, StudioBlack, StudioBlack)), Effect = effect };
        rectangle.Measure(new Size(size, size));
        rectangle.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(rectangle);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(size / 2, size / 2, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static byte RenderedShadowAlpha()
    {
        var panel = new Border
        {
            Width = 16, Height = 16, Background = Brushes.Black,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 0, Opacity = 1 },
        };
        var root = new Grid { Width = 64, Height = 64 };
        root.Children.Add(panel);
        root.Measure(new Size(64, 64));
        root.Arrange(new Rect(0, 0, 64, 64));
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(32, 32 + 8 + 4, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static string OnScreen(string? appDirectory)
    {
        Effect? effect = StudioRangeEffect(appDirectory);
        var grey = new SolidColorBrush(Color.FromRgb(StudioBlack, StudioBlack, StudioBlack));
        var withEffect = new Rectangle { Width = 32, Height = 32, Fill = grey, Effect = effect };
        var plain = new Rectangle { Width = 32, Height = 32, Fill = grey };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        row.Children.Add(withEffect);
        row.Children.Add(plain);
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, Left = FixturePoint.X, Top = FixturePoint.Y, SizeToContent = SizeToContent.WidthAndHeight, Content = row,
        };
        try
        {
            window.Show();
            Pump(TimeSpan.FromMilliseconds(750));
            IntPtr handle = new WindowInteropHelper(window).Handle;
            var source = (HwndSource)PresentationSource.FromVisual(window)!;
            string mode = source.CompositionTarget.RenderMode.ToString();
            string Sample(Rectangle target)
            {
                Point centre = target.PointToScreen(new Point(16, 16));
                var point = new NativePoint { X = (int)centre.X, Y = (int)centre.Y };
                IntPtr screen = GetDC(IntPtr.Zero);
                uint colour;
                try { colour = GetPixel(screen, point.X, point.Y); }
                finally { ReleaseDC(IntPtr.Zero, screen); }
                IntPtr top = GetAncestor(WindowFromPoint(point), 2);
                return $"({colour & 0xFF},{(colour >> 8) & 0xFF},{(colour >> 16) & 0xFF}) at {point.X},{point.Y}, "
                    + $"topmost window there {(top == handle ? "is the probe" : Describe(top))}";
            }
            return $"renderMode={mode}, tier={RenderCapability.Tier >> 16}; effect {(effect is null ? "none" : "studio-range")} {Sample(withEffect)}; "
                + $"reference {Sample(plain)}";
        }
        finally { window.Close(); }
    }

    private static string Desktop()
    {
        var text = new StringBuilder();
        text.Append($"foreground {Describe(GetForegroundWindow())}; taskbar={(FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)}; ");
        text.Append($"explorer processes={Process.GetProcessesByName("explorer").Length}; ");
        text.Append($"at fixture point {Describe(GetAncestor(WindowFromPoint(new NativePoint { X = (int)FixturePoint.X, Y = (int)FixturePoint.Y }), 2))}");
        int listed = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            text.Append(Environment.NewLine).Append("  z").Append(listed).Append(' ').Append(Describe(window));
            return ++listed < 25;
        }, IntPtr.Zero);
        return text.ToString();
    }

    private static string Describe(IntPtr window)
    {
        if (window == IntPtr.Zero) return "none";
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        GetWindowThreadProcessId(window, out uint processId);
        string process;
        try { using Process owner = Process.GetProcessById((int)processId); process = owner.ProcessName; }
        catch (ArgumentException) { process = "exited"; }
        GetWindowRect(window, out NativeRect rect);
        bool topmost = (GetWindowLongPtr(window, -20).ToInt64() & 0x8) != 0;
        DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int));
        return $"0x{window:X} {process}({processId}) class={name} title=\"{title}\" rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} "
            + $"topmost={topmost} cloaked={cloaked != 0}";
    }

    private static int CountH264Encoders(uint flags)
    {
        Marshal.ThrowExceptionForHR(MFStartup(0x00020070, 1));
        try
        {
            var output = new RegisterTypeInfo
            {
                MajorType = new Guid("73646976-0000-0010-8000-00AA00389B71"),
                SubType = new Guid("34363248-0000-0010-8000-00AA00389B71"),
            };
            Marshal.ThrowExceptionForHR(MFTEnumEx(new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"), flags, IntPtr.Zero, ref output,
                out IntPtr activates, out uint count));
            for (int i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(activates, i * IntPtr.Size));
            Marshal.FreeCoTaskMem(activates);
            return (int)count;
        }
        finally { MFShutdown(); }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct RegisterTypeInfo { public Guid MajorType, SubType; }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? layouts);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, IntPtr input, ref RegisterTypeInfo output,
        out IntPtr activates, out uint count);
}
