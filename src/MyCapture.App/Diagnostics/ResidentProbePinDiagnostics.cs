using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Pinning;

namespace MyCapture.App.Diagnostics;

/// <summary>Entirely opt-in pin render variants; never installs input handlers or timers in the product.</summary>
internal sealed class ResidentProbePinDiagnostics : IDisposable
{
    internal static ResidentProbePinDiagnostics? Current { get; private set; }
    private readonly ResidentProbeOptions _options;
    private readonly ResidentProbeRecorder _recorder;
    private readonly Dictionary<PinWindow, Entry> _pins = [];
    private readonly List<Task> _switches = [];
    private readonly DispatcherTimer? _timer;
    private readonly Action<PinWindow>? _previous;
    private readonly bool _installed;
    private bool _disposed;
    internal string Scenario { get; set; } = "pin-variant";
    internal int NextIndex { get; set; }

    private sealed class Entry(PinWindow pin, string scenario, int index)
    {
        internal readonly PinWindow Pin = pin;
        internal readonly string Scenario = scenario;
        internal readonly int Index = index;
        internal long LastActivity = Stopwatch.GetTimestamp();
        internal bool Software;
        internal bool Switching;
        internal int SwitchGeneration;
        internal DependencyPropertyDescriptor? Scaling;
        internal EventHandler? ScaleChanged;
        internal Action? StateChanged;
        internal HwndSourceHook? PaintHook;
        internal HwndSource? Source;
        internal int Paints;
    }

    internal ResidentProbePinDiagnostics(ResidentProbeOptions options, ResidentProbeRecorder recorder)
    {
        _options = options;
        _recorder = recorder;
        _previous = ResidentProbeWindowHook.PinConstructed;
        Current = this;
        _installed = options.Scenarios.Any(IsScenario) || options.Variants.Any(v =>
            v.StartsWith("sw-pin", StringComparison.Ordinal) || v.StartsWith("pin-", StringComparison.Ordinal));
        if (!_installed) return;
        ResidentProbeWindowHook.PinConstructed = Attach;
        if (options.Has("sw-pin-idle") && !FixedSoftware)
        {
            InputManager.Current.PreProcessInput += OnInput;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += OnIdle;
            _timer.Start();
        }
        recorder.Note("Pin diagnostics: frame endpoint = first eligible CompositionTarget.Rendering then next Send dispatcher turn / MediaContext.CompleteRender return. Not physical display latency. Landing intervals = distinct RenderingTime ticks (UI animation cadence, not GPU presents). CommitBytes = PROCESS_MEMORY_COUNTERS_EX.PrivateUsage (process commit, normally same as private bytes); Map.CommittedBytes includes private, section and image mappings, not system commit charge. All MB in reports are MiB.");
        if (options.Has("sw-pin-idle")) recorder.Note("sw-pin-idle flash check: NOT CHECKED in-process. Desktop video/visual inspection at both render-mode transitions is required; render completion cannot detect a compositor flash. Fixed sw-pin/sw-pin-aux takes precedence when combined with sw-pin-idle.");
    }

    internal static bool IsScenario(string scenario) => scenario is "pin-1600" or "pin-1080" or "pin-4k" or "pin-text";
    private bool FixedSoftware => _options.Has("sw-pin") || _options.Has("sw-pin-aux");

    private void Attach(PinWindow pin)
    {
        _previous?.Invoke(pin);
        if (_options.Has("pin-opaque"))
        {
            pin.AllowsTransparency = false;
            pin.Background = Brushes.White;
        }
        var entry = new Entry(pin, Scenario, NextIndex);
        _pins.Add(pin, entry);
        if (_options.Has("pin-scale-linear"))
        {
            entry.Scaling = DependencyPropertyDescriptor.FromProperty(RenderOptions.BitmapScalingModeProperty, pin.ResidentProbeImage.GetType());
            entry.ScaleChanged = (_, _) =>
            {
                if (RenderOptions.GetBitmapScalingMode(pin.ResidentProbeImage) == BitmapScalingMode.HighQuality)
                    RenderOptions.SetBitmapScalingMode(pin.ResidentProbeImage, BitmapScalingMode.Linear);
            };
            entry.Scaling?.AddValueChanged(pin.ResidentProbeImage, entry.ScaleChanged);
            RenderOptions.SetBitmapScalingMode(pin.ResidentProbeImage, BitmapScalingMode.Linear);
        }
        pin.SourceInitialized += OnSourceInitialized;
        pin.Closed += OnClosed;
        if (_timer is not null)
        {
            entry.StateChanged = () => Interact(pin);
            pin.ViewStateChanged += entry.StateChanged;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not PinWindow pin || !_pins.TryGetValue(pin, out Entry? entry)) return;
        // Handle is now available, before the first frame. The product pays only the constructor's
        // null check: this SourceInitialized handler exists solely inside the diagnostic run.
        ResidentProbeWindowHook.SourceInitialized?.Invoke(pin);
        entry.Source = HwndSource.FromHwnd(pin.Handle);
        if (FixedSoftware && entry.Source?.CompositionTarget is { } target) target.RenderMode = RenderMode.SoftwareOnly;
        entry.Software = entry.Source?.CompositionTarget?.RenderMode == RenderMode.SoftwareOnly;
        entry.PaintHook = (IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled) =>
        {
            if (message == 0x000F) entry.Paints++;
            return IntPtr.Zero;
        };
        entry.Source?.AddHook(entry.PaintHook);
    }

    private void OnInput(object sender, PreProcessInputEventArgs args)
    {
        if (args.StagingItem.Input is not (MouseEventArgs or KeyboardEventArgs or TouchEventArgs or StylusEventArgs)) return;
        if ((args.StagingItem.Input.OriginalSource ?? args.StagingItem.Input.Source
            ?? (args.StagingItem.Input is MouseEventArgs mouse ? mouse.MouseDevice.DirectlyOver : Keyboard.FocusedElement))
            is not DependencyObject source) return;
        Window? window = Window.GetWindow(source);
        PinWindow? pin = window as PinWindow ?? window?.Owner as PinWindow;
        if (pin is not null) Interact(pin);
    }

    internal void Interact(PinWindow pin)
    {
        if (!_pins.TryGetValue(pin, out Entry? entry)) return;
        entry.LastActivity = Stopwatch.GetTimestamp();
        if (entry.Software && !FixedSoftware) Switch(entry, software: false);
    }

    private void OnIdle(object? sender, EventArgs args)
    {
        foreach (Entry entry in _pins.Values.ToArray())
        {
            if (entry.Pin.IsClosed || !entry.Pin.IsShown) continue;
            if (entry.Pin.ResidentProbeBusy)
            {
                Interact(entry.Pin);
                continue;
            }
            if (!entry.Software && !entry.Switching && Stopwatch.GetElapsedTime(entry.LastActivity).TotalSeconds >= 2)
                Switch(entry, software: true);
        }
    }

    private void Switch(Entry entry, bool software)
    {
        if (entry.Source?.CompositionTarget is not { } target || entry.Pin.IsClosed) return;
        // Run the setter synchronously before returning to the input handler. The asynchronous
        // part only measures the next frame. A quick wake can supersede an outstanding idle fence.
        entry.Software = software;
        entry.Switching = true;
        _switches.Add(MeasureSwitchAsync(entry, target, software, ++entry.SwitchGeneration));
    }

    private async Task MeasureSwitchAsync(Entry entry, HwndTarget target, bool software, int generation)
    {
        string name = software ? "switch-to-software" : "switch-to-hardware";
        try
        {
            var frame = await ResidentProbePresentation.MeasureAsync(entry.Pin.Dispatcher, () =>
            {
                target.RenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;
                entry.Pin.InvalidateVisual();
            }, () => entry.Pin.IsVisible && !entry.Pin.IsClosed);
            _recorder.PinFrame(entry.Scenario, entry.Index, name, generation == entry.SwitchGeneration ? frame
                : frame with { CompleteMs = null, Error = "Render mode changed again before the frame fence completed" });
            _recorder.ObservePin(entry.Scenario, entry.Index, name + "-flash", "not checked; desktop visual/video inspection required");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            _recorder.PinFrame(entry.Scenario, entry.Index, name, new(0, null, ex.Message));
        }
        finally { if (generation == entry.SwitchGeneration) entry.Switching = false; }
    }

    internal bool IsSoftware(PinWindow pin) => _pins.TryGetValue(pin, out Entry? entry) && entry.Software;
    internal int PaintCount(PinWindow pin) => _pins.TryGetValue(pin, out Entry? entry) ? entry.Paints : 0;
    internal static bool IsLayered(PinWindow pin) => (GetWindowLong(pin.Handle, -20) & 0x80000) != 0;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    internal async Task DrainAsync()
    {
        Task[] pending = _switches.ToArray();
        await Task.WhenAll(pending);
        _switches.RemoveAll(task => task.IsCompleted);
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (sender is PinWindow pin) Detach(pin);
    }

    private void Detach(PinWindow pin)
    {
        if (!_pins.Remove(pin, out Entry? entry)) return;
        pin.SourceInitialized -= OnSourceInitialized;
        pin.Closed -= OnClosed;
        if (entry.StateChanged is not null) pin.ViewStateChanged -= entry.StateChanged;
        if (entry.ScaleChanged is not null) entry.Scaling?.RemoveValueChanged(pin.ResidentProbeImage, entry.ScaleChanged);
        if (entry.PaintHook is not null && entry.Source is { IsDisposed: false }) entry.Source.RemoveHook(entry.PaintHook);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Stop();
        if (_timer is not null) InputManager.Current.PreProcessInput -= OnInput;
        foreach (PinWindow pin in _pins.Keys.ToArray()) Detach(pin);
        if (_installed) ResidentProbeWindowHook.PinConstructed = _previous;
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
