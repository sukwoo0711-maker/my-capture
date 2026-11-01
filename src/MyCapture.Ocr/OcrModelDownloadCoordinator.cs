using MyCapture.Core.Settings;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace MyCapture.Ocr;

internal interface IOcrDownloadEnvironment
{
    bool ShouldDeferBackgroundDownload();
    bool IsMetered => false;
    bool? IsBatterySaverOn => null;
    int? ProcessorCount => null;
    ulong? InstalledMemoryBytes => null;
}

internal sealed class WindowsOcrDownloadEnvironment : IOcrDownloadEnvironment
{
    private readonly Func<EnergySaverStatus> _energySaverStatus;
    private readonly Func<ConnectionCostState?> _connectionCost;

    public WindowsOcrDownloadEnvironment()
        : this(() => PowerManager.EnergySaverStatus, ReadConnectionCost)
    {
    }

    /// <summary>Supplies the two OS readings so tests can run this class's own decisions.</summary>
    internal WindowsOcrDownloadEnvironment(Func<EnergySaverStatus> energySaverStatus,
        Func<ConnectionCostState?> connectionCost)
    {
        _energySaverStatus = energySaverStatus ?? throw new ArgumentNullException(nameof(energySaverStatus));
        _connectionCost = connectionCost ?? throw new ArgumentNullException(nameof(connectionCost));
    }

    public bool? IsBatterySaverOn
    {
        get
        {
            try
            {
                return MapEnergySaverStatus(_energySaverStatus());
            }
            catch (Exception) { return null; }
        }
    }

    // Windows reports Disabled when battery saver is off permanently or the device is plugged
    // in, which is what a desktop without a battery reports. Only On saves power; an unknown
    // value stays unknown so callers keep their cautious default.
    internal static bool? MapEnergySaverStatus(EnergySaverStatus status) => status switch
    {
        EnergySaverStatus.Disabled or EnergySaverStatus.Off => false,
        EnergySaverStatus.On => true,
        _ => null,
    };

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
            ConnectionCostState? cost = _connectionCost();
            return ShouldDefer(cost?.NetworkCostType ?? NetworkCostType.Unknown,
                cost?.Roaming ?? false, cost?.OverDataLimit ?? false,
                IsBatterySaverOn != false);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public bool IsMetered
    {
        get
        {
            try
            {
                ConnectionCostState? cost = _connectionCost();
                return cost is { } value && (value.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                    || value.Roaming || value.OverDataLimit);
            }
            catch (Exception) { return false; }
        }
    }

    internal static bool ShouldDefer(NetworkCostType cost, bool roaming, bool overLimit, bool batterySaver) =>
        cost != NetworkCostType.Unrestricted || roaming || overLimit || batterySaver;

    private static ConnectionCostState? ReadConnectionCost()
    {
        ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
        return cost is null ? null : new ConnectionCostState(cost.NetworkCostType, cost.Roaming, cost.OverDataLimit);
    }
}

internal readonly record struct ConnectionCostState(NetworkCostType NetworkCostType, bool Roaming, bool OverDataLimit);

internal sealed class OcrModelDownloadCoordinator(OcrModelStore store, IOcrDownloadEnvironment environment)
{
    internal async Task<bool> EnsureAsync(OcrQuality quality, bool isBackground, CancellationToken cancellationToken,
        bool deferForBatterySaver = false, IProgress<double>? progress = null)
    {
        if (!OcrQualityProfile.UseNeuralModel(quality)) return true;
        if (deferForBatterySaver && environment.IsBatterySaverOn != false) return false;
        if (isBackground && environment.ShouldDeferBackgroundDownload()) return false;
        return await store.EnsureAsync(cancellationToken, progress).ConfigureAwait(false);
    }
}
