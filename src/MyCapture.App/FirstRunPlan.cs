namespace MyCapture.App;

/// <summary>What the shell opens itself at launch.</summary>
/// <param name="ShowWelcome">The first-run welcome window.</param>
/// <param name="OpenLibrary">The library, opened right away.</param>
/// <param name="ShowReadyNotification">The "running in the tray" notification.</param>
internal readonly record struct LaunchPresentation(bool ShowWelcome, bool OpenLibrary, bool ShowReadyNotification);

/// <summary>What follows the welcome window.</summary>
internal enum AfterWelcome
{
    Nothing,
    OpenLibrary,
    StartCapture,
    OpenSettings,
}

/// <summary>
/// First-run presentation: one window at a time. A first run shows only the welcome window (no
/// library behind it and no ready notification on top); the library follows once it closes.
/// Later launches keep their behaviour: the library opens unless the launch is a quiet sign-in
/// launch, and the ready notification is shown once.
/// </summary>
internal static class FirstRunPlan
{
    internal static LaunchPresentation ForLaunch(bool isFirstRun, bool quietLaunch, bool openSettings) => isFirstRun
        ? new LaunchPresentation(ShowWelcome: true, OpenLibrary: false, ShowReadyNotification: false)
        : new LaunchPresentation(ShowWelcome: false, OpenLibrary: !quietLaunch && !openSettings, ShowReadyNotification: true);

    internal static AfterWelcome Next(WelcomeOutcome outcome) => outcome switch
    {
        WelcomeOutcome.Closed => AfterWelcome.OpenLibrary,
        WelcomeOutcome.TryCapture => AfterWelcome.StartCapture,
        WelcomeOutcome.OpenSettings => AfterWelcome.OpenSettings,
        _ => AfterWelcome.Nothing,
    };
}
