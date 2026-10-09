using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// Shell glue for the one-time Store rating request and the Print Screen key guidance.
/// The capture pipeline only reports outcomes and flow ends; the decisions live in
/// <see cref="StoreRatingCoordinator"/>, <see cref="StoreRatingPolicy"/> and
/// <see cref="PrintScreenKeyGuide"/>.
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
        if (_settingsApply is not null)
            _settingsApply.PrintScreenCaptureKeyEnabled = PrintScreenKeyGuide.IsWindowsCaptureKeyEnabled;
    }

    /// <summary>Anything on screen that a Store dialog must not interrupt.</summary>
    private bool IsStoreRatingDeferred() =>
        _overlay?.IsActive == true || _recorder?.IsActive == true || _activeCountdown is not null
        || _scrollCancellation is not null || _preparingAdvancedCapture || _quickAccess is not null
        || _pasteToScreenInFlight || HotkeyRecorder.Active is not null
        || Windows.OfType<Window>().Any(window => window.IsVisible
            && window is AnnotationEditorWindow or MyCapture.App.Recording.VideoEditorWindow or ShortcutHelpWindow);

    /// <summary>
    /// A line for the startup shortcut-failure notice when a failed shortcut uses Print Screen
    /// while Windows claims that key for its own capture tool; otherwise empty.
    /// </summary>
    private static string PrintScreenFailureLine(IEnumerable<HotkeyRegistrationFailure> failures)
    {
        string notice = PrintScreenKeyGuide.FailureNotice(
            failures.Select(failure => failure.Hotkey),
            PrintScreenKeyGuide.IsWindowsCaptureKeyEnabled());
        return notice.Length == 0 ? string.Empty : "\n" + notice;
    }
}
