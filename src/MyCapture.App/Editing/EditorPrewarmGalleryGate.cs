using System.Windows;

namespace MyCapture.App.Editing;

/// <summary>Waits for the reusable gallery's actual visibility without a polling timer.</summary>
internal sealed class EditorPrewarmGalleryGate(
    Action<bool> setVisible,
    Action stopDelayedRetry,
    Action requestWhenHidden)
{
    private Window? _window;
    private bool _visible;

    internal bool IsVisible => _visible;

    internal void Observe(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!ReferenceEquals(_window, window))
        {
            Detach();
            _window = window;
            window.IsVisibleChanged += OnVisibilityChanged;
            window.Closed += OnClosed;
        }
        Publish(window.IsVisible);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _window)) Publish(_window!.IsVisible);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _window)) return;
        Detach();
        Publish(false);
    }

    private void Publish(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        setVisible(visible);
        if (visible) stopDelayedRetry();
        else requestWhenHidden();
    }

    private void Detach()
    {
        if (_window is not { } window) return;
        window.IsVisibleChanged -= OnVisibilityChanged;
        window.Closed -= OnClosed;
        _window = null;
    }
}
