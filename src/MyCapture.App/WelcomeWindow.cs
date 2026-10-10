using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>What closed the first-run welcome, so the shell knows what to open next.</summary>
internal enum WelcomeOutcome
{
    /// <summary>Closed with the library button, the close button or Esc: the library opens.</summary>
    Closed,

    /// <summary>"Try it now": a region capture starts once the window is gone.</summary>
    TryCapture,

    /// <summary>The shortcut link: settings open on the shortcuts.</summary>
    OpenSettings,

    /// <summary>A capture shortcut arrived while it was open; nothing else opens.</summary>
    DismissedForCapture,
}

/// <summary>
/// The one window a first run shows: three illustrated steps (take a region capture, what happens
/// to it, where the tray icon lives) instead of a list of keys. Region capture has the only filled
/// button and really starts a capture; changing shortcuts is a small link.
/// </summary>
/// <remarks>
/// The illustrations are vector drawings in theme brushes, so they follow all six themes and high
/// contrast. The after-capture sentence follows the user's after-capture setting.
/// </remarks>
internal sealed class WelcomeWindow : Window
{
    private static readonly Geometry RegionGlyph = Geometry.Parse(
        "M3.5,7 L3.5,3.5 L7,3.5 M13,3.5 L16.5,3.5 L16.5,7 M16.5,13 L16.5,16.5 L13,16.5 M7,16.5 L3.5,16.5 L3.5,13 M8,8 L12,8 L12,12 L8,12 Z");

    internal WelcomeWindow(HotkeySettings keys, AfterCaptureAction afterCapture)
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

        TryNowButton = new Button { Content = UiText.Get("Onboard_TryNow"), MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        TryNowButton.SetResourceReference(StyleProperty, "Button.Primary");
        TryNowButton.ToolTip = UiText.Get("Onboard_TryNowHelp");
        AutomationProperties.SetHelpText(TryNowButton, UiText.Get("Onboard_TryNowHelp"));
        TryNowButton.Click += (_, _) => CloseWith(WelcomeOutcome.TryCapture);

        AfterCaptureText = AfterCaptureSentence(afterCapture);
        var steps = new StackPanel();
        AutomationProperties.SetName(steps, UiText.Get("Onboard_StepsName"));
        // Only a key that is really assigned is shown; otherwise the tray menu is the way in.
        FrameworkElement captureKeys = keys.Capture.IsAssigned
            ? ShortcutHelpWindow.Keycaps(keys.Capture.ToString())
            : Sentence(UiText.Get("Onboard_CaptureFromTray"));
        steps.Children.Add(Step(1, CaptureIllustration(), UiText.Get("Text_096A13A757DB"), captureKeys, TryNowButton));
        steps.Children.Add(Step(2, AfterCaptureIllustration(afterCapture), UiText.Get("Onboard_AfterCaptureTitle"),
            Sentence(AfterCaptureText), action: null));
        steps.Children.Add(Step(3, TrayIllustration(), UiText.Get("Onboard_TrayTitle"),
            Sentence(UiText.Get("Onboard_TrayBody")), action: null));

        var link = new Hyperlink(new Run(UiText.Get("Help.Settings")));
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent.Cool");
        link.Click += (_, _) => CloseWith(WelcomeOutcome.OpenSettings);
        ShortcutsLink = link;
        var linkText = new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };

        LibraryButton = new Button { Content = UiText.Get("Text_66BA5DCDA810"), MinWidth = 120, IsCancel = true };
        LibraryButton.SetResourceReference(StyleProperty, "Button.Secondary");
        LibraryButton.Click += (_, _) => CloseWith(WelcomeOutcome.Closed);

        var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        DockPanel.SetDock(LibraryButton, Dock.Right);
        footer.Children.Add(LibraryButton);
        footer.Children.Add(linkText);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        root.Children.Add(steps);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => TryNowButton.Focus();
    }

    internal WelcomeOutcome Outcome { get; private set; } = WelcomeOutcome.Closed;

    internal Button TryNowButton { get; }

    internal Button LibraryButton { get; }

    internal Hyperlink ShortcutsLink { get; }

    /// <summary>The second step's sentence, as the after-capture setting makes it true.</summary>
    internal string AfterCaptureText { get; }

    /// <summary>What happens to every capture, worded for the after-capture setting.</summary>
    internal static string AfterCaptureSentence(AfterCaptureAction action) => UiText.Get(action switch
    {
        AfterCaptureAction.QuickAccess => "Onboard_AfterCaptureQuickAccess",
        AfterCaptureAction.ClipboardOnly => "Onboard_AfterCaptureClipboard",
        _ => "Onboard_AfterCaptureEditor",
    });

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

    private static FrameworkElement Step(int number, FrameworkElement illustration, string title, FrameworkElement body,
        FrameworkElement? action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-6, -6, 0, 0),
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 12,
                FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        badge.SetResourceReference(Border.BorderBrushProperty, "Accent.Cool");
        AutomationProperties.SetName(badge, UiText.Format("Onboard_StepNumber", number));
        var picture = new Grid { Width = 104, Height = 76, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        picture.Children.Add(illustration);
        picture.Children.Add(badge);
        grid.Children.Add(picture);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        text.Children.Add(heading);
        text.Children.Add(body);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (action is not null)
        {
            action.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }

        var card = new Border
        {
            Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = grid,
            Margin = new Thickness(0, number == 1 ? 0 : 10, 0, 0),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        AutomationProperties.SetName(card, UiText.Format("Onboard_StepNumber", number) + " · " + title);
        return card;
    }

    private static TextBlock Sentence(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        KoreanWordWrap.SetText(block, text);
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        return block;
    }

    /// <summary>A screen with a window and an accent selection being dragged over part of it.</summary>
    private static FrameworkElement CaptureIllustration()
    {
        Canvas canvas = Tile();
        Add(canvas, Rounded(10, 10, 84, 56, 4, "Surface.Base", "Border.Strong", 1));
        Add(canvas, Rounded(18, 18, 46, 32, 2, "Surface.Overlay", "Border.Subtle", 1));
        var selection = new Rectangle { Width = 40, Height = 26, StrokeThickness = 1.5, StrokeDashArray = [3, 2] };
        selection.SetResourceReference(Shape.StrokeProperty, "Accent.Cool");
        Canvas.SetLeft(selection, 44);
        Canvas.SetTop(selection, 30);
        canvas.Children.Add(selection);
        Add(canvas, Glyph(RegionGlyph, 76, 48, 20, "Accent.Cool"));
        return Frame(canvas);
    }

    /// <summary>Clipboard, library and the next stop (editor, quick card or nothing) left to right.</summary>
    private static FrameworkElement AfterCaptureIllustration(AfterCaptureAction action)
    {
        Canvas canvas = Tile();
        string? third = action switch
        {
            AfterCaptureAction.QuickAccess => "Icon.Image",
            AfterCaptureAction.ClipboardOnly => null,
            _ => "Icon.Edit",
        };
        string[] icons = third is null ? ["Icon.Copy", "Icon.Storage"] : ["Icon.Copy", "Icon.Storage", third];
        double width = (icons.Length * 22) + ((icons.Length - 1) * 10);
        double x = (104 - width) / 2;
        for (int i = 0; i < icons.Length; i++)
        {
            var glyph = new Path { Width = 22, Height = 22, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            glyph.SetResourceReference(Path.DataProperty, icons[i]);
            glyph.SetResourceReference(Shape.StrokeProperty, i == icons.Length - 1 ? "Accent.Cool" : "Text.Primary");
            Canvas.SetLeft(glyph, x);
            Canvas.SetTop(glyph, 27);
            canvas.Children.Add(glyph);
            if (i < icons.Length - 1)
            {
                var dot = new Ellipse { Width = 3, Height = 3 };
                dot.SetResourceReference(Shape.FillProperty, "Text.Muted");
                Canvas.SetLeft(dot, x + 25.5);
                Canvas.SetTop(dot, 36.5);
                canvas.Children.Add(dot);
            }
            x += 32;
        }
        return Frame(canvas);
    }

    /// <summary>The right end of a taskbar: the ^ for hidden icons and the MyCapture icon ringed in accent.</summary>
    private static FrameworkElement TrayIllustration()
    {
        Canvas canvas = Tile();
        Add(canvas, Rounded(6, 46, 92, 22, 3, "Surface.Sunken", "Border.Subtle", 1));
        var chevron = new Path { Data = Geometry.Parse("M0,5 L4.5,0 L9,5"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        chevron.SetResourceReference(Shape.StrokeProperty, "Text.Primary");
        Canvas.SetLeft(chevron, 44);
        Canvas.SetTop(chevron, 54);
        canvas.Children.Add(chevron);
        var ring = new Ellipse { Width = 20, Height = 20, StrokeThickness = 1.6 };
        ring.SetResourceReference(Shape.StrokeProperty, "Accent.Cool");
        Canvas.SetLeft(ring, 58);
        Canvas.SetTop(ring, 47);
        canvas.Children.Add(ring);
        var icon = new Path { Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.4 };
        icon.SetResourceReference(Path.DataProperty, "Icon.Capture");
        icon.SetResourceReference(Shape.StrokeProperty, "Accent.Cool");
        Canvas.SetLeft(icon, 62);
        Canvas.SetTop(icon, 51);
        canvas.Children.Add(icon);
        for (int i = 0; i < 2; i++)
        {
            var line = new Rectangle { Width = 10, Height = 2, RadiusX = 1, RadiusY = 1 };
            line.SetResourceReference(Shape.FillProperty, "Text.Muted");
            Canvas.SetLeft(line, 82);
            Canvas.SetTop(line, 52 + (i * 6));
            canvas.Children.Add(line);
        }
        var pointer = new Path { Data = Geometry.Parse("M0,0 L0,11 L3,8 L5.5,12.5 L7,11.8 L4.8,7.4 L8.5,7.4 Z"), StrokeThickness = 1 };
        pointer.SetResourceReference(Shape.FillProperty, "Text.Primary");
        pointer.SetResourceReference(Shape.StrokeProperty, "Surface.Base");
        Canvas.SetLeft(pointer, 71);
        Canvas.SetTop(pointer, 30);
        canvas.Children.Add(pointer);
        return Frame(canvas);
    }

    private static Canvas Tile() => new() { Width = 104, Height = 76, ClipToBounds = true };

    private static FrameworkElement Frame(Canvas canvas)
    {
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = canvas };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Overlay");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        frame.IsHitTestVisible = false;
        return frame;
    }

    private static void Add(Canvas canvas, FrameworkElement element) => canvas.Children.Add(element);

    private static FrameworkElement Rounded(double x, double y, double width, double height, double radius,
        string fill, string stroke, double thickness)
    {
        var rect = new Rectangle { Width = width, Height = height, RadiusX = radius, RadiusY = radius, StrokeThickness = thickness };
        rect.SetResourceReference(Shape.FillProperty, fill);
        rect.SetResourceReference(Shape.StrokeProperty, stroke);
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        return rect;
    }

    private static FrameworkElement Glyph(Geometry geometry, double x, double y, double size, string stroke)
    {
        var path = new Path { Data = geometry, Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(Shape.StrokeProperty, stroke);
        Canvas.SetLeft(path, x);
        Canvas.SetTop(path, y);
        return path;
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
