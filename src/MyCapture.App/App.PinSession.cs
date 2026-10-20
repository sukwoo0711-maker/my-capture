using System.Windows.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Pinning;
using MyCapture.App.Settings;
using MyCapture.Core.Pin;
using MyCapture.Core.Storage;

namespace MyCapture.App;

public partial class App
{
    private PinSessionService? _pinSession;
    private bool _pinSessionSavedForSessionEnd;

    private void InitializePinSession()
    {
        if (_services is null || _pins is null || _settings is null) return;
        try
        {
            var log = _services.GetRequiredService<ILogger<PinSessionService>>();
            var store = new PinSessionStore(_services.GetRequiredService<AppPaths>().PinSessionRoot, log);
            _pinSession = new PinSessionService(_pins, store, () => _settings!.Pin, Dispatcher, log);
            // InitializeShell finishes hotkey registration before this dispatcher turn, on
            // both interactive and quiet sign-in launches.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = _pinSession.RestoreAsync()));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not initialize the open-pin session");
        }
    }

    private void OnPinSessionSettingsApplied(object? sender, SettingsApplyResult result)
    {
        if (result.Saved) _pinSession?.ApplySettings();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel)
        {
            _pinSession?.SaveNow();
            _pinSessionSavedForSessionEnd = true;
        }
    }

    private void FinishPinSessionOnExit()
    {
        // WPF has already closed Application.Windows by OnExit. A session-end snapshot
        // must survive that closure. Keep the service alive if Windows later cancels logoff.
        if (_pinSessionSavedForSessionEnd) _pinSession?.Dispose();
        else SavePinSessionForExit();
    }

    private void SavePinSessionForExit()
    {
        try
        {
            _pinSession?.SaveNow();
            _pinSession?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not finish the open-pin session on exit");
        }
    }
}
