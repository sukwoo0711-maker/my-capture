using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ShellIdentityTests
{
    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void ReadyNotification_RequiresSuccess_UsesConfiguredShortcut_AndOccursOnce(string language)
    {
        using var locale = UiText.UseLanguage(language);
        var notification = new ResidentReadyNotification();
        int calls = 0;
        var capture = new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, Hotkey.VkX);
        void Show(string title, string message)
        {
            calls++;
            Assert.Contains(AppIdentity.Label, title);
            Assert.Contains("Alt+Shift+X", message);
            Assert.DoesNotContain("Ctrl+Shift+C", message);
        }
        notification.Notify(false, capture, Show);
        Assert.Equal(0, calls);
        notification.Notify(true, capture, Show);
        notification.Notify(true, capture, Show);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void UnassignedCaptureShortcut_UsesTrayGuidance()
    {
        using var locale = UiText.UseLanguage("en");
        new ResidentReadyNotification().Notify(true, Hotkey.None, (_, message) =>
            Assert.Equal(UiText.Get("ShellRetake_ReadyWithoutShortcut"), message));
    }

    [Fact]
    public void ShellFailure_DoesNotReplayReadyNotification()
    {
        var notification = new ResidentReadyNotification();
        Assert.Throws<InvalidOperationException>(() => notification.Notify(true, Hotkey.None, (_, _) => throw new InvalidOperationException()));
        notification.Notify(true, Hotkey.None, (_, _) => Assert.Fail("A failed balloon must not be replayed."));
    }

    [Fact]
    public void Identity_UsesCompiledVersion_AndPreservesContextWithoutRepeatedPrefix()
    {
        Assert.Equal(AppIdentity.GetVersion(typeof(App).Assembly.GetName().Version), AppIdentity.Version);
        Assert.Equal("0.10.0", AppIdentity.GetVersion(new Version(0, 10, 0, 42)));
        Assert.Equal(AppIdentity.Label, AppIdentity.FormatWindowTitle("MyCapture"));
        string title = AppIdentity.FormatWindowTitle("Gallery — user capture.png");
        Assert.Equal(AppIdentity.Label + " — Gallery — user capture.png", title);
        Assert.Equal(title, AppIdentity.FormatWindowTitle(title));
    }
}
