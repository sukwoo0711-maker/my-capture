using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorDraftCloseRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedDiscardDuringAppExit_RestoresTheUsableEditorAndPreservesTheDraft(bool video) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = TestRecycleBin.CreateTempSubdirectory("mc-draft-close-race-").FullName;
        Guid id = Guid.NewGuid();
        string sessionRoot = Path.Combine(root, id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(sessionRoot, "retired")); // A directory blocks durable marker publication.
        string preserved = Path.Combine(sessionRoot, "preserved-draft");
        File.WriteAllText(preserved, "original draft");
        var store = new EditDraftStore(root, _ => { });
        var snapshot = new TaskCompletionSource<DraftSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? window = null;
        try
        {
            Func<Task<bool>> prepareExit;
            Action requestClose;
            int prompts = 0;
            if (video)
            {
                var editor = VideoEditorUxTrimTests.OpenReadyEditor(root, 10_000, show: true);
                window = editor;
                editor.AdoptRecoveredDraft(store, id, 1);
                editor.UnsavedEditsPromptForTest = () => { prompts++; return EditorCloseChoice.DontSave; };
                prepareExit = editor.PrepareForAppExitAsync;
                requestClose = () => { editor.MarkUserCloseRequestedForTest(); editor.Close(); };
            }
            else
            {
                var bitmap = EditorKeysTests.Solid(64, 48, 0x30);
                var bounds = new RectD(0, 0, 64, 48);
                var editor = new AnnotationEditorWindow(new FrozenFrame(bitmap, bounds, null, 0), bounds, bitmap)
                {
                    Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                };
                window = editor;
                editor.AdoptRecoveredDraft(store, id, 1);
                editor.ConfirmClose = () => { prompts++; return EditorCloseChoice.DontSave; };
                editor.Show();
                prepareExit = editor.PrepareForAppExitAsync;
                requestClose = editor.RequestClose;
            }
            var session = new EditDraftSession(store, window.Dispatcher,
                () => { entered.TrySetResult(); return snapshot.Task; }, _ => { }, id, 1);
            window.GetType().GetField("_draftSession", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            Task write = session.FlushAsync();
            PumpUntil(() => entered.Task.IsCompleted);
            requestClose();
            Assert.False(window.IsEnabled);
            Task<bool> exit = prepareExit();
            Assert.False(exit.IsCompleted);
            snapshot.TrySetResult(null);
            PumpUntil(() => exit.IsCompleted && write.IsCompleted);
            Assert.False(exit.GetAwaiter().GetResult());
            Assert.True(window.IsVisible);
            Assert.True(window.IsEnabled);
            Assert.False(store.IsRetired(id));
            Assert.Equal("original draft", File.ReadAllText(preserved));
            Assert.Equal(1, prompts);
        }
        finally
        {
            snapshot.TrySetResult(null);
            window?.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });

    private static void PumpUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
