using MyCapture.Core.Primitives;

namespace MyCapture.Core.Privacy;

/// <summary>High-confidence sensitive-data shapes that can be safely offered for redaction.</summary>
public enum PrivacyMatchKind
{
    EmailAddress,
    KoreanPhoneNumber,
    KoreanResidentNumber,
    PaymentCardNumber,
    IpAddress,
    SecretToken,
}

/// <summary>
/// One OCR token supplied to the detector. Text exists only at the input boundary; detector
/// results deliberately retain coordinates and classification, never recognised plaintext.
/// </summary>
public sealed record PrivacyToken(string Text, int LineIndex, int TokenIndex, RectD Bounds);

/// <summary>A redaction candidate expressed only as image coordinates and source token indexes.</summary>
public sealed record PrivacyMatch(
    PrivacyMatchKind Kind,
    int LineIndex,
    int FirstTokenIndex,
    int LastTokenIndex,
    RectD Bounds);

public interface IPrivacyDetector
{
    IReadOnlyList<PrivacyMatch> Detect(IReadOnlyList<PrivacyToken> tokens);
}
