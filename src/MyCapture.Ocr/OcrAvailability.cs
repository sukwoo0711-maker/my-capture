namespace MyCapture.Ocr;

/// <summary>
/// A user-facing verdict on whether OCR can run on this machine, and what to do if not.
/// </summary>
/// <remarks>
/// The external commercial review flagged a real risk: MyCapture's OCR is the OS
/// <c>Windows.Media.Ocr</c> engine, which reports <c>IsAvailable = false</c> on a PC with no
/// OCR language pack installed. For a paid product, a feature that silently does nothing is a
/// refund trigger. This type turns that hidden dependency into an explicit, testable message
/// the UI must show, so the user always knows why OCR/search is unavailable and how to fix it.
/// </remarks>
public sealed record OcrAvailability(
    bool IsAvailable,
    IReadOnlyList<string> SupportedLanguages,
    string Headline,
    string Detail)
{
    /// <summary>
    /// Builds the advisory from an OCR service's reported state. Pure: no WinRT call here, so
    /// the wording is unit-tested without an engine.
    /// </summary>
    public static OcrAvailability Describe(bool isAvailable, IReadOnlyList<string> supportedLanguages)
    {
        ArgumentNullException.ThrowIfNull(supportedLanguages);

        if (!isAvailable || supportedLanguages.Count == 0)
        {
            return new OcrAvailability(
                IsAvailable: false,
                SupportedLanguages: supportedLanguages,
                Headline: UiText.Get("Text_58B2C0573E72"),
                Detail: UiText.Get("Text_F371688B8EE8"));
        }

        string langs = string.Join(", ", supportedLanguages);
        return new OcrAvailability(
            IsAvailable: true,
            SupportedLanguages: supportedLanguages,
            Headline: UiText.Get("Text_BF0FF22A113E"),
            Detail: UiText.Format("Text_F6C51786B910", langs));
    }

    /// <summary>Convenience overload taking the service directly.</summary>
    public static OcrAvailability Describe(IOcrService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return Describe(service.IsAvailable, service.SupportedLanguages);
    }
}
