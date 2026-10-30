using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class WatermarkRound2Tests
{
    [Fact]
    public Task BarChangeRefreshesOpenSettingsWithoutDiscardingOtherDrafts() => SettingsThemePreviewTests.RunIsolated(
        nameof(BarChangeRefreshesOpenSettingsWithoutDiscardingOtherDrafts), () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        AppSettings saved = new();
        int saves = 0;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            saved = candidate; saves++;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance) { ScheduleDelay = (_, _) => new NoopDelay() };
        try
        {
            var draft = Assert.IsType<SettingsDraft>(window.DataContext);
            draft.RecordingStartDelaySeconds = "7";
            Dictionary<string, object?> accepted = Accepted(window);
            Dictionary<string, object?> originalAccepted = accepted.ToDictionary(pair => pair.Key, pair => pair.Value);
            var external = new RecordingWatermarkSettings
            {
                Enabled = true, Source = RecordingWatermarkSource.Text, Text = "From recording bar",
                Anchor = StampAnchor.TopLeft, WidthFraction = 0.27, Margin = 32, Opacity = 0.7,
            };
            // The app's recording-bar callback saves this value while the settings window remains open.
            saved.Recording.Watermark = external;
            window.RefreshRecordingWatermark();
            Assert.Equal(external, draft.RecordingWatermark);
            Assert.Equal("7", draft.RecordingStartDelaySeconds);
            Assert.Equal(0, saves);
            Assert.Equal(external, accepted[nameof(SettingsDraft.RecordingWatermark)]);
            Assert.Equal(true, accepted[nameof(SettingsDraft.RecordingWatermarkEnabled)]);
            foreach ((string key, object? value) in originalAccepted)
                if (key is not nameof(SettingsDraft.RecordingWatermark) and not nameof(SettingsDraft.RecordingWatermarkEnabled))
                    Assert.Equal(value, accepted[key]);

            RecordingWatermarkSettings? editing = null;
            window.EditRecordingWatermarkSettings = current => { editing = current; return current with { Opacity = 0.4 }; };
            var section = Assert.IsType<StackPanel>(window.FindName("RecordingWatermarkSection"));
            section.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(external, editing);
            Assert.True(window.FlushPendingSettings());
            Assert.Equal(external with { Opacity = 0.4 }, saved.Recording.Watermark);
            Assert.Equal(7, saved.Recording.StartDelaySeconds);
        }
        finally { window.CloseForExit(); application.Shutdown(); }
    }, typeof(WatermarkRound2Tests));

    [Fact]
    public void NativeWatermarkPlacementPreservesActiveInputWindow() => StaTestHost.Run(() =>
    {
        var input = new Window
        {
            Width = 220, Height = 100, Left = -30000, Top = -30000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        RecordingWatermarkWindow? watermark = null;
        IntPtr edit = IntPtr.Zero;
        try
        {
            input.Show();
            IntPtr inputHwnd = new WindowInteropHelper(input).Handle;
            edit = CreateWindowEx(0, "EDIT", "Synthetic input", 0x50000000,
                2, 2, 160, 24, inputHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, edit);
            _ = SetActiveWindow(inputHwnd);
            _ = SetFocus(edit);
            Assert.Equal(inputHwnd, GetActiveWindow());
            Assert.Equal(edit, GetFocus());
            Assert.True(GetWindowRect(inputHwnd, out NativeRect inputBounds));
            Assert.True(inputBounds.Right < SystemParameters.VirtualScreenLeft && inputBounds.Bottom < SystemParameters.VirtualScreenTop);

            var region = new RectD(-28000, -28000, 800, 450);
            RecordingWatermarkSnapshot snapshot = RecordingWatermarkSnapshot.Create(new RecordingWatermarkSettings
                { Enabled = true, Source = RecordingWatermarkSource.Text, Text = "Watermark" }, null);
            watermark = new RecordingWatermarkWindow(region, 1, snapshot);
            IntPtr watermarkHwnd = new WindowInteropHelper(watermark).EnsureHandle();
            Assert.True(GetWindowRect(watermarkHwnd, out NativeRect watermarkBounds));
            Assert.True(watermarkBounds.Right < SystemParameters.VirtualScreenLeft && watermarkBounds.Bottom < SystemParameters.VirtualScreenTop);
            Assert.True(inputHwnd == GetActiveWindow(), "Creating the offscreen watermark changed the active HWND.");
            Assert.Equal(edit, GetFocus());
            watermark.UpdateRecordingRegion(region with { X = -27900 }, 1.5);
            Assert.True(inputHwnd == GetActiveWindow(), "Repositioning the offscreen watermark changed the active HWND.");
            Assert.Equal(edit, GetFocus());
            watermark.Close();
            snapshot = RecordingWatermarkSnapshot.Create(snapshot.Settings with { Text = "Updated preview" }, null);
            watermark = new RecordingWatermarkWindow(region, 1, snapshot);
            _ = new WindowInteropHelper(watermark).EnsureHandle();
            Assert.True(inputHwnd == GetActiveWindow(), "Replacing the offscreen watermark preview changed the active HWND.");
            Assert.Equal(edit, GetFocus());
        }
        finally
        {
            watermark?.Close();
            if (edit != IntPtr.Zero) _ = DestroyWindow(edit);
            input.Close();
        }
    });

    private static Dictionary<string, object?> Accepted(SettingsWindow window) =>
        (Dictionary<string, object?>)typeof(SettingsWindow).GetField("_acceptedValues", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private sealed class NoopDelay : IDisposable { public void Dispose() { } }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
