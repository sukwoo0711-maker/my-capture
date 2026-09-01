using MyCapture.Core.Primitives;
using MyCapture.Core.Privacy;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PrivacyDetectionTests
{
    private readonly PrivacyDetector _detector = new();

    [Theory]
    [InlineData("person@example.com", PrivacyMatchKind.EmailAddress)]
    [InlineData("010-1234-5678", PrivacyMatchKind.KoreanPhoneNumber)]
    [InlineData("900101-1234567", PrivacyMatchKind.KoreanResidentNumber)]
    [InlineData("4111-1111-1111-1111", PrivacyMatchKind.PaymentCardNumber)]
    [InlineData("192.168.10.24", PrivacyMatchKind.IpAddress)]
    [InlineData("ghp_123456789012345678901234567890123456", PrivacyMatchKind.SecretToken)]
    public void DetectsHighConfidenceSingleTokenShapes(string text, PrivacyMatchKind expected)
    {
        PrivacyMatch match = Assert.Single(_detector.Detect([Token(text, 0, 0, 10)]));

        Assert.Equal(expected, match.Kind);
        Assert.Equal(new RectD(10, 2, 20, 8), match.Bounds);
    }

    [Fact]
    public void JoinsAdjacentTokensOnSameLineAndUnionsTheirBounds()
    {
        IReadOnlyList<PrivacyMatch> result = _detector.Detect([
            Token("person", 2, 4, 10),
            Token("@", 2, 5, 30),
            Token("example", 2, 6, 34),
            Token(".", 2, 7, 56),
            Token("com", 2, 8, 60),
        ]);

        PrivacyMatch match = Assert.Single(result);
        Assert.Equal(PrivacyMatchKind.EmailAddress, match.Kind);
        Assert.Equal(4, match.FirstTokenIndex);
        Assert.Equal(8, match.LastTokenIndex);
        Assert.Equal(new RectD(10, 2, 70, 8), match.Bounds);
    }

    [Fact]
    public void NeverJoinsAcrossLinesOrTokenIndexGaps()
    {
        IReadOnlyList<PrivacyMatch> result = _detector.Detect([
            Token("person", 0, 0, 0),
            Token("@example.com", 1, 1, 20),
            Token("010", 2, 0, 0),
            Token("1234", 2, 2, 20),
            Token("5678", 2, 3, 40),
        ]);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("4111-1111-1111-1112")]
    [InlineData("999.1.1.1")]
    [InlineData("900231-1234567")]
    [InlineData("010-12-34")]
    [InlineData("name@example")]
    [InlineData("ghp_too_short")]
    public void RejectsPlausibleFalsePositives(string text)
    {
        Assert.Empty(_detector.Detect([Token(text, 0, 0, 0)]));
    }

    [Fact]
    public void DeduplicatesOverlappingCandidatesDeterministically()
    {
        PrivacyToken[] tokens = [
            Token("4111", 0, 0, 0),
            Token("1111", 0, 1, 20),
            Token("1111", 0, 2, 40),
            Token("1111", 0, 3, 60),
        ];

        IReadOnlyList<PrivacyMatch> first = _detector.Detect(tokens);
        IReadOnlyList<PrivacyMatch> second = _detector.Detect(tokens.Reverse().ToArray());

        Assert.Equal(first, second);
        PrivacyMatch match = Assert.Single(first);
        Assert.Equal(PrivacyMatchKind.PaymentCardNumber, match.Kind);
        Assert.Equal(new RectD(0, 2, 80, 8), match.Bounds);
    }

    [Fact]
    public void OutputContractCannotRetainMatchedPlaintext()
    {
        string[] propertyNames = typeof(PrivacyMatch).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain("Text", propertyNames);
        Assert.DoesNotContain("Value", propertyNames);
        Assert.DoesNotContain("MatchedText", propertyNames);
    }

    private static PrivacyToken Token(string text, int line, int index, double x) =>
        new(text, line, index, new RectD(x, 2, 20, 8));
}
