using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// One-shot editor operations that used to do redundant or blocking work on the UI thread
/// (mosaic, crop, image insertion, font list, history and inspector refresh) keep their
/// pixels, history and visible state.
/// </summary>
public sealed class EditorHeavyOperationTests
{
    // ---- Mosaic --------------------------------------------------------------------

    public static TheoryData<string, int> FormatsAndTurns()
    {
        var data = new TheoryData<string, int>();
        foreach (string format in new[] { "Bgra32", "Pbgra32", "Bgr32", "Bgr24" })
            for (int turns = 0; turns < 4; turns++) data.Add(format, turns);
        return data;
    }

    [Theory]
    [MemberData(nameof(FormatsAndTurns))]
    public void MosaicPatchesMatchTheWholeImageReferenceOnEveryRegionAndEdge(string format, int turns) => StaTestHost.Run(() =>
    {
        foreach ((int width, int height) in new[] { (97, 61), (64, 48) })
        {
            AnnotationEditorControl editor = Editor(Pattern(width, height, Format(format)));
            for (int turn = 0; turn < turns; turn++) Assert.True(editor.HandleShortcut(Key.W, ModifierKeys.None));
            AssertMosaicsMatchReference(editor);
        }
    });

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Bgr24")]
    public void MosaicOverACroppedBaseMatchesTheWholeImageReference(string format) => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor(Pattern(97, 61, Format(format)));
        Assert.True(editor.HandleShortcut(Key.W, ModifierKeys.None));
        editor.ApplyCrop(new RectD(5, 9, 40, 70));
        AssertMosaicsMatchReference(editor);
    });

    private static void AssertMosaicsMatchReference(AnnotationEditorControl editor)
    {
        AnnotationEditorController controller = Controller(editor);
        AnnotationImageStore store = Field<AnnotationImageStore>(editor, "_imageStore");
        BitmapSource visible = editor.DisplayedBitmap;
        int w = visible.PixelWidth, h = visible.PixelHeight;
        RectD[] regions =
        [
            new(10.3, 7.6, 21.2, 19.9),           // fractional, inside
            new(w - 26.4, h - 18.7, 29, 23),      // runs past the right and bottom edges
            new(30, 25, -22.5, -17.25),           // dragged up and left
            new(0, 0, w, h),                      // the whole canvas
            new(w - 2, h - 2, 2, 2),              // the smallest patch, in the corner
            new(-5, -5, 20, 20),                  // starts outside the top-left corner
            new(3, 4, 1.5, 9),                    // too thin: no patch
        ];
        foreach (RectD region in regions)
            foreach (int block in new[] { SettingsRanges.MosaicBlockSize.Min, 7, 12, SettingsRanges.MosaicBlockSize.Max })
            {
                int itemsBefore = controller.Document.Items.Count;
                byte[]? expected = LegacyMosaic(visible, region, block, out int x, out int y, out int pw, out int ph);
                editor.ApplyMosaic(region, block);
                EditorMosaicAsyncTests.WaitFor(editor.MosaicOperationsSettledAsync());
                Assert.Same(visible, editor.DisplayedBitmap);
                if (expected is null)
                {
                    Assert.Equal(itemsBefore, controller.Document.Items.Count);
                    continue;
                }

                ImageAnnotation patch = Assert.IsType<ImageAnnotation>(controller.Document.Items[^1]);
                Assert.Equal(new RectD(x, y, pw, ph), patch.Rect);
                Assert.Equal(pw, patch.SourceWidth);
                Assert.Equal(ph, patch.SourceHeight);
                BitmapSource asset = store.Get(patch.AssetFileName)!;
                Assert.True(asset.IsFrozen);
                Assert.Equal(PixelFormats.Bgra32, asset.Format);
                Assert.Equal(96, asset.DpiX, 5);
                Assert.Equal(96, asset.DpiY, 5);
                Assert.Equal(expected, Pixels(asset));
            }
    }

    // The pre-change implementation, kept as an independent byte reference: it copies the
    // whole base and addresses it with four-byte pixels whatever the source format.
    private static byte[]? LegacyMosaic(BitmapSource mosaicBase, RectD region, int blockSize,
        out int x, out int y, out int width, out int height)
    {
        RectD normalized = region.Normalized().ClampTo(new RectD(0, 0, mosaicBase.PixelWidth, mosaicBase.PixelHeight));
        x = (int)Math.Floor(normalized.X);
        y = (int)Math.Floor(normalized.Y);
        width = (int)Math.Ceiling(normalized.Width);
        height = (int)Math.Ceiling(normalized.Height);
        if (width < 2 || height < 2) return null;
        int step = Math.Clamp(blockSize, SettingsRanges.MosaicBlockSize.Min, SettingsRanges.MosaicBlockSize.Max);
        int stride = mosaicBase.PixelWidth * 4;
        byte[] source = new byte[stride * mosaicBase.PixelHeight];
        mosaicBase.CopyPixels(source, stride, 0);
        int patchStride = width * 4;
        byte[] patch = new byte[patchStride * height];
        for (int by = 0; by < height; by += step)
        {
            int blockH = Math.Min(step, height - by);
            for (int bx = 0; bx < width; bx += step)
            {
                int blockW = Math.Min(step, width - bx);
                long sumB = 0, sumG = 0, sumR = 0, count = 0;
                for (int py = 0; py < blockH; py++)
                {
                    int row = (y + by + py) * stride;
                    for (int px = 0; px < blockW; px++)
                    {
                        int i = row + ((x + bx + px) * 4);
                        if (source[i + 3] == 0) continue;
                        sumB += source[i];
                        sumG += source[i + 1];
                        sumR += source[i + 2];
                        count++;
                    }
                }
                if (count == 0) continue;
                for (int py = 0; py < blockH; py++)
                    for (int px = 0; px < blockW; px++)
                    {
                        int di = ((by + py) * patchStride) + ((bx + px) * 4);
                        patch[di] = (byte)(sumB / count);
                        patch[di + 1] = (byte)(sumG / count);
                        patch[di + 2] = (byte)(sumR / count);
                        patch[di + 3] = 255;
                    }
            }
        }
        return patch;
    }

    // ---- Crop ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(FormatsAndTurns))]
    public void CropCutsTheBaseOnceAndHistoryStillRestoresIt(string format, int turns) => StaTestHost.Run(() =>
    {
        BitmapSource source = Pattern(79, 53, Format(format));
        AnnotationEditorControl editor = Editor(source);
        for (int turn = 0; turn < turns; turn++) Assert.True(editor.HandleShortcut(Key.W, ModifierKeys.None));
        BitmapSource oriented = editor.DisplayedBitmap;
        AnnotationEditorController controller = Controller(editor);
        int refreshes = 0;
        BitmapSource? atRefresh = null;
        // Registered after the editor's own handler, so it sees the base that refresh left.
        controller.Undo.Changed += (_, _) => { refreshes++; atRefresh = editor.DisplayedBitmap; };
        var region = new RectD(oriented.PixelWidth - 26.4, oriented.PixelHeight - 18.7, 29, 23);
        RectD pixels = region.Normalized().ClampTo(new RectD(0, 0, oriented.PixelWidth, oriented.PixelHeight)).ToPixelBounds();
        BitmapSource expected = LegacyCrop(oriented, (int)pixels.X, (int)pixels.Y, (int)pixels.Width, (int)pixels.Height);

        editor.ApplyCrop(region);

        Assert.Equal(1, refreshes);
        // The crop's history refresh already shows the final base: nothing is cut or swapped again.
        Assert.Same(atRefresh, editor.DisplayedBitmap);
        AssertBitmap(expected, editor.DisplayedBitmap);
        Assert.Equal(new RectD(0, 0, pixels.Width, pixels.Height), editor.DisplayedRegion);
        Assert.Equal((int)pixels.Width, controller.Document.CanvasWidth);
        Assert.Equal((int)pixels.Height, controller.Document.CanvasHeight);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        AssertBitmap(oriented, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
        AssertBitmap(expected, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        AssertBitmap(oriented, editor.DisplayedBitmap);
        // A second crop after undo still cuts from the base the user sees.
        editor.ApplyCrop(region);
        AssertBitmap(expected, editor.DisplayedBitmap);
    });

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Bgr24")]
    public void WholeCanvasCropRecordsNothingAndStillShowsTheCutCopy(string format) => StaTestHost.Run(() =>
    {
        BitmapSource source = Pattern(41, 29, Format(format));
        AnnotationEditorControl editor = Editor(source);
        AnnotationEditorController controller = Controller(editor);
        int refreshes = 0;
        controller.Undo.Changed += (_, _) => refreshes++;

        editor.ApplyCrop(new RectD(0, 0, 41, 29));

        Assert.Equal(0, refreshes);
        Assert.False(controller.CanUndo);
        Assert.NotSame(source, editor.DisplayedBitmap);
        AssertBitmap(LegacyCrop(source, 0, 0, 41, 29), editor.DisplayedBitmap);
    });

    // ---- Image insertion -----------------------------------------------------------

    [Fact]
    public void PickedImageDecodesWithoutHoldingTheUiThreadAndInsertsAsOneUndoStep() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-insert-");
        try
        {
            string path = Path.Combine(root, "large.png");
            ImageCodec.SavePng(Pattern(1500, 900, PixelFormats.Bgra32), path);
            BitmapSource reference = AnnotationImageStore.Decode(path)!.Bitmap;
            using var host = EditorWindowHost.Create();
            AnnotationEditorControl editor = host.Editor;
            AnnotationEditorController controller = Controller(editor);
            AnnotationImageStore store = Field<AnnotationImageStore>(editor, "_imageStore");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The production decoder, started only once the test opens the gate.
            Func<string, Task<AnnotationImageStore.DecodedFile?>> decode = editor.DecodeImageForInsert;
            editor.DecodeImageForInsert = async file =>
            {
                await gate.Task.ConfigureAwait(false);
                return await decode(file).ConfigureAwait(false);
            };
            Invoke(editor, "SelectTool", EditorTool.Image);

            Task insert = editor.InsertImageFileAsync(path, new PointD(200, 120));
            Assert.True(editor.IsEditingOperationInProgress);

            // The UI thread keeps running while the picture is still being read.
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(insert.IsCompleted);
            Assert.Empty(controller.Document.Items);
            Assert.False(controller.CanUndo);
            // Meanwhile the canvas is already on the tool both outcomes end on.
            Assert.Equal(EditorTool.Select, controller.Tool);
            gate.SetResult();
            PumpUntil(() => insert.IsCompleted);
            insert.GetAwaiter().GetResult();

            Assert.False(editor.IsEditingOperationInProgress);
            ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
            Assert.Equal(1500, item.SourceWidth);
            Assert.Equal(900, item.SourceHeight);
            Assert.Equal(LegacyInsertRect(640, 360, 1500, 900, new PointD(200, 120)), item.Rect);
            Assert.Equal("image-01.png", item.AssetFileName);
            Assert.Equal(1, controller.Undo.UndoCount);
            Assert.Same(item, controller.Selected);
            Assert.Equal(EditorTool.Select, controller.Tool);
            Assert.True(editor.ToolButton(EditorTool.Select).IsChecked == true);
            BitmapSource asset = store.Get(item.AssetFileName)!;
            Assert.True(asset.IsFrozen);
            Assert.Equal(reference.PixelWidth, asset.PixelWidth);
            Assert.Equal(reference.PixelHeight, asset.PixelHeight);
            Assert.Equal(reference.Format, asset.Format);
            Assert.Equal(Pixels(reference), Pixels(asset));
            Assert.Equal(Path.GetFullPath(path), store.SourcesFor([item.AssetFileName])[item.AssetFileName]);

            Assert.True(controller.PerformUndo());
            Assert.Empty(controller.Document.Items);
            Assert.NotNull(store.Get(item.AssetFileName));
            Assert.True(controller.PerformRedo());
            Assert.Same(item, Assert.Single(controller.Document.Items));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PictureStillDecodingWhenTheEditorUnloadsIsDropped(bool unreadable) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        Window window = Host(editor);
        AnnotationEditorController controller = Controller(editor);
        AnnotationImageStore store = Field<AnnotationImageStore>(editor, "_imageStore");
        var decoded = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reports = 0;
        editor.DecodeImageForInsert = _ => decoded.Task;
        editor.ReportUnreadableImage = () => reports++;

        Task insert = editor.InsertImageFileAsync("picked.png", new PointD(50, 50));
        Assert.True(editor.IsEditingOperationInProgress);
        window.Close();
        // Closing ends the wait at once; a decode that finishes later changes nothing.
        PumpUntil(() => insert.IsCompleted);
        decoded.SetResult(unreadable ? null : Decoded(Pattern(30, 20, PixelFormats.Bgra32)));
        Pump(TimeSpan.FromMilliseconds(50));

        insert.GetAwaiter().GetResult();
        Assert.False(editor.IsEditingOperationInProgress);
        Assert.Empty(controller.Document.Items);
        Assert.False(controller.CanUndo);
        Assert.Null(store.Get("image-01.png"));
        Assert.Equal(0, reports);
    });

    public static TheoryData<string> CommitActions() =>
    [
        nameof(EditorCommitAction.Done), nameof(EditorCommitAction.QuickSave), nameof(EditorCommitAction.SaveAs),
        nameof(EditorCommitAction.CopyToClipboard),
    ];

    [Theory]
    [MemberData(nameof(CommitActions))]
    public void CommitDuringAPendingPictureWaitsOnceAndSavesThePicture(string actionName) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        EditorCommitAction action = Enum.Parse<EditorCommitAction>(actionName);
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        AnnotationEditorController controller = Controller(editor);
        var decoded = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.DecodeImageForInsert = _ => decoded.Task;
        var commits = new List<AnnotationEditingResult>();
        editor.CommitRequested = result => { commits.Add(result); return Task.FromResult(true); };
        int completed = 0;
        editor.EditingCompleted += (_, _) => completed++;

        Task insert = editor.InsertImageFileAsync("picked.png", new PointD(20, 20));
        Assert.True(editor.IsEditingOperationInProgress);
        Invoke(editor, "Commit", action, false);
        Assert.True(editor.IsCommitInProgress);
        Assert.False(editor.IsHitTestVisible);
        // Further commands while the commit waits start nothing, as during a save.
        Invoke(editor, "Commit", action, false);
        editor.SaveAndClose();
        editor.ShortcutActions["Save"]();
        editor.ShortcutActions["SaveAs"]();
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.Empty(commits);
        Assert.Empty(controller.Document.Items);

        decoded.SetResult(Decoded(Pattern(30, 20, PixelFormats.Bgra32)));
        bool copy = action == EditorCommitAction.CopyToClipboard;
        PumpUntil(() => insert.IsCompleted && commits.Count == 1 && (copy ? !editor.IsCommitInProgress : completed == 1));
        Pump(TimeSpan.FromMilliseconds(100));

        insert.GetAwaiter().GetResult();
        AnnotationEditingResult saved = Assert.Single(commits);
        Assert.Equal(action, saved.Action);
        ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(saved.Document.Items));
        Assert.Equal(LegacyInsertRect(64, 48, 30, 20, new PointD(20, 20)), item.Rect);
        BitmapSource asset = Assert.Contains(item.AssetFileName, saved.ImageAssetBitmaps);
        Assert.Equal(30, asset.PixelWidth);
        Assert.Equal(20, asset.PixelHeight);
        Assert.Equal(copy ? 0 : 1, completed);
        if (copy)
        {
            // Copy keeps the editor open and editable, with the picture in it.
            Assert.False(editor.IsEditingOperationInProgress);
            Assert.True(editor.IsHitTestVisible);
            Assert.Same(item, Assert.Single(controller.Document.Items));
        }
    });

    [Fact]
    public void UnreadablePictureDuringAWaitingCommitIsReportedThenTheCommitSavesWithoutIt() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        var decoded = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.DecodeImageForInsert = _ => decoded.Task;
        var order = new List<string>();
        var commits = new List<AnnotationEditingResult>();
        editor.ReportUnreadableImage = () => order.Add("unreadable");
        editor.CommitRequested = result => { order.Add("commit"); commits.Add(result); return Task.FromResult(true); };

        Task insert = editor.InsertImageFileAsync("not-an-image.png", new PointD(20, 20));
        editor.SaveAndClose();
        decoded.SetResult(null);
        PumpUntil(() => insert.IsCompleted && commits.Count == 1);

        insert.GetAwaiter().GetResult();
        Assert.Equal(["unreadable", "commit"], order);
        Assert.Empty(Assert.Single(commits).Document.Items);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PictureIsAddedBeforeTheCommitAppliesAnOpenCropFrame(bool unreadable) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        var decoded = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.DecodeImageForInsert = _ => decoded.Task;
        int reports = 0;
        editor.ReportUnreadableImage = () => reports++;
        var commits = new List<AnnotationEditingResult>();
        editor.CommitRequested = result => { commits.Add(result); return Task.FromResult(true); };

        Task insert = editor.InsertImageFileAsync("picked.png", new PointD(20, 20));
        Invoke(editor, "SelectTool", EditorTool.Crop);
        editor.PendingCrop!.SetRegion(new RectD(8, 6, 40, 30));
        Invoke(editor, "RefreshCrop", true);
        editor.SaveAndClose();
        Assert.True(editor.IsCommitInProgress);
        decoded.SetResult(unreadable ? null : Decoded(Pattern(30, 20, PixelFormats.Bgra32)));
        PumpUntil(() => insert.IsCompleted && commits.Count == 1);

        insert.GetAwaiter().GetResult();
        AnnotationEditingResult saved = Assert.Single(commits);
        // The crop the user framed is applied either way.
        Assert.Equal(40, saved.Document.CanvasWidth);
        Assert.Equal(30, saved.Document.CanvasHeight);
        Assert.Equal(40, saved.SelectedBitmap.PixelWidth);
        Assert.Equal(unreadable ? 1 : 0, reports);
        if (unreadable)
        {
            Assert.Empty(saved.Document.Items);
            return;
        }

        // Placed at the click on the uncropped canvas, then moved with the crop, as the
        // synchronous insert followed by the crop placed it.
        RectD placed = LegacyInsertRect(64, 48, 30, 20, new PointD(20, 20));
        ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(saved.Document.Items));
        Assert.Equal(new RectD(placed.X - 8, placed.Y - 6, placed.Width, placed.Height), item.Rect);
        Assert.Contains(item.AssetFileName, saved.ImageAssetBitmaps);
    });

    [Fact]
    public void EditorUnloadedWhileACommitWaitsForAPictureAbandonsTheCommit() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        Window window = Host(editor);
        var decoded = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.DecodeImageForInsert = _ => decoded.Task;
        int commits = 0, completed = 0;
        editor.CommitRequested = _ => { commits++; return Task.FromResult(true); };
        editor.EditingCompleted += (_, _) => completed++;

        Task insert = editor.InsertImageFileAsync("picked.png", new PointD(20, 20));
        editor.SaveAndClose();
        Assert.True(editor.IsCommitInProgress);
        window.Close();
        PumpUntil(() => insert.IsCompleted && !editor.IsCommitInProgress);
        decoded.SetResult(Decoded(Pattern(30, 20, PixelFormats.Bgra32)));
        Pump(TimeSpan.FromMilliseconds(100));

        insert.GetAwaiter().GetResult();
        Assert.Equal(0, commits);
        Assert.Equal(0, completed);
        Assert.False(editor.IsEditingOperationInProgress);
        Assert.Empty(Controller(editor).Document.Items);
    });

    [Fact]
    public void PictureStartedDuringASaveWaitsForItAndIsAddedWhenTheEditorStaysOpen() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        AnnotationEditorController controller = Controller(editor);
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.DecodeImageForInsert = _ => Task.FromResult<AnnotationImageStore.DecodedFile?>(Decoded(Pattern(30, 20, PixelFormats.Bgra32)));
        editor.CommitRequested = _ => saved.Task;

        editor.SaveAndClose();
        Assert.True(editor.IsCommitInProgress);
        Task insert = editor.InsertImageFileAsync("picked.png", new PointD(20, 20));
        Pump(TimeSpan.FromMilliseconds(150));
        Assert.False(insert.IsCompleted);
        Assert.Empty(controller.Document.Items);

        saved.SetResult(false); // The save failed: the editor stays open and editable.
        PumpUntil(() => insert.IsCompleted);
        insert.GetAwaiter().GetResult();
        Assert.False(editor.IsCommitInProgress);
        ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
        Assert.Equal(LegacyInsertRect(64, 48, 30, 20, new PointD(20, 20)), item.Rect);
    });

    [Fact]
    public void UnreadablePictureIsStillReportedAndReturnsToSelect() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        AnnotationEditorController controller = Controller(editor);
        int reports = 0;
        editor.ReportUnreadableImage = () => reports++;
        string root = OwnedTestDirectory.Create("mc-insert-bad-");
        try
        {
            string path = Path.Combine(root, "not-an-image.png");
            File.WriteAllText(path, "not an image");
            Invoke(editor, "SelectTool", EditorTool.Image);

            Task insert = editor.InsertImageFileAsync(path, new PointD(20, 20));
            PumpUntil(() => insert.IsCompleted);

            insert.GetAwaiter().GetResult();
            Assert.Equal(1, reports);
            Assert.Equal(EditorTool.Select, controller.Tool);
            Assert.Empty(controller.Document.Items);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void PictureWaitsForACropFrameAndIsCentredWhenTheCanvasTurned() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor(Pattern(64, 48, PixelFormats.Bgra32));
        AnnotationEditorController controller = Controller(editor);
        var first = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Queue<Task<AnnotationImageStore.DecodedFile?>>([first.Task, second.Task]);
        editor.DecodeImageForInsert = _ => pending.Dequeue();

        // A crop frame opened while the picture decodes: the picture lands only after it closes.
        Task waiting = editor.InsertImageFileAsync("first.png", new PointD(10, 12));
        Invoke(editor, "SelectTool", EditorTool.Crop);
        Assert.NotNull(editor.PendingCrop);
        first.SetResult(Decoded(Pattern(12, 6, PixelFormats.Bgra32)));
        Pump(TimeSpan.FromMilliseconds(150));
        Assert.False(waiting.IsCompleted);
        Assert.Empty(controller.Document.Items);
        editor.CancelPendingCrop();
        PumpUntil(() => waiting.IsCompleted);
        waiting.GetAwaiter().GetResult();
        ImageAnnotation placed = Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
        Assert.Equal(LegacyInsertRect(64, 48, 12, 6, new PointD(10, 12)), placed.Rect);

        // A rotation while the picture decodes moved the clicked pixels: centre it instead.
        Task turned = editor.InsertImageFileAsync("second.png", new PointD(10, 12));
        Assert.True(editor.HandleShortcut(Key.W, ModifierKeys.None));
        second.SetResult(Decoded(Pattern(12, 6, PixelFormats.Bgra32)));
        PumpUntil(() => turned.IsCompleted);
        turned.GetAwaiter().GetResult();
        ImageAnnotation centred = Assert.IsType<ImageAnnotation>(controller.Document.Items[^1]);
        Assert.Equal(LegacyInsertRect(48, 64, 12, 6, new PointD(24, 32)), centred.Rect);
    });

    // The insertion geometry before the change: at most a third of the canvas, centred on the click.
    private static RectD LegacyInsertRect(int canvasWidth, int canvasHeight, int sourceWidth, int sourceHeight, PointD click)
    {
        double maxWidth = canvasWidth / 3;
        double maxHeight = canvasHeight / 3;
        double aspect = sourceHeight > 0 ? (double)sourceWidth / sourceHeight : 1.0;
        double width = Math.Min(sourceWidth, maxWidth);
        double height = width / aspect;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * aspect;
        }
        return new RectD(click.X - (width / 2), click.Y - (height / 2), width, height);
    }

    private static AnnotationImageStore.DecodedFile Decoded(BitmapSource bitmap) =>
        new(bitmap, ".png", Path.GetFullPath("picked.png"));

    [Fact]
    public void ImageStoreDecodeAndRegisterMatchLoadFromFile() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-insert-store-");
        try
        {
            string path = Path.Combine(root, "Picture.PNG");
            ImageCodec.SavePng(Pattern(33, 21, PixelFormats.Bgra32), path);
            var legacy = new AnnotationImageStore();
            var split = new AnnotationImageStore();
            (BitmapSource Bitmap, string AssetFileName) loaded = legacy.LoadFromFile(path)!.Value;
            AnnotationImageStore.DecodedFile decoded = Threading.StaThreadTask.RunAsync(() => AnnotationImageStore.Decode(path))
                .GetAwaiter().GetResult()!;
            (BitmapSource Bitmap, string AssetFileName) registered = split.Register(decoded);

            Assert.True(decoded.Bitmap.IsFrozen);
            Assert.Equal(loaded.AssetFileName, registered.AssetFileName);
            Assert.Equal("image-01.png", registered.AssetFileName);
            Assert.Equal(Pixels(loaded.Bitmap), Pixels(registered.Bitmap));
            Assert.Equal(legacy.SourcesFor([loaded.AssetFileName]), split.SourcesFor([registered.AssetFileName]));
            Assert.Null(AnnotationImageStore.Decode(Path.Combine(root, "missing.png")));
            Assert.Null(AnnotationImageStore.Decode(" "));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // ---- Font list -----------------------------------------------------------------

    [Fact]
    public void SystemFontListIsLocalizedSortedAndCachedAcrossThreads() => StaTestHost.Run(() =>
    {
        foreach (string language in new[] { "ko-KR", "en-US", "sv-SE" })
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(language);
            IReadOnlyList<FontChoice> sorted = AnnotationEditorControl.SortedSystemFontFamilies(culture);
            Assert.Equal(Fonts.SystemFontFamilies.Select(font => font.Source).Distinct(StringComparer.OrdinalIgnoreCase).Order(),
                sorted.Select(font => font.Value).Order());
            Assert.Equal(sorted.OrderBy(font => font.Label, StringComparer.Create(culture, true)), sorted);
            Assert.Same(sorted, AnnotationEditorControl.SortedSystemFontFamilies(culture));
            Assert.Same(sorted, Task.Run(() => AnnotationEditorControl.SortedSystemFontFamilies(culture)).GetAwaiter().GetResult());
            Assert.Same(sorted, MyCapture.App.Settings.SettingsWindow.InstalledFonts(culture));
        }
    });

    [Fact]
    public void FontPickerSharesRecommendedOrderPreservesSavedValuesAndExpandsInstalledFonts() => StaTestHost.Run(() =>
    {
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? write = AnnotationEditorPreferences.Write;
        AnnotationEditorPreferences.Read = () => new AnnotationDefaults
        {
            FontFamily = "Not Installed Family",
            RecentFontFamilies = ["Arial", "Another Missing Family"],
        };
        AnnotationEditorPreferences.Write = _ => { };
        try
        {
            AnnotationEditorControl first = Editor(Pattern(64, 48, PixelFormats.Bgra32));
            AnnotationEditorControl second = Editor(Pattern(64, 48, PixelFormats.Bgra32));
            foreach (AnnotationEditorControl editor in new[] { first, second })
                Invoke(editor, "SelectTool", EditorTool.Text);
            var expected = FontCatalog.ChoicesFor("Not Installed Family", FontCatalog.RecommendedFonts(UiText.Culture));
            ComboBox picker = Field<ComboBox>(first, "_textFontFamily");
            var firstChoices = picker.Items.Cast<FontChoice>().ToArray();
            var secondChoices = Field<ComboBox>(second, "_textFontFamily").Items.Cast<FontChoice>().ToArray();
            Assert.Equal(expected, firstChoices);
            Assert.Equal(expected, secondChoices);
            Assert.Equal("Not Installed Family", Assert.IsType<FontChoice>(picker.SelectedItem).Value);
            Assert.Same(firstChoices[1], secondChoices[1]);
            Assert.DoesNotContain(firstChoices, font => font.Value == "Another Missing Family");

            var caption = new TextAnnotation { Text = "x", FontFamily = "Caption Only Family", Rect = new RectD(4, 4, 30, 12) };
            Controller(first).Document.Add(caption);
            Controller(first).SetSelected(caption);
            Assert.Equal(FontCatalog.ChoicesFor(caption.FontFamily, FontCatalog.RecommendedFonts(UiText.Culture)), picker.Items.Cast<FontChoice>());
            Assert.Equal(caption.FontFamily, Assert.IsType<FontChoice>(picker.SelectedItem).Value);
            int undoCount = Controller(first).Undo.UndoCount;
            picker.SelectedItem = picker.Items.Cast<FontChoice>().Single(font => font.IsMore);
            Assert.Equal("Caption Only Family", caption.FontFamily);
            Assert.Equal(undoCount, Controller(first).Undo.UndoCount);
            Assert.Equal(FontCatalog.ChoicesFor(caption.FontFamily, FontCatalog.AvailableFonts(UiText.Culture)), picker.Items.Cast<FontChoice>());
            Assert.Equal(caption.FontFamily, Assert.IsType<FontChoice>(picker.SelectedItem).Value);
        }
        finally { AnnotationEditorPreferences.Read = read; AnnotationEditorPreferences.Write = write; }
    });

    // ---- History bookkeeping -------------------------------------------------------

    [Fact]
    public void PruneReleasesExactlyTheUnreachableAssetsAndSkipsHistoryWhenNothingCanBeReleased() => StaTestHost.Run(() =>
    {
        var document = AnnotationDocument.CreateFor(100, 100);
        var history = new UndoStack();
        var store = new AnnotationImageStore();
        var counted = new CountingCommand("history.png");
        history.Execute(counted);
        counted.Enumerations = 0;

        // Nothing held: the history is not walked at all.
        store.PruneToReachable(document, history);
        Assert.Equal(0, counted.Enumerations);

        // Everything held is on the canvas: the history is not walked either.
        store.Seed(new Dictionary<string, BitmapSource> { ["Live.png"] = Image() });
        document.Add(new ImageAnnotation { AssetFileName = "live.png" });
        store.PruneToReachable(document, history);
        Assert.Equal(0, counted.Enumerations);
        Assert.NotNull(store.Get("live.png"));

        // Held only by history: kept. Held by nothing: released.
        store.Seed(new Dictionary<string, BitmapSource> { ["HISTORY.png"] = Image(), ["orphan.png"] = Image() });
        store.PruneToReachable(document, history);
        Assert.Equal(1, counted.Enumerations);
        Assert.NotNull(store.Get("history.png"));
        Assert.NotNull(store.Get("live.png"));
        Assert.Null(store.Get("orphan.png"));
    });

    private sealed class CountingCommand(string asset) : IUndoableCommand
    {
        public int Enumerations { get; set; }
        public string Description => "counted";
        public void Execute() { }
        public void Undo() { }
        public IEnumerable<string> ReferencedImageAssets
        {
            get
            {
                Enumerations++;
                yield return asset;
            }
        }
    }

    // ---- Inspector -----------------------------------------------------------------

    [Fact]
    public void InspectorRefreshSetsOnlyWhatChanged() => StaTestHost.Run(() =>
    {
        bool? animations = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? write = AnnotationEditorPreferences.Write;
        AnnotationEditorPreferences.Read = () => new AnnotationDefaults();
        AnnotationEditorPreferences.Write = _ => { };
        AnnotationEditorControl editor = Editor(Pattern(320, 180, PixelFormats.Bgra32));
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        var window = new Window
        {
            Content = editor, Resources = resources, Width = 1000, Height = 640, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            AnnotationEditorController controller = Controller(editor);
            Invoke(editor, "SelectTool", EditorTool.Rectangle);

            // Undo tooltip: an equal text is not set again.
            var first = new RectangleAnnotation { Rect = new RectD(10, 10, 40, 30), StrokeThickness = 3 };
            controller.Undo.Execute(new AddAnnotationCommand(controller.Document, first));
            Button undo = Field<Button>(editor, "_undoButton");
            object tip = undo.ToolTip;
            Invoke(editor, "RefreshHistoryButtons");
            Invoke(editor, "UpdateInspector");
            Assert.Same(tip, undo.ToolTip);
            Assert.Equal(UiText.Format("EditorSelection_UndoHint", controller.Undo.NextUndoDescription!), undo.ToolTip);

            // Colour rings: the current swatch follows the theme resource; the others are clear.
            Button[] swatches = Field<WrapPanel>(editor, "_swatchPanel").Children.OfType<Button>().ToArray();
            ColorRgba red = (ColorRgba)swatches[0].Tag;
            ColorRgba blue = (ColorRgba)swatches[3].Tag;
            controller.ApplyStrokeColor(red);
            Invoke(editor, "UpdateInspector");
            AssertRings(swatches, red, resources);
            object ringExpression = ((Border)swatches[0].Content).ReadLocalValue(Border.BorderBrushProperty);
            Invoke(editor, "UpdateInspector");
            Assert.Same(ringExpression, ((Border)swatches[0].Content).ReadLocalValue(Border.BorderBrushProperty));
            controller.ApplyStrokeColor(blue);
            Invoke(editor, "UpdateInspector");
            AssertRings(swatches, blue, resources);
            ThemeService.ApplyResources(AppTheme.Daylight, resources, systemHighContrast: false);
            window.UpdateLayout();
            AssertRings(swatches, blue, resources);

            // Thickness: a thick-dashed shape selected with a pen keeps the minimum at one
            // without passing the value through six and back.
            var dashed = new RectangleAnnotation
            {
                Rect = new RectD(60, 60, 40, 30), StrokeThickness = 3, StrokeStyle = AnnotationStrokeStyle.ThickDashed,
            };
            var pen = new PenAnnotation { Points = [new PointD(5, 5), new PointD(30, 40)], StrokeThickness = 3 };
            controller.SetSelected(null);
            controller.Document.Add(pen);
            controller.Document.Add(dashed);
            Slider thickness = Field<Slider>(editor, "_thicknessSlider");
            Assert.Equal(1, thickness.Minimum);
            Assert.Equal(3, thickness.Value);
            int changes = 0;
            thickness.ValueChanged += (_, _) => changes++;
            controller.SelectAll();
            Assert.Same(dashed, controller.Selected);
            Assert.Equal(1, thickness.Minimum);
            Assert.Equal(3, thickness.Value);
            Assert.Equal(0, changes);
            // A thick-dashed shape alone still raises the minimum to six.
            controller.SetSelected(dashed);
            Assert.Equal(6, thickness.Minimum);
            Assert.Equal(6, thickness.Value);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = animations;
            AnnotationEditorPreferences.Read = read;
            AnnotationEditorPreferences.Write = write;
        }
    });

    private static void AssertRings(Button[] swatches, ColorRgba current, ResourceDictionary resources)
    {
        foreach (Button swatch in swatches)
        {
            var ring = (Border)swatch.Content;
            if ((ColorRgba)swatch.Tag == current)
            {
                Assert.IsAssignableFrom<Expression>(ring.ReadLocalValue(Border.BorderBrushProperty));
                Assert.Same(resources["Text.Primary"], ring.BorderBrush);
                Assert.Equal(UiText.Get("EditorProperty_SelectedColor"), System.Windows.Automation.AutomationProperties.GetItemStatus(swatch));
            }
            else
            {
                Assert.Same(Brushes.Transparent, ring.ReadLocalValue(Border.BorderBrushProperty));
                Assert.Equal(string.Empty, System.Windows.Automation.AutomationProperties.GetItemStatus(swatch));
            }
        }
    }

    // ---- Helpers -------------------------------------------------------------------

    private static AnnotationEditorControl Editor(BitmapSource bitmap)
    {
        var bounds = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        return new AnnotationEditorControl(new FrozenFrame(bitmap, bounds, null, 0), bounds, bitmap, null, null);
    }

    private static Window Host(AnnotationEditorControl editor)
    {
        var window = new Window
        {
            Content = editor, Width = 800, Height = 600, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static PixelFormat Format(string name) => name switch
    {
        "Bgra32" => PixelFormats.Bgra32,
        "Pbgra32" => PixelFormats.Pbgra32,
        "Bgr32" => PixelFormats.Bgr32,
        "Bgr24" => PixelFormats.Bgr24,
        _ => throw new ArgumentException(name),
    };

    // Varied channels and alpha, including fully transparent pixels that the mosaic skips.
    private static BitmapSource Pattern(int width, int height, PixelFormat format)
    {
        int channels = format.BitsPerPixel / 8;
        int stride = width * channels;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * channels;
                byte alpha = (x + y) % 11 == 0 ? (byte)0 : (byte)((x * 29 + y * 37) % 256);
                for (int channel = 0; channel < 3; channel++)
                {
                    byte value = (byte)((x * 17 + y * 13 + channel * 71) % 256);
                    pixels[index + channel] = format == PixelFormats.Pbgra32 ? (byte)(value * alpha / 255) : value;
                }
                if (channels == 4) pixels[index + 3] = alpha;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, 144, 120, format, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, 8);
        image.Freeze();
        return image;
    }

    private static BitmapSource LegacyCrop(BitmapSource source, int x, int y, int width, int height)
    {
        int stride = source.PixelWidth * 4;
        byte[] full = new byte[stride * source.PixelHeight];
        source.CopyPixels(full, stride, 0);
        int cropStride = width * 4;
        byte[] crop = new byte[cropStride * height];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(full, ((y + row) * stride) + x * 4, crop, row * cropStride, cropStride);
        BitmapSource bitmap = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, crop, cropStride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        int stride = source.PixelWidth * ((source.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void AssertBitmap(BitmapSource expected, BitmapSource actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(expected.DpiX, actual.DpiX, 5);
        Assert.Equal(expected.DpiY, actual.DpiY, 5);
        Assert.Equal(expected.Format, actual.Format);
        Assert.True(actual.IsFrozen);
        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for the dispatcher.");
            PumpOnce();
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration) PumpOnce();
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        var resume = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background,
            (sender, _) => { ((DispatcherTimer)sender!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        resume.Start();
        Dispatcher.PushFrame(frame);
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
