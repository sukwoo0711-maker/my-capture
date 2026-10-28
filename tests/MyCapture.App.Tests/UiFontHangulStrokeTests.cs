using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The bundled Pretendard keeps every Hangul stroke on every surface. Hinted rasterization
/// (Fixed hinting, Auto hinting of static text on screen, or Display formatting) dropped the thin
/// ㅡ of the Regular weight at body sizes: 4.9.0 showed "4.9.0 버전ㅇ로 업데이트됨" in the library's
/// update notice and "핀ㅇ로 띄우기" in the capture card menu at 100% scale.
/// </summary>
public sealed class UiFontHangulStrokeTests
{
    /// <summary>Syllables whose ㅡ is the stroke hinting dropped.</summary>
    private const string BarSyllables = "으스";

    /// <summary>The longest horizontal ink row must span this much of an em when 으 or 스 has its ㅡ.</summary>
    private const double BarRatio = 0.6;

    private const string Specimen = "4.9.0 버전으로 업데이트됨 · 핀으로 띄우기 · 으 스 트 를 은";

    [Fact]
    public void UiFontSettingsKeepTheBarOfEveryBodySizeSyllable() => StaTestHost.Run(() =>
    {
        var failures = new List<string>();
        foreach (FontWeight weight in new[] { FontWeights.Normal, FontWeights.SemiBold })
            foreach (int dpi in new[] { 96, 120, 144 })
                foreach (double size in new[] { 9, 9.5, 10.5, 11, 11.5, 12, 13, 13.5, 14, 14.5, 16 })
                    failures.AddRange(MissingBars(ThemeFonts.Ui, weight, size, dpi,
                        ThemeFonts.Formatting, ThemeFonts.Rendering, ThemeFonts.Hinting));
        Assert.True(failures.Count == 0, "The UI font lost the ㅡ of: " + string.Join(", ", failures));
    });

    [Fact]
    public void HintedRasterizationIsWhatDropsTheBar() => StaTestHost.Run(() =>
    {
        // The settings 4.9.0 shipped, and Display formatting. If this ever stops failing, the font
        // or WPF changed and the unhinted setting can be reconsidered.
        Assert.NotEmpty(MissingBars(ThemeFonts.Ui, FontWeights.Normal, 14, 96,
            TextFormattingMode.Ideal, TextRenderingMode.Grayscale, TextHintingMode.Fixed));
        Assert.NotEmpty(MissingBars(ThemeFonts.Ui, FontWeights.Normal, 13.5, 96,
            TextFormattingMode.Display, TextRenderingMode.Grayscale, TextHintingMode.Animated));
    });

    [Fact]
    public Task MenusToolTipsPopupsAndNoticesDrawTheUiFontUnhinted() =>
        SettingsThemePreviewTests.RunIsolated(nameof(MenusToolTipsPopupsAndNoticesDrawTheUiFontUnhinted), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_HANGUL_EVIDENCE");
            // Evidence captures what the screen shows, so its window is placed on screen.
            using Surfaces surfaces = Surfaces.Open(onScreen: !string.IsNullOrWhiteSpace(evidence));
            var report = new StringBuilder();
            var failures = new List<string>();
            foreach ((string name, FrameworkElement root) in surfaces.Roots)
            {
                TextBlock[] labels = KoreanLabels(root);
                if (labels.Length == 0) failures.Add($"{name} shows no Korean text");
                foreach (TextBlock label in labels)
                {
                    string where = $"{name} '{label.Text}' ({label.FontSize} DIP {label.FontWeight})";
                    TextFormattingMode formatting = TextOptions.GetTextFormattingMode(label);
                    TextHintingMode hinting = EffectiveHinting(label);
                    TextRenderingMode rendering = TextOptions.GetTextRenderingMode(label);
                    report.AppendLine($"{where}: {formatting}, {rendering}, {hinting} hinting, {label.FontFamily.Source}");
                    if (!label.FontFamily.Source.Contains("Pretendard", StringComparison.Ordinal)) failures.Add($"{where} is not in the UI font");
                    if (formatting != ThemeFonts.Formatting) failures.Add($"{where} is {formatting}-formatted");
                    if (hinting != ThemeFonts.Hinting) failures.Add($"{where} is drawn with {hinting} hinting");
                    if (rendering != ThemeFonts.Rendering) failures.Add($"{where} is drawn with {rendering} antialiasing");
                    // The surface's own size, weight and text settings, at every baseline phase.
                    failures.AddRange(MissingBars(label.FontFamily, label.FontWeight, label.FontSize, 96, formatting, rendering, hinting)
                        .Select(missing => $"{where}: {missing}"));
                    if (!label.Text.Contains('으')) continue;
                    foreach (int dpi in new[] { 96, 144 })
                    {
                        double ratio = BarRatioOf(label, dpi);
                        report.AppendLine($"    으 at {dpi} DPI: longest ink row {ratio:0.00} em");
                        if (ratio < BarRatio) failures.Add($"{where}: 으 at {dpi} DPI has no ㅡ (longest ink row {ratio:0.00} em)");
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(evidence))
                surfaces.SaveEvidence(DiagnosticOutputPaths.Create(evidence), report);
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }, typeof(UiFontHangulStrokeTests));

    private static TextBlock[] KoreanLabels(FrameworkElement root) =>
        [.. Descendants(root).OfType<TextBlock>().Where(label => label.IsVisible && label.Text.Any(ch => ch is >= '가' and <= '힣'))];

    /// <summary>
    /// The hinting <paramref name="element"/>'s glyphs are drawn with. Hinting is a render option of the
    /// visual tree, not an inherited property: the nearest visual ancestor that sets it decides, up to
    /// the root of the element's own tree (a window, or the root of a popup, menu or tooltip).
    /// </summary>
    private static TextHintingMode EffectiveHinting(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (TextOptions.GetTextHintingMode(current) is var mode and not TextHintingMode.Auto) return mode;
        return TextHintingMode.Auto;
    }

    /// <summary>
    /// Renders the root of <paramref name="label"/>'s visual tree at <paramref name="dpi"/>, so every
    /// render option above the label applies, and measures the ㅡ under the label's first 으.
    /// </summary>
    private static double BarRatioOf(TextBlock label, int dpi)
    {
        var root = (Visual)label;
        while (VisualTreeHelper.GetParent(root) is Visual parent) root = parent;
        var bounds = VisualTreeHelper.GetDescendantBounds(root);
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Right * scale) + 1, (int)Math.Ceiling(bounds.Bottom * scale) + 1,
            dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        int index = label.Text.IndexOf('으');
        TextPointer start = label.ContentStart.GetPositionAtOffset(index + 1) ?? label.ContentStart;
        Rect caret = start.GetCharacterRect(LogicalDirection.Forward);
        Point origin = label.TransformToAncestor(root).Transform(new Point(caret.X, 0));
        var cell = new Int32Rect((int)Math.Floor(origin.X * scale), (int)Math.Floor(origin.Y * scale),
            (int)Math.Ceiling(label.FontSize * scale), (int)Math.Ceiling(label.ActualHeight * scale));
        cell.Width = Math.Min(cell.Width, bitmap.PixelWidth - cell.X);
        cell.Height = Math.Min(cell.Height, bitmap.PixelHeight - cell.Y);
        Color ink = label.Foreground is SolidColorBrush brush ? brush.Color : Colors.White;
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[cell.Width * cell.Height * 4];
        converted.CopyPixels(cell, pixels, cell.Width * 4, 0);
        // The background is the commonest colour of the cell's top row, above the glyph.
        int background = Enumerable.Range(0, cell.Width).Select(x => Luma(pixels, x * 4))
            .GroupBy(value => value).OrderByDescending(group => group.Count()).First().Key;
        double contrast = 0.299 * ink.R + 0.587 * ink.G + 0.114 * ink.B - background;
        return LongestInkRow(bitmap, cell, (bgra, offset) => (Luma(bgra, offset) - background) / contrast > 0.37)
            / (label.FontSize * scale);
    }

    /// <summary>
    /// Each 으 and 스 drawn with these settings, at every eighth of a pixel of baseline phase, that
    /// shows no row of ink as long as its ㅡ.
    /// </summary>
    internal static List<string> MissingBars(FontFamily family, FontWeight weight, double size, int dpi,
        TextFormattingMode formatting, TextRenderingMode rendering, TextHintingMode hinting)
    {
        var missing = new List<string>();
        for (int phase = 0; phase < 8; phase++)
        {
            foreach (char syllable in BarSyllables)
            {
                var label = new TextBlock
                {
                    Text = syllable.ToString(), FontFamily = family, FontSize = size, FontWeight = weight,
                    Foreground = Brushes.Black, Margin = new Thickness(2, 2 + (phase / 8d * 96 / dpi), 2, 2),
                };
                TextOptions.SetTextFormattingMode(label, formatting);
                TextOptions.SetTextRenderingMode(label, rendering);
                TextOptions.SetTextHintingMode(label, hinting);
                var host = new Border { Background = Brushes.White, Child = label, UseLayoutRounding = false };
                host.Language = XmlLanguage.GetLanguage("ko-KR");
                VisualTreeHelper.SetRootDpi(host, new DpiScale(dpi / 96d, dpi / 96d));
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(host.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(host);
                double ratio = LongestInkRow(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight),
                    static (bgra, offset) => Luma(bgra, offset) < 160) / (size * dpi / 96);
                if (ratio < BarRatio) missing.Add($"{syllable} {weight} {size} DIP at {dpi} DPI +{phase}/8 px ({ratio:0.00} em)");
            }
        }
        return missing;
    }

    /// <summary>The most pixels in one row of <paramref name="cell"/> that are <paramref name="inked"/>.</summary>
    private static int LongestInkRow(BitmapSource bitmap, Int32Rect cell, Func<byte[], int, bool> inked)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[cell.Width * cell.Height * 4];
        converted.CopyPixels(cell, pixels, cell.Width * 4, 0);
        int best = 0;
        for (int y = 0; y < cell.Height; y++)
        {
            int count = 0;
            for (int x = 0; x < cell.Width; x++)
                if (inked(pixels, (y * cell.Width + x) * 4)) count++;
            best = Math.Max(best, count);
        }
        return best;
    }

    private static int Luma(byte[] bgra, int offset) =>
        (int)Math.Round(0.114 * bgra[offset] + 0.587 * bgra[offset + 1] + 0.299 * bgra[offset + 2]);

    /// <summary>
    /// The standard surfaces that draw small Korean UI text: a standard window with the update notice,
    /// a button and a text box, its ContextMenu (with a submenu), a ToolTip and an in-tree Popup.
    /// </summary>
    private sealed class Surfaces : IDisposable
    {
        private readonly Window _window;
        private readonly ContextMenu _menu;
        private readonly MenuItem _submenu;
        private readonly ToolTip _tip;
        private readonly Popup _popup;
        private readonly EngagementNotice _notice;

        private Surfaces(Window window, ContextMenu menu, MenuItem submenu, ToolTip tip, Popup popup, EngagementNotice notice)
        {
            _window = window;
            _menu = menu;
            _submenu = submenu;
            _tip = tip;
            _popup = popup;
            _notice = notice;
        }

        internal IEnumerable<(string Name, FrameworkElement Root)> Roots =>
        [
            ("update notice", _notice),
            ("context menu", _menu),
            ("submenu", (FrameworkElement)((Popup)_submenu.Template.FindName("PART_Popup", _submenu)).Child),
            ("tooltip", _tip),
            ("popup", (FrameworkElement)_popup.Child),
            ("window", (FrameworkElement)_window.Content),
        ];

        internal static Surfaces Open(bool onScreen)
        {
            var button = new Button { Content = "스크린샷 저장", Margin = new Thickness(20, 20, 20, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var field = new TextBox { Text = "으 스 트 를 은", Width = 200, Margin = new Thickness(20, 80, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var notice = new EngagementNotice();
            var popup = new Popup
            {
                PlacementTarget = button, Placement = PlacementMode.Bottom, AllowsTransparency = true,
                Child = new Border { Padding = new Thickness(8), Child = new TextBlock { Text = "핀으로 띄우기" } },
            };
            popup.Child.SetValue(Border.BackgroundProperty, Application.Current.TryFindResource("Surface.Floating"));
            var layout = new Grid { Width = 560, Height = 300 };
            layout.Children.Add(button);
            layout.Children.Add(field);
            layout.Children.Add(notice);
            layout.Children.Add(popup);
            var window = new Window
            {
                Content = layout, SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = onScreen ? 40 : -4000, Top = onScreen ? 40 : -4000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            var submenu = new MenuItem { Header = "정렬" };
            submenu.Items.Add(new MenuItem { Header = "이름으로" });
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            menu.Items.Add(new MenuItem { Header = UiText.Get("Gallery.Float"), InputGestureText = "P" });
            menu.Items.Add(new MenuItem { Header = UiText.Get("Tray_PinClipboard") });
            menu.Items.Add(submenu);
            button.ContextMenu = menu;
            var tip = new ToolTip { Content = "클립보드를 핀으로 띄우기", PlacementTarget = button, Placement = PlacementMode.Bottom };
            button.ToolTip = tip;
            window.Show();
            notice.ShowUpdateCompleted("4.9.0.0");
            notice.Opacity = 1;
            menu.IsOpen = true;
            Settle(window);
            submenu.IsSubmenuOpen = true;
            tip.IsOpen = true;
            popup.IsOpen = true;
            Settle(window);
            return new Surfaces(window, menu, submenu, tip, popup, notice);
        }

        /// <summary>
        /// Each surface as rendered at 96 and 144 DPI and as captured on screen, and a specimen line in
        /// each surface's text settings, with three-times enlargements and the measured bars.
        /// </summary>
        internal void SaveEvidence(string directory, StringBuilder report)
        {
            // Let entrance fades finish and static text settle into its final rendering first.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            foreach ((string name, FrameworkElement root) in Roots)
            {
                string slug = name.Replace(' ', '-');
                foreach (int dpi in new[] { 96, 144 })
                {
                    double scale = dpi / 96d;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
                        (int)Math.Ceiling(root.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    Save(OnBackground(bitmap), directory, $"surface-{slug}-{dpi}dpi.png");
                }
                if (PresentationSource.FromVisual(root) is HwndSource source)
                    Save(Capture(source.Handle), directory, $"screen-{slug}.png");
            }

            foreach (int dpi in new[] { 96, 144 })
            {
                var sheet = new StackPanel { Background = Application.Current.TryFindResource("Surface.Floating") as Brush ?? Brushes.Black };
                VisualTreeHelper.SetRootDpi(sheet, new DpiScale(dpi / 96d, dpi / 96d));
                foreach ((string name, FrameworkElement root) in Roots)
                {
                    TextBlock? label = KoreanLabels(root).FirstOrDefault();
                    if (label is null) continue;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
                    row.Children.Add(new TextBlock { Text = name, Width = 110, FontFamily = new FontFamily("Consolas"), FontSize = 11,
                        Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
                    var sample = new TextBlock { Text = Specimen, FontFamily = label.FontFamily, FontSize = label.FontSize,
                        FontWeight = label.FontWeight, Foreground = label.Foreground, Language = label.Language };
                    TextOptions.SetTextFormattingMode(sample, TextOptions.GetTextFormattingMode(label));
                    TextOptions.SetTextRenderingMode(sample, TextOptions.GetTextRenderingMode(label));
                    TextOptions.SetTextHintingMode(sample, EffectiveHinting(label));
                    row.Children.Add(sample);
                    sheet.Children.Add(row);
                }
                sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                sheet.Arrange(new Rect(sheet.DesiredSize));
                sheet.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(sheet.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(sheet.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(sheet);
                Save(bitmap, directory, $"specimen-{dpi}dpi.png");
                Save(Zoom(bitmap, 3), directory, $"specimen-{dpi}dpi-x3.png");
                foreach (TextBlock sample in sheet.Children.OfType<StackPanel>().Select(row => (TextBlock)row.Children[1]))
                    report.AppendLine($"specimen {dpi} DPI {sample.FontSize} DIP: 으 longest ink row {BarRatioOf(sample, dpi):0.00} em");
            }
            File.WriteAllText(DiagnosticOutputPaths.Child(directory, "report.txt"), report.ToString());
        }

        private static BitmapSource OnBackground(BitmapSource surface)
        {
            var visual = new DrawingVisual();
            double width = surface.PixelWidth * 96 / surface.DpiX, height = surface.PixelHeight * 96 / surface.DpiY;
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Application.Current.TryFindResource("Surface.Base") as Brush ?? Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawImage(surface, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(surface.PixelWidth, surface.PixelHeight, surface.DpiX, surface.DpiY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            return bitmap;
        }

        private static BitmapSource Zoom(BitmapSource source, int factor)
        {
            var image = new Image { Source = source, Width = source.PixelWidth * factor, Height = source.PixelHeight * factor, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            image.Measure(new Size(image.Width, image.Height));
            image.Arrange(new Rect(0, 0, image.Width, image.Height));
            var bitmap = new RenderTargetBitmap(source.PixelWidth * factor, source.PixelHeight * factor, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(image);
            return bitmap;
        }

        private static void Save(BitmapSource bitmap, string directory, string name)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(DiagnosticOutputPaths.Child(directory, name));
            encoder.Save(output);
        }

        /// <summary>What the window manager composes for <paramref name="hwnd"/>: the text as drawn on screen.</summary>
        private static BitmapSource Capture(IntPtr hwnd)
        {
            GetWindowRect(hwnd, out NativeRect rect);
            int width = Math.Max(1, rect.Right - rect.Left), height = Math.Max(1, rect.Bottom - rect.Top);
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
            IntPtr previous = SelectObject(memory, bitmap);
            try
            {
                PrintWindow(hwnd, memory, 2 /* PW_RENDERFULLCONTENT */);
                SelectObject(memory, previous);
                BitmapSource captured = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                captured.Freeze();
                return captured;
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(memory);
                _ = ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static void Settle(Window window)
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        public void Dispose()
        {
            _tip.IsOpen = false;
            _popup.IsOpen = false;
            _submenu.IsSubmenuOpen = false;
            _menu.IsOpen = false;
            _window.Close();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}
