using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The blur tool end to end in the editor model, renderer and chrome. Offscreen WPF only: no
/// desktop capture, global hotkeys or input injection.
/// </summary>
public sealed class EditorBlurAppTests : KoreanCaptionTest
{
    private const int Width = 200;
    private const int Height = 120;

    // ---- Editing model ---------------------------------------------------------------

    [Fact]
    public void DragCreatesASelectedBlurLayerThatUndoesAndRedoesAsOneStep()
    {
        AnnotationEditorController controller = Controller(out AnnotationDocument document, out UndoStack undo);
        controller.Tool = EditorTool.Blur;
        controller.ApplyBlurRadius(30);

        controller.PointerDown(new PointD(10, 10));
        controller.PointerMove(new PointD(40, 30));
        controller.PointerUp(new PointD(60, 40));

        BlurAnnotation blur = Assert.IsType<BlurAnnotation>(Assert.Single(document.Items));
        Assert.Equal(new RectD(10, 10, 50, 30), blur.Rect);
        Assert.Equal(30, blur.Radius);
        Assert.Same(blur, controller.Selected);
        Assert.Equal(EditorTool.Blur, controller.Tool);
        Assert.Equal(1, undo.UndoCount);

        Assert.True(controller.PerformUndo());
        Assert.Empty(document.Items);
        Assert.Null(controller.Selected);
        Assert.True(controller.PerformRedo());
        Assert.Same(blur, Assert.Single(document.Items));

        // A click without a drag leaves nothing behind.
        controller.PointerDown(new PointD(100, 100));
        controller.PointerUp(new PointD(100.5, 100.5));
        Assert.Single(document.Items);
    }

    [Fact]
    public void ExistingBlurMovesResizesRestylesAndDeletesWithUndo()
    {
        AnnotationEditorController controller = Controller(out AnnotationDocument document, out _);
        var blur = new BlurAnnotation { Rect = new RectD(10, 10, 50, 30), Radius = 12 };
        document.Add(blur);
        controller.Tool = EditorTool.Select;

        controller.PointerDown(new PointD(30, 20));
        Assert.Same(blur, controller.Selected);
        controller.PointerMove(new PointD(50, 40));
        controller.PointerUp(new PointD(50, 40));
        Assert.Equal(new RectD(30, 30, 50, 30), blur.Rect);

        // Bottom-right handle.
        controller.PointerDown(new PointD(80, 60));
        controller.PointerMove(new PointD(100, 70));
        controller.PointerUp(new PointD(100, 70));
        Assert.Equal(new RectD(30, 30, 70, 40), blur.Rect);

        controller.ApplyBlurRadius(40.4);
        Assert.Equal(40, blur.Radius);
        Assert.Equal(40, controller.BlurRadius);

        Assert.True(controller.PerformUndo());
        Assert.Equal(12, blur.Radius);
        Assert.Equal(new RectD(30, 30, 70, 40), blur.Rect);

        // Back-to-back drags of one layer may merge inside the undo stack's merge window, so
        // the geometry needs one or two steps to return to where it started.
        Assert.True(controller.PerformUndo());
        if (blur.Rect != new RectD(10, 10, 50, 30))
        {
            Assert.Equal(new RectD(30, 30, 50, 30), blur.Rect);
            Assert.True(controller.PerformUndo());
        }

        Assert.Equal(new RectD(10, 10, 50, 30), blur.Rect);
        Assert.False(controller.CanUndo);

        controller.SetSelected(blur);
        controller.DeleteSelected();
        Assert.Empty(document.Items);
        Assert.True(controller.PerformUndo());
        Assert.Same(blur, Assert.Single(document.Items));
    }

    // ---- Rendering -------------------------------------------------------------------

    [Fact]
    public void ExportBlursExactlyTheCoveredPixelsAndLivePreviewShowsTheSameBytes() => StaTestHost.Run(() =>
    {
        BitmapSource source = TextLikeBitmap(Width, Height);
        AnnotationDocument document = AnnotationDocument.CreateFor(Width, Height);
        var blur = new BlurAnnotation { Rect = new RectD(40.4, 30, 80, 40.2), Radius = 8 };
        document.Add(blur);
        RectD region = blur.PixelRegionWithin(Width, Height);
        Assert.Equal(new RectD(40, 30, 81, 41), region);

        byte[] original = Pixels(source);
        byte[] exported = Pixels(AnnotationFlattener.Flatten(source, document, new AnnotationRenderer(new AnnotationImageStore())));
        byte[] expected = GaussianBlurKernel.BlurRegion(original, Width, Height, Width * 4, 40, 30, 81, 41, 8);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                bool inside = x >= 40 && x < 121 && y >= 30 && y < 71;
                ReadOnlySpan<byte> want = inside
                    ? expected.AsSpan(((((y - 30) * 81) + (x - 40)) * 4), 4)
                    : original.AsSpan(i, 4);
                Assert.True(want.SequenceEqual(exported.AsSpan(i, 4)), $"Pixel ({x},{y}) differs from the expected export.");
            }
        }

        // The live surface path: an opaque cover until the background frame lands, then the
        // exact export bytes.
        var cache = new AnnotationBlurCache(Dispatcher.CurrentDispatcher);
        int ready = 0;
        cache.FrameReady += (_, _) => ready++;
        var live = new AnnotationRenderer(new AnnotationImageStore(), cache);

        byte[] pending = Pixels(RenderLive(live, source, document));
        int center = ((50 * Width) + 80) * 4;
        Assert.Equal(new byte[] { 0x80, 0x80, 0x80, 0xFF }, pending.AsSpan(center, 4).ToArray());

        Settle(cache);
        Assert.Equal(1, ready);
        Assert.Equal(exported, Pixels(RenderLive(live, source, document)));

        // Moving the layer only changes the clip: no new frame is computed.
        blur.Translate(30, 10);
        _ = RenderLive(live, source, document);
        Settle(cache);
        Assert.Equal(1, ready);
        Assert.Equal(
            Pixels(AnnotationFlattener.Flatten(source, document, new AnnotationRenderer(new AnnotationImageStore()))),
            Pixels(RenderLive(live, source, document)));
    });

    [Fact]
    public void LiveCacheShowsTheNearestStrengthWhileANewOneComputesAndDropsUnusedFrames() => StaTestHost.Run(() =>
    {
        BitmapSource source = TextLikeBitmap(Width, Height);
        var cache = new AnnotationBlurCache(Dispatcher.CurrentDispatcher);

        cache.BeginPass(source);
        Assert.Null(cache.Frame(8));
        cache.EndPass();
        Settle(cache);

        cache.BeginPass(source);
        BitmapSource strength8 = Assert.IsAssignableFrom<BitmapSource>(cache.Frame(8));
        cache.EndPass();

        // A slider step to 9: the 8 frame keeps covering while 9 is computed.
        cache.BeginPass(source);
        Assert.Same(strength8, cache.Frame(9));
        cache.EndPass();
        Settle(cache);

        cache.BeginPass(source);
        BitmapSource strength9 = Assert.IsAssignableFrom<BitmapSource>(cache.Frame(9));
        Assert.NotSame(strength8, strength9);
        cache.EndPass();

        // The 8 frame was not used in the last pass and is gone; a new base drops everything.
        cache.BeginPass(source);
        Assert.Same(strength9, cache.Frame(8));
        cache.EndPass();
        cache.Clear();
        cache.BeginPass(TextLikeBitmap(Width, Height));
        Assert.Null(cache.Frame(9));
        cache.EndPass();
        cache.Clear();
        Assert.True(cache.Pending.Wait(TimeSpan.FromSeconds(30)));
    });

    [Fact]
    public void RendererWithoutABaseCoversInsteadOfRevealing() => StaTestHost.Run(() =>
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(40, 30);
        document.Add(new BlurAnnotation { Rect = new RectD(5, 5, 20, 10) });
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 40, 30));
            new AnnotationRenderer(new AnnotationImageStore()).Render(dc, document, 1.0);
        }

        var target = new RenderTargetBitmap(40, 30, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        byte[] pixels = Pixels(target);
        Assert.Equal(new byte[] { 0x80, 0x80, 0x80, 0xFF }, pixels.AsSpan(((10 * 40) + 10) * 4, 4).ToArray());
    });

    [Fact]
    public void LargeCaptureBlursOffTheUiThreadWithinAFrameBudgetForPreview() => StaTestHost.Run(() =>
    {
        // A 4K base: the UI thread only queues work and keeps drawing the cover.
        BitmapSource source = TextLikeBitmap(3840, 2160);
        var cache = new AnnotationBlurCache(Dispatcher.CurrentDispatcher);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        cache.BeginPass(source);
        Assert.Null(cache.Frame(BlurAnnotation.DefaultRadius));
        cache.EndPass();
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 50, $"Queuing a 4K blur blocked the UI thread for {watch.ElapsedMilliseconds} ms.");

        Settle(cache);
        cache.BeginPass(source);
        BitmapSource frame = Assert.IsAssignableFrom<BitmapSource>(cache.Frame(BlurAnnotation.DefaultRadius));
        cache.EndPass();
        Assert.Equal((3840, 2160), (frame.PixelWidth, frame.PixelHeight));
        Assert.True(frame.IsFrozen);
    });

    // ---- Editor chrome and workflow ---------------------------------------------------

    [Fact]
    public void ShortcutBRailButtonAndInspectorExposeTheBlurTool() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        var controller = Field<AnnotationEditorController>(editor, "_controller");

        Assert.Equal("Blur", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.B, ModifierKeys.None));
        Assert.True(editor.HandleShortcut(Key.B, ModifierKeys.None));
        Assert.Equal(EditorTool.Blur, controller.Tool);

        ToggleButton button = Descendants(editor).OfType<ToggleButton>()
            .Single(candidate => AutomationProperties.GetName(candidate) == "블러 도구 (B)");
        Assert.True(button.IsChecked);
        Assert.Equal("블러 (B)", button.ToolTip);
        Assert.Equal("B", AutomationProperties.GetAcceleratorKey(button));

        Assert.Equal(Visibility.Visible, Field<FrameworkElement>(editor, "_blurSection").Visibility);
        Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(editor, "_colorSection").Visibility);
        Slider slider = Field<Slider>(editor, "_blurSlider");
        Assert.Equal("블러 강도", AutomationProperties.GetName(slider));
        Assert.Equal(BlurAnnotation.DefaultRadius, slider.Value);

        // Edge drags keep drawing blurs instead of starting an edge crop.
        Assert.Null(editor.EdgeCropGripAt(new PointD(0, 50)));

        editor.ShortcutActions["Rectangle"]();
        Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(editor, "_blurSection").Visibility);
    });

    [Fact]
    public void InspectorStrengthAppliesToTheSelectionAndIsRememberedForTheNextEditor() => StaTestHost.Run(() =>
    {
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? write = AnnotationEditorPreferences.Write;
        var saved = new AnnotationDefaults { LastTool = "Blur", BlurRadius = 20 };
        try
        {
            AnnotationEditorPreferences.Read = () => saved;
            AnnotationEditorPreferences.Write = value => saved = value;

            AnnotationEditorControl editor = Editor();
            var controller = Field<AnnotationEditorController>(editor, "_controller");
            Assert.Equal(EditorTool.Blur, controller.Tool);
            Assert.Equal(20, controller.BlurRadius);

            controller.PointerDown(new PointD(20, 20));
            controller.PointerUp(new PointD(90, 50));
            BlurAnnotation blur = Assert.IsType<BlurAnnotation>(controller.Selected);
            Assert.Equal(20, blur.Radius);

            Field<Slider>(editor, "_blurSlider").Value = 33;
            Assert.Equal(33, blur.Radius);
            Assert.Equal(33, saved.BlurRadius);
            Assert.Equal("Blur", saved.LastTool);
            editor.ShortcutActions["Undo"]();
            Assert.Equal(20, blur.Radius);

            AnnotationEditorControl next = Editor();
            Assert.Equal(33, Field<AnnotationEditorController>(next, "_controller").BlurRadius);
        }
        finally
        {
            AnnotationEditorPreferences.Read = read;
            AnnotationEditorPreferences.Write = write;
        }
    });

    [Fact]
    public void LibraryReeditRestoresABlurLayerThatCanBeSelectedMovedAndCommitted() => StaTestHost.Run(() =>
    {
        AnnotationDocument saved = AnnotationDocument.CreateFor(Width, Height);
        saved.Add(new RectangleAnnotation { Rect = new RectD(5, 5, 20, 20) });
        saved.Add(new BlurAnnotation { Rect = new RectD(60, 40, 50, 30), Radius = 22 });
        AnnotationDocument restored = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(saved.ToJson()));

        AnnotationEditorControl editor = Editor(restored);
        var controller = Field<AnnotationEditorController>(editor, "_controller");
        editor.ShortcutActions["Select"]();
        controller.PointerDown(new PointD(80, 50));
        BlurAnnotation blur = Assert.IsType<BlurAnnotation>(controller.Selected);
        Assert.Equal(22, blur.Radius);
        controller.PointerMove(new PointD(90, 60));
        controller.PointerUp(new PointD(90, 60));

        AnnotationDocument snapshot = editor.CreateCurrentImageSnapshot();
        BlurAnnotation committed = Assert.IsType<BlurAnnotation>(snapshot.Items[1]);
        Assert.Equal(new RectD(70, 50, 50, 30), committed.Rect);
        Assert.Equal(22, committed.Radius);
        Assert.Contains("\"kind\":\"blur\"", snapshot.ToJson(), StringComparison.Ordinal);

        editor.ShortcutActions["Undo"]();
        Assert.Equal(new RectD(60, 40, 50, 30), blur.Rect);
        editor.ShortcutActions["Delete"]();
        Assert.Single(controller.Document.Items);
    });

    [Fact]
    public void RotatingTheCaptureKeepsTheBlurOverTheSameContent() => StaTestHost.Run(() =>
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(Width, Height);
        var blur = new BlurAnnotation { Rect = new RectD(10, 20, 30, 40) };
        document.Add(blur);
        AnnotationEditorControl editor = Editor(document);

        editor.ShortcutActions["RotateRight"]();
        Assert.Equal(new RectD(Height - 60, 10, 40, 30), blur.Rect);
        Assert.Equal((Height, Width), (editor.DisplayedBitmap.PixelWidth, editor.DisplayedBitmap.PixelHeight));
        editor.ShortcutActions["Undo"]();
        Assert.Equal(new RectD(10, 20, 30, 40), blur.Rect);
    });

    [Fact]
    public void ShortcutHelpListsTheBlurKeyInTheAnnotationTab() => StaTestHost.Run(() =>
    {
        Assert.Contains(ShortcutCatalog.All, entry => entry.Scope == ShortcutScope.Annotation
            && entry.Command == "Blur" && entry.Key == Key.B && entry.Modifiers == ModifierKeys.None && entry.Hint == "B");

        var help = new ShortcutHelpWindow();
        try
        {
            TabControl tabs = ((DockPanel)help.Content).Children.OfType<TabControl>().Single();
            TabItem annotation = tabs.Items.OfType<TabItem>().Single(tab => (string)tab.Header == "이미지 편집기");
            var rows = (StackPanel)((ScrollViewer)annotation.Content).Content;
            UniformGrid row = rows.Children.OfType<UniformGrid>()
                .Single(candidate => ShortcutHelpRowAssertions.Title(candidate) == "블러");
            Assert.Equal("B", ShortcutHelpRowAssertions.Keys(row));
        }
        finally
        {
            help.Close();
        }
    });

    // ---- Themes ----------------------------------------------------------------------

    [Fact]
    public void BlurRailIconStaysVisibleInEveryThemeAndState() => StaTestHost.Run(() =>
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            ResourceDictionary resources = LoadMergedTheme();
            ThemeService.ApplyResources(theme, resources);
            Assert.IsAssignableFrom<Geometry>(resources["Icon.Blur"]);

            foreach (bool isChecked in new[] { false, true })
            {
                var glyph = new Path
                {
                    Data = (Geometry)resources["Icon.Blur"],
                    StrokeThickness = 1.6,
                    Fill = Brushes.Transparent,
                    Stretch = Stretch.None,
                };
                var button = new ToggleButton
                {
                    Content = new Canvas { Width = 20, Height = 20, Children = { glyph } },
                    IsChecked = isChecked,
                };
                button.SetResourceReference(FrameworkElement.StyleProperty, "Rail.ToolButton");
                glyph.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
                var rail = new Border { Resources = resources, Child = button, Width = 64, Height = 64 };
                rail.SetResourceReference(FrameworkElement.StyleProperty, "Rail.Panel");

                rail.Measure(new Size(64, 64));
                rail.Arrange(new Rect(0, 0, 64, 64));
                rail.UpdateLayout();

                // 4x so the 1.6px stroke has solid interior pixels to measure.
                var target = new RenderTargetBitmap(256, 256, 384, 384, PixelFormats.Pbgra32);
                target.Render(rail);
                byte[] pixels = Pixels(target);

                // The 40px button sits at 12..52 DIP and centres the 20px glyph at 22..42.
                // Its own fill is sampled left of the glyph, clear of the corner rounding and
                // the checked indicator.
                Color background = PixelAt(pixels, 256, 16 * 4, 32 * 4);
                double best = 1;
                for (int y = 22 * 4; y < 42 * 4; y++)
                {
                    for (int x = 22 * 4; x < 42 * 4; x++)
                    {
                        best = Math.Max(best, Contrast(PixelAt(pixels, 256, x, y), background));
                    }
                }

                Assert.True(best >= 3.0, $"{theme} {(isChecked ? "checked" : "idle")}: blur icon contrast {best:0.00} is below 3:1.");
            }
        }
    });

    // ---- Helpers ---------------------------------------------------------------------

    private static AnnotationEditorController Controller(out AnnotationDocument document, out UndoStack undo)
    {
        document = AnnotationDocument.CreateFor(Width, Height);
        undo = new UndoStack();
        return new AnnotationEditorController(document, undo);
    }

    private static AnnotationEditorControl Editor(AnnotationDocument? document = null)
    {
        BitmapSource bitmap = TextLikeBitmap(Width, Height);
        var region = new RectD(0, 0, Width, Height);
        var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap, document, null);
        editor.Measure(new Size(1100, 720));
        editor.Arrange(new Rect(0, 0, 1100, 720));
        editor.UpdateLayout();
        return editor;
    }

    /// <summary>Dark one-pixel strokes on white, a stand-in for small text.</summary>
    private static BitmapSource TextLikeBitmap(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                byte value = x % 3 == 0 || y % 7 == 0 ? (byte)0x20 : (byte)0xF0;
                pixels[i] = value;
                pixels[i + 1] = (byte)(value ^ (x & 0x0F));
                pixels[i + 2] = (byte)(value ^ (y & 0x0F));
                pixels[i + 3] = 0xFF;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource RenderLive(AnnotationRenderer renderer, BitmapSource source, AnnotationDocument document)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            renderer.Render(dc, document, 1.0, blurSource: source);
        }

        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Settle(AnnotationBlurCache cache)
    {
        Assert.True(cache.Pending.Wait(TimeSpan.FromSeconds(30)), "The background blur did not finish.");
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        BitmapSource converted = bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static Color PixelAt(byte[] pixels, int width, int x, int y)
    {
        int i = ((y * width) + x) * 4;
        return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            double c = value / 255d;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static ResourceDictionary LoadMergedTheme()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var merged = new ResourceDictionary();
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml", UriKind.Absolute),
            });
        }

        return merged;
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
