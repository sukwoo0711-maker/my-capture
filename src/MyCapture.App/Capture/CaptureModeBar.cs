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
}

/// <summary>Something the selector hands back to the app instead of capturing: it closes and the app runs it.</summary>
internal enum CaptureSelectorAction
{
    /// <summary>The capture half of the switch: the screenshot selector opens.</summary>
    Capture,

    /// <summary>The record half of the capture/record switch: the recording region selector opens.</summary>
    Record,

    /// <summary>A delayed region capture after <see cref="CaptureSelectorRequest.DelaySeconds"/>.</summary>
    DelayedCapture,

    /// <summary>Scrolling capture of the window under the pointer.</summary>
    ScrollingCapture,

    /// <summary>The previous region again, without a selector.</summary>
    RepeatLastRegion,
}

/// <summary>One request from the selector's switch or ⋯ menu; the app runs its existing command for it.</summary>
internal sealed record CaptureSelectorRequest(CaptureSelectorAction Action, int DelaySeconds = 0);

/// <summary>
/// An icon-first mode bar at the top of the pointer's monitor. A capture/record switch sits beside
/// the available selection targets, delay and scrolling commands. The previous region stays in ⋯.
/// A neutral wash and a text-coloured underline identify the selected kind and mode.
/// The ratio button opens a menu of aspect-ratio locks and fixed sizes; it carries the same wash and
/// underline while a ratio is locked.
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

    /// <summary>The delays the timer button offers, in seconds.</summary>
    internal static readonly IReadOnlyList<int> DelayChoices = [3, 5, 10];

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
    private readonly RadioButton _text;
    private readonly RadioButton _color;
    private readonly RadioButton _captureKind;
    private readonly RadioButton _recordKind;
    private readonly Button _fullScreen;
    private readonly Button _scroll;
    private readonly Button _delay;
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
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(4);
        SetResourceReference(BackgroundProperty, SurfaceKey);
        SetResourceReference(BorderBrushProperty, BorderKey);
        AutomationProperties.SetName(this, UiText.Get("CaptureGuide2_BarName"));

        // ToggleButton so the ratio button, which is no radio, shares the mode buttons' template.
        ControlTemplate modeTemplate = CreateTemplate(typeof(ToggleButton), checkable: true, subtleCheck: false);
        ControlTemplate kindTemplate = CreateTemplate(typeof(RadioButton), checkable: true, subtleCheck: true);
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
        _fullScreen = ActionButton(FullScreenIcon, UiText.Get("CaptureGuide2_FullScreen"), actionTemplate);
        _scroll = ActionButton(ScrollIcon, null, actionTemplate);
        _delay = ActionButton(DelayIcon, null, actionTemplate);
        _ratio = new LockToggle { Template = modeTemplate };
        Decorate(_ratio);
        _ratio.Content = ButtonContent(_ratio, RatioIcon, null);
        _text = ModeButton(CaptureSelectorMode.Text, TextIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Text") }, modeTemplate);
        _color = ModeButton(CaptureSelectorMode.Color, ColorIcon, new TextBlock { Text = UiText.Get("CaptureGuide2_Color") }, modeTemplate);
        _more = ActionButton(MoreIcon, label: null, actionTemplate);
        _help = ActionButton(HelpIcon, label: null, actionTemplate);
        _cancel = ActionButton(CancelIcon, label: null, actionTemplate);
        _fullScreen.Click += (_, _) => FullScreenRequested?.Invoke();
        _scroll.Click += (_, _) => ActionRequested?.Invoke(new CaptureSelectorRequest(CaptureSelectorAction.ScrollingCapture));
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
                     _kindSwitch, _recordingKindIndicator, _kindSeparator, _capture, _window, _fullScreen, _scroll, _delay,
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

    internal RadioButton CaptureButton => _capture;
    internal RadioButton WindowButton => _window;

    internal RadioButton TextButton => _text;

    internal RadioButton ColorButton => _color;

    internal RadioButton CaptureKindButton => _captureKind;

    internal RadioButton RecordKindButton => _recordKind;

    internal Button FullScreenButton => _fullScreen;
    internal Button ScrollButton => _scroll;
    internal Button DelayButton => _delay;

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
        bool canRepeatLastRegion = true, bool recording = false, bool ratioAvailable = true)
    {
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
        for (int i = 0; i < _items.Children.Count; i++)
        {
            var item = (FrameworkElement)_items.Children[i];
            bool secondary = ReferenceEquals(item, _scroll) || ReferenceEquals(item, _delay);
            if (secondary ? !secondaryActions : item.Visibility == Visibility.Collapsed) continue;
            fullWidth += ReferenceEquals(item, _kindSwitch)
                ? _captureKind.Width + _captureKind.Margin.Left + _captureKind.Margin.Right
                    + _recordKind.Width + _recordKind.Margin.Left + _recordKind.Margin.Right
                : item.Width + item.Margin.Left + item.Margin.Right;
        }

        _compactActions = secondaryActions && fullWidth > availableWidth;
        Visibility visibility = secondaryActions && !_compactActions ? Visibility.Visible : Visibility.Collapsed;
        if (_scroll.Visibility != visibility) _scroll.Visibility = visibility;
        if (_delay.Visibility != visibility) _delay.Visibility = visibility;
        double contentWidth = fullWidth - chromeWidth;
        if (_compactActions)
            contentWidth -= _scroll.Width + _scroll.Margin.Left + _scroll.Margin.Right
                + _delay.Width + _delay.Margin.Left + _delay.Margin.Right;
        // Keep the wrapping boundary on the layout pixel grid, rounded up. At 150% a natural
        // 527-DIP row otherwise rounds down to 526 2/3 during arrange and wraps its last target
        // below the height measured for one row. Narrower displays still wrap within the bar.
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = Math.Min(contentWidth, Math.Max(0, availableWidth - chromeWidth));
        _items.Width = Math.Ceiling(width * dpi) / dpi;
    }

    /// <summary>Fills in the current mode without raising <see cref="ModeRequested"/>.</summary>
    internal void ShowMode(CaptureSelectorMode mode, bool windowSelected = false)
    {
        _syncing = true;
        try
        {
            _capture.IsChecked = mode == CaptureSelectorMode.Capture && !windowSelected;
            _window.IsChecked = mode == CaptureSelectorMode.Capture && windowSelected;
            _text.IsChecked = mode == CaptureSelectorMode.Text;
            _color.IsChecked = mode == CaptureSelectorMode.Color;
        }
        finally { _syncing = false; }
        // The colour mode picks a pixel, not a region: nothing for a ratio to apply to.
        _ratio.IsEnabled = mode != CaptureSelectorMode.Color;
    }

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
        Resources[TextKey] = palette["CaptureGuide.Text"];
        Resources[MutedKey] = palette["CaptureGuide.MutedText"];
        Resources[AccentKey] = palette["CaptureGuide.SelectionBorder"];
        Resources[AccentTextKey] = palette["CaptureGuide.AccentText"];
        Brush neutral = palette["CaptureGuide.Text"].Clone();
        neutral.Opacity = 0.10;
        neutral.Freeze();
        Resources[HoverKey] = neutral;
        // The overlay has its own readable palette. Resolve the standard foreground token
        // locally so the 2-DIP underline remains readable with custom capture-guide colours.
        Resources["Text.Primary"] = palette["CaptureGuide.Text"];
    }

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
    /// The ⋯ menu keeps the previous-region command. Delay and scrolling have direct buttons.
    /// </summary>
    internal ContextMenu CreateMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _more, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        AutomationProperties.SetName(menu, UiText.Get("Onboard_BarMore"));
        if (_compactActions)
        {
            menu.Items.Add(MenuItemFor(menu, UiText.Get("Text_4DBA30197C0A"),
                new CaptureSelectorRequest(CaptureSelectorAction.ScrollingCapture)));
            var delay = new MenuItem { Header = UiText.Get("CaptureBar_Delay"), MinHeight = 36 };
            AutomationProperties.SetName(delay, UiText.Get("CaptureBar_Delay"));
            foreach (int seconds in DelayChoices)
                delay.Items.Add(MenuItemFor(menu, UiText.Format("Onboard_DelayCapture", seconds),
                    new CaptureSelectorRequest(CaptureSelectorAction.DelayedCapture, seconds)));
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
        foreach (int seconds in DelayChoices)
            menu.Items.Add(MenuItemFor(menu, UiText.Format("Onboard_DelayCapture", seconds),
                new CaptureSelectorRequest(CaptureSelectorAction.DelayedCapture, seconds)));
        return menu;
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
        AutomationProperties.SetName(_fullScreen, UiText.Get("CaptureGuide2_FullScreen"));
        Describe(_fullScreen, _recording ? "CaptureBar_RecordFullScreenHelp" : "CaptureGuide2_FullScreenHelp", key: null);
        AutomationProperties.SetName(_scroll, UiText.Get("Text_4DBA30197C0A"));
        Describe(_scroll, "CaptureBar_ScrollHelp", key: null);
        AutomationProperties.SetName(_delay, UiText.Get("CaptureBar_Delay"));
        Describe(_delay, "CaptureBar_DelayHelp", key: null);
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
        button.Width = button.MinWidth = 36;
        button.Height = button.MinHeight = 36;
        // The shared face needs all 34 DIPs inside the 36-DIP button's border.
        button.Padding = new Thickness(2, 0, 2, 0);
        button.Margin = new Thickness(1, 0, 1, 0);
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
        var line = new Rectangle
        {
            Width = 1,
            Height = 20,
            Margin = new Thickness(5, 0, 5, 0),
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        line.SetResourceReference(Shape.FillProperty, MutedKey);
        return line;
    }

    /// <summary>
    /// Chrome, neutral hover/checked layers, a 2-DIP Text.Primary underline and an inset focus ring.
    /// Washes follow FluidMotion; state and focus take effect immediately without changing layout.
    /// </summary>
    private static ControlTemplate CreateTemplate(Type type, bool checkable, bool subtleCheck)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        var hoverLayer = StateLayer("HoverLayer", MotionSpeed.Fast);
        hoverLayer.SetResourceReference(Border.BackgroundProperty, HoverKey);
        FrameworkElementFactory? checkedLayer = null;
        if (checkable)
        {
            checkedLayer = StateLayer("CheckedLayer", MotionSpeed.Normal);
            checkedLayer.SetResourceReference(Border.BackgroundProperty, HoverKey);
        }

        // The presenter keeps the one-pixel chrome inset it had as the chrome's child.
        var content = new FrameworkElementFactory(typeof(Border));
        content.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.AppendChild(presenter);
        var ring = new FrameworkElementFactory(typeof(Border), "FocusRing");
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        ring.SetValue(UIElement.OpacityProperty, 0.0);
        ring.SetResourceReference(Border.BorderBrushProperty, TextKey);
        var root = new FrameworkElementFactory(typeof(Grid));
        root.AppendChild(chrome);
        if (checkedLayer is not null)
        {
            root.AppendChild(checkedLayer);
            var underline = new FrameworkElementFactory(typeof(Border), "SelectedUnderline");
            underline.SetValue(FrameworkElement.HeightProperty, 2.0);
            underline.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 1));
            underline.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
            underline.SetValue(UIElement.IsHitTestVisibleProperty, false);
            underline.SetValue(UIElement.OpacityProperty, 0.0);
            underline.SetResourceReference(Border.BackgroundProperty, "Text.Primary");
            root.AppendChild(underline);
        }

        root.AppendChild(hoverLayer);
        root.AppendChild(content);
        root.AppendChild(ring);

        var template = new ControlTemplate(type) { VisualTree = root };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "HoverLayer"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "HoverLayer"));
        template.Triggers.Add(pressed);
        if (checkable)
        {
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(FluidMotion.FadeToProperty, 1.0, "CheckedLayer"));
            on.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "SelectedUnderline"));

            template.Triggers.Add(on);
        }

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "FocusRing"));
        template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.46));
        template.Triggers.Add(disabled);
        template.Seal();
        return template;
    }

    /// <summary>A hidden, hit-test-transparent wash with the chrome's shape that fades in on a state.</summary>
    private static FrameworkElementFactory StateLayer(string name, MotionSpeed speed)
    {
        var layer = new FrameworkElementFactory(typeof(Border), name);
        layer.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        layer.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        layer.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        layer.SetValue(UIElement.OpacityProperty, 0.0);
        layer.SetValue(UIElement.IsHitTestVisibleProperty, false);
        layer.SetValue(FluidMotion.TransitionProperty, speed);
        return layer;
    }
}
