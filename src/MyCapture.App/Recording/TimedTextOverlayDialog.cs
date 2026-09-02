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
        Title = existing is null ? "시간 텍스트 추가" : "시간 텍스트 편집";
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
        AutomationProperties.SetName(_text, "영상에 남길 텍스트");

        _start = TimeBox(startMs);
        AutomationProperties.SetName(_start, "텍스트 시작 시간 초");
        _end = TimeBox(endMs);
        AutomationProperties.SetName(_end, "텍스트 종료 시간 초");

        var placements = new[]
        {
            new PlacementChoice("아래", VideoTextPlacement.Bottom),
            new PlacementChoice("가운데", VideoTextPlacement.Center),
            new PlacementChoice("위", VideoTextPlacement.Top),
        };
        _placement = new ComboBox
        {
            ItemsSource = placements,
            DisplayMemberPath = nameof(PlacementChoice.Label),
            SelectedItem = placements.First(choice => choice.Value == (existing?.Placement ?? VideoTextPlacement.Bottom)),
            MinWidth = 140,
        };
        AutomationProperties.SetName(_placement, "텍스트 위치");

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

        root.Children.Add(Label("영상에 남길 텍스트", 0));
        Grid.SetRow(_text, 1);
        _text.Margin = new Thickness(0, 6, 0, 14);
        root.Children.Add(_text);

        var timing = new Grid();
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Add(timing, new TextBlock { Text = "시작(초)", VerticalAlignment = VerticalAlignment.Center }, 0);
        Add(timing, _start, 1);
        Add(timing, new TextBlock { Text = "끝(초)", VerticalAlignment = VerticalAlignment.Center }, 3);
        Add(timing, _end, 4);
        Add(timing, new TextBlock { Text = "위치", VerticalAlignment = VerticalAlignment.Center }, 6);
        Add(timing, _placement, 7);
        Grid.SetRow(timing, 2);
        root.Children.Add(timing);

        var hint = new TextBlock
        {
            Text = "텍스트는 지정한 구간에만 보이며 MP4와 GIF에 영구 합성됩니다. Ctrl+Enter로 저장할 수 있습니다.",
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
        var cancel = new Button { Content = "취소", MinWidth = 88, IsCancel = true };
        var save = new Button { Content = "텍스트 저장", MinWidth = 112, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
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
            Fail("텍스트를 입력해 주세요.", _text);
            return;
        }

        if (!TrySeconds(_start.Text, out double startSeconds)
            || !TrySeconds(_end.Text, out double endSeconds))
        {
            Fail("시작과 끝 시간을 초 단위 숫자로 입력해 주세요.", _start);
            return;
        }

        double startMs = startSeconds * 1000;
        double endMs = endSeconds * 1000;
        if (startMs < 0 || endMs > _durationMs + 0.5 || endMs <= startMs)
        {
            Fail(
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"0초부터 {(_durationMs / 1000):0.###}초 사이에서 끝이 시작보다 늦어야 합니다."),
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
