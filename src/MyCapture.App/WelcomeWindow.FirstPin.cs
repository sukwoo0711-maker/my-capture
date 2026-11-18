using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

internal sealed partial class WelcomeWindow
{
    private bool _firstPinExperience;
    private readonly StackPanel _firstPinContent = new() { Margin = new Thickness(0, 8, 0, 16) };
    private readonly StackPanel _practiceProgress = new();
    private readonly TextBlock _firstPinFeedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    internal FirstPinSession FirstPin { get; }
    internal bool IsFirstPinExperience => _firstPinExperience;
    internal Button SampleTextButton { get; } = new();
    internal Button FullPracticeButton { get; } = new();

    private void ConfigureFirstPin(WrapPanel actions)
    {
        var sample = WrappedLabel(UiText.Get("FirstRun.SampleText"), TypeScale.BodyLarge);
        var sampleSurface = new Border { Child = sample, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
        sampleSurface.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        sample.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _firstPinContent.Children.Add(sampleSurface);
        SampleTextButton.Content = UiText.Get("FirstRun.CopySample");
        SampleTextButton.HorizontalAlignment = HorizontalAlignment.Left;
        SampleTextButton.SetResourceReference(StyleProperty, "Button.Secondary");
        StyleFooterButton(SampleTextButton, 0);
        AutomationProperties.SetName(SampleTextButton, UiText.Get("FirstRun.CopySample"));
        SampleTextButton.Click += (_, _) => CopyFirstPinSample();
        _firstPinContent.Children.Add(SampleTextButton);
        ConfigureText(_firstPinFeedback, TypeScale.Body, secondary: true);
        AutomationProperties.SetLiveSetting(_firstPinFeedback, AutomationLiveSetting.Polite);
        _firstPinContent.Children.Add(_firstPinFeedback);
        FullPracticeButton.Content = UiText.Get("FirstRun.FullPractice");
        FullPracticeButton.SetResourceReference(StyleProperty, "Button.Secondary");
        FullPracticeButton.Margin = new Thickness(0, 0, 12, 0);
        StyleFooterButton(FullPracticeButton, 0);
        AutomationProperties.SetName(FullPracticeButton, UiText.Get("FirstRun.FullPractice"));
        FullPracticeButton.Click += (_, _) => OpenFullPractice();
        actions.Children.Insert(0, FullPracticeButton);
    }

    private void BeginFirstPinAttempt()
    {
        FirstPin.BeginAttempt(0);
        try { FirstPin.BeginAttempt((_options.ClipboardSequenceProvider ?? ClipboardWriter.GetVersion)()); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private void CopyFirstPinSample()
    {
        if (_closed || _showingConsent || !_firstPinExperience || FirstPin.State != FirstPinState.Ready) return;
        FirstPin.BeginAttempt(0);
        bool copied;
        try { copied = (_options.Clipboard ?? new WelcomeClipboard()).TryCopyText(UiText.Get("FirstRun.SampleText")); }
        catch (Exception error) when (error is not OutOfMemoryException) { copied = false; }
        SetText(_firstPinFeedback, UiText.Get(copied ? "FirstRun.SampleCopied" : "Journey.CopyFailed"));
        Announce(_firstPinFeedback);
    }

    internal void OpenFullPractice()
    {
        if (_closed || _showingConsent || !_firstPinExperience || FirstPin.State == FirstPinState.Ready) return;
        FirstPin.End();
        _firstPinExperience = false;
        Title = UiText.Get("FirstRun.PracticeWindowTitle");
        SetStatus(string.Empty, success: false);
        Session.ShowGroups();
        if (ShowActivated) TryNowButton.Focus();
    }

    private void RenderFirstPin()
    {
        CancelStepMotion();
        _hintTimer.Stop();
        _preview.SelectFeature(null);
        foreach (FrameworkElement part in _motionParts) part.Visibility = Visibility.Collapsed;
        _practiceProgress.Visibility = _notice.Visibility = ReplayButton.Visibility = PauseButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = SkipStepButton.Visibility = Visibility.Collapsed;
        SetStatus(string.Empty, success: false);
        bool ready = FirstPin.State == FirstPinState.Ready;
        bool pinned = FirstPin.State == FirstPinState.Pinned;
        _title.Visibility = _instruction.Visibility = Visibility.Visible;
        _title.FontSize = TypeScale.Title * _textScale;
        _instruction.FontSize = TypeScale.BodyLarge * _textScale;
        SetText(_title, UiText.Get(ready ? "FirstRun.PinTitle" : pinned ? "FirstRun.PinnedTitle" : "FirstRun.SkippedTitle"));
        SetText(_instruction, ready
            ? _pinAvailable ? UiText.Format("FirstRun.PinBody", _keys.PasteToScreen) : UiText.Get("FirstRun.PinBodyNoKey")
            : UiText.Get(pinned ? "FirstRun.PinnedBody" : "FirstRun.SkippedBody"));
        _firstPinContent.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        _caps.Clear();
        _keyDisplay.Content = ready && _pinAvailable ? Keycaps(_keys.PasteToScreen) : null;
        _keyDisplay.Visibility = ready && _pinAvailable ? Visibility.Visible : Visibility.Collapsed;
        _changeKeyRow.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        _conflict.Visibility = ready && _keys.PasteToScreen.IsAssigned && !_pinAvailable ? Visibility.Visible : Visibility.Collapsed;
        FullPracticeButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Content = UiText.Get("FirstRun.Skip");
        SkipButton.IsCancel = false;
        SkipButton.IsTabStop = ready;
        SkipButton.ToolTip = UiText.Get("FirstRun.Skip");
        AutomationProperties.SetName(SkipButton, UiText.Get("FirstRun.Skip"));
        string action = UiText.Get(ready ? "FirstRun.PinClipboard" : "FirstRun.Close");
        TryNowButton.Content = action;
        TryNowButton.ToolTip = action + "  Enter";
        TryNowButton.IsEnabled = TryNowButton.IsDefault = TryNowButton.IsTabStop = true;
        TryNowButton.SetResourceReference(StyleProperty, "Button.Primary");
        AutomationProperties.SetName(TryNowButton, action);
        AutomationProperties.SetHelpText(TryNowButton, InstructionText);
        AutomationProperties.SetAcceleratorKey(TryNowButton, "Enter");
        ApplyPrimarySlot();
        UpdateContentSizing();
        _scroll.ScrollToTop();
    }

    private void UpdateContentSizing()
    {
        bool compact = _showingConsent || _firstPinExperience;
        SizeToContent = compact ? SizeToContent.Height : SizeToContent.Manual;
        MinHeight = compact ? 0 : Math.Min(440, MaxHeight);
        if (compact) Height = double.NaN;
        else Height = Math.Min(MaxHeight >= 860 && _textScale <= 1.2 ? RoomyHeight : DefaultHeight, MaxHeight);
    }

    private void UpdatePreviewControls()
    {
        bool visible = _previewFrame.Visibility == Visibility.Visible;
        PauseButton.Visibility = visible && !_preview.ShowingInstructions ? Visibility.Visible : Visibility.Collapsed;
        string name = UiText.Get(_preview.ShowingInstructions ? "FirstRun.WatchExample" : "Journey.Replay");
        ReplayButton.ToolTip = name;
        AutomationProperties.SetName(ReplayButton, name);
        // Below the written steps the video is an offer, so it is named; over a playing video it is a compact control.
        if (_preview.ShowingInstructions)
        {
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(MarginProperty, "CommandButton.IconLabelGap");
            ReplayButton.Content = new StackPanel { Orientation = Orientation.Horizontal,
                Children = { SymbolIcon.Create(Geometry.Parse(ReplayGeometry), 16), label } };
        }
        else if (ReplayButton.Content is not SymbolIcon)
        {
            ReplayButton.Content = SymbolIcon.Create(Geometry.Parse(ReplayGeometry), 16);
        }
    }
}
