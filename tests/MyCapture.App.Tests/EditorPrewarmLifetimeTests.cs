using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorPrewarmLifetimeTests
{
    [Fact]
    public Task ThemedPrewarmAlsoReleasesItsRenderedTree() => SettingsThemePreviewTests.RunIsolated(
        nameof(ThemedPrewarmAlsoReleasesItsRenderedTree), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            ThemeService.ApplyResources(AppTheme.Midnight, application.Resources, systemHighContrast: false);
            foreach (bool source in new[] { false, true }) AssertReleased(source);
            application.Shutdown();
        }, typeof(EditorPrewarmLifetimeTests));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedPrewarmHostReleasesWindowEditorAndImageWhileDispatcherLives(bool source) =>
        Ocr410Fixture.RunSta(() => AssertReleased(source));

    private static void AssertReleased(bool source)
    {
        WeakReference[] references = CreateAndCloseHost(source);
        for (int attempt = 0; attempt < 4 && references.Any(reference => reference.IsAlive); attempt++)
        {
            Ocr410Fixture.Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive, "Prewarm retained a window, editor or source image."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateAndCloseHost(bool source)
    {
        BitmapSource bitmap = BitmapSource.Create(64, 36, 96, 96, PixelFormats.Bgr32, null,
            new byte[64 * 36 * 4], 64 * 4);
        bitmap.Freeze();
        var region = new RectD(0, 0, 64, 36);
        var monitor = new MonitorInfo("PREWARM-TEST", new RectD(-20000, -20000, 1920, 1080),
            new RectD(-20000, -20000, 1920, 1040), 96, true);
        var host = AnnotationEditorWindow.CreatePrewarmHost(new FrozenFrame(bitmap, region, monitor, 0),
            region, bitmap, [monitor], source ? CaptureProvenance.Create(appName: "Fixture") : CaptureProvenance.None);
        try
        {
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(host).Handle);
            Assert.False(host.IsVisible);
            if (!source)
            {
                var viewport = new Size(1000, 640);
                host.Editor.Measure(viewport);
                host.Editor.Arrange(new Rect(viewport));
                var target = new RenderTargetBitmap(1000, 640, 96, 96, PixelFormats.Pbgra32);
                target.Render(host.Editor);
            }
            return [new(host), new(host.Editor), new(bitmap)];
        }
        finally { host.Close(); }
    }
}
