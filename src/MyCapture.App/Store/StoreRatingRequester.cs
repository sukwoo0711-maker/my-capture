using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
/// before the Store can show UI. MyCapture usually has no window open when a capture flow
/// ends, so a transparent one-pixel owner is shown for the lifetime of the dialog. It is
/// centred on the screen so the Store dialog, which centres on its owner, is too.
/// </remarks>
internal sealed class StoreContextRatingRequester : IStoreRatingRequester
{
    public async Task<string> RequestRateAndReviewAsync()
    {
        var owner = new Window
        {
            Title = MyCapture.Core.Platform.AppIdentity.WindowLabel,
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        try
        {
            owner.Show();
            StoreContext context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, new WindowInteropHelper(owner).Handle);
            StoreRateAndReviewResult result = await context.RequestRateAndReviewAppAsync().AsTask();
            return result.ExtendedError is { } error
                ? $"{result.Status} (0x{error.HResult:X8})"
                : $"{result.Status}, updated={result.WasUpdated}";
        }
        finally
        {
            owner.Close();
        }
    }
}
