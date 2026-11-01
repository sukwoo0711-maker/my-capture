using System.Runtime.CompilerServices;
using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
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
    public Task StartupLayoutLeavesFontInventoryForFirstTextUse() => SettingsThemePreviewTests.RunIsolated(
        nameof(StartupLayoutLeavesFontInventoryForFirstTextUse), () => AssertFontInventoryDeferred("Select"),
        typeof(EditorPrewarmLifetimeTests));

    [Fact]
    public Task StartupLayoutWithRememberedTextToolLeavesFontInventoryForRealEditor() => SettingsThemePreviewTests.RunIsolated(
        nameof(StartupLayoutWithRememberedTextToolLeavesFontInventoryForRealEditor), () => AssertFontInventoryDeferred("Text"),
        typeof(EditorPrewarmLifetimeTests));

    private static void AssertFontInventoryDeferred(string lastTool)
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        ThemeService.ApplyResources(AppTheme.Midnight, application.Resources, systemHighContrast: false);
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? write = AnnotationEditorPreferences.Write;
        int writes = 0;
        AnnotationEditorPreferences.Read = () => new AnnotationDefaults { LastTool = lastTool, FontFamily = "Maple Mono" };
        AnnotationEditorPreferences.Write = _ => writes++;
        try
        {
            AssertFontInventoryEmpty();
            AnnotationEditorPrewarm.Run();
            Ocr410Fixture.Pump();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            AssertFontInventoryEmpty();
            Assert.Equal(0, writes);

            var bitmap = EditorKeysTests.Solid(640, 360, 0x30);
            var region = new RectD(0, 0, 640, 360);
            var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap);
            if (lastTool == "Select")
            {
                editor.Measure(new Size(1000, 640));
                editor.Arrange(new Rect(0, 0, 1000, 640));
                AssertFontInventoryEmpty();
                editor.ShortcutActions["Text"]();
            }
            ComboBox selector = EditorKeysTests.Field<ComboBox>(editor, "_textFontFamily");
            Assert.Equal("Maple Mono", selector.SelectedValue);
            Assert.Contains(selector.Items.OfType<FontChoice>(), choice => choice.IsMore);
            Assert.NotEmpty(FontCache("Installed"));
            Assert.NotEmpty(FontCache("Recommended"));
        }
        finally
        {
            AnnotationEditorPreferences.Read = read;
            AnnotationEditorPreferences.Write = write;
            application.Shutdown();
        }
    }

    private static void AssertFontInventoryEmpty()
    {
        foreach (string name in new[] { "Installed", "Recommended", "Available" }) Assert.Empty(FontCache(name));
    }

    private static IEnumerable FontCache(string name) => (IEnumerable)typeof(FontCatalog)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

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

    [Fact]
    public void SourceVariantsReuseOneEditorAndReleaseItWhileDispatcherLives() =>
        Ocr410Fixture.RunSta(() => AssertReleased(source: false, reuseSource: true));

    private static void AssertReleased(bool source, bool reuseSource = false)
    {
        WeakReference[] references = CreateAndCloseHost(source, reuseSource);
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
    private static WeakReference[] CreateAndCloseHost(bool source, bool reuseSource)
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
            if (reuseSource)
            {
                AnnotationEditorControl editor = host.Editor;
                foreach (CaptureProvenance provenance in new[] { CaptureProvenance.Create(appName: "Fixture"), CaptureProvenance.None })
                {
                    host.PrewarmCaptureSource([monitor], provenance);
                    Assert.Same(editor, host.Editor);
                    Assert.Equal(provenance.HasSource, editor.ShowsCaptureSource);
                    Assert.Equal(IntPtr.Zero, new WindowInteropHelper(host).Handle);
                    Assert.False(host.IsVisible);
                }
            }
            return [new(host), new(host.Editor), new(bitmap)];
        }
        finally { host.Close(); }
    }
}
