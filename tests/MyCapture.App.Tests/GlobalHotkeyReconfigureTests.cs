using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Transactional <see cref="GlobalHotkeyService.Reconfigure"/> behaviour, exercised through
/// an injected fake registrar so success, duplicate-collision rollback, and the
/// "never without a capture hotkey" guarantee are deterministic and machine-independent.
/// </summary>
/// <remarks>
/// A real <see cref="NativeMessageWindow"/> is still constructed (it owns the WM_HOTKEY
/// route and the service requires one), so the bodies run on a dedicated STA thread. The
/// native <c>RegisterHotKey</c> path is not called: the fake registrar stands in for it,
/// which is the seam the production service was refactored to expose.
/// </remarks>
public sealed class GlobalHotkeyReconfigureTests
{
    /// <summary>
    /// A registration seam that succeeds unless a chord collides with one already held, or
    /// with a caller-provided blocklist standing in for "another process owns this".
    /// </summary>
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        private readonly HashSet<(uint Modifiers, uint Vk)> _blocked;
        private readonly Dictionary<int, (uint Modifiers, uint Vk)> _held = [];

        public FakeRegistrar(IEnumerable<Hotkey>? blocked = null)
        {
            _blocked = [];
            if (blocked is not null)
            {
                foreach (Hotkey h in blocked)
                {
                    _blocked.Add(((uint)h.Modifiers, h.VirtualKey));
                }
            }
        }

        public int RegisterCallCount { get; private set; }

        public IReadOnlyDictionary<int, (uint Modifiers, uint Vk)> Held => _held;

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            RegisterCallCount++;
            var key = ((uint)hotkey.Modifiers, hotkey.VirtualKey);

            bool collides = _blocked.Contains(key)
                || System.Linq.Enumerable.Contains(_held.Values, key);
            if (collides)
            {
                errorCode = 1409; // ERROR_HOTKEY_ALREADY_REGISTERED
                errorMessage = "Hot key is already registered.";
                return false;
            }

            _held[id] = key;
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id) => _held.Remove(id);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    [Fact]
    public void Reconfigure_AppliesNewSetOnSuccess()
    {
        RunSta(() =>
        {
            var registrar = new FakeRegistrar();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
            service.Initialize(new HotkeySettings());

            var next = new HotkeySettings
            {
                Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkC),
                PasteToScreen = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3),
            };

            HotkeyReconfigureResult result = service.Reconfigure(next);

            Assert.True(result.Applied);
            Assert.Empty(result.Failures);
            Assert.Contains(GlobalHotkeyCommand.CaptureRegion, service.RegisteredCommands);
            Assert.Contains(GlobalHotkeyCommand.PasteToScreen, service.RegisteredCommands);
        });
    }

    [Fact]
    public void Reconfigure_RollsBackToPreviousSetOnCollision()
    {
        RunSta(() =>
        {
            // The would-be new capture chord (Ctrl+Alt+X) is owned by "another process".
            var blockedChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58); // X
            var registrar = new FakeRegistrar(blocked: [blockedChord]);

            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);

            // Start with the working defaults (Ctrl+Shift+C etc.).
            service.Initialize(new HotkeySettings());
            Assert.Contains(GlobalHotkeyCommand.CaptureRegion, service.RegisteredCommands);

            var next = new HotkeySettings { Capture = blockedChord };
            HotkeyReconfigureResult result = service.Reconfigure(next);

            Assert.False(result.Applied);
            Assert.NotEmpty(result.Failures);
            Assert.Equal(GlobalHotkeyCommand.CaptureRegion, result.Failures[0].Command);

            // The previous, working capture chord must still be registered: the app is never
            // left without its capture hotkey.
            Assert.Contains(GlobalHotkeyCommand.CaptureRegion, service.RegisteredCommands);
            var restored = registrar.Held.Values;
            Assert.Contains(((uint)(HotkeyModifiers.Control | HotkeyModifiers.Shift), Hotkey.VkC), restored);
        });
    }

    [Fact]
    public void Reconfigure_LeavesNoRegistrationWhenItRollsBackFromEmptyToCollision()
    {
        RunSta(() =>
        {
            var conflictA = new Hotkey(HotkeyModifiers.Control, Hotkey.VkF1);
            var registrar = new FakeRegistrar();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
            service.Initialize(new HotkeySettings());

            // Two different commands asking for the SAME chord: the second collides with the
            // first inside this reconfigure, forcing a full rollback.
            var next = new HotkeySettings
            {
                Capture = conflictA,
                PasteToScreen = conflictA,
            };

            HotkeyReconfigureResult result = service.Reconfigure(next);

            Assert.False(result.Applied);
            Assert.NotEmpty(result.Failures);
            // Rolled back to the previous default set, so capture is still present.
            Assert.Contains(GlobalHotkeyCommand.CaptureRegion, service.RegisteredCommands);
        });
    }

    [Fact]
    public void Reconfigure_SkipsUnassignedChords()
    {
        RunSta(() =>
        {
            var registrar = new FakeRegistrar();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
            service.Initialize(new HotkeySettings());

            var next = new HotkeySettings
            {
                Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC),
                // Explicitly clear the class defaults so only Capture is assigned.
                PasteToScreen = Hotkey.None,
                HideAllPins = Hotkey.None,
                ToggleClickThrough = Hotkey.None,
                RepeatLastRegion = Hotkey.None,
                CaptureWindow = Hotkey.None,
                CaptureFullScreen = Hotkey.None,
            };

            HotkeyReconfigureResult result = service.Reconfigure(next);

            Assert.True(result.Applied);
            Assert.Single(service.RegisteredCommands);
            Assert.Contains(GlobalHotkeyCommand.CaptureRegion, service.RegisteredCommands);
        });
    }
}
