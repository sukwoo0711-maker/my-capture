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
                messages.Add($"단축키 '{failure.Hotkey}'을(를) 등록할 수 없어 이전 값으로 되돌렸습니다.");
            }
        }

        // 3) Launch-at-login, transactionally. Never persist a value the system rejects.
        StartupApplyResult startupResult = _startup.Apply(next.General.LaunchAtLogin);
        if (!startupResult.Succeeded)
        {
            next.General.LaunchAtLogin = _startup.IsEnabled();
            messages.Add($"시작 프로그램 설정을 변경하지 못했습니다: {startupResult.Error}");
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
                "설정을 저장하지 못했습니다. 저장 폴더가 읽기 전용이거나, 디스크 공간이 부족하거나, " +
                "설정 파일 경로가 다른 항목과 충돌하는지 확인한 후 다시 시도해 주세요. " +
                "변경 내용은 적용되지 않았으며 이전 설정이 그대로 유지됩니다.",
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
                    "설정은 저장되었지만 캡처 목록 색인을 저장하지 못했습니다. " +
                    "새 보관 한도는 이번 실행에 적용되었으며, 색인은 다음 저장 시 다시 기록됩니다.");
            }
        }

        // Report the captures-root notice last so it reads after any partial-apply notes.
        if (capturesRootChanged)
        {
            messages.Add("캡처 저장 폴더 변경은 다시 시작한 후에 적용됩니다. 기존 파일은 이동하거나 삭제하지 않습니다.");
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
            RestartRequired: capturesRootChanged,
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
                    "이전 단축키를 복원하지 못했습니다. 일부 단축키가 동작하지 않을 수 있으니 프로그램을 다시 시작해 주세요.");
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Restoring the previous hotkey set threw after a failed save");
            messages.Add(
                "이전 단축키를 복원하는 중 오류가 발생했습니다. 일부 단축키가 동작하지 않을 수 있으니 프로그램을 다시 시작해 주세요.");
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
                        "시작 프로그램 설정을 이전 상태로 되돌리지 못했습니다. 설정에서 직접 확인해 주세요.");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Restoring launch-at-login threw after a failed save");
            messages.Add(
                "시작 프로그램 설정을 이전 상태로 되돌리는 중 오류가 발생했습니다. 설정에서 직접 확인해 주세요.");
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
        PasteToScreen = s.PasteToScreen,
        HideAllPins = s.HideAllPins,
        ToggleClickThrough = s.ToggleClickThrough,
        RepeatLastRegion = s.RepeatLastRegion,
        CaptureWindow = s.CaptureWindow,
        CaptureFullScreen = s.CaptureFullScreen,
    };
}
