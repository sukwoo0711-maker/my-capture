using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Inserting an image into the editor offers the capture library first, with the file dialog as
/// one of its ways out; library pictures go in as their current (edited) look.
/// </summary>
public sealed class EditorLibraryImagePickerTests
{
    private static readonly string[] Names = ["rendered.png", "original.png", "thumb.jpg"];

    // ---- Source ---------------------------------------------------------------------

    [Fact]
    public void Source_ListsStillImagesNewestFirst_WithoutRecordingsOrTheEditedCapture() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-lib-picker-src-");
        try
        {
            var queue = new CaptureQueue(AppPaths.CreateForRoot(root), new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            DateTimeOffset now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
            CaptureRecord old = Record(now.AddHours(-3), CaptureMediaKind.Image, "old");
            CaptureRecord recent = Record(now.AddHours(-1), CaptureMediaKind.Image, "recent");
            CaptureRecord video = Record(now.AddHours(-2), CaptureMediaKind.Video, "clip");
            CaptureRecord edited = Record(now, CaptureMediaKind.Image, "being edited");
            foreach (CaptureRecord record in new[] { old, recent, video, edited }) queue.Add(record);

            string editedOriginal = queue.GetFilePath(edited, CaptureFileNames.Original);
            IReadOnlyList<LibraryImageEntry> entries = LibraryImageSource.Create(queue, editedOriginal);

            Assert.Equal([recent.Id, old.Id], entries.Select(entry => entry.Id).ToArray());
            Assert.Equal(["recent", "old"], entries.Select(entry => entry.Title).ToArray());
            // Without an edited capture everything that is a still image is offered.
            Assert.Equal([edited.Id, recent.Id, old.Id], LibraryImageSource.Create(queue, null).Select(entry => entry.Id).ToArray());
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void FinalImagePath_PrefersTheEditedResult_ThenTheOriginal()
    {
        string root = OwnedTestDirectory.Create("mc-lib-picker-final-");
        try
        {
            Assert.Null(LibraryImageSource.FinalImagePath(root));
            File.WriteAllBytes(Path.Combine(root, CaptureFileNames.Original), [1]);
            Assert.Equal(Path.Combine(root, CaptureFileNames.Original), LibraryImageSource.FinalImagePath(root));
            File.WriteAllBytes(Path.Combine(root, CaptureFileNames.Rendered), [1]);
            Assert.Equal(Path.Combine(root, CaptureFileNames.Rendered), LibraryImageSource.FinalImagePath(root));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    // ---- Dialog ---------------------------------------------------------------------

    [Fact]
    public void Dialog_ShowsEntriesInOrder_AndReportsThePickedOnesInThatOrder() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-lib-picker-dialog-");
        try
        {
            LibraryImageEntry[] entries = [.. Enumerable.Range(0, 5).Select(i => Entry(root, i, 200 + i, 120))];
            var dialog = new LibraryImagePickerDialog(entries, new GalleryThumbnailLoader((_, _) => null));
            Assert.False(string.IsNullOrWhiteSpace(dialog.Title));
            Assert.False(dialog.ShowsEmptyState);
            Assert.Equal(entries.Select(entry => entry.Id).ToArray(), dialog.Tiles.Select(tile => tile.Entry.Id).ToArray());
            Assert.False(dialog.InsertButton.IsEnabled);
            Assert.Equal(SelectionMode.Extended, dialog.List.SelectionMode);

            // Clicked in reverse, reported newest first.
            dialog.List.SelectedItems.Add(dialog.Tiles[3]);
            dialog.List.SelectedItems.Add(dialog.Tiles[1]);
            Assert.True(dialog.InsertButton.IsEnabled);

            LibraryImagePick? result = ShowAndClick(dialog, dialog.InsertButton);
            Assert.NotNull(result);
            Assert.False(result.Browse);
            Assert.Equal([entries[1].Id, entries[3].Id], result.Entries.Select(entry => entry.Id).ToArray());
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Dialog_BrowseAndCancel_ReportTheirOwnOutcome() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-lib-picker-browse-");
        try
        {
            LibraryImageEntry[] entries = [Entry(root, 0, 100, 100)];
            var browse = new LibraryImagePickerDialog(entries, new GalleryThumbnailLoader((_, _) => null));
            LibraryImagePick? picked = ShowAndClick(browse, browse.BrowseButton);
            Assert.NotNull(picked);
            Assert.True(picked.Browse);
            Assert.Empty(picked.Entries);

            var cancel = new LibraryImagePickerDialog(entries, new GalleryThumbnailLoader((_, _) => null));
            Assert.Null(ShowAndClick(cancel, cancel.CancelButton));
            Assert.True(cancel.CancelButton.IsCancel);
            Assert.True(cancel.InsertButton.IsDefault);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Dialog_WithAnEmptyLibrary_ShowsTheMessageAndTheFileButtonOnly() => StaTestHost.Run(() =>
    {
        var dialog = new LibraryImagePickerDialog([], new GalleryThumbnailLoader((_, _) => null));
        Assert.True(dialog.ShowsEmptyState);
        Assert.False(dialog.InsertButton.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(UiText.Get("ImagePicker_Empty")));
        Assert.True(dialog.BrowseButton.IsEnabled);
        Assert.Empty(dialog.List.Items);
        LibraryImagePick? picked = ShowAndClick(dialog, dialog.BrowseButton);
        Assert.True(picked?.Browse);
    });

    [Fact]
    public void Dialog_FitsASmallScreen_AndNamesItsParts()
    {
        StaTestHost.Run(() =>
        {
            var dialog = new LibraryImagePickerDialog([Entry(OwnedTestDirectory.Create("mc-lib-picker-size-"), 0, 10, 10)],
                new GalleryThumbnailLoader((_, _) => null));
            Assert.True(dialog.Width <= 1024 - 40 && dialog.Height <= 768 - 40, "The picker must fit a 1024x768 screen.");
            Assert.True(dialog.MinWidth <= 1024 - 40 && dialog.MinHeight <= 768 - 40);
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(dialog)));
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(dialog.List)));
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(dialog.InsertButton)));
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(dialog.BrowseButton)));
            Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d, title 0$", dialog.Tiles[0].AutomationName);
        });
    }

    [Fact]
    public void Dialog_RealizesOnlyTheTilesInView_AndDecodesThemSmall() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-lib-picker-virtual-");
        try
        {
            string thumbs = Path.Combine(root, "t");
            var requests = new List<(string Path, int Size)>();
            LibraryImageEntry[] entries =
            [
                .. Enumerable.Range(0, 600).Select(i => new LibraryImageEntry(Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-i),
                    "", () => Path.Combine(thumbs, $"{i}.jpg"), () => null)),
            ];
            var loader = new GalleryThumbnailLoader((path, size) =>
            {
                lock (requests) requests.Add((path, size));
                return null;
            });
            LibraryImagePickerDialog dialog = ShowOffscreen(new LibraryImagePickerDialog(entries, loader));
            try
            {
                dialog.UpdateLayout();
                TileGridPanel panel = Descendant<TileGridPanel>(dialog.List);
                PumpUntil(() => { lock (requests) return requests.Count > 0; });
                Pump(TimeSpan.FromMilliseconds(150));
                Assert.InRange(panel.RealizedCount, 1, 80);
                lock (requests)
                {
                    Assert.InRange(requests.Count, 1, 80);
                    Assert.All(requests, request => Assert.Equal(LibraryImageTile.ThumbnailLongEdge, request.Size));
                    Assert.All(requests, request => Assert.EndsWith(".jpg", request.Path));
                }

                // Scrolling to the end realizes the last tiles and lets go of the first ones.
                dialog.List.ScrollIntoView(dialog.List.Items[^1]);
                dialog.UpdateLayout();
                Pump(TimeSpan.FromMilliseconds(100));
                Assert.InRange(panel.RealizedCount, 1, 80);
                Assert.NotNull(dialog.List.ItemContainerGenerator.ContainerFromIndex(599));
            }
            finally
            {
                dialog.Close();
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Dialog_ArrowKeysMoveTheSelectionAcrossTheGrid() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-lib-picker-keys-");
        try
        {
            LibraryImageEntry[] entries = [.. Enumerable.Range(0, 40).Select(i => Entry(root, i, 40, 30))];
            LibraryImagePickerDialog dialog = ShowOffscreen(new LibraryImagePickerDialog(entries, new GalleryThumbnailLoader((_, _) => null)));
            try
            {
                dialog.UpdateLayout();
                int columns = Descendant<TileGridPanel>(dialog.List).Columns;
                Assert.True(columns >= 2);
                Press(dialog, Key.Home);
                Assert.Equal(0, dialog.List.SelectedIndex);
                Press(dialog, Key.Right);
                Assert.Equal(1, dialog.List.SelectedIndex);
                Press(dialog, Key.Down);
                Assert.Equal(1 + columns, dialog.List.SelectedIndex);
                Press(dialog, Key.Left);
                Assert.Equal(columns, dialog.List.SelectedIndex);
                Press(dialog, Key.Up);
                Assert.Equal(0, dialog.List.SelectedIndex);
                Press(dialog, Key.End);
                Assert.Equal(39, dialog.List.SelectedIndex);
                Assert.Single(dialog.List.SelectedItems);
            }
            finally
            {
                dialog.Close();
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Dialog_EvidenceRender() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-lib-picker-render-");
        string? evidence = TestDiagnosticArtifacts.Create("MYCAPTURE_EDLIB_EVIDENCE", "picker");
        try
        {
            LibraryImageEntry[] entries = [.. Enumerable.Range(0, 14).Select(i => Entry(root, i, 320, 200, i % 2 == 0 ? "" : "회의 메모"))];
            LibraryImagePickerDialog filled = ShowOffscreen(new LibraryImagePickerDialog(entries));
            try
            {
                filled.List.SelectedItems.Add(filled.Tiles[1]);
                filled.List.SelectedItems.Add(filled.Tiles[2]);
                PumpUntil(() => filled.Tiles.Take(6).All(tile => tile.Thumbnail is not null));
                Pump(TimeSpan.FromMilliseconds(300));
                if (evidence is not null) TestDiagnosticArtifacts.SavePng(evidence, "picker-filled", Render(filled));
            }
            finally
            {
                filled.Close();
            }

            LibraryImagePickerDialog empty = ShowOffscreen(new LibraryImagePickerDialog([]));
            try
            {
                Pump(TimeSpan.FromMilliseconds(200));
                if (evidence is not null) TestDiagnosticArtifacts.SavePng(evidence, "picker-empty", Render(empty));
            }
            finally
            {
                empty.Close();
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    // ---- Editor ---------------------------------------------------------------------

    [Fact]
    public void ImageTool_OffersTheLibraryFirst_AndInsertsTheEditedLookAtItsOwnSize() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-lib-picker-insert-");
        try
        {
            LibraryImageEntry withResult = Entry(root, 0, 100, 100, rendered: (320, 200));
            LibraryImageEntry plain = Entry(root, 1, 64, 48);
            AnnotationEditorWindow window = OpenEditor();
            try
            {
                int fileDialogs = 0;
                IReadOnlyList<LibraryImageEntry>? offered = null;
                window.Editor.LibraryImagesOverride = () => [withResult, plain];
                window.Editor.PickImageFilePathOverride = () => { fileDialogs++; return null; };
                window.Editor.LibraryImagePickerOverride = entries =>
                {
                    offered = entries;
                    return new LibraryImagePick([entries[0]], Browse: false);
                };

                Task insert = window.Editor.InsertImageCommandAsync(new PointD(300, 200));
                PumpUntil(() => insert.IsCompleted);
                Assert.Equal(0, fileDialogs);
                Assert.Equal(2, offered!.Count);
                ImageAnnotation picture = Assert.Single(Pictures(window));
                Assert.Equal((320, 200), (picture.SourceWidth, picture.SourceHeight));
                Assert.Equal(EditorTool.Select, Controller(window).Tool);
                Assert.Equal(320, StoredBitmap(window, picture).PixelWidth);

                // The capture can leave the library afterwards; the picture stays in the editor.
                Directory.Delete(Path.Combine(root, "e0"), recursive: true);
                Assert.Equal(320, StoredBitmap(window, picture).PixelWidth);
                Assert.Equal(picture.Id, Assert.Single(Pictures(window)).Id);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ImageTool_InsertsSeveralLibraryPicturesCascaded_EachOneUndoable() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-lib-picker-many-");
        try
        {
            LibraryImageEntry[] entries = [Entry(root, 0, 90, 60), Entry(root, 1, 90, 60), Entry(root, 2, 90, 60)];
            AnnotationEditorWindow window = OpenEditor();
            try
            {
                window.Editor.LibraryImagesOverride = () => entries;
                window.Editor.LibraryImagePickerOverride = all => new LibraryImagePick(all, Browse: false);
                Task insert = window.Editor.InsertImageCommandAsync(new PointD(300, 200));
                PumpUntil(() => insert.IsCompleted);

                IReadOnlyList<ImageAnnotation> pictures = Pictures(window);
                Assert.Equal(3, pictures.Count);
                Assert.Equal(3, pictures.Select(picture => picture.AssetFileName).Distinct().Count());
                double step = AnnotationEditorControl.InsertCascadeStep;
                Assert.Equal(step, pictures[1].Rect.X - pictures[0].Rect.X, 6);
                Assert.Equal(step, pictures[1].Rect.Y - pictures[0].Rect.Y, 6);
                Assert.Equal(2 * step, pictures[2].Rect.X - pictures[0].Rect.X, 6);
                Assert.Equal(EditorTool.Select, Controller(window).Tool);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ImageTool_BrowseGoesThroughTheFileDialog_CancelLeavesNothing() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-lib-picker-file-");
        try
        {
            string file = Path.Combine(root, "from-disk.png");
            ImageCodec.SavePng(EditorKeysTests.Solid(150, 90, 0x40), file);
            AnnotationEditorWindow window = OpenEditor();
            try
            {
                window.Editor.LibraryImagesOverride = () => [Entry(root, 0, 40, 40)];
                int asked = 0;
                window.Editor.LibraryImagePickerOverride = _ => new LibraryImagePick([], Browse: true);
                window.Editor.PickImageFilePathOverride = () => { asked++; return file; };
                Task insert = window.Editor.InsertImageCommandAsync(new PointD(300, 200));
                PumpUntil(() => insert.IsCompleted);
                Assert.Equal(1, asked);
                Assert.Equal((150, 90), (Assert.Single(Pictures(window)).SourceWidth, Pictures(window)[0].SourceHeight));

                window.Editor.LibraryImagePickerOverride = _ => null;
                Task cancelled = window.Editor.InsertImageCommandAsync(new PointD(300, 200));
                PumpUntil(() => cancelled.IsCompleted);
                Assert.Equal(1, asked);
                Assert.Single(Pictures(window));
                Assert.Equal(EditorTool.Select, Controller(window).Tool);

                // A cancelled file dialog behind the picker also leaves nothing behind.
                window.Editor.LibraryImagePickerOverride = _ => new LibraryImagePick([], Browse: true);
                window.Editor.PickImageFilePathOverride = () => { asked++; return null; };
                Task browseCancelled = window.Editor.InsertImageCommandAsync(new PointD(300, 200));
                PumpUntil(() => browseCancelled.IsCompleted);
                Assert.Equal(2, asked);
                Assert.Single(Pictures(window));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ImageTool_WithoutALibrarySource_OpensTheFileDialogDirectly() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-lib-picker-none-");
        try
        {
            string file = Path.Combine(root, "plain.png");
            ImageCodec.SavePng(EditorKeysTests.Solid(120, 80, 0x50), file);
            AnnotationEditorWindow window = OpenEditor();
            try
            {
                Assert.Null(AnnotationEditorPreferences.LibraryImages);
                bool pickerShown = false;
                window.Editor.LibraryImagePickerOverride = _ => { pickerShown = true; return null; };
                window.Editor.PickImageFilePathOverride = () => file;
                Task insert = window.Editor.InsertImageCommandAsync(new PointD(300, 200));
                PumpUntil(() => insert.IsCompleted);
                Assert.False(pickerShown);
                Assert.Single(Pictures(window));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    // ---- Helpers --------------------------------------------------------------------

    private static CaptureRecord Record(DateTimeOffset createdAt, CaptureMediaKind kind, string title)
    {
        Guid id = Guid.NewGuid();
        return new CaptureRecord
        {
            Id = id,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            MediaKind = kind,
            Title = title,
            RelativeDirectory = CaptureQueue.BuildRelativeDirectory(id, createdAt),
        };
    }

    /// <summary>A library entry whose files live in <c>root\e{index}</c>; the edited look is only written when asked for.</summary>
    private static LibraryImageEntry Entry(string root, int index, int width, int height, string? title = null,
        (int Width, int Height)? rendered = null)
    {
        string directory = Path.Combine(root, $"e{index}");
        Directory.CreateDirectory(directory);
        byte shade = (byte)(40 + (index * 17 % 180));
        ImageCodec.SavePng(EditorKeysTests.Solid(width, height, shade), Path.Combine(directory, CaptureFileNames.Original));
        ImageCodec.SavePng(EditorKeysTests.Solid(Math.Max(1, width / 2), Math.Max(1, height / 2), shade),
            Path.Combine(directory, CaptureFileNames.Thumbnail));
        if (rendered is { } look)
        {
            ImageCodec.SavePng(EditorKeysTests.Solid(look.Width, look.Height, (byte)(shade + 20)), Path.Combine(directory, CaptureFileNames.Rendered));
        }

        return new LibraryImageEntry(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero).AddMinutes(-index),
            title ?? $"title {index}",
            () => Path.Combine(directory, CaptureFileNames.Thumbnail),
            () => LibraryImageSource.FinalImagePath(directory));
    }

    private static AnnotationEditorWindow OpenEditor()
    {
        BitmapSource bitmap = EditorKeysTests.Solid(640, 360, 0x30);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 1000, Height = 640,
            ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static LibraryImagePickerDialog ShowOffscreen(LibraryImagePickerDialog dialog)
    {
        dialog.Resources = SystemHighContrastThemeTests.LoadTheme();
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -4000;
        dialog.Top = -4000;
        dialog.ShowActivated = false;
        dialog.Show();
        dialog.UpdateLayout();
        return dialog;
    }

    /// <summary>Shows the dialog modally off-screen and clicks <paramref name="button"/> once it is up.</summary>
    private static LibraryImagePick? ShowAndClick(LibraryImagePickerDialog dialog, Button button)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -4000;
        dialog.Top = -4000;
        dialog.ShowActivated = false;
        dialog.Dispatcher.BeginInvoke(() =>
        {
            if (button == dialog.InsertButton) dialog.Accept();
            // Invoke runs the button's own click handling, which a cancel button needs to close the dialog.
            else ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
        }, DispatcherPriority.Background);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private static void Press(LibraryImagePickerDialog dialog, Key key)
    {
        var source = PresentationSource.FromVisual(dialog)!;
        dialog.List.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        Pump(TimeSpan.FromMilliseconds(30));
    }

    private static T Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindOrNull<T>(child) is { } nested) return nested;
        }

        throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} under {root.GetType().Name}.");
    }

    private static T? FindOrNull<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindOrNull<T>(child) is { } nested) return nested;
        }

        return null;
    }

    private static RenderTargetBitmap Render(Window window)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        return image;
    }

    private static AnnotationEditorController Controller(AnnotationEditorWindow window) =>
        (AnnotationEditorController)typeof(AnnotationEditorControl)
            .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;

    private static IReadOnlyList<ImageAnnotation> Pictures(AnnotationEditorWindow window) =>
        [.. Controller(window).Document.Items.OfType<ImageAnnotation>()];

    private static BitmapSource StoredBitmap(AnnotationEditorWindow window, ImageAnnotation picture)
    {
        var store = (AnnotationImageStore)typeof(AnnotationEditorControl)
            .GetField("_imageStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        return store.Get(picture.AssetFileName) ?? throw new Xunit.Sdk.XunitException("The picture's pixels are not kept in memory.");
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(done(), "Timed out waiting.");
    }

    /// <summary>Runs async continuations on this test thread's dispatcher, as in the app.</summary>
    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        internal DispatcherContextScope() =>
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
