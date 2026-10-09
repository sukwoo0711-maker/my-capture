using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>
/// Shell glue for the one-time Store rating request. The capture pipeline only reports
/// outcomes and flow ends; the decisions live in <see cref="StoreRatingCoordinator"/>
/// and <see cref="StoreRatingPolicy"/>.
/// </summary>
public partial class App
{
    private StoreRatingCoordinator? _storeRating;

    private void InitializeStoreRating()
    {
        if (_services is null) return;
        ServiceProvider services = _services;
        _storeRating = new StoreRatingCoordinator(
            () => _settings!,
            settings => services.GetRequiredService<SettingsStore>().Save(settings),
            write => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, write),
            IsStoreRatingDeferred,
            new StoreContextRatingRequester(),
            services.GetRequiredService<ILogger<StoreRatingCoordinator>>(),
            AppPackage.IsPackaged);
    }

    /// <summary>Anything on screen that a Store dialog must not interrupt.</summary>
    private bool IsStoreRatingDeferred() =>
        _overlay?.IsActive == true || _recorder?.IsActive == true || _activeCountdown is not null
        || _scrollCancellation is not null || _preparingAdvancedCapture || _quickAccess is not null
        || _pasteToScreenInFlight || HotkeyRecorder.Active is not null
        || Windows.OfType<Window>().Any(window => window.IsVisible
            && window is AnnotationEditorWindow or MyCapture.App.Recording.VideoEditorWindow or ShortcutHelpWindow);
}
