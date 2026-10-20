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
        internal byte[]? PngBytes { get; set; }
    }

    private readonly PinManager _pins;
    private readonly PinSessionStore _store;
    private readonly Func<PinSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _log;
    private readonly DispatcherTimer _saveDelay;
    private readonly ConditionalWeakTable<PinContent, ContentIdentity> _identities = new();
    private readonly Func<BitmapSource, byte[]> _encode;
    private readonly Func<byte[], BitmapSource?> _decode;
    private Task<IReadOnlyList<PinSessionSnapshot>>? _pendingRead;
    private bool _enabled;
    private bool _restoreStarted;
    private bool _restoring;
    private bool _changedDuringRestore;
    private bool _disposed;
    private int _generation;

    internal PinSessionService(
        PinManager pins,
        PinSessionStore store,
        Func<PinSettings> settings,
        Dispatcher dispatcher,
        ILogger log,
        Func<BitmapSource, byte[]>? encode = null,
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
            // Register the read now, before the queued startup restore can be overtaken by exit.
            _restoring = true;
            _pendingRead = Task.Run(_store.Load);
        }
        _saveDelay = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _saveDelay.Tick += OnSaveDelay;
        _pins.OpenPinsChanged += ScheduleSave;
    }

    internal Task RestoreAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _restoreStarted) return Task.CompletedTask;
        _restoreStarted = true;
        if (!_enabled)
        {
            // A previous process may have exited between saving the off setting and clearing.
            _store.Clear();
            return Task.CompletedTask;
        }

        _restoring = true;
        _pendingRead ??= Task.Run(_store.Load);
        return RestoreCoreAsync(_pendingRead, _generation);
    }

    private async Task RestoreCoreAsync(Task<IReadOnlyList<PinSessionSnapshot>> read, int generation)
    {
        try
        {
            IReadOnlyList<PinSessionSnapshot> saved = await read.ConfigureAwait(false);
            // Decode and freeze away from the dispatcher; only creating/showing windows is UI work.
            var decoded = await Task.Run(() =>
            {
                var result = new List<(PinSessionPin Pin, PinContent Content, byte[] Bytes)>();
                foreach (PinSessionSnapshot entry in saved)
                {
                    try
                    {
                        BitmapSource? image = entry.PngBytes is { } bytes ? _decode(bytes) : null;
                        if (image is null)
                        {
                            _log.LogWarning("Could not decode saved pin {PinId}", entry.Pin.Id);
                            continue;
                        }
                        if (!image.IsFrozen) image.Freeze();
                        PinSessionPin pin = entry.Pin;
                        var kind = pin.Kind switch
                        {
                            PinSessionContentKind.Image => PinContentKind.Image,
                            PinSessionContentKind.Text => PinContentKind.Text,
                            PinSessionContentKind.Table => PinContentKind.Table,
                            _ => throw new ArgumentOutOfRangeException(nameof(pin.Kind)),
                        };
                        result.Add((pin, PinContent.FromSession(image, kind, pin.OriginalText,
                            pin.SourcePageUrl, pin.UsesDeviceIndependentPixels), entry.PngBytes!));
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Could not decode saved pin {PinId}", entry.Pin.Id);
                    }
                }
                return result;
            }).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_enabled || generation != _generation) return;
                foreach ((PinSessionPin pin, PinContent content, byte[] bytes) in decoded)
                {
                    try
                    {
                        _identities.Add(content, new ContentIdentity(pin.Id) { PngBytes = bytes });
                        _pins.RestoreSessionPin(new PinManager.ClosedPin(content, pin.ImageWidthDip,
                            pin.ImageHeightDip, pin.Zoom, pin.Opacity, pin.Placement, pin.OpenedOrder));
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Could not reopen saved pin {PinId}", pin.Id);
                    }
                }
                _restoring = false;
                _pendingRead = null;
                if (_changedDuringRestore) ScheduleSave();
                _changedDuringRestore = false;
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not restore the open-pin session");
            if (!_dispatcher.HasShutdownStarted)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (generation != _generation || _disposed) return;
                    _restoring = false;
                    _pendingRead = null;
                    if (_changedDuringRestore) ScheduleSave();
                    _changedDuringRestore = false;
                });
            }
        }
    }

    internal void ApplySettings()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        bool enabled = _settings().RestoreOpenPinsOnLaunch;
        if (enabled == _enabled) return;
        _enabled = enabled;
        // A user toggle starts a new session from the current windows, even if startup's
        // queued restore has not run yet.
        _restoreStarted = true;
        _generation++;
        _restoring = false;
        _pendingRead = null;
        _saveDelay.Stop();
        if (enabled) SaveNow();
        else
        {
            try { _store.Clear(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not clear the saved open-pin session"); }
        }
    }

    internal void ScheduleSave()
    {
        if (_disposed || !_enabled) return;
        if (_restoring)
        {
            _changedDuringRestore = true;
            return;
        }
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void OnSaveDelay(object? sender, EventArgs e) => SaveNow();

    /// <summary>Also used synchronously at session end and before any exit closes live pins.</summary>
    internal void SaveNow()
    {
        _dispatcher.VerifyAccess();
        _saveDelay.Stop();
        if (_disposed || !_enabled) return;
        try
        {
            var snapshots = new List<PinSessionSnapshot>();
            // Shutdown may arrive while decoding. Keep those not-yet-open pins in the saved
            // session; the file read has no dispatcher dependency and needs no PNG re-encode.
            if (_restoring && _pendingRead is { } pending)
                snapshots.AddRange(pending.GetAwaiter().GetResult().Select(entry => entry with { PngBytes = null }));

            IReadOnlyList<PinManager.ClosedPin> live = _pins.SnapshotOpenPins();
            int skipped = Math.Max(0, live.Count - _store.MaxPins);
            if (skipped > 0) _log.LogInformation("Skipped {Count} older open pins over the session count budget", skipped);
            var newest = live.OrderByDescending(pin => pin.OpenedOrder).Take(_store.MaxPins).ToHashSet();
            long pendingAge = snapshots.Select(entry => entry.Pin.OpenedOrder).DefaultIfEmpty().Max();
            foreach (PinManager.ClosedPin pin in live.Where(newest.Contains))
            {
                ContentIdentity identity = _identities.GetValue(pin.Content, _ => new ContentIdentity(Guid.NewGuid()));
                var kind = pin.Content.Kind switch
                {
                    PinContentKind.Image => PinSessionContentKind.Image,
                    PinContentKind.Text => PinSessionContentKind.Text,
                    PinContentKind.Table => PinSessionContentKind.Table,
                    _ => throw new ArgumentOutOfRangeException(nameof(pin.Content.Kind)),
                };
                var saved = new PinSessionPin(identity.Id, kind, pin.Content.OriginalText,
                    pin.Content.SourcePageUrl, pin.Content.UsesDeviceIndependentPixels, pin.ImageWidthDip,
                    pin.ImageHeightDip, pin.Zoom, pin.Opacity, pin.Placement, pendingAge + pin.OpenedOrder);
                byte[]? bytes = null;
                if (!_store.HasImage(identity.Id))
                    bytes = identity.PngBytes ??= _encode(pin.Content.Image);
                snapshots.Add(new PinSessionSnapshot(saved, bytes));
            }
            _store.Save(snapshots);
        }
        catch (Exception ex)
        {
            // Session persistence is optional and must never escape the shutdown path.
            _log.LogWarning(ex, "Could not save the open-pin session");
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _saveDelay.Stop();
        _saveDelay.Tick -= OnSaveDelay;
        _pins.OpenPinsChanged -= ScheduleSave;
    }
}
