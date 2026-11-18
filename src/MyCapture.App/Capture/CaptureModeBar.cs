using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;

namespace MyCapture.App.Capture;

/// <summary>What a click or drag in the capture selector produces.</summary>
internal enum CaptureSelectorMode
{
    /// <summary>A click takes the window (or monitor) under the pointer, a drag takes a region.</summary>
    Capture,

    /// <summary>The same click or drag, recognised as text and copied instead of saved as an image.</summary>
    Text,

    /// <summary>A click copies the colour of the frozen pixel under the pointer; nothing is captured.</summary>
    Color,

    /// <summary>A pointer-drawn outline captured with transparency.</summary>
    Freeform,

    /// <summary>
    /// A click picks the window to scroll; the selector closes and the app scrolls and stitches that
    /// window. A drag does nothing.
    /// </summary>
    Scroll,
}

/// <summary>How the mode bar draws its outer edge.</summary>
internal enum CaptureModeBarFrame
{
    /// <summary>Inside the WPF selector: rounded corners and a one-pixel chip-border edge.</summary>
    Rounded,

    /// <summary>In its own square window without DWM corners (Windows 10): a one-pixel square edge.</summary>
    Square,

    /// <summary>In its own window that DWM rounds and outlines (Windows 11): no edge of its own.</summary>
    Window,
}

/// <summary>Something the selector hands back to the app instead of capturing: it closes and the app runs it.</summary>
internal enum CaptureSelectorAction
{
    /// <summary>The capture half of the switch: the screenshot selector opens.</summary>
    Capture,

    /// <summary>The record half of the capture/record switch: the recording region selector opens.</summary>
    Record,

    /// <summary>Scrolling capture of the window at <see cref="CaptureSelectorRequest.ScreenPoint"/>.</summary>
    ScrollingCapture,

    /// <summary>The previous region again, without a selector.</summary>
    RepeatLastRegion,
}

/// <summary>
/// One request from the selector's switch, its ⋯ menu or a click in the scroll mode; the app runs its
/// existing command for it. A scrolling capture carries the clicked point in virtual-desktop pixels.
/// </summary>
internal sealed record CaptureSelectorRequest(CaptureSelectorAction Action, MyCapture.Core.Primitives.PointD? ScreenPoint = null);

/// <summary>
/// An icon-first mode bar at the top of the pointer's monitor. A capture/record switch sits beside
/// the selection modes (region, window, freeform, scroll, text, colour) and the delay and ratio locks.
/// The previous region stays in ⋯. The chosen mode is the bar's one accent-filled button; the
/// capture/record switch and the ratio and delay locks mark their state with a neutral key-cap wash
/// and hairline. The ratio and delay buttons each open a menu, and the delay button shows its seconds
/// while a delay is armed.
/// </summary>
/// <remarks>
/// The bar is drawn from the selector's capture-guide palette (all six themes and the guide colour
/// override), not from the chrome tokens, so it matches the highlight around it. Every button has a
/// tooltip, an automation name and, where one exists, its key as tooltip text and accelerator key; the
/// bar reveals those keys while Alt is held or always when that preference is enabled.
/// The bar only raises requests; the overlay view decides what they do. It
/// is never part of a capture: the selection is cropped from the frozen frame, not from what the
/// overlay draws.
/// </remarks>
internal sealed class CaptureModeBar : Border
{
    internal const string SurfaceKey = "CaptureBar.Surface";
    internal const string BorderKey = "CaptureBar.Border";
    internal const string TextKey = "CaptureBar.Text";
    internal const string MutedKey = "CaptureBar.Muted";
    internal const string AccentKey = "CaptureBar.Accent";
    internal const string AccentTextKey = "CaptureBar.AccentText";
    internal const string HoverKey = "CaptureBar.Hover";
    internal const string HairlineKey = "CaptureBar.Hairline";

    /// <summary>Button size, DIPs: 32 × 32 with a 6-DIP radius.</summary>
    internal const double ButtonSize = 32;
    internal const double ButtonRadius = 6;
    /// <summary>Space between the bar's edge and its buttons, DIPs, on every side.</summary>
    internal const double EdgeSpace = 6;
    /// <summary>Margin on each side of a button, DIPs; two neighbours are 4 DIPs apart.</summary>
    private const double ButtonGap = 2;

    /// <summary>The delays the timer button offers, in seconds; 0 is no delay.</summary>
    internal static readonly IReadOnlyList<int> DelayChoices = [0, 3, 5, 10];

    // 20x20 outline glyphs on the shared symbol grid, stroked like Icon.Glyph.
    private static readonly Geometry RegionIcon = Geometry.Parse(
        "M3.5,7 L3.5,3.5 L7,3.5 M13,3.5 L16.5,3.5 L16.5,7 M16.5,13 L16.5,16.5 L13,16.5 M7,16.5 L3.5,16.5 L3.5,13 M8,8 L12,8 L12,12 L8,12 Z");
    private static readonly Geometry FullScreenIcon = Geometry.Parse(
        "M3.25,3.75 L16.75,3.75 A1,1 0 0 1 17.75,4.75 L17.75,13 A1,1 0 0 1 16.75,14 L3.25,14 A1,1 0 0 1 2.25,13 L2.25,4.75 A1,1 0 0 1 3.25,3.75 Z M10,14 L10,16.5 M6.5,16.5 L13.5,16.5");
    private static readonly Geometry WindowIcon = Geometry.Parse(
        "M3,4 L17,4 L17,16 L3,16 Z M3,7.5 L17,7.5 M5,5.75 L5.1,5.75 M7,5.75 L7.1,5.75");
    private static readonly Geometry ScrollIcon = Geometry.Parse(
        "M5,3 L3,3 L3,17 L5,17 M15,3 L17,3 L17,17 L15,17 M10,3 L10,17 M7,6 L10,3 L13,6 M7,14 L10,17 L13,14");
    private static readonly Geometry DelayIcon = Geometry.Parse(
        "M10,5 A6,6 0 1 1 9.99,5 M7.5,2.5 L12.5,2.5 M10,2.5 L10,5 M10,8 L10,11 L12,12.5 M14.5,5.5 L16,4");
    private static readonly Geometry TextIcon = Geometry.Parse(
        "M3.5,7 L3.5,3.5 L7,3.5 M13,3.5 L16.5,3.5 L16.5,7 M16.5,13 L16.5,16.5 L13,16.5 M7,16.5 L3.5,16.5 L3.5,13 M7,7 L13,7 M10,7 L10,13.5");
    private static readonly Geometry ColorIcon = Geometry.Parse(
        "M12.4,3.6 A2.1,2.1 0 0 1 16.4,7.6 L14.6,9.4 L10.6,5.4 Z M11.6,8.4 L5.2,14.8 L4.2,16.8 L6.2,15.8 L12.6,9.4 M9.6,4.4 L15.6,10.4");
    private static readonly Geometry HelpIcon = Geometry.Parse(
        "M10,2.75 A7.25,7.25 0 1 1 9.99,2.75 M7.6,7.9 A2.45,2.45 0 1 1 10.9,10.2 C10.25,10.5 10,10.95 10,11.7 L10,12.2 M10,14.6 L10,14.7");
    private static readonly Geometry CancelIcon = Geometry.Parse("M5.25,5.25 L14.75,14.75 M14.75,5.25 L5.25,14.75");
    private static readonly Geometry CameraIcon = Geometry.Parse(
        "M3.5,6.5 L6.25,6.5 L7.5,4.5 L12.5,4.5 L13.75,6.5 L16.5,6.5 A1,1 0 0 1 17.5,7.5 L17.5,15 A1,1 0 0 1 16.5,16 L3.5,16 A1,1 0 0 1 2.5,15 L2.5,7.5 A1,1 0 0 1 3.5,6.5 Z M10,8.25 A2.75,2.75 0 1 1 9.99,8.25");
    private static readonly Geometry RecordIcon = Geometry.Parse(
        "M10,3.25 A6.75,6.75 0 1 1 9.99,3.25 M10,7.25 A2.75,2.75 0 1 1 9.99,7.25");
    private static readonly Geometry MoreIcon = Geometry.Parse(
        "M5,9 A1,1 0 1 0 4.99,9 M10,9 A1,1 0 1 0 9.99,9 M15,9 A1,1 0 1 0 14.99,9");
    // A wide frame with two opposite corner brackets: the picture of an aspect ratio.
    private static readonly Geometry RatioIcon = Geometry.Parse(
        "M2.75,5.5 L17.25,5.5 L17.25,14.5 L2.75,14.5 Z M6.5,11.25 L6.5,8.5 L9.25,8.5 M13.5,8.75 L13.5,11.5 L10.75,11.5");
    private static readonly Geometry CheckIcon = Geometry.Parse("M3.75,10.25 L8.15,14.65 L16.25,5.35");

    private readonly RadioButton _capture;
    private readonly RadioButton _window;
    private readonly RadioButton _freeform;
    private string? _freeformUnavailableReason;
    private readonly RadioButton _text;
    private readonly RadioButton _color;
    private readonly RadioButton _captureKind;
    private readonly RadioButton _recordKind;
    private readonly Button _fullScreen;
    private readonly RadioButton _scroll;
    private readonly LockToggle _delay;
    private readonly FrameworkElement _delayIcon;
    private readonly TextBlock _delaySeconds;
    private int _delayChoice;
    private CaptureSelectorMode _mode;
    private readonly LockToggle _ratio;
    private readonly Button _more;
    private readonly Button _help;
    private readonly Button _cancel;
    private readonly TextBlock _captureLabel;
    private readonly FrameworkElement _textSeparator;
    private readonly FrameworkElement _kindSwitch;
    private readonly FrameworkElement _kindSeparator;
    private readonly FrameworkElement _recordingKindIndicator;
    private ContextMenu? _moreMenu;
    private ContextMenu? _delayMenu;
    private ContextMenu? _ratioMenu;
    private SelectionRatio _ratioChoice;
    private bool _ratioShown;
    private bool _syncing;
    private bool _detectWindows = true;
    private bool _canRepeatLastRegion = true;
    private bool _recording;
    private bool _otherActions;
    private bool _compactActions;
    private readonly WrapPanel _items;

    internal CaptureModeBar()
    {
        Focusable = false;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
        UseFrame(CaptureModeBarFrame.Rounded);
        SetResourceReference(BackgroundProperty, SurfaceKey);
        SetResourceReference(BorderBrushProperty, BorderKey);
        AutomationProperties.SetName(this, UiText.Get("CaptureGuide2_BarName"));

        // ToggleButton so the ratio button, which is no radio, shares the mode buttons' template.
        ControlTemplate modeTemplate = CreateTemplate(typeof(ToggleButton), checkable: true, subtleCheck: false);
        ControlTemplate kindTemplate = CreateTemplate(typeof(RadioButton), checkable: true, subtleCheck: true);
        // The locks are states, not the chosen mode: they keep the neutral check of the switch.
        ControlTemplate lockTemplate = CreateTemplate(typeof(ToggleButton), checkable: true, subtleCheck: true);
        ControlTemplate actionTemplate = CreateTemplate(typeof(Button), checkable: false, subtleCheck: false);

        // Capture | Record. Capture is where the user already is; Record leaves for the recording selector.
        _captureKind = KindButton(CameraIcon, UiText.Get("Onboard_BarCapture"), kindTemplate);
        _captureKind.IsChecked = true;
        _captureKind.Checked += (_, _) =>
        {
            if (_syncing || !_recording) return;
            ActionRequested?.Invoke(new CaptureSelectorRequest(CaptureSelectorAction.Capture));
            ShowCaptureKind();
        };
        _recordKind = KindButton(RecordIcon, UiText.Get("Onboard_BarRecord"), kindTemplate);
        _recordKind.Checked += (_, _) =>
        {
            if (_syncing || _recording) return;
            ActionRequested?.Invoke(new CaptureSelectorRequest(CaptureSelectorAction.Record));
            ShowCaptureKind();
        };
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        kinds.Children.Add(_captureKind);
        kinds.Children.Add(_recordKind);
        AutomationProperties.SetName(kinds, UiText.Get("Onboard_BarKindName"));
        _kindSwitch = kinds;
        _kindSeparator = Divider();
        _recordingKindIndicator = ButtonContent(_recordKind, RecordIcon, null);
        _recordingKindIndicator.Width = 32;
        _recordingKindIndicator.Margin = new Thickness(0, 0, 4, 0);
        _recordingKindIndicator.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_recordingKindIndicator, UiText.Get("Onboard_BarRecord"));

        _captureLabel = new TextBlock();
        _capture = ModeButton(CaptureSelectorMode.Capture, RegionIcon, _captureLabel, modeTemplate);
        _capture.Click += (_, _) => { if (!_syncing) ModeRequested?.Invoke(CaptureSelectorMode.Capture); };
        _window = new RadioButton { Template = modeTemplate };
        Decorate(_window);
        _window.Content = ButtonContent(_window, WindowIcon, null);
        _window.Checked += (_, _) => { if (!_syncing) WindowRequested?.Invoke(); };
        _freeform = ModeButton(CaptureSelectorMode.Freeform, Geometry.Parse("M5,3 C0,3 1,12 6,14 C12,19 18,13 16,7 C15,3 10,1 5,3 Z M6,14 L4,18"), new TextBlock(), modeTemplate);
        ToolTipService.SetShowOnDisabled(_freeform, true);
        _fullScreen = ActionButton(FullScreenIcon, UiText.Get("CaptureGuide2_FullScreen"), actionTemplate);
        _scroll = ModeButton(CaptureSelectorMode.Scroll, ScrollIcon, new TextBlock(), modeTemplate);
        _delay = new LockToggle { Template = lockTemplate };
        Decorate(_delay);
        // An armed delay replaces the clock with its seconds, so the button keeps its width and the
        // bar its layout.
        _delayIcon = ButtonContent(_delay, DelayIcon, null);
        _delaySeconds = new TextBlock
        {
            FontSize = TypeScale.Caption,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _delaySeconds.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = _delay });
        var delayFace = new Grid();
        delayFace.Children.Add(_delayIcon);
        delayFace.Children.Add(_delaySeconds);
        _delay.Content = delayFace;
        _ratio = new LockToggle { Template = lockTemplate };
        Decorate(_ratio);
        _ratio.Content = ButtonContent(_ratio, RatioIcon, null);
        _text = ModeButton(CaptureSelectorMode.Text, TextIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Text") }, modeTemplate);
        _color = ModeButton(CaptureSelectorMode.Color, ColorIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Color") }, modeTemplate);
        _more = ActionButton(MoreIcon, label: null, actionTemplate);
        _help = ActionButton(HelpIcon, label: null, actionTemplate);
        _cancel = ActionButton(CancelIcon, label: null, actionTemplate);
        _fullScreen.Click += (_, _) => FullScreenRequested?.Invoke();
        _delay.Click += (_, _) => OpenDelayMenu();
        _ratio.Click += (_, _) => OpenRatioMenu();
        _more.Click += (_, _) => OpenMoreMenu();
        _help.Click += (_, _) => HelpRequested?.Invoke();
        _cancel.Click += (_, _) => CancelRequested?.Invoke();
        AutomationProperties.SetName(_more, UiText.Get("Onboard_BarMore"));
        AutomationProperties.SetName(_help, UiText.Get("CaptureGuide2_Help"));
        AutomationProperties.SetName(_cancel, UiText.Get("CaptureGuide2_Cancel"));

        _textSeparator = Divider();
        _items = new WrapPanel { Orientation = Orientation.Horizontal };
        KeyboardNavigation.SetDirectionalNavigation(_items, KeyboardNavigationMode.Cycle);
        foreach (UIElement item in new UIElement[]
                 {
                     _kindSwitch, _recordingKindIndicator, _kindSeparator, _capture, _window, _freeform, _fullScreen, _scroll, _delay,
                     _ratio, _textSeparator, _text, _color, Divider(), _more, _help, _cancel,
                 })
            _items.Children.Add(item);
        Child = _items;
        ApplyTexts();
        ShowRatio(SelectionRatio.Free);
    }

    /// <summary>A mode button was chosen by click, keyboard or UI Automation.</summary>
    internal event Action<CaptureSelectorMode>? ModeRequested;
    internal event Action? WindowRequested;

    /// <summary>The record switch or a ⋯ menu command was chosen.</summary>
    internal event Action<CaptureSelectorRequest>? ActionRequested;

    internal event Action? FullScreenRequested;

    internal event Action? HelpRequested;

    internal event Action? CancelRequested;

    /// <summary>A lock (or Free) was chosen in the ratio menu.</summary>
    internal event Action<SelectionRatio>? RatioRequested;

    /// <summary>A fixed size was chosen in the ratio menu.</summary>
    internal event Action<FixedSelectionSize>? FixedSizeRequested;

    /// <summary>A delay (0 for none) was chosen in the delay menu.</summary>
    internal event Action<int>? DelayRequested;

    internal RadioButton CaptureButton => _capture;
    internal RadioButton WindowButton => _window;
    internal RadioButton FreeformButton => _freeform;

    internal RadioButton TextButton => _text;

    internal RadioButton ColorButton => _color;

    internal RadioButton CaptureKindButton => _captureKind;

    internal RadioButton RecordKindButton => _recordKind;

    internal Button FullScreenButton => _fullScreen;
    internal RadioButton ScrollButton => _scroll;

    /// <summary>Opens the delay menu; checked while a delay is armed.</summary>
    internal ToggleButton DelayButton => _delay;

    /// <summary>The delay menu while it is open.</summary>
    internal ContextMenu? DelayMenuForTest => _delayMenu;

    /// <summary>Opens the ratio menu; checked while a ratio is locked.</summary>
    internal ToggleButton RatioButton => _ratio;

    internal Button MoreButton => _more;

    internal Button HelpButton => _help;

    internal Button CancelButton => _cancel;

    /// <summary>The ⋯ menu while it is open.</summary>
    internal ContextMenu? MoreMenuForTest => _moreMenu;

    /// <summary>The ratio menu while it is open.</summary>
    internal ContextMenu? RatioMenuForTest => _ratioMenu;

    /// <summary>
    /// Shows only what the owner enabled. Without window detection the first mode is a plain region;
    /// text and colour need their owners' handlers; the capture/record switch and the ⋯ menu need an
    /// owner that runs their commands; the ratio button needs a selector whose size is not already fixed
    /// by a setting.
    /// </summary>
    internal void Configure(bool detectWindows, bool textAvailable, bool colorAvailable, bool otherActions = false,
        bool canRepeatLastRegion = true, bool recording = false, bool ratioAvailable = true,
        string? freeformUnavailableReason = null)
    {
        _freeformUnavailableReason = freeformUnavailableReason ?? (recording ? UiText.Get("CaptureFreeform_Recording") : null);
        _freeform.IsEnabled = _freeformUnavailableReason is null;
        _recording = recording;
        _otherActions = otherActions;
        _compactActions = false;
        _ratio.Visibility = ratioAvailable ? Visibility.Visible : Visibility.Collapsed;
        _detectWindows = detectWindows;
        _canRepeatLastRegion = canRepeatLastRegion;
        _window.Visibility = detectWindows && !recording ? Visibility.Visible : Visibility.Collapsed;
        _text.Visibility = textAvailable && !recording ? Visibility.Visible : Visibility.Collapsed;
        _color.Visibility = colorAvailable && !recording ? Visibility.Visible : Visibility.Collapsed;
        _textSeparator.Visibility = (textAvailable || colorAvailable) && !recording ? Visibility.Visible : Visibility.Collapsed;
        Visibility others = otherActions && !recording ? Visibility.Visible : Visibility.Collapsed;
        _kindSwitch.Visibility = _kindSeparator.Visibility = otherActions ? Visibility.Visible : Visibility.Collapsed;
        _more.Visibility = others;
        _scroll.Visibility = _delay.Visibility = others;
        // Recording currently has a geometry-only owner. Identify its kind without offering a
        // switch whose command that owner cannot run.
        _recordingKindIndicator.Visibility = recording && !otherActions ? Visibility.Visible : Visibility.Collapsed;
        ShowCaptureKind();
        ApplyTexts();
    }

    /// <summary>Uses the existing more menu for secondary commands on a narrow monitor.</summary>
    internal void ConstrainWidth(double availableWidth)
    {
        // All command faces have fixed widths. Count them before layout so narrowing the bar
        // needs only its normal measure pass; menus are still created only when opened.
        double chromeWidth = Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right;
        double fullWidth = chromeWidth;
        bool secondaryActions = _otherActions && !_recording;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 0; i < _items.Children.Count; i++)
        {
            var item = (FrameworkElement)_items.Children[i];
            bool secondary = ReferenceEquals(item, _scroll) || ReferenceEquals(item, _delay);
            if (secondary ? !secondaryActions : item.Visibility == Visibility.Collapsed) continue;
            fullWidth += ReferenceEquals(item, _kindSwitch)
                ? LaidOutWidth(_captureKind, dpi) + LaidOutWidth(_recordKind, dpi)
                : LaidOutWidth(item, dpi);
        }

        _compactActions = secondaryActions && fullWidth > availableWidth;
        Visibility visibility = secondaryActions && !_compactActions ? Visibility.Visible : Visibility.Collapsed;
        if (_scroll.Visibility != visibility) _scroll.Visibility = visibility;
        if (_delay.Visibility != visibility) _delay.Visibility = visibility;
        double contentWidth = fullWidth - chromeWidth;
        if (_compactActions)
            contentWidth -= LaidOutWidth(_scroll, dpi) + LaidOutWidth(_delay, dpi);
        // Keep the wrapping boundary on the layout pixel grid, rounded up. At 150% a natural
        // 527-DIP row otherwise rounds down to 526 2/3 during arrange and wraps its last target
        // below the height measured for one row. Narrower displays still wrap within the bar.
        double width = Math.Min(contentWidth, Math.Max(0, availableWidth - chromeWidth));
        _items.Width = Math.Ceiling((width * dpi) - 0.0001) / dpi;
    }

    /// <summary>
    /// The width <paramref name="item"/> takes in the row: layout rounding puts its width and its
    /// margins on the pixel grid separately. Counted from the DIP values instead, the row came out
    /// narrower than its commands at 150% (each 1-DIP divider takes 2 px) and Close wrapped below
    /// the bar, and 7 DIPs wider at 125% (each 36-DIP face with its 2-DIP margins takes 47 px),
    /// which left a gap after Close.
    /// </summary>
    private double LaidOutWidth(FrameworkElement item, double dpi) =>
        Laid(item.Width, dpi) + Laid(item.Margin.Left + item.Margin.Right, dpi);

    // The same rounding WPF's layout applies to a size and to a margin, which is no rounding at all
    // when the bar does not round its layout.
    private double Laid(double value, double dpi) => UseLayoutRounding ? Math.Round(value * dpi) / dpi : value;

    /// <summary>Fills in the current mode without raising <see cref="ModeRequested"/>.</summary>
    internal void ShowMode(CaptureSelectorMode mode, bool windowSelected = false)
    {
        _syncing = true;
        try
        {
            _capture.IsChecked = mode == CaptureSelectorMode.Capture && !windowSelected;
            _window.IsChecked = mode == CaptureSelectorMode.Capture && windowSelected;
            _freeform.IsChecked = mode == CaptureSelectorMode.Freeform;
            _text.IsChecked = mode == CaptureSelectorMode.Text;
            _color.IsChecked = mode == CaptureSelectorMode.Color;
            _scroll.IsChecked = mode == CaptureSelectorMode.Scroll;
        }
        finally { _syncing = false; }
        _mode = mode;
        // The colour and scroll modes pick a pixel or a window, not a region: nothing for a ratio to apply to.
        _ratio.IsEnabled = mode is not (CaptureSelectorMode.Color or CaptureSelectorMode.Freeform or CaptureSelectorMode.Scroll);
        // A delay waits before an image capture; text, colour and scrolling have their own flows.
        _delay.IsEnabled = DelayApplies(mode);
    }

    /// <summary>True for the modes an armed delay applies to.</summary>
    internal static bool DelayApplies(CaptureSelectorMode mode) =>
        mode is CaptureSelectorMode.Capture or CaptureSelectorMode.Freeform;

    /// <summary>Fills in the armed delay (0 for none), without raising <see cref="DelayRequested"/>.</summary>
    internal void ShowDelay(int seconds)
    {
        _delayChoice = Math.Max(0, seconds);
        _delay.IsChecked = _delayChoice > 0;
        _delayIcon.Visibility = _delayChoice > 0 ? Visibility.Collapsed : Visibility.Visible;
        _delaySeconds.Visibility = _delayChoice > 0 ? Visibility.Visible : Visibility.Collapsed;
        _delaySeconds.Text = _delayChoice > 0 ? UiText.Format("CaptureBar_DelayArmed", _delayChoice) : string.Empty;
        AutomationProperties.SetItemStatus(_delay, DelayName(_delayChoice));
    }

    /// <summary>The armed delay shown by the button, in seconds.</summary>
    internal int DelayShown => _delayChoice;

    internal static string DelayName(int seconds) =>
        seconds <= 0 ? UiText.Get("CaptureBar_DelayNone") : UiText.Format("Onboard_DelayCapture", seconds);

    /// <summary>Fills in the current lock, without raising <see cref="RatioRequested"/>.</summary>
    internal void ShowRatio(SelectionRatio ratio)
    {
        // The selector repeats this after every configuration change; only a new lock needs any work.
        if (_ratioShown && _ratioChoice == ratio) return;
        _ratioShown = true;
        _ratioChoice = ratio;
        _ratio.IsChecked = ratio != SelectionRatio.Free;
        // Screen readers hear the current lock with the button; sighted users see it in the menu and the readout.
        AutomationProperties.SetItemStatus(_ratio, RatioName(ratio));
    }

    internal static string RatioName(SelectionRatio ratio) =>
        ratio == SelectionRatio.Free ? UiText.Get("Crop.Free") : SelectionRatios.Label(ratio);

    /// <summary>Takes the selector's guide brushes, so the bar follows the theme and the guide colour.</summary>
    internal void ApplyPalette(IReadOnlyDictionary<string, Brush> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Resources[SurfaceKey] = palette["CaptureGuide.ChipSurface"];
        Resources[BorderKey] = palette["CaptureGuide.ChipBorder"];
        Resources[HairlineKey] = palette["CaptureGuide.ChipHairline"];
        Resources[TextKey] = palette["CaptureGuide.Text"];
        Resources[MutedKey] = palette["CaptureGuide.MutedText"];
        Resources[AccentKey] = palette["CaptureGuide.SelectionBorder"];
        Resources[AccentTextKey] = palette["CaptureGuide.AccentText"];
        // Hover lifts a button to the key-cap surface. High contrast maps that surface to the chip's
        // own Window colour, where a wash would vanish; there the text colour at 10% marks it.
        Brush hover = palette["CaptureGuide.KeycapSurface"];
        if (SameColor(hover, palette["CaptureGuide.ChipSurface"]))
        {
            hover = palette["CaptureGuide.Text"].Clone();
            hover.Opacity = 0.10;
            hover.Freeze();
        }
        Resources[HoverKey] = hover;
        // The overlay has its own readable palette. Resolve the standard foreground token locally
        // so anything inside the bar that asks for it reads on the bar's surface.
        Resources["Text.Primary"] = palette["CaptureGuide.Text"];
    }

    private static bool SameColor(Brush first, Brush second) =>
        first is SolidColorBrush a && second is SolidColorBrush b && a.Color == b.Color && a.Opacity == b.Opacity;

    /// <summary>
    /// Draws the bar's edge for where it is shown (see <see cref="CaptureModeBarFrame"/>).
    /// <paramref name="pixel"/> is one physical pixel in the bar's DIPs, so a square edge stays one
    /// pixel wide. Every frame keeps the same outer size: the edge takes its width from the padding.
    /// </summary>
    internal void UseFrame(CaptureModeBarFrame frame, double pixel = 1)
    {
        Frame = frame;
        double edge = frame switch
        {
            CaptureModeBarFrame.Window => 0,
            CaptureModeBarFrame.Square => pixel > 0 ? Math.Min(1, pixel) : 1,
            _ => 1,
        };
        CornerRadius = new CornerRadius(frame == CaptureModeBarFrame.Rounded ? 8 : 0);
        BorderThickness = new Thickness(edge);
        Padding = new Thickness(EdgeSpace - ButtonGap - edge, EdgeSpace - edge, EdgeSpace - ButtonGap - edge, EdgeSpace - edge);
    }

    /// <summary>The edge the bar draws now.</summary>
    internal CaptureModeBarFrame Frame { get; private set; }

    /// <summary>True for this bar and anything inside it.</summary>
    internal bool Holds(DependencyObject? element)
    {
        for (DependencyObject? current = element; current is not null; current = Parent(current))
            if (ReferenceEquals(current, this)) return true;
        return false;

        static DependencyObject? Parent(DependencyObject child) =>
            child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
    }

    /// <summary>
    /// The ⋯ menu keeps the previous-region command. Delay and scrolling have direct buttons; on a
    /// narrow monitor the menu takes them too, as the same mode and the same delay choices.
    /// </summary>
    internal ContextMenu CreateMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _more, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        AutomationProperties.SetName(menu, UiText.Get("Onboard_BarMore"));
        if (_compactActions)
        {
            string scrollName = UiText.Get("Text_4DBA30197C0A");
            var scroll = new MenuItem
            {
                Header = scrollName, MinHeight = 36, IsCheckable = true, IsChecked = _mode == CaptureSelectorMode.Scroll,
            };
            if (scroll.IsChecked) scroll.Icon = CheckGlyph();
            AutomationProperties.SetName(scroll, scrollName);
            scroll.Click += (_, _) =>
            {
                menu.IsOpen = false;
                ModeRequested?.Invoke(CaptureSelectorMode.Scroll);
            };
            menu.Items.Add(scroll);
            var delay = new MenuItem { Header = UiText.Get("CaptureBar_Delay"), MinHeight = 36, IsEnabled = DelayApplies(_mode) };
            AutomationProperties.SetName(delay, UiText.Get("CaptureBar_Delay"));
            AddDelayItems(delay.Items, menu);
            menu.Items.Add(delay);
            menu.Items.Add(new Separator());
        }
        MenuItem repeat = MenuItemFor(menu, UiText.Get("Text_878A28AF568C"), new CaptureSelectorRequest(CaptureSelectorAction.RepeatLastRegion));
        repeat.IsEnabled = _canRepeatLastRegion;
        menu.Items.Add(repeat);
        return menu;
    }

    internal ContextMenu CreateDelayMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _delay, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        AutomationProperties.SetName(menu, UiText.Get("CaptureBar_Delay"));
        AddDelayItems(menu.Items, menu);
        return menu;
    }

    /// <summary>No delay and the timed choices, the armed one checked; a choice closes the menu.</summary>
    private void AddDelayItems(ItemCollection items, ContextMenu menu)
    {
        foreach (int seconds in DelayChoices)
        {
            string header = DelayName(seconds);
            var item = new MenuItem { Header = header, MinHeight = 36, IsCheckable = true, IsChecked = seconds == _delayChoice };
            if (item.IsChecked) item.Icon = CheckGlyph();
            AutomationProperties.SetName(item, header);
            int picked = seconds;
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                DelayRequested?.Invoke(picked);
            };
            items.Add(item);
        }
    }

    /// <summary>
    /// The ratio menu: Free and the four locks (the current one checked), then the sizes the selector can
    /// place. A choice closes the menu and is raised for the owner to apply.
    /// </summary>
    internal ContextMenu CreateRatioMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _ratio, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        AutomationProperties.SetName(menu, UiText.Get("Crop.Ratio"));
        foreach (SelectionRatio choice in SelectionRatios.All)
        {
            string header = RatioName(choice);
            var item = new MenuItem { Header = header, MinHeight = 36, IsCheckable = true, IsChecked = choice == _ratioChoice };
            if (item.IsChecked) item.Icon = CheckGlyph();
            AutomationProperties.SetName(item, header);
            SelectionRatio picked = choice;
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                RatioRequested?.Invoke(picked);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        foreach (FixedSelectionSize size in SelectionRatios.FixedSizes)
        {
            string header = size.ToString();
            var item = new MenuItem { Header = header, MinHeight = 36 };
            AutomationProperties.SetName(item, UiText.Format("CaptureRatio_FixedSize", header));
            FixedSelectionSize picked = size;
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                FixedSizeRequested?.Invoke(picked);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private static SymbolIcon CheckGlyph()
    {
        SymbolIcon icon = SymbolIcon.Create(CheckIcon, SymbolIcon.InlineSize);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Primary");
        return icon;
    }

    private void OpenRatioMenu()
    {
        if (_ratioMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateRatioMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_ratioMenu, menu)) _ratioMenu = null; };
        _ratioMenu = menu;
        menu.IsOpen = true;
    }

    internal void DismissMenus()
    {
        if (_ratioMenu is { } ratio) ratio.IsOpen = false;
        if (_delayMenu is { } delay) delay.IsOpen = false;
        if (_moreMenu is { } more) more.IsOpen = false;
    }

    private void OpenDelayMenu()
    {
        if (_delayMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateDelayMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_delayMenu, menu)) _delayMenu = null; };
        _delayMenu = menu;
        menu.IsOpen = true;
    }

    private void OpenMoreMenu()
    {
        if (_moreMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateMoreMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_moreMenu, menu)) _moreMenu = null; };
        _moreMenu = menu;
        menu.IsOpen = true;
    }

    private MenuItem MenuItemFor(ContextMenu menu, string header, CaptureSelectorRequest request)
    {
        var item = new MenuItem { Header = header, MinHeight = 36 };
        AutomationProperties.SetName(item, header);
        item.Click += (_, _) =>
        {
            menu.IsOpen = false;
            ActionRequested?.Invoke(request);
        };
        return item;
    }

    private void ShowCaptureKind()
    {
        _syncing = true;
        try
        {
            _captureKind.IsChecked = !_recording;
            _recordKind.IsChecked = _recording;
        }
        finally { _syncing = false; }
    }

    private void ApplyTexts()
    {
        _captureLabel.Text = UiText.Get(_detectWindows ? "CaptureGuide2_Region" : "CaptureGuide2_RegionOnly");
        AutomationProperties.SetName(_capture, _captureLabel.Text);
        Describe(_capture, _recording ? "CaptureBar_RecordRegionHelp" : "CaptureGuide2_RegionOnlyHelp", key: null);
        AutomationProperties.SetName(_window, UiText.Get("CaptureBar_Window"));
        Describe(_window, "CaptureBar_WindowHelp", key: null);
        AutomationProperties.SetName(_freeform, UiText.Get("CaptureFreeform_Name"));
        Describe(_freeform, "CaptureFreeform_Help", "F");
        if (_freeformUnavailableReason is { } reason)
        {
            _freeform.ToolTip = reason;
            AutomationProperties.SetHelpText(_freeform, reason);
        }
        AutomationProperties.SetName(_fullScreen, UiText.Get("CaptureGuide2_FullScreen"));
        Describe(_fullScreen, _recording ? "CaptureBar_RecordFullScreenHelp" : "CaptureGuide2_FullScreenHelp", key: null);
        AutomationProperties.SetName(_scroll, UiText.Get("Text_4DBA30197C0A"));
        Describe(_scroll, "CaptureBar_ScrollHelp", "S");
        AutomationProperties.SetName(_delay, UiText.Get("CaptureBar_Delay"));
        Describe(_delay, "CaptureBar_DelayHelp", "D");
        AutomationProperties.SetName(_ratio, UiText.Get("Crop.Ratio"));
        Describe(_ratio, "CaptureRatio_Help", "R");
        AutomationProperties.SetName(_text, UiText.Get("CaptureGuide2_Text"));
        Describe(_text, "CaptureGuide2_TextHelp", "T");
        AutomationProperties.SetName(_color, UiText.Get("CaptureGuide2_Color"));
        Describe(_color, "CaptureGuide2_ColorHelp", "C");
        Describe(_captureKind, "Onboard_BarCaptureHelp", key: null);
        Describe(_recordKind, "Onboard_BarRecordHelp", key: null);
        Describe(_more, "Onboard_BarMoreHelp", key: null);
        Describe(_help, "CaptureGuide2_HelpTip", key: null);
        Describe(_cancel, "CaptureGuide2_CancelTip", "Esc");
    }

    /// <summary>Tooltip and automation help, plus the shared shortcut caption for assigned keys.</summary>
    private static void Describe(ButtonBase button, string helpKey, string? key)
    {
        string help = UiText.Get(helpKey);
        AutomationProperties.SetAcceleratorKey(button, key ?? string.Empty);
        string tip = key is not null ? $"{help} ({key})" : help;
        AutomationProperties.SetHelpText(button, tip);
        string name = AutomationProperties.GetName(button);
        button.ToolTip = string.IsNullOrEmpty(name) ? tip : $"{name}\n{tip}";
        CommandButtonPresentation.RefreshHint(button);
    }

    /// <summary>Routes Enter through the same activation as a mouse/Space click.</summary>
    internal void ActivateButton(ButtonBase button)
    {
        if (!button.IsEnabled) return;
        if (button is RegionModeButton region)
        {
            region.Activate();
            return;
        }
        if (button is RadioButton radio) radio.IsChecked = true;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    // Shows the ratio lock like a mode button but never flips itself: the selector sets IsChecked from its state.
    private sealed class LockToggle : ToggleButton
    {
        internal void Activate() => OnClick();

        // This opens a menu; the selected ratio is a separate status, not a toggle action.
        protected override AutomationPeer OnCreateAutomationPeer() => new LockMenuAutomationPeer(this);

        protected override void OnToggle()
        {
        }
    }

    private sealed class LockMenuAutomationPeer(LockToggle owner) : ButtonBaseAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(Button);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        void IInvokeProvider.Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            var button = (LockToggle)Owner;
            _ = button.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (button.IsEnabled) button.Activate();
            }));
        }
    }

    // Region can be requested again when already selected. Its Click owns click activation;
    // Checked owns independent UIA selection, so one activation never requests the mode twice.
    private sealed class RegionModeButton : RadioButton
    {
        internal bool IsClickActivation { get; private set; }
        internal void Activate() => OnClick();

        protected override void OnClick()
        {
            IsClickActivation = true;
            try { base.OnClick(); }
            finally { IsClickActivation = false; }
        }
    }

    private RadioButton ModeButton(CaptureSelectorMode mode, Geometry icon, TextBlock label, ControlTemplate template)
    {
        RadioButton button = mode == CaptureSelectorMode.Capture ? new RegionModeButton() : new RadioButton();
        button.Template = template;
        Decorate(button);
        button.Content = ButtonContent(button, icon, label);
        button.Checked += (_, _) =>
        {
            if (!_syncing && button is not RegionModeButton { IsClickActivation: true }) ModeRequested?.Invoke(mode);
        };
        return button;
    }

    private static RadioButton KindButton(Geometry icon, string label, ControlTemplate template)
    {
        var button = new RadioButton { Template = template };
        Decorate(button);
        button.Content = ButtonContent(button, icon, new TextBlock { Text = label });
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Button ActionButton(Geometry icon, string? label, ControlTemplate template)
    {
        var button = new Button { Template = template };
        Decorate(button);
        button.Content = ButtonContent(button, icon, label is null ? null : new TextBlock { Text = label });
        return button;
    }

    private static void Decorate(ButtonBase button)
    {
        CommandButtonPresentation.Apply(button, compact: true);
        button.Width = button.MinWidth = ButtonSize;
        button.Height = button.MinHeight = ButtonSize;
        // The shared face is 34 DIPs tall: the icon row and the shortcut row Alt reveals. It keeps
        // its whole height, one DIP past the 32-DIP button's top and bottom inside the bar's
        // padding, so neither row is clipped; at rest the icon sits in the button's centre.
        button.Padding = new Thickness(1, -1, 1, -1);
        button.Margin = new Thickness(ButtonGap, 0, ButtonGap, 0);
        button.Cursor = Cursors.Hand;
        button.FocusVisualStyle = null;
        button.Background = Brushes.Transparent;
        // Foreground stays in the readable guide text colour across every state.
        var style = new Style(button.GetType());
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TextKey)));
        button.Style = style;
        ToolTipService.SetInitialShowDelay(button, 400);
    }

    private static FrameworkElement ButtonContent(ButtonBase owner, Geometry icon, TextBlock? label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new SymbolIcon
        {
            Size = CommandButtonPresentation.IconSize,
            Data = icon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetBinding(SymbolIcon.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
        panel.Children.Add(glyph);
        return panel;
    }

    private static FrameworkElement Divider()
    {
        // Half the button's height: centred, it starts 8 DIPs down, a whole pixel at 125, 150 and 175 %.
        var line = new Rectangle
        {
            Width = 1,
            Height = ButtonSize / 2,
            Margin = new Thickness(4, 0, 4, 0),
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        line.SetResourceReference(Shape.FillProperty, MutedKey);
        return line;
    }

    /// <summary>
    /// Chrome, a key-cap hover wash, the checked layer and an inset focus ring. A mode button's
    /// check is the accent fill with on-accent icon and text, the bar's one accent; the switch and
    /// the locks (<paramref name="subtleCheck"/>) keep the key-cap wash with a hairline edge.
    /// Washes follow FluidMotion; state and focus take effect immediately without changing layout.
    /// </summary>
    private static ControlTemplate CreateTemplate(Type type, bool checkable, bool subtleCheck)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(ButtonRadius));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var hoverLayer = StateLayer("HoverLayer", MotionSpeed.Fast);
        hoverLayer.SetResourceReference(Border.BackgroundProperty, HoverKey);
        FrameworkElementFactory? checkedLayer = null;
        if (checkable)
        {
            checkedLayer = StateLayer("CheckedLayer", MotionSpeed.Normal);
            if (subtleCheck)
            {
                checkedLayer.SetResourceReference(Border.BackgroundProperty, HoverKey);
                checkedLayer.SetResourceReference(Border.BorderBrushProperty, HairlineKey);
            }
            else checkedLayer.SetResourceReference(Border.BackgroundProperty, "Accent.Default");
        }

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var ring = new FrameworkElementFactory(typeof(Border), "FocusRing");
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        ring.SetValue(UIElement.OpacityProperty, 0.0);
        ring.SetResourceReference(Border.BorderBrushProperty, TextKey);
        var root = new FrameworkElementFactory(typeof(Grid));
        root.AppendChild(chrome);
        // Hover sits under the check, so the chosen mode keeps its fill under the pointer.
        root.AppendChild(hoverLayer);
        if (checkedLayer is not null) root.AppendChild(checkedLayer);
        root.AppendChild(presenter);
        root.AppendChild(ring);

        var template = new ControlTemplate(type) { VisualTree = root };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "HoverLayer"));
        template.Triggers.Add(hover);
        // Pressed is a little quieter than hover, as Windows' own subtle buttons are.
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(FluidMotion.FadeToProperty, 0.7, "HoverLayer"));
        template.Triggers.Add(pressed);
        if (checkable)
        {
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "CheckedLayer"));
            if (!subtleCheck)
            {
                on.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text.OnAccent")));
                template.Triggers.Add(on);
                // The accent's own hover and pressed steps, which only darken it.
                template.Triggers.Add(CheckedState(UIElement.IsMouseOverProperty, new DynamicResourceExtension("Accent.Hover")));
                template.Triggers.Add(CheckedState(ButtonBase.IsPressedProperty, new DynamicResourceExtension("Accent.Pressed")));
            }
            else template.Triggers.Add(on);
        }

        var focus = new MultiTrigger();
        focus.Conditions.Add(new System.Windows.Condition(UIElement.IsKeyboardFocusedProperty, true));
        focus.Conditions.Add(new System.Windows.Condition(KeyboardFocusCue.IsVisibleProperty, true));
        focus.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "FocusRing"));
        template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.46));
        template.Triggers.Add(disabled);
        template.Seal();
        return template;

        static MultiTrigger CheckedState(DependencyProperty state, DynamicResourceExtension fill)
        {
            var trigger = new MultiTrigger();
            trigger.Conditions.Add(new System.Windows.Condition(ToggleButton.IsCheckedProperty, true));
            trigger.Conditions.Add(new System.Windows.Condition(state, true));
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, fill, "CheckedLayer"));
            return trigger;
        }
    }

    /// <summary>A hidden, hit-test-transparent wash with the chrome's shape that fades in on a state.</summary>
    private static FrameworkElementFactory StateLayer(string name, MotionSpeed speed)
    {
        var layer = new FrameworkElementFactory(typeof(Border), name);
        layer.SetValue(Border.CornerRadiusProperty, new CornerRadius(ButtonRadius));
        layer.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        layer.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        layer.SetValue(UIElement.OpacityProperty, 0.0);
        layer.SetValue(UIElement.IsHitTestVisibleProperty, false);
        layer.SetValue(FluidMotion.TransitionProperty, speed);
        return layer;
    }
}
