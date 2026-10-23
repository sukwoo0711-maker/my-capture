using MyCapture.Core.Capture;

namespace MyCapture.App.Pinning;

/// <summary>Associates the last capture copy with its source without exporting private source fields.</summary>
internal static class ClipboardProvenance
{
    internal const string Format = "MyCapture.ProvenanceToken";
    private static readonly object Gate = new();
    private static string? _token;
    private static CaptureProvenance? _source;
    private static Task<CaptureProvenance>? _pending;

    internal readonly record struct Snapshot(CaptureProvenance? Source, Task<CaptureProvenance>? Pending);

    internal static string Remember(CaptureProvenance provenance, Task<CaptureProvenance>? pending = null)
    {
        string token = Guid.NewGuid().ToString("N");
        Publish(token, provenance, pending);
        return token;
    }

    internal static void Publish(string token, CaptureProvenance provenance, Task<CaptureProvenance>? pending = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(provenance);
        lock (Gate)
        {
            _token = token;
            _source = provenance;
            _pending = pending;
        }
        if (pending is not null) _ = ResolvePendingAsync(token, pending);
    }

    private static async Task ResolvePendingAsync(string token, Task<CaptureProvenance> pending)
    {
        try
        {
            CaptureProvenance source = await pending.ConfigureAwait(false);
            if (!source.HasSource) return;
            lock (Gate)
            {
                if (string.Equals(token, _token, StringComparison.Ordinal))
                {
                    _source = source;
                    _pending = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // The capture remains usable with the source identity known when it was copied.
        }
    }

    internal static CaptureProvenance? Resolve(string? token)
        => Capture(token).Source;

    /// <summary>Detaches the matching copy's lookup before another clipboard copy replaces it.</summary>
    internal static Snapshot Capture(string? token)
    {
        if (string.IsNullOrEmpty(token)) return default;
        lock (Gate) return string.Equals(token, _token, StringComparison.Ordinal) ? new(_source, _pending) : default;
    }
}
