namespace MyCapture.App;

/// <summary>Where a WPF TextBox places its text, which none of its properties say.</summary>
internal static class TextBoxLayout
{
    /// <summary>
    /// A TextBox draws its text this far inside its content host on each side, at every zoom and
    /// whatever its padding, so its text starts this much to the right of a TextBlock at the same
    /// spot. Text that must line up with a picture or with other text takes it back.
    /// </summary>
    internal const double ContentInsetDip = 2;
}
