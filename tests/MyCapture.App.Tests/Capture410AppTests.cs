using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Offscreen WPF/model tests only: no Window.Show, desktop capture, input injection, or global hooks.</summary>
public sealed class Capture410AppTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InactiveOverlayDoesNotReceiveAnotherDispatcherThemeRefresh(bool previouslyLoaded) => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame(), showMagnifier: false);
        try
        {
            if (previouslyLoaded)
            {
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            }
            StaTestHost.Run(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var refresh = ThemeService.ScheduleCropResourceRefresh(dispatcher, new ResourceDictionary(), () => false);
                dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.Equal(DispatcherOperationStatus.Completed, refresh.Status);
            });
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData("Rectangle")]
    [InlineData("Pen")]
    [InlineData("Text")]
    [InlineData("Select")]
    public void DraggingAnEdgeEntersCropFromTheCurrentTool_AndEscDoesNotModifyPixels(string tool) => StaTestHost.Run(() =>
    {
        var editor = Editor();
        editor.ShortcutActions[tool]();
        BitmapSource original = editor.DisplayedBitmap;
        Assert.Null(editor.EdgeCropGripAt(new PointD(70, 50)));
        Assert.True(editor.TryBeginEdgeCrop(new PointD(0, 50)));
        Assert.Null(editor.PendingCrop);
        Assert.True(editor.ContinueEdgeCrop(new PointD(0.1, 50), false));
        Assert.Null(editor.PendingCrop);
        Assert.True(editor.ContinueEdgeCrop(new PointD(30, 50), false));
        Assert.Equal(CropGrip.Left, editor.PendingCrop!.Grip);
        Assert.Equal(new RectD(30, 0, 130, 120), editor.PendingCrop.Region);
        Assert.Same(original, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Null(editor.PendingCrop);
        Assert.Equal(160, editor.CreateCurrentImageSnapshot().CanvasWidth);
        Assert.Same(original, editor.DisplayedBitmap);
    });

    [Fact]
    public void FractionalEdgeDragShowsThePixelDimensionsThatEnterActuallyProduces() => StaTestHost.Run(() =>
    {
        var editor = Editor();
        Assert.True(editor.TryBeginEdgeCrop(new PointD(160, 120)));
        Assert.True(editor.ContinueEdgeCrop(new PointD(100.2, 80.2), false));
        editor.PendingCrop!.End();
        Assert.Equal("101", Field<TextBox>(editor, "_cropWidth").Text);
        Assert.Equal("81", Field<TextBox>(editor, "_cropHeight").Text);
        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
        Assert.Equal(101, editor.DisplayedBitmap.PixelWidth);
        Assert.Equal(81, editor.DisplayedBitmap.PixelHeight);
    });

    [Fact]
    public void EdgeCropRetainsRatioNumericEnterUndoRedoAndX() => StaTestHost.Run(() =>
    {
        var editor = Editor();
        Assert.True(editor.TryBeginEdgeCrop(new PointD(160, 120)));
        editor.ContinueEdgeCrop(new PointD(120, 90), true);
        var crop = editor.PendingCrop!;
        Assert.Equal(4.0 / 3, crop.Region.Width / crop.Region.Height, 6);
        crop.End();
        crop.SetRatio(1);
        crop.SetWidth(60);
        Assert.Equal(60, crop.Region.Height);
        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
        Assert.Equal(60, editor.DisplayedBitmap.PixelWidth);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.Equal(160, editor.DisplayedBitmap.PixelWidth);
        Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
        Assert.Equal(60, editor.DisplayedBitmap.PixelWidth);
        Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
        Assert.NotNull(editor.PendingCrop);
        editor.CancelPendingCrop();
    });

    [Theory]
    [InlineData("ko-KR", 1100)]
    [InlineData("en-US", 1100)]
    [InlineData("ko-KR", 680)]
    [InlineData("en-US", 680)]
    public void CropAndAutomaticTrimAreVisibleTopCommandsWithoutClipping(string culture, int width) => StaTestHost.Run(() =>
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        var editor = Editor(width: width);
        foreach (string id in new[] { "Capture410Crop", "Capture410AutoTrim" })
        {
            Button button = Assert.Single(Descendants(editor).OfType<Button>(),
                candidate => AutomationProperties.GetAutomationId(candidate) == id);
            Rect bounds = button.TransformToAncestor(editor).TransformBounds(new Rect(button.RenderSize));
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(bounds.Left >= 0 && bounds.Right <= editor.ActualWidth + 0.1, $"{id}: {bounds}, width {editor.ActualWidth}");
            Assert.True(bounds.Top >= 0 && bounds.Bottom < 140);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
        }
    });

    [Fact]
    public void AutomaticTrimPreviewsRenderedAnnotations_AndOnlyEnterCommits() => RunOffscreenAsync(async () =>
    {
        var document = AnnotationDocument.CreateFor(160, 120);
        document.Add(new RectangleAnnotation
        {
            Rect = new RectD(5, 40, 12, 12), Stroke = ColorRgba.FromRgb(200, 0, 0), StrokeThickness = 2,
        });
        var editor = Editor(document: document);
        BitmapSource original = editor.DisplayedBitmap;
        await editor.PreviewAutomaticTrimAsync();
        Assert.NotNull(editor.PendingCrop);
        Assert.InRange(editor.PendingCrop!.Region.Left, 2, 5);
        Assert.Equal(24, editor.PendingCrop.Region.Top);
        Assert.Same(original, editor.DisplayedBitmap);
        Assert.Equal(160, editor.CreateCurrentImageSnapshot().CanvasWidth);
        Assert.Single(editor.CreateCurrentImageSnapshot().Items);
        editor.ApplyPendingCrop();
        Assert.True(editor.DisplayedBitmap.PixelWidth < 160);
        Assert.Single(editor.CreateCurrentImageSnapshot().Items);
        editor.ShortcutActions["Undo"]();
        Assert.Equal(160, editor.DisplayedBitmap.PixelWidth);
        Assert.Equal(120, editor.DisplayedBitmap.PixelHeight);
    });

    [Fact]
    public void AutomaticTrimNoOpAndConcurrentEditDoNotOverwriteTheDocument() => RunOffscreenAsync(async () =>
    {
        var uniform = Editor(uniform: true);
        BitmapSource original = uniform.DisplayedBitmap;
        await uniform.PreviewAutomaticTrimAsync();
        Assert.Null(uniform.PendingCrop);
        Assert.Same(original, uniform.DisplayedBitmap);

        var editor = Editor();
        Task trim = editor.PreviewAutomaticTrimAsync();
        editor.ApplyCrop(new RectD(0, 0, 100, 100));
        await trim;
        Assert.Null(editor.PendingCrop);
        Assert.Equal(100, editor.DisplayedBitmap.PixelWidth);
        Assert.Equal(100, editor.DisplayedBitmap.PixelHeight);
    });

    [Fact]
    public void SixPalettesUseEveryToken_AndHighContrastAlwaysUsesSystemColors() => StaTestHost.Run(() =>
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            foreach (bool systemHighContrast in new[] { false, true })
            {
                var brushes = CaptureGuidePalette.Create(theme, "#FF1234", systemHighContrast);
                Assert.Equal(14, brushes.Count);
                Assert.All(brushes.Values, brush => Assert.True(brush.IsFrozen));
                bool highContrast = systemHighContrast || theme == AppTheme.HighContrast;
                foreach (string role in new[] { "SelectionBorder", "Crosshair", "StartPoint", "ChipBorder", "MagnifierBorder" })
                    Assert.Equal(highContrast ? SystemColors.WindowTextColor : Color.FromRgb(255, 18, 52),
                        ((SolidColorBrush)brushes["CaptureGuide." + role]).Color);
                if (highContrast)
                {
                    Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)brushes["CaptureGuide.ChipSurface"]).Color);
                    Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)brushes["CaptureGuide.Text"]).Color);
                }
                else
                {
                    ThemeColor surface = ThemeCatalog.ColorsFor(theme)["Surface.Floating"];
                    Assert.Equal(Color.FromArgb(surface.A, surface.R, surface.G, surface.B),
                        ((SolidColorBrush)brushes["CaptureGuide.ChipSurface"]).Color);
                }
            }
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void SelectionBorderIsOnePhysicalPixel_WithGlowOnlyInside(double scale) => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame(), showMagnifier: false);
        try
        {
            Render(view, scale);
            // RenderTargetBitmap restores its caller's DPI after rendering. Re-enter the
            // simulated monitor DPI before inspecting/drawing its guide geometry.
            typeof(CaptureOverlayView).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [new DpiScale(1, 1), new DpiScale(scale, scale)]);
            typeof(CaptureOverlayView).GetMethod("RenderLayers", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            Pen border = Field<Pen>(view, "_selectionBorderPen");
            Assert.Equal(1, border.Thickness * scale, 6);
            Assert.Equal(new double[] { 3, 3 }, Field<Pen>(view, "_guideLinePen").DashStyle.Dashes);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
                typeof(CaptureOverlayView).GetMethod("DrawSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [dc, new RectD(30, 30, 80, 70)]);
            var bitmap = new RenderTargetBitmap(160, 120, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[160 * 120 * 4];
            bitmap.CopyPixels(pixels, 640, 0);
            Assert.Equal(0, pixels[(40 * 160 + 29) * 4 + 3]);
            Assert.Equal(255, pixels[(40 * 160 + 30) * 4 + 3]);
            // Normal palettes use a low-opacity glow; system high contrast uses a system brush.
            if (!SystemParameters.HighContrast && ThemeService.Current != AppTheme.HighContrast)
                Assert.InRange(pixels[(40 * 160 + 31) * 4 + 3], (byte)0, (byte)32);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void PointerBurstReusesPalettePensAndGuideGeometryWithoutDesktopRebuild() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame(), showMagnifier: false);
        PointD pointer = new(40, 40);
        view.ReadCursor = () => pointer;
        try
        {
            view.UpdatePointer(false, false);
            Render(view);
            // Finish initial offscreen layout feedback before measuring the pointer path.
            typeof(CaptureOverlayView).GetMethod("RenderLayers", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            var palette = view.GuidePalette;
            Pen line = Field<Pen>(view, "_guideLinePen");
            int desktop = view.StaticRenderCount, pens = view.GuidePenBuildCount, guides = view.GuideGeometryBuildCount;
            for (int i = 0; i < 200; i++)
            {
                pointer = new PointD(40 + i % 80, 40 + i % 60);
                view.UpdatePointer(false, false);
            }
            typeof(CaptureOverlayView).GetMethod("RenderLayers", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            Assert.Equal(desktop, view.StaticRenderCount);
            Assert.Equal(pens, view.GuidePenBuildCount);
            Assert.Equal(guides, view.GuideGeometryBuildCount);
            Assert.Same(palette, view.GuidePalette);
            Assert.Same(line, Field<Pen>(view, "_guideLinePen"));
        }
        finally { view.ReleaseResources(); }
    });

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static AnnotationEditorControl Editor(bool uniform = false, AnnotationDocument? document = null, int width = 1100)
    {
        FrozenFrame frame = Frame(uniform);
        var editor = new AnnotationEditorControl(frame, frame.ScreenBounds, frame.Bitmap, document, null);
        editor.Measure(new Size(width, 720));
        editor.Arrange(new Rect(0, 0, width, 720));
        editor.UpdateLayout();
        return editor;
    }

    private static FrozenFrame Frame(bool uniform = false)
    {
        byte[] pixels = new byte[160 * 120 * 4];
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 160; x++)
            {
                int i = (y * 160 + x) * 4;
                byte value = !uniform && x >= 32 && x < 128 && y >= 24 && y < 96 ? (byte)40 : (byte)255;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        BitmapSource bitmap = BitmapSource.Create(160, 120, 96, 96, PixelFormats.Bgra32, null, pixels, 640);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 160, 120), null, 0);
    }

    private static void Render(CaptureOverlayView view, double scale = 1)
    {
        if (Math.Abs(view.GuidePixelSizeX - 1 / scale) > 0.000001)
            typeof(CaptureOverlayView).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [new DpiScale(1 / view.GuidePixelSizeX, 1 / view.GuidePixelSizeY), new DpiScale(scale, scale)]);
        view.InvalidateVisual();
        view.Measure(new Size(160 / scale, 120 / scale));
        view.Arrange(new Rect(0, 0, 160 / scale, 120 / scale));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(160, 120, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RunOffscreenAsync(Func<Task> action) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            Task task = action();
            var timeout = Stopwatch.StartNew();
            while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15))
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            Assert.True(task.IsCompleted, "Offscreen trim did not finish.");
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });
}
