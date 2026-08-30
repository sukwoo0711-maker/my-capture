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
                Headline: "OCR 언어 팩이 설치되어 있지 않습니다",
                Detail: "이 PC에 Windows OCR 언어 팩이 없어 텍스트 인식과 캡처 전문 검색을 사용할 수 없습니다. " +
                        "설정 → 시간 및 언어 → 언어 및 지역에서 언어의 [언어 기능]에 '광학 문자 인식(OCR)'을 추가한 뒤 다시 시도하세요. " +
                        "인터넷 연결 없이도 캡처·주석·녹화 등 나머지 기능은 정상 동작합니다.");
        }

        string langs = string.Join(", ", supportedLanguages);
        return new OcrAvailability(
            IsAvailable: true,
            SupportedLanguages: supportedLanguages,
            Headline: "OCR 사용 가능",
            Detail: $"인식 가능한 언어: {langs}. 모든 처리는 이 PC에서 오프라인으로 수행됩니다.");
    }

    /// <summary>Convenience overload taking the service directly.</summary>
    public static OcrAvailability Describe(IOcrService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return Describe(service.IsAvailable, service.SupportedLanguages);
    }
}
