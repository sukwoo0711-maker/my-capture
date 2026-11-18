using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.Core.Export;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Choosing the order of the images in a PDF made from the library.</summary>
public sealed class GalleryContactSheetOrderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void OrderColumnAppearsOnlyForTwoOrMoreImagesAndStartsInLibraryOrder() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string folder = OwnedTestDirectory.Create("mc-pdf-order-");
        GalleryContactSheetItem[] items = Items(folder, Colors.Red, Colors.Lime, Colors.Blue);

        var single = new GalleryContactSheetDialog(items[..1], 0, () => true);
        try
        {
            Assert.Null(single.OrderPanel);
            Assert.Equal(860, single.Width);
            Assert.Equal(items[..1], single.OrderedItems());
        }
        finally { single.Close(); }

        var dialog = new GalleryContactSheetDialog(items, 0, () => true);
        try
        {
            Assert.NotNull(dialog.OrderPanel);
            Assert.Equal(items, dialog.OrderedItems());
            Assert.Equal(0, dialog.SortChoice.SelectedIndex);
            Assert.Equal(["ContactSheet.SortLibrary", "ContactSheet.SortOldest", "ContactSheet.SortNewest",
                "ContactSheet.SortTitle", "ContactSheet.SortCustom"], dialog.SortKeys);
            Assert.Equal(["1", "2", "3"], dialog.OrderRows.Select(row => row.Number));
            Assert.Equal("1번째 이미지, 빨강, " + Start.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("ko-KR")),
                dialog.OrderRows[0].AutomationName);
            Assert.Equal(UiText.Get("ContactSheet.Order"), AutomationProperties.GetName(dialog.OrderList));
            Assert.Equal(UiText.Get("ContactSheet.MoveUp"), AutomationProperties.GetName(dialog.MoveUpButton));
            Assert.Equal("Alt+Down", AutomationProperties.GetAcceleratorKey(dialog.MoveDownButton));
            Assert.False(dialog.MoveUpButton.IsEnabled);
            Assert.True(dialog.MoveDownButton.IsEnabled);
        }
        finally { dialog.Close(); }

        // Untitled captures cannot be told apart by title, so that sort is not offered.
        var untitled = new GalleryContactSheetDialog(items.Select(item => item with { Title = "" }).ToArray(), 0, () => true);
        try { Assert.DoesNotContain("ContactSheet.SortTitle", untitled.SortKeys); }
        finally { untitled.Close(); OwnedTestDirectory.Delete(folder); }
    });

    [Fact]
    public void ButtonsKeysDragAndSortsReorderTheExport() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string folder = OwnedTestDirectory.Create("mc-pdf-order-");
        GalleryContactSheetItem[] items = Items(folder, Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow);
        var dialog = Show(new GalleryContactSheetDialog(items, 0, () => true));
        try
        {
            int[] Order() => dialog.OrderedItems().Select(item => Array.IndexOf(items, item)).ToArray();

            dialog.OrderList.SelectedIndex = 0;
            dialog.MoveDownButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([1, 0, 2, 3], Order());
            Assert.Equal(1, dialog.OrderList.SelectedIndex);
            Assert.Equal(SortIndex(dialog, "ContactSheet.SortCustom"), dialog.SortChoice.SelectedIndex);
            dialog.MoveUpButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([0, 1, 2, 3], Order());
            Assert.Equal(0, dialog.SortChoice.SelectedIndex);
            Assert.False(dialog.MoveUpButton.IsEnabled);

            // Alt+Down twice, then Alt+Up; the last row cannot go further down.
            Assert.True(dialog.HandleOrderKey(Key.Down, ModifierKeys.Alt, dialog.OrderList));
            Assert.True(dialog.HandleOrderKey(Key.Down, ModifierKeys.Alt, dialog.OrderList));
            Assert.Equal([1, 2, 0, 3], Order());
            Assert.True(dialog.HandleOrderKey(Key.Up, ModifierKeys.Alt, dialog.OrderList));
            Assert.Equal([1, 0, 2, 3], Order());
            Assert.False(dialog.HandleOrderKey(Key.Down, ModifierKeys.None, dialog.OrderList));
            dialog.OrderList.SelectedIndex = 3;
            Assert.False(dialog.MoveDownButton.IsEnabled);
            Assert.True(dialog.HandleOrderKey(Key.Down, ModifierKeys.Alt, dialog.OrderList));
            Assert.Equal([1, 0, 2, 3], Order());

            // Dragging the first row onto the last shown row puts it there.
            Drain(dialog);
            GalleryContactSheetOrderRow dragged = dialog.OrderRows[0];
            Assert.True(dialog.DragRowTo(dragged, Center(dialog, 3)));
            Assert.Equal([0, 2, 3, 1], Order());
            Drain(dialog);
            Assert.True(dialog.DragRowTo(dragged, Center(dialog, 0)));
            Assert.Equal([1, 0, 2, 3], Order());
            Assert.Equal(0, dialog.DropIndex(new Point(10, -50)));

            // Space on a row leaves it out of this export only; its place in the list stays.
            dialog.OrderList.SelectedIndex = 1;
            Drain(dialog);
            var container = (ListBoxItem)dialog.OrderList.ItemContainerGenerator.ContainerFromIndex(1);
            Assert.True(dialog.HandleOrderKey(Key.Space, ModifierKeys.None, container));
            Assert.Equal([1, 2, 3], Order());
            Assert.Equal(["1", "", "2", "3"], dialog.OrderRows.Select(row => row.Number));
            Assert.Equal("Image 2, Red, " + Start.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("en-US")) + ", excluded",
                dialog.OrderRows[1].AutomationName);
            dialog.OrderRows[1].IsIncluded = true;
            Assert.Equal([1, 0, 2, 3], Order());

            dialog.SortChoice.SelectedIndex = SortIndex(dialog, "ContactSheet.SortNewest");
            Assert.Equal([3, 2, 1, 0], Order());
            dialog.SortChoice.SelectedIndex = SortIndex(dialog, "ContactSheet.SortTitle");
            // Blue, Green, Red, Yellow.
            Assert.Equal([2, 1, 0, 3], Order());
            dialog.SortChoice.SelectedIndex = SortIndex(dialog, "ContactSheet.SortOldest");
            Assert.Equal([0, 1, 2, 3], Order());
            // Oldest first equals the library order here and keeps its own name.
            Assert.Equal(SortIndex(dialog, "ContactSheet.SortOldest"), dialog.SortChoice.SelectedIndex);
            dialog.MoveRow(0, 1);
            Assert.Equal(SortIndex(dialog, "ContactSheet.SortCustom"), dialog.SortChoice.SelectedIndex);
            dialog.SortChoice.SelectedIndex = 0;
            Assert.Equal([0, 1, 2, 3], Order());
        }
        finally { dialog.Close(); OwnedTestDirectory.Delete(folder); }
    });

    [Fact]
    public void TitleSortComparesNumbersByValue() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string folder = OwnedTestDirectory.Create("mc-pdf-order-");
        GalleryContactSheetItem[] items = Items(folder, Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow);
        string[] titles = ["Capture 10", "Capture 9", "", "capture 2"];
        items = items.Select((item, index) => item with { Title = titles[index] }).ToArray();
        var dialog = new GalleryContactSheetDialog(items, 0, () => true);
        try
        {
            dialog.SortChoice.SelectedIndex = SortIndex(dialog, "ContactSheet.SortTitle");
            // Untitled images follow the titled ones.
            Assert.Equal([3, 1, 0, 2], dialog.OrderedItems().Select(item => Array.IndexOf(items, item)));
        }
        finally { dialog.Close(); OwnedTestDirectory.Delete(folder); }
    });

    [Fact]
    public void SavedPdfPagesFollowTheChosenOrder() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string folder = OwnedTestDirectory.Create("mc-pdf-order-");
        Color[] colors = [Colors.Red, Colors.Lime, Colors.Blue];
        GalleryContactSheetItem[] items = Items(folder, colors);
        var options = new ContactSheetOptions(Grid: ContactSheetGrid.OneByOne, Caption: ContactSheetCaption.None);
        var dialog = new GalleryContactSheetDialog(items, 0, () => true);
        try
        {
            // Untouched, the dialog writes the images in the order it was given, as before.
            Assert.Equal(colors, PageColors(dialog.OrderedItems(), options));
            dialog.MoveRow(2, 0);
            dialog.OrderRows[2].IsIncluded = false;
            Assert.Equal([Colors.Blue, Colors.Red], PageColors(dialog.OrderedItems(), options));
        }
        finally { dialog.Close(); OwnedTestDirectory.Delete(folder); }
    });

    [Fact]
    public void LongListDecodesOnlyTheRowsItShows() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string folder = OwnedTestDirectory.Create("mc-pdf-order-");
        GalleryContactSheetItem[] items = Items(folder, Enumerable.Range(0, 80)
            .Select(index => Color.FromRgb((byte)(index * 3), 120, 200)).ToArray());
        var dialog = Show(new GalleryContactSheetDialog(items, 0, () => true));
        try
        {
            Drain(dialog);
            int requested = dialog.OrderRows.Count(row => row.ThumbnailRequested);
            Assert.InRange(requested, 1, 40);
            GalleryContactSheetOrderRow first = dialog.OrderRows[0];
            // The load resumes on this dispatcher, so keep it running while waiting.
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (!first.PendingThumbnail.IsCompleted && DateTime.UtcNow < deadline) { Drain(dialog); Thread.Sleep(20); }
            Assert.True(first.PendingThumbnail.IsCompletedSuccessfully);
            Drain(dialog);
            Assert.NotNull(first.Thumbnail);
            Assert.True(first.Thumbnail!.PixelWidth <= 96);
            dialog.OrderList.ScrollIntoView(dialog.OrderRows[^1]);
            Drain(dialog);
            Assert.True(dialog.OrderRows[^1].ThumbnailRequested);
            Assert.True(dialog.OrderRows.Count(row => row.ThumbnailRequested) < items.Length);
        }
        finally { dialog.Close(); OwnedTestDirectory.Delete(folder); }
    });

    internal static GalleryContactSheetItem[] Items(string folder, params Color[] colors)
    {
        string[] names = ["빨강", "초록", "파랑", "노랑"];
        string[] english = ["Red", "Green", "Blue", "Yellow"];
        bool korean = UiText.Culture.Name.StartsWith("ko", StringComparison.Ordinal);
        return colors.Select((color, index) =>
        {
            string path = Path.Combine(folder, $"image-{index}.png");
            var pixels = new byte[160 * 100 * 4];
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 100, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4)));
            using (FileStream output = File.Create(path)) encoder.Save(output);
            string title = index < names.Length ? (korean ? names[index] : english[index]) : $"Capture {index + 1}";
            return new GalleryContactSheetItem(path, title, Start.AddMinutes(index), "App", "Window");
        }).ToArray();
    }

    private static Color[] PageColors(IReadOnlyList<GalleryContactSheetItem> items, ContactSheetOptions options)
    {
        using var pdf = new MemoryStream();
        GalleryContactSheetDialog.WritePdf(pdf, items, options, CultureInfo.GetCultureInfo("ko-KR"), null, CancellationToken.None);
        byte[] bytes = pdf.ToArray();
        string text = Encoding.Latin1.GetString(bytes);
        Assert.Equal(items.Count, Regex.Matches(text, @"/Type /Page\b").Count);
        var colors = new List<Color>();
        foreach (Match image in Regex.Matches(text, @"/Filter /DCTDecode /Length (\d+) >>\nstream\n"))
        {
            int length = int.Parse(image.Groups[1].Value, CultureInfo.InvariantCulture);
            using var jpeg = new MemoryStream(bytes, image.Index + image.Length, length);
            BitmapFrame frame = BitmapDecoder.Create(jpeg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
            var pixel = new byte[3];
            bgr.CopyPixels(new Int32Rect(bgr.PixelWidth / 2, bgr.PixelHeight / 2, 1, 1), pixel, 3, 0);
            colors.Add(Nearest(Color.FromRgb(pixel[2], pixel[1], pixel[0])));
        }
        return [.. colors];
    }

    private static Color Nearest(Color sample) => new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow, Colors.White }
        .MinBy(color => Math.Abs(color.R - sample.R) + Math.Abs(color.G - sample.G) + Math.Abs(color.B - sample.B));

    private static GalleryContactSheetDialog Show(GalleryContactSheetDialog dialog)
    {
        // As in the app, code run from this thread resumes awaits (previews, thumbnails) on its dispatcher.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dialog.Dispatcher));
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000;
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
        dialog.Show();
        Drain(dialog);
        return dialog;
    }

    private static int SortIndex(GalleryContactSheetDialog dialog, string key) => dialog.SortKeys.ToList().IndexOf(key);

    private static Point Center(GalleryContactSheetDialog dialog, int position)
    {
        var container = (ListBoxItem)dialog.OrderList.ItemContainerGenerator.ContainerFromIndex(position);
        Rect bounds = container.TransformToAncestor(dialog.OrderList).TransformBounds(new Rect(container.RenderSize));
        return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    private static void Drain(Window window) =>
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
}
