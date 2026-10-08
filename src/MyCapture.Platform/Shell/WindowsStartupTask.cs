using MyCapture.Core.Settings;
using Windows.ApplicationModel;

namespace MyCapture.Platform.Shell;

/// <summary>
/// The production <see cref="IPackagedStartupTask"/> backed by the Store manifest's
/// <c>windows.startupTask</c> extension.
/// </summary>
/// <remarks>
/// The settings pipeline is synchronous, so each call blocks on the WinRT operation. The
/// operations complete on the thread pool without the dispatcher, and a packaged desktop app
/// gets no consent dialog from <c>RequestEnableAsync</c>, so the wait is short and cannot
/// deadlock the UI thread.
/// </remarks>
public sealed class WindowsStartupTask : IPackagedStartupTask
{
    public PackagedStartupState GetState() => (PackagedStartupState)Current().State;

    public PackagedStartupState RequestEnable() =>
        (PackagedStartupState)Current().RequestEnableAsync().AsTask().GetAwaiter().GetResult();

    public void Disable() => Current().Disable();

    private static StartupTask Current() =>
        StartupTask.GetAsync(PackagedStartupRegistration.TaskId).AsTask().GetAwaiter().GetResult();
}
