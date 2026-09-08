using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>Keyboard-accessible editor for one source-time text overlay.</summary>
internal sealed class TimedTextOverlayDialog : Window
{
    private readonly double _durationMs;
    private readonly Guid _id;
    private readonly TextBox _text;
    private readonly TextBox _start;
    private readonly TextBox _end;
    private readonly ComboBox _placement;
    private readonly TextBlock _error;

    internal TimedTextOverlayDialog(
        double durationMs,
        double playheadMs,
        TimedTextOverlay? existing = null)
    {
        _durationMs = Math.Max(1, durationMs);
        _id = existing?.Id ?? Guid.NewGuid();

        StandardWindowTheme.Apply(this);
        Title = existing is null ? UiText.Get("Text_6235669450B0") : UiText.Get("Text_16803FE760A3");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = ResourceBrush("Surface.Base", Color.FromRgb(0x0B, 0x0F, 0x17));
        Foreground = ResourceBrush("Text.Primary", Colors.White);
        FontFamily = Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");

        double startMs = existing?.StartMs ?? Math.Clamp(playheadMs, 0, _durationMs - 1);
        double endMs = existing?.EndMs ?? Math.Min(_durationMs, startMs + 3000);
        if (endMs <= startMs)
        {
            startMs = 0;
            endMs = _durationMs;
        }

        _text = new TextBox
        {
            Text = existing?.Text ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            MaxLength = VideoEditDocument.MaximumTextLength,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(_text, UiText.Get("Text_06B3E66B7B78"));

        _start = TimeBox(startMs);
        AutomationProperties.SetName(_start, UiText.Get("Text_FE872AF40869"));
        _end = TimeBox(endMs);
        AutomationProperties.SetName(_end, UiText.Get("Text_1882C23FDDB1"));

        var placements = new[]
        {
            new PlacementChoice(UiText.Get("Text_8F2EA9820639"), VideoTextPlacement.Bottom),
            new PlacementChoice(UiText.Get("Text_D41AD4FCB417"), VideoTextPlacement.Center),
            new PlacementChoice(UiText.Get("Text_E0BEBB354D8F"), VideoTextPlacement.Top),
        };
        _placement = new ComboBox
        {
            ItemsSource = placements,
            DisplayMemberPath = nameof(PlacementChoice.Label),
            SelectedItem = placements.First(choice => choice.Value == (existing?.Placement ?? VideoTextPlacement.Bottom)),
            MinWidth = 140,
        };
        AutomationProperties.SetName(_placement, UiText.Get("Text_08269F2F1B3F"));

        _error = new TextBlock
        {
            Foreground = ResourceBrush("State.Danger", Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 20,
        };
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);

        Content = BuildLayout();
        Loaded += (_, _) =>
        {
            _ = _text.Focus();
            _text.SelectAll();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    internal TimedTextOverlay? Result { get; private set; }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        for (int index = 0; index < 7; index++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        root.Children.Add(Label(UiText.Get("Text_06B3E66B7B78"), 0));
        Grid.SetRow(_text, 1);
        _text.Margin = new Thickness(0, 6, 0, 14);
        root.Children.Add(_text);

        // Labels sit above their controls so longer translated labels never push fields outside the dialog.
        var timing = new Grid();
        for (int column = 0; column < 3; column++)
            timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(timing, new TextBlock { Text = UiText.Get("Text_25A15C7C4EFF"), TextWrapping = TextWrapping.Wrap }, 0);
        Add(timing, new TextBlock { Text = UiText.Get("Text_A6434B74B299"), TextWrapping = TextWrapping.Wrap }, 1);
        Add(timing, new TextBlock { Text = UiText.Get("Text_6C0B9DD710AB"), TextWrapping = TextWrapping.Wrap }, 2);
        Control[] fields = [_start, _end, _placement];
        for (int column = 0; column < fields.Length; column++)
        {
            fields[column].Margin = new Thickness(0, 6, column == 2 ? 0 : 12, 0);
            Grid.SetRow(fields[column], 1);
            Add(timing, fields[column], column);
        }
        Grid.SetRow(timing, 2);
        root.Children.Add(timing);

        var hint = new TextBlock
        {
            Text = UiText.Get("Text_EEA09781BBD7"),
            Foreground = ResourceBrush("Text.Secondary", Colors.LightGray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(hint, 3);
        root.Children.Add(hint);

        Grid.SetRow(_error, 4);
        _error.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = UiText.Get("Text_BE876433993A"), MinWidth = 88, IsCancel = true };
        var save = new Button { Content = UiText.Get("Text_D9F974C95F68"), MinWidth = 112, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);
        return root;
    }

    private void Save()
    {
        string text = _text.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            Fail(UiText.Get("Text_7A3486B4FEBF"), _text);
            return;
        }

        if (!TrySeconds(_start.Text, out double startSeconds)
            || !TrySeconds(_end.Text, out double endSeconds))
        {
            Fail(UiText.Get("Text_328D27BC4C06"), _start);
            return;
        }

        double startMs = startSeconds * 1000;
        double endMs = endSeconds * 1000;
        if (startMs < 0 || endMs > _durationMs + 0.5 || endMs <= startMs)
        {
            Fail(
                UiText.Format("Text_41FC07B1EA9B", (_durationMs / 1000)),
                _start);
            return;
        }

        Result = new TimedTextOverlay
        {
            Id = _id,
            Text = text,
            StartMs = startMs,
            EndMs = endMs,
            Placement = (_placement.SelectedItem as PlacementChoice)?.Value ?? VideoTextPlacement.Bottom,
        };
        DialogResult = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            Save();
        }
    }

    private void Fail(string message, Control focus)
    {
        _error.Text = message;
        _ = focus.Focus();
    }

    private static bool TrySeconds(string value, out double seconds) =>
        (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds)
         || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        && double.IsFinite(seconds);

    private static TextBox TimeBox(double milliseconds) => new()
    {
        Text = (milliseconds / 1000).ToString("0.###", CultureInfo.CurrentCulture),
        Margin = new Thickness(8, 0, 0, 0),
    };

    private static TextBlock Label(string text, int row)
    {
        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold };
        Grid.SetRow(label, row);
        return label;
    }

    private static void Add(Grid grid, UIElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static Brush ResourceBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private sealed record PlacementChoice(string Label, VideoTextPlacement Value);
}
