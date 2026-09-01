using MyCapture.Core.Diagnostics;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class LogTextTests
{
    [Fact]
    public void SingleLine_EscapesEveryUnicodeLineSeparator()
    {
        string value = "before\r\nnext\u0085nel\u2028line\u2029paragraph";

        string safe = LogText.SingleLine(value);

        Assert.Equal("before\\r\\nnext\\u0085nel\\u2028line\\u2029paragraph", safe);
        Assert.DoesNotContain('\r', safe);
        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain('\u0085', safe);
        Assert.DoesNotContain('\u2028', safe);
        Assert.DoesNotContain('\u2029', safe);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("C:\\Captures\\shot.png", "C:\\Captures\\shot.png")]
    public void SingleLine_HandlesNullAndPreservesOrdinaryText(string? value, string expected) =>
        Assert.Equal(expected, LogText.SingleLine(value));
}
