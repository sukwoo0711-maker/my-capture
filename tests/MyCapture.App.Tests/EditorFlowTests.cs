using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The image editor's flow: Esc only steps back, closing asks before dropping edits, Copy keeps
/// the editor open, crop is a mode of the command bar with rotation inside it, the tool rail is
/// grouped, and the editor confirms a capture that is already saved and copied.
/// </summary>
public sealed class EditorFlowTests
{
    // ---- Esc ---------------------------------------------------------------------------

    [Fact]
    public void EscapeFinishesTypingThenClearsTheSelectionThenReturnsToSelectAndNeverCloses() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);

        // 1. Typing: Esc keeps the caption and ends the typing.
        Invoke(editor, "PlaceTextBox", new PointD(40, 40));
        Field<TextBox>(editor, "_activeTextBox").Text = "메모";
        Assert.True(Press(Field<TextBox>(editor, "_activeTextBox"), Key.Escape, ime: false));
        Assert.Null(Field<TextBox?>(editor, "_activeTextBox"));
        Assert.Equal("메모", Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items)).Text);

        // 2. A selection: Esc clears it and keeps the tool.
        controller.SetSelected(controller.Document.Items[0]);
        Assert.True(Press(editor, Key.R, ime: false));
        controller.SetSelected(controller.Document.Items[0]);
        Assert.True(Press(editor, Key.Escape, ime: false));
        Assert.Null(controller.Selected);
        Assert.Equal(EditorTool.Rectangle, controller.Tool);
        Assert.Equal(UiText.Get("EditorFlow_SelectionCleared"), editor.StatusText);

        // 3. A drawing tool: Esc returns to the select tool.
        Assert.True(Press(editor, Key.Escape, ime: false));
        Assert.Equal(EditorTool.Select, controller.Tool);

        // 4. Nothing left to step back from: Esc does nothing, and at no step did it close.
        Assert.True(Press(editor, Key.Escape, ime: false));
        Assert.Equal(EditorTool.Select, controller.Tool);
        Assert.Single(controller.Document.Items);
        Assert.True(host.Window.IsVisible);
        Assert.False(host.Window.WasCommitted);
    });

    [Fact]
    public void EscapeInCropModeCancelsTheFrameAndKeepsTheEditorOpen() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Assert.True(Press(host.Editor, Key.X, ime: false));
        host.Editor.PendingCrop!.SetWidth(200);
        Assert.True(Press(host.Editor, Key.Escape, ime: false));
        Assert.Null(host.Editor.PendingCrop);
        Assert.False(host.Editor.IsCropCommandBarVisible);
        Assert.Equal(640, host.Editor.DisplayedBitmap.PixelWidth);
        Assert.True(host.Window.IsVisible);
    });

    // ---- Unsaved edits and closing ---------------------------------------------------

    [Fact]
    public void UnsavedChangesFollowTheUndoStackTypingAndAnOpenCropFrame() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Assert.False(editor.HasUnsavedChanges);

        // An empty caption being typed is not an edit; one with text is.
        Invoke(editor, "PlaceTextBox", new PointD(40, 40));
        Assert.False(editor.HasUnsavedChanges);
        Field<TextBox>(editor, "_activeTextBox").Text = "a";
        Assert.True(editor.HasUnsavedChanges);
        Field<TextBox>(editor, "_activeTextBox").Text = string.Empty;
        Invoke(editor, "CommitActiveText");
        Assert.False(editor.HasUnsavedChanges);

        // A drawn shape is; undoing back to the opened state is not.
        AddRectangle(controller);
        Assert.True(editor.HasUnsavedChanges);
        Assert.True(controller.PerformUndo());
        Assert.False(editor.HasUnsavedChanges);

        // An open crop frame counts only once it differs from the whole image.
        Assert.True(Press(editor, Key.X, ime: false));
        Assert.False(editor.HasUnsavedChanges);
        editor.PendingCrop!.SetWidth(200);
        Assert.True(editor.HasUnsavedChanges);
        editor.CancelPendingCrop();

        // A rotation is an edit.
        Assert.True(Press(editor, Key.Q, ime: false));
        Assert.True(editor.HasUnsavedChanges);
    });

    [Fact]
    public void ClosingWithoutChangesNeverAsks() => StaTestHost.Run(() =>
    {
        foreach (bool titleBar in new[] { false, true })
        {
            using EditorWindowHost host = EditorWindowHost.Create();
            int asked = 0, cancelled = 0;
            host.Window.ConfirmClose = () => { asked++; return EditorCloseChoice.Cancel; };
            host.Window.Cancelled += (_, _) => cancelled++;
            if (titleBar) SendCloseCommand(host.Window); else host.Window.RequestClose();
            Assert.False(host.Window.IsVisible);
            Assert.Equal(0, asked);
            Assert.Equal(1, cancelled);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingWithChangesAsksAndCancelKeepsEverything(bool titleBar) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AddRectangle(Controller(host.Editor));
        int asked = 0;
        host.Window.ConfirmClose = () => { asked++; return EditorCloseChoice.Cancel; };
        if (titleBar) SendCloseCommand(host.Window); else ClickCommand(host.Editor, "EditorFlowCancel");
        Drain(host.Editor);
        Assert.Equal(1, asked);
        Assert.True(host.Window.IsVisible);
        Assert.Single(Controller(host.Editor).Document.Items);
        Assert.True(host.Editor.HasUnsavedChanges);
    });

    [Fact]
    public void DontSaveClosesWithoutCommitting() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AddRectangle(Controller(host.Editor));
        int commits = 0, cancelled = 0;
        host.Window.CommitRequested = _ => { commits++; return Task.FromResult(true); };
        host.Window.Cancelled += (_, _) => cancelled++;
        host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
        SendCloseCommand(host.Window);
        Assert.False(host.Window.IsVisible);
        Assert.Equal(0, commits);
        Assert.Equal(1, cancelled);
        Assert.False(host.Window.WasCommitted);
    });

    [Fact]
    public void SaveAndCloseRunsTheDoneCommitAndClosesOnlyWhenItSucceeds() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AddRectangle(Controller(host.Editor));
        var actions = new List<EditorCommitAction>();
        bool succeed = false;
        host.Window.CommitRequested = result => { actions.Add(result.Action); return Task.FromResult(succeed); };
        host.Window.ConfirmClose = () => EditorCloseChoice.SaveAndClose;

        // A failed save keeps the editor and the edit, and says so.
        host.Window.RequestClose();
        Drain(host.Editor);
        Assert.Equal(new[] { EditorCommitAction.Done }, actions);
        Assert.True(host.Window.IsVisible);
        Assert.Equal(UiText.Get("Text_06F58809C083"), host.Editor.StatusText);

        succeed = true;
        int committed = 0;
        host.Window.Committed += (_, _) => committed++;
        SendCloseCommand(host.Window);
        Drain(host.Editor);
        Assert.Equal(new[] { EditorCommitAction.Done, EditorCommitAction.Done }, actions);
        Assert.False(host.Window.IsVisible);
        Assert.True(host.Window.WasCommitted);
        Assert.Equal(1, committed);
    });

    [Fact]
    public void ClosingFromCodeDoesNotAsk() => StaTestHost.Run(() =>
    {
        // Retake, the owner closing and application exit close from code; WPF ignores a
        // cancelled close during exit, so only user closes are confirmed.
        using EditorWindowHost host = EditorWindowHost.Create();
        AddRectangle(Controller(host.Editor));
        int asked = 0;
        host.Window.ConfirmClose = () => { asked++; return EditorCloseChoice.Cancel; };
        host.Window.Close();
        Assert.False(host.Window.IsVisible);
        Assert.Equal(0, asked);
    });

    [Fact]
    public void CloseDialogOffersSaveDontSaveAndCancelInWinUiOrder() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        foreach ((string id, EditorCloseChoice expected) in new[] { ("save", EditorCloseChoice.SaveAndClose), ("dont", EditorCloseChoice.DontSave), ("cancel", EditorCloseChoice.Cancel) })
        {
            var dialog = new EditorCloseDialog
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000, ShowActivated = false,
            };
            dialog.Show();
            Button[] buttons = Descendants(dialog).OfType<Button>().ToArray();
            Assert.Equal(new[] { "저장하고 닫기", "저장 안 함", "취소" }, buttons.Select(button => (string)button.Content));
            Assert.True(dialog.SaveButton.IsDefault);
            Assert.True(dialog.CancelButton.IsCancel);
            Assert.False(dialog.DontSaveButton.IsDefault || dialog.DontSaveButton.IsCancel);
            Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "편집한 내용을 저장하시겠습니까?");
            Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "저장하지 않으면 이번에 편집한 내용이 사라집니다.");
            Button pressed = id switch { "save" => dialog.SaveButton, "dont" => dialog.DontSaveButton, _ => dialog.CancelButton };
            pressed.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(expected, dialog.Choice);
            Assert.False(dialog.IsVisible);
        }
    });

    // ---- Copy and Done -----------------------------------------------------------------

    [Fact]
    public void CopyLeavesTheEditorOpenAndReportsTheResult() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        AddRectangle(Controller(host.Editor));
        var results = new Queue<bool>([true, false]);
        var actions = new List<EditorCommitAction>();
        host.Window.CommitRequested = result => { actions.Add(result.Action); return Task.FromResult(results.Dequeue()); };
        int completed = 0;
        host.Window.Committed += (_, _) => completed++;

        ClickCommand(host.Editor, "EditorFlowCopy");
        Assert.Equal("클립보드에 복사됨", host.Editor.StatusText);
        Assert.True(host.Window.IsVisible);
        Assert.True(host.Editor.IsHitTestVisible);

        Assert.True(host.Editor.HandleShortcut(Key.C, ModifierKeys.Control));
        Assert.Equal(UiText.Get("Text_3D1E714B56CA"), host.Editor.StatusText);
        Assert.True(host.Window.IsVisible);

        Assert.Equal(new[] { EditorCommitAction.CopyToClipboard, EditorCommitAction.CopyToClipboard }, actions);
        Assert.Equal(0, completed);
        // Copying is not saving: closing still asks.
        Assert.True(host.Editor.HasUnsavedChanges);
    });

    [Fact]
    public void FinishingCommandsSayWhatTheyDoAndFollowWinUiOrder() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        Button save = Command(host.Editor, "EditorFlowSaveToFile");
        Assert.Equal("파일로 저장", AutomationProperties.GetName(save));
        Assert.Contains(Descendants(save).OfType<TextBlock>(), text => text.Text == "파일로 저장");
        Assert.Equal("이미지를 파일로 저장 (Ctrl+S)", save.ToolTip);
        Assert.Equal("라이브러리에 저장하고 클립보드에 복사한 뒤 닫기 (Ctrl+Enter)", Command(host.Editor, "EditorFlowDone").ToolTip);
        Button cancel = Command(host.Editor, "EditorFlowCancel");
        Assert.Equal("편집기 닫기", cancel.ToolTip);
        Assert.Equal(string.Empty, AutomationProperties.GetAcceleratorKey(cancel));

        // Finishing commands in WinUI order: the action, then the way out.
        string[] order = Descendants(host.Editor).OfType<Button>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => id is "EditorFlowSaveToFile" or "EditorFlowCopy" or "EditorFlowDone" or "EditorFlowCancel").ToArray();
        Assert.Equal(new[] { "EditorFlowSaveToFile", "EditorFlowCopy", "EditorFlowDone", "EditorFlowCancel" }, order);
    });

    // ---- Crop mode --------------------------------------------------------------------

    [Fact]
    public void CropModeSwapsTheCommandBarAndKeepsRotationInsideIt() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Button rotateLeft = Field<Button>(editor, "_rotateLeftButton");
        Button crop = Command(editor, "Capture410Crop");
        Assert.True(crop.IsVisible);
        Assert.False(rotateLeft.IsVisible);
        Assert.False(editor.IsCropCommandBarVisible);

        // Q and W rotate from anywhere, crop mode or not.
        Assert.True(Press(editor, Key.W, ime: false));
        Assert.Equal(1, controller.RotationTurns);

        crop.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        host.Window.UpdateLayout();
        Assert.True(editor.IsCropCommandBarVisible);
        Assert.NotNull(editor.PendingCrop);
        Assert.False(crop.IsVisible);
        Assert.True(rotateLeft.IsVisible);
        Assert.True(Command(editor, "EditorFlowCropApply").IsVisible);
        Assert.True(Command(editor, "EditorFlowCropCancel").IsVisible);
        Assert.True(Command(editor, "Capture410AutoTrim").IsVisible);
        Assert.True(Field<ComboBox>(editor, "_cropRatio").IsVisible);
        // The finishing commands and the panel's former Apply/Cancel are gone in crop mode.
        Assert.False(Command(editor, "EditorFlowDone").IsVisible);
        Assert.DoesNotContain(Descendants(Field<Border>(editor, "_inspectorPanel")).OfType<Button>(), button => button.IsVisible);

        // Rotating inside crop mode starts the frame over on the turned image, keeping the ratio.
        Field<ComboBox>(editor, "_cropRatio").SelectedIndex = 2; // 1:1
        rotateLeft.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(0, controller.RotationTurns);
        Assert.NotNull(editor.PendingCrop);
        Assert.Equal(editor.PendingCrop!.Region.Width, editor.PendingCrop.Region.Height, 3);
        Assert.True(Press(editor, Key.Q, ime: false));
        Assert.Equal(3, controller.RotationTurns);
        Assert.True(editor.IsCropCommandBarVisible);

        Command(editor, "EditorFlowCropApply").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Null(editor.PendingCrop);
        Assert.False(editor.IsCropCommandBarVisible);
        Assert.Equal(editor.DisplayedBitmap.PixelWidth, editor.DisplayedBitmap.PixelHeight);
        Assert.Equal(EditorTool.Select, controller.Tool);
    });

    // ---- Tool rail ----------------------------------------------------------------------

    [Fact]
    public void ToolRailIsGroupedByIntentWithRulesBetweenGroupsAndNoCrop() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        IReadOnlyList<IReadOnlyList<EditorTool>> groups = host.Editor.ToolGroups;
        Assert.Equal(
            new[]
            {
                new[] { EditorTool.Select },
                new[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Arrow },
                new[] { EditorTool.Pen, EditorTool.Highlighter },
                new[] { EditorTool.Text, EditorTool.StepNumber },
                new[] { EditorTool.Mosaic, EditorTool.Blur },
                new[] { EditorTool.Image },
            },
            groups.Select(group => group.ToArray()).ToArray());
        Assert.DoesNotContain(EditorTool.Crop, groups.SelectMany(group => group));
        Assert.DoesNotContain(EditorTool.Crop, Field<Dictionary<EditorTool, ToggleButton>>(host.Editor, "_toolButtons").Keys);

        var rail = Descendants(host.Editor).OfType<StackPanel>().Single(panel => panel.Name == "AnnotationToolGroups");
        UIElement[] children = rail.Children.Cast<UIElement>().ToArray();
        // group, rule, group, rule, … group: each group in its own two-column grid.
        Assert.Equal(groups.Count * 2 - 1, children.Length);
        for (int index = 0; index < children.Length; index++)
        {
            if (index % 2 == 0)
            {
                var grid = Assert.IsType<UniformGrid>(children[index]);
                Assert.Equal(2, grid.Columns);
                Assert.Equal(groups[index / 2].Count, grid.Children.Count);
            }
            else
            {
                var rule = Assert.IsType<Border>(children[index]);
                Assert.Equal("AnnotationToolGroupSeparator", rule.Tag);
                Assert.Equal(1, rule.Height);
            }
        }
    });

    // ---- Statuses -----------------------------------------------------------------------

    [Fact]
    public void OpeningAfterACaptureSaysItIsAlreadySavedAndCopied() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        Assert.Equal(string.Empty, host.Editor.StatusText);
        host.Editor.ShowCaptureReceipt(copiedToClipboard: true);
        Assert.Equal("클립보드에 복사됨 · 라이브러리에 저장됨", host.Editor.StatusText);
        host.Editor.ShowCaptureReceipt(copiedToClipboard: false);
        Assert.Equal("라이브러리에 저장됨", host.Editor.StatusText);
    });

    [Fact]
    public void StatusLineReportsResultsInNounFormWithoutParticlePairs() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        // Choosing a tool is not a result.
        Assert.Equal(string.Empty, host.Editor.StatusText);
        RectangleAnnotation rectangle = AddRectangle(controller);
        controller.SetSelected(rectangle);
        Assert.Equal($"선택: {rectangle.DisplayName}", host.Editor.StatusText);
        Assert.DoesNotContain("(를)", host.Editor.StatusText, StringComparison.Ordinal);
        Assert.Equal("사각형 추가 · Ctrl+Z로 취소", UiText.Format("EditorFlow_Added", "사각형"));
        Assert.Equal("3번 추가 · Ctrl+Z로 취소", UiText.Format("EditorFlow_StepNumberAdded", 3));
    });

    // ---- Helpers ------------------------------------------------------------------------

    private static RectangleAnnotation AddRectangle(AnnotationEditorController controller)
    {
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(20, 20));
        controller.PointerMove(new PointD(120, 80));
        controller.PointerUp(new PointD(120, 80));
        return Assert.IsType<RectangleAnnotation>(controller.Document.Items[^1]);
    }

    private static Button Command(AnnotationEditorControl editor, string automationId) =>
        Descendants(editor).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == automationId);

    private static void ClickCommand(AnnotationEditorControl editor, string automationId) =>
        Command(editor, automationId).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Drain(UIElement element) =>
        element.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    /// <summary>What the caption close button, Alt+F4 and the taskbar send: WM_SYSCOMMAND / SC_CLOSE.</summary>
    private static void SendCloseCommand(Window window) =>
        SendMessage(new WindowInteropHelper(window).Handle, 0x0112, (IntPtr)0xF060, IntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
