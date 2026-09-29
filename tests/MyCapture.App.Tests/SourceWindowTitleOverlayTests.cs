using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SourceWindowTitleOverlayTests
{
    [Fact]
    public void Setting_DefaultsOff_AndSurvivesCloneDraftRestartExportImport()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mycapture-title-setting-").FullName;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            File.WriteAllText(paths.SettingsFile, "{\"export\":{\"fileNamePattern\":\"legacy_{yyyyMMdd}\"}}");
            AppSettings old = store.Load();
            Assert.False(old.Export.ShowSourceWindowTitle);
            var draft = new SettingsDraft(old.DeepClone()) { ShowSourceWindowTitle = true };
            Assert.False(old.Export.ShowSourceWindowTitle);
            AppSettings applied = draft.ToAppSettings();
            Assert.True(applied.Export.ShowSourceWindowTitle);
            Assert.Equal("legacy_{yyyyMMdd}", applied.Export.FileNamePattern);
            Assert.True(new SettingsDraft(applied.DeepClone()).ShowSourceWindowTitle);
            store.Save(applied);
            Assert.True(new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load().Export.ShowSourceWindowTitle);
            string exported = Path.Combine(root, "portable.json");
            store.ExportTo(applied, exported);
            Assert.True(store.ImportFrom(exported).Export.ShowSourceWindowTitle);
            draft.ResetToDefaults();
            Assert.False(draft.ShowSourceWindowTitle);
            Assert.False(draft.ToAppSettings().Export.ShowSourceWindowTitle);
        }
        finally { TestRecycleBin.DeleteDirectory(root, recursive: true); }
    }

    [Theory]
    [InlineData(720, 360)]
    [InlineData(96, 40)]
    [InlineData(24, 12)]
    [InlineData(360, 720)]
    public void LongMultilingualTitle_IsSingleLineAndStaysInsideImage(int width, int height) => StaTestHost.Run(() =>
    {
        string title = string.Concat(Enumerable.Repeat("매출예산📊.xlsx — Excel\r\n\t", 100));
        var drawing = new DrawingGroup();
        using (DrawingContext dc = drawing.Open()) SourceWindowTitleRenderer.Draw(dc, title, width, height);
        Rect bounds = drawing.Bounds;
        Assert.False(bounds.IsEmpty);
        Assert.InRange(bounds.Left, 0, width - 1);
        Assert.InRange(bounds.Top, 0, height - 1);
        Assert.InRange(bounds.Right, width * 0.8, width);
        Assert.InRange(bounds.Bottom, 1, Math.Min(height, 40));
        Assert.True(bounds.Height <= 30, $"Expected one compact line, got {bounds}.");
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void MissingTitle_DrawsNoBadge(string? title) => StaTestHost.Run(() =>
    {
        var drawing = new DrawingGroup();
        using (DrawingContext dc = drawing.Open()) SourceWindowTitleRenderer.Draw(dc, title, 720, 360);
        Assert.True(drawing.Bounds.IsEmpty);
        BitmapSource original = Sample();
        BitmapSource flattened = AnnotationFlattener.Flatten(original, AnnotationDocument.CreateFor(720, 360),
            new AnnotationRenderer(new AnnotationImageStore()), title);
        Assert.Equal(Pixels(original), Pixels(flattened));
    });

    [Fact]
    public void TinyImage_RemainsIntactWhenThereIsNoRoomForReadableTitle() => StaTestHost.Run(() =>
    {
        var drawing = new DrawingGroup();
        using (DrawingContext dc = drawing.Open()) SourceWindowTitleRenderer.Draw(dc, "Excel", 1, 1);
        Assert.True(drawing.Bounds.IsEmpty);
    });

    [Fact]
    public void EditorWithoutSourceTitle_PassesExplicitEmptyTitleToCommit() => StaTestHost.Run(() =>
    {
        BitmapSource original = Sample();
        var region = new RectD(0, 0, 720, 360);
        var frame = new FrozenFrame(original, region, null, 0);
        Func<bool>? previous = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => true;
        var window = new AnnotationEditorWindow(frame, region, original) { ShowActivated = false };
        try
        {
            AnnotationEditingResult? result = null;
            window.CommitRequested = edit => { result = edit; return Task.FromResult(false); };
            Assert.True(window.Editor.HandleShortcut(Key.C, ModifierKeys.Control));
            PumpUntil(() => result is not null && !window.Editor.IsCommitInProgress);
            Assert.Equal(string.Empty, result!.SourceWindowTitle);
            Assert.True(result.ShowSourceWindowTitle);
        }
        finally
        {
            window.Close();
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = previous;
        }
    });

    [Fact]
    public void ActualPreviewUsesExportPixels_AndGalleryReopenReadsCurrentOption() => StaTestHost.Run(() =>
    {
        const string title = "2026년 3분기 매출.xlsx - Excel";
        BitmapSource original = Sample();
        var region = new RectD(0, 0, 720, 360);
        var frame = new FrozenFrame(original, region, null, 0);
        var document = AnnotationDocument.CreateFor(720, 360);
        var record = new CaptureRecord { SourceWindowTitle = title, Width = 720, Height = 360 };
        var context = new GalleryReeditContext(record, frame, region, original, document,
            new Dictionary<string, BitmapSource>());
        Func<bool>? previous = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        try
        {
            foreach (bool show in new[] { false, true, false })
            {
                AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => show;
                var window = new GalleryEditorWindow(context)
                {
                    Width = 1040, Height = 720, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var surface = (AnnotationEditorSurface)typeof(AnnotationEditorControl)
                        .GetField("_surface", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window.Editor)!;
                    Assert.Equal(show ? title : null, surface.SourceWindowTitle);
                    WriteEvidence(window, show ? "editor-title-on.png" : "editor-title-off.png");

                    // Compare the actual WPF preview at 1:1 image pixels with the export,
                    // excluding only its outer crop border. Both call the same badge renderer.
                    var preview = new AnnotationEditorSurface(frame, region,
                        new AnnotationEditorController(document, new UndoStack()),
                        new AnnotationRenderer(new AnnotationImageStore())) { SourceWindowTitle = surface.SourceWindowTitle };
                    preview.Measure(new Size(720, 360));
                    preview.Arrange(new Rect(0, 0, 720, 360));
                    preview.UpdateLayout();
                    var renderedPreview = new RenderTargetBitmap(720, 360, 96, 96, PixelFormats.Pbgra32);
                    renderedPreview.Render(preview);
                    BitmapSource flattened = AnnotationFlattener.Flatten(original, document,
                        new AnnotationRenderer(new AnnotationImageStore()), show ? title : null);
                    Assert.Equal(Pixels(flattened, inset: 2), Pixels(renderedPreview, inset: 2));
                    WriteEvidence(flattened, show ? "image-title-on.png" : "image-title-off.png");

                    AnnotationEditingResult? result = null;
                    window.CommitRequested = edit => { result = edit; return Task.FromResult(false); };
                    // A settings change in another window does not make an open editor
                    // export different pixels from its visible preview.
                    AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => !show;
                    Assert.True(window.Editor.HandleShortcut(Key.C, ModifierKeys.Control));
                    PumpUntil(() => result is not null && !window.Editor.IsCommitInProgress);
                    Assert.Equal(title, result!.SourceWindowTitle);
                    Assert.Equal(show, result.ShowSourceWindowTitle);
                    Assert.Empty(result.Document.Items);
                    Assert.Equal(Pixels(original), Pixels(result.SelectedBitmap));
                }
                finally { window.Close(); }
            }
        }
        finally { AnnotationEditorPreferences.ReadShowSourceWindowTitle = previous; }
    });

    private static BitmapSource Sample()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 360));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(218, 224, 230)), 1);
            for (int x = 0; x <= 720; x += 90) dc.DrawLine(pen, new Point(x, 0), new Point(x, 360));
            for (int y = 0; y <= 360; y += 30) dc.DrawLine(pen, new Point(0, y), new Point(720, y));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(220, 239, 230)), null, new Rect(1, 1, 88, 28));
        }
        var bitmap = new RenderTargetBitmap(720, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap, int inset = 0)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth - inset * 2, height = converted.PixelHeight - inset * 2;
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(new Int32Rect(inset, inset, width, height), pixels, width * 4, 0);
        return pixels;
    }

    private static void WriteEvidence(Window window, string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_SOURCE_TITLE_EVIDENCE"))) return;
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        WriteEvidence(bitmap, name);
    }

    private static void WriteEvidence(BitmapSource bitmap, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_SOURCE_TITLE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Assert.True(completed(), "The editor commit did not finish.");
    }
}
