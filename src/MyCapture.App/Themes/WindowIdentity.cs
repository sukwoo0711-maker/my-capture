using System.ComponentModel;
using System.Windows;
using MyCapture.Core.Platform;

namespace MyCapture.App.Themes;

/// <summary>Decorates native captions without replacing contextual names or title bindings.</summary>
public static class WindowIdentity
{
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(WindowIdentity), new PropertyMetadata(false));
    private static readonly DependencyPropertyDescriptor TitleDescriptor =
        DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window));

    public static void Attach(Window window)
    {
        if ((bool)window.GetValue(AttachedProperty)) return;
        window.SetValue(AttachedProperty, true);
        TitleDescriptor.AddValueChanged(window, OnTitleChanged);
        window.Closed += OnClosed;
        OnTitleChanged(window, EventArgs.Empty);
    }

    private static void OnTitleChanged(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        string title = AppIdentity.FormatWindowTitle(window.Title);
        if (title != window.Title) window.SetCurrentValue(Window.TitleProperty, title);
    }

    private static void OnClosed(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        TitleDescriptor.RemoveValueChanged(window, OnTitleChanged);
        window.Closed -= OnClosed;
    }
}
