namespace MyCapture.Core.Settings;

/// <summary>Mirrors <c>Windows.ApplicationModel.StartupTaskState</c> so the logic stays testable.</summary>
public enum PackagedStartupState
{
    Disabled = 0,
    DisabledByUser = 1,
    Enabled = 2,
    DisabledByPolicy = 3,
    EnabledByPolicy = 4,
}

/// <summary>The manifest startup task of the packaged (Microsoft Store) build.</summary>
public interface IPackagedStartupTask
{
    PackagedStartupState GetState();

    /// <summary>Asks Windows to enable the task and returns the resulting state.</summary>
    PackagedStartupState RequestEnable();

    void Disable();
}

/// <summary>
/// Launch-at-login for the Microsoft Store build through the <c>windows.startupTask</c>
/// manifest extension.
/// </summary>
/// <remarks>
/// <para>
/// Windows owns the task state. Store policy requires consent before changing how Windows
/// starts, so the task ships disabled and is enabled only from the settings window. A task
/// the user turned off in Settings or Task Manager
/// (<see cref="PackagedStartupState.DisabledByUser"/>) or one fixed by policy is never
/// overridden; the user is told where to change it instead. Like the Run key service, a
/// change is verified and never reported as applied when it is not.
/// </para>
/// </remarks>
public sealed class PackagedStartupRegistration : IStartupRegistration
{
    /// <summary>Must match the <c>TaskId</c> of the startup task in the Store manifest.</summary>
    public const string TaskId = "MyCaptureStartup";

    private readonly IPackagedStartupTask _task;

    public PackagedStartupRegistration(IPackagedStartupTask task)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
    }

    public bool IsEnabled() => IsOn(_task.GetState());

    public StartupApplyResult Apply(bool desiredEnabled)
    {
        try
        {
            PackagedStartupState state = _task.GetState();
            if (desiredEnabled)
            {
                if (IsOn(state)) return StartupApplyResult.Ok(true);
                if (Blocked(state) is { } blocked) return StartupApplyResult.Fail(true, blocked);
                return IsOn(_task.RequestEnable())
                    ? StartupApplyResult.Ok(true)
                    : StartupApplyResult.Fail(true, UiText.Get("Text_4D69B33A2FD7"));
            }

            if (state == PackagedStartupState.EnabledByPolicy)
            {
                return StartupApplyResult.Fail(false, UiText.Get("Startup.StoreManagedByPolicy"));
            }

            if (state == PackagedStartupState.Enabled) _task.Disable();
            return IsOn(_task.GetState())
                ? StartupApplyResult.Fail(false, UiText.Get("Text_4D69B33A2FD7"))
                : StartupApplyResult.Ok(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return StartupApplyResult.Fail(desiredEnabled, ex.Message);
        }
    }

    /// <summary>
    /// Never changes the task. It ships disabled and only an explicit choice in settings, or the
    /// user in Windows Settings, turns it on, so the saved setting is mirrored from
    /// <see cref="IsEnabled"/> at launch instead of being forced onto Windows.
    /// </summary>
    public bool ReconcileOnStartup(bool settingEnabled) => false;

    private static bool IsOn(PackagedStartupState state) =>
        state is PackagedStartupState.Enabled or PackagedStartupState.EnabledByPolicy;

    private static string? Blocked(PackagedStartupState state) => state switch
    {
        PackagedStartupState.DisabledByUser => UiText.Get("Startup.StoreDisabledByUser"),
        PackagedStartupState.DisabledByPolicy => UiText.Get("Startup.StoreManagedByPolicy"),
        _ => null,
    };
}
