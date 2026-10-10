using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>
/// A folder shown in the library body, so a folder created for today is visible beside
/// today's captures and opens like the matching tree node.
/// </summary>
/// <remarks>
/// Cards are cached by folder id across refreshes, like tiles, so a rebuild keeps focus and
/// bindings. The preview reuses the newest capture's tile and therefore its bounded thumbnail
/// cache instead of decoding a second copy.
/// </remarks>
public sealed class GalleryFolderCardViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private int _captureCount;
    private int _folderCount;
    private GalleryItemViewModel? _preview;

    internal GalleryFolderCardViewModel(Guid folderId)
    {
        FolderId = folderId;
        Scope = CaptureFolderScope.ForFolder(folderId);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid FolderId { get; }

    /// <summary>The library scope this card opens.</summary>
    public CaptureFolderScope Scope { get; }

    public string Name => _name;

    /// <summary>Captures in this folder and all of its subfolders.</summary>
    public int CaptureCount => _captureCount;

    /// <summary>Direct subfolders, the folders shown after opening this card.</summary>
    public int FolderCount => _folderCount;

    /// <summary>The newest capture in this folder tree, or <see langword="null"/> when it is empty.</summary>
    public GalleryItemViewModel? Preview => _preview;

    public bool HasPreview => _preview is not null;

    public string CountText => _folderCount > 0
        ? UiText.Format("LibraryFolder_CardCountWithFolders", _captureCount, _folderCount)
        : UiText.Format("LibraryFolder_CardCount", _captureCount);

    public string AccessibleName => UiText.Format("LibraryFolder_CardAccessibleName", _name, CountText);

    public string ToolTipText => _name + Environment.NewLine + CountText + Environment.NewLine
        + UiText.Get("LibraryFolder_CardOpenHint");

    private bool _isDropAvailable;
    private bool _isDropTarget;

    /// <summary>True while a capture drag is in progress and this folder could receive it.</summary>
    public bool IsDropAvailable
    {
        get => _isDropAvailable;
        internal set { if (_isDropAvailable != value) { _isDropAvailable = value; Raise(); } }
    }

    /// <summary>True while dragged captures are over this card and would move into it.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        internal set { if (_isDropTarget != value) { _isDropTarget = value; Raise(); } }
    }

    internal void Update(string name, int captureCount, int folderCount, GalleryItemViewModel? preview)
    {
        bool nameChanged = _name != name;
        bool countChanged = _captureCount != captureCount || _folderCount != folderCount;
        bool previewChanged = !ReferenceEquals(_preview, preview);
        _name = name;
        _captureCount = captureCount;
        _folderCount = folderCount;
        _preview = preview;
        if (nameChanged) Raise(nameof(Name));
        if (countChanged)
        {
            Raise(nameof(CaptureCount));
            Raise(nameof(FolderCount));
            Raise(nameof(CountText));
        }
        if (previewChanged)
        {
            Raise(nameof(Preview));
            Raise(nameof(HasPreview));
        }
        if (nameChanged || countChanged)
        {
            Raise(nameof(AccessibleName));
            Raise(nameof(ToolTipText));
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
