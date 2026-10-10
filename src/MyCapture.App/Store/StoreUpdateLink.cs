using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Store;

/// <summary>
/// Opens the MyCapture listing in the Microsoft Store, which delivers every update.
/// </summary>
/// <remarks>
/// <para>
/// MyCapture is distributed only through the Store, so the update tab neither checks GitHub nor
/// downloads an installer. It hands the user to the Store app's product page, which offers the
/// update when one is available, and falls back to the web listing when the Store protocol
/// cannot open (the Store app removed or blocked by policy). The app itself sends no request.
/// </para>
/// <para>
/// <c>StoreContext.GetAppAndOptionalStorePackageUpdatesAsync</c> could ask the Store first, but
/// it would be a connection made by the app itself, answers only for a Store-signed install,
/// and could only lead to the same Store page. The page alone is enough.
/// </para>
/// </remarks>
internal sealed class StoreUpdateLink(Action<string> open)
{
    /// <summary>The MyCapture product page in the Microsoft Store app.</summary>
    internal const string StoreAppUri = "ms-windows-store://pdp/?productid=" + StoreRatingPolicy.ProductId;

    /// <summary>The same listing on the web, for a PC whose Store app cannot open.</summary>
    internal const string WebUri = "https://apps.microsoft.com/detail/" + StoreRatingPolicy.ProductId;

    private readonly Action<string> _open = open ?? throw new ArgumentNullException(nameof(open));

    /// <summary>Opens links through the Windows shell.</summary>
    internal StoreUpdateLink() : this(ShellOpen)
    {
    }

    /// <summary>
    /// Opens the Store app page, then the web page. Returns the link that opened, or
    /// <see langword="null"/> when neither could.
    /// </summary>
    internal string? Open(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        foreach (string uri in new[] { StoreAppUri, WebUri })
        {
            try
            {
                _open(uri);
                return uri;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                log.LogWarning(ex, "Could not open the Microsoft Store listing at {Uri}", uri);
            }
        }

        return null;
    }

    private static void ShellOpen(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
}
