using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

/// <summary>Shared controls for file copies; batch exports only expose URL and size.</summary>
internal sealed class ImageExportOptionsPanel : StackPanel
{
    private readonly ComboBox _format = new();
    private readonly ComboBox _edge = new();
    private readonly ComboBox _background = new();
    private readonly CheckBox _url = new() { Content = UiText.Get("ExportOptions.SourceUrl") };
    private readonly CheckBox _shadow = new() { Content = UiText.Get("ExportOptions.Shadow") };
    private readonly Slider _margin = new() { Minimum = 0, Maximum = 128, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Slider _radius = new() { Minimum = 0, Maximum = 48, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _marginValue = new();
    private readonly TextBlock _radiusValue = new();
    private bool _loading;
    private static readonly int[] Edges = [0, 2560, 1920, 1280, 800];
    internal event Action? Changed;
    internal event Action<ImageExportOptions>? PresetSelected;

    internal ImageExportOptionsPanel(ImageExportOptions initial, bool full)
    {
        _marginValue.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _radiusValue.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        if (full)
        {
            var presets = new ComboBox();
            foreach (string label in new[] { UiText.Get("ExportOptions.Preset.Last"), UiText.Get("ExportOptions.Preset.Original"),
                UiText.Get("ExportOptions.Preset.Messenger"), UiText.Get("ExportOptions.Preset.Document") })
                presets.Items.Add(label);
            AddControl("ExportOptions.Preset", presets);
            presets.SelectedIndex = 0;
            presets.SelectionChanged += (_, _) =>
            {
                ImageExportOptions selected = presets.SelectedIndex == 0 ? initial
                    : ImageExportOptions.FromPreset((ImageExportPreset)(presets.SelectedIndex - 1));
                Apply(selected);
                PresetSelected?.Invoke(selected);
            };
            foreach (string label in new[] { UiText.Get("ExportOptions.Format.Automatic"), UiText.Get("ExportOptions.Format.Png"),
                UiText.Get("ExportOptions.Format.Jpeg") }) _format.Items.Add(label);
            AddControl("ExportOptions.Format", _format);
        }
        foreach (int edge in Edges) _edge.Items.Add(edge == 0 ? UiText.Get("ExportOptions.OriginalSize") : UiText.Format("ExportOptions.Pixels", edge));
        AddControl("ExportOptions.LongEdge", _edge);
        _url.Margin = new Thickness(0, 10, 0, 8);
        Children.Add(_url);
        if (full)
        {
            var styling = new StackPanel();
            var expander = new Expander { Header = UiText.Get("ExportOptions.Style"), Content = styling, Margin = new Thickness(0, 8, 0, 8) };
            expander.SetResourceReference(StyleProperty, "Expander.Options");
            Children.Add(expander);
            foreach (string label in new[] { UiText.Get("ExportOptions.Background.Transparent"), UiText.Get("ExportOptions.Background.White"),
                UiText.Get("ExportOptions.Background.LightGray"), UiText.Get("ExportOptions.Background.DarkGray"),
                UiText.Get("ExportOptions.Background.Accent") })
                _background.Items.Add(label);
            AddControl("ExportOptions.Background", _background, styling);
            AddControl("ExportOptions.Margin", _margin, styling); styling.Children.Add(_marginValue);
            AddControl("ExportOptions.Radius", _radius, styling); styling.Children.Add(_radiusValue);
            styling.Children.Add(_shadow);
        }
        Apply(initial);
        _format.SelectionChanged += (_, _) => Notify();
        _edge.SelectionChanged += (_, _) => Notify();
        _background.SelectionChanged += (_, _) => Notify();
        _margin.ValueChanged += (_, _) => Notify();
        _radius.ValueChanged += (_, _) => Notify();
        _url.Checked += (_, _) => Notify(); _url.Unchecked += (_, _) => Notify();
        _shadow.Checked += (_, _) => Notify(); _shadow.Unchecked += (_, _) => Notify();
    }

    internal ImageExportOptions Read(int reduction = 0) => new ImageExportOptions
    {
        Format = (ImageExportFormat)Math.Max(0, _format.SelectedIndex),
        LongEdge = Edges[Math.Max(0, _edge.SelectedIndex)], IncludeSourceUrl = _url.IsChecked == true,
        Margin = (int)_margin.Value, CornerRadius = (int)_radius.Value,
        Background = (ImageExportBackground)Math.Max(0, _background.SelectedIndex), Shadow = _shadow.IsChecked == true,
        ReductionPercent = reduction,
    }.Normalize();

    internal void Apply(ImageExportOptions options)
    {
        options = options.Normalize(); _loading = true;
        _format.SelectedIndex = (int)options.Format; _edge.SelectedIndex = Array.IndexOf(Edges, options.LongEdge);
        _url.IsChecked = options.IncludeSourceUrl; _margin.Value = options.Margin; _radius.Value = options.CornerRadius;
        _background.SelectedIndex = (int)options.Background; _shadow.IsChecked = options.Shadow;
        _loading = false; UpdateValues(); Changed?.Invoke();
    }

    private void Notify() { UpdateValues(); if (!_loading) Changed?.Invoke(); }
    private void UpdateValues()
    {
        _marginValue.Text = UiText.Format("ExportOptions.Pixels", (int)_margin.Value);
        _radiusValue.Text = UiText.Format("ExportOptions.Pixels", (int)_radius.Value);
    }
    private void AddControl(string key, FrameworkElement control, Panel? parent = null)
    {
        parent ??= this;
        var label = new Label { Content = UiText.Get(key), Target = control, Padding = new Thickness(0, 6, 0, 3) };
        label.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        parent.Children.Add(label);
        AutomationProperties.SetName(control, UiText.Get(key));
        parent.Children.Add(control);
    }

    internal static ImageExportOptions? ShowCopyOptions(Window? owner, ImageExportOptions initial)
    {
        var panel = new ImageExportOptionsPanel(initial, full: false) { Margin = new Thickness(20) };
        Window dialog = CreateCopyOptionsDialog(owner, panel);
        return dialog.ShowDialog() == true ? initial with { IncludeSourceUrl = panel.Read().IncludeSourceUrl, LongEdge = panel.Read().LongEdge } : null;
    }

    internal static Window CreateCopyOptionsDialog(Window? owner, ImageExportOptionsPanel panel)
    {
        var dialog = new Window { Title = UiText.Get("ExportOptions.Title"), Width = 360, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        StandardWindowTheme.Apply(dialog);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        Button accept = ThemedDialog.CreateActionButton(UiText.Get("ExportOptions.Continue"), primary: true);
        Button cancel = ThemedDialog.CreateActionButton(UiText.Get("Dialog.Cancel"), primary: false);
        accept.IsDefault = true;
        cancel.IsCancel = true;
        accept.Click += (_, _) => dialog.DialogResult = true;
        WrapPanel actions = ThemedDialog.CreateActionRow(accept, cancel);
        actions.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(actions);
        return dialog;
    }
}
