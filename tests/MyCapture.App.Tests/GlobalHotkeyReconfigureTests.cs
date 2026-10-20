using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using MyCapture.Platform.Interop;
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

        public int BlockedErrorCode { get; init; } = 1409;

        public Action<Hotkey>? OnRegistrationAttempt { get; set; }

        public IReadOnlyDictionary<int, (uint Modifiers, uint Vk)> Held => _held;

        public void Block(Hotkey hotkey) => _blocked.Add(((uint)hotkey.Modifiers, hotkey.VirtualKey));

        public void Unblock(Hotkey hotkey) => _blocked.Remove(((uint)hotkey.Modifiers, hotkey.VirtualKey));

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            RegisterCallCount++;
            OnRegistrationAttempt?.Invoke(hotkey);
            var key = ((uint)hotkey.Modifiers, hotkey.VirtualKey);

            bool collides = _blocked.Contains(key)
                || System.Linq.Enumerable.Contains(_held.Values, key);
            if (collides)
            {
                errorCode = BlockedErrorCode;
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

    private sealed class FakeOverride : IHotkeyOverride
    {
        public Dictionary<int, Hotkey> Held { get; } = [];
        public List<int> ReleasedIds { get; } = [];
        public bool CanClaim { get; init; } = true;
        public int ClaimCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }

        public bool TryClaim(int id, Hotkey hotkey)
        {
            ClaimCallCount++;
            if (!CanClaim) return false;
            Held[id] = hotkey;
            return true;
        }

        public void Release(int id)
        {
            ReleasedIds.Add(id);
            Held.Remove(id);
        }

        public void Dispose() => DisposeCallCount++;
    }

    private static HotkeySettings OnlyCapture(Hotkey hotkey, bool forceCoreShortcuts = false) => new()
    {
        Capture = hotkey,
        OpenLibrary = Hotkey.None,
        PasteToScreen = Hotkey.None,
        HideAllPins = Hotkey.None,
        ToggleClickThrough = Hotkey.None,
        RepeatLastRegion = Hotkey.None,
        CaptureWindow = Hotkey.None,
        CaptureFullScreen = Hotkey.None,
        RecordRegion = Hotkey.None,
        UploadGitHubImage = Hotkey.None,
        ScrollingCapture = Hotkey.None,
        DelayedCapture = Hotkey.None,
        RestoreClosedPin = Hotkey.None,
        CaptureText = Hotkey.None,
        PauseRecording = Hotkey.None,
        ForceCoreShortcuts = forceCoreShortcuts,
    };

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
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
    public void NewInstall_F3AlreadyTaken_ReportsPasteToScreenInStartupFailures()
    {
        RunSta(() =>
        {
            Hotkey f3 = new(HotkeyModifiers.None, Hotkey.VkF3);
            var registrar = new FakeRegistrar([f3]);
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);

            service.Initialize(NewInstallDefaults.Create().Hotkeys);

            HotkeyRegistrationFailure failure = Assert.Single(service.Failures);
            Assert.Equal(GlobalHotkeyCommand.PasteToScreen, failure.Command);
            Assert.Equal(f3, failure.Hotkey);
            Assert.DoesNotContain(GlobalHotkeyCommand.PasteToScreen, service.RegisteredCommands);
            Assert.Contains(GlobalHotkeyCommand.CaptureRegion, service.RegisteredCommands);
            Assert.Contains(GlobalHotkeyCommand.RecordRegion, service.RegisteredCommands);
            Assert.Contains(GlobalHotkeyCommand.HideAllPins, service.RegisteredCommands);
        });
    }

    [Fact]
    public void RecorderProbeLeavesBindingsIntactAndNewCaptureCommandsRegister()
    {
        RunSta(() =>
        {
            var blocked = new Hotkey(HotkeyModifiers.Control, 0x78);
            var registrar = new FakeRegistrar([blocked]);
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
            var settings = new HotkeySettings
            {
                ScrollingCapture = new Hotkey(HotkeyModifiers.Control, 0x76),
                DelayedCapture = new Hotkey(HotkeyModifiers.Control, 0x77),
            };
            service.Initialize(settings);
            int held = registrar.Held.Count;
            Assert.True(service.CanRegister(settings.ScrollingCapture));
            Assert.True(service.CanRegister(new Hotkey(HotkeyModifiers.Control, 0x79)));
            Assert.False(service.CanRegister(blocked));
            Assert.Equal(held, registrar.Held.Count);
            Assert.Equal(settings.ScrollingCapture, service.GetAssigned(GlobalHotkeyCommand.ScrollingCapture));
            Assert.Equal(settings.DelayedCapture, service.GetAssigned(GlobalHotkeyCommand.DelayedCapture));
        });
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
    public void SettingsDiagnosticProbe_FallsBackAndAssignsOnlyCapture()
    {
        RunSta(() =>
        {
            HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
            var registrar = new FakeRegistrar(blocked:
            [
                new Hotkey(modifiers, Hotkey.VkF1 + 23), // F24
                new Hotkey(modifiers, Hotkey.VkF1 + 22), // F23
            ]);
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
            service.Initialize(new HotkeySettings());

            HotkeyReconfigureResult result = SettingsSelfTest.TryApplyDiagnosticHotkey(
                service,
                out Hotkey appliedHotkey);

            Assert.True(result.Applied);
            Assert.Equal(modifiers, appliedHotkey.Modifiers);
            Assert.Equal(Hotkey.VkF1 + 21, appliedHotkey.VirtualKey); // F22
            Assert.Single(service.RegisteredCommands);
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
                OpenLibrary = Hotkey.None,
                PasteToScreen = Hotkey.None,
                HideAllPins = Hotkey.None,
                ToggleClickThrough = Hotkey.None,
                RepeatLastRegion = Hotkey.None,
                CaptureWindow = Hotkey.None,
                CaptureFullScreen = Hotkey.None,
                RecordRegion = Hotkey.None,
                UploadGitHubImage = Hotkey.None,
            };

            HotkeyReconfigureResult result = service.Reconfigure(next);

            Assert.True(result.Applied);
            Assert.Single(service.RegisteredCommands);
            Assert.Contains(GlobalHotkeyCommand.CaptureRegion, service.RegisteredCommands);
        });
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkX)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkZ)]
    [InlineData(HotkeyModifiers.None, Hotkey.VkF3)]
    public void Initialize_ClaimsCoreConflictWhenForced(HotkeyModifiers modifiers, uint virtualKey)
    {
        RunSta(() =>
        {
            var chord = new Hotkey(modifiers, virtualKey);
            var registrar = new FakeRegistrar([chord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);

            service.Initialize(OnlyCapture(chord, forceCoreShortcuts: true));

            Assert.Empty(service.Failures);
            Assert.Equal(GlobalHotkeyCommand.CaptureRegion, Assert.Single(service.RegisteredCommands));
            Assert.Equal(GlobalHotkeyCommand.CaptureRegion, Assert.Single(service.OverriddenCommands));
            Assert.Equal(chord, service.GetAssigned(GlobalHotkeyCommand.CaptureRegion));
            Assert.Equal(chord, Assert.Single(hotkeyOverride.Held).Value);
            Assert.Empty(registrar.Held);
        });
    }

    [Theory]
    [InlineData(false, HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC, 1409)]
    [InlineData(true, HotkeyModifiers.Control, Hotkey.VkF1, 1409)]
    [InlineData(true, HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC, 87)]
    public void Initialize_DoesNotOverrideWithoutEligibleConflict(
        bool forceCoreShortcuts, HotkeyModifiers modifiers, uint virtualKey, int errorCode)
    {
        RunSta(() =>
        {
            var chord = new Hotkey(modifiers, virtualKey);
            var registrar = new FakeRegistrar([chord]) { BlockedErrorCode = errorCode };
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);

            service.Initialize(OnlyCapture(chord, forceCoreShortcuts));

            Assert.Equal(errorCode, Assert.Single(service.Failures).NativeErrorCode);
            Assert.Empty(service.RegisteredCommands);
            Assert.Empty(service.OverriddenCommands);
            Assert.Equal(0, hotkeyOverride.ClaimCallCount);
        });
    }

    [Fact]
    public void Initialize_ReportsConflictWhenOverrideCannotClaim()
    {
        RunSta(() =>
        {
            var chord = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3);
            var registrar = new FakeRegistrar([chord]);
            var hotkeyOverride = new FakeOverride { CanClaim = false };
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);

            service.Initialize(OnlyCapture(chord, forceCoreShortcuts: true));

            Assert.Equal(1409, Assert.Single(service.Failures).NativeErrorCode);
            Assert.Empty(service.RegisteredCommands);
            Assert.Empty(service.OverriddenCommands);
            Assert.Equal(1, hotkeyOverride.ClaimCallCount);
        });
    }

    [Fact]
    public void Initialize_PrefersNativeRegistrationWhenItSucceeds()
    {
        RunSta(() =>
        {
            var registrar = new FakeRegistrar();
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);

            service.Initialize(OnlyCapture(new Hotkey(HotkeyModifiers.None, Hotkey.VkF3), forceCoreShortcuts: true));

            Assert.Empty(service.Failures);
            Assert.Single(service.RegisteredCommands);
            Assert.Empty(service.OverriddenCommands);
            Assert.Equal(0, hotkeyOverride.ClaimCallCount);
            Assert.Single(registrar.Held);
        });
    }

    [Fact]
    public void Initialize_ClaimsCoreChordAssignedToAnotherCommandAndRoutesItsMessages()
    {
        RunSta(() =>
        {
            var chord = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3);
            var registrar = new FakeRegistrar([chord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            HotkeySettings settings = OnlyCapture(Hotkey.None, forceCoreShortcuts: true);
            settings.CaptureText = chord;
            service.Initialize(settings);

            Assert.Empty(service.Failures);
            Assert.Equal(GlobalHotkeyCommand.CaptureText, Assert.Single(service.RegisteredCommands));
            Assert.Equal(GlobalHotkeyCommand.CaptureText, Assert.Single(service.OverriddenCommands));
            Assert.Equal(chord, service.GetAssigned(GlobalHotkeyCommand.CaptureText));
            Assert.Null(service.GetAssigned(GlobalHotkeyCommand.PasteToScreen));
            int id = Assert.Single(hotkeyOverride.Held).Key;
            var pressed = new List<GlobalHotkeyCommand>();
            NativeWindowMessageEventArgs? receivedMessage = null;
            service.Pressed += (_, args) => pressed.Add(args.Command);
            window.MessageReceived += (_, args) =>
            {
                if (args.Message == NativeMethods.WM_HOTKEY) receivedMessage = args;
            };

            Assert.True(service.PostDiagnosticCommand(GlobalHotkeyCommand.CaptureText));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);

            Assert.Equal(GlobalHotkeyCommand.CaptureText, Assert.Single(pressed));
            Assert.NotNull(receivedMessage);
            Assert.Equal(new IntPtr(id), receivedMessage.WParam);
            Assert.True(receivedMessage.Handled);
        });
    }

    [Fact]
    public void Reconfigure_ClaimsCoreConflictWhenForceIsEnabled()
    {
        RunSta(() =>
        {
            var chord = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3);
            var registrar = new FakeRegistrar([chord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(Hotkey.None));

            HotkeyReconfigureResult result = service.Reconfigure(OnlyCapture(chord, forceCoreShortcuts: true));

            Assert.True(result.Applied);
            Assert.Empty(result.Failures);
            Assert.Empty(service.Failures);
            Assert.Equal(GlobalHotkeyCommand.CaptureRegion, Assert.Single(service.OverriddenCommands));
            Assert.Equal(chord, service.GetAssigned(GlobalHotkeyCommand.CaptureRegion));
        });
    }

    [Fact]
    public void Reconfigure_ReleasesOverrideWhenItsChordIsRemoved()
    {
        RunSta(() =>
        {
            var chord = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3);
            var registrar = new FakeRegistrar([chord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(chord, forceCoreShortcuts: true));
            int id = Assert.Single(hotkeyOverride.Held).Key;

            HotkeyReconfigureResult result = service.Reconfigure(OnlyCapture(Hotkey.None));

            Assert.True(result.Applied);
            Assert.Empty(service.OverriddenCommands);
            Assert.Empty(hotkeyOverride.Held);
            Assert.Equal(id, Assert.Single(hotkeyOverride.ReleasedIds));
            Assert.False(service.CanRegister(chord));
        });
    }

    [Fact]
    public void Reconfigure_RollbackRestoresOverridesAndPreviousForceFlag()
    {
        RunSta(() =>
        {
            var previousChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
            var blockedChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkX);
            var unassignedCoreChord = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3);
            var registrar = new FakeRegistrar([previousChord, blockedChord, unassignedCoreChord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(previousChord, forceCoreShortcuts: true));
            int id = Assert.Single(hotkeyOverride.Held).Key;

            HotkeyReconfigureResult result = service.Reconfigure(OnlyCapture(blockedChord));

            Assert.False(result.Applied);
            Assert.Equal(blockedChord, Assert.Single(result.Failures).Hotkey);
            Assert.Equal(previousChord, service.GetAssigned(GlobalHotkeyCommand.CaptureRegion));
            Assert.Equal(GlobalHotkeyCommand.CaptureRegion, Assert.Single(service.OverriddenCommands));
            Assert.Equal(previousChord, Assert.Single(hotkeyOverride.Held).Value);
            Assert.Equal(id, Assert.Single(hotkeyOverride.ReleasedIds));
            Assert.Equal(2, hotkeyOverride.ClaimCallCount);
            int calls = registrar.RegisterCallCount;
            Assert.True(service.CanRegister(unassignedCoreChord));
            Assert.Equal(calls, registrar.RegisterCallCount);
        });
    }

    [Fact]
    public void Reconfigure_RollbackReleasesNewOverridesAndRestoresForceDisabled()
    {
        RunSta(() =>
        {
            var previousChord = new Hotkey(HotkeyModifiers.Control, Hotkey.VkF1);
            var coreChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
            var blockedChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkX);
            var registrar = new FakeRegistrar([coreChord, blockedChord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(previousChord));
            HotkeySettings next = OnlyCapture(coreChord, forceCoreShortcuts: true);
            next.RecordRegion = blockedChord;

            HotkeyReconfigureResult result = service.Reconfigure(next);

            Assert.False(result.Applied);
            Assert.Equal(blockedChord, Assert.Single(result.Failures).Hotkey);
            Assert.Equal(previousChord, service.GetAssigned(GlobalHotkeyCommand.CaptureRegion));
            Assert.Empty(service.OverriddenCommands);
            Assert.Empty(hotkeyOverride.Held);
            Assert.Single(hotkeyOverride.ReleasedIds);
            Assert.Equal(1, hotkeyOverride.ClaimCallCount);
            Assert.False(service.CanRegister(coreChord));
        });
    }

    [Fact]
    public void Dispose_ReleasesOverridesAndDisposesOverrideOnce()
    {
        RunSta(() =>
        {
            var chord = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3);
            var registrar = new FakeRegistrar([chord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(chord, forceCoreShortcuts: true));
            int id = Assert.Single(hotkeyOverride.Held).Key;

            service.Dispose();
            service.Dispose();

            Assert.Empty(service.RegisteredCommands);
            Assert.Empty(service.OverriddenCommands);
            Assert.Empty(hotkeyOverride.Held);
            Assert.Equal(id, Assert.Single(hotkeyOverride.ReleasedIds));
            Assert.Equal(1, hotkeyOverride.DisposeCallCount);
        });
    }

    [Fact]
    public void Reconfigure_RollbackPreservesExistingStartupConflictAfterDecline()
    {
        RunSta(() =>
        {
            var chord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
            var registrar = new FakeRegistrar([chord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(chord));
            HotkeyRegistrationFailure originalFailure = Assert.Single(service.Failures);
            HotkeySettings declined = OnlyCapture(chord);
            CoreShortcuts.Decline(declined);

            HotkeyReconfigureResult result = service.Reconfigure(declined);

            Assert.False(result.Applied);
            Assert.Equal(originalFailure, Assert.Single(service.Failures));
            Assert.Empty(service.RegisteredCommands);
            Assert.Empty(service.OverriddenCommands);
            Assert.Equal(0, hotkeyOverride.ClaimCallCount);
        });
    }

    [Fact]
    public void Reconfigure_RollbackClearsPreviousFailureWhenChordBecomesAvailable()
    {
        RunSta(() =>
        {
            var previousChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
            var proposedChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkX);
            var registrar = new FakeRegistrar([previousChord, proposedChord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(previousChord));
            Assert.Single(service.Failures);
            registrar.OnRegistrationAttempt = chord =>
            {
                if (chord == proposedChord) registrar.Unblock(previousChord);
            };

            HotkeyReconfigureResult result = service.Reconfigure(OnlyCapture(proposedChord));

            Assert.False(result.Applied);
            Assert.Equal(proposedChord, Assert.Single(result.Failures).Hotkey);
            Assert.Empty(service.Failures);
            Assert.Equal(previousChord, service.GetAssigned(GlobalHotkeyCommand.CaptureRegion));
        });
    }

    [Fact]
    public void Reconfigure_KeepAvailableKeepsClaimedAndRegisteredChordsBesideAnUnrelatedConflict()
    {
        RunSta(() =>
        {
            // Another program owns F3 and Shift+F3; the legacy set hides pins with Shift+F3.
            var shiftF3 = new Hotkey(HotkeyModifiers.Shift, Hotkey.VkF3);
            var registrar = new FakeRegistrar([CoreShortcuts.PasteToScreen, shiftF3]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            var legacy = new HotkeySettings();
            service.Initialize(legacy);
            Assert.Equal(2, service.Failures.Count);
            var accepted = new HotkeySettings();
            CoreShortcuts.Accept(accepted);

            HotkeyReconfigureResult result = service.Reconfigure(accepted, keepAvailable: true);

            Assert.True(result.Applied);
            HotkeyRegistrationFailure failure = Assert.Single(result.Failures);
            Assert.Equal(GlobalHotkeyCommand.HideAllPins, failure.Command);
            Assert.Equal(shiftF3, failure.Hotkey);
            Assert.Equal(1409, failure.NativeErrorCode);
            Assert.Equal(failure, Assert.Single(service.Failures));
            Assert.Equal(GlobalHotkeyCommand.PasteToScreen, Assert.Single(service.OverriddenCommands));
            Assert.Equal(CoreShortcuts.PasteToScreen, Assert.Single(hotkeyOverride.Held).Value);
            Assert.Equal(CoreShortcuts.PasteToScreen, service.GetAssigned(GlobalHotkeyCommand.PasteToScreen));
            Assert.Equal(CoreShortcuts.Capture, service.GetAssigned(GlobalHotkeyCommand.CaptureRegion));
            Assert.Equal(CoreShortcuts.RecordRegion, service.GetAssigned(GlobalHotkeyCommand.RecordRegion));
            Assert.Equal(CoreShortcuts.OpenLibrary, service.GetAssigned(GlobalHotkeyCommand.OpenLibrary));
            Assert.Equal(legacy.UploadGitHubImage, service.GetAssigned(GlobalHotkeyCommand.UploadGitHubImage));
            Assert.Null(service.GetAssigned(GlobalHotkeyCommand.HideAllPins));
            Assert.Equal(4, registrar.Held.Count);
        });
    }

    [Fact]
    public void Reconfigure_WithoutKeepAvailableStillRollsBackTheSameConflict()
    {
        RunSta(() =>
        {
            var shiftF3 = new Hotkey(HotkeyModifiers.Shift, Hotkey.VkF3);
            var registrar = new FakeRegistrar([CoreShortcuts.PasteToScreen, shiftF3]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            var legacy = new HotkeySettings();
            service.Initialize(legacy);
            var accepted = new HotkeySettings();
            CoreShortcuts.Accept(accepted);

            HotkeyReconfigureResult result = service.Reconfigure(accepted);

            Assert.False(result.Applied);
            Assert.Equal(shiftF3, Assert.Single(result.Failures).Hotkey);
            Assert.Empty(service.OverriddenCommands);
            Assert.Empty(hotkeyOverride.Held);
            Assert.Equal(2, service.Failures.Count);
            Assert.Contains(service.Failures, item => item.Hotkey == CoreShortcuts.PasteToScreen);
            Assert.Contains(service.Failures, item => item.Hotkey == shiftF3);
        });
    }

    [Fact]
    public void Reconfigure_RollbackReportsFailureToRestorePreviousChord()
    {
        RunSta(() =>
        {
            var previousChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
            var proposedChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkX);
            var registrar = new FakeRegistrar([proposedChord]);
            var hotkeyOverride = new FakeOverride();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance, hotkeyOverride);
            service.Initialize(OnlyCapture(previousChord));
            registrar.OnRegistrationAttempt = chord =>
            {
                if (chord == proposedChord) registrar.Block(previousChord);
            };

            HotkeyReconfigureResult result = service.Reconfigure(OnlyCapture(proposedChord));

            Assert.False(result.Applied);
            Assert.Equal(proposedChord, Assert.Single(result.Failures).Hotkey);
            Assert.Equal(previousChord, Assert.Single(service.Failures).Hotkey);
            Assert.Empty(service.RegisteredCommands);
        });
    }
}
