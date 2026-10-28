using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Media;

/// <summary>Owns only pending tooltip reads; card creation and scrolling never start a read.</summary>
internal sealed class MediaDetailsToolTipLoader
{
    private readonly Dictionary<FrameworkElement, CancellationTokenSource> _pending = [];
    private readonly Func<IReadOnlyList<MediaDetailsSource>, CancellationToken, Task<IReadOnlyList<MediaDetails>>> _read;

    internal MediaDetailsToolTipLoader(Window owner,
        Func<IReadOnlyList<MediaDetailsSource>, CancellationToken, Task<IReadOnlyList<MediaDetails>>>? read = null)
    {
        _read = read ?? MediaDetailsReader.ReadAsync;
        owner.IsVisibleChanged += (_, _) => { if (!owner.IsVisible) CancelAll(); };
        owner.Closed += (_, _) => CancelAll();
    }

    internal async Task LoadAsync(FrameworkElement target, object dataContext, ToolTip tooltip,
        MediaDetailsSource source, Action<MediaDetails> apply)
    {
        target.Dispatcher.VerifyAccess();
        if (_pending.TryGetValue(target, out CancellationTokenSource? previous)) previous.Cancel();
        using var cancellation = new CancellationTokenSource();
        _pending[target] = cancellation;
        CancellationToken token = cancellation.Token;
        void Cancel(object? sender, RoutedEventArgs args) => cancellation.Cancel();
        void Recycled(object? sender, DependencyPropertyChangedEventArgs args) => cancellation.Cancel();
        target.Unloaded += Cancel;
        target.DataContextChanged += Recycled;
        tooltip.Closed += Cancel;
        try
        {
            IReadOnlyList<MediaDetails> details = await _read([source], token);
            // A decoder already in progress can finish after cancellation; never apply that result.
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(target.DataContext, dataContext) && ReferenceEquals(target.ToolTip, tooltip))
                apply(details[0]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            target.Unloaded -= Cancel;
            target.DataContextChanged -= Recycled;
            tooltip.Closed -= Cancel;
            if (_pending.TryGetValue(target, out CancellationTokenSource? current) && ReferenceEquals(current, cancellation))
                _pending.Remove(target);
        }
    }

    private void CancelAll()
    {
        // Cancellation can synchronously complete an awaiting reader and remove its entry.
        foreach (CancellationTokenSource cancellation in _pending.Values.ToArray()) cancellation.Cancel();
    }
}
