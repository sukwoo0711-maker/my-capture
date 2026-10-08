using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace MyCapture.Core.Platform;

/// <summary>
/// Whether this process runs with package identity, as the Microsoft Store build does.
/// </summary>
/// <remarks>
/// <para>
/// A packaged full-trust app has new AppData files and every HKCU write virtualized into a
/// per-package store that other processes cannot see and that uninstall deletes, and the
/// Store delivers its updates. Storage, launch-at-login and the update tab branch on this
/// one answer instead of guessing from the install path.
/// </para>
/// </remarks>
public static class AppPackage
{
    private const int AppModelErrorNoPackage = 15700;
    private static readonly Lazy<bool> Detected = new(Detect);

    /// <summary>Test override; <see langword="null"/> uses the real process identity.</summary>
    internal static bool? IsPackagedOverride { get; set; }

    public static bool IsPackaged => IsPackagedOverride ?? Detected.Value;

    /// <summary>
    /// The package's own LocalState, LocalCache and TempState folders. They are real paths that
    /// Explorer, viewers and drop targets can open, unlike files written through virtualized
    /// AppData. Returns <see langword="false"/> when the process has no package data.
    /// </summary>
    public static bool TryGetDataFolders(out string localState, out string localCache, out string temporary)
    {
        localState = localCache = temporary = string.Empty;
        if (!IsPackaged) return false;
        try
        {
            ApplicationData data = ApplicationData.Current;
            localState = data.LocalFolder.Path;
            localCache = data.LocalCacheFolder.Path;
            temporary = data.TemporaryFolder.Path;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when Windows started the packaged app through its manifest startup task. That launch
    /// carries no command line, so it stands in for the unpackaged <c>--background</c> switch.
    /// </summary>
    public static bool IsStartupTaskActivation()
    {
        if (!IsPackaged) return false;
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool Detect()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
