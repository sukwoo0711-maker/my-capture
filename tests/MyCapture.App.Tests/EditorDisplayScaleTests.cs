using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class EditorDisplayScaleTests(ITestOutputHelper output) : KoreanCaptionTest
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(WindowStyle.SingleBorderWindow, ResizeMode.CanResize)]
    [InlineData(WindowStyle.SingleBorderWindow, ResizeMode.NoResize)]
    [InlineData(WindowStyle.SingleBorderWindow, ResizeMode.CanMinimize)]
    [InlineData(WindowStyle.SingleBorderWindow, ResizeMode.CanResizeWithGrip)]
    [InlineData(WindowStyle.ThreeDBorderWindow, ResizeMode.CanResize)]
    [InlineData(WindowStyle.ToolWindow, ResizeMode.CanResize)]
    [InlineData(WindowStyle.None, ResizeMode.NoResize)]
    [InlineData(WindowStyle.None, ResizeMode.CanResize)]
    public void PredictedNativeFrameMatchesHwndWithoutSourceResize(WindowStyle style, ResizeMode resize) => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = MonitorEnumerator.GetPrimary();
        Size frameBefore = default, sizeBefore = default;
        WithWindow(SyntheticCapture(900, 520), monitor, true, (window, _) =>
        {
            Assert.Equal(frameBefore, NativeFrame(window));
            Assert.Equal(sizeBefore, new Size(window.Width, window.Height));
            Assert.True(window.SourceSizingMeasurement.Skipped);
            Assert.Equal(0, window.SourceSizingMeasurement.Passes);
        }, beforeShow: window =>
        {
            window.WindowStyle = style;
            window.ResizeMode = resize;
            window.PlaceOnMonitor(monitor);
            // Keep native creation off screen after sizing for the real monitor's DPI.
            window.Left = -20000;
            window.Top = -20000;
            frameBefore = NativeFrame(window);
            sizeBefore = new Size(window.Width, window.Height);
        });
    });

    [Fact]
    public void ReportTenThousandImagePointConversions() => StaTestHost.Run(() =>
    {
        WithWindow(SyntheticCapture(1920, 1080), Monitor(3440, 1440, 1392), false, (_, surface) =>
        {
            double checksum = 0;
            for (int index = 0; index < 10000; index++) checksum += surface.ToImagePoint(new Point(256, 256)).X;
            var samples = new double[5];
            for (int sample = 0; sample < samples.Length; sample++)
            {
                long started = Stopwatch.GetTimestamp();
                for (int index = 0; index < 10000; index++) checksum += surface.ToImagePoint(new Point(256, 256)).X;
                samples[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            Assert.True(double.IsFinite(checksum) && checksum > 0);
            output.WriteLine($"ToImagePoint 10000 calls: median={samples.Order().ElementAt(2):F4} ms; samples={string.Join(", ", samples.Select(value => value.ToString("F4", CultureInfo.InvariantCulture)))} ms; checksum={checksum:R}");
        });
    });

    [Theory]
    [InlineData(900, 520)]
    [InlineData(1920, 1080)]
    public void ReportWindowSizingPassesAndTime(int width, int height)
    {
        // Each STA thread has its own dispatcher and so an empty chrome cache: its first open is
        // a first open in the process and its second a later one. Run -1 only warms the JIT.
        var first = new List<(AnnotationEditorWindow.SizingMeasurement Constructor, AnnotationEditorWindow.SizingMeasurement Source, TimeSpan Open)>();
        var later = new List<(AnnotationEditorWindow.SizingMeasurement Constructor, AnnotationEditorWindow.SizingMeasurement Source, TimeSpan Open)>();
        for (int run = -1; run < 5; run++)
        {
            bool record = run >= 0;
            StaTestHost.Run(() =>
            {
                BitmapSource bitmap = SyntheticCapture(width, height);
                ResourceDictionary resources = ThemedResources();
                for (int open = 0; open < 2; open++)
                {
                    TimeSpan elapsed = default;
                    WithWindow(bitmap, Monitor(3440, 1440, 1392), false, (window, _) =>
                    {
                        if (open == 0)
                        {
                            Assert.Equal(0, window.ConstructorSizingMeasurement.Passes);
                            Assert.True(window.ConstructorSizingMeasurement.Skipped);
                            Assert.InRange(window.SourceSizingMeasurement.Passes, 1, 8);
                        }
                        else
                        {
                            Assert.Equal(0, window.ConstructorSizingMeasurement.Passes);
                            Assert.Equal(0, window.SourceSizingMeasurement.Passes);
                        }
                        if (record) (open == 0 ? first : later).Add((window.ConstructorSizingMeasurement, window.SourceSizingMeasurement, elapsed));
                    }, centerOnTarget: true, resources: resources, opened: value => elapsed = value);
                }
            });
        }
        void Report(string stage, IEnumerable<AnnotationEditorWindow.SizingMeasurement> values)
        {
            var samples = values.ToList();
            output.WriteLine($"SizeForMonitor {width}x{height} {stage}: median={samples.Select(value => value.Elapsed.TotalMilliseconds).Order().ElementAt(2):F4} ms; passes={string.Join(",", samples.Select(value => value.Passes))}; skipped={string.Join(",", samples.Select(value => value.Skipped))}; samples={string.Join(", ", samples.Select(value => value.Elapsed.TotalMilliseconds.ToString("F4", CultureInfo.InvariantCulture)))} ms");
        }
        foreach ((string open, var samples) in new[] { ("first", first), ("later", later) })
        {
            Report($"{open} constructor", samples.Select(value => value.Constructor));
            Report($"{open} SourceInitialized", samples.Select(value => value.Source));
            output.WriteLine($"SizeForMonitor {width}x{height} {open} combined median={samples.Select(value => (value.Constructor.Elapsed + value.Source.Elapsed).TotalMilliseconds).Order().ElementAt(2):F4} ms");
            output.WriteLine($"Editor open {width}x{height} {open} (construct to laid out after Show): median={samples.Select(value => value.Open.TotalMilliseconds).Order().ElementAt(2):F4} ms; samples={string.Join(", ", samples.Select(value => value.Open.TotalMilliseconds.ToString("F4", CultureInfo.InvariantCulture)))} ms");
        }
    }

    [Theory]
    [InlineData(900, 520)]
    [InlineData(1920, 1080)]
    public void SecondOpenOfTheSameCaptureSizeTakesNoSizingPass(int width, int height) => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        ResourceDictionary resources = ThemedResources();
        BitmapSource bitmap = SyntheticCapture(width, height);
        Rect firstBounds = default;
        WithWindow(bitmap, monitor, false, (window, _) =>
        {
            Assert.Equal(0, window.ConstructorSizingMeasurement.Passes);
            Assert.True(window.ConstructorSizingMeasurement.Skipped);
            Assert.InRange(window.SourceSizingMeasurement.Passes, 1, 8);
            firstBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
        }, centerOnTarget: true, resources: resources, beforeShow: window =>
        {
            Assert.Equal(0, window.Editor.InspectorBudgetMeasureCount);
            Assert.True(window.ConstructorSizingMeasurement.Skipped);
        });
        WithWindow(bitmap, monitor, false, (window, surface) =>
        {
            Assert.Equal(0, window.ConstructorSizingMeasurement.Passes);
            Assert.Equal(0, window.SourceSizingMeasurement.Passes);
            Assert.Equal(firstBounds, new Rect(window.Left, window.Top, window.Width, window.Height));
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
        }, centerOnTarget: true, resources: resources);
        // The gallery's retention banner is other content and never reuses the plain chrome.
        WithWindow(bitmap, monitor, true, (window, surface) =>
        {
            Assert.IsType<GalleryEditorWindow>(window);
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
        }, gallery: true, resources: resources);
    });

    [Fact]
    public void SourceInitializationKeepsTheClientSizeWhenOnlyTheNativeFrameDiffers() => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        Size estimatedFrame = default, client = default;
        WithWindow(SyntheticCapture(900, 520), monitor, true, (window, surface) =>
        {
            Size frame = NativeFrame(window);
            Assert.NotEqual(estimatedFrame, frame);
            Assert.False(window.SourceSizingMeasurement.Skipped);
            Assert.Equal(0, window.SourceSizingMeasurement.Passes);
            var content = (FrameworkElement)window.Content;
            Assert.Equal(client, new Size(content.ActualWidth, content.ActualHeight));
            Assert.Equal(new Size(client.Width + frame.Width, client.Height + frame.Height), new Size(window.Width, window.Height));
            Assert.Equal(monitor.WorkArea.Left + Math.Floor((monitor.WorkArea.Width - window.Width) / 2), window.Left);
            Assert.Equal(monitor.WorkArea.Top + Math.Floor((monitor.WorkArea.Height - window.Height) / 2), window.Top);
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
        }, beforeShow: window =>
        {
            // Both ordinary resize modes can have the same DWM frame. Change the border
            // style after sizing so the fallback has a genuinely different native frame.
            estimatedFrame = NativeFrame(window);
            client = new Size(window.Width - estimatedFrame.Width, window.Height - estimatedFrame.Height);
            window.WindowStyle = WindowStyle.ThreeDBorderWindow;
        });
    });

    [Fact]
    public void SourceInitializationMeasuresAgainAfterADpiChange() => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        ResourceDictionary resources = ThemedResources();
        BitmapSource bitmap = SyntheticCapture(900, 520);
        DpiScale? previous = AnnotationEditorWindow.SizingDpiOverrideForTest;
        try
        {
            // The first open measures these inputs, so the second differs by its DPI alone.
            WithWindow(bitmap, monitor, true, (_, _) => { }, resources: resources);
            WithWindow(bitmap, monitor, true, (window, _) =>
            {
                Assert.Equal(0, window.ConstructorSizingMeasurement.Passes);
                Assert.False(window.SourceSizingMeasurement.Skipped);
                Assert.InRange(window.SourceSizingMeasurement.Passes, 1, 8);
            }, resources: resources, beforeShow: _ => AnnotationEditorWindow.SizingDpiOverrideForTest = new DpiScale(1.25, 1.25));
        }
        finally { AnnotationEditorWindow.SizingDpiOverrideForTest = previous; }
    });

    [Fact]
    public void ThemeAndCultureChangesMeasureTheChromeAgain() => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        ResourceDictionary resources = ThemedResources();
        BitmapSource bitmap = SyntheticCapture(900, 520);
        void Open(Action<AnnotationEditorWindow> assert) =>
            WithWindow(bitmap, monitor, false, (window, _) => assert(window), centerOnTarget: true, resources: resources);
        void AssertMeasured(AnnotationEditorWindow window)
        {
            Assert.Equal(0, window.ConstructorSizingMeasurement.Passes);
            Assert.True(window.ConstructorSizingMeasurement.Skipped);
            Assert.InRange(window.SourceSizingMeasurement.Passes, 1, 8);
        }
        Open(AssertMeasured);
        Open(window => Assert.Equal(0, window.ConstructorSizingMeasurement.Passes + window.SourceSizingMeasurement.Passes));
        ThemeService.ApplyResources(AppTheme.HighContrast, resources, systemHighContrast: false);
        Open(AssertMeasured);
        using (UiText.UseLanguage("en-US")) Open(AssertMeasured);
    });

    [Fact]
    public void RepeatedSizingReusesUnchangedMeasurement() => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = SyntheticCapture(900, 520);
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        var region = new RectD(0, 0, 900, 520);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, monitor, 0), region, bitmap, pointerMonitor: monitor);
        try
        {
            // Explicit placement still resolves the cold budget before moving the window.
            window.PlaceOnMonitor(monitor);
            Size original = new(window.Width, window.Height);
            var repeated = (AnnotationEditorWindow.SizingMeasurement)typeof(AnnotationEditorWindow)
                .GetMethod("SizeForMonitor", PrivateInstance)!.Invoke(window, [monitor, false, true, null, true])!;
            Assert.True(repeated.Skipped);
            Assert.Equal(0, repeated.Passes);
            Assert.Equal(original, new Size(window.Width, window.Height));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SourceInitializationReusesUnchangedNativeChrome() => StaTestHost.Run(() =>
    {
        // Borderless chrome is zero both before and after HWND creation. PlaceOnMonitor
        // measures the loaded theme/content first, leaving no new sizing inputs at Show.
        WithWindow(SyntheticCapture(900, 520), Monitor(3440, 1440, 1392), true, (window, surface) =>
        {
            Assert.True(window.SourceSizingMeasurement.Skipped);
            Assert.Equal(0, window.SourceSizingMeasurement.Passes);
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
        }, borderless: true);
    });

    [Fact]
    public void CachedFrameLayoutRefreshesForMoveResizeFrameAndDpiChanges() => StaTestHost.Run(() =>
    {
        WithWindow(SyntheticCapture(1920, 1080), Monitor(3440, 1440, 1392), false, (window, surface) =>
        {
            void AssertCurrent(double dpi)
            {
                Point offset = Field<Point>(surface, "_deviceOffset");
                var expected = AnnotationEditorSurface.CalculateFrameLayout(surface.RenderSize,
                    new Size(surface.Frame.PixelWidth, surface.Frame.PixelHeight), dpi, offset);
                Assert.Equal(expected.Scale, surface.DipPerPixel);
                Assert.Equal(expected.Origin, surface.FrameOrigin);
                PointD imagePoint = surface.ToImagePoint(surface.ToSurfacePoint(new PointD(31, 29)));
                Assert.Equal(31, imagePoint.X, 8);
                Assert.Equal(29, imagePoint.Y, 8);
                Assert.True(Field<bool>(surface, "_frameLayoutValid"));
            }
            AssertCurrent(1);
            double fullSizeScale = surface.DipPerPixel;
            Point oldScreenOrigin = surface.PointToScreen(surface.FrameOrigin);
            window.Left += 128;
            window.Top += 64;
            Settle(window);
            AssertCurrent(1);
            Assert.Equal(oldScreenOrigin + new Vector(128, 64), surface.PointToScreen(surface.FrameOrigin));

            window.Width -= 160;
            window.Height -= 100;
            Settle(window);
            AssertCurrent(1);
            Assert.True(surface.DipPerPixel < fullSizeScale);

            // Move the content host while preserving its size: layout offsets can change
            // without a RenderSizeChanged notification on the drawing surface.
            AnnotationEditorControl editor = window.Editor;
            Size priorSurfaceSize = surface.RenderSize;
            editor.Width = editor.ActualWidth;
            editor.Height = editor.ActualHeight;
            editor.UseLayoutRounding = false;
            window.Content = null;
            var host = new Canvas { UseLayoutRounding = false };
            host.Children.Add(editor);
            window.Content = host;
            Settle(window);
            Point beforeOffset = Field<Point>(surface, "_deviceOffset");
            Point beforeOrigin = surface.FrameOrigin;
            Canvas.SetLeft(editor, 7.25);
            Canvas.SetTop(editor, 9.25);
            Settle(window);
            Assert.Equal(priorSurfaceSize, surface.RenderSize);
            Assert.NotEqual(beforeOffset, Field<Point>(surface, "_deviceOffset"));
            Assert.NotEqual(beforeOrigin, surface.FrameOrigin);
            AssertCurrent(1);

            BitmapSource replacement = SyntheticCapture(240, 90);
            var region = new RectD(0, 0, 240, 90);
            surface.ReplaceFrame(new FrozenFrame(replacement, region, null, 0), region);
            Assert.False(Field<bool>(surface, "_frameLayoutValid"));
            AssertCurrent(1);
            Assert.Equal(1, surface.DipPerPixel);

            // Exercise the actual DPI notification path without moving a real monitor.
            MethodInfo dpiChanged = typeof(AnnotationEditorSurface).GetMethod("OnDpiChanged", PrivateInstance)!;
            dpiChanged.Invoke(surface, [new DpiScale(1, 1), new DpiScale(1.5, 1.5)]);
            Assert.False(Field<bool>(surface, "_frameLayoutValid"));
            AssertCurrent(1.5);
            Assert.Equal(1 / 1.5, surface.DipPerPixel);
            dpiChanged.Invoke(surface, [new DpiScale(1.5, 1.5), new DpiScale(1, 1)]);
            AssertCurrent(1);
        });
    });

    [Theory]
    [InlineData(900, 520)]
    [InlineData(1920, 1080)]
    public void StandaloneFirstFrameStaysCenteredAfterSizeCorrection(int width, int height) => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        WithWindow(SyntheticCapture(width, height), monitor, false, (window, _) =>
        {
            Assert.Equal(monitor.WorkArea.Left + Math.Floor((monitor.WorkArea.Width - window.Width) / 2), window.Left);
            Assert.Equal(monitor.WorkArea.Top + Math.Floor((monitor.WorkArea.Height - window.Height) / 2), window.Top);
        }, centerOnTarget: true);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void LayoutCapsPhysicalPixelsAndSnapsRelativeToSurface(double dpi)
    {
        foreach (Point offset in new[] { new Point(), new Point(0.25, -0.375) })
        {
            var viewport = new Size(1001, 701);
            var pixels = new Size(600, 400);
            var layout = AnnotationEditorSurface.CalculateFrameLayout(viewport, pixels, dpi, offset);
            Assert.Equal(1 / dpi, layout.Scale);
            AssertDeviceAligned(layout.Origin, dpi, offset);
            AssertInsets(new Rect(layout.Origin, new Size(pixels.Width * layout.Scale, pixels.Height * layout.Scale)), viewport);

            foreach (Size large in new[] { new Size(3000, 800), new Size(800, 3000) })
            {
                layout = AnnotationEditorSurface.CalculateFrameLayout(viewport, large, dpi, offset);
                Assert.InRange(layout.Scale, double.Epsilon, Math.BitDecrement(1 / dpi));
                AssertDeviceAligned(layout.Origin, dpi, offset);
                AssertInsets(new Rect(layout.Origin, new Size(large.Width * layout.Scale, large.Height * layout.Scale)), viewport);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void UnknownDpiUsesOne(double dpi)
    {
        var layout = AnnotationEditorSurface.CalculateFrameLayout(new Size(800, 600), new Size(240, 90), dpi);
        Assert.Equal(1, layout.Scale);
        AssertDeviceAligned(layout.Origin, 1, default);
    }

    [Fact]
    public void FractionalOffsetKeepsTheClampedOriginOnTheDeviceGrid()
    {
        var offset = new Point(0.6, 0.2);
        var layout = AnnotationEditorSurface.CalculateFrameLayout(new Size(100, 100), new Size(100, 101), 1.25, offset);
        AssertDeviceAligned(layout.Origin, 1.25, offset);
        AssertInsets(new Rect(layout.Origin, new Size(100 * layout.Scale, 101 * layout.Scale)), new Size(100, 100));
    }

    [Theory]
    [InlineData(3440, 1440, 1392, 240, 90, true)]
    [InlineData(3440, 1440, 1392, 480, 300, true)]
    [InlineData(3440, 1440, 1392, 900, 520, true)]
    [InlineData(3440, 1440, 1392, 1280, 720, true)]
    [InlineData(3440, 1440, 1392, 1920, 1080, true)]
    [InlineData(3440, 1440, 1392, 3440, 1440, false)]
    [InlineData(1920, 1080, 1032, 1280, 720, true)]
    [InlineData(1920, 1080, 1032, 1920, 1080, false)]
    [InlineData(1920, 1080, 1032, 2560, 1440, false)]
    public void RealWindowFitsCaptureAtOneHundredPercentWhenPossible(
        int monitorWidth, int monitorHeight, int workHeight, int width, int height, bool fits) => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(monitorWidth, monitorHeight, workHeight);
        WithWindow(SyntheticCapture(width, height), monitor, placeOnMonitor: false, (window, surface) =>
        {
            Assert.Equal(1, VisualTreeHelper.GetDpi(surface).PixelsPerDip);
            if (fits) Assert.Equal(1, surface.DipPerPixel);
            else
            {
                Assert.InRange(surface.DipPerPixel, double.Epsilon, Math.BitDecrement(1.0));
                Assert.True(window.Width <= monitor.WorkArea.Width - 64);
                Assert.True(window.Height <= monitor.WorkArea.Height - 64);
            }
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
            Point surfaceOrigin = surface.TranslatePoint(default, (UIElement)window.Content);
            AssertDeviceAligned(surface.FrameOrigin, 1, surfaceOrigin);
            if (width <= 480)
            {
                Assert.Equal(980, window.Width);
                // Narrow windows reserve more than one row of property controls.
                var inspector = (FrameworkElement)typeof(AnnotationEditorControl)
                    .GetField("_inspectorPanel", PrivateInstance)!.GetValue(window.Editor)!;
                Assert.True(inspector.ActualHeight > 60, $"Property bar height: {inspector.ActualHeight}");
            }
        });
    });

    [Theory]
    [InlineData(1920, 1080, 1032, 600, 400, false)]
    [InlineData(2560, 1600, 1552, 1200, 800, false)]
    public void TargetMonitorDpiBudgetsImageAndMeasuredChrome(
        int monitorWidth, int monitorHeight, int workHeight, int width, int height, bool placeOnMonitor) => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(monitorWidth, monitorHeight, workHeight, 144);
        WithWindow(SyntheticCapture(width, height), monitor, placeOnMonitor, (window, surface) =>
        {
            Assert.True(surface.ActualWidth >= width / 1.5 + 32,
                $"Surface width {surface.ActualWidth} cannot fit {width / 1.5 + 32} DIP");
            Assert.True(surface.ActualHeight >= height / 1.5 + 32,
                $"Surface height {surface.ActualHeight} cannot fit {height / 1.5 + 32} DIP");
            Assert.InRange(window.Width, window.MinWidth, monitor.WorkArea.Width / 1.5 - 64);
            Assert.InRange(window.Height, window.MinHeight, monitor.WorkArea.Height / 1.5 - 64);
        });
    });

    [Theory]
    [InlineData(1920, 1080, 1032, 600, 400)]
    [InlineData(2560, 1600, 1552, 1200, 800)]
    public void PlacementBudgetsTargetDpiBeforeMovingTheNativeWindow(
        int monitorWidth, int monitorHeight, int workHeight, int width, int height) => StaTestHost.Run(() =>
    {
        // A synthetic monitor cannot change the HWND DPI. Inspect the target-DIP layout
        // before CenterOn applies physical bounds on this machine's real 96-DPI monitor.
        WithWindow(SyntheticCapture(width, height), Monitor(monitorWidth, monitorHeight, workHeight, 144), true,
            (window, surface) =>
            {
                Assert.True(surface.ActualWidth >= width / 1.5 + 32, $"Surface {surface.RenderSize} in window {window.Width}x{window.Height}");
                Assert.True(surface.ActualHeight >= height / 1.5 + 32, $"Surface {surface.RenderSize} in window {window.Width}x{window.Height}");
                Assert.InRange(window.Width, 980, monitorWidth / 1.5 - 64);
                Assert.InRange(window.Height, 620, workHeight / 1.5 - 64);
            }, show: false);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GalleryBannerIsIncludedInTheMeasuredChrome(bool placeOnMonitor) => StaTestHost.Run(() =>
    {
        WithWindow(SyntheticCapture(900, 520), Monitor(3440, 1440, 1392), placeOnMonitor, (window, surface) =>
        {
            Assert.IsType<GalleryEditorWindow>(window);
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
        }, gallery: true, beforeShow: window =>
        {
            if (!placeOnMonitor)
            {
                Assert.Equal(0, window.Editor.InspectorBudgetMeasureCount);
                Assert.True(window.ConstructorSizingMeasurement.Skipped);
            }
        });
    });

    [Fact]
    public void ExplicitCallerWindowSizeSurvivesSourceInitialization() => StaTestHost.Run(() =>
    {
        WithWindow(SyntheticCapture(240, 90), Monitor(3440, 1440, 1392), false,
            (window, _) => Assert.Equal(new Size(1100, 700), new Size(window.Width, window.Height)),
            explicitSize: new Size(1100, 700));
    });

    [Fact]
    public void NativeTrackingLimitStillClampsAnExplicitCallerSize() => StaTestHost.Run(() =>
    {
        WithWindow(SyntheticCapture(240, 90), Monitor(3440, 1440, 1392), false,
            (window, _) => Assert.Equal(new Size(1044, 700), new Size(window.Width, window.Height)),
            explicitSize: new Size(1100, 700), nativeMaximum: WindowTrackSizeScope.SmallDesktopMaximum);
    });

    [Theory]
    [InlineData(3440, 1440, 1392, 1280, 720)]
    [InlineData(1920, 1080, 1032, 1280, 720)]
    [InlineData(3440, 1440, 1392, 1920, 1080)]
    public void NativeTrackingLimitFitsTheImageToTheActualClient(
        int monitorWidth, int monitorHeight, int workHeight, int width, int height) => StaTestHost.Run(() =>
    {
        WithWindow(SyntheticCapture(width, height), Monitor(monitorWidth, monitorHeight, workHeight), false,
            (window, surface) =>
            {
                Assert.Equal(1044, window.Width);
                Assert.True(window.Height <= 788);
                Assert.True(GetClientRect(new WindowInteropHelper(window).Handle, out NativeRect client));
                DpiScale dpi = VisualTreeHelper.GetDpi(window);
                var content = (FrameworkElement)window.Content;
                Assert.Equal((client.Right - client.Left) / dpi.DpiScaleX, content.ActualWidth);
                Assert.Equal((client.Bottom - client.Top) / dpi.DpiScaleY, content.ActualHeight);
                double expected = Math.Min(1 / dpi.DpiScaleX,
                    Math.Min((surface.ActualWidth - 32) / width, (surface.ActualHeight - 32) / height));
                Assert.Equal(expected, surface.DipPerPixel, 10);
                Assert.Equal(876.0 / width, surface.DipPerPixel);
                AssertInsets(surface.FrameRectDip, surface.RenderSize);
                output.WriteLine($"native limit 1044x788: requested monitor={monitorWidth}x{monitorHeight}; " +
                    $"capture={width}x{height}; window={window.Width}x{window.Height}; " +
                    $"client={content.RenderSize}; surface={surface.RenderSize}; image={surface.FrameRectDip}; scale={surface.DipPerPixel:R}");
            }, nativeMaximum: WindowTrackSizeScope.SmallDesktopMaximum);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GalleryAndNormalEditorsKeepTheirChromeWhenOpenedInEitherOrder(bool galleryFirst, bool prewarm) => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        BitmapSource bitmap = SyntheticCapture(900, 520);
        ResourceDictionary resources = ThemedResources();
        if (prewarm)
        {
            var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            var host = AnnotationEditorWindow.CreatePrewarmHost(new FrozenFrame(bitmap, region, monitor, 0),
                region, bitmap, [monitor], provenance: null);
            try
            {
                // Seed this same themed environment without an HWND, as startup prewarm does.
                host.Resources = resources;
                host.PlaceOnMonitor(monitor);
            }
            finally { host.Close(); }
        }
        foreach (bool gallery in new[] { galleryFirst, !galleryFirst, galleryFirst })
            WithWindow(bitmap, monitor, false, (window, surface) =>
            {
                ReportSizing(window, surface, gallery ? "gallery" : "normal");
                Assert.Equal(1, surface.DipPerPixel);
                AssertInsets(surface.FrameRectDip, surface.RenderSize);
                if (gallery)
                {
                    DockPanel root = Assert.IsType<DockPanel>(window.Content);
                    Border banner = Assert.IsType<Border>(root.Children[0]);
                    AssertBannerInsideWindow(window, banner);
                }
            }, gallery: gallery, resources: resources);
    });

    [Fact]
    public void SameWrapperTypeWithDifferentBannerHeightsDoesNotReuseOtherWindowsChrome() => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        BitmapSource bitmap = SyntheticCapture(900, 520);
        ResourceDictionary resources = ThemedResources();
        foreach (double height in new[] { 24.0, 160.0, 24.0 })
        {
            Border? banner = null;
            WithWindow(bitmap, monitor, false, (window, surface) =>
            {
                ReportSizing(window, surface, $"banner-{height}");
                Assert.Equal(1, surface.DipPerPixel);
                AssertInsets(surface.FrameRectDip, surface.RenderSize);
                AssertBannerInsideWindow(window, banner!);
            }, resources: resources, beforeShow: window => banner = AddHostBanner(window, height));
        }
    });

    [Fact]
    public void BannerChangedAfterPlacementIsMeasuredBeforeFirstRender() => StaTestHost.Run(() =>
    {
        MonitorInfo monitor = Monitor(3440, 1440, 1392);
        Border? banner = null;
        WithWindow(SyntheticCapture(900, 520), monitor, false, (window, surface) =>
        {
            ReportSizing(window, surface, "changed-banner");
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
            AssertBannerInsideWindow(window, banner!);
        }, beforeShow: window =>
        {
            banner = AddHostBanner(window, 24);
            window.PlaceOnMonitor(monitor);
            window.Left = -20000;
            window.Top = -20000;
            banner.Height = 160;
            // A caller can lay out its revised host before Show. Valid layout alone must
            // not make the previously measured chrome (with the shorter banner) current.
            var root = (FrameworkElement)window.Content;
            Size native = NativeFrame(window);
            var client = new Size(window.Width - native.Width, window.Height - native.Height);
            root.Measure(client);
            root.Arrange(new Rect(client));
            Assert.True(root.IsMeasureValid && root.IsArrangeValid);
        });
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedBannerInvalidatesChromeCachedAtOtherClientSizes(bool layoutBeforeSizing) => StaTestHost.Run(() =>
    {
        MonitorInfo wide = Monitor(3440, 1440, 1392);
        MonitorInfo narrow = Monitor(1000, 1440, 1392);
        Border? banner = null;
        WithWindow(SyntheticCapture(900, 520), wide, false, (window, surface) =>
        {
            Assert.Equal(1, surface.DipPerPixel);
            window.PlaceOnMonitor(narrow);
            Settle(window);

            banner!.Height = 160;
            if (layoutBeforeSizing)
            {
                window.UpdateLayout();
                Assert.True(((FrameworkElement)window.Content).IsMeasureValid);
            }
            window.PlaceOnMonitor(narrow);
            Settle(window);
            AssertBannerInsideWindow(window, banner);

            // The same host previously cached its smaller banner at the wide client size.
            // Correcting the narrow size must invalidate that older wide entry as well.
            window.PlaceOnMonitor(wide);
            Settle(window);
            ReportSizing(window, surface, "changed-banner-return-to-wide");
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
            AssertBannerInsideWindow(window, banner);
        }, beforeShow: window => banner = AddHostBanner(window, 24));
    });

    private static Border AddHostBanner(AnnotationEditorWindow window, double height)
    {
        window.Content = null;
        var banner = new Border { Height = height, Child = new TextBlock { Text = "Capture notice" } };
        var root = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        root.Children.Add(banner);
        root.Children.Add(window.Editor);
        window.Content = root;
        return banner;
    }

    private static void AssertBannerInsideWindow(AnnotationEditorWindow window, FrameworkElement banner)
    {
        var content = (FrameworkElement)window.Content;
        Point origin = banner.TranslatePoint(default, content);
        Assert.True(banner.ActualHeight > 0);
        Assert.True(new Rect(content.RenderSize).Contains(new Rect(origin, banner.RenderSize)),
            $"Banner {new Rect(origin, banner.RenderSize)} is outside client {content.RenderSize}");
        Assert.True(content.ActualWidth <= window.ActualWidth && content.ActualHeight <= window.ActualHeight);
    }

    private void ReportSizing(AnnotationEditorWindow window, AnnotationEditorSurface surface, string stage) =>
        output.WriteLine($"{stage}: scale={surface.DipPerPixel:R}; window={window.Width:R}x{window.Height:R}; " +
            $"automatic={typeof(AnnotationEditorWindow).GetField("_lastAutomaticSize", PrivateInstance)!.GetValue(window)}; " +
            $"constructor={window.ConstructorSizingMeasurement}; source={window.SourceSizingMeasurement}");

    [Fact]
    public void OneHundredPercentPreservesEveryPixelIncludingOuterBorderAndCropScale() => StaTestHost.Run(() =>
    {
        BitmapSource source = SyntheticCapture(900, 520);
        WithWindow(source, Monitor(3440, 1440, 1392), placeOnMonitor: false, (window, surface) =>
        {
            Assert.Equal(1, surface.DipPerPixel);
            var content = (FrameworkElement)window.Content;
            Point origin = surface.TranslatePoint(surface.FrameOrigin, content);
            AssertDeviceAligned(origin, 1, default);
            var render = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            render.Render(content);
            int stride = source.PixelWidth * 4;
            byte[] expected = new byte[stride * source.PixelHeight];
            byte[] actual = new byte[expected.Length];
            source.CopyPixels(expected, stride, 0);
            render.CopyPixels(new Int32Rect((int)Math.Round(origin.X), (int)Math.Round(origin.Y), 900, 520), actual, stride, 0);
            int differentPixels = 0;
            var differences = new List<string>();
            for (int i = 0; i < expected.Length; i += 4)
                if (!expected.AsSpan(i, 4).SequenceEqual(actual.AsSpan(i, 4)))
                {
                    differentPixels++;
                    if (differences.Count < 12)
                        differences.Add($"({i / 4 % 900},{i / stride}): {Convert.ToHexString(expected.AsSpan(i, 4))}->{Convert.ToHexString(actual.AsSpan(i, 4))}");
                }
            Assert.True(differentPixels == 0, $"Differing pixels: {differentPixels}; {string.Join("; ", differences)}");

            window.Editor.ApplyCrop(new RectD(225, 130, 450, 260));
            Settle(window);
            Assert.Equal(450, surface.Frame.PixelWidth);
            Assert.Equal(260, surface.Frame.PixelHeight);
            Assert.Equal(1, surface.DipPerPixel);
            AssertInsets(surface.FrameRectDip, surface.RenderSize);
        });
    });

    [Theory]
    [InlineData(900, 520)]
    [InlineData(3440, 1440)]
    public void CropHandlesAndOuterGrabBandsRemainInsideSurface(int width, int height) => StaTestHost.Run(() =>
    {
        WithWindow(SyntheticCapture(width, height), Monitor(3440, 1440, 1392), false, (window, surface) =>
        {
            typeof(AnnotationEditorControl).GetMethod("SelectTool", PrivateInstance)!.Invoke(window.Editor, [EditorTool.Crop]);
            Settle(window);
            CropSelection crop = Assert.IsType<CropSelection>(surface.PendingCrop);
            Rect image = surface.ToSurfaceRect(crop.Region);
            Rect viewport = new(default(Point), surface.RenderSize);
            Point[] handles = ((IEnumerable<Point>)typeof(AnnotationEditorSurface).GetMethod("HandleCenters",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [image])!).ToArray();
            Assert.Equal(8, handles.Length);
            foreach (Point center in handles)
                Assert.True(viewport.Contains(new Rect(center.X - 4.5, center.Y - 4.5, 9, 9)));
            Rect outerBand = image;
            outerBand.Inflate(7, 7);
            Assert.True(viewport.Contains(outerBand));
            foreach ((Point point, CropGrip grip) in new[]
            {
                (new Point(image.Left - 6.5, image.Top + image.Height / 2), CropGrip.Left),
                (new Point(image.Right + 6.5, image.Top + image.Height / 2), CropGrip.Right),
                (new Point(image.Left + image.Width / 2, image.Top - 6.5), CropGrip.Top),
                (new Point(image.Left + image.Width / 2, image.Bottom + 6.5), CropGrip.Bottom),
            })
            {
                Assert.Equal(grip, crop.HitTest(surface.ToImagePoint(point), 7 / surface.DipPerPixel));
                Point viewportPoint = surface.TranslatePoint(point, window.Editor.PointerInputElement);
                window.Editor.SimulatePointerDown(viewportPoint);
                Assert.Equal(grip, crop.Grip);
                window.Editor.SimulatePointerUp(viewportPoint);
            }
        });
    });

    private static void WithWindow(BitmapSource bitmap, MonitorInfo monitor, bool placeOnMonitor,
        Action<AnnotationEditorWindow, AnnotationEditorSurface> assert, bool gallery = false, Size? explicitSize = null,
        bool show = true, bool centerOnTarget = false, bool borderless = false, ResourceDictionary? resources = null,
        Action<AnnotationEditorWindow>? beforeShow = null, Action<TimeSpan>? opened = null, Size? nativeMaximum = null)
    {
        bool? animations = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        long started = Stopwatch.GetTimestamp();
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var frame = new FrozenFrame(bitmap, region, monitor, 0);
        var record = new CaptureRecord { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, MediaKind = CaptureMediaKind.Image };
        AnnotationEditorWindow window = gallery
            ? new GalleryEditorWindow(new GalleryReeditContext(record, frame, region, bitmap,
                AnnotationDocument.CreateFor(bitmap.PixelWidth, bitmap.PixelHeight), new Dictionary<string, BitmapSource>()), record: record,
                pointerMonitor: monitor)
            : new AnnotationEditorWindow(frame, region, bitmap,
                pointerMonitor: placeOnMonitor ? Monitor(3440, 1440, 1392) : monitor);
        // Avoid the product's foreground/focus behavior while exercising its actual layout.
        var rendered = (EventHandler)typeof(AnnotationEditorWindow).GetMethod("OnContentRendered", PrivateInstance, [typeof(object), typeof(EventArgs)])!
            .CreateDelegate(typeof(EventHandler), window);
        window.ContentRendered -= rendered;
        try
        {
            // Like the application's shared resources, a caller can reuse one themed dictionary.
            window.Resources = resources ?? ThemedResources();
            if (borderless)
            {
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
            }
            if (placeOnMonitor) window.PlaceOnMonitor(monitor);
            if (explicitSize is { } size) { window.Width = size.Width; window.Height = size.Height; }
            if (!centerOnTarget) window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            beforeShow?.Invoke(window);
            using var tracking = new WindowTrackSizeScope(window,
                nativeMaximum.HasValue ? default : new Size(monitor.Bounds.Width, monitor.Bounds.Height), nativeMaximum);
            if (show)
            {
                window.Show();
                // Construction, sizing and the first layout at the final size, before rendering.
                if (opened is not null)
                {
                    window.UpdateLayout();
                    opened(Stopwatch.GetElapsedTime(started));
                }
                // IsVisibleChanged precedes HWND creation in WPF. Show returns after the
                // SourceInitialized correction and before the first rendered frame.
                Rect firstVisibleBounds = new(window.Left, window.Top, window.Width, window.Height);
                Settle(window);
                Assert.Equal(firstVisibleBounds, new Rect(window.Left, window.Top, window.Width, window.Height));
            }
            var surface = (AnnotationEditorSurface)typeof(AnnotationEditorControl).GetField("_surface", PrivateInstance)!
                .GetValue(window.Editor)!;
            assert(window, surface);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = animations;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static MonitorInfo Monitor(int width, int height, int workHeight, uint dpi = 96) =>
        new("DISPLAY-TEST", new RectD(-20000, -20000, width, height), new RectD(-20000, -20000, width, workHeight), dpi, true);

    private static ResourceDictionary ThemedResources()
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        return resources;
    }

    private static Size NativeFrame(AnnotationEditorWindow window) =>
        (Size)typeof(AnnotationEditorWindow).GetMethod("MeasureNativeChrome", PrivateInstance)!.Invoke(window, null)!;

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;

    private static void AssertDeviceAligned(Point origin, double dpi, Point offset)
    {
        Assert.Equal(Math.Round(origin.X * dpi + offset.X), origin.X * dpi + offset.X, 8);
        Assert.Equal(Math.Round(origin.Y * dpi + offset.Y), origin.Y * dpi + offset.Y, 8);
    }

    private static void AssertInsets(Rect image, Size surface)
    {
        const double tolerance = 0.0000001;
        Assert.True(image.Left >= 16 - tolerance && image.Top >= 16 - tolerance,
            $"Image origin {image.TopLeft} is inside the inset");
        Assert.True(surface.Width - image.Right >= 16 - tolerance && surface.Height - image.Bottom >= 16 - tolerance,
            $"Image {image} does not leave the inset in surface {surface}");
    }

    private static BitmapSource SyntheticCapture(int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int x = 20; x < width - 1; x += 40)
                context.DrawRectangle(Brushes.Gray, null, new Rect(x, 1, 1, height - 2));
            for (int y = 25; y < height - 1; y += 30)
                context.DrawRectangle(Brushes.Gray, null, new Rect(1, y, width - 2, 1));
            for (int y = 6; y + 16 < height; y += 30)
                context.DrawText(new FormattedText("Display 100% / 12 px text", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Consolas"), 12, Brushes.Black, 1), new Point(6, y));
            context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, width, 1));
            context.DrawRectangle(Brushes.Red, null, new Rect(0, height - 1, width, 1));
            context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 1, height));
            context.DrawRectangle(Brushes.Red, null, new Rect(width - 1, 0, 1, height));
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        // Physical screen captures are opaque. WPF's text rasterizer can leave alpha
        // 254 even over white; keep those RGB samples and make this fixture opaque so
        // the comparison measures image sampling, not compositing onto the editor.
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        target.CopyPixels(pixels, stride, 0);
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
