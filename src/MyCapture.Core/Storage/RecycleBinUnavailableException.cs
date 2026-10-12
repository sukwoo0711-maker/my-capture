namespace MyCapture.Core.Storage;

/// <summary>
/// Windows cannot move this item to the Recycle Bin (the bin is turned off, the item is on a
/// removable or network volume, or it is larger than the bin). Nothing was deleted; only an
/// explicit, separately confirmed request may remove the item permanently.
/// </summary>
public sealed class RecycleBinUnavailableException : IOException
{
    public RecycleBinUnavailableException()
        : base("Windows cannot move this item to the Recycle Bin; it was left in place.")
    {
    }

    public RecycleBinUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
