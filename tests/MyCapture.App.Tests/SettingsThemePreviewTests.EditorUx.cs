using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string EditorUxEvidence = "MYCAPTURE_EDITOR_UX_EVIDENCE";

    /// <summary>
    /// Renders the editor's rotate commands and the step-number inspector in the real editor
    /// window under the glass and high-contrast themes.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_EDITOR_UX_EVIDENCE</c> to a directory to keep the PNGs, and
    /// <c>MYCAPTURE_EDITOR_UX_PHASE</c> to label them (for example before and after a fix).
    /// </remarks>
    [Fact]
    public Task EditorUx_RotateCommandsAndStepNumberInspectorRenderInEveryEvidenceTheme() =>
        RunIsolated(nameof(EditorUx_RotateCommandsAndStepNumberInspectorRenderInEveryEvidenceTheme), VerifyEditorUxEvidence);

    private static void VerifyEditorUxEvidence()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_UX_PHASE") is { Length: > 0 } label ? label : "after";
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                RenderEditorUxScene(theme, phase);
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void RenderEditorUxScene(AppTheme theme, string phase)
    {
        BitmapSource bitmap = EditorEvidenceBackground(720, 300);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 1180, Height = 560,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Drain(window);
            AnnotationEditorControl editor = window.Editor;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var controller = (AnnotationEditorController)typeof(AnnotationEditorControl).GetField("_controller", flags)!.GetValue(editor)!;
            var rotateLeft = (Button)typeof(AnnotationEditorControl).GetField("_rotateLeftButton", flags)!.GetValue(editor)!;
            var rotateRight = (Button)typeof(AnnotationEditorControl).GetField("_rotateRightButton", flags)!.GetValue(editor)!;
            var inspector = (FrameworkElement)typeof(AnnotationEditorControl).GetField("_inspectorPanel", flags)!.GetValue(editor)!;
            MethodInfo selectTool = typeof(AnnotationEditorControl).GetMethod("SelectTool", flags)!;

            // Two numbers already placed, so the tool's next number is 3.
            controller.Tool = EditorTool.StepNumber;
            controller.AddStepNumber(new PointD(60, 60));
            controller.AddStepNumber(new PointD(160, 60));
            controller.SetSelected(null);
            selectTool.Invoke(editor, [EditorTool.StepNumber]);
            Drain(window);

            Assert.True(rotateLeft.IsVisible && rotateRight.IsVisible);
            BitmapSource full = RenderEditorWindow(window);
            var bar = (FrameworkElement)rotateLeft.Parent;
            SaveEditorUxEvidence(Crop(full, window, bar, 6), $"editor-rotate-commands-{theme}-{phase}.png");
            SaveEditorUxEvidence(Crop(full, window, inspector, 0), $"editor-step-inspector-{theme}-{phase}.png");
            SaveEditorUxEvidence(full, $"editor-step-window-{theme}-{phase}.png");
        }
        finally
        {
            window.Close();
            Drain(window);
        }
    }

    private static BitmapSource RenderEditorWindow(Window window)
    {
        Drain(window);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass themes leave the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Crop(BitmapSource full, Window window, FrameworkElement element, double margin)
    {
        double scale = full.PixelWidth / window.ActualWidth;
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        bounds.Inflate(margin, margin);
        bounds.Intersect(new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        var pixels = new Int32Rect((int)Math.Floor(bounds.X * scale), (int)Math.Floor(bounds.Y * scale),
            (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
        pixels.Width = Math.Min(pixels.Width, full.PixelWidth - pixels.X);
        pixels.Height = Math.Min(pixels.Height, full.PixelHeight - pixels.Y);
        var cropped = new CroppedBitmap(full, pixels);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// Lays every canvas cursor out on one sheet: the tool cursors at 32, 48 and 64 pixels on a
    /// light and a dark capture, with the hot spot marked, beside the Windows cursors the
    /// other tools keep.
    /// </summary>
    [Fact]
    public Task EditorUx_CursorSheetShowsEveryToolCursorWithItsHotSpot() =>
        RunIsolated(nameof(EditorUx_CursorSheetShowsEveryToolCursorWithItsHotSpot), RenderCursorSheet);

    private static void RenderCursorSheet()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        try
        {
            var red = new MyCapture.Core.Primitives.ColorRgba(255, 0xEF, 0x44, 0x44);
            EditorCursorStyle machine = EditorCursorStyle.Read();
            var rows = new (string Label, EditorCursorKind Kind, int SystemId)[]
            {
                ("선택 V · 빈 곳", EditorCursorKind.Arrow, 32512),
                ("선택 V · 주석 위", EditorCursorKind.Move, 32646),
                ("선택 V · 모서리 핸들", EditorCursorKind.ResizeNorthwestSoutheast, 32642),
                ("선택 V · 좌우 핸들", EditorCursorKind.ResizeWestEast, 32644),
                ("텍스트 T", EditorCursorKind.TextBeam, 32513),
                ("사각형 R · 타원 E · 화살표 A", EditorCursorKind.Cross, 32515),
                ("연필 P", EditorCursorKind.Pen, 0),
                ("형광펜 H", EditorCursorKind.Highlighter, 0),
                ("단계 번호 N (다음 번호 3)", EditorCursorKind.StepNumber, 0),
                ("자르기 X", EditorCursorKind.Crop, 0),
                ("모자이크 M", EditorCursorKind.Mosaic, 0),
                ("블러 B", EditorCursorKind.Blur, 0),
                ("이미지 I", EditorCursorKind.Image, 0),
            };
            var sheet = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)) };
            var header = new Grid { Margin = new Thickness(12, 10, 12, 6) };
            foreach (double width in new double[] { 230, 300, 300, 300 }) header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            AddCell(header, 0, "도구 · 상황", bold: true);
            AddCell(header, 1, "도구 커서는 기본 흰색 스타일 · 밝은 캡처 32 · 48 · 64 px (빨간 점: 핫스팟)", bold: true);
            AddCell(header, 2, "도구 커서는 기본 흰색 스타일 · 어두운 캡처", bold: true);
            AddCell(header, 3, "이 PC 포인터 스타일 · 어두운 캡처", bold: true);
            sheet.Children.Add(header);
            foreach ((string label, EditorCursorKind kind, int systemId) in rows)
            {
                var row = new Grid { Margin = new Thickness(12, 4, 12, 4) };
                foreach (double width in new double[] { 230, 300, 300, 300 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
                string source = EditorCursorPolicy.IsCustom(kind) ? label : label + " (Windows 커서, 이 PC 설정)";
                AddCell(row, 0, source, bold: false);
                for (int column = 1; column <= 3; column++)
                {
                    bool dark = column >= 2;
                    var strip = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(dark ? Color.FromRgb(0x1F, 0x29, 0x37) : Color.FromRgb(0xE8, 0xEC, 0xF2)) };
                    foreach (int size in new[] { 32, 48, 64 })
                    {
                        (BitmapSource bitmap, int hotX, int hotY) = EditorCursorPolicy.IsCustom(kind)
                            ? Unpack(EditorCursors.Render(kind, size, 3, red, column == 3 ? machine : EditorCursorStyle.White))
                            : SystemCursorImage(systemId, size);
                        var cell = new Grid { Width = 72, Height = 72, Margin = new Thickness(4) };
                        var image = new Image { Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Stretch = Stretch.None,
                            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                        cell.Children.Add(image);
                        if (!dark)
                        {
                            var hot = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brushes.Red,
                                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                                Margin = new Thickness(hotX - 1.5, hotY - 1.5, 0, 0) };
                            cell.Children.Add(hot);
                        }

                        strip.Children.Add(cell);
                    }

                    Grid.SetColumn(strip, column);
                    strip.Margin = new Thickness(0, 0, 8, 0);
                    row.Children.Add(strip);
                }

                sheet.Children.Add(row);
            }

            sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sheet.Arrange(new Rect(sheet.DesiredSize));
            sheet.UpdateLayout();
            var bitmapSheet = new RenderTargetBitmap((int)Math.Ceiling(sheet.ActualWidth), (int)Math.Ceiling(sheet.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmapSheet.Render(sheet);
            bitmapSheet.Freeze();
            SaveEditorUxEvidence(bitmapSheet, "editor-cursor-sheet.png");
        }
        finally
        {
            application.Shutdown();
        }

        static (BitmapSource, int, int) Unpack(EditorCursorImage image) => (image.Bitmap, image.HotSpotX, image.HotSpotY);

        static void AddCell(Grid grid, int column, string text, bool bold)
        {
            var block = new TextBlock { Text = text, FontSize = 13, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadSystemCursor(IntPtr instance, nint name);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CopyImage(IntPtr image, int type, int width, int height, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetIconInfo")]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr cursor);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool Icon;
        public int HotX;
        public int HotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    /// <summary>The Windows cursor <paramref name="id"/> at <paramref name="size"/> pixels, for comparison on the sheet.</summary>
    private static (BitmapSource Bitmap, int HotX, int HotY) SystemCursorImage(int id, int size)
    {
        const int ImageCursor = 2, CopyFromResource = 0x4000;
        // The shared system cursor stays untouched; a private copy at the requested size is drawn.
        IntPtr shared = LoadSystemCursor(IntPtr.Zero, id);
        Assert.NotEqual(IntPtr.Zero, shared);
        IntPtr handle = CopyImage(shared, ImageCursor, size, size, CopyFromResource);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            Assert.True(GetIconInfo(handle, out IconInfo info));
            if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
            BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return (bitmap, info.HotX, info.HotY);
        }
        finally
        {
            DestroyCursor(handle);
        }
    }

    private static void SaveEditorUxEvidence(BitmapSource bitmap, string name)
    {
        string? requested = Environment.GetEnvironmentVariable(EditorUxEvidence);
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
