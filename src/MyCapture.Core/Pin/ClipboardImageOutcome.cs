namespace MyCapture.Core.Pin;

/// <summary>
/// Why a clipboard image read finished the way it did.
/// </summary>
public enum ClipboardImageStatus
{
    /// <summary>An image was present and successfully read.</summary>
    Success,

    /// <summary>The clipboard opened but held no image (text, files, empty).</summary>
    NoImage,

    /// <summary>
    /// Every attempt to open the clipboard failed with <c>CLIPBRD_E_CANT_OPEN</c>;
    /// another process held it open for the whole retry window.
    /// </summary>
    Busy,
}

/// <summary>
/// The outcome of attempting to read an image off the clipboard, independent of WPF.
/// </summary>
/// <remarks>
/// The WPF reader returns a <see cref="System.Windows.Media.Imaging.BitmapSource"/>
/// alongside a status of this kind, but the branching the caller does — pin it, show a
/// "no image" balloon, or show a "clipboard busy" balloon — is decided purely on
/// <see cref="ClipboardImageStatus"/> and is therefore testable without a clipboard.
/// </remarks>
public readonly record struct ClipboardImageOutcome(ClipboardImageStatus Status, int PixelWidth, int PixelHeight)
{
    public bool HasImage => Status == ClipboardImageStatus.Success && PixelWidth > 0 && PixelHeight > 0;

    public static ClipboardImageOutcome Success(int pixelWidth, int pixelHeight) =>
        new(ClipboardImageStatus.Success, pixelWidth, pixelHeight);

    public static ClipboardImageOutcome NoImage() => new(ClipboardImageStatus.NoImage, 0, 0);

    public static ClipboardImageOutcome Busy() => new(ClipboardImageStatus.Busy, 0, 0);
}
