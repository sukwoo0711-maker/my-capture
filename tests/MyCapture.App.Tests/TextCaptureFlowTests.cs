using System.Reflection;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Shell;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>Text capture from the overlay's T key through the coordinator to the hotkey service.</summary>
public sealed class TextCaptureFlowTests
{
    private static readonly Hotkey TextChord = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54);

    [Fact]
    public void TKeyTogglesTextModeOnlyWhenTheOwnerEnablesIt() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        PointD pointer = new(100, 100);
        var view = new CaptureOverlayView(Frame(), showMagnifier: false)
        {
            ReadCursor = () => pointer,
            TextCaptureAvailable = true,
            ShowModeBar = true,
        };
        var plain = new CaptureOverlayView(Frame(), showMagnifier: false) { ReadCursor = () => pointer };
        RegionSelectionEventArgs? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        try
        {
            view.InitializePointer();
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.True(bar.CaptureButton.IsChecked);
            Assert.False(bar.TextButton.IsChecked);
            KeyEventArgs first = KeyArgs(Key.T);
            view.RaiseEvent(first);
            Assert.True(first.Handled);
            Assert.True(view.TextCaptureMode);
            // The bar fills in the text button, so T needs no separate hint chip.
            Assert.True(bar.TextButton.IsChecked);
            Assert.False(bar.CaptureButton.IsChecked);
            view.RaiseEvent(KeyArgs(Key.T));
            Assert.False(view.TextCaptureMode);
            Assert.True(bar.CaptureButton.IsChecked);
            view.RaiseEvent(KeyArgs(Key.T));

            Invoke(view, "OnMouseLeftButtonDown", Button(System.Windows.UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(160, 140);
            Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Invoke(view, "OnMouseLeftButtonUp", Button(System.Windows.UIElement.MouseLeftButtonUpEvent));
            PumpUntil(() => confirmed is not null);
            Assert.True(confirmed!.TextCapture);
            Assert.Equal(new RectD(100, 100, 60, 40), confirmed.BitmapRegion);

            plain.InitializePointer();
            KeyEventArgs ignored = KeyArgs(Key.T);
            plain.RaiseEvent(ignored);
            Assert.False(ignored.Handled);
            Assert.False(plain.TextCaptureMode);
        }
        finally
        {
            view.ReleaseResources();
            plain.ReleaseResources();
        }
    });

    [Fact]
    public void OverlayForwardsTheTextModeToTheCompletedSelection() => StaTestHost.Run(() =>
    {
        var window = new CaptureOverlayWindow(Frame(), abortOnFocusLoss: false) { TextCaptureAvailable = true };
        CaptureSelectionCompletedEventArgs? completed = null;
        window.SelectionCompleted += (_, e) => completed = e;
        window.CompleteSelection(new RectD(10, 20, 30, 40), detectedWindow: null, textCapture: true);
        Assert.NotNull(completed);
        Assert.True(completed!.TextCapture);
        Assert.Equal(30, completed.SelectedBitmap.PixelWidth);
        Assert.Equal(40, completed.SelectedBitmap.PixelHeight);
    });

    [Fact]
    public void TextSelectionSkipsPersistenceAndEditorWhileImageSelectionKeepsThem() => StaTestHost.Run(() =>
    {
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance);
        int persisted = 0;
        int editorChecks = 0;
        var texts = new List<CaptureSelectionCompletedEventArgs>();
        coordinator.SelectionPersistRequested = _ => { persisted++; return Task.CompletedTask; };
        coordinator.OpenEditorAfterCapture = () => { editorChecks++; return false; };
        coordinator.TextCaptureRequested = texts.Add;
        FrozenFrame frame = Frame();
        MethodInfo completed = typeof(CaptureOverlayCoordinator).GetMethod("OnOverlaySelectionCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var text = new CaptureSelectionCompletedEventArgs(frame, new RectD(0, 0, 20, 10),
            ScreenCaptureEngine.Crop(frame, new RectD(0, 0, 20, 10))) { TextCapture = true };
        completed.Invoke(coordinator, [null, text]);
        Assert.Same(text, Assert.Single(texts));
        Assert.Equal(0, persisted);
        Assert.Equal(0, editorChecks);
        Assert.False(coordinator.IsActive);

        var image = new CaptureSelectionCompletedEventArgs(frame, new RectD(0, 0, 20, 10),
            ScreenCaptureEngine.Crop(frame, new RectD(0, 0, 20, 10)));
        completed.Invoke(coordinator, [null, image]);
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        Assert.Single(texts);
        Assert.Equal(1, persisted);
        Assert.Equal(1, editorChecks);

        // Without a text handler a stray text-mode selection still takes the normal path.
        coordinator.TextCaptureRequested = null;
        completed.Invoke(coordinator, [null, text]);
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        Assert.Equal(2, persisted);
    });

    [Fact]
    public void TextCaptureHotkeyRegistersRoutesAndClonesLikeTheOtherOptionalCommands() => StaTestHost.Run(() =>
    {
        Assert.Contains(GlobalHotkeyCommand.CaptureText, Enum.GetValues<GlobalHotkeyCommand>());
        var registrar = new RecordingRegistrar();
        using var window = new NativeMessageWindow();
        using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
        var pressed = new List<GlobalHotkeyCommand>();
        service.Pressed += (_, e) => pressed.Add(e.Command);

        service.Initialize(new HotkeySettings());
        Assert.DoesNotContain(GlobalHotkeyCommand.CaptureText, service.RegisteredCommands);

        var settings = new HotkeySettings { CaptureText = TextChord };
        Assert.Equal(TextChord, settings.DeepCloneHotkeys().CaptureText);
        Assert.True(service.Reconfigure(settings).Applied);
        Assert.Equal(TextChord, service.GetAssigned(GlobalHotkeyCommand.CaptureText));
        Assert.Contains(TextChord, registrar.Held.Values);
        Assert.True(service.PostDiagnosticCommand(GlobalHotkeyCommand.CaptureText));
        PumpUntil(() => pressed.Count > 0);
        Assert.Equal(GlobalHotkeyCommand.CaptureText, Assert.Single(pressed));

        Assert.True(service.Reconfigure(new HotkeySettings()).Applied);
        Assert.Null(service.GetAssigned(GlobalHotkeyCommand.CaptureText));
        Assert.DoesNotContain(TextChord, registrar.Held.Values);
    });

    private sealed class RecordingRegistrar : IHotkeyRegistrar
    {
        private readonly Dictionary<int, Hotkey> _held = [];
        internal IReadOnlyDictionary<int, Hotkey> Held => _held;

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            if (_held.ContainsValue(hotkey))
            {
                errorCode = 1409;
                errorMessage = "taken";
                return false;
            }

            _held[id] = hotkey;
            return true;
        }

        public void Unregister(int id) => _held.Remove(id);
    }
}
