using MyCapture.App.Updates;

namespace MyCapture.App;

/// <summary>The running application version shared by visible labels and update checks.</summary>
internal static class ApplicationVersion
{
    internal static UpdateVersion Current { get; } = UpdateVersion.FromVersion(
        typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    internal static string DisplayText => UiText.Format("App.CurrentVersion", Current);
}
