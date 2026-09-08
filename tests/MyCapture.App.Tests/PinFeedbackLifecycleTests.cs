using System.Diagnostics;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinFeedbackLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DelayedClipboardOrOcrCompletion_AfterClose_DoesNotRestartFeedbackTimer(bool textFeedback) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 64);
        image.Freeze();
        var pin = new PinWindow(PinContent.FromImage(image), new PinViewState(16, 16, 1, 1, 0.1),
            0, 0, () => new PinSettings());
        var timer = (DispatcherTimer)typeof(PinWindow).GetField("_feedbackTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin)!;
        try
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task feedback = CompleteLaterAsync();
            pin.Close();
            Assert.True(pin.IsClosed);
            Assert.False(timer.IsEnabled);
            completed.SetResult(true);
            long started = Stopwatch.GetTimestamp();
            while (!feedback.IsCompleted)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5)) throw new TimeoutException();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            feedback.GetAwaiter().GetResult();
            Assert.False(timer.IsEnabled);

            async Task CompleteLaterAsync()
            {
                bool copied = await completed.Task;
                if (textFeedback) pin.ReportOriginalTextCopyResult(copied);
                else pin.ReportCopyResult(copied);
            }
        }
        finally
        {
            // Also contain the timer if an unfixed implementation fails the assertion.
            timer.Stop();
            pin.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });
}
