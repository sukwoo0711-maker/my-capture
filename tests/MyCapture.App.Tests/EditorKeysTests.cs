using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Editor shortcuts typed while a Korean IME is in Hangul mode, and keyboard focus around
/// the inspector inputs and the live text box.
/// </summary>
/// <remarks>
/// WPF reports a letter composed by the IME as <see cref="Key.ImeProcessed"/> and keeps the
/// physical key in <see cref="KeyEventArgs.ImeProcessedKey"/>. The tests build that exact
/// event with the framework's own internal marker and send it through the editor window's
/// tunnelling route, which is what a real Hangul-mode key press does.
/// </remarks>
public sealed class EditorKeysTests
{
    public static TheoryData<string> UnmodifiedAnnotationLetters()
    {
        var data = new TheoryData<string>();
        foreach (ShortcutDefinition definition in ShortcutCatalog.All.Where(item => item.Scope == ShortcutScope.Annotation
            && item.Modifiers == ModifierKeys.None && item.Key is >= Key.A and <= Key.Z))
        {
            data.Add(definition.Command);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnmodifiedAnnotationLetters))]
    public void HangulModeLetter_RunsTheSameAnnotationCommandAsTheLatinKey(string command) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        int hits = 0;
        Dictionary<string, Action> actions = Field<Dictionary<string, Action>>(host.Editor, "_shortcutActions");
        Action original = actions[command];
        actions[command] = () => { hits++; original(); };
        Key key = ShortcutCatalog.Get(ShortcutScope.Annotation, command).Key;

        Assert.True(Press(host.Editor, key, ime: true), $"{command} ({key}) in Hangul mode was not handled.");
        Assert.Equal(1, hits);
        if (Enum.TryParse(command, out EditorTool tool))
        {
            Assert.Equal(tool, Controller(host.Editor).Tool);
        }
    });

    [Fact]
    public void ColorInputKeepsLettersAndEnterAppliesThenReturnsShortcutsToCanvas() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        TextBox color = ColorInput(host.Editor);
        Assert.True(color.Focus());

        Assert.False(Press(color, Key.T, ime: true));
        Assert.False(Press(color, Key.T, ime: false));
        Assert.Equal(EditorTool.Rectangle, controller.Tool);

        color.Text = "#FF3B82F6";
        Assert.True(Press(color, Key.Enter, ime: false));
        Assert.Equal(ColorRgba.FromRgb(0x3B, 0x82, 0xF6), controller.StrokeColor);
        Assert.Same(host.Editor, Keyboard.FocusedElement);
        Assert.True(Press(host.Editor, Key.T, ime: true));
        Assert.Equal(EditorTool.Text, controller.Tool);
    });

    [Fact]
    public void ColorInputEscapeRestoresCurrentColorAndReturnsShortcutsToCanvas() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        TextBox color = ColorInput(host.Editor);
        Assert.True(color.Focus());
        color.Text = "#zz";

        Assert.True(Press(color, Key.Escape, ime: false));
        Assert.Equal(AnnotationEditorControl.ColorCode(controller.StrokeColor), color.Text);
        Assert.Same(host.Editor, Keyboard.FocusedElement);
        Assert.True(Press(host.Editor, Key.E, ime: true));
        Assert.Equal(EditorTool.Ellipse, controller.Tool);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FocusedSliderStillLetsLettersSelectTools(bool ime) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        Slider thickness = Field<Slider>(host.Editor, "_thicknessSlider");
        Assert.True(thickness.Focus());

        Assert.True(Press(thickness, Key.T, ime));
        Assert.Equal(EditorTool.Text, controller.Tool);
    });

    [Fact]
    public void LiveTextKeepsCompositionKeysAndPlainEscapeCommitsThenShortcutsWork() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        host.Window.UpdateLayout();
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        Assert.Same(box, Keyboard.FocusedElement);

        Assert.False(Press(box, Key.T, ime: true));
        Assert.False(Press(box, Key.T, ime: false));
        Assert.False(Press(box, Key.Escape, ime: true));
        Assert.Same(box, Field<TextBox?>(host.Editor, "_activeTextBox"));

        box.Text = "확인";
        Assert.True(Press(box, Key.Escape, ime: false));
        Assert.Null(Field<TextBox?>(host.Editor, "_activeTextBox"));
        Assert.Equal("확인", Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items)).Text);
        Assert.Same(host.Editor, Keyboard.FocusedElement);

        Assert.True(Press(host.Editor, Key.R, ime: true));
        Assert.Equal(EditorTool.Rectangle, controller.Tool);
    });

    [Fact]
    public void CanvasDisablesTheImeWhileTextInputsKeepIt() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        // WPF reads the setting from the focused element itself, so every focusable
        // non-text control carries it rather than inheriting it from the canvas.
        Assert.False(InputMethod.GetIsInputMethodEnabled(host.Editor));
        Assert.False(InputMethod.GetIsInputMethodEnabled(Field<Slider>(host.Editor, "_thicknessSlider")));
        Assert.False(InputMethod.GetIsInputMethodEnabled(Field<ComboBox>(host.Editor, "_strokeStyleComboBox")));
        Assert.False(InputMethod.GetIsInputMethodEnabled(Field<ComboBox>(host.Editor, "_cropRatio")));
        Assert.False(InputMethod.GetIsInputMethodEnabled(Field<TextBox>(host.Editor, "_cropWidth")));
        Assert.All(Descendants(host.Editor).OfType<ButtonBase>(), button => Assert.False(InputMethod.GetIsInputMethodEnabled(button)));
        Assert.True(InputMethod.GetIsInputMethodEnabled(ColorInput(host.Editor)));

        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        Assert.True(InputMethod.GetIsInputMethodEnabled(Field<TextBox>(host.Editor, "_activeTextBox")));
    });

    [Fact]
    public void ShortcutKeyResolutionKeepsCompositionOnlyInsideTextInput()
    {
        Assert.Equal(Key.T, ShortcutInput.Resolve(Key.ImeProcessed, Key.T, textInputFocused: false));
        Assert.Equal(Key.ImeProcessed, ShortcutInput.Resolve(Key.ImeProcessed, Key.T, textInputFocused: true));
        Assert.Equal(Key.R, ShortcutInput.Resolve(Key.R, Key.None, textInputFocused: false));
        Assert.Equal(Key.Escape, ShortcutInput.Resolve(Key.Escape, Key.None, textInputFocused: true));
    }

    [Theory]
    [InlineData("In")]
    [InlineData("Out")]
    [InlineData("FrameEdit")]
    [InlineData("Gif")]
    public void VideoEditorHangulModeLetter_RunsTheSameCommandAsTheLatinKey(string command) => StaTestHost.Run(() =>
    {
        var source = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 120, Height = 80,
            ShowActivated = false, ShowInTaskbar = false,
        };
        var video = new VideoEditorWindow(new RecordingResult("unused.mp4", 3000, 15, 45, 640, 360),
            AppPaths.CreateForRoot(ValidatedFilePaths.CreateTemporaryDirectory()), NullLoggerFactory.Instance);
        try
        {
            source.Show();
            typeof(VideoEditorWindow).GetField("_mediaReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(video, true);
            int hits = 0;
            Dictionary<string, Action> actions = Field<Dictionary<string, Action>>(video, "_shortcutActions");
            actions[command] = () => hits++;
            Key key = ShortcutCatalog.Get(ShortcutScope.Video, command).Key;

            KeyEventArgs args = KeyArgs(source, key, ime: true, Keyboard.KeyDownEvent);
            video.RaiseEvent(args);

            Assert.True(args.Handled, $"{command} ({key}) in Hangul mode was not handled.");
            Assert.Equal(1, hits);
        }
        finally
        {
            typeof(VideoEditorWindow).GetField("_mediaReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(video, false);
            video.Close();
            source.Close();
        }
    });

    internal static bool Press(UIElement target, Key key, bool ime)
    {
        KeyEventArgs preview = KeyArgs(target, key, ime, Keyboard.PreviewKeyDownEvent);
        target.RaiseEvent(preview);
        if (preview.Handled)
        {
            return true;
        }

        KeyEventArgs down = KeyArgs(target, key, ime, Keyboard.KeyDownEvent);
        target.RaiseEvent(down);
        return down.Handled;
    }

    internal static KeyEventArgs KeyArgs(Visual sourceVisual, Key key, bool ime, RoutedEvent routedEvent)
    {
        PresentationSource source = PresentationSource.FromVisual(sourceVisual)
            ?? throw new InvalidOperationException("The key target must be shown.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routedEvent };
        if (ime)
        {
            typeof(KeyEventArgs).GetMethod("MarkImeProcessed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, null);
            Assert.Equal(Key.ImeProcessed, args.Key);
            Assert.Equal(key, args.ImeProcessedKey);
        }

        return args;
    }

    internal static TextBox ColorInput(AnnotationEditorControl editor) =>
        Descendants(editor).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == UiText.Get("Tools.CustomColor"));

    internal static AnnotationEditorController Controller(AnnotationEditorControl editor) =>
        Field<AnnotationEditorController>(editor, "_controller");

    internal static void Invoke(object target, string method, params object[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    internal static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    internal sealed class EditorWindowHost : IDisposable
    {
        private EditorWindowHost(AnnotationEditorWindow window) => Window = window;

        public AnnotationEditorWindow Window { get; }

        public AnnotationEditorControl Editor => Window.Editor;

        public static EditorWindowHost Create(BitmapSource? bitmap = null, AnnotationDocument? document = null)
        {
            bitmap ??= Solid(640, 360, 0x30);
            var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap, initialDocument: document)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000, Top = -4000, Width = 1000, Height = 640,
                ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            window.UpdateLayout();
            window.Editor.Focus();
            return new EditorWindowHost(window);
        }

        public void Dispose() => Window.Close();
    }

    internal static BitmapSource Solid(int width, int height, byte value)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
