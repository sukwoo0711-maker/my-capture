using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ShellRetakeWindowTests
{
    [Fact]
    public void TitleIdentity_PreservesBindingUpdatesAndContext() => StaTestHost.Run(() =>
    {
        var source = new Caption { Text = "Settings" };
        var window = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
        window.SetBinding(Window.TitleProperty, new Binding(nameof(Caption.Text)) { Source = source });
        WindowIdentity.Attach(window);
        Assert.Equal(AppIdentity.Label + " — Settings", window.Title);
        Assert.True(BindingOperations.IsDataBound(window, Window.TitleProperty));
        source.Text = "Gallery — capture.png";
        Assert.Equal(AppIdentity.Label + " — Gallery — capture.png", window.Title);
        WindowIdentity.Attach(window);
        Assert.Equal(AppIdentity.Label + " — Gallery — capture.png", window.Title);
        window.Close();
    });

    [Fact]
    public void Retake_ClosesOnlyOwnedEditor_StartsFreshFrame_AndKeepsPersistedOriginal() => StaTestHost.Run(() =>
    {
        string originals = OwnedTestDirectory.Create("retake-original");
        string original = System.IO.Path.Combine(originals, "original.png");
        Window? editor = null;
        int acquired = 0;
        var outside = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
        outside.Show();
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { editor ??= window; return true; };
        coordinator.SelectionPersistRequested = _ => { System.IO.File.WriteAllText(original, "durable original"); return Task.CompletedTask; };
        try
        {
            Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.True(coordinator.CanRetake);
            Assert.True(editor!.IsVisible);
            coordinator.CloseEditorForRetake();
            Assert.False(editor.IsVisible);
            Assert.True(outside.IsVisible);
            Assert.False(coordinator.IsActive);
            coordinator.Start(false, false, false);
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
            Assert.Equal(1, acquired);
            Assert.True(coordinator.IsActive);
            Assert.Equal("durable original", System.IO.File.ReadAllText(original));
            coordinator.Cancel();
        }
        finally { outside.Close(); OwnedTestDirectory.Delete(originals); }
    });

    [Fact]
    public void Retake_RespectsEditorCloseVeto_AndDoesNotStartConcurrentCapture() => StaTestHost.Run(() =>
    {
        Window? editor = null;
        int acquired = 0;
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { editor = window; return true; };
        coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        CancelEventHandler veto = (_, args) => args.Cancel = true;
        editor!.Closing += veto;
        try
        {
            coordinator.CloseEditorForRetake();
            Assert.True(editor.IsVisible);
            Assert.True(coordinator.IsActive);
            coordinator.Start(false, false, false);
            Assert.Equal(0, acquired);
        }
        finally { editor.Closing -= veto; coordinator.Cancel(); }
    });

    [Fact]
    public void PendingPersistence_CannotRetake_AndCancellationPreventsOldEditorReopening() => StaTestHost.Run(() =>
    {
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int windows = 0;
        using var coordinator = Create(Frame);
        coordinator.SelectionPersistRequested = _ => persisted.Task;
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = _ => { windows++; return true; };
        coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
        Assert.False(coordinator.CanRetake);
        coordinator.CloseEditorForRetake();
        Assert.True(coordinator.IsActive);
        coordinator.Cancel();
        persisted.SetResult();
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        Assert.False(coordinator.IsActive);
        Assert.Equal(0, windows);
    });

    private static CaptureOverlayCoordinator Create(Func<FrozenFrame> acquire) => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
        new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
        NullLogger<CaptureOverlayCoordinator>.Instance, _ => acquire(), () => new RectD(0, 0, 32, 20));

    private static FrozenFrame Frame()
    {
        var bitmap = BitmapSource.Create(32, 20, 96, 96, PixelFormats.Bgr32, null, new byte[32 * 20 * 4], 128);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);
    }

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

    private sealed class Caption : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
