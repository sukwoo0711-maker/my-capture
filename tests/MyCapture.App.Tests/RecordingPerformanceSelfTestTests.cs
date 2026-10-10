using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingPerformanceSelfTestTests
{
    private static readonly Color Fixture = RecordingPerformanceSelfTest.FixtureColor;

    [Fact]
    public void Fixture_KeepsItsColourWhenTheThemedImplicitWindowStyleIsInScope() => StaTestHost.Run(() =>
    {
        // Mirrors Controls.xaml: the implicit Window style turns on ModernWindowChrome, which
        // replaces Background with Surface.Base once the HWND exists.
        var themed = new ResourceDictionary
        {
            [typeof(Window)] = new Style(typeof(Window))
            {
                Setters = { new Setter(ModernWindowChrome.EnabledProperty, true) },
            },
            ["Surface.Base"] = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)),
        };
        var plain = new Window { Background = new SolidColorBrush(Fixture) };
        plain.Resources.MergedDictionaries.Add(themed);
        Window fixture = RecordingPerformanceSelfTest.CreateFixture();
        fixture.Resources.MergedDictionaries.Add(themed);
        try
        {
            new WindowInteropHelper(plain).EnsureHandle();
            new WindowInteropHelper(fixture).EnsureHandle();

            // Control: a plain Window really loses the fixture colour to the themed chrome.
            Assert.True(ModernWindowChrome.GetEnabled(plain));
            Assert.NotEqual(Fixture, (plain.Background as SolidColorBrush)?.Color);

            Assert.False(ModernWindowChrome.GetEnabled(fixture));
            Assert.Equal(Fixture, Assert.IsType<SolidColorBrush>(fixture.Background).Color);
        }
        finally
        {
            plain.Close();
            fixture.Close();
        }
    });

    [Fact]
    public void SelectFixtureMonitor_SkipsACursorDisplayThatCannotHoldTheSustainedWorkload()
    {
        MonitorInfo wide = Monitor(@"\\.\DISPLAY1", 0, 0, 3440, 1440, primary: true);
        MonitorInfo portrait = Monitor(@"\\.\DISPLAY2", 3440, 0, 768, 1366, primary: false);
        MonitorInfo[] all = [wide, portrait];

        Assert.False(RecordingPerformanceSelfTest.CanCover(RecordingPerformanceSelfTest.FixtureBoundsOn(portrait), 1280, 720));
        Assert.Same(wide, RecordingPerformanceSelfTest.SelectFixtureMonitor(portrait, all));
        Assert.Same(wide, RecordingPerformanceSelfTest.SelectFixtureMonitor(wide, all));

        // A large secondary cursor display keeps the fixture where the pointer is.
        MonitorInfo secondary = Monitor(@"\\.\DISPLAY3", -1920, 0, 1920, 1080, primary: false);
        Assert.Same(secondary, RecordingPerformanceSelfTest.SelectFixtureMonitor(secondary, [wide, secondary]));

        // With no display large enough the cursor display is kept so the run reports the gap.
        Assert.Same(portrait, RecordingPerformanceSelfTest.SelectFixtureMonitor(portrait, [portrait]));
    }

    [Fact]
    public void FixtureBounds_CoverEverySustainedRegionWithAMarginOnAWideDisplay()
    {
        RectD bounds = RecordingPerformanceSelfTest.FixtureBoundsOn(Monitor(@"\\.\DISPLAY1", 0, 0, 3440, 1440, primary: true));
        Assert.Equal(new RectD(20, 20, 1400, 850), bounds);
        Assert.True(RecordingPerformanceSelfTest.CanCover(bounds, 320, 240));
        Assert.True(RecordingPerformanceSelfTest.CanCover(bounds, 1280, 720));
        Assert.False(RecordingPerformanceSelfTest.CanCover(bounds, 1381, 720));
    }

    [Fact]
    public void CountChangedPixels_InspectsTheFixedOverlayBandWithChannelTolerance()
    {
        const int width = 320, height = 240, stride = width * 4;
        byte[] pixels = Fill(width, height, Fixture.B, Fixture.G, Fixture.R);
        Assert.Equal(0, RecordingPerformanceSelfTest.CountChangedPixels(pixels, stride));

        // Within +/-12 per channel is still the fixture.
        Set(pixels, stride, 10, 10, (byte)(Fixture.B + 12), (byte)(Fixture.G - 12), (byte)(Fixture.R + 12));
        Assert.Equal(0, RecordingPerformanceSelfTest.CountChangedPixels(pixels, stride));

        Set(pixels, stride, 11, 10, (byte)(Fixture.B + 13), Fixture.G, Fixture.R);
        Assert.Equal(1, RecordingPerformanceSelfTest.CountChangedPixels(pixels, stride));

        // Outside the inspected 306x66 band is ignored.
        Set(pixels, stride, 3, 3, 0, 0, 0);
        Set(pixels, stride, 310, 70, 0, 0, 0);
        Assert.Equal(1, RecordingPerformanceSelfTest.CountChangedPixels(pixels, stride));

        // The theme Surface.Base that replaced the fixture fills the whole band.
        byte[] themed = Fill(width, height, 0x17, 0x0F, 0x0B);
        Assert.Equal(306 * 66, RecordingPerformanceSelfTest.CountChangedPixels(themed, stride));
    }

    [Fact]
    public void CountFixtureMismatches_CoversTheRequestedRectangleOnly()
    {
        const int width = 8, height = 6, stride = width * 4;
        byte[] pixels = Fill(width, height, Fixture.B, Fixture.G, Fixture.R);
        Set(pixels, stride, 0, 0, 255, 0, 255);
        Set(pixels, stride, 7, 5, 255, 0, 255);
        Assert.Equal(2, RecordingPerformanceSelfTest.CountFixtureMismatches(pixels, stride, 0, 0, width, height));
        Assert.Equal(0, RecordingPerformanceSelfTest.CountFixtureMismatches(pixels, stride, 1, 1, 7, 5));
    }

    private static MonitorInfo Monitor(string name, double left, double top, double width, double height, bool primary)
    {
        var bounds = new RectD(left, top, width, height);
        return new MonitorInfo(name, bounds, bounds, 96, primary);
    }

    private static byte[] Fill(int width, int height, byte b, byte g, byte r)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static void Set(byte[] pixels, int stride, int x, int y, byte b, byte g, byte r)
    {
        int offset = y * stride + x * 4;
        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
    }
}
