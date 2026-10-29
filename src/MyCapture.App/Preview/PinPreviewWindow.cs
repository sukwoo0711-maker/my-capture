using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.Core.Localization;

namespace MyCapture.App.Preview;

/// <summary>A small control surface for pins created from other applications.</summary>
internal sealed class PinPreviewWindow : Window
{
    private readonly PreviewPinInput _input;
    private readonly TextBlock _shortcut;
    private readonly TextBlock _notice;
    private readonly TextBlock _status;
    private readonly Button _pin;
    private readonly Button _retry;

    internal PinPreviewWindow(PreviewPinInput input)
    {
        _input = input;
        Title = "MyCapture 미리보기";
        Width = 470;
        MinWidth = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");

        var layout = new StackPanel { Margin = new Thickness(24) };
        var instruction = Text("다른 앱에서 복사한 텍스트나 이미지를 핀으로 띄웁니다.");
        layout.Children.Add(instruction);
        _shortcut = Text(string.Empty);
        _shortcut.FontSize = 22;
        _shortcut.FontWeight = FontWeights.SemiBold;
        _shortcut.Margin = new Thickness(0, 16, 0, 12);
        AutomationProperties.SetAutomationId(_shortcut, "PreviewActiveShortcut");
        layout.Children.Add(_shortcut);
        _notice = Text(string.Empty);
        _notice.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetAutomationId(_notice, "PreviewShortcutNotice");
        layout.Children.Add(_notice);
        _status = Text(string.Empty);
        AutomationProperties.SetAutomationId(_status, "PreviewStatus");
        layout.Children.Add(_status);
        // New pins use the display language chosen here: it is the translation target.
        var language = new ComboBox { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        AutomationProperties.SetName(language, "표시 언어");
        foreach ((string label, string? tag) in new[] { ("표시 언어 · Windows 설정 따르기", (string?)null), ("표시 언어 · 한국어", "ko"), ("표시 언어 · English", "en") })
            language.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        language.SelectedIndex = UiText.Culture.TwoLetterISOLanguageName == "ko" ? 1 : 2;
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem item) UiText.Configure(item.Tag as string);
        };
        layout.Children.Add(language);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        _pin = Button("클립보드 핀", "Button.Primary");
        _pin.Click += async (_, _) => await _input.PinClipboardAsync();
        actions.Children.Add(_pin);
        _retry = Button("단축키 다시 연결", "Button.Secondary");
        _retry.Click += (_, _) => _input.RegisterShortcut();
        actions.Children.Add(_retry);
        var close = Button("종료", "Button.Secondary");
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        layout.Children.Add(actions);
        Content = layout;
        _input.Changed += Refresh;
        Closed += (_, _) => _input.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        _shortcut.Text = "활성 단축키 · " + _input.ShortcutText;
        _notice.Text = _input.ShortcutNotice;
        _notice.Visibility = _notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _retry.Visibility = _notice.Visibility;
        _status.Text = _input.StatusText;
        _pin.IsEnabled = !_input.IsBusy;
    }

    private static TextBlock Text(string value)
    {
        var text = new TextBlock { Text = value, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        return text;
    }

    private static Button Button(string label, string style)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(StyleProperty, style);
        return button;
    }
}
