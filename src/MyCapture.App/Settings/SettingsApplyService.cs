using System.IO;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Settings;

/// <summary>
/// One place that knows how to make an edited <see cref="AppSettings"/> take effect
/// across the running process.
/// </summary>
/// <remarks>
/// <para>
/// Applying settings touches several independent subsystems, and the order matters.
/// Doing it here — rather than inline in the window's code-behind — keeps the sequence
/// auditable and lets the risky pieces (hotkey reconfigure, autostart) report failure
/// without the window having to understand each subsystem.
/// </para>
/// <para>
/// The whole apply is a transaction around persistence. Reconfiguring global hotkeys and
/// the launch-at-login registration changes live OS state, so if the settings file cannot
/// then be written the app would otherwise be left with new hotkeys and a new Run key but
/// old, still-live settings — and the write exception would escape into the dispatcher's
/// unhandled-exception handler. To prevent that, the sequence is:
/// </para>
/// <list type="number">
/// <item>Snapshot the previous settings, the previous hotkey set, and the <em>actual</em>
/// launch-at-login state as the OS currently reports it.</item>
/// <item>Reconfigure global hotkeys (rolls back internally on collision).</item>
/// <item>Apply launch-at-login, surfacing — never hiding — a registration failure.</item>
/// <item>Persist atomically through <see cref="SettingsStore"/>. If this throws a
/// persistence fault (read-only workspace, full disk, a directory where the file should
/// be), reconfigure the hotkeys back to the previous working set, restore the Run key to
/// its actual prior state, publish nothing, touch the queue not at all, and return an
/// unsuccessful result carrying an actionable Korean message rather than throwing.</item>
/// <item>Publish the new object so existing <c>Func&lt;AppSettings&gt;</c> suppliers read it.</item>
/// <item>Push queue caps and save the index. A failure here is non-fatal: the user's
/// settings are already persisted, so it is reported as a message without reversing them.</item>
/// </list>
/// <para>
/// Relocating the captures directory is reported as "restart required" rather than
/// moving or deleting the user's existing files: silently moving gigabytes of captures
/// is the kind of surprise a capture tool must never spring.
/// </para>
/// </remarks>
public sealed class SettingsApplyService
{
    private readonly SettingsStore _store;
    private readonly Action<AppSettings> _publish;
    private readonly Func<AppSettings> _current;
    private readonly CaptureQueue? _queue;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly StartupRegistrationService _startup;
    private readonly ILogger _log;

    public SettingsApplyService(
        SettingsStore store,
        Func<AppSettings> current,
        Action<AppSettings> publish,
        CaptureQueue? queue,
        GlobalHotkeyService hotkeys,
        StartupRegistrationService startup,
        ILogger log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _queue = queue;
        _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Applies <paramref name="next"/>. Returns a result describing the outcome the UI
    /// should surface.
    /// </summary>
    /// <remarks>
    /// When persistence succeeds (<see cref="SettingsApplyResult.Saved"/> is
    /// <see langword="true"/>) the applied settings are live and stored, even if a hotkey
    /// reconfigure rolled back or autostart could not be registered — those are recorded
    /// as messages and the stored chords are what a later launch will use. When
    /// persistence fails, no OS-visible change survives: hotkeys and the Run key are
    /// restored to their prior state, nothing is published, the queue is untouched, and
    /// <see cref="SettingsApplyResult.Saved"/> is <see langword="false"/> so the window
    /// stays open with the user's draft intact.
    /// </remarks>
    public SettingsApplyResult Apply(AppSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);

        AppSettings previous = _current();
        var messages = new List<string>();

        // 1) Snapshot everything a rollback would need to restore, captured before any
        //    OS-visible change. The hotkey set is the *previous, working* set; the startup
        //    state is what the OS actually reports right now, not what the old settings
        //    file claimed — the two can differ if the Run key was hand-edited.
        HotkeySettings previousHotkeys = previous.Hotkeys.DeepCloneHotkeys();
        bool startupWasEnabled = _startup.IsEnabled();

        // 2) Hotkeys first, transactionally, so a collision can veto the chords before
        //    anything else changes. On rollback the app keeps its previous, working set.
        HotkeyReconfigureResult hotkeyResult = _hotkeys.Reconfigure(next.Hotkeys);
        if (!hotkeyResult.Applied)
        {
            // Keep the previously working chords in what we persist, so the saved file
            // never claims a set the app could not register.
            next.Hotkeys = previousHotkeys.DeepCloneHotkeys();
            foreach (HotkeyRegistrationFailure failure in hotkeyResult.Failures)
            {
                messages.Add(UiText.Format("Text_2B53D424B10E", failure.Hotkey));
            }
        }

        // 3) Launch-at-login, transactionally. Never persist a value the system rejects.
        StartupApplyResult startupResult = _startup.Apply(next.General.LaunchAtLogin);
        if (!startupResult.Succeeded)
        {
            next.General.LaunchAtLogin = _startup.IsEnabled();
            messages.Add(UiText.Format("Text_0FA1A9D03BE3", startupResult.Error));
        }

        // 4) Captures directory relocation is restart-required, not a live move.
        bool capturesRootChanged = !string.Equals(
            (previous.Queue.CapturesDirectoryOverride ?? string.Empty).Trim(),
            (next.Queue.CapturesDirectoryOverride ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

        // 5) Persist atomically. This is the point of no return: only when the write
        //    succeeds do we publish and touch the queue. A persistence fault rolls the
        //    OS-visible changes (hotkeys, Run key) back to how they were on entry.
        try
        {
            _store.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(
                ex,
                "Persisting settings failed; rolling back hotkeys and launch-at-login to their prior state");

            List<string> rollbackMessages = RollBackAfterFailedSave(previousHotkeys, startupWasEnabled);

            var failureMessages = new List<string>
            {
                UiText.Get("Text_ABAB2010D671"),
            };
            failureMessages.AddRange(rollbackMessages);

            return SettingsApplyResult.NotSaved(failureMessages);
        }

        // 6) Publish so existing Func suppliers see the new object immediately.
        _publish(next);

        // 7) Queue caps take effect now (except a root move, which waits for restart).
        //    A failure here is non-fatal: the user's settings are already persisted, so it
        //    must NOT reverse them. Lowering MaxItems/MaxBytes can evict older captures —
        //    that eviction is intentional and stays applied; only the *persistence* of the
        //    trimmed index is what can fail, and that is what we report.
        if (_queue is not null)
        {
            try
            {
                _queue.UpdateLimits(next.Queue);
                _queue.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(
                    ex,
                    "Queue limits applied but the index could not be saved; settings remain persisted");
                messages.Add(
                    UiText.Get("Text_6DDE7DF5BF97"));
            }
        }

        MyCapture.App.Themes.ThemeService.ApplyFromSettings(next.General.Theme);

        bool languageChanged = !string.Equals(previous.General.Language, next.General.Language, StringComparison.OrdinalIgnoreCase);
        if (languageChanged)
        {
            messages.Add(UiText.Get("Settings.LanguageRestart"));
        }

        // Report the captures-root notice last so it reads after any partial-apply notes.
        if (capturesRootChanged)
        {
            messages.Add(UiText.Get("Text_D8538A946A6C"));
        }

        _log.LogInformation(
            "Settings applied (hotkeys={HotkeyState}, autostart={Autostart}, messages={MessageCount})",
            hotkeyResult.Applied ? "ok" : "rolled-back",
            startupResult.Succeeded ? next.General.LaunchAtLogin : "unchanged",
            messages.Count);

        return new SettingsApplyResult(
            Saved: true,
            HotkeysApplied: hotkeyResult.Applied,
            StartupApplied: startupResult.Succeeded,
            RestartRequired: capturesRootChanged || languageChanged,
            Messages: messages);
    }

    /// <summary>
    /// Undoes the OS-visible changes made before a failed save: restores the previous
    /// hotkey set and returns the launch-at-login registration to the exact state the OS
    /// reported on entry. Returns any messages describing a rollback that could not itself
    /// be completed, so the caller never silently claims a clean revert.
    /// </summary>
    private List<string> RollBackAfterFailedSave(HotkeySettings previousHotkeys, bool startupWasEnabled)
    {
        var messages = new List<string>();

        // Restore the previous, working hotkey set. Reconfigure rolls back internally on a
        // collision, but restoring a set the app already held should not collide.
        try
        {
            HotkeyReconfigureResult restore = _hotkeys.Reconfigure(previousHotkeys);
            if (!restore.Applied)
            {
                _log.LogCritical(
                    "Could not restore the previous hotkey set after a failed save; {Count} chord(s) did not re-register",
                    restore.Failures.Count);
                messages.Add(
                    UiText.Get("Text_F47CC25B5D11"));
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Restoring the previous hotkey set threw after a failed save");
            messages.Add(
                UiText.Get("Text_B2B3E2E19B90"));
        }

        // Return the Run key to exactly the state the OS reported on entry. Only act when
        // the current state actually differs, so a no-op stays a no-op.
        try
        {
            if (_startup.IsEnabled() != startupWasEnabled)
            {
                StartupApplyResult restore = _startup.Apply(startupWasEnabled);
                if (!restore.Succeeded)
                {
                    _log.LogCritical(
                        "Could not restore launch-at-login to its prior state ({Prior}) after a failed save: {Error}",
                        startupWasEnabled,
                        restore.Error);
                    messages.Add(
                        UiText.Get("Text_B64A6FFA1008"));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Restoring launch-at-login threw after a failed save");
            messages.Add(
                UiText.Get("Text_4E99F6190A6C"));
        }

        return messages;
    }
}

/// <summary>
/// The outcome of an apply the UI needs to react to.
/// </summary>
/// <remarks>
/// <see cref="Saved"/> is the load-bearing flag: it is <see langword="true"/> only when
/// the settings were persisted. On <see langword="false"/> nothing OS-visible changed —
/// the window must stay open and keep the user's draft rather than reload or hide. The
/// remaining flags describe non-fatal partial outcomes of a <em>saved</em> apply.
/// </remarks>
public sealed record SettingsApplyResult(
    bool Saved,
    bool HotkeysApplied,
    bool StartupApplied,
    bool RestartRequired,
    IReadOnlyList<string> Messages)
{
    /// <summary>
    /// A persistence failure: settings were not saved, all OS-visible changes were rolled
    /// back, and <paramref name="messages"/> explain what to do (and any rollback trouble).
    /// </summary>
    public static SettingsApplyResult NotSaved(IReadOnlyList<string> messages) =>
        new(Saved: false, HotkeysApplied: false, StartupApplied: false, RestartRequired: false, messages);
}

internal static class HotkeyCloneExtensions
{
    // A private, focused clone so applying can restore the previous hotkey set without
    // depending on a full AppSettings clone.
    public static HotkeySettings DeepCloneHotkeys(this HotkeySettings s) => new()
    {
        Capture = s.Capture,
        OpenLibrary = s.OpenLibrary,
        PasteToScreen = s.PasteToScreen,
        HideAllPins = s.HideAllPins,
        ToggleClickThrough = s.ToggleClickThrough,
        RepeatLastRegion = s.RepeatLastRegion,
        CaptureWindow = s.CaptureWindow,
        CaptureFullScreen = s.CaptureFullScreen,
        RecordRegion = s.RecordRegion,
        UploadGitHubImage = s.UploadGitHubImage,
    };
}
