using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ReceiptKeyboardRouterTests
{
    [Fact]
    public void HigherPriorityToastTakesTheSingleRegistrationAndClosingItRestoresTheCard()
    {
        var registrations = new FakeRegistrations();
        using var router = new ReceiptKeyboardRouter(registrations.Create);
        object card = new(), toast = new();
        int cardActivations = 0, toastActivations = 0;
        router.SetTarget(card, 101, true, 10, () => { cardActivations++; return true; });
        Assert.True(router.IsRegisteredFor(card));
        Assert.True(router.Activate());

        router.SetTarget(toast, 202, true, 100, () => { toastActivations++; return true; });
        Assert.False(router.IsRegisteredFor(card));
        Assert.True(router.IsRegisteredFor(toast));
        Assert.False(router.MatchesMessage(101, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
        Assert.True(router.MatchesMessage(202, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
        Assert.True(router.Activate());

        router.Remove(toast);
        Assert.True(router.IsRegisteredFor(card));
        Assert.True(router.Activate());
        router.Remove(card);

        Assert.Equal(2, cardActivations);
        Assert.Equal(1, toastActivations);
        Assert.False(router.Activate());
        Assert.False(router.MatchesMessage(101, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
        Assert.Null(registrations.HeldBy);
        Assert.Equal(1, registrations.MaximumSimultaneous);
        Assert.Equal(new[] { "register:101", "unregister:101", "register:202", "unregister:202", "register:101", "unregister:101" },
            registrations.Calls);
    }

    [Fact]
    public void RefreshingTheSelectedOwnerUpdatesItsCallbackWithoutRegistrationChurn()
    {
        var registrations = new FakeRegistrations();
        using var router = new ReceiptKeyboardRouter(registrations.Create);
        object card = new(), lower = new();
        int oldActivations = 0, newActivations = 0;
        router.SetTarget(card, 101, true, 10, () => { oldActivations++; return true; });
        router.SetTarget(lower, 303, true, 1, () => false);
        router.SetTarget(card, 101, true, 10, () => { newActivations++; return true; });
        router.Remove(lower);

        Assert.True(router.Activate());
        Assert.Equal(0, oldActivations);
        Assert.Equal(1, newActivations);
        Assert.Equal(new[] { "register:101" }, registrations.Calls);

        router.SetTarget(card, 102, true, 10, () => true);
        Assert.Equal(new[] { "register:101", "unregister:101", "register:102" }, registrations.Calls);
        Assert.True(router.MatchesMessage(102, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
        Assert.False(router.MatchesMessage(101, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
    }

    [Fact]
    public void EqualPriorityUsesTheLatestTargetAndUnavailableTargetsFallBack()
    {
        var registrations = new FakeRegistrations();
        using var router = new ReceiptKeyboardRouter(registrations.Create);
        object first = new(), latest = new();
        router.SetTarget(first, 101, true, 10, () => true);
        router.SetTarget(latest, 202, true, 10, () => true);
        Assert.True(router.IsRegisteredFor(latest));

        router.SetTarget(first, 101, true, 10, () => true);
        Assert.True(router.IsRegisteredFor(first));
        router.SetTarget(first, 101, false, 10, () => true);
        Assert.True(router.IsRegisteredFor(latest));
        router.SetTarget(latest, 202, false, 10, () => true);

        Assert.Null(registrations.HeldBy);
        Assert.False(router.Activate());
        Assert.Equal(1, registrations.MaximumSimultaneous);
    }

    [Fact]
    public void FailedNativeRegistrationKeepsLocalActivationAndRejectsNativeMessages()
    {
        var registrations = new FakeRegistrations { FailRegistration = true };
        using var router = new ReceiptKeyboardRouter(registrations.Create);
        object toast = new();
        int activations = 0;
        router.SetTarget(toast, 202, true, 100, () => { activations++; return true; });

        Assert.False(router.IsRegisteredFor(toast));
        Assert.False(router.MatchesMessage(202, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
        Assert.True(router.Activate());
        Assert.Equal(1, activations);
        router.SetTarget(toast, 202, true, 100, () => false);
        Assert.False(router.Activate());
        Assert.Equal(new[] { "register:202" }, registrations.Calls);
        router.Remove(toast);
        Assert.Equal(new[] { "register:202" }, registrations.Calls);
        Assert.Null(registrations.HeldBy);
    }

    [Fact]
    public void OnlyTheRegisteredWindowAndHotkeyIdMatchAndDisposeReleasesTheLeaseOnce()
    {
        var registrations = new FakeRegistrations();
        var router = new ReceiptKeyboardRouter(registrations.Create);
        object card = new();
        int changes = 0;
        router.RegistrationChanged += () => changes++;
        router.SetTarget(card, 101, true, 10, () => true);
        Assert.True(router.MatchesMessage(101, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
        Assert.False(router.MatchesMessage(101, 0x0100, ReceiptKeyboardRouter.ActivationKeyId));
        Assert.False(router.MatchesMessage(101, 0x0312, ReceiptKeyboardRouter.ActivationKeyId + 1));
        Assert.False(router.MatchesMessage(202, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));

        router.Dispose();
        router.Dispose();
        router.Remove(card);
        router.SetTarget(card, 101, true, 10, () => true);

        Assert.Equal(2, changes);
        Assert.Equal(new[] { "register:101", "unregister:101" }, registrations.Calls);
        Assert.False(router.IsRegisteredFor(card));
        Assert.False(router.Activate());
        Assert.False(router.MatchesMessage(101, 0x0312, ReceiptKeyboardRouter.ActivationKeyId));
    }

    private sealed class FakeRegistrations
    {
        internal List<string> Calls { get; } = [];
        internal nint? HeldBy { get; private set; }
        internal int MaximumSimultaneous { get; private set; }
        internal bool FailRegistration { get; init; }

        internal IHotkeyRegistrar Create(nint hwnd) => new Registrar(this, hwnd);

        private sealed class Registrar(FakeRegistrations owner, nint hwnd) : IHotkeyRegistrar
        {
            public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
            {
                Assert.Equal(0x7103, id);
                Assert.Equal(new Hotkey(HotkeyModifiers.Alt, 0x51), hotkey);
                owner.Calls.Add($"register:{hwnd}");
                Assert.Null(owner.HeldBy);
                errorCode = owner.FailRegistration ? 1409 : 0;
                errorMessage = owner.FailRegistration ? "Synthetic conflict" : string.Empty;
                if (owner.FailRegistration) return false;
                owner.HeldBy = hwnd;
                owner.MaximumSimultaneous = Math.Max(owner.MaximumSimultaneous, 1);
                return true;
            }

            public void Unregister(int id)
            {
                Assert.Equal(0x7103, id);
                Assert.Equal((nint?)hwnd, owner.HeldBy);
                owner.Calls.Add($"unregister:{hwnd}");
                owner.HeldBy = null;
            }
        }
    }
}
