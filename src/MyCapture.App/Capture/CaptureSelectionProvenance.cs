using MyCapture.Core.Capture;

namespace MyCapture.App.Capture;

/// <summary>Uses only the metadata tied to this pixel acquisition, including its bounded late read.</summary>
internal static class CaptureSelectionProvenance
{
    internal static CaptureProvenance Current(CaptureSelectionCompletedEventArgs selection) =>
        selection.PendingSource is { IsCompletedSuccessfully: true } completed
            ? FromSource(completed.Result)
            : CaptureProvenance.FromSource(selection.SourceDetails, selection.SourcePageUrl, selection.SourceTitle);

    internal static Task<CaptureProvenance>? Pending(CaptureSelectionCompletedEventArgs selection) =>
        selection.PendingSource is { } pending ? ResolveAsync(pending, Current(selection)) : null;

    private static async Task<CaptureProvenance> ResolveAsync(Task<CaptureSelectionSource> pending, CaptureProvenance fallback)
    {
        try
        {
            CaptureSelectionSource source = await pending.ConfigureAwait(false);
            return source.HasNoDetails ? fallback : FromSource(source);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return fallback; }
    }

    private static CaptureProvenance FromSource(CaptureSelectionSource source) =>
        CaptureProvenance.FromSource(source.Details, source.PageUrl, source.Title);

    internal static string ReceiptText(string status, CaptureProvenance provenance) =>
        provenance.HasSource ? $"{status} · {provenance.DisplayText}" : status;
}
