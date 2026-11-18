using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Export;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The PDF preview follows the chosen row, and rows are dragged the way Windows reorders a list.</summary>
public sealed class GalleryContactSheetPreviewTests
{
    private static readonly Color[] Palette = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow, Colors.Magenta, Colors.Cyan];

    [Fact]
    public void ChosenRowTurnsThePreviewToItsPageAndRingsIt() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string folder = OwnedTestDirectory.Create("mc-pdf-preview-");
        GalleryContactSheetItem[] items = GalleryContactSheetOrderTests.Items(folder, Palette[..5]);
        var dialog = Show(new GalleryContactSheetDialog(items, 0, () => true));
        try
        {
            // The dialog opens on the first image: the first page, first cell.
            WaitForPreview(dialog);
            Assert.Equal(0, dialog.OrderList.SelectedIndex);
            Assert.Equal(0, dialog.PreviewPage);
            Assert.Equal(0, dialog.HighlightedCell);
            Assert.Equal(Visibility.Visible, dialog.PreviewHighlight.Visibility);

            // One image a page: the third row shows the third image.
            dialog.LayoutChoice.SelectedIndex = (int)ContactSheetGrid.OneByOne;
            dialog.OrderList.SelectedIndex = 2;
            WaitForPreview(dialog);
            Assert.Equal(2, dialog.PreviewPage);
            Assert.Equal(0, dialog.HighlightedCell);
            Assert.Equal(Colors.Blue, CellColor(dialog, ContactSheetGrid.OneByOne, 0));
            Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "3페이지 미리보기");

            // Four a page, the choice kept: the first page, with the third cell ringed.
            dialog.LayoutChoice.SelectedIndex = (int)ContactSheetGrid.TwoByTwo;
            WaitForPreview(dialog);
            Assert.Equal(2, dialog.OrderList.SelectedIndex);
            Assert.Equal(0, dialog.PreviewPage);
            Assert.Equal(2, dialog.HighlightedCell);
            Assert.Equal(Colors.Blue, CellColor(dialog, ContactSheetGrid.TwoByTwo, 2));
            Rect ring = RingBounds(dialog);
            Rect cell = CellBounds(dialog, ContactSheetGrid.TwoByTwo, 2);
            Assert.True(ring.Contains(cell), $"ring {ring} misses cell {cell}");
            Assert.True(ring.Width < cell.Width + 10 && ring.Height < cell.Height + 10);

            // Another row on the same page moves the ring without drawing the page again.
            ImageSource? page = dialog.PreviewImage;
            dialog.OrderList.SelectedIndex = 3;
            Assert.Equal(3, dialog.HighlightedCell);
            Assert.True(dialog.PendingPreview.IsCompleted);
            Assert.Same(page, dialog.PreviewImage);

            // The fifth row is on the second page.
            dialog.OrderList.SelectedIndex = 4;
            WaitForPreview(dialog);
            Assert.Equal(1, dialog.PreviewPage);
            Assert.Equal(0, dialog.HighlightedCell);
            Assert.Equal(Palette[4], CellColor(dialog, ContactSheetGrid.TwoByTwo, 0));

            // A row left out shows the page before it, without a ring.
            dialog.OrderRows[4].IsIncluded = false;
            WaitForPreview(dialog);
            Assert.Equal(0, dialog.PreviewPage);
            Assert.Equal(-1, dialog.HighlightedCell);
            Assert.Equal(Visibility.Collapsed, dialog.PreviewHighlight.Visibility);
            dialog.OrderRows[4].IsIncluded = true;

            // Alt+Up on one image a page: the moved image keeps the choice and the preview follows it.
            dialog.LayoutChoice.SelectedIndex = (int)ContactSheetGrid.OneByOne;
            dialog.OrderList.SelectedIndex = 2;
            Assert.True(dialog.HandleOrderKey(Key.Up, ModifierKeys.Alt, dialog.OrderList));
            WaitForPreview(dialog);
            Assert.Equal(1, dialog.OrderList.SelectedIndex);
            Assert.Equal(1, dialog.PreviewPage);
            Assert.Equal(Colors.Blue, CellColor(dialog, ContactSheetGrid.OneByOne, 0));
        }
        finally { dialog.Close(); OwnedTestDirectory.Delete(folder); }
        Assert.Null(dialog.PreviewImage);
    });

    [Fact]
    public void PreviewDecodesLargeImagesAtTheCellSize() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string folder = OwnedTestDirectory.Create("mc-pdf-preview-");
        GalleryContactSheetItem[] items = GalleryContactSheetOrderTests.Items(folder, Colors.Red, Colors.Lime);
        string large = Path.Combine(folder, "large.png");
        const int width = 3840, height = 2160;
        var pixels = new byte[width * height * 3];
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3)));
        using (FileStream output = File.Create(large)) encoder.Save(output);
        items[1] = items[1] with { ImagePath = large };
        var decoded = new ConcurrentBag<(string Path, int Width, int Height)>();
        GalleryContactSheetRenderer.PreviewDecodedForTest = (item, image) => decoded.Add((item.ImagePath, image.PixelWidth, image.PixelHeight));
        var dialog = Show(new GalleryContactSheetDialog(items, 0, () => true));
        try
        {
            WaitForPreview(dialog);
            decoded.Clear();
            dialog.LayoutChoice.SelectedIndex = (int)ContactSheetGrid.OneByOne;
            dialog.OrderList.SelectedIndex = 1;
            WaitForPreview(dialog);
            Assert.Equal(1, dialog.PreviewPage);
            var layout = new ContactSheetLayout(new ContactSheetOptions(Grid: ContactSheetGrid.OneByOne));
            int cellEdge = (int)Math.Ceiling(Math.Max(layout.Cell(0).Width, layout.Cell(0).Height) * 96 / 72);
            (string _, int decodedWidth, int decodedHeight) = Assert.Single(decoded, entry => entry.Path == large);
            Assert.True(Math.Max(decodedWidth, decodedHeight) <= cellEdge, $"{decodedWidth} x {decodedHeight} for a {cellEdge} cell");
            Assert.True(decodedWidth < width);
        }
        finally
        {
            GalleryContactSheetRenderer.PreviewDecodedForTest = null;
            dialog.Close();
            OwnedTestDirectory.Delete(folder);
        }
    });

    [Fact]
    public void DragDecisionsFollowTheSystemDistanceAndTheListEdges()
    {
        Assert.False(GalleryContactSheetDialog.PassesDragThreshold(new Vector(0, 0)));
        Assert.False(GalleryContactSheetDialog.PassesDragThreshold(
            new Vector(SystemParameters.MinimumHorizontalDragDistance - 0.5, SystemParameters.MinimumVerticalDragDistance - 0.5)));
        Assert.True(GalleryContactSheetDialog.PassesDragThreshold(new Vector(SystemParameters.MinimumHorizontalDragDistance, 0)));
        Assert.True(GalleryContactSheetDialog.PassesDragThreshold(new Vector(0, -SystemParameters.MinimumVerticalDragDistance)));

        // Row 0 hovering at 2: rows 1 and 2 slide up a row; at 0 again nothing slides.
        Assert.Equal([0, -40, -40, 0], Enumerable.Range(0, 4).Select(position => GalleryContactSheetDialog.ShiftFor(position, 0, 2, 40)));
        Assert.Equal([0, 40, 40, 0, 0], Enumerable.Range(0, 5).Select(position => GalleryContactSheetDialog.ShiftFor(position, 3, 1, 40)));
        Assert.All(Enumerable.Range(0, 4), position => Assert.Equal(0, GalleryContactSheetDialog.ShiftFor(position, 2, 2, 40)));

        Assert.Equal(-1, GalleryContactSheetDialog.AutoScrollDirection(-30, 300));
        Assert.Equal(-1, GalleryContactSheetDialog.AutoScrollDirection(GalleryContactSheetDialog.AutoScrollBand - 1, 300));
        Assert.Equal(0, GalleryContactSheetDialog.AutoScrollDirection(150, 300));
        Assert.Equal(1, GalleryContactSheetDialog.AutoScrollDirection(300 - GalleryContactSheetDialog.AutoScrollBand + 1, 300));
        Assert.Equal(1, GalleryContactSheetDialog.AutoScrollDirection(340, 300));
    }

    [Fact]
    public void DraggedRowLiftsRowsSlideAsideAndTheDropReordersOnce() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string folder = OwnedTestDirectory.Create("mc-pdf-drag-");
        GalleryContactSheetItem[] items = GalleryContactSheetOrderTests.Items(folder, Palette);
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var dialog = Show(new GalleryContactSheetDialog(items, 0, () => true));
        try
        {
            int[] Order() => dialog.OrderedItems().Select(item => Array.IndexOf(items, item)).ToArray();
            WaitForPreview(dialog);
            GalleryContactSheetOrderRow[] rows = [.. dialog.OrderRows];
            double slot = Container(dialog, 0).ActualHeight;

            dialog.OrderList.SelectedIndex = 0;
            Assert.True(dialog.BeginRowDrag(rows[0], Center(dialog, 0)));
            Assert.True(dialog.IsDraggingRow);
            Assert.NotNull(dialog.LiftedRow);
            // Without Windows animation effects the lift is already complete.
            Assert.Equal(GalleryContactSheetDragAdorner.LiftScale, dialog.LiftedRow!.Scale);
            Assert.Equal(0, Container(dialog, 0).Opacity);

            // Over the third row: the two rows it passed open its place; nothing is reordered yet.
            dialog.UpdateRowDrag(Center(dialog, 2));
            Assert.Equal(2, dialog.DragTarget);
            Assert.Equal(-slot, dialog.RowShift(rows[1]), 3);
            Assert.Equal(-slot, dialog.RowShift(rows[2]), 3);
            Assert.Equal(0, dialog.RowShift(rows[3]));
            Assert.Equal(rows, dialog.OrderRows);
            Assert.Equal([0, 1, 2, 3, 4, 5], Order());
            // The numbers already read as they will after the drop.
            Assert.Equal(["3", "1", "2", "4", "5", "6"], rows.Select(row => row.Number));
            Assert.StartsWith("Image 3,", rows[0].AutomationName, StringComparison.Ordinal);

            // Back over its own place: the rows return.
            dialog.UpdateRowDrag(Center(dialog, 0));
            Assert.Equal(0, dialog.DragTarget);
            Assert.All(rows[1..], row => Assert.Equal(0, dialog.RowShift(row)));
            Assert.Equal(["1", "2", "3", "4", "5", "6"], rows.Select(row => row.Number));

            // Dropped over the fourth row: one reorder, the dragged image stays chosen and is previewed.
            dialog.UpdateRowDrag(Center(dialog, 3));
            dialog.EndRowDrag(commit: true);
            Assert.False(dialog.IsDraggingRow);
            Assert.Equal([1, 2, 3, 0, 4, 5], Order());
            Assert.Same(rows[0], dialog.OrderList.SelectedItem);
            Assert.All(rows, row => Assert.Equal(0, dialog.RowShift(row)));
            Assert.Equal(1, Container(dialog, 3).Opacity);
            WaitForPreview(dialog);
            Assert.Equal(0, dialog.PreviewPage);
            Assert.Equal(3, dialog.HighlightedCell);

            // Esc during a drag puts the row back and leaves the dialog open.
            Assert.True(dialog.BeginRowDrag(rows[1], Center(dialog, 0)));
            dialog.UpdateRowDrag(Center(dialog, 4));
            Assert.Equal(4, dialog.DragTarget);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            dialog.OrderList.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(dialog.IsDraggingRow);
            Assert.True(dialog.IsVisible);
            Assert.Equal([1, 2, 3, 0, 4, 5], Order());
            Assert.Equal(["1", "2", "3", "4", "5", "6"], dialog.OrderRows.Select(row => row.Number));
            Assert.All(rows, row => Assert.Equal(0, dialog.RowShift(row)));
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            dialog.Close();
            OwnedTestDirectory.Delete(folder);
        }
    });

    [Fact]
    public void DragMotionUsesTheMotionTokensOnlyWhenWindowsAnimatesAndScrollsAtTheEdges() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string folder = OwnedTestDirectory.Create("mc-pdf-drag-");
        GalleryContactSheetItem[] items = GalleryContactSheetOrderTests.Items(folder, Enumerable.Range(0, 40)
            .Select(index => Color.FromRgb((byte)(index * 6), 90, 160)).ToArray());
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var dialog = Show(new GalleryContactSheetDialog(items, 0, () => true));
        try
        {
            Drain(dialog);
            GalleryContactSheetOrderRow[] rows = [.. dialog.OrderRows];
            Assert.True(dialog.BeginRowDrag(rows[0], Center(dialog, 0)));
            // With animation effects on, the lift and the slides run as animations.
            Assert.True(dialog.LiftedRow!.LiftAnimates);
            dialog.UpdateRowDrag(Center(dialog, 2));
            var slid = (TranslateTransform)Container(dialog, 1).RenderTransform;
            Assert.True(slid.HasAnimatedProperties);

            // Held at the bottom edge, the list scrolls and the target follows past the rows first shown.
            double height = dialog.OrderList.ActualHeight;
            int shown = dialog.OrderList.ItemContainerGenerator.Items.Count(item =>
                dialog.OrderList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem { IsVisible: true } container
                && container.TranslatePoint(new Point(), dialog.OrderList).Y < height);
            dialog.UpdateRowDrag(new Point(40, height - 2));
            Assert.True(dialog.AutoScrolling);
            int steps = 0;
            while (steps < 60 && dialog.AutoScrollStep()) steps++;
            Assert.True(steps > 0);
            Assert.True(dialog.DragTarget >= shown, $"target {dialog.DragTarget}, first shown {shown}");
            Assert.Equal(rows.Length - 1, dialog.DragTarget);
            dialog.UpdateRowDrag(new Point(40, height / 2));
            Assert.False(dialog.AutoScrolling);
            int target = dialog.DragTarget;
            dialog.EndRowDrag(commit: true);
            Assert.False(dialog.AutoScrolling);
            Assert.Same(rows[0], dialog.OrderRows[target]);
            Assert.Same(rows[0], dialog.OrderList.SelectedItem);
            // The dropped row settles into its place over the Normal token.
            Drain(dialog);
            var settled = (TranslateTransform)Container(dialog, target).RenderTransform;
            Assert.True(settled.HasAnimatedProperties);
            Assert.Equal(0, (double)settled.GetAnimationBaseValue(TranslateTransform.YProperty));
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            dialog.Close();
            OwnedTestDirectory.Delete(folder);
        }
    });

    private static GalleryContactSheetDialog Show(GalleryContactSheetDialog dialog)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dialog.Dispatcher));
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000;
        // Inside the 1024 x 768 CI desktop.
        dialog.Width = 1000; dialog.Height = 680;
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
        dialog.Show();
        Drain(dialog);
        return dialog;
    }

    /// <summary>Waits until no preview is pending and the last one has drawn.</summary>
    private static void WaitForPreview(GalleryContactSheetDialog dialog)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        int settled = 0;
        while (DateTime.UtcNow < deadline && settled < 3)
        {
            Drain(dialog);
            settled = dialog.PendingPreview.IsCompleted && dialog.PreviewImage is not null ? settled + 1 : 0;
            Thread.Sleep(20);
        }
        Assert.True(dialog.PendingPreview.IsCompletedSuccessfully, $"The preview did not finish: {dialog.PendingPreview.Exception}");
        Assert.NotNull(dialog.PreviewImage);
    }

    private static Color CellColor(GalleryContactSheetDialog dialog, ContactSheetGrid grid, int cell)
    {
        var page = (BitmapSource)dialog.PreviewImage!;
        ContactSheetRect bounds = new ContactSheetLayout(new ContactSheetOptions(Grid: grid)).Cell(cell);
        // The preview is drawn at 96 pixels an inch from PDF points; the image sits a little above the caption.
        int x = (int)((bounds.X + bounds.Width / 2) * 96 / 72), y = (int)((bounds.Y + bounds.Height / 2) * 96 / 72);
        var bgra = new FormatConvertedBitmap(page, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        var sample = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        return Palette.Append(Colors.White).MinBy(color => Math.Abs(color.R - sample.R) + Math.Abs(color.G - sample.G) + Math.Abs(color.B - sample.B));
    }

    private static Rect RingBounds(GalleryContactSheetDialog dialog)
    {
        FrameworkElement ring = dialog.PreviewHighlight;
        var host = (Visual)VisualTreeHelper.GetParent(ring);
        return ring.TransformToAncestor(host).TransformBounds(new Rect(ring.RenderSize));
    }

    private static Rect CellBounds(GalleryContactSheetDialog dialog, ContactSheetGrid grid, int cell)
    {
        var layout = new ContactSheetLayout(new ContactSheetOptions(Grid: grid));
        var image = Descendants(dialog).OfType<Image>().Single(candidate => ReferenceEquals(candidate.Source, dialog.PreviewImage));
        var host = (Visual)VisualTreeHelper.GetParent(dialog.PreviewHighlight);
        double scale = image.ActualWidth / layout.PageWidth;
        Point origin = image.TranslatePoint(new Point(), (UIElement)host);
        ContactSheetRect bounds = layout.Cell(cell);
        return new Rect(origin.X + bounds.X * scale, origin.Y + bounds.Y * scale, bounds.Width * scale, bounds.Height * scale);
    }

    private static ListBoxItem Container(GalleryContactSheetDialog dialog, int position) =>
        (ListBoxItem)dialog.OrderList.ItemContainerGenerator.ContainerFromIndex(position);

    /// <summary>The centre of a row's laid-out place, leaving out any slide it has.</summary>
    private static Point Center(GalleryContactSheetDialog dialog, int position)
    {
        ListBoxItem container = Container(dialog, position);
        Point top = container.TranslatePoint(new Point(), dialog.OrderList);
        double shift = (container.RenderTransform as TranslateTransform)?.Y ?? 0;
        return new Point(top.X + container.ActualWidth / 2, top.Y - shift + container.ActualHeight / 2);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Drain(Window window) =>
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
}
