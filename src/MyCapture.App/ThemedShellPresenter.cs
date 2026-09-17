using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>One bounded, nonactivating app notification and a themed tray command menu.</summary>
internal sealed class ThemedShellPresenter(Func<bool> suppressNotification) : IDisposable
{
    private Window? _toast;
    private Window? _help;

    /// <summary>Raised whenever a tray-owned surface (command menu) has just left the screen,
    /// so a capture starting immediately afterwards can wait out the dismissal.</summary>
    internal event Action? ForegroundDismissed;
    private ContextMenu? _menu;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>A command or a submenu branch in the tray tree. A null Action marks a branch.</summary>
    internal sealed record MenuEntry(string Label, Action? Action, IReadOnlyList<MenuEntry>? Children = null);

    internal void ShowNotification(string title, string message, TrayBalloonKind kind)
    {
        Dismiss();
        if (suppressNotification()) return;
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var close = new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Dismiss();
        content.Children.Add(close);
        var window = CreateWindow(content, title);
        window.Width = 360;
        window.ShowActivated = false;
        window.Topmost = true;
        window.WindowStyle = WindowStyle.None;
        window.SizeToContent = SizeToContent.Height;
        window.SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(window);
        window.Loaded += (_, _) =>
        {
            Rect work = SystemParameters.WorkArea;
            window.Left = Math.Max(work.Left, work.Right - window.ActualWidth - 16);
            window.Top = Math.Max(work.Top, work.Bottom - window.ActualHeight - 16);
        };
        _toast = window;
        _timer.Tick -= OnTimeout;
        _timer.Tick += OnTimeout;
        window.Show();
        _timer.Start();
    }

    private void OnTimeout(object? sender, EventArgs e) => Dismiss();

    internal void ShowMenu(IEnumerable<MenuEntry> commands, string language, Action<string> chooseLanguage)
    {
        Dismiss();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 240 };
        AppendEntries(menu.Items, commands, menu);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Language", IsEnabled = false, MinHeight = 36 });
        foreach ((string label, string code) in new[] { ("한국어", "ko"), ("English", "en") })
        {
            bool selected = language.StartsWith(code, StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(language) && UiText.Culture.TwoLetterISOLanguageName == code);
            // The shared MenuItem template has no submenu/check presenter. Keep these
            // directly reachable and show selection in the header using that existing style.
            var item = new MenuItem { Header = selected ? "✓ " + label : "   " + label, MinHeight = 36 };
            item.Click += (_, _) => { menu.IsOpen = false; chooseLanguage(code); };
            menu.Items.Add(item);
        }
        var help = new MenuItem { Header = UiText.Get("Shell.Help"), MinHeight = 36 };
        help.Click += (_, _) => { menu.IsOpen = false; ShowHelp(); };
        menu.Items.Add(help);
        _menu = menu;
        menu.IsOpen = true;
    }

    private void AppendEntries(ItemCollection items, IEnumerable<MenuEntry> entries, ContextMenu root)
    {
        foreach (MenuEntry entry in entries)
        {
            if (entry.Action is null && entry.Children is { Count: > 0 } children)
            {
                var branch = new MenuItem { Header = entry.Label, MinHeight = 36 };
                AppendEntries(branch.Items, children, root);
                items.Add(branch);
                continue;
            }

            var item = new MenuItem { Header = entry.Label, MinHeight = 36 };
            Action action = entry.Action ?? (() => { });
            item.Click += (_, _) =>
            {
                root.IsOpen = false;
                // The menu is visually dismissing while the command runs; a capture that
                // starts from here must let the dismissal leave the screen before BitBlt.
                ForegroundDismissed?.Invoke();
                action();
            };
            items.Add(item);
        }
    }

    private void ShowHelp()
    {
        if (_help is not null) { _help.Activate(); return; }
        var text = new TextBlock { Text = UiText.Get("Shell.HelpText"), TextWrapping = TextWrapping.Wrap, LineHeight = 25 };
        var window = CreateWindow(text, UiText.Get("Shell.Help"));
        StandardWindowTheme.Apply(window);
        window.Width = 560;
        window.Height = 360;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Closed += (_, _) => _help = null;
        _help = window;
        window.Show();
    }

    private static Window CreateWindow(UIElement child, string title)
    {
        var window = new Window { Title = title, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            Content = new Border { Padding = new Thickness(20), Child = child } };
        window.SetResourceReference(Window.BackgroundProperty, "Surface.Base");
        window.SetResourceReference(Window.ForegroundProperty, "Text.Primary");
        return window;
    }

    internal void Dismiss()
    {
        _timer.Stop();
        _toast?.Close();
        _toast = null;
        if (_menu is not null) _menu.IsOpen = false;
        _menu = null;
    }

    internal void DismissForCapture() { Dismiss(); _help?.Close(); }
    public void Dispose() { DismissForCapture(); _timer.Tick -= OnTimeout; }
}
