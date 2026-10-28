using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifPreviewCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_StaticOriginalCanBeRequestedAgainAfterCancelledDecode(bool rendererReturnsImage) => RunSta(() =>
    {
        using var fixture = new PreviewFixture();
        fixture.Compare.IsChecked = true;
        PendingRender first = Assert.Single(fixture.Requests);
        Assert.Equal(1, fixture.Panel.FrameCount);
        Assert.False(fixture.Panel.IsPlaying);
        Assert.False(Descendants(fixture.Panel).OfType<Slider>().Single().IsEnabled);
        Assert.Null(fixture.Panel.OriginalFrame);

        fixture.Panel.Stop();
        Assert.True(first.Token.IsCancellationRequested);
        if (rendererReturnsImage) first.Completion.SetResult(Bitmap(10));
        else first.Completion.SetCanceled(first.Token);
        Pump();
        Assert.Null(fixture.Panel.OriginalFrame);

        fixture.ReopenComparison();
        Assert.Equal(2, fixture.Requests.Count);
        BitmapSource expected = Bitmap(200);
        fixture.Requests[1].Completion.SetResult(expected);
        Pump();
        Assert.Same(expected, fixture.Panel.OriginalFrame);
        Assert.Contains(Descendants(fixture.Panel).OfType<TextBlock>(), text => text.Text == UiText.Get("GifPreview_Original"));

        // Stopping an already completed original preserves its cached frame.
        fixture.Panel.Stop();
        fixture.ReopenComparison();
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Same(expected, fixture.Panel.OriginalFrame);
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("error")]
    [InlineData("success")]
    public void Stop_RetryBeforeOldDecodeFinishesPreservesNewRequest(string oldOutcome) => RunSta(() =>
    {
        using var fixture = new PreviewFixture();
        fixture.Compare.IsChecked = true;
        PendingRender first = Assert.Single(fixture.Requests);
        fixture.Panel.Stop();
        fixture.ReopenComparison(); // Retry before the old renderer acknowledges cancellation.
        Assert.Equal(2, fixture.Requests.Count);
        PendingRender second = fixture.Requests[1];

        CompleteOld(first, oldOutcome);
        Pump();
        Assert.Null(fixture.Panel.OriginalFrame);
        Assert.Contains(Descendants(fixture.Panel).OfType<TextBlock>(), text => text.Text == UiText.Get("GifPreview_OriginalLoading"));
        fixture.ReopenComparison();
        Assert.Equal(2, fixture.Requests.Count); // The late result did not reset the new frame index.

        fixture.Panel.Stop();
        Assert.True(second.Token.IsCancellationRequested); // Nor did its finally detach the new CTS.
        fixture.ReopenComparison();
        Assert.Equal(3, fixture.Requests.Count);
        second.Completion.SetCanceled(second.Token);
        BitmapSource expected = Bitmap(200);
        fixture.Requests[2].Completion.SetResult(expected);
        Pump();
        Assert.Same(expected, fixture.Panel.OriginalFrame);
        Assert.Contains(Descendants(fixture.Panel).OfType<TextBlock>(), text => text.Text == UiText.Get("GifPreview_Original"));
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("error")]
    [InlineData("success")]
    public void Stop_LateOldDecodeDoesNotChangeCompletedRetry(string oldOutcome) => RunSta(() =>
    {
        using var fixture = new PreviewFixture();
        fixture.Compare.IsChecked = true;
        PendingRender first = Assert.Single(fixture.Requests);
        fixture.Panel.Stop();
        fixture.ReopenComparison();
        Assert.Equal(2, fixture.Requests.Count);
        BitmapSource expected = Bitmap(200);
        fixture.Requests[1].Completion.SetResult(expected);
        Pump();

        CompleteOld(first, oldOutcome);
        Pump();
        Assert.Same(expected, fixture.Panel.OriginalFrame);
        Assert.Contains(Descendants(fixture.Panel).OfType<TextBlock>(), text => text.Text == UiText.Get("GifPreview_Original"));
        fixture.ReopenComparison();
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Same(expected, fixture.Panel.OriginalFrame);
    });

    private static void CompleteOld(PendingRender request, string outcome)
    {
        switch (outcome)
        {
            case "cancel": request.Completion.SetCanceled(request.Token); break;
            case "error": request.Completion.SetException(new InvalidOperationException("Old decoder failed after cancellation.")); break;
            case "success": request.Completion.SetResult(Bitmap(10)); break;
            default: throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private sealed record PendingRender(CancellationToken Token, TaskCompletionSource<BitmapSource> Completion);

    private sealed class PreviewFixture : IDisposable
    {
        private readonly GifPreviewSample _sample = StaticSample();
        internal List<PendingRender> Requests { get; } = [];
        internal GifPreviewPanel Panel { get; }
        internal ToggleButton Compare { get; }

        internal PreviewFixture()
        {
            Panel = new GifPreviewPanel((sample, index, token) =>
            {
                Assert.Same(_sample, sample);
                Assert.Equal(0, index);
                var completion = new TaskCompletionSource<BitmapSource>();
                Requests.Add(new PendingRender(token, completion));
                return completion.Task;
            });
            Panel.ShowSample(_sample, autoplay: true);
            Compare = Descendants(Panel).OfType<ToggleButton>().Single(button =>
                AutomationProperties.GetName(button) == UiText.Get("GifPreview_Compare"));
        }

        internal void ReopenComparison()
        {
            Compare.IsChecked = false;
            Compare.IsChecked = true;
        }

        public void Dispose()
        {
            Panel.ShowPreparing();
            foreach (PendingRender request in Requests) request.Completion.TrySetCanceled();
            Pump();
            _sample.Dispose();
        }
    }

    private static GifPreviewSample StaticSample()
    {
        var frames = new GifSampleFrames([Bitmap(100)], [100], 1, 1);
        // Keep the fixture in memory: constructing the decoded result is sufficient for
        // this panel lifecycle test and avoids involving the video encoder or filesystem.
        ConstructorInfo constructor = Assert.Single(typeof(GifPreviewSample).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        return (GifPreviewSample)constructor.Invoke([
            new GifSampleWindow(0, 1_000, true), GifExportQuality.Standard, 1d,
            new GifSegmentSize(100, 10, 20, 0.2), 0d, frames, 0d, null,
            new GifFrameSchedule([0], [100], [], [])]);
    }

    private static BitmapSource Bitmap(byte value)
    {
        BitmapSource result = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { value, value, value, 255 }, 4);
        result.Freeze();
        return result;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Descendants(element)) yield return descendant;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action) => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        action();
    });
}
