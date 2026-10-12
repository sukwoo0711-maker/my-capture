using System.Windows;
using System.Windows.Interop;
using Windows.Services.Store;

namespace MyCapture.App.Store;

/// <summary>Shows the Microsoft Store rate-and-review dialog.</summary>
internal interface IStoreRatingRequester
{
    /// <summary>Returns a short outcome for the log. May throw when the Store is unavailable.</summary>
    Task<string> RequestRateAndReviewAsync();
}

/// <summary>
/// Production requester backed by <see cref="StoreContext.RequestRateAndReviewAppAsync"/>.
/// </summary>
/// <remarks>
/// A desktop app must associate the <see cref="StoreContext"/> with a top-level window
/// before the Store can show UI. The user must choose Review in their active library or
/// settings window; no invisible owner is created and no other application is activated.
/// </remarks>
internal sealed class StoreContextRatingRequester : IStoreRatingRequester
{
    public async Task<string> RequestRateAndReviewAsync()
    {
        Window owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive
            && window is MyCapture.App.Gallery.GalleryWindow or MyCapture.App.Settings.SettingsWindow)
            ?? throw new InvalidOperationException("A user-active MyCapture window is required for a review.");
        StoreContext context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(context, new WindowInteropHelper(owner).Handle);
        StoreRateAndReviewResult result = await context.RequestRateAndReviewAppAsync().AsTask();
        return result.ExtendedError is { } error
            ? $"{result.Status} (0x{error.HResult:X8})"
            : $"{result.Status}, updated={result.WasUpdated}";
    }
}
