namespace MyCapture.Core.Settings;

/// <summary>
/// Launch-at-login as the settings pipeline sees it. The unpackaged build registers through the
/// per-user Run key; the Microsoft Store build registers through its manifest startup task,
/// because a packaged app's Run key write is virtualized and never runs at sign-in.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>True only when Windows will actually start this build at sign-in.</summary>
    bool IsEnabled();

    /// <summary>
    /// Brings the registration in line with <paramref name="desiredEnabled"/> and reports the
    /// real outcome, so the caller never persists a state the system rejected.
    /// </summary>
    StartupApplyResult Apply(bool desiredEnabled);

    /// <summary>Best-effort launch reconciliation with the saved setting. Never throws.</summary>
    bool ReconcileOnStartup(bool settingEnabled);
}
