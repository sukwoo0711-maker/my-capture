using MyCapture.Core.Settings;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace MyCapture.Ocr;

internal interface IOcrDownloadEnvironment
{
    bool ShouldDeferBackgroundDownload();
    bool? IsBatterySaverOn => null;
    int? ProcessorCount => null;
    ulong? InstalledMemoryBytes => null;
}

internal sealed class WindowsOcrDownloadEnvironment : IOcrDownloadEnvironment
{
    public bool? IsBatterySaverOn
    {
        get
        {
            try
            {
                return PowerManager.EnergySaverStatus switch
                {
                    EnergySaverStatus.Off => false,
                    EnergySaverStatus.On => true,
                    _ => null,
                };
            }
            catch (Exception) { return null; }
        }
    }

    public int? ProcessorCount => Environment.ProcessorCount;

    public ulong? InstalledMemoryBytes
    {
        get
        {
            try
            {
                return GetPhysicallyInstalledSystemMemory(out ulong kilobytes)
                    && kilobytes > 0 && kilobytes <= ulong.MaxValue / 1024
                    ? kilobytes * 1024 : null;
            }
            catch (Exception) { return null; }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    public bool ShouldDeferBackgroundDownload()
    {
        // An unavailable OS status must not turn startup into an expensive download.
        // An explicit action bypasses this check and can still prepare the models.
        try
        {
            ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return ShouldDefer(cost?.NetworkCostType ?? NetworkCostType.Unknown,
                cost?.Roaming ?? false, cost?.OverDataLimit ?? false,
                IsBatterySaverOn != false);
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal static bool ShouldDefer(NetworkCostType cost, bool roaming, bool overLimit, bool batterySaver) =>
        cost != NetworkCostType.Unrestricted || roaming || overLimit || batterySaver;
}

internal sealed class OcrModelDownloadCoordinator(OcrModelStore store, IOcrDownloadEnvironment environment)
{
    internal async Task<bool> EnsureAsync(OcrQuality quality, bool isBackground, CancellationToken cancellationToken,
        bool deferForBatterySaver = false)
    {
        if (!OcrQualityProfile.UseNeuralModel(quality)) return true;
        if (deferForBatterySaver && environment.IsBatterySaverOn != false) return false;
        if (isBackground && environment.ShouldDeferBackgroundDownload()) return false;
        if (!await store.EnsureAsync(cancellationToken).ConfigureAwait(false)) return false;
        // Preserve the startup prefetch scope; super-resolution is prepared on explicit use.
        return isBackground || quality != OcrQuality.Enhanced
            || await store.EnsureSuperResolutionAsync(cancellationToken).ConfigureAwait(false);
    }
}
