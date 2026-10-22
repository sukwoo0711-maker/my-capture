using MyCapture.Core.Settings;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace MyCapture.Ocr;

internal interface IOcrDownloadEnvironment
{
    bool ShouldDeferBackgroundDownload();
}

internal sealed class WindowsOcrDownloadEnvironment : IOcrDownloadEnvironment
{
    public bool ShouldDeferBackgroundDownload()
    {
        // An unavailable OS status must not turn startup into an expensive download.
        // An explicit action bypasses this check and can still prepare the models.
        try
        {
            ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return ShouldDefer(cost?.NetworkCostType ?? NetworkCostType.Unknown,
                cost?.Roaming ?? false, cost?.OverDataLimit ?? false,
                PowerManager.EnergySaverStatus == EnergySaverStatus.On);
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
    internal async Task<bool> EnsureAsync(OcrQuality quality, bool isBackground, CancellationToken cancellationToken)
    {
        if (!OcrQualityProfile.UseNeuralModel(quality)) return true;
        if (isBackground && environment.ShouldDeferBackgroundDownload()) return false;
        if (!await store.EnsureAsync(cancellationToken).ConfigureAwait(false)) return false;
        // Preserve the startup prefetch scope; super-resolution is prepared on explicit use.
        return isBackground || quality != OcrQuality.Enhanced
            || await store.EnsureSuperResolutionAsync(cancellationToken).ConfigureAwait(false);
    }
}
