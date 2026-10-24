using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class VideoEditorInputRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("ko-KR", "동영상을 불러오는 중…")]
    [InlineData("en-US", "Loading video…")]
    public void LocalLoadingStaysPercentageFreeUntilTheExistingReadyPath(string language, string expected) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-load-text-");
        var editor = new VideoEditorWindow(new RecordingResult(System.IO.Path.Combine(root, "not-opened.mp4"), 10000, 30, 300, 640, 360),
            AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
        try
        {
            TextBlock label = Field<TextBlock>(editor, "_loadingLabel");
            Assert.Equal(expected, label.Text);
            Invoke(editor, "StartLoadProgress");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(280) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            Assert.Equal(expected, label.Text);
            Assert.False(editor.IsMediaReadyForTest);
            Assert.Equal(Visibility.Visible, Field<Border>(editor, "_loadingOverlay").Visibility);
            Assert.True(Field<DispatcherTimer>(editor, "_openTimeoutTimer").IsEnabled);

            Invoke(editor, "CompleteOpen", 10000d, true);
            Assert.True(editor.IsMediaReadyForTest);
            Assert.Equal(Visibility.Collapsed, Field<Border>(editor, "_loadingOverlay").Visibility);
            Assert.Null(Field<DispatcherTimer?>(editor, "_openTimeoutTimer"));
        }
        finally { editor.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void TabFocusedButtonOwnsSpaceAndDoesNotTogglePlayback() => WithReadyEditor(editor =>
    {
        Assert.True(editor.TimelineForTest.SetTrimRange(2000, 8000));
        TextBox end = Field<TextBox>(editor, "_trimOutInput");
        Button reset = Field<Button>(editor, "_resetTrimButton");
        int clicks = 0;
        reset.Click += (_, _) => clicks++;
        end.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(end.Focus());
        // Send the navigation key through the window, then use WPF's Tab traversal.
        end.RaiseEvent(KeyArgs(editor, Key.Tab, Keyboard.PreviewKeyDownEvent));
        Assert.True(end.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
        Assert.Same(reset, Keyboard.FocusedElement);
        Press(editor, reset, Key.Space);
        Assert.Equal(1, clicks);
        Assert.Equal(0, editor.TimelineForTest.InMs);
        Assert.Equal(10000, editor.TimelineForTest.OutMs);
        Assert.False(editor.PlayButtonShowsPauseForTest);
    });

    [Fact]
    public void EnterAfterPointerFocusGivesTheDetailsButtonItsOwnSpaceAction() => WithReadyEditor(editor =>
    {
        editor.SetDetailsExpandedForTest(false);
        ToggleButton toggle = Field<ToggleButton>(editor, "_detailToggle");
        toggle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(toggle.Focus());
        Press(editor, toggle, Key.Enter);
        editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
        Assert.True(editor.AreDetailsExpandedForTest);
        Button first = Assert.IsType<Button>(Keyboard.FocusedElement);
        Assert.Equal(UiText.Get("Text_DC710AA11970"), AutomationProperties.GetName(first));
        int clicks = 0;
        first.Click += (_, _) => clicks++;
        Press(editor, first, Key.Space);
        Assert.Equal(1, clicks);
        Assert.False(editor.PlayButtonShowsPauseForTest);
    });

    [Fact]
    public void MouseFocusedButtonLeavesSpaceToPlayback() => WithReadyEditor(editor =>
    {
        Assert.True(editor.TimelineForTest.SetTrimRange(2000, 8000));
        Button reset = Field<Button>(editor, "_resetTrimButton");
        int clicks = 0;
        reset.Click += (_, _) => clicks++;
        reset.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(reset.Focus());
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, clicks);
        // Reset disables itself at full length; make it usable again without changing focus.
        Assert.True(editor.TimelineForTest.SetTrimRange(2000, 8000));
        Assert.True(reset.Focus());
        Press(editor, reset, Key.Space);
        Assert.Equal(1, clicks);
        Assert.True(editor.PlayButtonShowsPauseForTest);
        Assert.Equal(2000, editor.TimelineForTest.InMs);
    });

    [Fact]
    public void MouseFocusedToggleLeavesSpaceToPlayback() => WithReadyEditor(editor =>
    {
        editor.SetDetailsExpandedForTest(false);
        ToggleButton toggle = Field<ToggleButton>(editor, "_detailToggle");
        toggle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(toggle.Focus());
        Press(editor, toggle, Key.Space);
        Assert.False(editor.AreDetailsExpandedForTest);
        Assert.True(editor.PlayButtonShowsPauseForTest);
    });

    [Fact]
    public void TimelineFocusedSpaceTogglesPlayback() => WithReadyEditor(editor =>
    {
        Assert.True(editor.TimelineForTest.Focus());
        Press(editor, editor.TimelineForTest, Key.Space);
        Assert.True(editor.PlayButtonShowsPauseForTest);
        Press(editor, editor.TimelineForTest, Key.Space);
        Assert.False(editor.PlayButtonShowsPauseForTest);
    });

    [Theory]
    [InlineData("combo")]
    [InlineData("slider")]
    [InlineData("list")]
    public void SpaceControlsKeepBothPreviewAndBubblingSpace(string kind) => WithReadyEditor(editor =>
    {
        Control control = kind switch
        {
            "combo" => new ComboBox { Items = { "one", "two" } },
            "list" => new ListBox { Items = { "one", "two" } },
            _ => new Slider(),
        };
        ((Grid)editor.Content).Children.Add(control);
        editor.UpdateLayout();
        control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(control.Focus());
        var preview = KeyArgs(editor, Key.Space, Keyboard.PreviewKeyDownEvent);
        control.RaiseEvent(preview);
        Assert.False(preview.Handled);
        Press(editor, control, Key.Space);
        Assert.False(editor.PlayButtonShowsPauseForTest);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckBoxFollowsTheButtonRuleForSpace(bool reachedWithTab) => WithReadyEditor(editor =>
    {
        var check = new CheckBox();
        ((Grid)editor.Content).Children.Add(check);
        editor.UpdateLayout();
        check.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(check.Focus());
        if (reachedWithTab) check.RaiseEvent(KeyArgs(editor, Key.Tab, Keyboard.PreviewKeyDownEvent));
        Press(editor, check, Key.Space);
        // A clicked check box must not repeat itself on Space; after Tab it takes its own action.
        Assert.Equal(reachedWithTab, check.IsChecked == true);
        Assert.Equal(!reachedWithTab, editor.PlayButtonShowsPauseForTest);
    });

    [Fact]
    public void TextInputSpaceInsertsTextWithoutTogglingPlayback() => WithReadyEditor(editor =>
    {
        TextBox input = Field<TextBox>(editor, "_trimInInput");
        Assert.True(input.Focus());
        input.Text = "5";
        input.CaretIndex = input.Text.Length;
        // The text box types the space from its own Space key handling.
        Press(editor, input, Key.Space);
        Assert.Equal("5 ", input.Text);
        Assert.False(editor.PlayButtonShowsPauseForTest);
    });

    [Fact]
    public void InvalidPairSurvivesFocusLossAndAppliesAfterEditingTheOtherField() => WithReadyEditor(editor =>
    {
        Assert.True(editor.TimelineForTest.SetTrimRange(1000, 3000));
        TextBox start = Field<TextBox>(editor, "_trimInInput");
        TextBox end = Field<TextBox>(editor, "_trimOutInput");
        Assert.True(start.Focus());
        start.Text = "5";
        end.Text = "3";
        Assert.True(end.Focus()); // LostFocus validates the pair without replacing either draft.
        Assert.Equal("5", start.Text);
        Assert.Equal("3", end.Text);
        Assert.Equal(1000, editor.TimelineForTest.InMs);
        Assert.Equal(3000, editor.TimelineForTest.OutMs);
        Assert.Equal(UiText.Get("Video.TrimInputInvalid"), AutomationProperties.GetHelpText(start));
        Assert.Equal(UiText.Get("Video.TrimInputInvalid"), AutomationProperties.GetItemStatus(end));
        Assert.Equal(UiText.Get("Video.TrimIn"), AutomationProperties.GetName(start));

        end.Text = "8";
        Press(editor, end, Key.Enter);
        Assert.Equal(5000, editor.TimelineForTest.InMs);
        Assert.Equal(8000, editor.TimelineForTest.OutMs);
        Assert.Equal("0:05.000", start.Text);
        Assert.Equal("0:08.000", end.Text);
        Assert.Equal(UiText.Get("Video.SourceTimeHint"), AutomationProperties.GetHelpText(start));
        Assert.Empty(AutomationProperties.GetItemStatus(end));
        Assert.NotEqual(UiText.Get("Video.TrimInputInvalid"), Field<TextBlock>(editor, "_statusLabel").Text);
    });

    [Theory]
    [InlineData("11", "8", true, false)]
    [InlineData("2", "11", false, true)]
    [InlineData("bad", "8", true, false)]
    [InlineData("2", "bad", false, true)]
    public void InvalidFieldKeepsDraftAndAssociatesErrorWithThatField(string startText, string endText, bool startError, bool endError) => WithReadyEditor(editor =>
    {
        Assert.True(editor.TimelineForTest.SetTrimRange(1000, 9000));
        TextBox start = Field<TextBox>(editor, "_trimInInput");
        TextBox end = Field<TextBox>(editor, "_trimOutInput");
        start.Text = startText;
        end.Text = endText;
        Press(editor, start, Key.Enter);
        Assert.Equal(startText, start.Text);
        Assert.Equal(endText, end.Text);
        Assert.Equal(1000, editor.TimelineForTest.InMs);
        Assert.Equal(9000, editor.TimelineForTest.OutMs);
        Assert.Equal(startError, AutomationProperties.GetItemStatus(start).Length > 0);
        Assert.Equal(endError, AutomationProperties.GetItemStatus(end).Length > 0);
    });

    [Fact]
    public void RangeInsideRemovedContentKeepsDraftsAndTheActualRange() => WithReadyEditor(editor =>
    {
        editor.TimelineForTest.SetPlayhead(3000);
        Assert.True(editor.HandleShortcut(Key.K, ModifierKeys.Control));
        editor.TimelineForTest.SetPlayhead(7000);
        Assert.True(editor.HandleShortcut(Key.K, ModifierKeys.Control));
        editor.TimelineForTest.SelectSegmentAt(5000);
        Assert.True(editor.HandleShortcut(Key.Delete, ModifierKeys.None));
        Assert.Single(Field<VideoEditDocument>(editor, "_editDocument").RemovedRanges);
        int undoBefore = Field<List<VideoEditDocument>>(editor, "_undo").Count;
        TextBox start = Field<TextBox>(editor, "_trimInInput");
        TextBox end = Field<TextBox>(editor, "_trimOutInput");
        start.Text = "4";
        end.Text = "6";
        Press(editor, end, Key.Enter);
        Assert.Equal("4", start.Text);
        Assert.Equal("6", end.Text);
        Assert.Equal(0, editor.TimelineForTest.InMs);
        Assert.Equal(10000, editor.TimelineForTest.OutMs);
        Assert.Equal(undoBefore, Field<List<VideoEditDocument>>(editor, "_undo").Count);
        Assert.Equal(UiText.Get("Video.TrimInputInvalid"), AutomationProperties.GetItemStatus(start));
        Assert.Equal(UiText.Get("Video.TrimInputInvalid"), AutomationProperties.GetItemStatus(end));
        Assert.Equal(UiText.Get("Video.TrimInputInvalid"), Field<TextBlock>(editor, "_statusLabel").Text);
    });

    [Fact]
    public void EscapeAndHandleDragRestoreActualTrimValuesAndClearErrors() => WithReadyEditor(editor =>
    {
        Assert.True(editor.TimelineForTest.SetTrimRange(1000, 9000));
        TextBox start = Field<TextBox>(editor, "_trimInInput");
        TextBox end = Field<TextBox>(editor, "_trimOutInput");
        start.Text = "bad";
        end.Text = "3";
        Press(editor, start, Key.Enter);
        Press(editor, start, Key.Escape);
        Assert.Equal("0:01.000", start.Text);
        Assert.Equal("0:09.000", end.Text);
        Assert.Empty(AutomationProperties.GetItemStatus(start));
        start.Text = "bad";
        Press(editor, start, Key.Enter);
        editor.TimelineForTest.BeginTrimDragForTest(end: true);
        editor.TimelineForTest.MoveTrimDragForTest(8000);
        editor.TimelineForTest.EndTrimDragForTest();
        Assert.Equal("0:01.000", start.Text);
        Assert.Equal("0:08.000", end.Text);
        Assert.Equal(UiText.Get("Video.SourceTimeHint"), AutomationProperties.GetHelpText(start));
        Assert.Empty(AutomationProperties.GetItemStatus(start));
        Assert.NotEqual(UiText.Get("Video.TrimInputInvalid"), Field<TextBlock>(editor, "_statusLabel").Text);
    });

    private static void WithReadyEditor(Action<VideoEditorWindow> action) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-input-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10000, show: true);
            editor.SetDetailsExpandedForTest(true);
            editor.UpdateLayout();
            action(editor);
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    private static KeyEventArgs KeyArgs(Window window, Key key, RoutedEvent routedEvent) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key) { RoutedEvent = routedEvent };

    private static void Press(Window window, UIElement target, Key key)
    {
        var down = KeyArgs(window, key, Keyboard.PreviewKeyDownEvent);
        target.RaiseEvent(down);
        if (!down.Handled)
        {
            down.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(down);
        }
        var up = KeyArgs(window, key, Keyboard.PreviewKeyUpEvent);
        target.RaiseEvent(up);
        if (!up.Handled)
        {
            up.RoutedEvent = Keyboard.KeyUpEvent;
            target.RaiseEvent(up);
        }
    }

    private static void Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
}
