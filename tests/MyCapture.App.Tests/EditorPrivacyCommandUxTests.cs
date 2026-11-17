using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The privacy command and the editor strings the UX audit corrected: the tool rail follows the
/// tool the redaction leaves behind, Esc cancels a running search, disabled commands say why, and
/// the save-as and step-number labels name what they do.
/// </summary>
public sealed class EditorPrivacyCommandUxTests
{
    private sealed class ControlledRedaction(bool available = true) : IPrivacyRedactionService
    {
        internal readonly TaskCompletionSource<PrivacyRedactionResult> Completion = new();
        internal CancellationToken Token { get; private set; }
        public bool IsAvailable { get; } = available;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return Completion.Task;
        }
    }

    private static AnnotationEditorWindow Create(IPrivacyRedactionService service)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        BitmapSource bitmap = Solid(64, 48, 0x30);
        var region = new RectD(0, 0, 64, 48);
        return new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap, privacyRedactionService: service);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Await(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Drain();
        Assert.True(task.IsCompleted, "Bounded dispatcher operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    // The privacy command's tooltip names its key after the text unless the text already does.
    private static string WithKey(string key)
    {
        string text = UiText.Get(key), hint = ShortcutCatalog.Get(ShortcutScope.Annotation, "Privacy").Hint;
        return text.EndsWith($"({hint})", StringComparison.Ordinal) ? text : $"{text} ({hint})";
    }

    private static Dictionary<EditorTool, ToggleButton> Rail(AnnotationEditorControl editor) =>
        Field<Dictionary<EditorTool, ToggleButton>>(editor, "_toolButtons");

    private static Button Redact(AnnotationEditorControl editor) => Field<Button>(editor, "_redactButton");

    [Fact]
    public void PrivacyRedaction_LeavesTheToolRailOnTheSelectToolItSwitchesTo() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var service = new ControlledRedaction();
        AnnotationEditorWindow window = Create(service);
        AnnotationEditorControl editor = window.Editor;
        Invoke(editor, "SelectTool", EditorTool.Rectangle);
        Assert.True(Rail(editor)[EditorTool.Rectangle].IsChecked == true);

        Task detection = editor.ApplyPrivacyRedactionsAsync();
        service.Completion.SetResult(new PrivacyRedactionResult(PrivacyRedactionStatus.Success, [new RectD(2, 2, 12, 10)]));
        Await(detection);

        Assert.Equal(EditorTool.Select, Controller(editor).Tool);
        Assert.False(Rail(editor)[EditorTool.Rectangle].IsChecked == true);
        Assert.True(Rail(editor)[EditorTool.Select].IsChecked == true);
        window.Close();
    });

    [Fact]
    public void Escape_CancelsARunningPrivacySearchAndOtherwiseStepsBackAsBefore() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var service = new ControlledRedaction();
        AnnotationEditorWindow window = Create(service);
        AnnotationEditorControl editor = window.Editor;

        Task detection = editor.ApplyPrivacyRedactionsAsync();
        Assert.False(service.Token.IsCancellationRequested);
        Assert.False(Redact(editor).IsEnabled);

        Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.True(service.Token.IsCancellationRequested);
        Assert.Equal(UiText.Get("Text_6AE98E57E121"), editor.StatusText);
        service.Completion.SetResult(new PrivacyRedactionResult(PrivacyRedactionStatus.Cancelled, []));
        Await(detection);
        Assert.True(Redact(editor).IsEnabled);
        Assert.Empty(Controller(editor).Document.Items);

        // With no search running Esc keeps its old order: back to the select tool first.
        Invoke(editor, "SelectTool", EditorTool.Rectangle);
        Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Equal(EditorTool.Select, Controller(editor).Tool);
        window.Close();
    });

    [Fact]
    public void Escape_DuringTheSearchDoesNotRequestClose() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var service = new ControlledRedaction();
        AnnotationEditorWindow window = Create(service);
        AnnotationEditorControl editor = window.Editor;
        int closeRequests = 0;
        editor.CloseRequested += (_, _) => closeRequests++;

        Task detection = editor.ApplyPrivacyRedactionsAsync();
        Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Equal(0, closeRequests);
        service.Completion.SetResult(new PrivacyRedactionResult(PrivacyRedactionStatus.Cancelled, []));
        Await(detection);
        window.Close();
    });

    [Fact]
    public void DisabledPrivacyAndCopyTextCommandsExplainWhy() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var offline = new ControlledRedaction(available: false);
        AnnotationEditorControl editor = Create(offline).Editor;
        Button redact = Redact(editor);
        Assert.False(redact.IsEnabled);
        Assert.True(ToolTipService.GetShowOnDisabled(redact));
        Assert.Equal(WithKey("Text_39E626945336"), redact.ToolTip);

        var online = new ControlledRedaction();
        AnnotationEditorControl working = Create(online).Editor;
        redact = Redact(working);
        Assert.True(redact.IsEnabled);
        Assert.Equal(WithKey("Text_A31A72A338DD"), redact.ToolTip);
        Task detection = working.ApplyPrivacyRedactionsAsync();
        Assert.False(redact.IsEnabled);
        Assert.Equal(WithKey("EditorFlow_RedactBusy"), redact.ToolTip);
        online.Completion.SetResult(new PrivacyRedactionResult(PrivacyRedactionStatus.NoMatches, []));
        Await(detection);
        Assert.True(redact.IsEnabled);
        Assert.Equal(WithKey("Text_A31A72A338DD"), redact.ToolTip);

        Button copyText = Field<Button>(editor, "_copyTextButton");
        Assert.True(ToolTipService.GetShowOnDisabled(copyText));
    });

    [Fact]
    public void PrivacyCommandIconDiffersFromTheMosaicToolAndSaveAsOpensADialogWithAnEllipsis() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        AnnotationEditorControl editor = Create(new ControlledRedaction()).Editor;

        string icon = FirstPath(Redact(editor)).Data.ToString();
        string mosaic = FirstPath(Rail(editor)[EditorTool.Mosaic]).Data.ToString();
        Assert.NotEqual(mosaic, icon);

        Button save = Descendants(editor).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "EditorFlowSaveToFile");
        string[] headers = [.. save.ContextMenu!.Items.OfType<MenuItem>().Select(item => (string)item.Header)];
        Assert.Contains("다른 이름으로 저장…", headers);
        Assert.All(headers.Where(header => header.Contains("저장") && header.Contains("다른")), header => Assert.EndsWith("…", header));
        using var english = UiText.UseLanguage("en");
        Assert.Equal("Save as…", UiText.Get("Text_A81F975C401F"));
    });

    [Fact]
    public void StepNumberPropertiesNameTheSelectedMarkerNumberApartFromTheNextNumber() => StaTestHost.Run(() =>
    {
        using (UiText.UseLanguage("ko-KR"))
        {
            Assert.Equal("선택한 번호", UiText.Get("EditorStep_Number"));
            Assert.Equal("다음 번호", UiText.Get("EditorUx_StepNumberNext"));
        }
        using (UiText.UseLanguage("en"))
        {
            Assert.Equal("Selected number", UiText.Get("EditorStep_Number"));
            Assert.Equal("Next number", UiText.Get("EditorUx_StepNumberNext"));
        }
    });

    [Fact]
    public void StoreRatingActionHasOneShortAndOneFullLabel() => StaTestHost.Run(() =>
    {
        // Compact invitation cards keep the short label; settings and support name the store.
        foreach (string language in new[] { "ko-KR", "en" })
        {
            using var scope = UiText.UseLanguage(language);
            Assert.Equal(UiText.Get("StoreRating_InviteAction"), UiText.Get("LibraryUx_RateAction"));
            Assert.Equal(UiText.Get("StoreRating_Button"), UiText.Get("Support_StoreReview"));
        }
        using (UiText.UseLanguage("ko-KR")) Assert.Equal("평가", UiText.Get("StoreRating_InviteAction"));
    });

    [Fact]
    public void PracticeIntroNamesTheThreeTopicsAndFifteenSteps() => StaTestHost.Run(() =>
    {
        using (UiText.UseLanguage("ko-KR")) Assert.Contains("3개 주제 15단계", UiText.Get("Onboarding.AboutBody"));
        using (UiText.UseLanguage("en")) Assert.Contains("Fifteen steps in three topics", UiText.Get("Onboarding.AboutBody"));
    });

    private static System.Windows.Shapes.Path FirstPath(DependencyObject root) =>
        LogicalDescendants(root).OfType<System.Windows.Shapes.Path>().First();

    private static IEnumerable<DependencyObject> Descendants(AnnotationEditorControl root) => LogicalDescendants(root);

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject current = stack.Pop();
            foreach (object child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is not DependencyObject node) continue;
                yield return node;
                stack.Push(node);
            }
        }
    }
}
