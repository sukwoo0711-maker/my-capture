using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingControlViewportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualMonitorControlsRemainVisibleBeforeDuringAndAfterLargeRecording(bool fullScreen) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        foreach (MonitorInfo monitor in MonitorEnumerator.GetAll())
        {
            RectD region = fullScreen ? monitor.Bounds : new RectD(
                monitor.Bounds.Left + 20, monitor.Bounds.Top + 20,
                monitor.Bounds.Width - 40, monitor.Bounds.Height - 40);
            using var firstFrame = new ManualResetEventSlim();
            var capture = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
            using var recorder = new RegionRecorder(new RegionFrameGrabber(capture, includeCursor: false),
                options => new FrameSink(options.Width, options.Height, firstFrame), NullLogger.Instance);
            var window = new RecordingControlWindow(region,
                new RecordingSettings { UseStartDelay = false, FrameRate = RecordingFrameRate.Fps15 },
                () => recorder, () => "recording-viewport-no-file.mp4", NullLogger.Instance)
            {
                ShowActivated = false,
            };
            bool finished = false;
            Exception? failure = null;
            window.RecordingFinished += (_, _) => finished = true;
            window.Failed += (_, args) => failure = args.Exception;
            try
            {
                LoadTheme(window);
                window.Show();
                PumpUntil(() => window.IsVisible);
                window.UpdateLayout();
                Border strip = Field<Border>(window, "_controlStrip");
                Button primary = Field<Button>(window, "_primaryButton");
                primary.Style = (Style)window.FindResource("Button.Primary");
                foreach (Button cancel in Buttons(strip).OfType<Button>().Where(button => !ReferenceEquals(button, primary)))
                    cancel.Style = (Style)window.FindResource("Button.Ghost");
                window.UpdateLayout();
                AssertControlsInsideWorkArea(strip, monitor.WorkArea);
                SaveEvidence(window, strip, monitor, fullScreen, "ready");

                // Exercise the real start command and capture worker. The injected encoder
                // accepts the acquired pixels without creating or deleting any media file.
                primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => firstFrame.IsSet || failure is not null);
                Assert.Null(failure);
                Assert.True(window.IsRecording);
                primary.Style = (Style)window.FindResource("Button.Danger");
                window.UpdateLayout();
                AssertControlsInsideWorkArea(strip, monitor.WorkArea);
                Assert.True(primary.IsEnabled);
                SaveEvidence(window, strip, monitor, fullScreen, "recording");

                // The same actual button now stops recording; completion keeps the palette
                // visible while its owner registers the clip with the library.
                primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => finished || failure is not null);
                Assert.Null(failure);
                Assert.True(finished);
                window.UpdateLayout();
                AssertControlsInsideWorkArea(strip, monitor.WorkArea, requireEnabled: false);
                SaveEvidence(window, strip, monitor, fullScreen, "completed");
            }
            finally
            {
                if (window.IsRecording)
                {
                    window.RequestStop();
                    PumpUntil(() => finished || failure is not null);
                }
                window.CompleteAndClose();
            }
        }
    });

    private static void AssertControlsInsideWorkArea(Border strip, RectD work, bool requireEnabled = true)
    {
        Assert.True(strip.IsVisible);
        ButtonBase[] buttons = Buttons(strip).ToArray();
        Assert.Equal(3, buttons.Length); // start/stop, clock toggle, cancel
        foreach (ButtonBase button in buttons)
        {
            Assert.True(button.IsVisible && button.IsTabStop && button.Focusable);
            if (requireEnabled) Assert.True(button.IsEnabled);
            RectD bounds = ScreenBounds(button);
            Assert.True(bounds.Width > 0 && bounds.Height > 0);
            Assert.True(bounds.Left >= work.Left - 1 && bounds.Top >= work.Top - 1
                && bounds.Right <= work.Right + 1 && bounds.Bottom <= work.Bottom + 1,
                $"{AutomationProperties.GetName(button)} is outside work area: {bounds}; work={work}");
            RectD stripBounds = ScreenBounds(strip);
            Assert.True(bounds.Left >= stripBounds.Left - 1 && bounds.Top >= stripBounds.Top - 1
                && bounds.Right <= stripBounds.Right + 1 && bounds.Bottom <= stripBounds.Bottom + 1,
                $"{AutomationProperties.GetName(button)} is clipped by the control strip.");
        }
    }

    private static RectD ScreenBounds(FrameworkElement element)
    {
        Point topLeft = element.PointToScreen(new Point());
        Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new RectD(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private static void SaveEvidence(RecordingControlWindow window, Border strip, MonitorInfo monitor,
        bool fullScreen, string state)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_RECORDING_CONTROLS_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        string stem = $"controls-{monitor.Bounds.Left}-{monitor.Bounds.Top}-{(fullScreen ? "full" : "large")}-{state}";
        var root = (FrameworkElement)window.Content;
        Render(root, Path.Combine(directory, stem + ".png"));
        Render(strip, Path.Combine(directory, stem + "-palette.png"));
        File.WriteAllLines(Path.Combine(directory, stem + ".txt"),
            new[] { $"Actual WPF window; monitor={monitor.Bounds}; work={monitor.WorkArea}; DPI={monitor.Dpi}; state={state}" }
                .Concat(Buttons(strip).Select(button =>
                    $"{AutomationProperties.GetName(button)}: {ScreenBounds(button)}; visible={button.IsVisible}; enabled={button.IsEnabled}")));
    }

    private static void Render(FrameworkElement element, string path)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void LoadTheme(Window window)
    {
        XElement? combined = null;
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(RecordingControlViewportTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            combined ??= new XElement(dictionary.Name);
            foreach (XAttribute declaration in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                    && !value.Contains(";assembly=", StringComparison.Ordinal)) value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined!.ToString(SaveOptions.DisableFormatting)));
    }

    private static IEnumerable<ButtonBase> Buttons(DependencyObject root) => Descendants(root).OfType<ButtonBase>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void PumpUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(predicate(), "The actual recording control window did not reach the expected state.");
    }

    private sealed class FrameSink(int width, int height, ManualResetEventSlim received) : IVideoEncoder
    {
        public int Width => width;
        public int Height => height;
        public void WriteFrame(in EncoderFrame frame) { Assert.Equal(width, frame.Width); Assert.Equal(height, frame.Height); received.Set(); }
        public void Complete() { }
        public void Dispose() { }
    }
}
