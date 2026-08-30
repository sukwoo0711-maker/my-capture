using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

/// <summary>
/// A reusable, accessible dialog that shows an OCR result: selectable text, a copy button,
/// a rerun option, and a status line carrying the language and timing (or empty/error guidance).
/// </summary>
/// <remarks>
/// <para>
/// One instance is reused across the whole app (gallery and pins) by
/// <see cref="OcrResultPresenter"/>: <see cref="ShowResult"/> repopulates and re-shows it rather
/// than creating a new window each time. A normal close hides it back so reopening is instant and
/// any in-flight rerun keeps its handler wiring.
/// </para>
/// <para>
/// The text area is a read-only multiline <see cref="TextBox"/> so the recognised text is fully
/// selectable and copyable with the keyboard (Ctrl+A/Ctrl+C) as well as the copy button. The
/// window carries automation names on every control and closes on Escape.
/// </para>
/// </remarks>
internal sealed class OcrResultWindow : Window
{
    private readonly TextBox _textBox;
    private readonly LiveTextBlock _status;
    private readonly Button _copyButton;
    private readonly Button _rerunButton;

    private bool _allowClose;

    internal OcrResultWindow()
    {
        Title = "MyCapture — 텍스트 인식";
        Width = 680;
        Height = 560;
        MinWidth = 440;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        AutomationProperties.SetName(this, "텍스트 인식 결과 창");

        // Uniform dark root: one calm surface frames the whole window so the recognised text is
        // the only thing that stands out. The window Style already sets this, but making it
        // explicit keeps the surface consistent even if the default style is ever overridden.
        Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x1B, 0x17, 0x12)));

        var root = new Grid
        {
            Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x1B, 0x17, 0x12))),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Sentence-case Korean heading only — no decorative English eyebrow. A quiet status line
        // sits directly beneath it as compact secondary text.
        var heading = new StackPanel { Margin = new Thickness(24, 22, 24, 16) };
        heading.Children.Add(new TextBlock
        {
            Text = "텍스트 인식",
            Foreground = ResourceBrush("Text.Primary", Brushes.White),
            FontFamily = Application.Current?.TryFindResource("Font.Display") as FontFamily ?? new FontFamily("Segoe UI"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        });

        _status = new LiveTextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("Text.Secondary", Brushes.LightGray),
            FontSize = 12,
        };
        AutomationProperties.SetName(_status, "인식 상태");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        heading.Children.Add(_status);

        // The heading rides on the same uniform surface — no raised bar, no divider — so nothing
        // competes with the recognised text for attention.
        var header = new Border
        {
            Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x1B, 0x17, 0x12))),
            Child = heading,
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _textBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = Application.Current?.TryFindResource("Font.Mono") as FontFamily
                ?? new FontFamily("Consolas, Malgun Gothic"),
            FontSize = 14,
            Padding = new Thickness(16),
            Background = ResourceBrush("Surface.Sunken", new SolidColorBrush(Color.FromRgb(0x1B, 0x17, 0x12))),
            BorderBrush = ResourceBrush("Border.Subtle", Brushes.DimGray),
        };
        AutomationProperties.SetName(_textBox, "인식된 텍스트");
        AutomationProperties.SetHelpText(
            _textBox, "인식된 텍스트입니다. Ctrl+A로 전체 선택, Ctrl+C로 복사할 수 있습니다.");

        var textFrame = new Border
        {
            Margin = new Thickness(24, 2, 24, 4),
            Padding = new Thickness(1),
            Background = ResourceBrush("Surface.Sunken", new SolidColorBrush(Color.FromRgb(0x1B, 0x17, 0x12))),
            BorderBrush = ResourceBrush("Border.Subtle", Brushes.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = _textBox,
        };
        Grid.SetRow(textFrame, 1);
        root.Children.Add(textFrame);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        // Rerun stays a quiet secondary: a ghost button that recedes so it never rivals Copy.
        _rerunButton = MakeButton("다시 인식", "OCR을 다시 실행합니다");
        _rerunButton.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
        _rerunButton.Margin = new Thickness(0, 0, 8, 0);
        _rerunButton.Click += (_, _) => RerunRequested?.Invoke(this, EventArgs.Empty);
        buttons.Children.Add(_rerunButton);

        // Copy is the sole primary action — the one thing this window exists to make easy.
        _copyButton = MakeButton("복사", "인식된 텍스트를 클립보드에 복사합니다");
        _copyButton.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        _copyButton.Click += (_, _) => CopyText();
        buttons.Children.Add(_copyButton);

        // The footer shares the uniform surface — no raised bar or divider — keeping the window
        // text-first with the actions quietly anchored bottom-right.
        var footer = new Border
        {
            Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x1B, 0x17, 0x12))),
            Padding = new Thickness(24, 14, 24, 18),
            Child = buttons,
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;

        _ = InputBindings.Add(new KeyBinding(
            new RelayUiCommand(CopyText), new KeyGesture(Key.C, ModifierKeys.Control)));
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Raised when the user asks to run recognition again.</summary>
    internal event EventHandler? RerunRequested;

    /// <summary>Populates the window from a result and shows/activates it.</summary>
    internal void ShowResult(OcrResult result, string contextLabel)
    {
        ArgumentNullException.ThrowIfNull(result);

        ApplyResult(result, contextLabel);

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        _ = _textBox.Focus();
        _textBox.SelectAll();
    }

    /// <summary>Shows a busy state while a (re)run is in flight, keeping the window responsive.</summary>
    internal void ShowBusy(string contextLabel)
    {
        _status.Text = $"{contextLabel} · 인식 중…";
        _rerunButton.IsEnabled = false;
        _copyButton.IsEnabled = false;

        if (!IsVisible)
        {
            Show();
        }

        _ = Activate();
    }

    /// <summary>Closes the window for real, used only on an explicit application exit.</summary>
    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void ApplyResult(OcrResult result, string contextLabel)
    {
        _rerunButton.IsEnabled = true;

        switch (result.Status)
        {
            case OcrStatus.Success:
                _textBox.Text = result.Text;
                _copyButton.IsEnabled = true;
                _status.Text =
                    $"{contextLabel} · 언어 {result.LanguageTag} · {result.Lines.Count}줄 · {result.Elapsed.TotalMilliseconds:0}ms";
                break;

            case OcrStatus.NoText:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text =
                    $"{contextLabel} · 인식된 텍스트가 없습니다. 이미지에 글자가 없거나 너무 작을 수 있습니다.";
                break;

            case OcrStatus.Unavailable:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text =
                    result.Message ??
                    "이 시스템에서 OCR을 사용할 수 없습니다. 언어 팩 설치가 필요할 수 있습니다.";
                break;

            case OcrStatus.Cancelled:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text = $"{contextLabel} · 인식이 취소되었습니다.";
                break;

            default:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text = result.Message ?? "텍스트 인식에 실패했습니다.";
                break;
        }
    }

    private void CopyText()
    {
        if (string.IsNullOrEmpty(_textBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_textBox.Text);
            _status.Text = "복사됨 · " + _status.Text;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard momentarily locked by another app; a copy failure must not throw.
            _status.Text = "클립보드에 복사하지 못했습니다. 잠시 후 다시 시도해 주세요.";
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A normal close hides the reusable window; only an explicit exit tears it down.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// TextBlock that forces a UI Automation live-region event for direct Text updates.
    /// WPF exposes AutomationProperties.LiveSetting but does not consistently raise the
    /// corresponding event when a non-bound Text property changes, so screen readers could
    /// otherwise miss the transition from “recognising” to the final result.
    /// </summary>
    private sealed class LiveTextBlock : TextBlock
    {
        public new string Text
        {
            get => base.Text;
            set
            {
                if (string.Equals(base.Text, value, StringComparison.Ordinal))
                {
                    return;
                }

                base.Text = value;
                if (!IsLoaded)
                {
                    return;
                }

                AutomationPeer? peer = UIElementAutomationPeer.FromElement(this)
                    ?? UIElementAutomationPeer.CreatePeerForElement(this);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
    }

    private static Brush ResourceBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static Button MakeButton(string text, string automationName)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            MinHeight = 32,
            Padding = new Thickness(12, 6, 12, 6),
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }
}

/// <summary>Minimal <see cref="ICommand"/> for the window's input bindings.</summary>
internal sealed class RelayUiCommand : ICommand
{
    private readonly Action _execute;

    internal RelayUiCommand(Action execute) =>
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
