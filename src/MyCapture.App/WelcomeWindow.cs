using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App;

/// <summary>What closed the first-run welcome, so the shell knows what to open next.</summary>
internal enum WelcomeOutcome
{
    /// <summary>Skipped, closed with the close button or Esc: the library opens.</summary>
    Closed,

    /// <summary>"Try it now": a region capture starts once the window is gone.</summary>
    TryCapture,

    /// <summary>The shortcut link: settings open on the shortcuts.</summary>
    OpenSettings,

    /// <summary>A capture shortcut arrived while it was open; nothing else opens.</summary>
    DismissedForCapture,
}

/// <summary>The first capture, with the current shortcut and the sign-in choice kept in reach.</summary>
internal sealed class WelcomeWindow : Window
{
    // Keep the shell's constructor contract while capture and retention tips move to their moments of use.
    internal WelcomeWindow(HotkeySettings keys, AfterCaptureAction afterCapture,
        int imageRetentionHours = CaptureRetention.DefaultImageRetentionHours,
        IOcrDownloadEnvironment? ocrEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Title = UiText.Get("Help.Welcome");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        StandardWindowTheme.Apply(this);
        SourceInitialized += (_, _) => CaptureWindowExclusion.DisableTransitions(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var title = new TextBlock
        {
            Text = UiText.Get("Welcome_FirstCaptureTitle"), FontSize = 26, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24),
        };
        AutomationProperties.SetName(title, title.Text);

        FrameworkElement captureKeys = CaptureKeys(keys.Capture);
        captureKeys.HorizontalAlignment = HorizontalAlignment.Center;
        captureKeys.Margin = new Thickness(0, 0, 0, 28);

        TryNowButton = new Button { Content = UiText.Get("Onboard_TryNow"), IsDefault = true };
        TryNowButton.SetResourceReference(StyleProperty, "Button.Primary");
        CommandButtonPresentation.Apply(TryNowButton);
        TryNowButton.MinWidth = 132;
        TryNowButton.MinHeight = 40;
        TryNowButton.ToolTip = UiText.Get("Onboard_TryNowHelp") + "  Enter";
        AutomationProperties.SetName(TryNowButton, UiText.Get("Onboard_TryNow"));
        AutomationProperties.SetHelpText(TryNowButton, UiText.Get("Onboard_TryNowHelp"));
        TryNowButton.Click += (_, _) => CloseWith(WelcomeOutcome.TryCapture);

        SkipButton = new Button { Content = UiText.Get("Welcome_Skip"), IsCancel = true };
        SkipButton.SetResourceReference(StyleProperty, "Button.Secondary");
        CommandButtonPresentation.Apply(SkipButton);
        SkipButton.MinWidth = 112;
        SkipButton.MinHeight = 40;
        SkipButton.Margin = new Thickness(9, 0, 3, 0);
        SkipButton.ToolTip = UiText.Get("Welcome_Skip") + "  Esc";
        AutomationProperties.SetName(SkipButton, UiText.Get("Welcome_Skip"));
        SkipButton.Click += (_, _) => CloseWith(WelcomeOutcome.Closed);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        actions.Children.Add(TryNowButton);
        actions.Children.Add(SkipButton);

        // The shell applies this choice after the welcome closes, before it opens the next surface.
        StartAtSignInCheck = new CheckBox
        {
            Content = UiText.Get("Onboard_StartAtSignIn"), IsChecked = true, MinHeight = 32, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiText.Get("Onboard_StartAtSignInHelp"),
        };
        AutomationProperties.SetName(StartAtSignInCheck, UiText.Get("Onboard_StartAtSignIn"));
        AutomationProperties.SetHelpText(StartAtSignInCheck, UiText.Get("Onboard_StartAtSignInHelp"));

        AccurateOcrCheck = new CheckBox
        {
            Content = UiText.Get("Welcome_AccurateOcr"),
            IsChecked = FirstRunOcrChoice.ShouldPreselect(ocrEnvironment ?? new WindowsOcrDownloadEnvironment()),
            MinHeight = 32, FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = FirstRunOcrChoice.Description,
        };
        AutomationProperties.SetName(AccurateOcrCheck, UiText.Get("Welcome_AccurateOcr"));
        AutomationProperties.SetHelpText(AccurateOcrCheck, FirstRunOcrChoice.Description);
        AccurateOcrHelp = new TextBlock
        {
            Text = FirstRunOcrChoice.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 0, 0, 0),
        };
        AccurateOcrHelp.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        ShortcutsLink = new Hyperlink(new Run(UiText.Get("Help.Settings")));
        ShortcutsLink.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(ShortcutsLink, UiText.Get("Help.Settings"));
        ShortcutsLink.Click += (_, _) => CloseWith(WelcomeOutcome.OpenSettings);
        var linkText = new TextBlock(ShortcutsLink)
        {
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinHeight = 32,
            LineHeight = 32, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(16, 0, 0, 0),
        };

        var preferences = new Grid();
        for (int row = 0; row < 3; row++)
            preferences.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preferences.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        preferences.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        preferences.Children.Add(StartAtSignInCheck);
        Grid.SetColumn(linkText, 1);
        preferences.Children.Add(linkText);
        Grid.SetRow(AccurateOcrCheck, 1);
        Grid.SetColumnSpan(AccurateOcrCheck, 2);
        preferences.Children.Add(AccurateOcrCheck);
        Grid.SetRow(AccurateOcrHelp, 2);
        Grid.SetColumnSpan(AccurateOcrHelp, 2);
        preferences.Children.Add(AccurateOcrHelp);
        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 16, 0, 0),
            Margin = new Thickness(0, 28, 0, 0), Child = preferences,
        };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");

        var root = new StackPanel { Margin = new Thickness(24, 28, 24, 20) };
        root.Children.Add(title);
        root.Children.Add(captureKeys);
        root.Children.Add(actions);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => TryNowButton.Focus();
    }

    internal WelcomeOutcome Outcome { get; private set; } = WelcomeOutcome.Closed;

    internal Button TryNowButton { get; }

    internal Button SkipButton { get; }

    internal Hyperlink ShortcutsLink { get; }

    /// <summary>"Start MyCapture when I sign in to Windows", checked by default.</summary>
    internal CheckBox StartAtSignInCheck { get; }

    /// <summary>The start-at-sign-in choice the window closes with.</summary>
    internal bool StartAtSignIn => StartAtSignInCheck.IsChecked == true;

    internal CheckBox AccurateOcrCheck { get; }
    internal TextBlock AccurateOcrHelp { get; }
    internal bool UseAccurateOcr => AccurateOcrCheck.IsChecked == true;

    /// <summary>A capture shortcut pressed while the welcome is open closes it without opening anything.</summary>
    internal void DismissForCapture()
    {
        if (!IsVisible) return;
        CloseWith(WelcomeOutcome.DismissedForCapture);
    }

    private void CloseWith(WelcomeOutcome outcome)
    {
        Outcome = outcome;
        Close();
    }

    private static FrameworkElement CaptureKeys(Hotkey capture)
    {
        if (!capture.IsAssigned)
        {
            var unassigned = new TextBlock { Text = UiText.Get("Hotkey.Unassigned"), FontSize = 16, MinHeight = 52,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            unassigned.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            AutomationProperties.SetName(unassigned, unassigned.Text);
            return unassigned;
        }

        var keys = (WrapPanel)ShortcutHelpWindow.Keycaps(capture.ToString());
        foreach (Border cap in keys.Children.OfType<Border>())
        {
            cap.MinWidth = 52;
            cap.MinHeight = 52;
            cap.Padding = new Thickness(14, 10, 14, 10);
            cap.Margin = new Thickness(3, 2, 3, 2);
            if (cap.Child is TextBlock label)
            {
                label.FontSize = 22;
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Center;
            }
        }
        return keys;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWith(WelcomeOutcome.Closed);
        }
    }
}
