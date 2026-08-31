namespace MyCapture.App.Editing;

/// <summary>
/// Serializes every in-process image export from destination choice through durable write.
/// </summary>
/// <remarks>
/// Without one app-wide transaction, two independent editor/pin dialogs can both approve a
/// name that does not yet exist and the slower write silently replaces the faster one. Quick
/// saves also participate so their atomic name claim cannot race an already-approved Save As.
/// </remarks>
internal static class ImageExportTransaction
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await Gate.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            Gate.Release();
        }
    }
}
