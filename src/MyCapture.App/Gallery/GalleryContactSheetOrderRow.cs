using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Gallery;

/// <summary>
/// One image in the PDF order list. The small picture is decoded only when its row is first
/// shown, so a long selection opens at once and never decodes rows nobody scrolls to.
/// </summary>
internal sealed class GalleryContactSheetOrderRow : INotifyPropertyChanged
{
    private readonly Func<CancellationToken, Task<BitmapSource?>> _loadThumbnail;
    private readonly CancellationToken _cancellation;
    private readonly Action<GalleryContactSheetOrderRow> _inclusionChanged;
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _isIncluded = true;
    private string _number = string.Empty;
    private string _automationName = string.Empty;
    private string _includeName = string.Empty;

    internal GalleryContactSheetOrderRow(int item, string title, string detail,
        Func<CancellationToken, Task<BitmapSource?>> loadThumbnail, CancellationToken cancellation,
        Action<GalleryContactSheetOrderRow> inclusionChanged)
    {
        Item = item;
        Title = title;
        Detail = detail;
        _loadThumbnail = loadThumbnail;
        _cancellation = cancellation;
        _inclusionChanged = inclusionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The index of this image in the dialog's original item list.</summary>
    internal int Item { get; }

    public string Title { get; }

    public string Detail { get; }

    /// <summary>The image's place in the PDF; empty while it is left out.</summary>
    public string Number { get => _number; private set => Set(ref _number, value, nameof(Number)); }

    public string AutomationName { get => _automationName; private set => Set(ref _automationName, value, nameof(AutomationName)); }

    public string IncludeName { get => _includeName; private set => Set(ref _includeName, value, nameof(IncludeName)); }

    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value) return;
            _isIncluded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsIncluded)));
            _inclusionChanged(this);
        }
    }

    internal Task PendingThumbnail { get; private set; } = Task.CompletedTask;

    internal bool ThumbnailRequested => _thumbnailRequested;

    public BitmapSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                PendingThumbnail = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    /// <param name="position">The row's place in the list, counting left-out images.</param>
    /// <param name="number">The image's place in the PDF, or 0 when it is left out.</param>
    internal void SetPlace(int position, int number)
    {
        string label = Detail.Length == 0 ? Title : $"{Title}, {Detail}";
        Number = number > 0 ? number.ToString(UiText.Culture) : string.Empty;
        AutomationName = number > 0
            ? UiText.Format("ContactSheet.OrderItem", number, label)
            : UiText.Format("ContactSheet.OrderItemExcluded", position, label);
        IncludeName = UiText.Format("ContactSheet.IncludeItem", position);
    }

    private async Task LoadThumbnailAsync()
    {
        try
        {
            BitmapSource? decoded = await _loadThumbnail(_cancellation);
            if (_cancellation.IsCancellationRequested || decoded is null) return;
            _thumbnail = decoded;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or System.IO.InvalidDataException) { }
    }

    private void Set(ref string field, string value, string name)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
