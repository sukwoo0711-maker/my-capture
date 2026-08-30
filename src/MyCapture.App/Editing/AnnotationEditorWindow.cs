using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Editing;

/// <summary>
/// Resizable, taskbar-visible editor host used after capture selection and for gallery re-edit.
/// </summary>
internal class AnnotationEditorWindow : Window
{
    private readonly AnnotationEditorControl _editor;
    private bool _committed;

    internal AnnotationEditorWindow(
        FrozenFrame sourceFrame,
        RectD sourceRegion,
        BitmapSource selectedBitmap,
        string title = "MyCapture — 캡처 편집",
        AnnotationDocument? initialDocument = null,
        IReadOnlyDictionary<string, BitmapSource>? initialAssets = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ArgumentNullException.ThrowIfNull(selectedBitmap);

        Title = title;
        Background = Application.Current?.TryFindResource("Surface.Base") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x22));
        Foreground = Application.Current?.TryFindResource("Text.Primary") as Brush ?? Brushes.White;
        FontFamily = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = 680;
        MinHeight = 460;

        Rect work = SystemParameters.WorkArea;
        double desiredWidth = Math.Max(980, selectedBitmap.PixelWidth + 360);
        double desiredHeight = Math.Max(620, selectedBitmap.PixelHeight + 156);
        Width = Math.Min(desiredWidth, Math.Max(MinWidth, work.Width - 64));
        Height = Math.Min(desiredHeight, Math.Max(MinHeight, work.Height - 64));

        AutomationProperties.SetName(this, "캡처 편집 창");

        _editor = new AnnotationEditorControl(
            sourceFrame,
            sourceRegion,
            selectedBitmap,
            initialDocument,
            initialAssets);
        _editor.EditingCompleted += OnEditingCompleted;
        _editor.EditingCancelled += OnEditingCancelled;
        Content = _editor;

        ContentRendered += OnContentRendered;
    }

    internal Func<AnnotationEditingResult, bool>? CommitRequested
    {
        get => _editor.CommitRequested;
        set => _editor.CommitRequested = value;
    }

    internal event EventHandler<AnnotationEditingResult>? Committed;

    internal event EventHandler? Cancelled;

    internal bool WasCommitted => _committed;

    internal AnnotationEditorControl Editor => _editor;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_editor.HandleKeyDown(e))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _ = Activate();
        _ = _editor.Focus();
        _ = Keyboard.Focus(_editor);
    }

    private void OnEditingCompleted(object? sender, AnnotationEditingResult e)
    {
        _committed = true;
        Committed?.Invoke(this, e);
        Close();
    }

    private void OnEditingCancelled(object? sender, EventArgs e)
    {
        Cancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _editor.EditingCompleted -= OnEditingCompleted;
        _editor.EditingCancelled -= OnEditingCancelled;
        ContentRendered -= OnContentRendered;
        base.OnClosed(e);
    }
}
