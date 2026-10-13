using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SupportDiagnosticsTests
{
    [Fact]
    public void LoggerNeverFormatsPersonalContentAndKeepsOnlyBoundedActualErrorCodes()
    {
        var errors = new RecentErrorLog();
        using var provider = new RecentErrorLogProvider(errors);
        ILogger logger = provider.CreateLogger("C:/private/name");
        string personal = "SECRET_TOKEN https://private.example C:/capture/person.png";
        for (int i = 0; i < 20; i++)
            logger.Log(LogLevel.Error, new EventId(i, personal), personal, new InvalidOperationException(personal),
                (_, _) => throw new InvalidOperationException("Formatter must never execute"));
        logger.Log(LogLevel.Information, new EventId(999), personal, null, (_, _) => personal);
        SafeErrorRecord[] snapshot = errors.Snapshot();
        Assert.Equal(RecentErrorLog.Capacity, snapshot.Length);
        Assert.Equal(8, snapshot[0].EventId); Assert.Equal(19, snapshot[^1].EventId);
        string text = SupportDiagnostics.Format("4.1.1.0", "10.0.26200.0", true, 144, 144, snapshot);
        Assert.Contains("144", text); Assert.Contains("HRESULT=0x", text);
        Assert.DoesNotContain("SECRET", text); Assert.DoesNotContain("https://", text); Assert.DoesNotContain("C:/", text);
    }
}
