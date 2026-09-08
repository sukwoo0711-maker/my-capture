using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Themes;
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
        StandardWindowTheme.Apply(this);

        Title = UiText.Get("Text_7CD1B787C3E6");
        Width = 680;
        Height = 560;
        MinWidth = 440;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        AutomationProperties.SetName(this, UiText.Get("Text_E5F541120733"));

        // Uniform dark root: one calm surface frames the whole window so the recognised text is
        // the only thing that stands out. The window Style already sets this, but making it
        // explicit keeps the surface consistent even if the default style is ever overridden.
        Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)));

        var root = new Grid
        {
            Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17))),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Sentence-case Korean heading only — no decorative English eyebrow. A quiet status line
        // sits directly beneath it as compact secondary text.
        var heading = new StackPanel { Margin = new Thickness(24, 22, 24, 16) };
        heading.Children.Add(new TextBlock
        {
            Text = UiText.Get("Text_A7C9AFB5B1AF"),
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
        AutomationProperties.SetName(_status, UiText.Get("Text_9E53B74AE062"));
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        heading.Children.Add(_status);

        // The heading rides on the same uniform surface — no raised bar, no divider — so nothing
        // competes with the recognised text for attention.
        var header = new Border
        {
            Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17))),
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
            Background = ResourceBrush("Surface.Sunken", new SolidColorBrush(Color.FromRgb(0x08, 0x0C, 0x12))),
            BorderBrush = ResourceBrush("Border.Subtle", Brushes.DimGray),
        };
        AutomationProperties.SetName(_textBox, UiText.Get("Text_B75AA3DC8AFB"));
        AutomationProperties.SetHelpText(
            _textBox, UiText.Get("Text_F73E8EBF6CE0"));

        var textFrame = new Border
        {
            Margin = new Thickness(24, 2, 24, 4),
            Padding = new Thickness(1),
            Background = ResourceBrush("Surface.Sunken", new SolidColorBrush(Color.FromRgb(0x08, 0x0C, 0x12))),
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
        _rerunButton = MakeButton(UiText.Get("Text_7245B7D87A08"), UiText.Get("Text_613230F07E18"));
        _rerunButton.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
        _rerunButton.Margin = new Thickness(0, 0, 8, 0);
        _rerunButton.Click += (_, _) => RerunRequested?.Invoke(this, EventArgs.Empty);
        buttons.Children.Add(_rerunButton);

        // Copy is the sole primary action — the one thing this window exists to make easy.
        _copyButton = MakeButton(UiText.Get("Text_37B3D3B11B26"), UiText.Get("Text_20CF6A996B4D"));
        _copyButton.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        _copyButton.Click += (_, _) => CopyText();
        buttons.Children.Add(_copyButton);

        // The footer shares the uniform surface — no raised bar or divider — keeping the window
        // text-first with the actions quietly anchored bottom-right.
        var footer = new Border
        {
            Background = ResourceBrush("Surface.Base", new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17))),
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

    /// <summary>Raised when the user dismisses the reusable window with Esc or the close button.</summary>
    internal event EventHandler? Dismissed;

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
        _status.Text = UiText.Format("Text_4A376FDFB111", contextLabel);
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
                    UiText.Format("Text_D1F6E7772569", contextLabel, result.LanguageTag, result.Lines.Count, result.Elapsed.TotalMilliseconds);
                break;

            case OcrStatus.NoText:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text =
                    UiText.Format("Text_A600C689F4FB", contextLabel);
                break;

            case OcrStatus.Unavailable:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text =
                    result.Message ??
                    UiText.Get("Text_1DC093CD4D48");
                break;

            case OcrStatus.Cancelled:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text = UiText.Format("Text_5626DD9C2F6A", contextLabel);
                break;

            default:
                _textBox.Text = string.Empty;
                _copyButton.IsEnabled = false;
                _status.Text = result.Message ?? UiText.Get("Text_AA254F35F02E");
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
            _status.Text = UiText.Get("Text_6EAF5A406E0E") + _status.Text;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard momentarily locked by another app; a copy failure must not throw.
            _status.Text = UiText.Get("Text_75425A6D9BE8");
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
            Dismissed?.Invoke(this, EventArgs.Empty);
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
