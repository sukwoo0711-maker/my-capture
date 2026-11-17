using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyCapture.App.Capture;
using MyCapture.App.Sources;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// The small bar a pinned image shows just above its top-right corner while the pointer is over it: copy,
/// text copy, save, translation and source when the pin offers them, and close, so the commonest pin
/// commands no longer hide in a long tooltip and the right-click menu.
/// </summary>
/// <remarks>
/// It is a separate, never-activated tool window owned by the pin and excluded from screen capture,
/// so it never appears in a screenshot or a recording while the pin itself stays capturable exactly
/// as before. Without Windows animations it appears and disappears at once.
/// Every pin kind lays it out by one rule: the commands keep one order (<see cref="CommandOrder"/>),
/// a command a pin lacks leaves no gap, and every button shares one height, icon size and minimum
/// width, so an icon-only bar spaces its icons evenly.
/// </remarks>
internal sealed class PinHoverBar : Window
{
    /// <summary>The opacities the opacity button offers.</summary>
    internal static readonly IReadOnlyList<double> OpacityChoices = [1.0, 0.8, 0.6, 0.4];

    /// <summary>Gap between the bar and the pin's edge, in DIPs.</summary>
    internal const double EdgeGap = 6;

    /// <summary>Excludes the bar from capture; tests substitute a recorder.</summary>
    internal static Func<Window, bool> ApplyCaptureExclusion { get; set; } = CaptureWindowExclusion.TryApply;

    /// <summary>
    /// The one width every button keeps at least while the names show: a full Ctrl+C or Ctrl+S key
    /// caption fits inside it, so Alt never widens a button.
    /// </summary>
    internal const double ButtonMinWidth = 60;

    /// <summary>
    /// The one width of every button in the icon-only bar. Its narrow side padding still keeps a full
    /// Ctrl+C key caption inside, so Alt never widens a button and the icons sit at even steps.
    /// </summary>
    internal const double IconOnlyButtonWidth = 44;

    // 20x20 outline glyphs on the shared symbol grid.
    private static readonly Geometry SourceGlyph = Geometry.Parse(
        "M8,5 H4 V16 H15 V12 M10,4 H16 V10 M9,11 L16,4");
    /// <summary>The translation button's glyph, which the pin menu's translation command also shows.</summary>
    internal static readonly Geometry TranslationGlyph = Geometry.Parse(
        "M2,4 H11 M6,2 V4 M4,7 L10,13 M9,4 C9,8 6,11 2,13 M11,17 L15,7 L19,17 M12.5,14 H17.5");

    private readonly List<TextBlock> _labels = [];
    private readonly Dictionary<Button, TextBlock> _buttonLabels = [];
    private readonly Border _surface;
    private readonly StackPanel _row;
    private double _labeledTranslationWidth;
    private ContextMenu? _opacityMenu;
    private bool _compact;
    private readonly UIElement _sourceGlyph;
    private int _sourceIconVersion;

    internal PinHoverBar(CaptureProvenance? provenance = null)
    {
        Title = UiText.Get("Onboard_PinBarName");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = CaptureWindowExclusion.SupportsLayeredWindows;
        Background = AllowsTransparency ? Brushes.Transparent : CaptureWindowExclusion.OpaqueBackground();
        if (!AllowsTransparency) ModernWindowChrome.SetEnabled(this, false);
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FluidMotion.SetWindowEntrance(this, false);
        StandardWindowTheme.ApplyFloatingText(this);
        AutomationProperties.SetName(this, UiText.Get("Onboard_PinBarName"));

        CopyButton = BarButton(SymbolGeometry("Icon.Copy"), UiText.Get("QuickAccess.Copy"), CopyKey);
        // The pin menu's own "Copy text" command and its gesture: the kept original text, or the
        // picture's text read on this PC. A mouse gesture, so it names it in the tooltip only.
        TextCopyButton = BarButton(SymbolGeometry("Icon.Ocr"), UiText.Get("Text_03A1596F1681"), key: null);
        TextCopyButton.ToolTip = $"{UiText.Get("Text_03A1596F1681")} ({UiText.Get("Text_C7011B8D345C")})";
        AutomationProperties.SetHelpText(TextCopyButton, (string)TextCopyButton.ToolTip);
        TranslationButton = BarButton(TranslationGlyph, UiText.Get("Pin_Translate"), key: null);
        TranslationButton.Visibility = Visibility.Collapsed;
        TranslationButton.Click += (_, _) => TranslationRequested?.Invoke();
        SaveButton = BarButton(SymbolGeometry("Icon.Save"), UiText.Get("QuickAccess.Save"), "Ctrl+S");
        SourceButton = BarButton(SourceGlyph, UiText.Get("Source.Open"), key: null, showLabel: false);
        // The source application's icon takes the glyph's place at the glyph's size once it is in memory.
        var sourceContent = (StackPanel)SourceButton.Content;
        _sourceGlyph = sourceContent.Children[0];
        sourceContent.Children.RemoveAt(0);
        SourceIcon = new Image
        {
            Name = SourceAppIcon.ImageName, Width = CommandButtonPresentation.IconSize, Height = CommandButtonPresentation.IconSize,
            Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(SourceIcon, BitmapScalingMode.HighQuality);
        var sourceSymbol = new Grid { VerticalAlignment = VerticalAlignment.Center };
        sourceSymbol.Children.Add(_sourceGlyph);
        sourceSymbol.Children.Add(SourceIcon);
        sourceContent.Children.Insert(0, sourceSymbol);
        SetProvenance(provenance ?? CaptureProvenance.None);
        CloseButton = BarButton(SymbolGeometry("Icon.Close"), UiText.Get("Dialog.Close"), "Esc", showLabel: false);
        CopyButton.Click += (_, _) => CopyRequested?.Invoke();
        CopyButton.ToolTipOpening += (_, _) =>
        {
            if (CopyNameProvider?.Invoke() is { } copyName) SetCopyAction(copyName);
        };
        TextCopyButton.Click += (_, _) => TextCopyRequested?.Invoke();
        SaveButton.Click += (_, _) => SaveRequested?.Invoke();
        SourceButton.Click += (_, _) => SourceRequested?.Invoke();
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();

        _row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (Button button in CommandOrder)
            _row.Children.Add(button);
        var row = _row;
        _surface = new Border
        {
            Child = row,
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(AllowsTransparency ? 8 : 0),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0),
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        _surface.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        _surface.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        Content = _surface;

        // The pin owns keyboard focus; this capture-excluded tool window never activates.
        Loaded += (_, _) =>
        {
            if (Owner is { } owner) ShortcutPresentation.FollowOwner(this, owner);
        };

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            MyCapture.App.Diagnostics.ResidentProbeWindowHook.SourceInitialized?.Invoke(this);
            WindowStyleFacade.SetNoActivate(hwnd, true);
            WindowStyleFacade.ExcludeFromAltTab(hwnd);
            CaptureWindowExclusion.DisableTransitions(this);
            IsCaptureExcluded = ApplyCaptureExclusion(this);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
        };
    }

    private const string CopyKey = "Ctrl+C";

    internal event Action? CopyRequested;

    /// <summary>What the copy button places on the clipboard now; read each time its tooltip opens.</summary>
    internal Func<string?>? CopyNameProvider { get; set; }

    /// <summary>
    /// Names what copy places on the clipboard in the tooltip, the automation name and its help.
    /// The visible name stays "Copy", so the bar never changes width.
    /// </summary>
    internal void SetCopyAction(string name)
    {
        string help = $"{name} ({CopyKey})";
        CopyButton.ToolTip = help;
        AutomationProperties.SetName(CopyButton, name);
        AutomationProperties.SetHelpText(CopyButton, help);
    }

    internal event Action? TranslationRequested;

    internal Button TranslationButton { get; }

    /// <returns>
    /// Whether the bar can change size: the button was shown or hidden, or its name changed. Enabling it
    /// or a new tooltip keeps the bar where it is.
    /// </returns>
    internal bool SetTranslationAction(bool visible, bool enabled, string label, string help)
    {
        Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TextBlock name = _buttonLabels[TranslationButton];
        bool resizes = TranslationButton.Visibility != visibility || name.Text != label;
        TranslationButton.Visibility = visibility;
        TranslationButton.IsEnabled = enabled;
        name.Text = label;
        TranslationButton.ToolTip = help;
        AutomationProperties.SetName(TranslationButton, label);
        AutomationProperties.SetHelpText(TranslationButton, help);
        return resizes;
    }

    /// <summary>
    /// Left to right: the main commands (copy, text copy, save), the ones a pin may lack
    /// (translation, source), then close. A command a pin lacks is collapsed, so the rest keep this order.
    /// </summary>
    internal IReadOnlyList<Button> CommandOrder => [CopyButton, TextCopyButton, SaveButton, TranslationButton, SourceButton, CloseButton];

    /// <summary>
    /// The widest bar that still shows every name: the named set copy · text copy · save with close
    /// (and source) as measured with the app theme, plus the translation button's own named width
    /// when the pin offers it, so offering translation never turns one pin kind's names into icons.
    /// </summary>
    internal double LabeledWidthBudget => BaseLabeledWidthBudget
        + (TranslationButton.Visibility == Visibility.Visible ? _labeledTranslationWidth : 0);

    /// <summary>The measured named width of copy · text copy · save · close (and source), in DIPs.</summary>
    internal double BaseLabeledWidthBudget => UiText.Culture.TwoLetterISOLanguageName == "ko"
        ? SourceButton.Visibility == Visibility.Visible ? 408 : 342
        : SourceButton.Visibility == Visibility.Visible ? 421 : 355;

    internal bool NeedsCompact(double pinWidth, double labeledWidth) =>
        labeledWidth > LabeledWidthBudget || pinWidth < labeledWidth + EdgeGap * 4;

    internal event Action? TextCopyRequested;

    /// <summary>Copies the pin's text: its kept original, or the text read from its picture.</summary>
    internal Button TextCopyButton { get; }

    internal event Action? SaveRequested;

    internal event Action? SourceRequested;

    internal event Action<double>? OpacityRequested;
    internal event Action? OpacityMenuOpened;
    internal event Action? OpacityMenuClosed;

    internal event Action? CloseRequested;

    internal Button CopyButton { get; }

    internal Button SaveButton { get; }

    internal Button SourceButton { get; }

    /// <summary>The source application's icon, shown in the glyph's place once the icon cache has it.</summary>
    internal Image SourceIcon { get; }

    internal void SetProvenance(CaptureProvenance source, SourceIconCache? icons = null)
    {
        SourceButton.Visibility = source.HasSource ? Visibility.Visible : Visibility.Collapsed;
        string tooltip = SourceChip.TooltipFor(source);
        SourceButton.ToolTip = source.HasSource ? $"{source.DisplayText}\n{tooltip}" : null;
        AutomationProperties.SetName(SourceButton, source.HasSource
            ? UiText.Format(source.CanOpen ? "Source.OpenAutomation" : "Source.Label", source.DisplayText)
            : UiText.Get("Source.Open"));
        AutomationProperties.SetHelpText(SourceButton, tooltip);

        int version = ++_sourceIconVersion;
        ShowSourceIcon(null);
        if (!source.HasAppIdentity) return;
        icons ??= SourceIconCache.Shared;
        if (icons.TryGet(source, out ImageSource? icon)) ShowSourceIcon(icon);
        else icons.Request(source, ready =>
        {
            if (version == _sourceIconVersion) ShowSourceIcon(ready);
        });
    }

    private void ShowSourceIcon(ImageSource? icon)
    {
        SourceIcon.Source = icon;
        SourceIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        _sourceGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Hidden;
    }

    internal Button CloseButton { get; }

    /// <summary>The window was excluded from screen capture when its handle was created.</summary>
    internal bool IsCaptureExcluded { get; private set; }

    /// <summary>
    /// Icons only: the names stay in the tooltips and automation names, and every button narrows to
    /// <see cref="IconOnlyButtonWidth"/> in the same order and steps.
    /// </summary>
    internal bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            foreach (TextBlock label in _labels) label.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            foreach (Button button in CommandOrder)
            {
                button.MinWidth = value ? IconOnlyButtonWidth : ButtonMinWidth;
                button.SetResourceReference(Control.PaddingProperty,
                    value ? "CommandButton.Padding.CompactIconOnly" : "CommandButton.Padding.Compact");
            }
        }
    }

    /// <summary>
    /// The bar's width with every name shown, in DIPs, without showing it.
    /// </summary>
    /// <remarks>
    /// A window that has never been shown is collapsed, so measuring the window gives zero; its
    /// content measures with the button templates and theme resources it will be shown with.
    /// </remarks>
    internal double MeasureLabeledWidth()
    {
        bool compact = _compact;
        Compact = false;
        // A name shown again marks only its own panel for measuring, so a shown icon-only bar
        // reported its icon-only width as the labelled one and swapped to the names it has no room for.
        InvalidateNameMeasures();
        _surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = _surface.DesiredSize.Width;
        // DesiredSize already includes the button's margin.
        _labeledTranslationWidth = TranslationButton.Visibility == Visibility.Visible ? TranslationButton.DesiredSize.Width : 0;
        Compact = compact;
        return width;
    }

    private void InvalidateNameMeasures()
    {
        foreach (TextBlock label in _labels)
        {
            for (DependencyObject? element = label; element is UIElement visual;
                 element = VisualTreeHelper.GetParent(visual) ?? LogicalTreeHelper.GetParent(visual))
            {
                visual.InvalidateMeasure();
                if (ReferenceEquals(visual, _surface)) break;
            }
        }
    }

    /// <summary>Reads the pin's current opacity, shown as the checked entry of the opacity menu.</summary>
    internal Func<double> ReadOpacity { get; set; } = () => 1;

    /// <summary>
    /// The opacity menu (the pin's O key) without opening it. The bar has no opacity button; the menu
    /// opens just under the bar, at the pin's top-right corner.
    /// </summary>
    internal ContextMenu CreateOpacityMenu()
    {
        ContextMenu menu = CaptureWindowExclusion.CreateContextMenu();
        menu.PlacementTarget = _surface;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 2;
        AutomationProperties.SetName(menu, UiText.Get("Onboard_PinOpacity"));
        foreach (double choice in OpacityChoices)
        {
            string header = $"{choice * 100:0}%";
            var item = new MenuItem { Header = header, MinHeight = 34, IsCheckable = true, StaysOpenOnClick = true, IsChecked = Math.Abs(choice - ReadOpacity()) < 0.05 };
            if (item.IsChecked) item.Icon = Glyph(SymbolGeometry("Icon.Check"), 16);
            AutomationProperties.SetName(item, header);
            double opacity = choice;
            item.Click += (_, _) =>
            {
                OpacityRequested?.Invoke(opacity);
                menu.IsOpen = false;
            };
            menu.Items.Add(item);
        }

        menu.Opened += (_, _) =>
        {
            CaptureWindowExclusion.ExcludeMenuPopup(menu);
        };
        return menu;
    }

    /// <summary>
    /// Where the bar goes, in physical pixels: just above the pin, right-aligned, whatever the pin's size,
    /// so it never covers the first line of the code or table the pin shows. Without room above on the
    /// pin's work area it goes just below; only a pin that leaves no room on either side takes it inside
    /// the top-right corner of its visible part. Kept on the work area.
    /// </summary>
    internal static RectD Place(RectD pin, double barWidth, double barHeight, RectD workArea, double scale)
    {
        double gap = EdgeGap * (scale > 0 ? scale : 1);
        double above = pin.Top - barHeight - gap;
        double below = pin.Bottom + gap;
        double x = pin.Right - barWidth;
        double y;
        if (above >= workArea.Top) y = above;
        else if (below + barHeight <= workArea.Bottom) y = below;
        else
        {
            x = Math.Min(pin.Right, workArea.Right) - barWidth - gap;
            y = Math.Max(pin.Top, workArea.Top) + gap;
        }
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - barWidth));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - barHeight));
        return new RectD(Math.Round(x), Math.Round(y), Math.Ceiling(barWidth), Math.Ceiling(barHeight));
    }

    /// <summary>Fades in, or appears at once when Windows animations are off.</summary>
    internal void Reveal()
    {
        BeginAnimation(OpacityProperty, null);
        if (!AllowsTransparency || !FluidMotion.AnimationsEnabled)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    /// <summary>
    /// Fades out over the fast token while its pin leaves, taking no more clicks; hides at once when
    /// Windows animations are off or the bar is opaque. The pin's close then closes it.
    /// </summary>
    internal void Leave()
    {
        IsHitTestVisible = false;
        if (_opacityMenu is { IsOpen: true } opacity) opacity.IsOpen = false;
        if (SourceButton.ContextMenu is { IsOpen: true } source) source.IsOpen = false;
        if (!IsVisible) return;
        if (!AllowsTransparency || !FluidMotion.AnimationsEnabled)
        {
            Hide();
            return;
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.HoldEnd,
        }, HandoffBehavior.SnapshotAndReplace);
    }

    internal void OpenOpacityMenu(bool keyboard = false)
    {
        if (_opacityMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateOpacityMenu();
        if (keyboard && Owner is { } owner && _surface.IsVisible)
        {
            // Keep keyboard ownership with the pin; the floating bar never activates.
            Point origin = owner.PointFromScreen(_surface.PointToScreen(new Point(0, _surface.ActualHeight)));
            menu.PlacementTarget = owner;
            menu.Placement = PlacementMode.RelativePoint;
            menu.HorizontalOffset = origin.X;
            menu.VerticalOffset = origin.Y + 2;
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(_opacityMenu, menu)) _opacityMenu = null; OpacityMenuClosed?.Invoke(); };
        _opacityMenu = menu;
        menu.Opened += (_, _) =>
        {
            OpacityMenuOpened?.Invoke();
            if (keyboard) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (!menu.IsOpen) return;
                ((MenuItem)menu.Items[0]).Focus();
            }));
        };
        menu.IsOpen = true;
    }

    /// <summary>True while the opacity menu is open, so the pin keeps the bar while the pointer is in it.</summary>
    internal bool IsMenuOpen => _opacityMenu?.IsOpen == true || SourceButton.ContextMenu?.IsOpen == true;

    private Button BarButton(Geometry glyph, string name, string? key, bool showLabel = true)
    {
        var button = new Button
        {
            Focusable = false,
            Cursor = Cursors.Hand,
        };
        button.SetResourceReference(StyleProperty, "Button.GhostCompact");
        CommandButtonPresentation.Apply(button, compact: true);
        // One width for every command, wide enough for a chord even when the pin collapses the names:
        // per-key widths made an icon-only bar space its icons unevenly.
        button.MinWidth = ButtonMinWidth;
        // Centred in the face: the face is as wide as its key caption, and a left-aligned icon sat up to
        // 8 DIPs off its button's middle in the icon-only bar.
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        SymbolIcon icon = Glyph(glyph, CommandButtonPresentation.IconSize);
        icon.SetBinding(SymbolIcon.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        content.Children.Add(icon);
        if (showLabel)
        {
            var label = new TextBlock { Text = name, Margin = new Thickness(CommandButtonPresentation.IconLabelGap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _labels.Add(label);
            _buttonLabels.Add(button, label);
            content.Children.Add(label);
        }

        button.Content = content;
        AutomationProperties.SetName(button, name);
        if (key is not null) AutomationProperties.SetAcceleratorKey(button, key);
        button.ToolTip = key is null ? name : $"{name} ({key})";
        ToolTipService.SetInitialShowDelay(button, 300);
        return button;
    }

    private static SymbolIcon Glyph(Geometry geometry, double size)
    {
        SymbolIcon icon = SymbolIcon.Create(geometry, size);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Primary");
        return icon;
    }

    private static Geometry SymbolGeometry(string key) =>
        Application.Current?.TryFindResource(key) as Geometry ?? System.Windows.Media.Geometry.Parse("M4,4 H16 V16 H4 Z");

    private static IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_MOUSEACTIVATE: a press on the bar runs its command without taking the foreground.
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE
    }

}
