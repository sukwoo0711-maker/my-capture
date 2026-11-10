using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioEvidenceTests
{
    [Fact]
    public Task RenderAllStates() => SettingsThemePreviewTests.RunIsolated(nameof(RenderAllStates), () =>
    {
        string directory = OwnedTestDirectory.Create("mc-recording-audio-evidence-");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        ThemeService.Apply(AppTheme.Daylight);
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach (string culture in new[] { "ko-KR", "en-US" })
            foreach (string state in new[] { "ready", "recording", "paused", "saving" })
            {
                using var language = UiText.UseLanguage(culture);
                var window = Create(state);
                try
                {
                    Border strip = Field<Border>(window, "_controlStrip");
                    foreach (double scale in new[] { 1d, 1.5d })
                    {
                        strip.Measure(new Size(strip.Width, strip.Height));
                        strip.Arrange(new Rect(0, 0, strip.Width, strip.Height)); strip.UpdateLayout();
                        var image = new RenderTargetBitmap((int)Math.Ceiling(strip.ActualWidth * scale),
                            (int)Math.Ceiling(strip.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        image.Render(strip);
                        TestDiagnosticArtifacts.SavePng(directory, $"{culture}-{state}-{(int)(scale * 100)}.png", image);
                    }
                }
                finally { window.Close(); }
            }
            Console.WriteLine($"Recording audio visual evidence: {directory}");
            Assert.Equal(16, System.IO.Directory.GetFiles(directory, "*.png").Length);
        }
        finally { application.Shutdown(); FluidMotion.AnimationsEnabledOverrideForTest = null; }
        // Intentional retained diagnostic artifacts, owned by this test process; no cleanup mutation.
    }, typeof(RecordingAudioEvidenceTests));

    internal static RecordingControlWindow Create(string state)
    {
        var window = new RecordingControlWindow(new RectD(100, 100, 640, 360), new RecordingSettings(),
            () => throw new InvalidOperationException("Visual evidence never records."), () => "unused.mp4", NullLogger.Instance);
        if (state is "recording" or "paused") window.PreviewRecordingForTest(state == "paused", TimeSpan.FromSeconds(12));
        if (state == "saving")
        {
            window.PreviewRecordingForTest(false, TimeSpan.FromSeconds(12));
            window.PauseButtonForTest.IsEnabled = false;
            typeof(RecordingControlWindow).GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            typeof(RecordingControlWindow).GetMethod("ShowCompletionStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [UiText.Get("Text_528544494302")]);
        }
        return window;
    }

    internal static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
