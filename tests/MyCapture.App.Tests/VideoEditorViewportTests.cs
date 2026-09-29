using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoEditorViewportTests
{
    [Fact]
    public void DefaultSizeKeepsCommandsOnEachActualTargetMonitor() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string root = Path.Combine(Path.GetTempPath(), "mc-video-default-viewport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RecordingResult recording = EncodeClip(Path.Combine(root, "source.mp4"), 3840, 2160);
        foreach (MonitorInfo monitor in MonitorEnumerator.GetAll())
        {
            // A tiny nonactivating owner chooses WPF's CenterScreen target without moving
            // the user's pointer or changing any display setting. Editor dimensions are
            // left exactly as the production constructor computes them.
            var owner = new Window
            {
                Width = 1, Height = 1, WindowStyle = WindowStyle.None,
                Left = (monitor.WorkArea.Left + 10) / monitor.ScaleFactor,
                Top = (monitor.WorkArea.Top + 10) / monitor.ScaleFactor,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false, ShowActivated = false,
            };
            VideoEditorWindow? editor = null;
            try
            {
                owner.Show();
                editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance)
                {
                    Owner = owner, ShowActivated = false, ShowInTaskbar = false,
                };
                LoadWindowTheme(editor);
                editor.FontSize = (double)editor.FindResource("FontSize.Body");
                editor.Show();
                var media = Descendants(editor).OfType<MediaElement>().Single();
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while ((media.NaturalVideoWidth != 3840 || !editor.IsMediaReadyForTest)
                    && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline)
                    PumpFor(TimeSpan.FromMilliseconds(25));
                Assert.Equal(3840, media.NaturalVideoWidth);
                Assert.True(editor.IsMediaReadyForTest);
                editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
                var layout = Assert.IsType<Grid>(editor.Content);
                var client = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(layout));
                SaveEvidence(editor, client, 3840, (int)monitor.WorkArea.Width, (int)monitor.WorkArea.Height);
                foreach (string name in new[] { "MediaExport_SaveEdits", "MediaExport_ExportFormats", "Text_9A9C87658130" })
                {
                    Button button = Assert.Single(Descendants(editor).OfType<Button>(),
                        b => AutomationProperties.GetName(b) == UiText.Get(name));
                    Point top = button.PointToScreen(new Point());
                    Point bottom = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                    Assert.True(top.X >= monitor.WorkArea.Left - 1 && top.Y >= monitor.WorkArea.Top - 1
                        && bottom.X <= monitor.WorkArea.Right + 1 && bottom.Y <= monitor.WorkArea.Bottom + 1,
                        $"{name}: screen={top}..{bottom}; target={monitor.DeviceName} {monitor.WorkArea}; "
                        + $"primary work={SystemParameters.WorkArea}; editor={editor.Left},{editor.Top} {editor.ActualWidth}x{editor.ActualHeight}");
                }
            }
            finally { editor?.Close(); owner.Close(); }
        }
    });

    [Theory]
    [InlineData(320, 240, 920, 680)]
    [InlineData(3840, 2160, 760, 555)]
    [InlineData(3840, 2160, 920, 680)]
    public void DecodedMediaKeepsSaveAndPlaybackWithinClientViewport(
        int sourceWidth, int sourceHeight, int windowWidth, int windowHeight) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        // Keep generated evidence recoverable; this fixture never deletes its temporary clip.
        string root = Path.Combine(Path.GetTempPath(), "mc-video-viewport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RecordingResult recording = EncodeClip(Path.Combine(root, "source.mp4"), sourceWidth, sourceHeight);
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
        try
        {
            LoadWindowTheme(editor);
            editor.FontSize = (double)editor.FindResource("FontSize.Body");
            editor.WindowStartupLocation = WindowStartupLocation.Manual;
            editor.Width = windowWidth;
            editor.Height = windowHeight;
            editor.Left = -10000;
            editor.Top = -10000;
            editor.ShowActivated = false;
            editor.ShowInTaskbar = false;
            editor.Show();
            var media = Descendants(editor).OfType<MediaElement>().Single();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while ((media.NaturalVideoWidth != sourceWidth || !editor.IsMediaReadyForTest)
                && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline)
                PumpFor(TimeSpan.FromMilliseconds(25));
            Assert.False(editor.HasMediaFailedForTest, editor.MediaFailureForTest);
            // Require the real decoder dimensions; the five-second fallback is not evidence
            // that a large MediaElement participated in WPF's measure/arrange pass.
            Assert.Equal(sourceWidth, media.NaturalVideoWidth);
            Assert.Equal(sourceHeight, media.NaturalVideoHeight);
            Assert.True(editor.IsMediaReadyForTest);
            editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));

            var layout = Assert.IsType<Grid>(editor.Content);
            var client = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(layout));
            SaveEvidence(editor, client, sourceWidth, windowWidth, windowHeight);
            foreach (string name in new[] { "MediaExport_SaveEdits", "MediaExport_ExportFormats", "Text_9A9C87658130" })
            {
                Button button = Assert.Single(Descendants(editor).OfType<Button>(),
                    b => AutomationProperties.GetName(b) == UiText.Get(name));
                Assert.True(button.IsVisible && button.IsEnabled && button.IsTabStop && button.Focusable, name);
                AssertWithinClient(button, client);
                FocusManager.SetFocusedElement(editor, button);
                Assert.Same(button, FocusManager.GetFocusedElement(editor));
            }
            var viewport = Assert.Single(Descendants(editor).OfType<Grid>(), grid => grid.Name == "VideoPreviewViewport");
            AssertWithinClient(viewport, client);
            Assert.True(viewport.ActualWidth >= 100 && viewport.ActualHeight >= 112);
            AssertWithinClient(media, viewport);

            // A second layout after a decoded source has acquired its natural size must
            // also release the preview's old size when the window becomes compact.
            editor.Width = 760;
            editor.Height = 555;
            editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
            foreach (Button button in Descendants(editor).OfType<Button>().Where(b =>
                AutomationProperties.GetName(b) == UiText.Get("MediaExport_SaveEdits") ||
                AutomationProperties.GetName(b) == UiText.Get("Text_9A9C87658130")))
                AssertWithinClient(button, client);
        }
        finally { editor.Close(); }
    });

    private static void AssertWithinClient(FrameworkElement element, FrameworkElement client)
    {
        Point top = element.TranslatePoint(new Point(), client);
        Point bottom = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), client);
        Assert.True(top.X >= -0.5 && top.Y >= -0.5 && bottom.X <= client.ActualWidth + 0.5 && bottom.Y <= client.ActualHeight + 0.5,
            $"{element.GetType().Name} {AutomationProperties.GetName(element)} bounds={top}..{bottom}; viewport={client.ActualWidth}x{client.ActualHeight}");
    }

    private static RecordingResult EncodeClip(string path, int width, int height)
    {
        const int fps = 10, frames = 10;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0x50;
            pixels[offset + 1] = 0x30;
            pixels[offset + 2] = 0x18;
            pixels[offset + 3] = 255;
        }
        using var encoder = new MediaFoundationVideoEncoder(
            new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps)),
            NullLogger.Instance);
        for (int i = 0; i < frames; i++)
            encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, i * 100.0));
        encoder.Complete();
        return new RecordingResult(path, 1000, fps, frames, width, height);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SaveEvidence(Window editor, FrameworkElement client, int sourceWidth, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_VIDEO_VIEWPORT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        string stem = $"video-{sourceWidth}-{width}x{height}";
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth), (int)Math.Ceiling(client.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(client);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(Path.Combine(directory, stem + ".png"))) encoder.Save(stream);
        File.WriteAllLines(Path.Combine(directory, stem + ".txt"), Descendants(editor).OfType<Button>().Where(b => b.IsVisible).Select(b =>
        {
            Point top = b.TranslatePoint(new Point(), client);
            Point bottom = b.TranslatePoint(new Point(b.ActualWidth, b.ActualHeight), client);
            return $"{AutomationProperties.GetName(b)}: {top}..{bottom}; client={client.ActualWidth}x{client.ActualHeight}";
        }));
    }

    private static void LoadWindowTheme(Window window)
    {
        XElement? combined = null;
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(VideoEditorViewportTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            combined ??= new XElement(dictionary.Name);
            foreach (XAttribute declaration in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined!.ToString(SaveOptions.DisableFormatting)));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
