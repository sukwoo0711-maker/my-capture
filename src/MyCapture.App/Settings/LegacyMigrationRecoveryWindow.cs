using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Themes;

namespace MyCapture.App.Settings;

/// <summary>Shows only when startup recovery outlasts the short no-window grace period.</summary>
internal sealed class LegacyMigrationRecoveryWindow : Window
{
    private bool _finished;
    private readonly Task _recovery;
    internal Exception? Failure { get; private set; }
    internal LegacyMigrationRecoveryWindow(Task recovery)
    {
        _recovery = recovery;
        Title = UiText.Get("LegacyImport.Recovering");
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = Title, TextWrapping = TextWrapping.Wrap, FontSize = 18 },
                new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 20, 0, 0) },
            },
        };
        Loaded += async (_, _) =>
        {
            try { await _recovery; }
            catch (Exception error) { Failure = error; }
            finally { _finished = true; Close(); }
        };
    }
    protected override void OnClosing(CancelEventArgs e)
    { if (!_finished) e.Cancel = true; base.OnClosing(e); }

    internal static async Task AwaitAsync(Task recovery, Func<Task> delay, Action<Task> show)
    {
        if (recovery.IsCompleted) { await recovery; return; }
        if (await Task.WhenAny(recovery, delay()) != recovery && !recovery.IsCompleted) show(recovery);
        await recovery;
    }
}
