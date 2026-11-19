using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class PinTranslationIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task APictureModelCheckFailureKeepsItsStageRegardlessOfDispatcherOrder(bool workerFirst) => RunAsync(async () =>
    {
        using var translator = new OrderedModelCheckFailure();
        var log = new StageLog();
        PinManager manager = Manager(translator, log);
        var reading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int readings = 0;
        int uiThread = Environment.CurrentManagedThreadId;
        manager.ConfigureImageTranslation((_, token) =>
        {
            Interlocked.Increment(ref readings);
            return workerFirst ? Task.FromResult<string?>(English) : reading.Task.WaitAsync(token);
        });
        PinWindow? pin = null;
        try
        {
            BitmapSource original = Image();
            Assert.True(original.IsFrozen);
            pin = manager.PinImage(original);
            pin.InvokeTranslationAction();
            Assert.False(pin.TranslationWork.IsCompleted);
            if (workerFirst)
            {
                // Keep this STA turn until the eager worker reaches its check. No dispatcher work can run yet.
                Assert.True(translator.WaitForCheck(TimeSpan.FromSeconds(5)), "The eager worker must reach its model check before the dispatcher yields.");
            }

            // The translation handler was queued at Normal priority. This barrier runs after it has
            // reached its reading await, with either the model check or the recognition still gated.
            await pin.Dispatcher.InvokeAsync(() =>
            {
                Assert.False(pin.TranslationWork.IsCompleted);
                Assert.Equal(workerFirst, translator.CheckEntered.Task.IsCompleted);
                if (!workerFirst) Assert.True(reading.TrySetResult(English));
            }, DispatcherPriority.Background).Task.WaitAsync(TimeSpan.FromSeconds(5));
            await translator.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(uiThread, translator.CheckThread);
            Assert.True(translator.FailCheck());
            await pin.TranslationWork.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal(UiText.Get("Pin_TranslationFailedModelCheck"), pin.FeedbackForTest.Text);
            Assert.Same(original, pin.PinnedContent.Image);
            Assert.False(pin.IsShowingTranslation);
            Assert.Null(pin.DisplayedText);
            StageLog.Entry entry = Assert.Single(log.Entries);
            Assert.Equal("Pin translation failed while checking the model", entry.Template);
            Assert.Same(translator.Failure, entry.Exception);
            Assert.Contains(" ex=System.UnauthorizedAccessException ", entry.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(English, entry.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(translator.Failure.Message, entry.Line, StringComparison.Ordinal);
            Assert.Equal(1, readings);
            Assert.Equal(1, translator.Checks);
            Assert.Equal(1, translator.WarmUps);
            Assert.Equal(0, translator.UnexpectedCalls);
        }
        finally
        {
            reading.TrySetCanceled();
            translator.CancelCheck();
            manager.CloseAll();
            if (pin is not null) await pin.TranslationWork.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    private sealed class OrderedModelCheckFailure : ITextTranslator, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly object _signalLock = new();
        private readonly TaskCompletionSource<bool> _checked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        internal TaskCompletionSource<bool> CheckEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception Failure { get; } = new UnauthorizedAccessException("Synthetic model-check failure.");
        internal int Checks;
        internal int WarmUps;
        internal int UnexpectedCalls;
        internal int CheckThread;
        public long ModelDownloadBytes => 1;

        internal bool WaitForCheck(TimeSpan timeout) => _entered.Wait(timeout);
        internal bool FailCheck() => _checked.TrySetException(Failure);
        internal void CancelCheck() => _checked.TrySetCanceled();

        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Checks);
            CheckThread = Environment.CurrentManagedThreadId;
            CheckEntered.TrySetResult(true);
            lock (_signalLock)
            {
                if (!_disposed) _entered.Set();
            }
            return _checked.Task.WaitAsync(cancellationToken);
        }

        public Task WarmUpAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WarmUps);
            return Task.CompletedTask;
        }

        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw UnexpectedCall();

        public Task DeleteModelAsync(CancellationToken cancellationToken) => throw UnexpectedCall();

        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken) => throw UnexpectedCall();

        private Exception UnexpectedCall()
        {
            Interlocked.Increment(ref UnexpectedCalls);
            return new InvalidOperationException("Only the model check should run.");
        }

        public void Dispose()
        {
            CancelCheck();
            lock (_signalLock)
            {
                _disposed = true;
                _entered.Dispose();
            }
        }
    }
}
