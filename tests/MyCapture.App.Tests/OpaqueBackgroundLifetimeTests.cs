using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OpaqueBackgroundLifetimeTests
{
    [Fact]
    public Task FallbackSourceSurvivesCollectionWhileBackgroundIsOwned() => SettingsThemePreviewTests.RunIsolated(
        nameof(FallbackSourceSurvivesCollectionWhileBackgroundIsOwned), () =>
        {
            Assert.Null(Application.Current);
            BrushSample sample = CreateBackground();
            Color expected = Color.FromRgb(0x15, 0x1E, 0x2B);
            AssertOpaque(sample.Brush, expected);

            Collect();

            Assert.True(sample.Source.IsAlive, "A live background must retain its private binding source.");
            Expression(sample.Brush).UpdateTarget();
            AssertOpaque(sample.Brush, expected);
            Assert.Equal(BindingStatus.Active, Expression(sample.Brush).Status);
            GC.KeepAlive(sample.Brush);
        }, typeof(OpaqueBackgroundLifetimeTests));

    [Fact]
    public Task ThemeSourceSurvivesCollectionAndKeepsFollowingReplacementTokens() => SettingsThemePreviewTests.RunIsolated(
        nameof(ThemeSourceSurvivesCollectionAndKeepsFollowingReplacementTokens), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources["Surface.Floating"] = new SolidColorBrush(Color.FromArgb(128, 30, 40, 50)) { Opacity = 0.25 };
            BrushSample sample = CreateBackground();
            AssertOpaque(sample.Brush, Color.FromRgb(30, 40, 50));

            foreach (Color next in new[]
            {
                Color.FromArgb(64, 220, 230, 240),
                Color.FromArgb(0, 10, 20, 30),
                Color.FromArgb(255, 70, 80, 90),
            })
            {
                Collect();
                Assert.True(sample.Source.IsAlive, "GC must not detach a background from live theme changes.");
                application.Resources["Surface.Floating"] = new SolidColorBrush(next) { Opacity = 0 };
                ThemeBrushes.Refresh(application.Resources);
                Drain();

                // Verify the normal notification path before forcing an explicit binding update.
                Color expected = Color.FromRgb(next.R, next.G, next.B);
                AssertOpaque(sample.Brush, expected);
                Collect();
                Expression(sample.Brush).UpdateTarget();
                AssertOpaque(sample.Brush, expected);
                Assert.Equal(BindingStatus.Active, Expression(sample.Brush).Status);
            }

            GC.KeepAlive(sample.Brush);
            GC.KeepAlive(application);
        }, typeof(OpaqueBackgroundLifetimeTests));

    [Fact]
    public Task ClosedWindowReleasesBackgroundAndThemeSource() => SettingsThemePreviewTests.RunIsolated(
        nameof(ClosedWindowReleasesBackgroundAndThemeSource), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources["Surface.Floating"] = new SolidColorBrush(Color.FromArgb(128, 30, 40, 50));
            WeakReference[] references = CreateAndCloseWindow();

            for (int attempt = 0; attempt < 5 && references.Any(reference => reference.IsAlive); attempt++)
                Collect();

            Assert.All(references, reference => Assert.False(reference.IsAlive,
                "Closing the last owner must allow the window, background and private theme source to be collected."));
            // The application/resource dictionary remains alive; its follower registry must not own them.
            ThemeBrushes.Refresh(application.Resources);
            GC.KeepAlive(application);
        }, typeof(OpaqueBackgroundLifetimeTests));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BrushSample CreateBackground()
    {
        var brush = Assert.IsType<SolidColorBrush>(CaptureWindowExclusion.OpaqueBackground());
        Binding binding = Assert.IsType<Binding>(BindingOperations.GetBinding(brush, SolidColorBrush.ColorProperty));
        var source = Assert.IsType<SolidColorBrush>(binding.Source);
        return new BrushSample(brush, new WeakReference(source));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateAndCloseWindow()
    {
        BrushSample sample = CreateBackground();
        var window = new Window
        {
            Background = sample.Brush,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 16,
            Height = 16,
        };
        try
        {
            window.Show();
            Drain();
        }
        finally
        {
            window.Close();
        }
        return [new WeakReference(window), new WeakReference(sample.Brush), sample.Source];
    }

    private static BindingExpression Expression(SolidColorBrush brush) =>
        Assert.IsType<BindingExpression>(BindingOperations.GetBindingExpression(brush, SolidColorBrush.ColorProperty));

    private static void AssertOpaque(SolidColorBrush brush, Color expected)
    {
        Assert.Equal(expected, brush.Color);
        Assert.Equal(255, brush.Color.A);
        Assert.Equal(1, brush.Opacity);
    }

    private static void Collect()
    {
        Drain();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Drain();
    }

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private sealed record BrushSample(SolidColorBrush Brush, WeakReference Source);
}
