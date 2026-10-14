using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Hangul-mode shortcuts through the composition route, the Hangul notice, the step-number
/// tool's purpose and preview, the rotate commands' names, and the per-tool canvas cursors.
/// </summary>
public sealed class EditorUxTests
{
    // The standard 2-set (두벌식) Korean layout, written out independently of the code under test.
    public static TheoryData<char, Key, bool> TwoSetLayout() => new()
    {
        { 'ㅂ', Key.Q, false }, { 'ㅈ', Key.W, false }, { 'ㄷ', Key.E, false }, { 'ㄱ', Key.R, false }, { 'ㅅ', Key.T, false },
        { 'ㅛ', Key.Y, false }, { 'ㅕ', Key.U, false }, { 'ㅑ', Key.I, false }, { 'ㅐ', Key.O, false }, { 'ㅔ', Key.P, false },
        { 'ㅁ', Key.A, false }, { 'ㄴ', Key.S, false }, { 'ㅇ', Key.D, false }, { 'ㄹ', Key.F, false }, { 'ㅎ', Key.G, false },
        { 'ㅗ', Key.H, false }, { 'ㅓ', Key.J, false }, { 'ㅏ', Key.K, false }, { 'ㅣ', Key.L, false }, { 'ㅋ', Key.Z, false },
        { 'ㅌ', Key.X, false }, { 'ㅊ', Key.C, false }, { 'ㅍ', Key.V, false }, { 'ㅠ', Key.B, false }, { 'ㅜ', Key.N, false },
        { 'ㅡ', Key.M, false }, { 'ㅃ', Key.Q, true }, { 'ㅉ', Key.W, true }, { 'ㄸ', Key.E, true }, { 'ㄲ', Key.R, true },
        { 'ㅆ', Key.T, true }, { 'ㅒ', Key.O, true }, { 'ㅖ', Key.P, true },
    };

    [Theory]
    [MemberData(nameof(TwoSetLayout))]
    public void EveryTwoSetJamoReadsBackAsItsKey(char jamo, Key key, bool shift)
    {
        KeyStroke stroke = Assert.Single(ShortcutInput.HangulKeystrokes(jamo.ToString())!);
        Assert.Equal(new KeyStroke(key, shift ? ModifierKeys.Shift : ModifierKeys.None), stroke);
    }

    [Fact]
    public void EverySingleLetterShortcutHasAJamoThatTypesIt()
    {
        var typed = TwoSetLayout().Select(row => (Key: (Key)row[1], Shift: (bool)row[2]))
            .Where(row => !row.Shift).Select(row => row.Key).ToHashSet();
        IEnumerable<ShortcutDefinition> letters = ShortcutCatalog.All.Where(item => item.Scope is ShortcutScope.Annotation or ShortcutScope.Video
            && item.Modifiers == ModifierKeys.None && item.Key is >= Key.A and <= Key.Z);
        Assert.NotEmpty(letters);
        Assert.All(letters, item => Assert.Contains(item.Key, typed));
    }

    [Theory]
    [InlineData("ㄳ", "R T")]
    [InlineData("ㅄ", "Q T")]
    [InlineData("ㄺ", "F R")]
    [InlineData("ㅘ", "H K")]
    [InlineData("ㅝ", "N J")]
    [InlineData("ㅢ", "M L")]
    [InlineData("사", "T K")]
    [InlineData("한", "G K S")]
    [InlineData("닭", "E K F R")]
    [InlineData("왜", "D H O")]
    [InlineData("ㅅㄱ", "T R")]
    public void CompoundJamoAndSyllablesSpellTheirKeysInOrder(string text, string keys)
    {
        IReadOnlyList<KeyStroke> strokes = ShortcutInput.HangulKeystrokes(text)!;
        Assert.Equal(keys, string.Join(' ', strokes.Select(stroke => stroke.Key)));
        Assert.All(strokes, stroke => Assert.Equal(ModifierKeys.None, stroke.Modifiers));
        Assert.Equal(new[] { new KeyStroke(Key.R, ModifierKeys.Shift), new KeyStroke(Key.K) }, ShortcutInput.HangulKeystrokes("까"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("t", false)]
    [InlineData("ㅅa", true)]
    [InlineData("ㆍ", true)]
    [InlineData("1", false)]
    public void NonHangulOrUnknownJamoHasNoKeys(string? text, bool containsHangul)
    {
        Assert.Null(ShortcutInput.HangulKeystrokes(text));
        Assert.Equal(containsHangul, ShortcutInput.ContainsHangul(text));
    }

    [Fact]
    public void NoticeConditionIsAnUnidentifiedImeKeyOutsideTextInput()
    {
        Assert.True(ShortcutInput.IsUnidentifiedImeKey(Key.ImeProcessed, Key.None, textInputFocused: false));
        Assert.False(ShortcutInput.IsUnidentifiedImeKey(Key.ImeProcessed, Key.None, textInputFocused: true));
        Assert.False(ShortcutInput.IsUnidentifiedImeKey(Key.ImeProcessed, Key.T, textInputFocused: false));
        Assert.False(ShortcutInput.IsUnidentifiedImeKey(Key.T, Key.None, textInputFocused: false));
    }

    public static TheoryData<string> AnnotationLetterCommands()
    {
        var data = new TheoryData<string>();
        foreach (ShortcutDefinition item in ShortcutCatalog.All.Where(item => item.Scope == ShortcutScope.Annotation
            && item.Modifiers == ModifierKeys.None && item.Key is >= Key.A and <= Key.Z))
        {
            data.Add(item.Command);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AnnotationLetterCommands))]
    public void JamoComposedOnTheCanvasRunsTheShortcutOnceAndEndsTheComposition(string command) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        int[] hits = CountHits(host.Editor, command);
        Key key = ShortcutCatalog.Get(ShortcutScope.Annotation, command).Key;
        char jamo = JamoFor(key);
        var composition = new ImeComposition(host.Editor, jamo.ToString());

        Assert.False(Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputStartEvent));
        Assert.Equal(1, hits[0]);
        Drain(host.Editor);
        Assert.Equal(1, composition.CompleteCalls);

        // The IME then reports the finished text: swallowed, not run again.
        composition.Finish(jamo.ToString());
        Assert.True(Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputEvent));
        Assert.Equal(1, hits[0]);
        if (Enum.TryParse(command, out EditorTool tool)) Assert.Equal(tool, Controller(host.Editor).Tool);
    });

    [Fact]
    public void ACompositionThatGrowsRunsOnlyItsNewKeys() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        int[] text = CountHits(host.Editor, "Text");
        int[] rectangle = CountHits(host.Editor, "Rectangle");
        var composition = new ImeComposition(host.Editor, "ㅅ");
        Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputStartEvent);
        composition.Update("ㅅㄱ");
        Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputUpdateEvent);

        Assert.Equal(1, text[0]);
        Assert.Equal(1, rectangle[0]);
        Assert.Equal(EditorTool.Rectangle, Controller(host.Editor).Tool);
    });

    [Fact]
    public void TextTypedFromAWmCharIsRunOnceAndSwallowed() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        int[] hits = CountHits(host.Editor, "Ellipse");
        // An IMM32-style IME delivers the finished jamo as a character; WPF wraps it in an
        // auto-completing composition and raises start then the text.
        var composition = new ImeComposition(host.Editor, "ㄷ", autoComplete: true);
        Assert.False(Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputStartEvent));
        Assert.True(Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputEvent));
        Drain(host.Editor);

        Assert.Equal(1, hits[0]);
        Assert.Equal(0, composition.CompleteCalls);
    });

    [Fact]
    public void LiveTextBoxAndColorInputKeepTheirHangul() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        TextBox color = ColorInput(host.Editor);
        Assert.True(color.Focus());
        var inColor = new ImeComposition(color, "ㅅ");
        Assert.False(Raise(color, inColor, TextCompositionManager.PreviewTextInputStartEvent));
        inColor.Finish("ㅅ");
        Assert.False(Raise(color, inColor, TextCompositionManager.PreviewTextInputEvent));
        Assert.Equal(EditorTool.Rectangle, controller.Tool);

        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        TextBox live = Field<TextBox>(host.Editor, "_activeTextBox");
        var typed = new ImeComposition(live, "ㅅ");
        Assert.False(Raise(live, typed, TextCompositionManager.PreviewTextInputStartEvent));
        Assert.Same(live, Field<TextBox?>(host.Editor, "_activeTextBox"));
        Assert.Equal(EditorTool.Select, controller.Tool);
        // No completion is queued for compositions inside text inputs.
        Drain(host.Editor);
        Assert.Equal(0, typed.CompleteCalls);
        Assert.Equal(0, inColor.CompleteCalls);
    });

    [Fact]
    public void AKeyAlreadyRunFromKeyDownIsNotRunAgainByItsJamoEcho() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        int[] hits = CountHits(host.Editor, "Rectangle");
        Assert.True(Press(host.Editor, Key.R, ime: true));
        Assert.Equal(1, hits[0]);

        var echo = new ImeComposition(host.Editor, "ㄱ", autoComplete: true);
        Raise(host.Editor, echo, TextCompositionManager.PreviewTextInputStartEvent);
        Assert.True(Raise(host.Editor, echo, TextCompositionManager.PreviewTextInputEvent));
        Assert.Equal(1, hits[0]);

        var next = new ImeComposition(host.Editor, "ㄱ", autoComplete: true);
        Raise(host.Editor, next, TextCompositionManager.PreviewTextInputStartEvent);
        Assert.Equal(2, hits[0]);

        // A plain letter proves the IME is not composing, so older echoes are dropped.
        Assert.True(Press(host.Editor, Key.R, ime: true));
        Assert.True(Press(host.Editor, Key.V, ime: false));
        Raise(host.Editor, new ImeComposition(host.Editor, "ㄱ", autoComplete: true), TextCompositionManager.PreviewTextInputStartEvent);
        Assert.Equal(4, hits[0]);
    });

    [Fact]
    public void UnidentifiedHangulKeyShowsTheNoticeOnlyWhenNothingElseRan() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        string notice = UiText.Get("EditorUx_HangulShortcutNotice");
        Assert.Equal("한글 입력 상태에서 누른 키를 확인할 수 없어 단축키를 실행하지 못했습니다. 한/영 키로 영문 입력으로 바꾼 뒤 다시 누르십시오.", notice);

        // An identified Hangul-mode key just runs.
        Assert.True(Press(host.Editor, Key.T, ime: true));
        Drain(host.Editor);
        Assert.NotEqual(notice, host.Editor.StatusText);

        // The IME took a key without naming it, but then composed ㅅ: the relay ran T, no notice.
        Assert.True(Press(host.Editor, Key.R, ime: false));
        Assert.False(PressUnidentified(host.Editor));
        Raise(host.Editor, new ImeComposition(host.Editor, "ㅅ"), TextCompositionManager.PreviewTextInputStartEvent);
        Drain(host.Editor);
        Assert.Equal(EditorTool.Text, Controller(host.Editor).Tool);
        Assert.NotEqual(notice, host.Editor.StatusText);

        // Nothing arrived after the unnamed key: the user is told why the key did nothing.
        Assert.False(PressUnidentified(host.Editor));
        Drain(host.Editor);
        Assert.Equal(notice, host.Editor.StatusText);

        // Repeats inside a few seconds do not re-announce. (A result replaces the notice first:
        // switching tools reports nothing, so a rotation stands in for "something happened".)
        Assert.True(Press(host.Editor, Key.W, ime: false));
        Assert.NotEqual(notice, host.Editor.StatusText);
        Assert.False(PressUnidentified(host.Editor));
        Drain(host.Editor);
        Assert.NotEqual(notice, host.Editor.StatusText);
    });

    [Fact]
    public void HangulThatSpellsNoKeyShowsTheNotice() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var composition = new ImeComposition(host.Editor, "ㆍ");
        Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputStartEvent);
        Assert.Equal(UiText.Get("EditorUx_HangulShortcutNotice"), host.Editor.StatusText);
        composition.Finish("ㆍ");
        Assert.True(Raise(host.Editor, composition, TextCompositionManager.PreviewTextInputEvent));
    });

    [Theory]
    [InlineData("In")]
    [InlineData("Out")]
    [InlineData("FrameEdit")]
    [InlineData("Gif")]
    public void VideoEditorRunsHangulLettersInThePreviewStageAndFromJamo(string command) => StaTestHost.Run(() =>
    {
        var source = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 120, Height = 80,
            ShowActivated = false, ShowInTaskbar = false,
        };
        var video = new VideoEditorWindow(new RecordingResult("unused.mp4", 3000, 15, 45, 640, 360),
            AppPaths.CreateForRoot(ValidatedFilePaths.CreateTemporaryDirectory()), NullLoggerFactory.Instance);
        FieldInfo ready = typeof(VideoEditorWindow).GetField("_mediaReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            source.Show();
            ready.SetValue(video, true);
            int hits = 0;
            Dictionary<string, Action> actions = Field<Dictionary<string, Action>>(video, "_shortcutActions");
            actions[command] = () => hits++;
            Key key = ShortcutCatalog.Get(ShortcutScope.Video, command).Key;

            // Text services mark an unhandled ImeProcessed preview key handled and WPF then
            // raises no KeyDown, so the letter must be taken in the preview stage.
            KeyEventArgs preview = KeyArgs(source, key, ime: true, Keyboard.PreviewKeyDownEvent);
            video.RaiseEvent(preview);
            Assert.True(preview.Handled, $"{command} ({key}) was not handled in the preview stage.");
            Assert.Equal(1, hits);

            var composition = new ImeComposition(video, JamoFor(key).ToString(), autoComplete: true);
            Raise(video, composition, TextCompositionManager.PreviewTextInputStartEvent);
            Assert.True(Raise(video, composition, TextCompositionManager.PreviewTextInputEvent));
            Assert.Equal(1, hits); // the echo of the key just handled

            Raise(video, new ImeComposition(video, JamoFor(key).ToString()), TextCompositionManager.PreviewTextInputStartEvent);
            Assert.Equal(2, hits);
        }
        finally
        {
            ready.SetValue(video, false);
            video.Close();
            source.Close();
        }
    });

    // ---- Step number -------------------------------------------------------------------

    [Fact]
    public void NextStepNumberFollowsTheHighestBadge()
    {
        var undo = new MyCapture.Core.Undo.UndoStack();
        var controller = new AnnotationEditorController(AnnotationDocument.CreateFor(400, 300), undo) { Tool = EditorTool.StepNumber };
        Assert.Equal(1, controller.NextStepNumber);
        controller.AddStepNumber(new PointD(10, 10));
        TextAnnotation second = controller.AddStepNumber(new PointD(60, 10));
        Assert.Equal("2", second.Text);
        Assert.Equal(3, controller.NextStepNumber);
        controller.SetSelected(second);
        controller.DeleteSelected();
        Assert.Equal(2, controller.NextStepNumber);
        Assert.True(controller.PerformUndo());
        Assert.Equal(3, controller.NextStepNumber);
    }

    [Fact]
    public void StepToolKeepsItsPurposeOnTheToolAndShowsOnlyValuesAndResults() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Assert.True(Press(editor, Key.N, ime: false));
        host.Window.UpdateLayout();

        // The purpose sentence lives on the tool (tooltip and help text) and nowhere else: the
        // panel shows the next number, and choosing the tool reports nothing.
        string purpose = "클릭할 때마다 1, 2, 3… 순서 번호를 붙입니다. 사용 순서나 절차를 설명할 때 씁니다.";
        var inspector = Field<Border>(editor, "_inspectorPanel");
        Assert.DoesNotContain(Descendants(inspector).OfType<TextBlock>(), text => text.IsVisible
            && (text.Text == purpose || text.Text == UiText.Get("EditorUx_StepNumberNextHint")));
        Assert.Equal(Visibility.Visible, Field<FrameworkElement>(editor, "_stepSection").Visibility);
        Assert.Contains(Descendants(Field<FrameworkElement>(editor, "_stepSection")).OfType<TextBlock>(), text => text.Text == "다음 번호");
        Assert.Equal("다음에 붙을 번호 1", editor.StepPreviewName);
        Assert.Equal(string.Empty, editor.StatusText);

        ToggleButton button = Field<Dictionary<EditorTool, ToggleButton>>(editor, "_toolButtons")[EditorTool.StepNumber];
        Assert.Equal("단계 번호 — 클릭할 때마다 1, 2, 3… 순서 번호를 붙입니다 (N)", button.ToolTip);
        Assert.Equal(purpose, AutomationProperties.GetHelpText(button));
        Assert.Equal("단계 번호 도구 (N)", AutomationProperties.GetName(button));

        controller.HitTolerance = 1;
        controller.PointerDown(new PointD(80, 80));
        controller.PointerUp(new PointD(80, 80));
        Assert.Equal("다음에 붙을 번호 2", editor.StepPreviewName);
        Assert.Equal("1번 추가 · Ctrl+Z로 취소", editor.StatusText);
        Assert.DoesNotContain("(를)", editor.StatusText, StringComparison.Ordinal);
        Assert.Equal("단계 번호 1", Field<TextBlock>(editor, "_inspectorTitle").Text);
        Assert.DoesNotContain(Descendants(inspector).OfType<TextBlock>(), text => text.IsVisible && text.Text == purpose);
        Assert.Equal(EditorCursorKind.StepNumber, editor.CanvasCursorKind);

        Assert.True(Press(editor, Key.V, ime: false));
        Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(editor, "_stepSection").Visibility);
    });

    [Fact]
    public void StepPreviewFollowsTheChosenColour() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Assert.True(Press(host.Editor, Key.N, ime: false));
        TextBox color = ColorInput(host.Editor);
        Assert.True(color.Focus());
        color.Text = "#FF3B82F6";
        Assert.True(Press(color, Key.Enter, ime: false));

        var preview = Field<Border>(host.Editor, "_stepPreview");
        Assert.Equal(Color.FromRgb(0x3B, 0x82, 0xF6), Assert.IsType<SolidColorBrush>(preview.Background).Color);
    });

    // ---- Rotate commands --------------------------------------------------------------

    [Fact]
    public void RotateCommandsNameTheTurnAndShowALabel() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.UpdateLayout();
        Button left = Field<Button>(host.Editor, "_rotateLeftButton");
        Button right = Field<Button>(host.Editor, "_rotateRightButton");
        // Rotation is in the everyday overflow and has labelled buttons while cropping.
        Assert.False(left.IsVisible);
        Assert.False(right.IsVisible);
        Assert.True(Press(host.Editor, Key.X, ime: false));
        host.Window.UpdateLayout();
        Assert.True(left.IsVisible && right.IsVisible);
        // Compared with the crop bar's Apply button, the bar's other full-height command.
        Button apply = Descendants(host.Editor).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "EditorFlowCropApply");

        Assert.Equal("이미지를 왼쪽으로 90도 회전", AutomationProperties.GetName(left));
        Assert.Equal("이미지를 오른쪽으로 90도 회전", AutomationProperties.GetName(right));
        Assert.Equal("Q", AutomationProperties.GetAcceleratorKey(left));
        Assert.Equal("W", AutomationProperties.GetAcceleratorKey(right));
        Assert.Equal("이미지를 왼쪽으로 90도 회전 (Q)", left.ToolTip);
        Assert.Equal("이미지를 오른쪽으로 90도 회전 (W)", right.ToolTip);
        Assert.Contains(Descendants(left).OfType<TextBlock>(), block => block.Text == "왼쪽 회전");
        Assert.Contains(Descendants(right).OfType<TextBlock>(), block => block.Text == "오른쪽 회전");

        // Same row height and vertical centre as the bar's other commands.
        Assert.InRange(left.ActualHeight, apply.ActualHeight - 0.5, apply.ActualHeight + 0.5);
        Assert.InRange(right.ActualHeight, apply.ActualHeight - 0.5, apply.ActualHeight + 0.5);
        double Centre(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), host.Window).Y;
        Assert.InRange(Centre(left), Centre(apply) - 0.5, Centre(apply) + 0.5);

        left.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(3, Controller(host.Editor).RotationTurns);
        right.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(0, Controller(host.Editor).RotationTurns);
    });

    [Fact]
    public void RotateIconsAreSharedSymbolsDistinctFromUndoAndRedo() => StaTestHost.Run(() =>
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var symbols = new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Symbols.xaml", UriKind.Absolute) };
        var left = Assert.IsAssignableFrom<Geometry>(symbols["Icon.RotateLeft"]);
        var right = Assert.IsAssignableFrom<Geometry>(symbols["Icon.RotateRight"]);
        Assert.NotEqual(((Geometry)symbols["Icon.Undo"]).ToString(), left.ToString());
        Assert.NotEqual(((Geometry)symbols["Icon.Redo"]).ToString(), right.ToString());
        // Drawn on the shared 20x20 grid, mirrored left to right.
        Assert.InRange(left.Bounds.Left, 0, 20);
        Assert.InRange(left.Bounds.Right, 0, 20);
        Assert.Equal(20 - left.Bounds.Right, right.Bounds.Left, 3);
    });

    // ---- Cursors ------------------------------------------------------------------------

    [Theory]
    [InlineData("Select", "Arrow")]
    [InlineData("Text", "TextBeam")]
    [InlineData("Rectangle", "Cross")]
    [InlineData("Ellipse", "Cross")]
    [InlineData("Arrow", "Cross")]
    [InlineData("Pen", "Pen")]
    [InlineData("Highlighter", "Highlighter")]
    [InlineData("StepNumber", "StepNumber")]
    [InlineData("Crop", "Crop")]
    [InlineData("Mosaic", "Mosaic")]
    [InlineData("Blur", "Blur")]
    [InlineData("Image", "Image")]
    public void EachToolHasItsOwnCanvasCursor(string toolName, string kindName)
    {
        EditorTool tool = Enum.Parse<EditorTool>(toolName);
        EditorCursorKind expected = Enum.Parse<EditorCursorKind>(kindName);
        Assert.Equal(expected, EditorCursorPolicy.Resolve(tool, default));
        Assert.Equal(EditorCursorPolicy.IsCustom(expected), EditorCursors.SystemCursor(expected, highContrast: false) is null);
        Assert.NotNull(EditorCursors.SystemCursor(expected, highContrast: true));
    }

    [Theory]
    [InlineData("TopLeft", false, "ResizeNorthwestSoutheast")]
    [InlineData("BottomRight", false, "ResizeNorthwestSoutheast")]
    [InlineData("TopRight", false, "ResizeNortheastSouthwest")]
    [InlineData("BottomLeft", false, "ResizeNortheastSouthwest")]
    [InlineData("TopCenter", true, "ResizeNorthSouth")]
    [InlineData("MiddleLeft", true, "ResizeWestEast")]
    [InlineData("None", true, "Move")]
    [InlineData("None", false, "Arrow")]
    public void SelectionCursorShowsMoveAndResize(string handle, bool overAnnotation, string expected) =>
        Assert.Equal(Enum.Parse<EditorCursorKind>(expected),
            EditorCursorPolicy.Resolve(EditorTool.Select, new EditorPointerTarget(Enum.Parse<ResizeHandle>(handle), overAnnotation)));

    [Theory]
    [InlineData("New", "Crop")]
    [InlineData("Move", "Move")]
    [InlineData("TopLeft", "ResizeNorthwestSoutheast")]
    [InlineData("TopRight", "ResizeNortheastSouthwest")]
    [InlineData("Bottom", "ResizeNorthSouth")]
    [InlineData("Left", "ResizeWestEast")]
    public void CropGripsWinOverTheTool(string gripName, string expectedName)
    {
        CropGrip grip = Enum.Parse<CropGrip>(gripName);
        EditorCursorKind expected = Enum.Parse<EditorCursorKind>(expectedName);
        Assert.Equal(expected, EditorCursorPolicy.Resolve(EditorTool.Crop, new EditorPointerTarget(CropGrip: grip)));
        Assert.Equal(expected, EditorCursorPolicy.Resolve(EditorTool.Select, new EditorPointerTarget(ResizeHandle.TopLeft, true, grip)));
    }

    [Theory]
    [InlineData(32, 1.0, 32)]
    [InlineData(32, 1.25, 40)]
    [InlineData(32, 1.5, 48)]
    [InlineData(32, 2.0, 64)]
    [InlineData(48, 1.5, 72)]
    [InlineData(16, 1.0, 32)]
    [InlineData(512, 1.0, 256)]
    [InlineData(32, double.NaN, 32)]
    public void CursorSizeFollowsPointerSizeAndMonitorScale(int baseSize, double scale, int expected) =>
        Assert.Equal(expected, EditorCursors.PixelSize(baseSize, scale));

    public static TheoryData<string, int> CustomCursorSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (EditorCursorKind kind in Enum.GetValues<EditorCursorKind>().Where(EditorCursorPolicy.IsCustom))
        {
            foreach (int size in new[] { 32, 40, 48, 64, 96 }) data.Add(kind.ToString(), size);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CustomCursorSizes))]
    public void ToolCursorHotSpotSitsOnTheDrawnTipAtEverySize(string kindName, int size) => StaTestHost.Run(() =>
    {
        EditorCursorKind kind = Enum.Parse<EditorCursorKind>(kindName);
        EditorCursorImage image = EditorCursors.Render(kind, size, 7, ColorRgba.FromRgb(0xEF, 0x44, 0x44), EditorCursorStyle.White);
        Assert.Equal(size, image.Bitmap.PixelWidth);
        Assert.Equal(size, image.Bitmap.PixelHeight);
        byte[] pixels = Pixels(image.Bitmap);
        Assert.True(Alpha(pixels, size, image.HotSpotX, image.HotSpotY) >= 160,
            $"{kind} at {size}px: hot spot ({image.HotSpotX},{image.HotSpotY}) is not on the drawn tip.");
        // The hot spot scales with the cursor (the 32 px design anchor times the scale).
        EditorCursorImage reference = EditorCursors.Render(kind, 32, 7, ColorRgba.FromRgb(0xEF, 0x44, 0x44), EditorCursorStyle.White);
        Assert.InRange(image.HotSpotX, Math.Floor(reference.HotSpotX * size / 32.0) - 1, Math.Ceiling((reference.HotSpotX + 1) * size / 32.0));
        Assert.InRange(image.HotSpotY, Math.Floor(reference.HotSpotY * size / 32.0) - 1, Math.Ceiling((reference.HotSpotY + 1) * size / 32.0));
        // Both a dark outline and a light body, so the cursor reads on any capture.
        Assert.Contains(Enumerable.Range(0, size * size), i => pixels[(i * 4) + 3] > 200 && pixels[i * 4] < 40 && pixels[(i * 4) + 2] < 40);
        Assert.Contains(Enumerable.Range(0, size * size), i => pixels[(i * 4) + 3] > 200 && pixels[i * 4] > 220 && pixels[(i * 4) + 1] > 220);

        byte[] file = EditorCursors.BuildCursorFile(image);
        using var reader = new BinaryReader(new MemoryStream(file));
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(2, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        Assert.Equal(size >= 256 ? 0 : size, reader.ReadByte());
        Assert.Equal(size >= 256 ? 0 : size, reader.ReadByte());
        reader.ReadUInt16();
        Assert.Equal(image.HotSpotX, reader.ReadUInt16());
        Assert.Equal(image.HotSpotY, reader.ReadUInt16());
        int bytes = reader.ReadInt32();
        Assert.Equal(22, reader.ReadInt32());
        Assert.Equal(file.Length - 22, bytes);
        // AND mask: the empty corner is transparent, the hot spot is not.
        int maskStride = ((size + 31) / 32) * 4;
        int maskStart = 22 + 40 + (size * size * 4);
        bool Masked(int x, int y) => (file[maskStart + ((size - 1 - y) * maskStride) + (x / 8)] & (0x80 >> (x % 8))) != 0;
        Assert.True(Masked(0, size - 1));
        Assert.False(Masked(image.HotSpotX, image.HotSpotY));

        using var cursor = new Cursor(new MemoryStream(file), scaleWithDpi: false);
    });

    [Fact]
    public void ToolCursorsAreCachedAndTheStepCursorFollowsNumberAndColour() => StaTestHost.Run(() =>
    {
        var red = ColorRgba.FromRgb(0xEF, 0x44, 0x44);
        EditorCursorStyle white = EditorCursorStyle.White;
        Cursor pen = EditorCursors.Get(EditorCursorKind.Pen, 32, 0, red, highContrast: false, white);
        Assert.Same(pen, EditorCursors.Get(EditorCursorKind.Pen, 32, 5, ColorRgba.White, highContrast: false, white));
        Assert.NotSame(pen, EditorCursors.Get(EditorCursorKind.Pen, 48, 0, red, highContrast: false, white));
        Assert.NotSame(pen, EditorCursors.Get(EditorCursorKind.Pen, 32, 0, red, highContrast: false, EditorCursorStyle.FromSettings(1, null)));
        Assert.NotSame(Cursors.Cross, pen);

        Cursor one = EditorCursors.Get(EditorCursorKind.StepNumber, 32, 1, red, highContrast: false, white);
        Assert.NotSame(one, EditorCursors.Get(EditorCursorKind.StepNumber, 32, 2, red, highContrast: false, white));
        Assert.NotSame(one, EditorCursors.Get(EditorCursorKind.StepNumber, 32, 1, ColorRgba.FromRgb(0x3B, 0x82, 0xF6), highContrast: false, white));
        Assert.Same(Cursors.Pen, EditorCursors.Get(EditorCursorKind.Pen, 32, 0, red, highContrast: true, white));
        Assert.Same(Cursors.Cross, EditorCursors.Get(EditorCursorKind.StepNumber, 32, 1, red, highContrast: true, white));

        // The badge digits differ, so the rendered images differ.
        Assert.False(Pixels(EditorCursors.Render(EditorCursorKind.StepNumber, 32, 1, red, white).Bitmap)
            .SequenceEqual(Pixels(EditorCursors.Render(EditorCursorKind.StepNumber, 32, 2, red, white).Bitmap)));
    });

    [Theory]
    [InlineData(null, null, 0xFF, 0xFF, 0xFF, true)]
    [InlineData(0, null, 0xFF, 0xFF, 0xFF, true)]
    [InlineData(2, null, 0xFF, 0xFF, 0xFF, true)]
    [InlineData(1, null, 0x0B, 0x0F, 0x17, false)]
    [InlineData(6, 0x000400FF, 0xFF, 0x00, 0x04, true)]
    [InlineData(6, 0x00402010, 0x10, 0x20, 0x40, false)]
    public void ToolCursorsTakeThePointerStyle(int? type, int? colorRef, int r, int g, int b, bool darkOutline)
    {
        EditorCursorStyle style = EditorCursorStyle.FromSettings(type, colorRef);
        Assert.Equal(Color.FromRgb((byte)r, (byte)g, (byte)b), style.Body);
        Assert.Equal(darkOutline ? EditorCursorStyle.Ink : Colors.White, style.Outline);
    }

    [Fact]
    public void CustomPointerColourFillsTheToolBody() => StaTestHost.Run(() =>
    {
        // This PC's setting when the feature was written: custom colour 262399 (red).
        EditorCursorStyle red = EditorCursorStyle.FromSettings(6, 262399);
        byte[] pixels = Pixels(EditorCursors.Render(EditorCursorKind.Pen, 64, 0, ColorRgba.White, red).Bitmap);
        Assert.Contains(Enumerable.Range(0, 64 * 64), i => pixels[(i * 4) + 3] == 255 && pixels[(i * 4) + 2] > 240 && pixels[(i * 4) + 1] < 20);
        Assert.DoesNotContain(Enumerable.Range(0, 64 * 64), i => pixels[(i * 4) + 3] == 255 && pixels[i * 4] > 240 && pixels[(i * 4) + 1] > 240 && pixels[(i * 4) + 2] > 240);
    });

    [Fact]
    public void CanvasCursorFollowsTheToolAndStaysOffTheRestOfTheEditor() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        var viewport = (FrameworkElement)editor.PointerInputElement;
        foreach ((Key key, EditorCursorKind kind) in new[]
        {
            (Key.P, EditorCursorKind.Pen), (Key.H, EditorCursorKind.Highlighter), (Key.T, EditorCursorKind.TextBeam),
            (Key.R, EditorCursorKind.Cross), (Key.N, EditorCursorKind.StepNumber), (Key.M, EditorCursorKind.Mosaic),
            (Key.B, EditorCursorKind.Blur), (Key.X, EditorCursorKind.Crop), (Key.V, EditorCursorKind.Arrow),
        })
        {
            Assert.True(Press(editor, key, ime: false));
            Assert.Equal(kind, editor.CanvasCursorKind);
            Assert.NotNull(viewport.Cursor);
            Assert.Same(viewport.Cursor, editor.CanvasCursor);
            if (!EditorCursorPolicy.IsCustom(kind)) Assert.Same(EditorCursors.SystemCursor(kind, false), viewport.Cursor);
        }

        // The rail, command bar and inspector keep the normal pointer.
        Assert.Null(editor.Cursor);
    });

    // ---- Helpers ------------------------------------------------------------------------

    private static char JamoFor(Key key) => TwoSetLayout().First(row => (Key)row[1] == key && !(bool)row[2])[0] is char jamo ? jamo : '?';

    /// <summary>Counts runs of <paramref name="command"/>; the count is in element 0.</summary>
    private static int[] CountHits(AnnotationEditorControl editor, string command)
    {
        Dictionary<string, Action> actions = Field<Dictionary<string, Action>>(editor, "_shortcutActions");
        Action original = actions[command];
        var counter = new int[1];
        actions[command] = () => { counter[0]++; original(); };
        return counter;
    }

    private static bool Raise(UIElement target, TextComposition composition, RoutedEvent routedEvent)
    {
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = routedEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static bool PressUnidentified(UIElement target)
    {
        // A key the IME took without reporting which one: ImeProcessed with no real key.
        KeyEventArgs args = KeyArgs(target, Key.None, ime: false, Keyboard.PreviewKeyDownEvent);
        typeof(KeyEventArgs).GetMethod("MarkImeProcessed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, null);
        Assert.Equal(Key.ImeProcessed, args.Key);
        Assert.Equal(Key.None, args.ImeProcessedKey);
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static void Drain(DispatcherObject owner) =>
        owner.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private static byte[] Pixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    private static byte Alpha(byte[] pixels, int size, int x, int y) => pixels[(((y * size) + x) * 4) + 3];

    /// <summary>A composition as a Korean IME reports it through WPF.</summary>
    private sealed class ImeComposition : TextComposition
    {
        public ImeComposition(IInputElement source, string text, bool autoComplete = false)
            : base(InputManager.Current, source, autoComplete ? text : string.Empty,
                autoComplete ? TextCompositionAutoComplete.On : TextCompositionAutoComplete.Off)
        {
            if (!autoComplete) CompositionText = text;
        }

        public int CompleteCalls { get; private set; }

        public void Update(string text) => CompositionText = text;

        public void Finish(string result)
        {
            CompositionText = string.Empty;
            Text = result;
        }

        public override void Complete() => CompleteCalls++;
    }
}
