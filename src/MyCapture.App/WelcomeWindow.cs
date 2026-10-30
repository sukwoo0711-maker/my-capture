using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>What closed the first-use guide, so the shell knows what to open next.</summary>
internal enum WelcomeOutcome
{
    /// <summary>Skipped, closed with the close button or Esc: the library opens.</summary>
    Closed,

    /// <summary>Reserved: a region capture starts once the window is gone. The guide now captures while open.</summary>
    TryCapture,

    /// <summary>The shortcut link: settings open on the shortcuts.</summary>
    OpenSettings,

    /// <summary>Reserved: a capture shortcut dismissed the window. The guide now stays open for the lesson.</summary>
    DismissedForCapture,

    /// <summary>The last screen's start button: the person did the steps and confirmed the choices.</summary>
    Completed,
}

/// <summary>
/// First-use guide: capture, draw, pin, resize and find again with the real app, then the
/// start-at-sign-in and accurate-OCR choices. One task per screen; the next screen follows the
/// real event, so nothing here is a description of something the person has not done.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private const double PreviewHeight = 188;
    private const double RoomyPreviewHeight = 236;
    private const double RoomyHeight = 800;
    internal const double DefaultWidth = 640;
    internal const double DefaultHeight = 720;

    private HotkeySettings _keys;
    private readonly WelcomeOptions _options;
    private readonly DispatcherTimer _hintTimer;
    private readonly WelcomeTutorialPreview _preview;
    private readonly double _textScale;
    private readonly string[,] _practiceCells;
    private readonly TextBlock _progress = new();
    private readonly Border[] _progressSegments = [new(), new(), new()];
    private readonly TextBlock _title = new();
    private readonly TextBlock _instruction = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _statusMark = new();
    private readonly Grid _statusRow = new();
    private readonly TextBlock _hint = new();
    private readonly Border _notice;
    private readonly ContentControl _keyDisplay = new();
    private readonly Border _previewFrame;
    private readonly FrameworkElement _practiceTable;
    private readonly FrameworkElement _trayDiagram;
    private readonly FrameworkElement _preferences;
    private readonly ContentControl _recap = new();
    private readonly StackPanel _conflict = new();
    private readonly TextBlock _changeKeyRow = new();
    private readonly ScrollViewer _scroll;
    private bool _captureAvailable, _pinAvailable, _libraryAvailable;
    private bool _closed, _consentAnswered, _keyPressed;
    internal PracticeSession Session { get; }
    private bool _showingConsent;
    private readonly StackPanel _groups = new();
    internal Button BackButton { get; } = new();
    internal Button ReplayButton { get; } = new();
    internal Button PauseButton { get; } = new();
    internal event EventHandler? RestorePinRequested;

    internal WelcomeWindow(HotkeySettings keys, AfterCaptureAction afterCapture,
        int imageRetentionHours = CaptureRetention.DefaultImageRetentionHours,
        IOcrDownloadEnvironment? ocrEnvironment = null, WelcomeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
        _options = options ?? new WelcomeOptions();
        _showingConsent = _options.ShowCoreShortcutsConsent;
        Session = new PracticeSession(_options.FeaturePractice);
        _textScale = double.IsFinite(_options.TextScale) ? Math.Clamp(_options.TextScale, 1, 3) : 1;
        _captureAvailable = keys.Capture.IsAssigned;
        _pinAvailable = keys.PasteToScreen.IsAssigned;
        _libraryAvailable = keys.OpenLibrary.IsAssigned;
        Title = UiText.Get("Help.Welcome");
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = 420;
        MinHeight = 440;
        // Fixed like the other first-run windows: the work area limits it and a taller step scrolls.
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        StandardWindowTheme.Apply(this);
        ContentRendered += (_, _) => { if (ShowActivated) WindowForeground.BringForward(this); };
        FontSize = 14 * _textScale;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SourceInitialized += (_, _) =>
        {
            CaptureWindowExclusion.DisableTransitions(this);
            // Only the default placement follows the pointer; a caller that positions the window itself keeps its place.
            MonitorInfo monitor = (_options.MonitorProvider ?? MonitorEnumerator.GetFromCursor)();
            if (WindowStartupLocation == WindowStartupLocation.CenterScreen) PlaceOnPointerMonitor(monitor);
            else LimitToWorkArea(monitor);
        };

        _practiceCells = new[,]
        {
            { UiText.Get("Onboarding.TableItem"), UiText.Get("Onboarding.TableSpec") },
            { UiText.Get("Onboarding.TableMemory"), "16 GB" },
            { UiText.Get("Onboarding.TableStorage"), "512 GB" },
        };

        ConfigureText(_progress, 13, secondary: true);
        _progress.Margin = new Thickness(0, 8, 0, 0);
        ConfigureText(_status, 15);
        _status.FontWeight = FontWeights.SemiBold;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        // A check mark for what worked; the sentence beside it carries the meaning.
        _statusMark.Text = "✓";
        _statusMark.FontFamily = new FontFamily("Segoe UI Symbol");
        _statusMark.FontSize = 17 * _textScale;
        _statusMark.FontWeight = FontWeights.Bold;
        _statusMark.Margin = new Thickness(0, 0, 8, 0);
        _statusMark.VerticalAlignment = VerticalAlignment.Top;
        _statusMark.SetResourceReference(TextBlock.ForegroundProperty, "State.Success");
        _statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusRow.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_status, 1);
        _statusRow.Children.Add(_statusMark);
        _statusRow.Children.Add(_status);
        _statusRow.Margin = new Thickness(0, 0, 0, 12);
        _statusRow.Visibility = Visibility.Collapsed;
        ConfigureText(_title, 25);
        _title.FontWeight = FontWeights.SemiBold;
        _title.Margin = new Thickness(0, 16, 0, 8);
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Polite);
        ConfigureText(_instruction, 16);
        _instruction.Margin = new Thickness(0, 0, 0, 14);
        _instruction.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _keyDisplay.HorizontalAlignment = HorizontalAlignment.Left;
        _keyDisplay.Margin = new Thickness(0, 0, 0, 14);

        ConfigureText(_hint, 16);
        _hint.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(_hint, AutomationLiveSetting.Polite);
        _notice = new Border
        {
            Child = _hint, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(28, 0, 28, 12),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed,
        };
        _notice.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        _notice.SetResourceReference(Border.BorderBrushProperty, "Text.Primary");
        DockPanel.SetDock(_notice, Dock.Bottom);

        _preview = new WelcomeTutorialPreview(_options);
        _previewFrame = new Border
        {
            MinHeight = PreviewHeight, Width = PreviewHeight * 16 / 9, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _preview,
            SnapsToDevicePixels = true,
        };
        _previewFrame.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        _previewFrame.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        _practiceTable = new Image { Source = PracticePicture.Load(), Height = 120, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(_practiceTable, UiText.Get("Journey.PictureName"));
        _trayDiagram = MakeTrayDiagram();

        TryNowButton = new Button { IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        TryNowButton.SetResourceReference(StyleProperty, "Button.Primary");
        StyleFooterButton(TryNowButton, 168);
        TryNowButton.Click += (_, _) => RunPrimaryAction();
        SkipButton = new Button
        {
            Content = UiText.Get("Welcome_Skip"), IsCancel = true, Margin = new Thickness(12, 0, 0, 0),
            ToolTip = UiText.Get("Welcome_Skip") + "  Esc",
        };
        SkipButton.SetResourceReference(StyleProperty, "Button.Secondary");
        StyleFooterButton(SkipButton, 104);
        AutomationProperties.SetName(SkipButton, UiText.Get("Welcome_Skip"));
        SkipButton.Click += (_, _) =>
        {
            if (CurrentStep == WelcomeStep.Consent) AnswerCoreShortcuts(false);
            else CloseWith(WelcomeOutcome.Closed);
        };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(TryNowButton);
        actions.Children.Add(SkipButton);
        var footer = new Border
        {
            Child = actions, Padding = new Thickness(28, 16, 28, 18), BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        DockPanel.SetDock(footer, Dock.Bottom);

        // The shell applies this choice after the guide closes, before it opens the next surface.
        StartAtSignInCheck = new CheckBox
        {
            Content = UiText.Get("Onboard_StartAtSignIn"), ContentTemplate = WrappingLabelTemplate(),
            IsChecked = _options.StartAtSignIn ?? true, MinHeight = 32, FontSize = 14 * _textScale,
            ToolTip = UiText.Get("Onboard_StartAtSignInHelp"), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(StartAtSignInCheck, UiText.Get("Onboard_StartAtSignIn"));
        AutomationProperties.SetHelpText(StartAtSignInCheck, UiText.Get("Onboard_StartAtSignInHelp"));
        AccurateOcrCheck = new CheckBox
        {
            Content = UiText.Get("Welcome_AccurateOcr"), ContentTemplate = WrappingLabelTemplate(),
            IsChecked = _options.UseAccurateOcr ?? FirstRunOcrChoice.ShouldPreselect(ocrEnvironment ?? new WindowsOcrDownloadEnvironment()),
            MinHeight = 32, FontSize = 14 * _textScale, Margin = new Thickness(0, 10, 0, 0), ToolTip = FirstRunOcrChoice.Description,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(AccurateOcrCheck, UiText.Get("Welcome_AccurateOcr"));
        AutomationProperties.SetHelpText(AccurateOcrCheck, FirstRunOcrChoice.Description);
        // Wraps between words: WPF otherwise breaks between any two Hangul syllables.
        AccurateOcrHelp = WrappedLabel(FirstRunOcrChoice.Description);
        AccurateOcrHelp.FontSize = 13 * _textScale;
        AccurateOcrHelp.Margin = new Thickness(28, 0, 0, 0);
        AccurateOcrHelp.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        ShortcutsLink = new Hyperlink(new Run(UiText.Get("Help.Settings")));
        ShortcutsLink.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(ShortcutsLink, UiText.Get("Help.Settings"));
        ShortcutsLink.Click += (_, _) => CloseWith(WelcomeOutcome.OpenSettings);
        var linkText = new TextBlock(ShortcutsLink)
        {
            VerticalAlignment = VerticalAlignment.Center, FontSize = 13 * _textScale, MinHeight = 32,
            LineHeight = 32 * _textScale, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(16, 0, 0, 0),
        };
        var replayNote = WrappedLabel(UiText.Get("Onboarding.ReplayNote"), 13);
        replayNote.Margin = new Thickness(0, 22, 0, 0);
        replayNote.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        var preferences = new Grid();
        for (int row = 0; row < 4; row++)
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
        Grid.SetRow(replayNote, 3);
        Grid.SetColumnSpan(replayNote, 2);
        preferences.Children.Add(replayNote);
        _preferences = preferences;
        _recap.Content = MakeRecap();
        ChangeShortcutLink = new Hyperlink(new Run(UiText.Get("Practice.ChangeKey")));
        ChangeShortcutLink.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        ChangeShortcutLink.Click += (_, _) => RequestShortcutChange();
        _changeKeyRow.Inlines.Add(ChangeShortcutLink);
        _changeKeyRow.FontSize = 13 * _textScale;
        _changeKeyRow.Margin = new Thickness(0, 0, 0, 10);
        TextBlock conflictText = WrappedLabel(UiText.Get("Practice.KeyConflict"));
        conflictText.SetResourceReference(TextBlock.ForegroundProperty, "State.Danger");
        _conflict.Children.Add(conflictText);
        var conflictActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        ChangeShortcutButton = new Button { Content = UiText.Get("Practice.AssignKey") };
        ChangeShortcutButton.SetResourceReference(StyleProperty, "Button.Secondary");
        ChangeShortcutButton.Click += (_, _) => RequestShortcutChange();
        SkipStepButton = new Button { Content = UiText.Get("Practice.SkipStep"), Margin = new Thickness(8, 0, 0, 0) };
        SkipStepButton.SetResourceReference(StyleProperty, "Button.Secondary");
        SkipStepButton.Click += (_, _) => SkipCurrentStep();
        foreach (Button button in new[] { ChangeShortcutButton })
        {
            button.FontSize = 14 * _textScale;
            button.MinHeight = 36 * _textScale;
            AutomationProperties.SetName(button, (string)button.Content);
            conflictActions.Children.Add(button);
        }
        _conflict.Children.Add(conflictActions);
        BackButton.Content = UiText.Get("Journey.Back");
        BackButton.Click += (_, _) => Session.Back();
        foreach (Button button in new[] { BackButton, SkipStepButton })
        {
            button.SetResourceReference(StyleProperty, "Button.Secondary");
            StyleFooterButton(button, 0); button.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(button, (string)button.Content);
        }
        actions.Children.Insert(0, SkipStepButton);
        actions.Children.Insert(0, BackButton);
        ReplayButton.Content = UiText.Get("Journey.Replay");
        ReplayButton.Click += (_, _) => _preview.Replay();
        PauseButton.Content = UiText.Get("Journey.Pause");
        PauseButton.Click += (_, _) => _preview.Pause();
        var playback = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        foreach (Button button in new[] { ReplayButton, PauseButton })
        {
            button.SetResourceReference(StyleProperty, "Button.Secondary");
            button.MinHeight = 32; button.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(button, (string)button.Content);
            playback.Children.Add(button);
        }
        foreach (var (step, name) in new[] { (PracticeStep.A1, "Journey.CaptureGroup"), (PracticeStep.B0, "Journey.PinGroup"), (PracticeStep.C1, "Journey.FindGroup") })
        {
            var button = new Button { Content = UiText.Get(name), Margin = new Thickness(0, 4, 0, 4), MinHeight = 36 };
            button.SetResourceReference(StyleProperty, "Button.Secondary");
            button.Click += (_, _) => Session.StartGroup(step);
            _groups.Children.Add(button);
        }

        var progressRow = new Grid();
        for (int index = 0; index < _progressSegments.Length; index++)
        {
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Border segment = _progressSegments[index];
            segment.Height = 7; segment.CornerRadius = new CornerRadius(3.5); segment.BorderThickness = new Thickness(1);
            segment.SetResourceReference(Border.BorderBrushProperty, "Border.Control");
            segment.Margin = new Thickness(index == 0 ? 0 : 6, 0, 0, 0);
            Grid.SetColumn(segment, index);
            progressRow.Children.Add(segment);
        }
        var progress = new StackPanel();
        progress.Children.Add(progressRow);
        progress.Children.Add(_progress);

        var body = new StackPanel { Margin = new Thickness(28, 18, 28, 14) };
        body.Children.Add(progress);
        body.Children.Add(_title);
        body.Children.Add(_instruction);
        body.Children.Add(_statusRow);
        body.Children.Add(_practiceTable);
        body.Children.Add(_keyDisplay);
        body.Children.Add(_changeKeyRow);
        body.Children.Add(_conflict);
        body.Children.Add(_previewFrame);
        body.Children.Add(playback);
        body.Children.Add(_groups);
        body.Children.Add(_trayDiagram);
        body.Children.Add(_recap);
        body.Children.Add(_preferences);
        _scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        // The footer is declared first so it docks below the content; Tab still reads the content before the buttons.
        KeyboardNavigation.SetTabIndex(_scroll, 0);
        KeyboardNavigation.SetTabIndex(footer, 1);
        var root = new DockPanel();
        root.Children.Add(footer);
        root.Children.Add(_notice);
        root.Children.Add(_scroll);
        Content = root;
        _hintTimer = new DispatcherTimer { Interval = _options.HintDelay };
        _hintTimer.Tick += (_, _) => ShowRetryHint();
        Session.Changed += OnSessionChanged;
        RenderStep();
        Loaded += (_, _) =>
        {
            BringToFront();
            if (ShowActivated) TryNowButton.Focus();
            StartHintTimer();
        };
        StateChanged += (_, _) => _preview.Suspend(WindowState == WindowState.Minimized);
        IsVisibleChanged += (_, _) => _preview.Suspend(!IsVisible || WindowState == WindowState.Minimized);
        Closed += (_, _) =>
        {
            if (CurrentStep == WelcomeStep.Consent && !_consentAnswered)
            {
                _consentAnswered = true;
                CoreShortcutsAnswered?.Invoke(false);
            }
            _closed = true; Session.Changed -= OnSessionChanged; Session.End(); _hintTimer.Stop(); _preview.Dispose();
        };
    }

    /// <summary>The person asked for a region capture, by the button or by the shortcut.</summary>
    internal event EventHandler? CaptureRequested;

    /// <summary>The person asked to return to the editor for the pin practice.</summary>
    internal event EventHandler? PinRequested;

    /// <summary>The person asked to open the library by the button.</summary>
    internal event EventHandler? LibraryRequested;
    internal event EventHandler? EditorRequested;
    internal event Action<bool>? CoreShortcutsAnswered;
    internal event Action<GlobalHotkeyCommand>? ShortcutChangeRequested;

    internal WelcomeStep CurrentStep => _showingConsent ? WelcomeStep.Consent : Session.Step switch
    {
        PracticeStep.A1 or PracticeStep.A2 => WelcomeStep.Capture,
        >= PracticeStep.A3 and <= PracticeStep.A6 => WelcomeStep.Draw,
        PracticeStep.B0 or PracticeStep.B1 => WelcomeStep.Pin,
        >= PracticeStep.B2 and <= PracticeStep.B6 => WelcomeStep.Resize,
        PracticeStep.C1 or PracticeStep.C2 => WelcomeStep.Library,
        _ => WelcomeStep.Finish,
    };
    internal WelcomeOutcome Outcome { get; private set; } = WelcomeOutcome.Closed;

    /// <summary>The one primary action of the current step. The label and effect change with the step.</summary>
    internal Button TryNowButton { get; }
    internal Button SkipButton { get; }
    internal Hyperlink ShortcutsLink { get; }
    internal Hyperlink ChangeShortcutLink { get; }
    internal Button ChangeShortcutButton { get; }
    internal Button SkipStepButton { get; }

    /// <summary>"Start MyCapture when I sign in to Windows", checked by default.</summary>
    internal CheckBox StartAtSignInCheck { get; }

    /// <summary>The start-at-sign-in choice the window closes with.</summary>
    internal bool StartAtSignIn => StartAtSignInCheck.IsChecked == true;
    internal CheckBox AccurateOcrCheck { get; }
    internal TextBlock AccurateOcrHelp { get; }
    internal bool UseAccurateOcr => AccurateOcrCheck.IsChecked == true;
    internal bool IsReplay => _options.Replay;
    internal bool PracticeCopied => Session.ClipboardSequence != 0;
    internal bool KeyPressed => _keyPressed;
    internal bool ConflictShown => _conflict.Visibility == Visibility.Visible;
    internal bool IsFeaturePractice => _options.FeaturePractice;
    internal bool CapturePending => Session.CapturePending;
    internal bool SuccessShown => _statusMark.Visibility == Visibility.Visible && _statusRow.Visibility == Visibility.Visible;
    internal TimeSpan HintDelay => _hintTimer.Interval;
    internal string TitleText => KoreanWordWrap.Strip(_title.Text);
    internal string InstructionText => KoreanWordWrap.Strip(_instruction.Text);
    internal string ProgressText => _progress.Text;
    internal string StatusText => KoreanWordWrap.Strip(_status.Text);
    internal string RetryHintText => _notice.Visibility == Visibility.Visible ? KoreanWordWrap.Strip(_hint.Text) : string.Empty;
    internal bool KeyShown => _keyDisplay.Visibility == Visibility.Visible;
    internal bool PreviewShown => _previewFrame.Visibility == Visibility.Visible;
    internal bool PracticeTableShown => _practiceTable.Visibility == Visibility.Visible;
    internal bool TrayDiagramShown => _trayDiagram.Visibility == Visibility.Visible;
    internal bool PreferencesShown => _preferences.Visibility == Visibility.Visible;
    internal ScrollViewer Scroller => _scroll;
    internal WelcomeTutorialPreview Preview => _preview;

    /// <summary>
    /// The shell reports which shortcuts Windows really registered. A shortcut that failed is
    /// never shown as the way to continue: the buttons stay the only path on that step.
    /// </summary>
    internal void UpdateShortcutAvailability(bool capture, bool pin, bool library)
    {
        (bool Capture, bool Pin, bool Library) next = (capture && _keys.Capture.IsAssigned,
            pin && _keys.PasteToScreen.IsAssigned, library && _keys.OpenLibrary.IsAssigned);
        if (next == (_captureAvailable, _pinAvailable, _libraryAvailable)) return;
        (_captureAvailable, _pinAvailable, _libraryAvailable) = next;
        RenderStep();
    }

    internal void UpdateHotkeys(HotkeySettings keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
        RenderStep();
    }

    internal void NotifyHotkeyFired(GlobalHotkeyCommand command)
    {
        if (_closed || CurrentCommand() != command || !CurrentKey().IsAssigned) return;
        _keyPressed = true;
        RenderStep();
    }

    internal bool Notify(PracticeEvent signal) => Session.Accept(signal);

    // Untagged historical notifications cannot prove which practice object produced a result.
    internal void NotifyArrowShortcutFired() { }
    internal void NotifyCaptureCompleted(bool copied) { }
    internal void NotifyPinCreated() { }
    internal void NotifyArrowAdded() { }
    internal void NotifyEditorPinCreated(Guid pinId) { }
    internal void NotifyPracticePinClosed(Guid pinId) { }
    internal void NotifyReplacementPinOpened(Guid pinId) { }
    internal void NotifyPinZoomChanged(Guid pinId) { }
    internal void NotifyLibraryOpened() => Notify(new(PracticeEventKind.LibraryReady, Session.Id));
    internal void NotifyCaptureCancelled() => Notify(new(PracticeEventKind.CaptureCancelled, Session.Id, Session.CaptureRequestId));
    internal void ResetPendingCapture() => NotifyCaptureCancelled();

    internal void DismissForCapture()
    {
        if (_closed || Session.Step is not (PracticeStep.A1 or PracticeStep.A2) || Session.CapturePending) return;
        Session.BeginCapture();
        _preview.Pause();
        UpdateLayout();
    }

    private void RunPrimaryAction()
    {
        if (_closed || Session.CapturePending || Session.PinRestorePending) return;
        if (_showingConsent) { AnswerCoreShortcuts(true); return; }
        Session.ClearFeedback();
        PracticeStep step = Session.Step;
        if (step is >= PracticeStep.B2 and <= PracticeStep.B6 && Session.WindowPinId is null)
        {
            if (Session.PinRestorable) RestorePinRequested?.Invoke(this, EventArgs.Empty);
            else Session.StartGroup(PracticeStep.B0);
            return;
        }
        if (step is >= PracticeStep.A3 and <= PracticeStep.A6 && Session.CaptureId is null || step == PracticeStep.C2 && Session.CaptureId is null)
        { Session.StartGroup(PracticeStep.A1); return; }
        switch (step)
        {
            case PracticeStep.A1:
            case PracticeStep.A2:
                if (Session.CapturePending) return;
                DismissForCapture();
                // Render A2 and its sample before the capture handler freezes the desktop.
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                { if (!_closed && Session.CapturePending) CaptureRequested?.Invoke(this, EventArgs.Empty); }));
                break;
            case PracticeStep.A3:
                EditorRequested?.Invoke(this, EventArgs.Empty); break;
            case >= PracticeStep.A4 and <= PracticeStep.A6 when Session.EditorId is null:
                EditorRequested?.Invoke(this, EventArgs.Empty); break;
            case PracticeStep.B0:
            case PracticeStep.B1 when Session.ClipboardSequence == 0:
                try
                {
                    uint sequence = (_options.CopyPracticePicture ?? PracticePicture.Copy)();
                    if (sequence == 0) Session.Notice("Journey.CopyFailed");
                    else Notify(new(PracticeEventKind.PictureCopied, Session.Id, ClipboardSequence: sequence));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Session.Notice("Journey.CopyFailed"); }
                break;
            case PracticeStep.B1: PinRequested?.Invoke(this, EventArgs.Empty); break;
            case PracticeStep.B6: Session.RepeatPinClose(); break;
            case PracticeStep.C1:
            case PracticeStep.C2: LibraryRequested?.Invoke(this, EventArgs.Empty); break;
            case PracticeStep.Results: CloseWith(WelcomeOutcome.Completed); break;
            case PracticeStep.Groups: Session.StartGroup(PracticeStep.A1); break;
        }
    }

    private void AnswerCoreShortcuts(bool accept)
    {
        if (_consentAnswered || _closed) return;
        _consentAnswered = true; _showingConsent = false;
        CoreShortcutsAnswered?.Invoke(accept);
        RenderStep();
    }
    private void RequestShortcutChange()
    { if (CurrentCommand() is { } command) ShortcutChangeRequested?.Invoke(command); }
    private void SkipCurrentStep() { if (!_closed && !_showingConsent) Session.Skip(); }
    internal string PracticeText() => string.Join("\r\n", Enumerable.Range(0, _practiceCells.GetLength(0)).Select(row => _practiceCells[row, 0] + "\t" + _practiceCells[row, 1]));

    private void OnSessionChanged()
    {
        if (_closed) return;
        _keyPressed = false;
        string message = Session.NoticeKey is { } notice ? UiText.Get(notice)
            : Session.LastCompleted is { } completed ? UiText.Get($"Journey.{completed}.Success") + "  "
                + UiText.Format("Journey.Next", UiText.Get($"Journey.{Session.Step}.Title")) : string.Empty;
        SetStatus(message, announce: false, success: Session.LastCompleted is not null && Session.NoticeKey is null);
        RenderStep();
        _scroll.ScrollToTop();
        if (message.Length > 0) Announce(_status); else Announce(_title);
    }
    private Hotkey CurrentKey() => Session.Step switch
    {
        PracticeStep.A1 when _captureAvailable => _keys.Capture,
        PracticeStep.B1 when _pinAvailable => _keys.PasteToScreen,
        _ => Hotkey.None,
    };
    private GlobalHotkeyCommand? CurrentCommand() => Session.Step switch
    {
        PracticeStep.A1 or PracticeStep.A2 => GlobalHotkeyCommand.CaptureRegion,
        PracticeStep.B1 => GlobalHotkeyCommand.PasteToScreen,
        _ => null,
    };
    private bool CurrentKeyConflicts() => Session.Step switch
    {
        PracticeStep.A1 or PracticeStep.A2 => _keys.Capture.IsAssigned && !_captureAvailable,
        PracticeStep.B1 => _keys.PasteToScreen.IsAssigned && !_pinAvailable,
        _ => false,
    };
    internal static string? FeatureFor(PracticeStep step) => step switch
    {
        PracticeStep.A1 or PracticeStep.A2 => "capture-region", PracticeStep.A3 => "practice-editor-open",
        PracticeStep.A4 or PracticeStep.A5 => "image-arrow", PracticeStep.A6 => "image-copy",
        PracticeStep.B0 => "practice-image-copy", PracticeStep.B1 => "practice-image-pin", PracticeStep.B2 => "pin-resize",
        PracticeStep.B3 or PracticeStep.B4 => "pin-opacity", PracticeStep.B5 => "practice-pin-close", PracticeStep.B6 => "pin-restore",
        PracticeStep.C1 => "library-open", PracticeStep.C2 => "practice-library-find", _ => null,
    };
    private void RenderStep()
    {
        PracticeStep step = Session.Step;
        bool finish = step == PracticeStep.Results && !_showingConsent;
        bool groups = step == PracticeStep.Groups;
        SkipButton.Visibility = finish ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.IsTabStop = !finish; SkipButton.IsCancel = !_showingConsent;
        SkipButton.Content = UiText.Get(_showingConsent ? "Practice.KeepCurrent" : "Welcome_Skip");
        SkipButton.ToolTip = (string)SkipButton.Content + "  Esc";
        AutomationProperties.SetName(SkipButton, (string)SkipButton.Content);
        BackButton.Visibility = SkipStepButton.Visibility = _showingConsent || groups ? Visibility.Collapsed : Visibility.Visible;
        SkipStepButton.Visibility = finish ? Visibility.Collapsed : SkipStepButton.Visibility;
        _groups.Visibility = groups || finish ? Visibility.Visible : Visibility.Collapsed;
        int group = step <= PracticeStep.A6 ? 0 : step <= PracticeStep.B6 ? 1 : 2;
        string label = UiText.Get(group == 0 ? "Journey.CaptureGroup" : group == 1 ? "Journey.PinGroup" : "Journey.FindGroup");
        int number = Array.IndexOf(PracticeSession.Steps, step) + 1;
        _progress.Text = _showingConsent ? UiText.Get("Practice.ConsentLabel") : groups || finish ? UiText.Get($"Journey.{step}.Title") : $"{number} / 15 · {label}";
        AutomationProperties.SetName(_progress, _progress.Text);
        for (int i = 0; i < _progressSegments.Length; i++)
        {
            _progressSegments[i].Visibility = i < 3 && !_showingConsent ? Visibility.Visible : Visibility.Collapsed;
            bool complete = PracticeSession.Steps.Where(item => i == 0 ? item <= PracticeStep.A6 : i == 1 ? item >= PracticeStep.B0 && item <= PracticeStep.B6 : item >= PracticeStep.C1)
                .All(item => Session.Results[item] == PracticeStepResult.Completed);
            if (complete) _progressSegments[i].SetResourceReference(Border.BackgroundProperty, "Text.Primary");
            else _progressSegments[i].Background = Brushes.Transparent;
        }
        SetText(_title, UiText.Get(_showingConsent ? "CoreShortcuts.Title" : $"Journey.{step}.Title"));
        string instruction = _showingConsent ? UiText.Get("CoreShortcuts.Body") : step == PracticeStep.B1
            ? _pinAvailable ? UiText.Format("Journey.B1.Body", _keys.PasteToScreen) : UiText.Get("Journey.B1.NoKey")
            : UiText.Get(step == PracticeStep.B6 && Session.WindowPinId is not null ? "Journey.PinAlreadyRestored" : $"Journey.{step}.Body");
        SetText(_instruction, instruction);
        Hotkey key = _showingConsent ? Hotkey.None : CurrentKey();
        _keyDisplay.Content = key.IsAssigned ? Keycaps(key) : null;
        _keyDisplay.Visibility = key.IsAssigned ? Visibility.Visible : Visibility.Collapsed;
        _conflict.Visibility = !_showingConsent && CurrentKeyConflicts() ? Visibility.Visible : Visibility.Collapsed;
        _changeKeyRow.Visibility = !_showingConsent && CurrentCommand() is not null ? Visibility.Visible : Visibility.Collapsed;
        _practiceTable.Visibility = !_showingConsent && step is PracticeStep.A1 or PracticeStep.A2 or PracticeStep.B0 ? Visibility.Visible : Visibility.Collapsed;
        _trayDiagram.Visibility = Visibility.Collapsed;
        _preferences.Visibility = finish && !_options.FeaturePractice ? Visibility.Visible : Visibility.Collapsed;
        _recap.Visibility = finish ? Visibility.Visible : Visibility.Collapsed;
        if (finish) _recap.Content = MakeRecap();
        string? feature = _showingConsent ? null : FeatureFor(step);
        bool showPreview = feature is not null && !(step == PracticeStep.A2 && Session.CapturePending);
        _previewFrame.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        ReplayButton.Visibility = PauseButton.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        if (_preview.AssetPath is not { } existing || feature is null || !System.IO.Path.GetFileName(existing).StartsWith(feature + ".", StringComparison.Ordinal)) _preview.SelectFeature(feature);
        string action = UiText.Get(_showingConsent ? "Practice.Start" : step switch
        {
            PracticeStep.A1 or PracticeStep.A2 => "Onboarding.StartCapture",
            PracticeStep.A3 => "Journey.A3.Title", PracticeStep.B0 => "Journey.B0.Title", PracticeStep.B1 => "Journey.ShowPicture",
            PracticeStep.B6 => "Journey.CloseAgain", PracticeStep.C1 or PracticeStep.C2 => "Journey.C1.Title",
            PracticeStep.Results => "Welcome_Skip", PracticeStep.Groups => "Journey.CaptureGroup", _ => "Journey.Replay",
        });
        if (!_showingConsent)
        {
            if (step is >= PracticeStep.A3 and <= PracticeStep.A6 && Session.CaptureId is null || step == PracticeStep.C2 && Session.CaptureId is null) action = UiText.Get("Journey.GoCapture");
            else if (step is >= PracticeStep.A4 and <= PracticeStep.A6 && Session.EditorId is null) action = UiText.Get("Journey.A3.Title");
            else if (step == PracticeStep.B1 && Session.ClipboardSequence == 0) action = UiText.Get("Journey.CopyAgain");
            else if (step == PracticeStep.B0 && Session.NoticeKey == "Journey.CopyFailed") action = UiText.Get("Journey.CopyRetry");
            else if (step is >= PracticeStep.B2 and <= PracticeStep.B6 && Session.WindowPinId is null) action = UiText.Get(Session.PinRestorable ? "Journey.Reopen" : "Journey.CopyAgain");
        }
        TryNowButton.Content = action; AutomationProperties.SetName(TryNowButton, action);
        AutomationProperties.SetHelpText(TryNowButton, instruction); TryNowButton.ToolTip = action + "  Enter";
        bool waitingElsewhere = !_showingConsent && (step is >= PracticeStep.A4 and <= PracticeStep.A6 && Session.EditorId is not null
            || step is >= PracticeStep.B2 and <= PracticeStep.B5 && Session.WindowPinId is not null);
        TryNowButton.Visibility = waitingElsewhere ? Visibility.Collapsed : Visibility.Visible;
        TryNowButton.IsDefault = TryNowButton.IsTabStop = !waitingElsewhere;
        TryNowButton.IsEnabled = !Session.CapturePending && !Session.PinRestorePending;
        _notice.Visibility = Visibility.Collapsed;
        StartHintTimer();
    }
    internal void ShowRetryHint()
    {
        _hintTimer.Stop();
        if (_closed || _showingConsent || Session.CapturePending || Session.Step is PracticeStep.Groups or PracticeStep.Results) return;
        SetText(_hint, Session.Step == PracticeStep.B2 ? UiText.Get("Journey.ResizeHelp") : InstructionText);
        _notice.Visibility = Visibility.Visible;
        Announce(_hint);
    }

    private void StartHintTimer()
    {
        _hintTimer.Stop();
        if (IsLoaded && !_closed && !Session.CapturePending && CurrentStep is not (WelcomeStep.Finish or WelcomeStep.Consent)) _hintTimer.Start();
    }

    private void SetStatus(string message, bool announce = true, bool success = true)
    {
        SetText(_status, message);
        _status.SetResourceReference(TextBlock.ForegroundProperty, success ? "Text.Primary" : "Text.Secondary");
        _statusMark.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
        _statusRow.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (success && message.Length > 0 && !(_options.ReducedMotion ?? !FluidMotion.AnimationsEnabled))
            _statusMark.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        else { _statusMark.BeginAnimation(OpacityProperty, null); _statusMark.Opacity = 1; }
        if (announce) Announce(_status);
    }

    private static void Announce(FrameworkElement element)
    {
        if (!element.IsLoaded) return;
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))?
            .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ConfigureText(TextBlock text, double size, bool secondary = false)
    {
        text.FontSize = size * _textScale;
        text.TextWrapping = TextWrapping.Wrap;
        if (secondary) text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
    }

    private static void SetText(TextBlock text, string value)
    {
        KoreanWordWrap.Show(text, value);
        AutomationProperties.SetName(text, value);
    }

    /// <summary>A check box label that wraps instead of being cut off when the text is large; the content stays the plain string.</summary>
    private static DataTemplate WrappingLabelTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        return new DataTemplate { VisualTree = text };
    }

    private TextBlock WrappedLabel(string value, double size = 14)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size * _textScale };
        SetText(text, value);
        return text;
    }

    private void StyleFooterButton(Button button, double minWidth)
    {
        CommandButtonPresentation.Apply(button);
        // The shared command size is a fixed height in the base font; a larger text size needs room to grow.
        button.ClearValue(HeightProperty);
        button.FontSize = 15 * _textScale;
        button.MinHeight = 44 * _textScale;
        button.MinWidth = minWidth;
        button.Padding = new Thickness(18, 8, 18, 8);
    }

    private FrameworkElement Keycaps(Hotkey hotkey)
    {
        var keys = (WrapPanel)ShortcutHelpWindow.Keycaps(hotkey.ToString());
        foreach (Border cap in keys.Children.OfType<Border>())
        {
            cap.MinWidth = 54 * _textScale; cap.MinHeight = 52 * _textScale;
            cap.Padding = new Thickness(14, 6, 14, 6); cap.Margin = new Thickness(0, 2, 6, 2);
            cap.BorderThickness = new Thickness(1, 1, 1, 3);
            cap.CornerRadius = new CornerRadius(8);
            if (_keyPressed) cap.SetResourceReference(Border.BackgroundProperty, "Text.Primary");
            if (cap.Child is TextBlock label)
            {
                label.FontSize = 22 * _textScale;
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
                if (_keyPressed) label.SetResourceReference(TextBlock.ForegroundProperty, "Surface.Base");
            }
        }
        // A plus between the caps says they are pressed together.
        for (int index = keys.Children.Count - 1; index >= 1; index--)
        {
            if (keys.Children[index] is not Border || keys.Children[index - 1] is not Border) continue;
            var plus = new TextBlock { Text = "+", FontSize = 20 * _textScale, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            plus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            keys.Children.Insert(index, plus);
        }
        if (_keyPressed)
        {
            var check = new TextBlock { Text = "✓", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 22 * _textScale,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            check.SetResourceReference(TextBlock.ForegroundProperty, "State.Success");
            AutomationProperties.SetName(check, UiText.Get("Practice.KeyPressed"));
            keys.Children.Add(check);
        }
        return keys;
    }

    /// <summary>Only completed practice actions receive a check; the keys reflect the current settings.</summary>
    private FrameworkElement MakeRecap()
    {
        var stack = new StackPanel();
        foreach (PracticeStep step in PracticeSession.Steps)
        {
            PracticeStepResult result = Session.Results[step];
            var row = WrappedLabel((result == PracticeStepResult.Completed ? "✓ " : "– ") + UiText.Get($"Journey.{step}.Title") + " · " + UiText.Get($"Journey.{result}"), 13);
            row.Margin = new Thickness(0, 2, 0, 2); stack.Children.Add(row);
        }
        return stack;
    }

    private FrameworkElement MakePracticeTable()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        TextBlock label = WrappedLabel(UiText.Get("Onboarding.PracticeLabel"), 13);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, 0, 6);
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        panel.Children.Add(label);
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition());
        table.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < _practiceCells.GetLength(0); row++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int column = 0; column < 2; column++)
            {
                TextBlock text = WrappedLabel(_practiceCells[row, column], 16);
                if (row == 0) text.FontWeight = FontWeights.SemiBold;
                var cell = new Border
                {
                    Child = text, Padding = new Thickness(16, 6, 16, 6),
                    BorderThickness = new Thickness(0, 0, column == 0 ? 1 : 0, row < _practiceCells.GetLength(0) - 1 ? 1 : 0),
                };
                cell.SetResourceReference(Border.BorderBrushProperty, "Border.Control");
                cell.SetResourceReference(Border.BackgroundProperty, row == 0 ? "Surface.Overlay" : "Surface.Raised");
                Grid.SetRow(cell, row); Grid.SetColumn(cell, column); table.Children.Add(cell);
            }
        }
        var outline = new Border { Child = table, BorderThickness = new Thickness(1) };
        outline.SetResourceReference(Border.BorderBrushProperty, "Border.Control");
        panel.Children.Add(outline);
        AutomationProperties.SetName(panel, UiText.Get("Onboarding.PracticeLabel"));
        return panel;
    }

    /// <summary>
    /// The taskbar corner as the person sees it: the ^ button, and inside the opened flyout the real
    /// MyCapture icon with a ring. The ring marks the two places to look.
    /// </summary>
    private FrameworkElement MakeTrayDiagram()
    {
        const double slot = 56, taskbarHeight = 56;
        var layout = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(slot) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border Placeholder(double size)
        {
            var box = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), Margin = new Thickness(3) };
            box.SetResourceReference(Border.BackgroundProperty, "Surface.Hover");
            return box;
        }

        Border Ring(UIElement child, double size)
        {
            var ring = new Border
            {
                Width = size, Height = size, Child = child, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2),
                Margin = new Thickness(3),
            };
            ring.SetResourceReference(Border.BorderBrushProperty, "Text.Primary");
            return ring;
        }

        // The opened overflow flyout: a few other icons, and MyCapture ringed among them.
        var icons = new StackPanel { Orientation = Orientation.Horizontal };
        icons.Children.Add(Placeholder(slot - 6));
        icons.Children.Add(Placeholder(slot - 6));
        var trayIcon = new Image { Margin = new Thickness(5), Stretch = Stretch.Uniform, Source = LoadTrayIcon() };
        if (trayIcon.Source is null) trayIcon.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(trayIcon, UiText.Get("Onboarding.TrayIconName"));
        RenderOptions.SetBitmapScalingMode(trayIcon, BitmapScalingMode.HighQuality);
        icons.Children.Add(Ring(trayIcon, slot));
        icons.Children.Add(Placeholder(slot - 6));
        var flyout = new Border
        {
            Child = icons, Padding = new Thickness(8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        flyout.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        flyout.SetResourceReference(Border.BorderBrushProperty, "Border.Control");
        Grid.SetRow(flyout, 0); Grid.SetColumn(flyout, 0); Grid.SetColumnSpan(flyout, 2);
        grid.Children.Add(flyout);

        // A caret from the flyout down to the ^ button.
        var caret = new Polygon { Points = [new Point(0, 0), new Point(16, 0), new Point(8, 9)], Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center };
        caret.SetResourceReference(Shape.FillProperty, "Text.Primary");
        Grid.SetRow(caret, 1); Grid.SetColumn(caret, 1);
        grid.Children.Add(caret);

        var taskbar = new Border { Height = taskbarHeight, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        taskbar.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        taskbar.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        Grid.SetRow(taskbar, 2); Grid.SetColumnSpan(taskbar, 4);
        grid.Children.Add(taskbar);

        var apps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        for (int index = 0; index < 4; index++) apps.Children.Add(Placeholder(38));
        Grid.SetRow(apps, 2); Grid.SetColumn(apps, 0);
        grid.Children.Add(apps);

        var chevron = new TextBlock { Text = "^", FontSize = 32, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(chevron, "^");
        Border chevronRing = Ring(chevron, slot - 4);
        chevronRing.Margin = new Thickness(0);
        chevronRing.VerticalAlignment = VerticalAlignment.Center;
        chevronRing.HorizontalAlignment = HorizontalAlignment.Center;
        chevronRing.CornerRadius = new CornerRadius(8);
        Grid.SetRow(chevronRing, 2); Grid.SetColumn(chevronRing, 1);
        grid.Children.Add(chevronRing);

        var system = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        system.Children.Add(Placeholder(22));
        system.Children.Add(Placeholder(22));
        Grid.SetRow(system, 2); Grid.SetColumn(system, 2);
        grid.Children.Add(system);
        var clock = new TextBlock { Text = "12:00", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 14, 0) };
        clock.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        Grid.SetRow(clock, 2); Grid.SetColumn(clock, 3);
        grid.Children.Add(clock);
        layout.Children.Add(grid);

        TextBlock location = WrappedLabel(UiText.Get("Onboarding.TrayLocation"), 15);
        location.FontWeight = FontWeights.SemiBold;
        layout.Children.Add(location);
        TextBlock actions = WrappedLabel(UiText.Get("Onboard_TrayTipActions"), 14);
        actions.Margin = new Thickness(0, 4, 0, 0);
        actions.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        layout.Children.Add(actions);
        AutomationProperties.SetName(layout, UiText.Get("Onboarding.TrayLocation"));
        return layout;
    }

    // The shipped tray icon, so the picture shows what the person will look for.
    private static ImageSource? LoadTrayIcon()
    {
        try
        {
            string directory = System.IO.Path.GetDirectoryName(typeof(WelcomeWindow).Assembly.Location) ?? AppContext.BaseDirectory;
            string path = System.IO.Path.Combine(directory, "Assets", "tray-idle.ico");
            if (!File.Exists(path)) return null;
            using FileStream stream = File.OpenRead(path);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame? frame = decoder.Frames.Where(candidate => candidate.PixelWidth >= 32)
                .OrderBy(candidate => candidate.PixelWidth).FirstOrDefault() ?? decoder.Frames.LastOrDefault();
            if (frame is null) return null;
            frame.Freeze();
            return frame;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The window's centred rectangle on <paramref name="monitor"/>, in that monitor's physical pixels.</summary>
    internal static RectD CenterOn(MonitorInfo monitor, double widthDip, double heightDip)
    {
        double scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1;
        double width = Math.Min(widthDip * scale, monitor.WorkArea.Width);
        double height = Math.Min(heightDip * scale, monitor.WorkArea.Height);
        return new RectD(monitor.WorkArea.Left + ((monitor.WorkArea.Width - width) / 2),
            monitor.WorkArea.Top + ((monitor.WorkArea.Height - height) / 2), width, height);
    }

    internal void LimitToWorkArea(MonitorInfo monitor)
    {
        double scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1;
        MaxWidth = Math.Max(1, monitor.WorkArea.Width / scale);
        MaxHeight = Math.Max(1, monitor.WorkArea.Height / scale);
        MinWidth = Math.Min(420, MaxWidth);
        MinHeight = Math.Min(440, MaxHeight);
        // A tall display shows the clip larger; a laptop keeps every step above the fold at ordinary text size.
        bool roomy = MaxHeight >= 860 && _textScale <= 1.2;
        double previewHeight = roomy ? RoomyPreviewHeight : _textScale > 1.2 ? 150 : PreviewHeight;
        _previewFrame.Height = double.NaN;
        _previewFrame.MinHeight = previewHeight;
        _previewFrame.Width = Math.Min(previewHeight * 16 / 9, Math.Max(120, MaxWidth - 72));
        Width = Math.Min(DefaultWidth, MaxWidth);
        Height = Math.Min(roomy ? RoomyHeight : DefaultHeight, MaxHeight);
    }

    /// <summary>Centres on the monitor under the pointer before the window is first shown.</summary>
    private void PlaceOnPointerMonitor(MonitorInfo monitor)
    {
        LimitToWorkArea(monitor);
        PhysicalWindowPositioner.Move(new WindowInteropHelper(this).Handle, CenterOn(monitor, Width, Height));
    }

    /// <summary>Raises the window above the editor, library or card that opened after it, without pinning it on top.</summary>
    private void BringToFront()
    {
        if (!ShowActivated || _closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void CloseWith(WelcomeOutcome outcome) { Outcome = outcome; Close(); }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseWith(WelcomeOutcome.Closed);
    }
}
