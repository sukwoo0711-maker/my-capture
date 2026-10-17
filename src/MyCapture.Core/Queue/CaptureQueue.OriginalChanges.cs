using System.Runtime.CompilerServices;

namespace MyCapture.Core.Queue;

public sealed partial class CaptureQueue
{
    // Runtime identity, separate from the rendered ContentRevision. Weak keys retain the
    // generation for suspended readers without retaining records removed from the library.
    private readonly ConditionalWeakTable<CaptureRecord, OriginalState> _originalStates = new();

    /// <summary>
    /// Call on the queue owner before replacing an existing original. The returned scope
    /// excludes it from import deduplication until disposed, including rollback in finally.
    /// Both metadata and index have a null hash before this method returns.
    /// </summary>
    public async Task<IDisposable> BeginOriginalChangeAsync(CaptureRecord record,
        CancellationToken cancellationToken = default)
    {
        IDisposable change = AcquireOriginalChange(record);
        try
        {
            using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
            EnsureOriginalChangeRecord(record);
            record.OriginalContentHash = null;
            await PublishRecordAsync(record, reservation);
            return change;
        }
        catch
        {
            change.Dispose();
            throw;
        }
    }

    /// <summary>Synchronous owner/recovery equivalent. Like Save(), bypasses async capacity
    /// to avoid blocking the owner behind reservations whose continuations need that owner.</summary>
    public IDisposable BeginOriginalChange(CaptureRecord record)
    {
        IDisposable change = AcquireOriginalChange(record);
        try
        {
            record.OriginalContentHash = null;
            PublishRecord(record, null).GetAwaiter().GetResult();
            return change;
        }
        catch
        {
            change.Dispose();
            throw;
        }
    }

    internal bool TryGetOriginalGeneration(CaptureRecord record, out long generation)
    {
        _originalStates.TryGetValue(record, out OriginalState? state);
        generation = state?.Generation ?? 0;
        return state?.Changing != true;
    }

    private IDisposable AcquireOriginalChange(CaptureRecord record)
    {
        EnsureOriginalChangeRecord(record);
        OriginalState state = _originalStates.GetOrCreateValue(record);
        if (state.Changing) throw new InvalidOperationException("The capture original is already being changed.");
        long generation = checked(state.Generation + 1);
        IDisposable evictionLease = AcquireEvictionLease(record.Id);
        state.Generation = generation;
        state.Changing = true;
        return new OriginalChange(state, evictionLease);
    }

    private void EnsureOriginalChangeRecord(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!ReferenceEquals(Find(record.Id), record) || IsRemovalPending(record.Id))
            throw new InvalidOperationException("The capture no longer owns its original.");
    }

    private sealed class OriginalState
    {
        internal long Generation;
        internal bool Changing;
    }

    private sealed class OriginalChange(OriginalState state, IDisposable evictionLease) : IDisposable
    {
        private IDisposable? _evictionLease = evictionLease;

        public void Dispose()
        {
            IDisposable? lease = Interlocked.Exchange(ref _evictionLease, null);
            if (lease is null) return;
            state.Changing = false;
            lease.Dispose();
        }
    }
}
