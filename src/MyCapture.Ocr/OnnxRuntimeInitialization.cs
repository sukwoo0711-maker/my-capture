using Microsoft.ML.OnnxRuntime;

namespace MyCapture.Ocr;

/// <summary>Both OCR engines share this guard before creating any native session.</summary>
internal sealed class OnnxRuntimeInitialization
{
    internal static OnnxRuntimeInitialization Shared { get; } =
        new(() => OrtEnv.Instance().DisableTelemetryEvents());

    private readonly Lazy<bool> _initialized;

    internal OnnxRuntimeInitialization(Action disableTelemetry)
    {
        ArgumentNullException.ThrowIfNull(disableTelemetry);
        _initialized = new Lazy<bool>(() =>
        {
            disableTelemetry();
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal void EnsureInitialized() => _ = _initialized.Value;
}
