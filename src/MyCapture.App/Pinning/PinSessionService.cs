using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>Persists live pins independently of the memory-only closed-pin history.</summary>
internal sealed class PinSessionService : IDisposable
{
    private sealed class ContentIdentity(Guid id)
    {
        internal Guid Id { get; } = id;
        internal long EncodedLength { get; set; } = -1;
    }

    private sealed record CapturedPin(PinSessionPin Pin, BitmapSource Image, ContentIdentity Identity);
    private sealed record SaveRequest(int Generation, long Sequence, IReadOnlyList<CapturedPin> Pins, bool Save);
    private const int CleanupRetryLimit = 6;
    private readonly PinManager _pins;
    private readonly PinSessionStore _store;
    private readonly Func<PinSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _log;
    private readonly DispatcherTimer _saveDelay;
    private readonly DispatcherTimer _cleanupDelay;
    private readonly ConditionalWeakTable<PinContent, ContentIdentity> _identities = new();
    private readonly Func<BitmapSource, byte[]> _encode;
    private readonly Func<byte[], BitmapSource?> _decode;
    private readonly object _stateGate = new();
    private Task<PinSessionLoadResult>? _pendingRead;
    private Task _worker = Task.CompletedTask;
    private IReadOnlyList<PinSessionSnapshot> _unrestored = [];
    private bool _baselineLoaded;
    private bool _manifestUnreadable;
    private bool _pendingClear;
    private int _cleanupAttempts;
    private volatile bool _enabled;
    private bool _restoreStarted;
    private bool _restoring;
    private bool _changedDuringRestore;
    private volatile bool _disposed;
    private int _generation;
    private long _sequence;

    internal PinSessionService(
        PinManager pins, PinSessionStore store, Func<PinSettings> settings,
        Dispatcher dispatcher, ILogger log, Func<BitmapSource, byte[]>? encode = null,
        Func<byte[], BitmapSource?>? decode = null)
    {
        _pins = pins;
        _store = store;
        _settings = settings;
        _dispatcher = dispatcher;
        _log = log;
        _encode = encode ?? ImageCodec.EncodePng;
        _decode = decode ?? ImageCodec.TryDecodePng;
        _enabled = settings().RestoreOpenPinsOnLaunch;
        if (_enabled)
        {
            // Register the read before queued startup restoration can be overtaken by exit.
            _restoring = true;
            _pendingRead = Task.Run(_store.LoadDetailed);
        }
        _saveDelay = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _cleanupDelay = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _saveDelay.Tick += OnSaveDelay;
        _cleanupDelay.Tick += OnCleanupDelay;
        _pins.OpenPinsChanged += ScheduleSave;
    }

    internal TimeSpan SynchronousSaveTimeout { get; set; } = TimeSpan.FromSeconds(3);
    internal Task PendingWorkForTest => _worker;
    internal bool HasPendingReadForTest => _pendingRead is not null;
    internal bool CleanupPendingForTest { get { lock (_stateGate) return _pendingClear; } }
    internal IReadOnlyList<object> ContentIdentitiesForTest => _identities.Select(pair => (object)pair.Value).ToArray();
    internal Task FlushDebounceForTest() { OnSaveDelay(null, EventArgs.Empty); return _worker; }

    internal Task RestoreAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _restoreStarted) return Task.CompletedTask;
        _restoreStarted = true;
        if (!_enabled)
        {
            MarkPendingClear();
            SaveNow();
            return Task.CompletedTask;
        }
        _restoring = true;
        _pendingRead ??= Task.Run(_store.LoadDetailed);
        // While the session is read and decoded, pay the first pin window's one-time cost here,
        // so it does not stall the frames between the first restored pins.
        try { PinWindowPrewarm.Run(); }
        catch (Exception ex) { _log.LogDebug(ex, "Pin window prewarm failed"); }
        return RestoreCoreAsync(_pendingRead, _generation);
    }

    private void AdoptInitialRead(PinSessionLoadResult result, int generation)
    {
        lock (_stateGate)
        {
            if (!IsGenerationCurrent(generation) || _baselineLoaded) return;
            _baselineLoaded = true;
            _manifestUnreadable = result.ManifestUnreadable;
            _unrestored = result.Pins.Select(entry => entry with { PngBytes = null }).ToArray();
        }
    }

    private async Task RestoreCoreAsync(Task<PinSessionLoadResult> read, int generation)
    {
        try
        {
            PinSessionLoadResult loaded = await read.ConfigureAwait(false);
            AdoptInitialRead(loaded, generation);
            var decoded = await Task.Run(() =>
            {
                var result = new List<(PinSessionPin Pin, PinContent Content, long Length)>();
                var corrupt = new HashSet<Guid>();
                foreach (PinSessionSnapshot entry in loaded.Pins)
                {
                    // IO-unreadable and protection-pending records keep their files without opening a window.
                    if (entry.PngBytes is not { } bytes) continue;
                    try
                    {
                        BitmapSource? image = _decode(bytes);
                        if (image is null) throw new System.IO.InvalidDataException("The saved PNG could not be decoded.");
                        if (!image.IsFrozen) image.Freeze();
                        PinSessionPin pin = entry.Pin;
                        result.Add((pin, PinContent.FromSession(image, ToContentKind(pin.Kind), pin.OriginalText,
                            pin.SourcePageUrl, pin.UsesDeviceIndependentPixels, pin.TextStyle,
                            pin.CodeDocument), bytes.LongLength));
                    }
                    catch (Exception ex)
                    {
                        corrupt.Add(entry.Pin.Id);
                        _log.LogWarning("Could not decode saved pin {PinId} ({FailureType})", entry.Pin.Id, ex.GetType().Name);
                    }
                }
                return (Pins: result, Corrupt: corrupt);
            }).ConfigureAwait(false);
            Consume(decoded.Corrupt, generation);
            // One pin per dispatcher turn, so the pins already shown keep rendering between them.
            // Every turn leaves the session consistent for an exit save: a pin is open or still carried.
            foreach ((PinSessionPin pin, PinContent content, long length) in decoded.Pins)
            {
                bool current = await _dispatcher.InvokeAsync(() =>
                {
                    if (!IsRestoreCurrent(generation)) return false;
                    try
                    {
                        _identities.Add(content, new ContentIdentity(pin.Id) { EncodedLength = length });
                        _pins.RestoreSessionPin(new PinManager.ClosedPin(content, pin.ImageWidthDip,
                            pin.ImageHeightDip, pin.Zoom, pin.Opacity, pin.Placement, pin.OpenedOrder));
                        Consume([pin.Id], generation);
                    }
                    catch (Exception ex) { _log.LogWarning("Could not reopen saved pin {PinId} ({FailureType})", pin.Id, ex.GetType().Name); }
                    return true;
                }, DispatcherPriority.Background);
                if (!current) return;
            }
            await _dispatcher.InvokeAsync(() => { if (IsRestoreCurrent(generation)) FinishRestore(); }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not restore the open-pin session ({FailureType})", ex.GetType().Name);
            if (!_dispatcher.HasShutdownStarted)
                await _dispatcher.InvokeAsync(() => { if (IsGenerationCurrent(generation)) FinishRestore(); });
        }
    }

    private bool IsRestoreCurrent(int generation) => IsGenerationCurrent(generation) && _enabled;

    /// <summary>Drops reopened or undecodable records from the carried-forward session.</summary>
    private void Consume(IReadOnlyCollection<Guid> ids, int generation)
    {
        if (ids.Count == 0) return;
        lock (_stateGate)
        {
            if (IsGenerationCurrent(generation))
                _unrestored = _unrestored.Where(entry => !ids.Contains(entry.Pin.Id)).ToArray();
        }
    }

    private void FinishRestore()
    {
        _restoring = false;
        _pendingRead = null;
        bool changed = _changedDuringRestore;
        _changedDuringRestore = false;
        if (changed) ScheduleSave();
    }

    internal void ApplySettings()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        bool enabled = _settings().RestoreOpenPinsOnLaunch;
        if (enabled != _enabled)
        {
            _enabled = enabled;
            _restoreStarted = true;
            Interlocked.Increment(ref _generation);
            Interlocked.Increment(ref _sequence);
            _restoring = false;
            _pendingRead = null;
            _saveDelay.Stop();
            lock (_stateGate)
            {
                _baselineLoaded = true;
                _manifestUnreadable = false;
                _unrestored = [];
            }
            if (!enabled) MarkPendingClear();
            SaveNow();
        }
        else if (CleanupPendingForTest)
        {
            RestartCleanupRetries();
            SaveNow();
        }
    }

    internal void ScheduleSave()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        // Invalidate a snapshot as soon as the live state changes, not a second later.
        Interlocked.Increment(ref _sequence);
        if (CleanupPendingForTest) RestartCleanupRetries();
        if (!_enabled) return;
        if (_restoring) { _changedDuringRestore = true; return; }
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void OnSaveDelay(object? sender, EventArgs e)
    {
        _saveDelay.Stop();
        try
        {
            if (!_disposed && _enabled) QueueWork(CaptureRequest(save: true));
        }
        catch (Exception ex) { _log.LogWarning("Could not capture the open-pin session for saving ({FailureType})", ex.GetType().Name); }
    }

    private SaveRequest CaptureRequest(bool save)
    {
        long sequence = Interlocked.Increment(ref _sequence);
        var captured = new List<CapturedPin>();
        if (save)
        {
            foreach (PinManager.ClosedPin pin in _pins.SnapshotOpenPins())
            {
                BitmapSource image = pin.Content.Image;
                if (!image.IsFrozen) image = (BitmapSource)image.GetAsFrozen();
                ContentIdentity identity = _identities.GetValue(pin.Content, _ => new ContentIdentity(Guid.NewGuid()));
                var record = new PinSessionPin(identity.Id, ToStoredKind(pin.Content.Kind), pin.Content.OriginalText,
                    pin.Content.SourcePageUrl, pin.Content.UsesDeviceIndependentPixels, pin.ImageWidthDip,
                    pin.ImageHeightDip, pin.Zoom, pin.Opacity, pin.Placement, pin.OpenedOrder,
                    pin.Content.TextStyle, pin.Content.CodeDocument);
                captured.Add(new CapturedPin(record, image, identity));
            }
        }
        return new SaveRequest(_generation, sequence, captured, save);
    }

    private void QueueWork(SaveRequest request)
    {
        Task<PinSessionLoadResult>? read = _pendingRead;
        _worker = _worker.ContinueWith(_ => ProcessWork(request, read), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private bool IsGenerationCurrent(int generation) => !_disposed && generation == Volatile.Read(ref _generation);
    private bool CanCommit(SaveRequest request) => IsGenerationCurrent(request.Generation) && _enabled
        && request.Sequence == Interlocked.Read(ref _sequence);

    private void ProcessWork(SaveRequest request, Task<PinSessionLoadResult>? read)
    {
        try
        {
            if (!IsGenerationCurrent(request.Generation)) return;
            if (read is not null) AdoptInitialRead(read.GetAwaiter().GetResult(), request.Generation);
            if (!IsGenerationCurrent(request.Generation)) return;
            if (CleanupPendingForTest)
            {
                bool cleared = _store.Clear();
                lock (_stateGate)
                {
                    if (cleared && IsGenerationCurrent(request.Generation)) _pendingClear = false;
                }
                if (!cleared) return;
            }
            if (!request.Save || !CanCommit(request)) return;
            bool unreadable;
            lock (_stateGate) unreadable = _manifestUnreadable;
            if (unreadable)
            {
                // Exactly one retry per save. A failed retry leaves both manifest and PNGs untouched.
                PinSessionLoadResult retry = _store.LoadDetailed();
                if (retry.ManifestUnreadable || !CanCommit(request)) return;
                lock (_stateGate)
                {
                    if (!CanCommit(request)) return;
                    _manifestUnreadable = false;
                    _unrestored = retry.Pins.Select(entry => entry with { PngBytes = null }).ToArray();
                }
            }
            IReadOnlyList<PinSessionSnapshot> carried;
            lock (_stateGate) carried = _unrestored;
            long carriedAge = carried.Select(entry => entry.Pin.OpenedOrder).DefaultIfEmpty().Max();
            var candidates = carried.Select(entry => (entry.Pin, Live: (CapturedPin?)null, entry.ProtectionPending))
                .Concat(request.Pins.Select(pin => (Pin: pin.Pin with { OpenedOrder = carriedAge + pin.Pin.OpenedOrder },
                    Live: (CapturedPin?)pin, ProtectionPending: false)))
                .Select((entry, index) => (entry.Pin, entry.Live, entry.ProtectionPending, Index: index)).ToArray();
            var admitted = new List<(int Index, PinSessionSnapshot Snapshot)>();
            var images = new HashSet<Guid>();
            long used = 0;
            int skipped = 0;
            // Recovery records reserve their slots within the same limit so newer live pins cannot orphan them.
            foreach (var candidate in candidates.OrderByDescending(entry => entry.ProtectionPending)
                .ThenByDescending(entry => entry.Pin.OpenedOrder).ThenByDescending(entry => entry.Index))
            {
                if (!CanCommit(request)) return;
                if (admitted.Count >= _store.MaxPins) { skipped++; continue; }
                byte[]? bytes = null;
                if (images.Add(candidate.Pin.Id))
                {
                    bool existing = _store.TryGetImageLength(candidate.Pin.Id, out long length);
                    if (candidate.Live is { } live)
                    {
                        existing = existing && _store.HasImage(candidate.Pin.Id);
                        if (existing) live.Identity.EncodedLength = length;
                        else
                        {
                            length = live.Identity.EncodedLength;
                            if (length > _store.MaxPngBytes - used) { images.Remove(candidate.Pin.Id); skipped++; continue; }
                            bytes = _encode(live.Image);
                            length = live.Identity.EncodedLength = bytes.LongLength;
                        }
                        if (length > _store.MaxPngBytes - used) { images.Remove(candidate.Pin.Id); skipped++; continue; }
                    }
                    else if (!existing)
                    {
                        // Preserve an existing unreadable record without evicting readable
                        // siblings merely because its metadata is temporarily unavailable.
                        length = 0;
                    }
                    used += length;
                }
                admitted.Add((candidate.Index, new PinSessionSnapshot(candidate.Pin, bytes, candidate.ProtectionPending)));
            }
            if (skipped > 0) _log.LogInformation("Skipped {Count} older open pins over the session budget", skipped);
            _store.Save(admitted.OrderBy(entry => entry.Index).Select(entry => entry.Snapshot).ToArray(), () => CanCommit(request));
        }
        catch (Exception ex) { _log.LogWarning("Could not save the open-pin session ({FailureType})", ex.GetType().Name); }
    }

    /// <summary>Waits for serialized persistence without work that needs the dispatcher.</summary>
    internal void SaveNow()
    {
        _dispatcher.VerifyAccess();
        _saveDelay.Stop();
        if (_disposed) return;
        if (CleanupPendingForTest) RestartCleanupRetries();
        if (!_enabled && !CleanupPendingForTest) return;
        try
        {
            SaveRequest request = CaptureRequest(save: _enabled);
            QueueWork(request);
            if (!_worker.Wait(SynchronousSaveTimeout))
            {
                Interlocked.Increment(ref _sequence);
                _log.LogWarning("Timed out saving open pins; retained the previously committed session");
            }
        }
        catch (Exception ex) { _log.LogWarning("Could not finish saving the open-pin session ({FailureType})", ex.GetType().Name); }
    }

    private void MarkPendingClear()
    {
        lock (_stateGate) _pendingClear = true;
        RestartCleanupRetries();
    }

    private void RestartCleanupRetries()
    {
        _cleanupAttempts = 0;
        _cleanupDelay.Stop();
        _cleanupDelay.Start();
    }

    private void OnCleanupDelay(object? sender, EventArgs e)
    {
        if (_disposed || !CleanupPendingForTest)
        {
            _cleanupDelay.Stop();
            if (!_disposed && _enabled) ScheduleSave();
            return;
        }
        if (_cleanupAttempts >= CleanupRetryLimit) { _cleanupDelay.Stop(); return; }
        if (!_worker.IsCompleted) return;
        _cleanupAttempts++;
        QueueWork(CaptureRequest(save: false));
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _sequence);
        _pendingRead = null;
        _saveDelay.Stop();
        _cleanupDelay.Stop();
        _saveDelay.Tick -= OnSaveDelay;
        _cleanupDelay.Tick -= OnCleanupDelay;
        _pins.OpenPinsChanged -= ScheduleSave;
    }

    private static PinContentKind ToContentKind(PinSessionContentKind kind) => kind switch
    {
        PinSessionContentKind.Image => PinContentKind.Image,
        PinSessionContentKind.Text => PinContentKind.Text,
        PinSessionContentKind.Table => PinContentKind.Table,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PinSessionContentKind ToStoredKind(PinContentKind kind) => kind switch
    {
        PinContentKind.Image => PinSessionContentKind.Image,
        PinContentKind.Text => PinSessionContentKind.Text,
        PinContentKind.Table => PinSessionContentKind.Table,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
