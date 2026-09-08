namespace MyCapture.Core.Queue;

/// <summary>A bounded publication slot. Dispose an unused reservation; accepted writes release it themselves.</summary>
public sealed class CaptureWriteReservation : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private int _state;

    internal CaptureWriteReservation(SemaphoreSlim slots) => _slots = slots;

    internal void Accept(SemaphoreSlim slots)
    {
        if (!ReferenceEquals(slots, _slots) || Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("The publication reservation is not available for this queue.");
    }

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _state, 2) == 1) _slots.Release();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) _slots.Release();
    }
}
