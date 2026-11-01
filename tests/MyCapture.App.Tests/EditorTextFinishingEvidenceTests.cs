using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorTextFinishingEvidenceTests : KoreanCaptionTest
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void ResizedLiveTextUsesCurrentImagePositionAndScale() => WithStillMotion(() =>
    {
        BitmapSource image = GridImage();
        var editor = Editor(image);
        using var host = new PlainHost(editor, 980, 720, ResizeMode.CanResize);
        TextBox box = BeginText(editor, host.Window, new PointD(420, 180), "첫 줄 abc\n둘째 줄 입력 내용");
        host.Window.Width = 680;
        host.Window.Height = 520;
        Settle(host.Window);

        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        TextAnnotation text = Assert.Single(editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>());
        Assert.Equal(text.FontSize * surface.DipPerPixel, box.FontSize, 6);
        Rect firstGlyph = box.GetRectFromCharacterIndex(0);
        Point actual = box.TranslatePoint(firstGlyph.TopLeft, surface);
        Point expected = surface.ToSurfacePoint(new PointD(text.Rect.X, text.Rect.Y));
        Assert.InRange((actual - expected).Length, 0, .02);
        Assert.Equal("첫 줄 abc\n둘째 줄 입력 내용", box.Text);
        Save(editor, "resize-to-be.png");
    });

    [Fact]
    public void FontFieldDisplaysPixelsWithoutChangingStoredSize() => WithStillMotion(() =>
    {
        BitmapSource image = OrderImage();
        var editor = Editor(image);
        using var host = new PlainHost(editor, 1020, 700, ResizeMode.NoResize);
        BeginText(editor, host.Window, new PointD(80, 80), "글자 크기 18 / Font size 18");

        var section = Field<FrameworkElement>(editor, "_fontSizeSection");
        Assert.Single(EditorKeysTests.Descendants(section).OfType<TextBlock>(), block => block.Text == "px");
        Assert.DoesNotContain(EditorKeysTests.Descendants(section).OfType<TextBlock>(), block => block.Text == "pt");
        Assert.Equal(18, Assert.Single(editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>()).FontSize);
        Save(editor, "font-unit-to-be.png");
    });

    [Fact]
    public void SavedCopyFailureShowsSavedStateAndRetryAction() => WithStillMotion(() =>
    {
        BitmapSource image = EditorKeysTests.Solid(900, 520, 0xE8);
        var region = new RectD(0, 0, 900, 520);
        var monitor = new MonitorInfo(@"\\.\DISPLAY_AUDIT", new RectD(0, 0, 3440, 1440), new RectD(0, 0, 3440, 1392), 96, true);
        var window = new AnnotationEditorWindow(new FrozenFrame(image, region, monitor, 0), region, image, pointerMonitor: monitor);
        var activate = (EventHandler)typeof(AnnotationEditorWindow).GetMethod("OnContentRendered", Members, [typeof(object), typeof(EventArgs)])!
            .CreateDelegate(typeof(EventHandler), window);
        window.ContentRendered -= activate;
        window.Resources = ThemeResources();
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.ConfirmClose = () => EditorCloseChoice.DontSave;
        try
        {
            window.Show();
            Settle(window);
            AnnotationEditorControl editor = window.Editor;
            Invoke(editor, "SelectTool", EditorTool.Rectangle);
            Settle(window);
            editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(120, 90)));
            editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(300, 190)), true);
            editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(300, 190)));
            int copies = 0;
            window.CommitRequested = result =>
            {
                copies++;
                if (result.Action == EditorCommitAction.CopyToClipboard) return Task.FromResult(true);
                result.SaveCompleted = true;
                result.ClipboardCopyFailed = true;
                return Task.FromResult(false);
            };

            Assert.True(editor.HandleShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift));
            Settle(window);
            Assert.Equal(UiText.Get("EditorFlow_SavedCopyFailed"), editor.StatusText);
            Assert.False(editor.HasUnsavedChanges);
            var retry = Field<Button>(editor, "_retryCopyButton");
            Assert.Equal(Visibility.Visible, retry.Visibility);
            Assert.Equal(UiText.Get("EditorFlow_RetryCopy"), retry.Content);
            Save(editor, "saved-copy-failed-to-be.png");

            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.Equal(2, copies);
            Assert.Equal(Visibility.Collapsed, retry.Visibility);
            Assert.True(window.IsVisible);
        }
        finally { window.Close(); }
    });

    private static void WithStillMotion(Action action) => StaTestHost.Run(() =>
    {
        Assert.Null(AnnotationEditorPreferences.Write);
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try { action(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    private static AnnotationEditorControl Editor(BitmapSource image)
    {
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        return new AnnotationEditorControl(new FrozenFrame(image, region, null, 0), region, image, null, null);
    }

    private static TextBox BeginText(AnnotationEditorControl editor, Window window, PointD at, string content)
    {
        Invoke(editor, "SelectTool", EditorTool.Text);
        Settle(window);
        editor.SimulatePointerDown(editor.ViewportPointOf(at));
        editor.SimulatePointerUp(editor.ViewportPointOf(at));
        var box = Field<TextBox>(editor, "_activeTextBox");
        box.Text = content;
        Settle(window);
        return box;
    }

    private static ResourceDictionary ThemeResources()
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        return resources;
    }

    private static void Settle(Window window)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        window.UpdateLayout();
    }

    private static void Save(FrameworkElement element, string name)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_TEXT_EVIDENCE");
        if (string.IsNullOrEmpty(root)) return;
        Assert.True(Path.IsPathFullyQualified(root));
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(root, name));
        encoder.Save(stream);
    }

    private static BitmapSource GridImage() => DrawImage(dc =>
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 520));
        for (int y = 0; y < 520; y += 40)
            dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(0, y + .5), new Point(900, y + .5));
        for (int x = 0; x < 900; x += 80)
            dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(x + .5, 0), new Point(x + .5, 520));
    });

    private static BitmapSource OrderImage() => DrawImage(dc =>
    {
        dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 900, 520));
        for (int y = 20; y < 520; y += 32)
        {
            dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(0, y + 24), new Point(900, y + 24));
            dc.DrawText(new FormattedText($"Order reference / 주문 항목 {y}:   1200 × 800 px", CultureInfo.GetCultureInfo("ko-KR"),
                FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 15, Brushes.Black, 1), new Point(20, y));
        }
    });

    private static BitmapSource DrawImage(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(900, 520, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Members)!.GetValue(owner)!;
    private static void Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Members)!.Invoke(owner, args);

    private sealed class PlainHost : IDisposable
    {
        internal Window Window { get; }

        internal PlainHost(AnnotationEditorControl editor, int width, int height, ResizeMode resizeMode)
        {
            Window = new Window
            {
                Content = editor, Resources = ThemeResources(), Width = width, Height = height,
                WindowStyle = WindowStyle.None, ResizeMode = resizeMode, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            Window.Show();
            Settle(Window);
        }

        public void Dispose() => Window.Close();
    }
}
