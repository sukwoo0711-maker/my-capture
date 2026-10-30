using MyCapture.Core.Settings;
using MyCapture.Core.Platform;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

internal interface IOcrAccuracyModel
{
    bool IsSupported { get; }
    bool IsReady { get; }
    bool IsMetered { get; }
    long DownloadBytes { get; }
    Task<bool> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken);
}

internal sealed record OcrAccuracyOptions(
    Func<OcrSettings> ReadSettings, Action SaveSettings, IOcrAccuracyModel Model);

internal sealed class OcrAccuracyModel(OcrModelStore store, IOcrDownloadEnvironment environment) : IOcrAccuracyModel
{
    public bool IsSupported => AppArchitecture.SupportsNeuralOcr;
    public bool IsReady
    {
        get
        {
            try { return store.HasRequiredModels; }
            catch (Exception) { return false; }
        }
    }
    public bool IsMetered => environment.IsMetered;
    public long DownloadBytes => OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate);
    public Task<bool> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        new OcrModelDownloadCoordinator(store, environment).EnsureAsync(OcrQuality.Accurate,
            isBackground: false, cancellationToken, progress: progress);
}
