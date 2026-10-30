using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorObscureEvidenceTests
{
    [Fact]
    public void RenderRailPropertiesWatermarkAndCrop() => StaTestHost.Run(() =>
    {
        string? stage = Environment.GetEnvironmentVariable("MYCAPTURE_OBSCURE_EVIDENCE");
        if (string.IsNullOrEmpty(stage)) return;
        using var language = UiText.UseLanguage("ko-KR");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, ".git"))) repository = repository.Parent;
        string root = Path.Combine(repository!.FullName, "work", "editor-obscure-crop-esc");
        Directory.CreateDirectory(root);
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Width = 1100;
        host.Window.Height = 700;
        host.Window.Resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, host.Window.Resources, systemHighContrast: false);
        foreach (var (tool, name) in new[] { (EditorTool.Mosaic, "pixelate"), (EditorTool.Blur, "blur") })
        {
            Invoke(host.Editor, "SelectTool", tool);
            Save(host.Window, Path.Combine(root, stage + "-" + name + ".png"));
        }
        Invoke(host.Editor, "SelectTool", EditorTool.Crop);
        host.Editor.PendingCrop!.Begin(new PointD(100, 80), 7);
        host.Editor.PendingCrop.Drag(new PointD(350, 240), false);
        host.Editor.PendingCrop.End();
        Invoke(host.Editor, "RefreshCrop", true);
        Save(host.Window, Path.Combine(root, stage + "-crop.png"));
        host.Editor.CancelPendingCrop();

        // Measure and render the real context menu directly without opening a desktop popup.
        ContextMenu menu = host.Editor.EnsureStampMenu();
        menu.Resources = host.Window.Resources;
        menu.Measure(new Size(350, 260));
        menu.Arrange(new Rect(new Point(), menu.DesiredSize));
        Save(menu, Path.Combine(root, stage + "-watermark.png"));
    });

    private static void Save(FrameworkElement window, string path)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Assert.True(stream.Length > 1000);
    }
}
