using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorSavedCopyStatusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedFileWithFailedCopyShowsSavedAndRetriesCopyOnly(bool throws) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        Field<TextBox>(host.Editor, "_activeTextBox").Text = "저장한 내용";
        Invoke(host.Editor, "CommitActiveText");
        var actions = new List<EditorCommitAction>();
        bool copySucceeds = false;
        host.Window.CommitRequested = result =>
        {
            actions.Add(result.Action);
            if (result.Action == EditorCommitAction.CopyToClipboard) return Task.FromResult(copySucceeds);
            result.SaveCompleted = true;
            result.ClipboardCopyFailed = true;
            return throws ? Task.FromException<bool>(new InvalidOperationException("Fake clipboard failure")) : Task.FromResult(false);
        };
        Assert.True(host.Editor.HandleShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.True(host.Window.IsVisible);
        Assert.False(host.Editor.HasUnsavedChanges);
        Assert.Equal(UiText.Get("EditorFlow_SavedCopyFailed"), host.Editor.StatusText);
        Button retry = Field<Button>(host.Editor, "_retryCopyButton");
        Assert.Equal(Visibility.Visible, retry.Visibility);
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(UiText.Get("EditorFlow_SavedCopyFailed"), host.Editor.StatusText);
        Controller(host.Editor).ApplyFontSize(30);
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(UiText.Get("Text_3D1E714B56CA"), host.Editor.StatusText);
        Assert.True(host.Editor.HasUnsavedChanges);
        Controller(host.Editor).PerformUndo();
        copySucceeds = true;
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { EditorCommitAction.SaveAs, EditorCommitAction.CopyToClipboard,
            EditorCommitAction.CopyToClipboard, EditorCommitAction.CopyToClipboard }, actions);
        Assert.Equal(UiText.Get("EditorFlow_Copied"), host.Editor.StatusText);
        Assert.Equal(Visibility.Collapsed, retry.Visibility);
        Assert.True(host.Window.IsVisible);
        Assert.False(host.Editor.HasUnsavedChanges);
        Controller(host.Editor).ApplyFontSize(30);
        Assert.True(host.Editor.HasUnsavedChanges);
        Controller(host.Editor).PerformUndo();
        Assert.False(host.Editor.HasUnsavedChanges);
    });

    [Fact]
    public void FailedSaveRemainsDirtyAndDoesNotOfferCopyAsSaveRecovery() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        Field<TextBox>(host.Editor, "_activeTextBox").Text = "미저장";
        host.Window.CommitRequested = _ => Task.FromResult(false);
        host.Editor.HandleShortcut(Key.S, ModifierKeys.Control);
        Assert.True(host.Editor.HasUnsavedChanges);
        Assert.Equal(UiText.Get("Text_06F58809C083"), host.Editor.StatusText);
        Assert.Equal(Visibility.Collapsed, Field<Button>(host.Editor, "_retryCopyButton").Visibility);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DerivedVideoFrameResultReturnsSaveFactsToTheEditor(bool throws) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.FrameStillImage = new MyCapture.App.Recording.VideoFrameStillImage(500,
            [new MyCapture.Core.Recording.TimedTextOverlay { Text = "caption", StartMs = 400, EndMs = 600 }]);
        var bitmap = Solid(640, 360, 240);
        var region = new RectD(0, 0, 640, 360);
        var original = new AnnotationEditingResult(new MyCapture.Platform.Capture.FrozenFrame(bitmap, region, null, 0),
            region, bitmap, Controller(host.Editor).Document, EditorCommitAction.SaveAs,
            new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>(), new Dictionary<string, string>());
        host.Window.CommitRequested = derived =>
        {
            Assert.NotSame(original, derived);
            derived.SaveCompleted = true;
            derived.ClipboardCopyFailed = true;
            return throws ? Task.FromException<bool>(new InvalidOperationException("Fake copy failure")) : Task.FromResult(false);
        };
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(host.Editor.Dispatcher));
            Task<bool> saving = host.Window.CommitRequested(original);
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!saving.IsCompleted && DateTime.UtcNow < deadline)
                host.Editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.True(saving.IsCompleted);
            if (throws) Assert.Throws<InvalidOperationException>(() => saving.GetAwaiter().GetResult());
            else Assert.False(saving.GetAwaiter().GetResult());
            Assert.True(original.SaveCompleted);
            Assert.True(original.ClipboardCopyFailed);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });
}
