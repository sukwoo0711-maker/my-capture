using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Sources;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;

namespace MyCapture.App.Media;

/// <summary>One compact information surface for library items and either editor.</summary>
internal sealed class MediaDetailsWindow : Window
{
    private static readonly ConditionalWeakTable<Window, object> OpenOwners = new();
    private readonly StackPanel _facts = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };

    internal MediaDetailsWindow(IReadOnlyList<MediaDetails> details)
    {
        Title = UiText.Get("MediaInfo.Title");
        Width = 500; MinWidth = 360; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(_facts);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        content.Children.Add(_status);
        if (details.Count == 1) AddSingle(details[0]);
        else AddSelection(details);
        var close = ThemedDialog.CreateActionButton(UiText.Get("Dialog.Close"), primary: true);
        close.IsCancel = true; close.IsDefault = true;
        close.Click += (_, _) => Close();
        content.Children.Add(ThemedDialog.CreateActionRow(close));
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    internal static async Task ShowAsync(Window owner, IReadOnlyList<MediaDetailsSource> sources)
    {
        owner.Dispatcher.VerifyAccess();
        if (sources.Count == 0 || OpenOwners.TryGetValue(owner, out _)) return;
        OpenOwners.Add(owner, new object());
        try
        {
            IReadOnlyList<MediaDetails> details = await MediaDetailsReader.ReadAsync(sources);
            if (!owner.IsVisible) return;
            new MediaDetailsWindow(details) { Owner = owner }.ShowDialog();
        }
        finally { OpenOwners.Remove(owner); }
    }

    private void AddSingle(MediaDetails details)
    {
        AddFact("MediaInfo.FileSize", SizeText(details.SizeBytes, details.FilePath.Length == 0));
        if (details.Source.CanvasWidth > 0 && details.Source.CanvasHeight > 0)
            AddFact("MediaInfo.CanvasSize", MediaDetailsFormatter.Resolution(details.Source.CanvasWidth, details.Source.CanvasHeight));
        AddFact("MediaInfo.Resolution", details.Resolution);
        if (details.Source.DurationMs is double duration) AddFact("MediaInfo.Duration", MediaDetailsFormatter.Duration(duration));
        AddFact("MediaInfo.Format", details.Format);
        AddFact("MediaInfo.Created", DateText(details.CreatedAt));
        AddFact("MediaInfo.Modified", DateText(details.ModifiedAt));
        AddFact("MediaInfo.Location", details.FilePath);
        if (details.FileUnavailable) KoreanWordWrap.Show(_status, UiText.Get("MediaInfo.ReadFailed"));
        if (details.FilePath.Length == 0) return;
        var copy = ThemedDialog.CreateActionButton(UiText.Get("Library.Source.CopyPath"), primary: false);
        var folder = ThemedDialog.CreateActionButton(UiText.Get("Library.Source.OpenFolder"), primary: false);
        copy.Click += (_, _) => KoreanWordWrap.Show(_status, CaptureSourceActions.Default.CopyPath(details.FilePath));
        folder.Click += async (_, _) => KoreanWordWrap.Show(_status, await CaptureSourceActions.Default.OpenLocationAsync(
            CaptureProvenance.Create(documentPath: details.FilePath), allowMissingParent: true));
        _facts.Children.Add(ThemedDialog.CreateActionRow(copy, folder));
    }

    private void AddSelection(IReadOnlyList<MediaDetails> details)
    {
        MediaSizeSummary summary = MediaDetailsFormatter.Summarize(details.Select(detail => detail.SizeBytes));
        AddFact("MediaInfo.Count", UiText.Format("MediaInfo.CountValue", summary.Count));
        AddFact("MediaInfo.TotalSize", summary.UnavailableCount == 0 ? SizeText(summary.TotalBytes) : UiText.Get("MediaInfo.Unavailable"));
        if (summary.UnavailableCount > 0)
        {
            AddFact("MediaInfo.ReadableSize", SizeText(summary.TotalBytes));
            KoreanWordWrap.Show(_status, UiText.Format("MediaInfo.UnavailableCount", summary.UnavailableCount));
        }
    }

    private void AddFact(string key, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 12, 0) }, UiText.Get(key));
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var text = new TextBox { Text = value.Length == 0 ? UiText.Get("MediaInfo.Unavailable") : value,
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0, 4, 0, 0), MinHeight = 0 };
        AutomationProperties.SetName(text, UiText.Get(key));
        Grid.SetColumn(text, 1); row.Children.Add(label); row.Children.Add(text); _facts.Children.Add(row);
    }

    internal static string SizeText(decimal bytes) => UiText.Format("MediaInfo.SizeValue",
        MediaDetailsFormatter.FileSize(bytes), MediaDetailsFormatter.ExactBytes(bytes));

    private static string SizeText(long? bytes, bool unsaved) => bytes is long value ? SizeText(value)
        : UiText.Get(unsaved ? "MediaInfo.Unsaved" : "MediaInfo.Unavailable");

    private static string DateText(DateTime? date) => date?.ToString("G", CultureInfo.CurrentCulture) ?? string.Empty;

    internal static string TooltipText(MediaDetails details)
    {
        var parts = new List<string> { UiText.Get("MediaInfo.FileSize") + ": " + (details.SizeBytes is long bytes
            ? MediaDetailsFormatter.FileSize(bytes) : UiText.Get("MediaInfo.Unavailable")) };
        if (details.Resolution.Length > 0) parts.Add(details.Resolution);
        if (details.Source.DurationMs is double duration && MediaDetailsFormatter.Duration(duration) is { Length: > 0 } formatted)
            parts.Add(formatted);
        if (details.Format.Length > 0) parts.Add(details.Format);
        return string.Join(" · ", parts);
    }
}
