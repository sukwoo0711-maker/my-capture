using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;

namespace MyCapture.App.Editing;

/// <summary>What the user chose for one recoverable draft.</summary>
internal enum DraftRecoveryChoice
{
    /// <summary>Reopen the draft in its editor.</summary>
    Recover,

    /// <summary>Delete the draft; the capture it came from is unchanged.</summary>
    Discard,
}

/// <summary>One recoverable draft as the recovery dialog lists it.</summary>
/// <param name="SessionId">The draft's editing session.</param>
/// <param name="IsVideo">A recording draft rather than an image draft.</param>
/// <param name="SavedUtc">When the draft was last saved.</param>
/// <param name="Width">Pixel width of the edited image or video; 0 when unknown.</param>
/// <param name="Height">Pixel height of the edited image or video; 0 when unknown.</param>
/// <param name="Thumbnail">A small frozen picture of the draft, or null for a placeholder glyph.</param>
internal sealed record DraftRecoveryItem(Guid SessionId, bool IsVideo, DateTimeOffset SavedUtc, int Width, int Height,
    BitmapSource? Thumbnail)
{
    /// <summary>"이미지 편집 초안" or "동영상 편집 초안".</summary>
    internal string Heading => UiText.Get(IsVideo ? "EditRecovery_VideoDraft" : "EditRecovery_ImageDraft");

    /// <summary>"10월 12일 14:32 · 640 × 360", in local time; just the time when the size is unknown.</summary>
    internal string Detail
    {
        get
        {
            string saved = SavedUtc.ToLocalTime().ToString(UiText.Get("EditRecovery_TimeFormat"), UiText.Culture);
            return Width > 0 && Height > 0 ? UiText.Format("EditRecovery_Detail", saved, Width, Height) : saved;
        }
    }
}

/// <summary>
/// Lists every draft a previous session left unsaved, each with a thumbnail, when it was saved
/// and its size, and [Recover] [Discard draft] beside it; [Later] keeps them all for the next
/// launch. It is a normal top-level window with a taskbar button, brought to the front when it
/// opens, so it cannot wait unseen behind other windows after a sign-in.
/// </summary>
internal sealed class DraftRecoveryDialog : Window
{
    /// <summary>Width of a draft's thumbnail.</summary>
    internal const double ThumbnailWidth = 96;

    /// <summary>Height of a draft's thumbnail frame.</summary>
    internal const double ThumbnailHeight = 64;

    private readonly Func<DraftRecoveryItem, DraftRecoveryChoice, Task<bool>> _resolve;
    private readonly StackPanel _rows = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<DraftRecoveryItem, Row> _byItem = [];
    private readonly bool _single;

    /// <param name="items">The drafts to offer, newest first.</param>
    /// <param name="resolve">
    /// Recovers or discards one draft. True removes it from the list; false (it failed and the
    /// user was told) keeps it so it can be tried again or discarded.
    /// </param>
    internal DraftRecoveryDialog(IReadOnlyList<DraftRecoveryItem> items, Func<DraftRecoveryItem, DraftRecoveryChoice, Task<bool>> resolve)
    {
        ArgumentNullException.ThrowIfNull(items);
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        Title = UiText.Get("EditRecovery_Title");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(this, UiText.Get("EditRecovery_Title"));

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(KoreanWordWrap.Show(new TextBlock
        {
            FontSize = TypeScale.Subtitle, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        }, UiText.Get("EditRecovery_Title")));
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) };
        KoreanWordWrap.SetText(message, UiText.Get("EditRecovery_Message"));
        panel.Children.Add(message);

        AutomationProperties.SetName(_rows, UiText.Get("EditRecovery_Title"));
        _single = items.Count == 1;
        foreach (DraftRecoveryItem item in items) AddRow(item);
        panel.Children.Add(new ScrollViewer
        {
            Content = _rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        LaterButton = new Button { Content = UiText.Get("EditRecovery_Later"), MinWidth = 88, MinHeight = 36, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        LaterButton.SetResourceReference(StyleProperty, "Button.Secondary");
        AutomationProperties.SetName(LaterButton, UiText.Get("EditRecovery_Later"));
        LaterButton.Click += (_, _) => Close();
        panel.Children.Add(LaterButton);
        Content = panel;

        Loaded += (_, _) => { if (_byItem.Values.FirstOrDefault() is { } first) first.Recover.Focus(); };
        Closed += (_, _) => _closed.TrySetResult();
    }

    /// <summary>Completes when the dialog closes: every draft was handled, or the user chose Later.</summary>
    internal Task Completion => _closed.Task;

    internal Button LaterButton { get; }

    /// <summary>The drafts still listed.</summary>
    internal IReadOnlyList<DraftRecoveryItem> Items => [.. _byItem.Keys];

    /// <summary>The Recover and Discard draft buttons of one listed draft.</summary>
    internal (Button Recover, Button Discard) ButtonsFor(DraftRecoveryItem item) =>
        (_byItem[item].Recover, _byItem[item].Discard);

    /// <summary>Shows the dialog above every other window, with its taskbar button, and activates it when Windows allows.</summary>
    internal void ShowInFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _ = Activate();
        // A sign-in launch may not own the foreground; raising the z-order still puts it on top.
        Topmost = true;
        Topmost = false;
    }

    private void AddRow(DraftRecoveryItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var frame = new Border
        {
            Width = ThumbnailWidth, Height = ThumbnailHeight, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        if (item.Thumbnail is { } thumbnail)
        {
            // A Border with an image brush, not an Image: the picture repeats the text beside it,
            // so it stays out of the UI Automation tree.
            var picture = new Border { Background = new ImageBrush(thumbnail) { Stretch = Stretch.Uniform } };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            frame.Child = picture;
        }
        else
        {
            SymbolIcon glyph = SymbolIcon.FromResource(item.IsVideo ? "Icon.Record" : "Icon.Image");
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Secondary");
            frame.Child = glyph;
        }
        grid.Children.Add(frame);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        text.Children.Add(KoreanWordWrap.Show(new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, item.Heading));
        // Parts stay together (no-break spaces); the digits and units inside them stay whole as well.
        var detail = KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) },
            KeepPartsTogether(item.Detail));
        AutomationProperties.SetName(detail, item.Detail);
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // One filled button per dialog: Recover is the primary action only when there is one draft.
        Button recover = Action(UiText.Get("EditRecovery_Restore"), item, _single ? "Button.Primary" : "Button.Secondary");
        Button discard = Action(UiText.Get("EditRecovery_Discard"), item, "Button.Secondary");
        discard.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(recover);
        actions.Children.Add(discard);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        var card = new Border
        {
            Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid,
            Margin = new Thickness(0, _byItem.Count == 0 ? 0 : 8, 0, 0),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        AutomationProperties.SetName(card, item.Heading + ", " + item.Detail);
        _rows.Children.Add(card);
        var row = new Row(card, recover, discard);
        _byItem.Add(item, row);
        recover.Click += async (_, _) => await ResolveAsync(item, DraftRecoveryChoice.Recover);
        discard.Click += async (_, _) => await ResolveAsync(item, DraftRecoveryChoice.Discard);
    }

    /// <summary>
    /// The detail line wraps only between its parts ("10월 12일 14:32" · "640 × 360"), never inside
    /// a date or a size.
    /// </summary>
    internal static string KeepPartsTogether(string detail) =>
        string.Join(" · ", detail.Split(" · ").Select(part => part.Replace(' ', ' ')));

    private static Button Action(string label, DraftRecoveryItem item, string style)
    {
        var button = new Button { Content = label, MinWidth = 72, MinHeight = 32 };
        button.SetResourceReference(StyleProperty, style);
        // The visible label first, then which draft it acts on.
        AutomationProperties.SetName(button, UiText.Format("EditRecovery_RowAction", label, item.Detail));
        return button;
    }

    private async Task ResolveAsync(DraftRecoveryItem item, DraftRecoveryChoice choice)
    {
        if (!_byItem.TryGetValue(item, out Row? row)) return;
        row.Card.IsEnabled = false;
        bool done;
        try { done = await _resolve(item, choice); }
        catch (Exception) { done = false; }
        if (!done)
        {
            row.Card.IsEnabled = true;
            return;
        }
        _rows.Children.Remove(row.Card);
        _byItem.Remove(item);
        if (_byItem.Count == 0)
        {
            Close();
            return;
        }
        if (_rows.Children[0] is Border first) first.Margin = new Thickness(0);
        _byItem.Values.First().Recover.Focus();
    }

    private sealed record Row(Border Card, Button Recover, Button Discard);
}
