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

    [Fact]
    public void SingleLine_EscapesOtherControlCharacters()
    {
        string value = "a\0b\tc\u001b[31md\u007fe\u009bf";

        string safe = LogText.SingleLine(value);

        Assert.Equal("a\\u0000b\\u0009c\\u001B[31md\\u007Fe\\u009Bf", safe);
        Assert.DoesNotContain(safe, char.IsControl);
    }

    [Fact]
    public void SingleLine_ForgedEntryStaysOnOnePhysicalLine()
    {
        string forged = "C:\\Captures\\x.png\r\n2026-01-01 [INF] Forged entry\u001b[2K";

        string safe = LogText.SingleLine(forged);

        Assert.Equal("C:\\Captures\\x.png\\r\\n2026-01-01 [INF] Forged entry\\u001B[2K", safe);
    }
}
