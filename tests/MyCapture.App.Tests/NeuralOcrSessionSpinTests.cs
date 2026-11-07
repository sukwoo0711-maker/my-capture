using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NeuralOcrSessionSpinTests
{
    [Fact]
    public void ProductWholeSessionsSpinOneMillisecondWithBackoffFour()
    {
        Assert.Equal(
            new[]
            {
                KeyValuePair.Create("session.intra_op.spin_duration_us", "1000"),
                KeyValuePair.Create("session.inter_op.spin_duration_us", "1000"),
                KeyValuePair.Create("session.intra_op.spin_backoff_max", "4"),
                KeyValuePair.Create("session.inter_op.spin_backoff_max", "4"),
            },
            NeuralOcrEngine.WholeSessionConfigEntries(disableSpinning: false, runtimeSpinning: false).ToArray());
    }

    [Fact]
    public void DiagnosticArmsEitherStopSpinningOrKeepTheRuntimeSpin()
    {
        Assert.Equal(
            new[]
            {
                KeyValuePair.Create("session.intra_op.allow_spinning", "0"),
                KeyValuePair.Create("session.inter_op.allow_spinning", "0"),
            },
            NeuralOcrEngine.WholeSessionConfigEntries(disableSpinning: true, runtimeSpinning: false).ToArray());
        Assert.Empty(NeuralOcrEngine.WholeSessionConfigEntries(disableSpinning: false, runtimeSpinning: true));
        Assert.Throws<ArgumentException>(() => NeuralOcrEngine.WholeSessionConfigEntries(disableSpinning: true, runtimeSpinning: true));
    }
}
