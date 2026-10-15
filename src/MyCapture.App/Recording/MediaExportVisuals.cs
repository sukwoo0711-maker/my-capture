using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal static class MediaExportVisuals
{
    internal static Button Button(FrameworkElement scope, string caption, string? icon = null, bool primary = false)
    {
        var button = new Button();
        CommandButtonPresentation.Apply(button);
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "Button.Primary" : "Button.Secondary");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            var path = new SymbolIcon { Size = CommandButtonPresentation.IconSize, StrokeThickness = 0,
                Margin = new Thickness(0, 0, CommandButtonPresentation.IconLabelGap, 0), VerticalAlignment = VerticalAlignment.Center };
            path.SetResourceReference(SymbolIcon.DataProperty, icon);
            path.SetBinding(SymbolIcon.FillProperty, new Binding(nameof(Control.Foreground)) { Source = button });
            panel.Children.Add(path);
        }
        panel.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        button.Content = panel;
        AutomationProperties.SetName(button, caption);
        button.ToolTip = caption;
        return button;
    }

    /// <summary>Sizes a dialog's closing action like every other dialog: 88 wide at least, 8 apart.</summary>
    internal static Button AsDialogAction(this Button button)
    {
        button.MinWidth = DialogActionMinWidth;
        button.Margin = new Thickness(8, 0, 0, 0);
        return button;
    }

    internal const double DialogActionMinWidth = 88;
}

/// <summary>The shared export/library-save progress row, including an early-estimate warmup.</summary>
internal sealed class MediaExportProgressRow : Grid
{
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _percent = new() { Margin = new Thickness(12, 0, 0, 0), MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _remaining = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    internal Button CancelButton { get; }
    internal string RemainingText => _remaining.Text;
    internal string PercentText => _percent.Text;
    internal double Value => _bar.Value;

    internal MediaExportProgressRow()
    {
        Margin = new Thickness(0, 12, 0, 0);
        Visibility = Visibility.Collapsed;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bar.SetResourceReference(Control.ForegroundProperty, "Accent.Default");
        _bar.SetResourceReference(Control.BackgroundProperty, "Surface.Sunken");
        _percent.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _remaining.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        CancelButton = MediaExportVisuals.Button(this, UiText.Get("MediaExport_Cancel"));
        CancelButton.Margin = new Thickness(12, 0, 0, 0);
        Children.Add(_bar);
        SetColumn(_percent, 1);
        Children.Add(_percent);
        SetColumn(_remaining, 2);
        Children.Add(_remaining);
        SetColumn(CancelButton, 3);
        Children.Add(CancelButton);
    }

    internal void Start(string operation, bool indeterminate = false)
    {
        AutomationProperties.SetName(_bar, operation);
        _bar.Value = 0;
        _bar.IsIndeterminate = indeterminate;
        _percent.Text = indeterminate ? string.Empty : UiText.Format("MediaExport_Percent", 0);
        _remaining.Text = string.Empty;
        CancelButton.IsEnabled = true;
        Visibility = Visibility.Visible;
    }

    internal void Report(VideoFrameRenderProgress progress, TimeSpan elapsed)
    {
        int total = Math.Max(1, progress.TotalFrames);
        int completed = Math.Clamp(progress.CompletedFrames, 0, total);
        double fraction = (double)completed / total;
        _bar.IsIndeterminate = progress.TotalFrames <= 0;
        _bar.Value = fraction * 100;
        _percent.Text = UiText.Format("MediaExport_Percent", (int)(fraction * 100));
        _remaining.Text = completed >= 5 && completed < total && elapsed.TotalSeconds >= 0.4
            ? UiText.Format("MediaExport_Remaining", Math.Max(1, Math.Ceiling(elapsed.TotalSeconds / completed * (total - completed))))
            : string.Empty;
    }

    internal void FinalizeProgress()
    {
        _bar.Value = 100;
        _percent.Text = UiText.Format("MediaExport_Percent", 100);
        _bar.IsIndeterminate = true;
        _remaining.Text = string.Empty;
    }

    internal void Stop() => Visibility = Visibility.Collapsed;
}
