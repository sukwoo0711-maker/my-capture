using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;

namespace MyCapture.App.Editing;

/// <summary>
/// The reusable stamp: a logo or blog name registered once, then placed on any capture in one step.
/// </summary>
/// <remarks>
/// <para>
/// A placed stamp is an ordinary image annotation, so it can be moved, resized, deleted, undone
/// and is saved and exported like any inserted picture. The stamp command only chooses its first
/// position and opacity. The picture is stored once under the data folder (see
/// <see cref="StampStore"/>) and read when the command is used, never when the editor opens.
/// </para>
/// <para>
/// A placed stamp the user moved or resized is remembered when the editor saves or closes, so the
/// next stamp lands where this one was left. Only stamps made by this command are remembered.
/// </para>
/// </remarks>
internal sealed partial class AnnotationEditorControl
{
    private ToggleButton _stampButton = null!;
    private ContextMenu? _stampMenu;
    private MenuItem? _stampChangeItem;
    private MenuItem? _stampRemoveItem;
    private readonly List<MenuItem> _stampOpacityItems = [];
    private bool _stampBusy;
    private readonly List<PlacedStamp> _placedStamps = [];

    /// <summary>A stamp this command placed, with the canvas it was laid out on.</summary>
    private sealed record PlacedStamp(Guid Id, RectD Rect, (int Turns, PointD CropOrigin, int Width, int Height) Canvas);

    /// <summary>A rail button that runs a command rather than choosing a tool, so it never shows as selected.</summary>
    private sealed class RailCommandButton : ToggleButton
    {
        protected override void OnToggle()
        {
        }
    }

    /// <summary>
    /// Shows the file picker for the stamp image and returns the chosen path, or <see langword="null"/>
    /// when cancelled. Tests substitute the modal dialog.
    /// </summary>
    internal Func<string?> PickStampImagePath { get; set; } = static () =>
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Stamp_PickerTitle"),
            Filter = UiText.Get("Text_DC9F5ED04461"),
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>The stamp command on the tool rail, for tests.</summary>
    internal ToggleButton StampButton => _stampButton;

    /// <summary>The stamp's context menu, built on first use; <see langword="null"/> until then.</summary>
    internal ContextMenu? StampMenu => _stampMenu;

    /// <summary>Builds the stamp menu if needed and returns it, as opening it does.</summary>
    internal ContextMenu EnsureStampMenu() => StampContextMenu();

    /// <summary>The ids of the stamps this editor placed and still remembers, for tests.</summary>
    internal IReadOnlyList<Guid> PlacedStampIds => [.. _placedStamps.Select(stamp => stamp.Id)];

    private ToggleButton BuildStampButton()
    {
        string label = UiText.Get("Stamp_Place");
        var button = new RailCommandButton
        {
            Content = BuildIcon("Icon.Stamp", FallbackStamp, 20),
            ToolTip = label,
            Width = 40, Height = 38, MinHeight = 0, MinWidth = 0, Padding = new Thickness(2), Margin = new Thickness(0, 1, 0, 1),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Rail.ToolButton");
        AutomationName(button, $"{label} ({ShortcutCatalog.Get(ShortcutScope.Annotation, "Stamp").Hint})");
        AttachEditorShortcut(button, ShortcutScope.Annotation, "Stamp", corner: true);
        // The plain "name (key)" tooltip hides that the options live on the right button,
        // so the stamp keeps its longer hint, which still ends with the key.
        button.ToolTip = UiText.Get("Stamp_ToolTip");
        PreserveEditorMouseFocus(button);
        button.Click += (_, _) => PlaceStampFromUser();
        // WPF needs a menu before the first opening event. Populate its items only on use;
        // constructing this empty menu does not look up the registered picture.
        button.ContextMenu = new ContextMenu { MinWidth = 210 };
        button.ContextMenuOpening += (_, _) => StampContextMenu();
        _stampButton = button;
        return button;
    }

    private static Geometry FallbackStamp() => Geometry.Parse("M7.75,10 C7.75,8.2 8.5,7.6 8.5,6 A1.5,1.5 0 0 1 11.5,6 C11.5,7.6 12.25,8.2 12.25,10 M4.5,10 H15.5 A1,1 0 0 1 16.5,11 V13.5 H3.5 V11 A1,1 0 0 1 4.5,10 Z M3.5,16.75 H16.5");

    private ContextMenu StampContextMenu()
    {
        if (_stampMenu is not null) return _stampMenu;

        ContextMenu menu = _stampButton.ContextMenu!;
        var place = new MenuItem
        {
            Header = UiText.Get("Stamp_Place"),
            InputGestureText = ShortcutCatalog.Get(ShortcutScope.Annotation, "Stamp").Hint,
        };
        place.Click += (_, _) => PlaceStampFromUser();
        AutomationName(place, UiText.Get("Stamp_Place"));

        _stampChangeItem = new MenuItem { Header = UiText.Get("Stamp_Change") };
        _stampChangeItem.Click += (_, _) => ChangeStampImageFromUser();
        AutomationName(_stampChangeItem, UiText.Get("Stamp_Change"));

        var opacity = new MenuItem { Header = UiText.Get("Stamp_Opacity") };
        AutomationName(opacity, UiText.Get("Stamp_Opacity"));
        foreach (int percent in StampSettings.OpacityChoices)
        {
            var item = new MenuItem
            {
                Header = UiText.Format("Stamp_OpacityPercent", percent),
                IsCheckable = true,
                Tag = percent,
            };
            AutomationName(item, UiText.Format("Stamp_OpacityPercent", percent));
            item.Click += (_, _) => SetStampOpacity(percent);
            _stampOpacityItems.Add(item);
            opacity.Items.Add(item);
        }

        _stampRemoveItem = new MenuItem { Header = UiText.Get("Stamp_Remove") };
        _stampRemoveItem.Click += (_, _) => RemoveStampImage();
        AutomationName(_stampRemoveItem, UiText.Get("Stamp_Remove"));

        menu.Items.Add(place);
        menu.Items.Add(_stampChangeItem);
        menu.Items.Add(opacity);
        menu.Items.Add(new Separator());
        menu.Items.Add(_stampRemoveItem);
        menu.Opened += (_, _) => RefreshStampMenu();
        _stampMenu = menu;
        return menu;
    }

    /// <summary>Marks the current opacity and enables Remove only while a stamp is registered.</summary>
    private void RefreshStampMenu()
    {
        string? folder = StampStore.Folder;
        if (_stampRemoveItem is not null)
        {
            _stampRemoveItem.IsEnabled = !_stampBusy && folder is not null && StampStore.RegisteredPath(folder) is not null;
        }

        int current = StampSettingsNow().OpacityPercent;
        int nearest = StampSettings.OpacityChoices.MinBy(choice => Math.Abs(choice - current));
        foreach (MenuItem item in _stampOpacityItems)
        {
            item.IsChecked = item.Tag is int percent && percent == nearest;
        }
    }

    private StampSettings StampSettingsNow()
    {
        StampSettings stamp = AnnotationEditorPreferences.Read?.Invoke().Stamp.Clone() ?? new StampSettings();
        stamp.Normalize();
        return stamp;
    }

    private async void PlaceStampFromUser() => await PlaceStampAsync();

    private async void ChangeStampImageFromUser() => await ChangeStampImageAsync();

    /// <summary>
    /// The stamp command: adds the registered stamp at the remembered placement, or, when none is
    /// registered, asks for a picture, registers it and adds it.
    /// </summary>
    internal Task PlaceStampAsync() => RunStampAsync(changeImageOnly: false);

    /// <summary>Asks for a picture and makes it the stamp, without adding anything to the canvas.</summary>
    internal Task ChangeStampImageAsync() => RunStampAsync(changeImageOnly: true);

    private async Task RunStampAsync(bool changeImageOnly)
    {
        if (_completed || _commitInProgress || _stampBusy) return;
        string? folder = StampStore.Folder;
        if (folder is null)
        {
            SetStatus(UiText.Get("Stamp_Unavailable"));
            return;
        }

        _stampBusy = true;
        if (_stampRemoveItem is not null) _stampRemoveItem.IsEnabled = false;
        try
        {
            string? stored = changeImageOnly ? null : StampStore.RegisteredPath(folder);
            string? picked = null;
            if (stored is null)
            {
                picked = PickStampImagePath();
                if (picked is null) return;
            }

            // As when choosing the image tool, leave an unapplied crop before inserting.
            // Otherwise the image waits for the crop, and closing waits for that image.
            if (!changeImageOnly && _crop is not null) SelectTool(EditorTool.Select);

            // Reading, saving and adding the picture count as an editing operation, as for any
            // inserted picture: a save or close started meanwhile waits for it.
            await WhilePendingImageAsync(async () =>
            {
                CancellationToken closed = (_imageInsertCts ??= new CancellationTokenSource()).Token;
                if (picked is not null)
                {
                    stored = await RegisterStampAsync(folder, picked, closed);
                    if (stored is null || changeImageOnly)
                    {
                        if (stored is not null) SetStatus(UiText.Get("Stamp_Changed"));
                        return;
                    }
                }

                await InsertPendingImageAsync(stored!, default, asStamp: true);
            });
        }
        finally
        {
            _stampBusy = false;
            if (_stampMenu is not null) RefreshStampMenu();
        }
    }

    /// <summary>
    /// Copies the picked picture into the stamp folder, scaled down if it is large. Returns the
    /// stored path, or <see langword="null"/> after reporting why it could not be stored.
    /// </summary>
    private async Task<string?> RegisterStampAsync(string folder, string source, CancellationToken closed)
    {
        AnnotationImageStore.DecodedFile? decoded;
        try
        {
            decoded = await DecodeImageForInsert(source).WaitAsync(closed);
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested)
        {
            return null;
        }

        if (IsImageInsertClosed(closed)) return null;
        if (decoded is null)
        {
            ReportUnreadableImage();
            return null;
        }

        try
        {
            BitmapSource pixels = decoded.Bitmap;
            await StaThreadTask.RunAsync(() => StampStore.Save(pixels, folder), "MyCapture stamp writer").WaitAsync(closed);
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            SetStatus(UiText.Get("Stamp_SaveFailed"));
            return null;
        }

        return StampStore.PathIn(folder);
    }

    /// <summary>
    /// Adds the decoded stamp picture as an image annotation at the remembered placement on the
    /// canvas as it is now, with the remembered opacity, and remembers it for placement updates.
    /// </summary>
    private void AddStampAnnotation(string assetFileName, BitmapSource bitmap)
    {
        StampSettings stamp = StampSettingsNow();
        int sourceWidth = bitmap.PixelWidth;
        int sourceHeight = bitmap.PixelHeight;
        RectD rect = StampLayout.Place(
            stamp.Anchor, stamp.MarginX, stamp.MarginY, stamp.WidthFraction,
            _canvasWidth, _canvasHeight, sourceWidth, sourceHeight);
        ImageAnnotation image = _controller.AddImageAnnotation(
            assetFileName, sourceWidth, sourceHeight, rect, stamp.OpacityPercent / 100.0);
        _placedStamps.Add(new PlacedStamp(image.Id, rect, CanvasGeometry()));
        SyncToolButtons();
        SetStatus(UiText.Format("EditorFlow_Added", UiText.Get("Stamp_Name")));
    }

    /// <summary>The stored picture could not be decoded: forget it so the next use asks for a new one.</summary>
    private void ForgetUnreadableStamp()
    {
        if (StampStore.Folder is { } folder) StampStore.Delete(folder);
        SetStatus(UiText.Get("Stamp_Unreadable"));
    }

    private (int Turns, PointD CropOrigin, int Width, int Height) CanvasGeometry() =>
        (_controller.RotationTurns, _controller.CropOrigin, _canvasWidth, _canvasHeight);

    /// <summary>
    /// Sets the opacity of stamps to come, and of the selected stamp if it was placed by this
    /// command (one undo step).
    /// </summary>
    private void SetStampOpacity(int percent)
    {
        StampSettings stamp = StampSettingsNow();
        stamp.OpacityPercent = percent;
        RememberPreferences(stamp);

        if (_controller.Selected is ImageAnnotation selected && _placedStamps.Any(placed => placed.Id == selected.Id))
        {
            double before = selected.Opacity;
            double after = percent / 100.0;
            if (Math.Abs(before - after) > 1e-9)
            {
                _controller.Undo.Execute(new PropertyChangeCommand<ImageAnnotation, double>(
                    selected, UiText.Get("Stamp_Opacity"), static (target, value) => target.Opacity = value, before, after));
                _surface.InvalidateVisual();
            }
        }

        SetStatus(UiText.Format("Stamp_OpacitySet", percent));
    }

    /// <summary>
    /// Deletes the stored picture and forgets the placement. Stamps already on the canvas are
    /// ordinary images and stay.
    /// </summary>
    private void RemoveStampImage()
    {
        if (_stampBusy) return;
        if (StampStore.Folder is not { } folder || !StampStore.Delete(folder)) return;
        _placedStamps.Clear();
        RememberPreferences(new StampSettings());
        SetStatus(UiText.Get("Stamp_Removed"));
    }

    /// <summary>
    /// Remembers where the user left the stamp they placed: the nine-grid cell, margins and width
    /// as a fraction of the canvas. Nothing is written for a stamp that was not moved or resized,
    /// that was rotated, or whose canvas was rotated or cropped since it was placed.
    /// </summary>
    private void RememberStampPlacement()
    {
        if (_placedStamps.Count == 0 || AnnotationEditorPreferences.Write is null) return;

        (int Turns, PointD CropOrigin, int Width, int Height) canvas = CanvasGeometry();
        for (int index = _placedStamps.Count - 1; index >= 0; index--)
        {
            PlacedStamp placed = _placedStamps[index];
            ImageAnnotation? item = _controller.Document.Items.OfType<ImageAnnotation>().FirstOrDefault(image => image.Id == placed.Id);
            if (item is null) continue;

            // The newest stamp still on the canvas decides.
            if (placed.Canvas != canvas || Math.Abs(item.RotationDegrees) > 1e-6) return;
            if (StampLayout.AreSame(item.Rect, placed.Rect)) return;

            StampSettings stamp = StampSettingsNow();
            (StampAnchor anchor, double marginX, double marginY, double fraction) =
                StampLayout.Describe(item.Rect, _canvasWidth, _canvasHeight, stamp.MarginX, stamp.MarginY);
            stamp.Anchor = anchor;
            stamp.MarginX = marginX;
            stamp.MarginY = marginY;
            stamp.WidthFraction = fraction;
            stamp.Normalize();
            RememberPreferences(stamp);
            _placedStamps[index] = placed with { Rect = item.Rect };
            return;
        }
    }
}
