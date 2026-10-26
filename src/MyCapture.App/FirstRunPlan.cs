using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>What the shell opens itself at launch.</summary>
/// <param name="ShowWelcome">The first-run welcome window.</param>
/// <param name="OpenLibrary">The library, opened right away.</param>
/// <param name="ShowCoreShortcuts">The unanswered shortcut question, before any other launch window.</param>
internal readonly record struct LaunchPresentation(bool ShowWelcome, bool OpenLibrary, bool ShowCoreShortcuts = false);

/// <summary>What follows the welcome window.</summary>
internal enum AfterWelcome
{
    Nothing,
    OpenLibrary,
    StartCapture,
    OpenSettings,
}

/// <summary>What became of the welcome's start-at-sign-in choice.</summary>
/// <param name="Enabled">Windows will start MyCapture at sign-in; the setting records exactly this.</param>
/// <param name="Notice">Why the requested state could not be reached, worded for the user; null when it was.</param>
internal sealed record StartAtSignInOutcome(bool Enabled, string? Notice);

/// <summary>
/// First-run presentation: one window at a time. The unanswered core-shortcut question comes
/// first, including on a quiet launch. After the answer, a first run shows the welcome window (no
/// library behind it and no notification on top); the library follows once it closes, with the
/// "ready" notification naming the capture key. Later launches keep their behaviour: the library
/// opens unless the launch is a quiet sign-in launch. They show no "ready" notification: it would
/// repeat at every sign-in what the user already knows.
/// </summary>
internal static class FirstRunPlan
{
    internal static LaunchPresentation ForLaunch(bool isFirstRun, bool quietLaunch, bool openSettings, bool coreShortcutsAsked = true) => !coreShortcutsAsked
        ? new LaunchPresentation(ShowWelcome: false, OpenLibrary: false, ShowCoreShortcuts: true)
        : isFirstRun
        ? new LaunchPresentation(ShowWelcome: true, OpenLibrary: false)
        : new LaunchPresentation(ShowWelcome: false, OpenLibrary: !quietLaunch && !openSettings);

    internal static AfterWelcome Next(WelcomeOutcome outcome) => outcome switch
    {
        WelcomeOutcome.Closed => AfterWelcome.OpenLibrary,
        // The guide's third step already opened the library; finishing does not open it a second time.
        WelcomeOutcome.Completed => AfterWelcome.Nothing,
        WelcomeOutcome.TryCapture => AfterWelcome.StartCapture,
        WelcomeOutcome.OpenSettings => AfterWelcome.OpenSettings,
        _ => AfterWelcome.Nothing,
    };

    /// <summary>
    /// Whether the "ready" notification follows the welcome. Not when a capture starts instead:
    /// the capture overlay would close it at once.
    /// </summary>
    internal static bool AnnouncesReady(WelcomeOutcome outcome) =>
        outcome is WelcomeOutcome.Closed or WelcomeOutcome.Completed or WelcomeOutcome.OpenSettings;

    /// <summary>
    /// Applies the welcome's start-at-sign-in choice through <paramref name="registration"/> (the
    /// Store build's startup task or the Run key) and reports what Windows really does now. A
    /// startup app the user turned off in Windows, or one an organization manages, is never
    /// overridden; the notice says where to change it.
    /// </summary>
    internal static StartAtSignInOutcome ApplyStartAtSignIn(bool requested, IStartupRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        StartupApplyResult result = registration.Apply(requested);
        bool enabled;
        try { enabled = registration.IsEnabled(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            enabled = result.Succeeded && requested;
        }
        string? notice = result.Succeeded ? null
            : UiText.Format("Text_0FA1A9D03BE3", result.Error ?? UiText.Get("Text_4D69B33A2FD7"));
        return new StartAtSignInOutcome(enabled, notice);
    }
}
