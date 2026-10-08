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
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
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
            var actions = ((StackPanel)card.Content).Children.OfType<UniformGrid>().Single();
            Assert.Equal(2, actions.Columns);
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
        string bad = Path.Combine(Path.GetTempPath(), "MyCapture-invalid-image-" + Guid.NewGuid().ToString("N") + ".png");
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
            image => { outputs.Add(image); return Task.FromResult(true); });
        Assert.True(actions.CopyAsync().GetAwaiter().GetResult());
        actions.PinAsync().GetAwaiter().GetResult();
        Assert.True(actions.SaveAsync().GetAwaiter().GetResult());
        string exported = actions.ExportAsync().GetAwaiter().GetResult();
        outputs.Add(ImageCodec.TryLoad(exported)!);
        Assert.Equal(4, outputs.Count);
        Assert.All(outputs, bitmap => Assert.Equal(Pixels(current), Pixels(bitmap)));
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
