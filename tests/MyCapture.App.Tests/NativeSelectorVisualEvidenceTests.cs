using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in, offscreen comparison of the two real selector renderers on a synthetic desktop. Native
/// desktop pixels come from the selector's DIB and the bounded windows are rendered from their actual
/// content. Nothing reads the screen, moves the cursor, shows a window or uses a user profile.
/// </summary>
public sealed class NativeSelectorVisualEvidenceTests
{
    private const int Width = CaptureCueEvidenceTests.Width;
    private const int Height = CaptureCueEvidenceTests.Height;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void RenderPointerChromeAlongsideWpf(double scale) => StaTestHost.Run(() =>
    {
        // Opt-in: a new evidence directory below repository artifacts, named per scale.
        string? run = TestDiagnosticArtifacts.Create("MYCAPTURE_VISUAL_EVIDENCE_DIR", $"native-selector-{scale * 100:0}");
        if (run is null) return;
        using var language = UiText.UseLanguage("ko-KR");

        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        RectD bounds = frame.ScreenBounds;
        IReadOnlyList<MonitorInfo> monitors =
            [new("evidence", bounds, bounds, (uint)Math.Round(96 * scale), true)];
        PointD pointer = new(640, 360), nativePointer = pointer;
        var view = new CaptureOverlayView(frame, showMagnifier: true)
        {
            ReadCursor = () => pointer,
            PlaceCursor = target => { pointer = target; return true; },
            SimulatedPointerInside = true,
            ReadMonitors = () => monitors,
            ShowModeBar = true,
        };
        CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Resolve());
        using var owner = new NativeCaptureOverlay(bounds, false, true)
        {
            ReadCursor = () => nativePointer,
            PlaceCursor = target => { nativePointer = target; return true; },
            ReadMonitors = () => monitors,
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        try
        {
            view.InitializePointer();
            owner.PrepareVisualFeedback();
            owner.UpdatePointer(false, false);
            owner.FlushFeedbackForTest();
            chrome.Update(nativePointer, Int32Rect.Empty);
            SavePair(run, "item-01-dpi-pointer", RenderWpf(view, scale), RenderNative(owner, chrome, scale));

            // The same text and image state near the far edge also shows the physical bounds and
            // clipping at a scale boundary. All coordinates here are fixture pixels.
            pointer = nativePointer = new PointD(1160, 580);
            view.UpdatePointer(false, false);
            owner.UpdatePointer(false, false);
            owner.FlushFeedbackForTest();
            chrome.Update(nativePointer, Int32Rect.Empty);
            SavePair(run, "item-02-typography-edge", RenderWpf(view, scale), RenderNative(owner, chrome, scale));

            pointer = nativePointer = new PointD(640, 360);
            view.UpdatePointer(true, true);
            owner.UpdatePointer(true, true);
            pointer = pointer.Offset(10, 10);
            nativePointer = nativePointer.Offset(10, 10);
            PointD wpfEffective = view.UpdatePointer(true, true);
            PointD nativeEffective = owner.UpdatePointer(true, true);
            Assert.Equal(new PointD(642, 362), wpfEffective);
            Assert.Equal(wpfEffective, nativeEffective);
            owner.FlushFeedbackForTest();
            chrome.Update(owner.EffectivePointer, Int32Rect.Empty);
            BitmapSource wpfPrecision = RenderWpf(view, scale);
            BitmapSource nativePrecision = RenderNative(owner, chrome, scale);
            SavePair(run, "item-03-precision-active", wpfPrecision, nativePrecision);

            Rect wpfPanel = WindowDetectOverlayTests.Visual(view, "_magnifierVisual").ContentBounds;
            wpfPanel.Scale(scale, scale);
            RectD nativePanel = chrome.BoundsForTest(chrome.Windows.Last());
            SaveComparison(run, "item-03-magnifier-grid-footer",
                CropDetail(wpfPrecision, wpfPanel),
                CropDetail(nativePrecision, new Rect(nativePanel.Left, nativePanel.Top, nativePanel.Width, nativePanel.Height)));
        }
        finally { view.ReleaseResources(); }
        WriteCornerEvidence(run, scale);
        WriteAnchorEvidence(run, scale, new PointD(300, 250), new PointD(520, 430), "central");
        WriteAnchorEvidence(run, scale, new PointD(1279, 719), new PointD(1000, 500), "near-edge");
        WriteHoverEvidence(run, scale);
        WriteGridEvidence(run, scale);
        WriteAnchorEvidence(run, scale, new PointD(300, 250), new PointD(520, 430), "central",
            new System.Drawing.Size(320, 180), new RectD(300, 250, 320, 180));
        WriteAnchorEvidence(run, scale, new PointD(1279, 719), new PointD(1000, 500), "near-edge",
            new System.Drawing.Size(320, 180), new RectD(960, 540, 320, 180));
    });

    private static void WriteGridEvidence(string directory, double scale)
    {
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        RectD bounds = frame.ScreenBounds;
        int stride = frame.PixelWidth * 4;
        var original = new byte[stride * frame.PixelHeight];
        frame.Bitmap.CopyPixels(original, stride, 0);
        PointD pointer = new(360, 280);
        MonitorInfo[] monitors = [new("grid-evidence", bounds, bounds, (uint)Math.Round(96 * scale), true)];
        var view = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: true)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            SimulatedPointerInside = true,
            ReadMonitors = () => monitors,
            ShowModeBar = true,
        };
        CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Resolve());
        using var owner = new NativeCaptureOverlay(bounds, false, false, showSelectionGrid: true)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            ReadMonitors = () => monitors,
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, false, false);
        try
        {
            view.InitializePointer();
            owner.PrepareVisualFeedback();
            foreach ((PointD point, string name) in new[]
            {
                (new PointD(360, 280), "ready"),
                (new PointD(820, 460), "moved"),
            })
            {
                pointer = point;
                view.UpdatePointer(false, false);
                owner.UpdatePointer(false, false);
                owner.FlushFeedbackForTest();
                chrome.Update(pointer, Int32Rect.Empty);
                _ = CaptureCueEvidenceTests.Render(view, scale);
                view.FlushFeedbackForTest();
                BitmapSource wpf = CaptureCueEvidenceTests.Render(view, scale);
                BitmapSource native = NativeSelectorTestRenderer.Render(owner, chrome);
                Assert.True(view.GuideStripsArePixelsForTest);
                DrawingVisual horizontal = WindowDetectOverlayTests.Visual(view, "_horizontalGuideVisual");
                DrawingVisual vertical = WindowDetectOverlayTests.Visual(view, "_verticalGuideVisual");
                Assert.Equal(1, horizontal.Opacity);
                Assert.Equal(1, vertical.Opacity);
                Assert.Equal(0, horizontal.Offset.X);
                Assert.Equal(point.Y / scale, horizontal.Offset.Y, 6);
                Assert.Equal(point.X / scale, vertical.Offset.X, 6);
                Assert.Equal(0, vertical.Offset.Y);
                ImageDrawing across = Assert.Single(horizontal.Drawing.Children.OfType<ImageDrawing>());
                ImageDrawing down = Assert.Single(vertical.Drawing.Children.OfType<ImageDrawing>());
                Assert.Equal(Width, across.Rect.Width * scale, 6);
                Assert.Equal(Height, down.Rect.Height * scale, 6);
                Assert.Equal(new Int32Rect(0, (int)point.Y - 1, Width, 2), owner.HorizontalGuideBoundsForTest);
                Assert.Equal(new Int32Rect((int)point.X - 1, 0, 2, Height), owner.VerticalGuideBoundsForTest);
                SavePair(directory, "item-07-grid-" + name, wpf, native);
                var intersection = new Rect(point.X - 24 * scale, point.Y - 24 * scale, 48 * scale, 48 * scale);
                SaveComparison(directory, "item-07-grid-" + name + "-pointer",
                    CropDetail(wpf, intersection), CropDetail(native, intersection));
            }

            view.SimulatePointerDown();
            owner.BeginSelection(pointer);
            pointer = new PointD(980, 620);
            view.SimulatePointerMove();
            owner.UpdatePointer(false, false);
            owner.MoveSelection(pointer);
            owner.FlushFeedbackForTest();
            chrome.Update(pointer, owner.SelectionRectangle);
            view.FlushFeedbackForTest();
            BitmapSource wpfDrag = CaptureCueEvidenceTests.Render(view, scale);
            BitmapSource nativeDrag = NativeSelectorTestRenderer.Render(owner, chrome);
            Assert.Equal(0, WindowDetectOverlayTests.Visual(view, "_horizontalGuideVisual").Opacity);
            Assert.Equal(0, WindowDetectOverlayTests.Visual(view, "_verticalGuideVisual").Opacity);
            Assert.Null(owner.HorizontalGuideBoundsForTest);
            Assert.Null(owner.VerticalGuideBoundsForTest);
            SavePair(directory, "item-07-grid-drag", wpfDrag, nativeDrag);

            var after = new byte[original.Length];
            frame.Bitmap.CopyPixels(after, stride, 0);
            Assert.True(frame.Bitmap.IsFrozen);
            Assert.True(original.AsSpan().SequenceEqual(after), "Rendering pointer feedback changed the source frame pixels.");
        }
        finally { view.ReleaseResources(); }
    }

    private static void WriteHoverEvidence(string directory, double scale)
    {
        var notes = new WindowCandidate(new IntPtr(0x3301), new RectD(40, 30, 200, 150), "Notes");
        var chat = new WindowCandidate(new IntPtr(0x3302), new RectD(400, 40, 180, 140), "Chat");
        FrozenFrame frame = HoverFrame(notes.ScreenBounds, chat.ScreenBounds);
        RectD bounds = frame.ScreenBounds;
        PointD pointer = new(100, 100);
        TimeSpan now = TimeSpan.Zero;
        MonitorInfo[] monitors = [new("hover-evidence", bounds, bounds, (uint)Math.Round(96 * scale), true)];
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        var view = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: false)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            SimulatedPointerInside = true,
            ReadMonitors = () => monitors,
            ReadClock = () => now,
            DetectWindows = true,
            WindowCandidates = [notes, chat],
            ShowModeBar = true,
        };
        CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Resolve());
        using var owner = new NativeCaptureOverlay(bounds, false, false, showSelectionGrid: false)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            ReadMonitors = () => monitors,
            ReadClock = () => now,
            DetectWindows = true,
            SourceWindows = [notes, chat],
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, false, false);
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        try
        {
            owner.PreparePointerFeedback();
            owner.PrepareVisualFeedback();
            view.InitializePointer();
            owner.UpdatePointer(false, false);
            owner.RefreshHover();
            _ = CaptureCueEvidenceTests.Render(view, scale);
            view.FlushFeedbackForTest();
            _ = CaptureCueEvidenceTests.Render(view, scale);
            Assert.Equal(notes.ScreenBounds, view.ShownHoverForTest);
            Assert.Equal(view.ShownHoverForTest, owner.ShownHoverForTest);

            pointer = new PointD(490, 120);
            view.UpdatePointer(false, false);
            owner.UpdatePointer(false, false);
            owner.RefreshHover();
            foreach (int milliseconds in new[] { 0, 25, 50 })
            {
                now = TimeSpan.FromMilliseconds(milliseconds);
                view.FlushFeedbackForTest();
                BitmapSource wpf = CaptureCueEvidenceTests.Render(view, scale);
                owner.FlushFeedbackForTest();
                chrome.Update(pointer, owner.DisplayedRegionForTest);
                chrome.FlushHoverTabForTest();
                BitmapSource native = NativeSelectorTestRenderer.Render(owner, chrome);
                Assert.Equal(view.ShownHoverForTest, owner.ShownHoverForTest);
                Assert.Equal(chat.ScreenBounds, view.HoverRegionForTest);
                Assert.Equal(view.HoverRegionForTest, owner.HoverRegion);
                Assert.Equal(view.TargetLabelForTest, chrome.TargetLabelForTest);
                Assert.EndsWith("180 × 140", chrome.TargetLabelForTest!, StringComparison.Ordinal);
                if (milliseconds == 0) Assert.Equal(notes.ScreenBounds, owner.ShownHoverForTest);
                if (milliseconds == 25)
                {
                    Assert.NotEqual(notes.ScreenBounds, owner.ShownHoverForTest);
                    Assert.NotEqual(chat.ScreenBounds, owner.ShownHoverForTest);
                }
                if (milliseconds == 50) Assert.Equal(chat.ScreenBounds, owner.ShownHoverForTest);
                string name = milliseconds == 0 ? "initial" : $"{milliseconds}ms";
                SavePair(directory, "item-06-hover-" + name, wpf, native);
                Rect wpfTab = Assert.IsType<Rect>(view.TargetTabForTest);
                Rect nativeTab = Assert.IsType<Rect>(chrome.TargetTabForTest);
                wpfTab.Scale(scale, scale);
                nativeTab.Scale(scale, scale);
                SaveComparison(directory, "item-06-hover-" + name + "-tab",
                    CropDetail(wpf, wpfTab), CropDetail(native, nativeTab));
            }
        }
        finally
        {
            view.ReleaseResources();
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
        }
    }

    private static void WriteAnchorEvidence(string directory, double scale, PointD start, PointD end, string name,
        System.Drawing.Size? preset = null, RectD? expectedPreset = null)
    {
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        RectD bounds = frame.ScreenBounds;
        PointD pointer = start;
        MonitorInfo[] monitors = [new("anchor-evidence", bounds, bounds, (uint)Math.Round(96 * scale), true)];
        var view = new CaptureOverlayView(frame, showMagnifier: false)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            SimulatedPointerInside = true,
            ReadMonitors = () => monitors,
            ShowModeBar = true,
        };
        CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Resolve());
        using var owner = new NativeCaptureOverlay(bounds, false, false)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            ReadMonitors = () => monitors,
        };
        owner.AttachFrame(frame);
        if (preset.HasValue)
        {
            view.PresetSize = preset;
            owner.PresetSize = preset;
        }
        RectD? wpfCompleted = null, nativeCompleted = null;
        view.SelectionConfirmed += (_, e) => wpfCompleted = e.BitmapRegion;
        owner.SelectionCompleted += (_, e) => nativeCompleted = e.BitmapRegion;
        using var chrome = new NativeSelectorChrome(owner, false, false);
        try
        {
            view.InitializePointer();
            owner.PrepareVisualFeedback();
            owner.UpdatePointer(false, false);
            view.SimulatePointerDown();
            owner.BeginSelection(start);
            pointer = end;
            view.SimulatePointerMove();
            owner.UpdatePointer(false, false);
            owner.MoveSelection(end);
            owner.FlushFeedbackForTest();
            chrome.Update(end, owner.SelectionRectangle);
            _ = CaptureCueEvidenceTests.Render(view, scale);
            view.FlushFeedbackForTest();
            BitmapSource wpf = CaptureCueEvidenceTests.Render(view, scale);
            BitmapSource native = NativeSelectorTestRenderer.Render(owner, chrome);
            Assert.Equal(view.Selection!.Value.ToPixelBounds(), new RectD(owner.SelectionRectangle.X,
                owner.SelectionRectangle.Y, owner.SelectionRectangle.Width, owner.SelectionRectangle.Height));
            if (expectedPreset is { } expected) Assert.Equal(expected, view.Selection);
            string prefix = preset.HasValue ? "item-08-preset-" : "item-05-anchor-";
            SavePair(directory, prefix + name, wpf, native);

            Rect wpfAnchor = Assert.IsType<Rect>(view.AnchorChipBoundsForTest);
            Rect nativeAnchor = Assert.IsType<Rect>(chrome.AnchorChipBoundsForTest);
            wpfAnchor.Scale(scale, scale);
            nativeAnchor.Scale(chrome.AnchorScaleForTest, chrome.AnchorScaleForTest);
            SaveComparison(directory, prefix + name + "-chip",
                CropDetail(wpf, wpfAnchor), CropDetail(native, nativeAnchor));

            Rect wpfSize = Assert.IsType<Rect>(view.DeferredGuideReadout);
            wpfSize.Scale(scale, scale);
            RectD nativeSize = chrome.SizeChipBoundsForTest;
            Assert.True(chrome.IsReadoutVisibleForTest);
            SaveComparison(directory, prefix + name + "-readout",
                CropDetail(wpf, wpfSize),
                CropDetail(native, new Rect(nativeSize.Left, nativeSize.Top, nativeSize.Width, nativeSize.Height)));
            if (expectedPreset is { } completed)
            {
                // The fixture constructed chrome separately; release those owned HWNDs before the
                // native completion closes its owner. The capture still uses its actual drag state.
                chrome.Dispose();
                view.SimulatePointerUp();
                owner.EndSelection(end);
                WindowDetectOverlayTests.PumpUntil(() => wpfCompleted.HasValue && nativeCompleted.HasValue);
                Assert.Equal(completed, wpfCompleted);
                Assert.Equal(completed, nativeCompleted);
            }
        }
        finally { view.ReleaseResources(); }
    }

    private static void WriteCornerEvidence(string directory, double scale)
    {
        var pixels = new byte[Width * Height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 2] = 255;
            pixels[index + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32,
            null, pixels, Width * 4);
        bitmap.Freeze();
        RectD bounds = new(0, 0, Width, Height);
        var frame = new FrozenFrame(bitmap, bounds, null, 0);
        PointD pointer = new(640, 360);
        MonitorInfo[] monitors = [new("corner-evidence", bounds, bounds, (uint)Math.Round(96 * scale), true)];
        var view = new CaptureOverlayView(frame, showSelectionGrid: false)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            SimulatedPointerInside = true,
            ReadMonitors = () => monitors,
        };
        CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Resolve());
        using var owner = new NativeCaptureOverlay(bounds, false, true, showSelectionGrid: false)
        {
            ReadCursor = () => pointer,
            PlaceCursor = _ => false,
            ReadMonitors = () => monitors,
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        try
        {
            view.InitializePointer();
            owner.PrepareVisualFeedback();
            owner.UpdatePointer(false, false);
            owner.FlushFeedbackForTest();
            chrome.Update(pointer, Int32Rect.Empty);
            BitmapSource wpf = RenderWpf(view, scale);
            BitmapSource native = NativeSelectorTestRenderer.Render(owner, chrome);
            Rect wpfImage = view.MagnifierImageBoundsForTest!.Value;
            wpfImage.Scale(scale, scale);
            RectD nativeImage = chrome.MagnifierImageBoundsForTest;
            Assert.Equal(wpfImage.Width, nativeImage.Width, 6);
            Assert.Equal(wpfImage.Height, nativeImage.Height, 6);
            SaveComparison(directory, "item-04-magnifier-corners",
                CropDetail(wpf, wpfImage),
                CropDetail(native, new Rect(nativeImage.Left, nativeImage.Top, nativeImage.Width, nativeImage.Height)));
        }
        finally { view.ReleaseResources(); }
    }

    private static BitmapSource RenderWpf(CaptureOverlayView view, double scale)
    {
        // The first arrange settles DPI and queues dependent pointer feedback. An offscreen view
        // has no live mouse-over state or composition turn, so provide both explicitly.
        _ = CaptureCueEvidenceTests.Render(view, scale);
        view.FlushFeedbackForTest();
        BitmapSource rendered = CaptureCueEvidenceTests.Render(view, scale);
        Assert.NotNull(view.MagnifierBitmapForTest);
        Assert.NotNull(view.MagnifierImageBoundsForTest);
        Assert.False(WindowDetectOverlayTests.Visual(view, "_pointerVisual").ContentBounds.IsEmpty);
        return rendered;
    }

    private static FrozenFrame HoverFrame(RectD notes, RectD chat)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(47, 85, 122)), null, new Rect(0, 0, Width, Height));
            DrawWindow(notes, "Notes", Color.FromRgb(247, 248, 250), Color.FromRgb(228, 232, 238), Colors.Black);
            DrawWindow(chat, "Chat", Color.FromRgb(30, 36, 49), Color.FromRgb(44, 52, 69), Colors.White);

            void DrawWindow(RectD bounds, string title, Color body, Color header, Color text)
            {
                dc.DrawRectangle(new SolidColorBrush(body), null, new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
                dc.DrawRectangle(new SolidColorBrush(header), null, new Rect(bounds.Left, bounds.Top, bounds.Width, 25));
                dc.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), TypeScale.Caption, new SolidColorBrush(text), 1),
                    new Point(bounds.Left + 8, bounds.Top + 5));
                var rows = new SolidColorBrush(Color.FromArgb(70, text.R, text.G, text.B));
                for (int row = 0; row < 4; row++)
                    dc.DrawRectangle(rows, null, new Rect(bounds.Left + 12, bounds.Top + 42 + row * 20,
                        bounds.Width - 24 - row % 2 * 28, 6));
            }
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        source.Freeze();
        return new FrozenFrame(source, new RectD(0, 0, Width, Height), null, 0);
    }

    private static BitmapSource RenderNative(NativeCaptureOverlay owner, NativeSelectorChrome chrome, double scale) =>
        NativeSelectorTestRenderer.Render(owner, chrome);

    private static void SavePair(string directory, string name, BitmapSource wpf, BitmapSource native)
    {
        Assert.Equal(Width, wpf.PixelWidth);
        Assert.Equal(Height, wpf.PixelHeight);
        Assert.Equal(Width, native.PixelWidth);
        Assert.Equal(Height, native.PixelHeight);
        SaveComparison(directory, name, wpf, native);
    }

    private static BitmapSource CropDetail(BitmapSource scene, Rect bounds)
    {
        const int margin = 10;
        int left = Math.Clamp((int)Math.Floor(bounds.Left) - margin, 0, scene.PixelWidth - 1);
        int top = Math.Clamp((int)Math.Floor(bounds.Top) - margin, 0, scene.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right) + margin, left + 1, scene.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + margin, top + 1, scene.PixelHeight);
        return new CroppedBitmap(scene, new Int32Rect(left, top, right - left, bottom - top));
    }

    private static void SaveComparison(string directory, string name, BitmapSource wpf, BitmapSource native)
    {
        TestDiagnosticArtifacts.SavePng(directory, name + "-wpf.png", wpf);
        TestDiagnosticArtifacts.SavePng(directory, name + "-native.png", native);
        const int header = 38, gap = 12;
        int leftWidth = Math.Max(260, wpf.PixelWidth), rightWidth = Math.Max(360, native.PixelWidth);
        int height = Math.Max(wpf.PixelHeight, native.PixelHeight);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 27, 32)), null,
                new Rect(0, 0, leftWidth + rightWidth + gap, height + header));
            var typeface = new Typeface("Segoe UI");
            void Heading(string text, double x) => dc.DrawText(new FormattedText(text,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 16, Brushes.White, 1),
                new Point(x + 12, 9));
            Heading("WPF · current reference", 0);
            Heading("Native · actual DIB + bounded chrome", leftWidth + gap);
            dc.DrawImage(wpf, new Rect(0, header, wpf.PixelWidth, wpf.PixelHeight));
            dc.DrawImage(native, new Rect(leftWidth + gap, header, native.PixelWidth, native.PixelHeight));
        }
        var pair = new RenderTargetBitmap(leftWidth + rightWidth + gap, height + header, 96, 96, PixelFormats.Pbgra32);
        pair.Render(visual);
        TestDiagnosticArtifacts.SavePng(directory, name + "-side-by-side.png", pair);
    }
}
