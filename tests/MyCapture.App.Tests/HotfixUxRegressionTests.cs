using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using System.Reflection;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class HotfixUxRegressionTests
{
    [Theory]
    [InlineData(Key.A)] [InlineData(Key.D1)] [InlineData(Key.Space)]
    [InlineData(Key.Enter)] [InlineData(Key.Left)] [InlineData(Key.ImeProcessed)]
    public void RecorderRejectsUnmodifiedTypingAndImeSentinel(Key key) =>
        Assert.Null(HotkeyRecorder.FromKey(key, ModifierKeys.None));

    [Theory]
    [InlineData(Key.Back)] [InlineData(Key.Delete)]
    public void RecorderClearUpdatesBoundDraft(Key key) => Sta(() =>
    {
        var draft = new SettingsDraft(new AppSettings()) { ScrollingCaptureHotkey = "F8" };
        var recorder = new HotkeyRecorder { DataContext = draft };
        recorder.SetBinding(HotkeyRecorder.TextProperty, new Binding(nameof(draft.ScrollingCaptureHotkey)) { Mode = BindingMode.TwoWay });
        Assert.True(recorder.HandleRecordedKey(key, ModifierKeys.None));
        Assert.Equal(string.Empty, draft.ScrollingCaptureHotkey);
        Assert.False(recorder.HandleRecordedKey(Key.Tab, ModifierKeys.None));
        recorder.HandleRecordedKey(Key.A, ModifierKeys.None);
        Assert.Equal(string.Empty, draft.ScrollingCaptureHotkey);
        recorder.HandleRecordedKey(Key.OemQuestion, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("Ctrl+Shift+VK_0xBF", draft.ScrollingCaptureHotkey);
    });

    [Fact]
    public void HelpQuestionMarkDoesNotConsumeEditableOrPasswordInput() => Sta(() =>
    {
        Assert.False(ShortcutHelpWindow.IsHelpKey(Key.OemQuestion, ModifierKeys.Shift, new PasswordBox()));
        Assert.False(ShortcutHelpWindow.IsHelpKey(Key.OemQuestion, ModifierKeys.Shift, new TextBox()));
        Assert.True(ShortcutHelpWindow.IsHelpKey(Key.OemQuestion, ModifierKeys.Shift, new Button()));
        Assert.True(ShortcutHelpWindow.IsHelpKey(Key.F1, ModifierKeys.None, new TextBox()));
    });

    [Fact]
    public void NativeRegistrarRejectsUnsafeChordBeforeWindowsRegistration()
    {
        var registrar = new NativeHotkeyRegistrar(IntPtr.Zero);
        Assert.False(registrar.TryRegister(0x5132, new Hotkey(HotkeyModifiers.None, 0x41), out int error, out string message));
        Assert.Equal(87, error);
        Assert.Equal(UiText.Get("Hotkey.ModifierRequired"), message);
    }

    [Fact]
    public void CardUsesNativeNoActivateAndCannotStealEditorKeyboardFocus() => Sta(() =>
    {
        var editor = new Window { Content = new TextBox(), Width = 200, Height = 100 };
        var card = Card();
        try
        {
            editor.Show();
            editor.Activate();
            ((TextBox)editor.Content).Focus();
            IInputElement? before = Keyboard.FocusedElement;
            card.Show();
            card.UpdateLayout();
            Assert.False(card.ShowActivated);
            Assert.False(card.IsActive);
            Assert.Same(before, Keyboard.FocusedElement);
            Assert.NotEqual(0, GetWindowLongPtrW(new WindowInteropHelper(card).Handle, -20).ToInt64() & 0x08000000L);
            var actions = ((StackPanel)card.Content).Children.OfType<DockPanel>().First().Children.OfType<UniformGrid>().Single();
            Assert.Equal(4, actions.Columns);
            Assert.All(actions.Children.OfType<Button>(), button => Assert.False(button.Focusable));
            card.Advance(TimeSpan.FromSeconds(9), false);
            Assert.False(card.IsVisible);
        }
        finally { card.Close(); editor.Close(); }
    });

    [Fact]
    public void ApplicationMessagesSurviveAndUnexpectedDetailsRemainSeparate() => Sta(() =>
    {
        using var language = MyCapture.Core.Localization.UiText.UseLanguage("ko");
        var limit = new VideoLayerLimitException("현재 동영상의 레이어 한도에 도달했습니다.");
        Assert.Equal(limit.Message, ThemedDialog.RecoveryMessage(limit));
        var changed = UserFacingError.Mark(new InvalidOperationException(UiText.Get("Capture.DisplayChanged")));
        Assert.Equal(changed.Message, ThemedDialog.RecoveryMessage(changed));
        string bad = ValidatedFilePaths.FileWithinRoot(ValidatedFilePaths.CreateTemporaryDirectory(), "invalid.png", ".png");
        File.WriteAllText(bad, "not an image");
        var invalid = Assert.Throws<InvalidDataException>(() => VideoLayerAssets.ReadImage(bad, CancellationToken.None));
        Assert.Equal(UiText.Get("Video.InvalidImage"), ThemedDialog.RecoveryMessage(invalid));
        Assert.DoesNotContain("private-path", ThemedDialog.RecoveryMessage(new IOException("private-path")));
        var dialog = new ThemedDialog(changed.Message, "MyCapture", MessageBoxButton.OK, MessageBoxResult.OK);
        Assert.True(dialog.ShowInTaskbar);
        dialog.Close();
        string? fallback = null;
        ThemedDialog.ShowUnhandled(changed, _ => throw new InvalidOperationException("theme failure"), (text, _) => fallback = text);
        Assert.Equal(changed.Message, fallback);
    });

    [Fact]
    public void AllCardOutputsUseEditedAnnotationPixels() => Sta(() =>
    {
        BitmapSource original = Image();
        var region = new RectD(0, 0, original.PixelWidth, original.PixelHeight);
        var document = AnnotationDocument.CreateFor(original.PixelWidth, original.PixelHeight);
        document.Items.Add(new RectangleAnnotation { Rect = new RectD(3, 3, 20, 20), Stroke = ColorRgba.FromRgb(255, 0, 0), StrokeThickness = 4 });
        var editor = new AnnotationEditorControl(new FrozenFrame(original, region, null, 0), region, original, document, null);
        BitmapSource current = editor.RenderCurrentImageAsync().GetAwaiter().GetResult();
        Assert.False(Pixels(original).SequenceEqual(Pixels(current)));
        List<BitmapSource> outputs = [];
        var actions = new QuickAccessImageActions(() => Task.FromResult(current),
            image => { outputs.Add(image); return Task.FromResult(true); }, outputs.Add,
            image => { outputs.Add(image); return Task.FromResult(true); },
            exportService: new GalleryDragExportService(ValidatedFilePaths.CreateTemporaryDirectory()));
        Assert.True(actions.CopyAsync().GetAwaiter().GetResult());
        actions.PinAsync().GetAwaiter().GetResult();
        Assert.True(actions.SaveAsync().GetAwaiter().GetResult());
        string exported = actions.ExportAsync().GetAwaiter().GetResult();
        outputs.Add(ImageCodec.TryLoad(exported)!);
        Assert.Equal(4, outputs.Count);
        Assert.All(outputs, bitmap => Assert.Equal(Pixels(current), Pixels(bitmap)));
    });

    [Theory]
    [InlineData(Key.A)] [InlineData(Key.D1)] [InlineData(Key.Space)]
    [InlineData(Key.Enter)] [InlineData(Key.Left)]
    public void RecorderRejectsShiftOnlyTyping(Key key) => Assert.Null(HotkeyRecorder.FromKey(key, ModifierKeys.Shift));

    [Fact]
    public void RecorderModifierAndCancelDoNotLeaveStaleValidation() => Sta(() =>
    {
        var recorder = new HotkeyRecorder { Text = "F8" };
        TextBlock message = recorder.Children.OfType<TextBlock>().Single();
        recorder.HandleRecordedKey(Key.LWin, ModifierKeys.Windows);
        recorder.HandleRecordedKey(Key.RWin, ModifierKeys.Windows);
        Assert.Empty(message.Text);
        foreach (Key cancel in new[] { Key.Escape, Key.Tab })
        {
            recorder.HandleRecordedKey(Key.A, ModifierKeys.Shift);
            Assert.NotEmpty(message.Text);
            Assert.Equal(cancel != Key.Tab, recorder.HandleRecordedKey(cancel, ModifierKeys.None));
            Assert.Empty(message.Text);
            Assert.Equal("F8", recorder.Text);
        }
    });

    [Fact]
    public void RenderedExportsReuseTimestampSuffixAndDeleteExpiredFiles() => Sta(() =>
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        DateTimeOffset now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var exports = new GalleryDragExportService(root, () => now);
        string first = exports.PrepareExport(Image());
        string second = exports.PrepareExport(Image());
        Assert.Equal(Path.Combine(root, "MyCapture_20261008_120000.png"), first);
        Assert.Equal(Path.Combine(root, "MyCapture_20261008_120000-02.png"), second);
        File.SetLastWriteTimeUtc(first, now.UtcDateTime.AddDays(-3));
        File.SetLastWriteTimeUtc(second, now.UtcDateTime.AddDays(-1));
        exports.CleanupExpiredBestEffort(now - GalleryDragExportService.DefaultRetention);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(Pixels(Image()), Pixels(ImageCodec.TryLoad(second)!));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "MyCapture", "DragExports"), new GalleryDragExportService().StagingRoot);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CardShowsOnlyMarkedUserFacingErrors(bool userFacing) => Sta(() =>
    {
        var card = Card();
        Exception error = new InvalidOperationException("private diagnostic detail");
        if (userFacing) error = UserFacingError.Mark(new InvalidOperationException(UiText.Get("Text_D0FD12A01E4A")));
        var run = typeof(QuickAccessWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Task)run.Invoke(card, new object[] { (Func<Task<bool>>)(() => Task.FromException<bool>(error)), "QuickAccess.Copied" })!).GetAwaiter().GetResult();
        TextBlock status = ((StackPanel)card.Content).Children.OfType<TextBlock>().Single();
        Assert.Equal(UiText.Get(userFacing ? "Text_D0FD12A01E4A" : "QuickAccess.Failed"), status.Text);
        card.Close();
    });

    [Fact]
    public void CardPreviewUpdatesDimensions() => Sta(() =>
    {
        var card = Card();
        card.UpdatePreview(new CroppedBitmap(Image(), new Int32Rect(0, 0, 16, 12)));
        Assert.Equal("16 × 12", ((StackPanel)card.Content).Children.OfType<DockPanel>().Last().Children.OfType<TextBlock>().Single().Text);
        card.Close();
    });

    [Fact]
    public void CurrentImageSnapshotKeepsLiveTextAndUndoStateUntouched() => Sta(() =>
    {
        BitmapSource image = Image();
        var region = new RectD(0, 0, 32, 32);
        var editor = new AnnotationEditorControl(new FrozenFrame(image, region, null, 0), region, image, null, null);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var controller = (AnnotationEditorController)typeof(AnnotationEditorControl).GetField("_controller", flags)!.GetValue(editor)!;
        typeof(AnnotationEditorControl).GetMethod("PlaceTextBox", flags)!.Invoke(editor, [new PointD(2, 2)]);
        var box = (TextBox)typeof(AnnotationEditorControl).GetField("_activeTextBox", flags)!.GetValue(editor)!;
        box.Text = "Edited";
        TextAnnotation live = controller.Document.Items.OfType<TextAnnotation>().Single();
        live.ZIndex = 40;
        RectD before = live.Rect;
        AnnotationDocument snapshot = editor.CreateCurrentImageSnapshot();
        TextAnnotation copy = snapshot.Items.OfType<TextAnnotation>().Single();
        Assert.Equal("Edited", copy.Text);
        Assert.Equal(0, copy.ZIndex);
        Assert.Equal(string.Empty, live.Text);
        Assert.Equal(40, live.ZIndex);
        Assert.Equal(before, live.Rect);
        Assert.True(controller.IsCreatingText);
        Assert.Same(box, typeof(AnnotationEditorControl).GetField("_activeTextBox", flags)!.GetValue(editor));
        typeof(AnnotationEditorControl).GetField("_redactionInProgress", flags)!.SetValue(editor, true);
        Assert.Throws<InvalidOperationException>(() => { _ = editor.RenderCurrentImageAsync(); });
        Assert.True(controller.IsCreatingText);
        Assert.True(controller.PerformUndo());
        Assert.Empty(controller.Document.Items);
    });

    [Fact]
    public void IsolatedClipboardWriterReceivesExactPngAndBitmapWithoutSystemClipboard() => Sta(() =>
    {
        byte[]? png = null;
        bool hasBitmap = false;
        bool copied = ClipboardImageService.CopyImageAsync(Image(), null, data =>
        {
            hasBitmap = data.GetDataPresent(DataFormats.Bitmap);
            using var bytes = new MemoryStream();
            ((Stream)data.GetData("PNG")!).CopyTo(bytes);
            png = bytes.ToArray();
        }).GetAwaiter().GetResult();
        Assert.True(copied && hasBitmap);
        using var stream = new MemoryStream(png!);
        BitmapSource bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(Pixels(Image()), Pixels(bitmap));
    });

    private static QuickAccessWindow Card() => new(Image(), () => Task.FromResult(true), () => { },
        () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));

    private static BitmapSource Image()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray();
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        image.Freeze(); return image;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
}
