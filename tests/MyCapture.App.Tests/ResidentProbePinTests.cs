using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentProbePinTests
{
    [Fact]
    public void FrameFenceWaitsForARealWpfRenderPass() => StaTestHost.Run(() =>
    {
        var content = PinContent.FromImage(ResidentProbeImages.CachedPattern(24, 24, 1));
        var pin = new PinWindow(content, new PinViewState(24, 24, 1, 1, .1), 0, 0) { ShowActivated = false };
        try
        {
            Task<ResidentProbePresentation.Frame> pending = ResidentProbePresentation.MeasureAsync(
                Dispatcher.CurrentDispatcher, pin.Show, () => pin.IsVisible, timeoutMs: 5000);
            var clock = Stopwatch.StartNew();
            while (!pending.IsCompleted && clock.ElapsedMilliseconds < 6000) RangePatternClip.Pump(TimeSpan.FromMilliseconds(10));
            Assert.True(pending.IsCompleted, "WPF frame fence did not complete");
            ResidentProbePresentation.Frame frame = pending.GetAwaiter().GetResult();
            Assert.Null(frame.Error);
            Assert.NotNull(frame.CompleteMs);
            Assert.True(frame.CompleteMs >= frame.RenderingMs);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("sw-pin", true, true)]
    [InlineData("sw-pin-aux,pin-scale-linear,pin-opaque", true, false)]
    [InlineData("sw-pin-idle,pin-scale-linear", false, true)]
    public void PinVariantsConfigureTheHwndBeforeShowAndKeepLinearAfterZoom(string variants, bool software, bool layered) => StaTestHost.Run(() =>
    {
        var options = new ResidentProbeOptions("unused", ["pin-1600"], true, false,
            ResidentProbeOptions.ReadVariants(["--resident-variant", variants]));
        using var recorder = new ResidentProbeRecorder(options, Dispatcher.CurrentDispatcher);
        using (var diagnostics = new ResidentProbePinDiagnostics(options, recorder))
        {
            var content = PinContent.FromImage(ResidentProbeImages.CachedPattern(24, 24, 1));
            var pin = new PinWindow(content, new PinViewState(24, 24, 1, 1, .1), 0, 0);
            try
            {
                Assert.Equal(layered, pin.AllowsTransparency);
                var hwnd = new WindowInteropHelper(pin).EnsureHandle();
                Assert.Equal(software ? RenderMode.SoftwareOnly : RenderMode.Default, HwndSource.FromHwnd(hwnd)!.CompositionTarget!.RenderMode);
                pin.ResidentProbeSetZoom(1.5);
                Assert.True(pin.ResidentProbeHasRasterCache);
                pin.ForceZoomSettleForTest();
                Assert.False(pin.ResidentProbeHasRasterCache);
                Assert.Equal(options.Has("pin-scale-linear") ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality,
                    RenderOptions.GetBitmapScalingMode(pin.ResidentProbeImage));
            }
            finally { pin.Close(); }
        }
        Assert.Null(ResidentProbeWindowHook.PinConstructed);
    });

    [Theory]
    [InlineData("sw-pin")]
    [InlineData("sw-pin-aux")]
    [InlineData("sw-pin-idle")]
    [InlineData("pin-scale-linear")]
    [InlineData("pin-opaque")]
    [InlineData("pin-150")]
    [InlineData("sw-pin,pin-scale-linear,pin-150")]
    [InlineData("sw-pin-aux,pin-opaque,pin-scale-linear")]
    [InlineData("pin-light")]
    [InlineData("pin-light,pin-150")]
    [InlineData("pin-light,sw-pin-aux")]
    [InlineData("pin-switch")]
    [InlineData("pin-switch,sw-pin-aux")]
    [InlineData("pin-zoom")]
    [InlineData("pin-zoom,pin-hold-whole")]
    [InlineData("pin-zoom,pin-move-live")]
    [InlineData("pin-zoom,pin-hold-whole,pin-move-live")]
    public void VariantsAreExplicitAndComposable(string text)
    {
        var selected = ResidentProbeOptions.ReadVariants(["--resident-variant", text.ToUpperInvariant()]);
        Assert.Equal(text.Split(',').Length, selected.Count);
        foreach (string variant in text.Split(',')) Assert.True(selected.Contains(variant));
        Assert.DoesNotContain(ResidentProbeOptions.AllScenarios, ResidentProbePinDiagnostics.IsScenario);
        Assert.Single(ResidentProbeOptions.AllScenarios, s => s == "pins-10");
    }

    [Theory]
    [InlineData("pin-light,pin-native")]
    [InlineData("pin-light,pin-opaque")]
    [InlineData("pin-light,pin-scale-linear")]
    [InlineData("pin-light,sw-pin")]
    [InlineData("pin-light,sw-pin-idle")]
    [InlineData("pin-switch,pin-light")]
    [InlineData("pin-switch,pin-native")]
    [InlineData("pin-switch,pin-150")]
    [InlineData("pin-zoom,pin-light")]
    [InlineData("pin-zoom,pin-switch")]
    [InlineData("pin-zoom,pin-native")]
    [InlineData("pin-zoom,pin-150")]
    [InlineData("pin-zoom,sw-pin")]
    [InlineData("pin-hold-whole")]
    [InlineData("pin-move-live,pin-light")]
    [InlineData("pin-plain")]
    public void LightweightVariantRejectsWpfRendererVariantsAndThePlainPrototypeIsGone(string text) =>
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ReadVariants(["--resident-variant", text]));

    [Theory]
    [InlineData(0x20000u, 0x04u, "private read/write")]
    [InlineData(0x20000u, 0x404u, "private write-combined")]
    [InlineData(0x40000u, 0x404u, "mapped/section")]
    [InlineData(0x1000000u, 0x20u, "image")]
    [InlineData(0x20000u, 0x20u, "private other")]
    public void RegionsUseTypeBeforeProtection(uint type, uint protect, string expected) =>
        Assert.Equal(expected, ResidentProbePinMemory.Classify(type, protect));

    [Fact]
    public void RegionDeltaPreservesExactSizesReleasesAndProtectionChanges()
    {
        var source = new ResidentProbePinMemory.Region("0x100000", 6_402_048, "private read/write", 0x20000, 4);
        var surface = source with { Base = "0x900000", Kind = "private write-combined", Protect = 0x404 };
        var before = new ResidentProbePinMemory.Map(source.Bytes, [source]);
        var open = new ResidentProbePinMemory.Map(source.Bytes + surface.Bytes, [source, surface]);
        var delta = ResidentProbePinMemory.Compare(before, open);
        Assert.Equal(surface, Assert.Single(delta.Added));
        Assert.Empty(delta.Removed);
        Assert.Equal(surface.Bytes, delta.Net.Single(x => x.Kind == surface.Kind).Bytes);
        var closed = ResidentProbePinMemory.Compare(open, before);
        Assert.Equal(surface, Assert.Single(closed.Removed));
        Assert.Equal(-1, closed.Net.Single(x => x.Kind == surface.Kind).Count);
        var changed = ResidentProbePinMemory.Compare(before, new(source.Bytes, [source with { Protect = 0x104 }]));
        Assert.Single(changed.Added);
        Assert.Single(changed.Removed);
        Assert.Equal(0, Assert.Single(changed.Net).Bytes);
    }

    [Fact]
    public void NativeCountersAndRegionsCanBeReadWithoutDesktopAccess()
    {
        using Process process = Process.GetCurrentProcess();
        Assert.True(ResidentProbePinMemory.ProcessCommit(process.Handle) > 0);
        var map = ResidentProbePinMemory.Take();
        Assert.True(map.CommittedBytes > 0);
        Assert.NotEmpty(map.Regions);
        Assert.All(map.Regions, region => Assert.True(region.Bytes >= 1_048_576));
        Assert.True(map.CommittedBytes >= map.Regions.Sum(r => r.Bytes));
    }

    [Fact]
    public void SyntheticPicturesHaveDistinctPixelsAndSingleImmutableSources() => StaTestHost.Run(() =>
    {
        var first = ResidentProbeImages.CachedPattern(24, 24, 17);
        var second = ResidentProbeImages.CachedPattern(24, 24, 34);
        byte[] a = new byte[24 * 24 * 4], b = new byte[a.Length];
        first.CopyPixels(a, 24 * 4, 0);
        second.CopyPixels(b, 24 * 4, 0);
        Assert.NotEqual(a, b);
        Assert.True(first.IsFrozen);
        Assert.Same(first, PinContent.FromImage(first).Display);
    });

    [Fact]
    public void TextFixturesUseTheProductRendererAndRemainDpiIndependent() => StaTestHost.Run(() =>
    {
        foreach (bool table in new[] { false, true })
        {
            string text = App.PinProbeText(3, table);
            var native = ClipboardTextRenderer.Render(text, table, 1);
            var scaled = ClipboardTextRenderer.Render(text, table, 1.5);
            Assert.InRange(native.Width, 700, 1200);
            Assert.InRange(native.Height, 500, 700);
            Assert.InRange(Math.Abs(native.Width - scaled.Width), 0, 1);
            Assert.InRange(Math.Abs(native.Height - scaled.Height), 0, 1);
            Assert.Equal(144, scaled.DpiX);
            Assert.NotEqual(text, App.PinProbeText(4, table));
        }
    });

    [Fact]
    public void OrdinaryPinDoesNotInstallDiagnosticsAndHookRunsBeforeHwnd() => StaTestHost.Run(() =>
    {
        Assert.Null(ResidentProbeWindowHook.PinConstructed);
        var content = PinContent.FromImage(ResidentProbeImages.CachedPattern(24, 24, 1));
        var ordinary = new PinWindow(content, new PinViewState(24, 24, 1, 1, .1), 0, 0);
        try
        {
            Assert.True(ordinary.AllowsTransparency);
            Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(ordinary.ResidentProbeImage));
            bool called = false;
            ResidentProbeWindowHook.PinConstructed = pin =>
            {
                Assert.Equal(IntPtr.Zero, pin.Handle);
                pin.AllowsTransparency = false;
                called = true;
            };
            var diagnostic = new PinWindow(content, new PinViewState(24, 24, .5, 1, .1), 0, 0);
            try
            {
                Assert.True(called);
                Assert.False(diagnostic.AllowsTransparency);
                diagnostic.ResidentProbeInitialSize();
                Assert.Equal(1, diagnostic.State.Zoom);
                Assert.Equal(24, diagnostic.Width);
            }
            finally { diagnostic.Close(); }
        }
        finally
        {
            ResidentProbeWindowHook.PinConstructed = null;
            ordinary.Close();
        }
    });
}
