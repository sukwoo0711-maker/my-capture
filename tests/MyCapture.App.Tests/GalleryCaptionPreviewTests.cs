using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryCaptionPreviewTests
{
    private const string LongCaption = "2026년 3분기 제품 개발 계획과 고객 피드백 검토 — 화면 캡처 라이브러리 사용성 개선 및 최종 검증 결과 보고서 📷 — MyCapture 기능 개선 회의.xlsx";

    [Fact]
    public void FullCaptionPopupWrapsWithoutMovingCardsAndClosesCleanly() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var window = new Window
        {
            Width = 660, Height = 560, WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Left + 24, Top = SystemParameters.WorkArea.Top + 24,
            ShowInTaskbar = false, ShowActivated = false,
        };
        LoadProductTemplates(window);
        FluidMotion.SetWindowEntrance(window, false);
        window.SetResourceReference(Window.BackgroundProperty, "Surface.Base");
        var panel = new Grid { Margin = new Thickness(22) };
        var row = new ListBox
        {
            Style = (Style)window.FindResource("Gallery.TileRowList"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        row.Items.Add(Tile(LongCaption));
        row.Items.Add(Tile("간단한 메모"));
        panel.Children.Add(row);
        window.Content = panel;
        try
        {
            window.Show();
            Drain(window);
            var first = Assert.IsType<ListBoxItem>(row.ItemContainerGenerator.ContainerFromIndex(0));
            var second = Assert.IsType<ListBoxItem>(row.ItemContainerGenerator.ContainerFromIndex(1));
            Size beforeSize = first.RenderSize;
            Point beforeSecond = second.TranslatePoint(new Point(), window);
            TextBlock caption = Assert.Single(Descendants(first).OfType<TextBlock>(), text => text.Text == LongCaption);
            Assert.Equal(TextTrimming.CharacterEllipsis, caption.TextTrimming);
            SaveEvidence(window, null, "gallery-caption-idle.png");

            var tip = Assert.IsType<ToolTip>(first.ToolTip);
            Assert.True(GalleryCaptionPreview.GetIsEnabled(first));
            tip.PlacementTarget = first;
            tip.IsOpen = true;
            Drain(window);
            Assert.True(tip.IsOpen);
            TextBlock expanded = Assert.Single(Descendants(tip).OfType<TextBlock>(), text => text.Text == LongCaption);
            Assert.Equal(TextWrapping.Wrap, expanded.TextWrapping);
            Assert.Equal(TextTrimming.None, expanded.TextTrimming);
            Assert.True(expanded.ActualHeight > caption.ActualHeight * 2);
            Assert.InRange(tip.ActualWidth, 100, 440);
            Assert.Equal(beforeSize, first.RenderSize);
            Assert.Equal(beforeSecond, second.TranslatePoint(new Point(), window));
            SaveEvidence(window, tip, "gallery-caption-expanded.png");
            tip.IsOpen = false;
            Drain(window);
            Assert.False(tip.IsOpen);

            // Exercise the attached lifecycle through the actual WPF input pipeline.
            // The test never synthesizes operating-system keystrokes into other apps.
            window.Activate();
            var tab = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Tab)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = window,
            };
            InputManager.Current.ProcessInput(tab);
            Keyboard.Focus(first);
            Drain(window);
            Assert.True(first.IsKeyboardFocused);
            Assert.True(tip.IsOpen);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = first,
            };
            first.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(tip.IsOpen);
            Keyboard.Focus(second);
            Drain(window);
            Assert.True(tip.IsOpen);
            Assert.Same(second, tip.PlacementTarget);
            Keyboard.Focus(first);
            Drain(window);
            Assert.True(tip.IsOpen);
            Assert.Same(first, tip.PlacementTarget);
            first.Visibility = Visibility.Hidden;
            Assert.False(tip.IsOpen);
            first.Visibility = Visibility.Visible;
            Drain(window);

            // One shared style tooltip must bind to its current tile, not retain the
            // previously hovered record. Reopening follows metadata edits as well.
            tip.PlacementTarget = second;
            tip.IsOpen = true;
            Drain(window);
            Assert.Contains(Descendants(tip).OfType<TextBlock>(), text => text.Text == "간단한 메모");
            Assert.DoesNotContain(Descendants(tip).OfType<TextBlock>(), text => text.Text == LongCaption);
            tip.IsOpen = false;
            var tile = Assert.IsType<GalleryItemViewModel>(first.DataContext);
            tile.Record.Title = new string('文', 140) + " 📷 끝";
            tile.RaiseMetaChanged();
            tip.PlacementTarget = first;
            tip.IsOpen = true;
            Drain(window);
            Assert.Contains(Descendants(tip).OfType<TextBlock>(), text => text.Text == tile.Caption);
            Assert.InRange(tip.ActualWidth, 100, 440);
            Assert.Equal(beforeSize, first.RenderSize);
            tip.IsOpen = false;

            // Exercise WPF's native popup placement beside the right/bottom display
            // edges. No card or popup custom-position arithmetic is substituted.
            window.Left = SystemParameters.WorkArea.Right - window.Width;
            window.Top = SystemParameters.WorkArea.Bottom - window.Height;
            tip.PlacementTarget = second;
            tip.Placement = PlacementMode.Right;
            tip.IsOpen = true;
            Drain(window);
            Point popupTop = tip.PointToScreen(new Point());
            Point popupBottom = tip.PointToScreen(new Point(tip.ActualWidth, tip.ActualHeight));
            var work = SystemParameters.WorkArea;
            var transform = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
            Point workTop = transform.Transform(work.TopLeft);
            Point workBottom = transform.Transform(work.BottomRight);
            Assert.True(popupTop.X >= workTop.X - 2 && popupTop.Y >= workTop.Y - 2);
            Assert.True(popupBottom.X <= workBottom.X + 2 && popupBottom.Y <= workBottom.Y + 2,
                $"Popup escaped work area: {popupTop} to {popupBottom}; work {workTop} to {workBottom}");
            tip.IsOpen = false;
        }
        finally { window.Close(); }
    });

    private static GalleryItemViewModel Tile(string title) => new(
        new CaptureRecord { Title = title, Width = 1280, Height = 720, CreatedAt = new DateTimeOffset(2026, 9, 30, 14, 25, 0, TimeSpan.Zero), IsPinned = true },
        _ => "missing-synthetic-preview.jpg", 320);

    private static void LoadProductTemplates(Window window)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combined = new XElement(p + "ResourceDictionary");
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(GalleryCaptionPreviewTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            CopyNamespaces(dictionary, combined);
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        combined.SetAttributeValue(XNamespace.Xmlns + "gallery", "clr-namespace:MyCapture.App.Gallery;assembly=MyCapture");
        combined.Add(new XElement(XName.Get("BoolToVisibilityConverter", "clr-namespace:MyCapture.App.Gallery;assembly=MyCapture"), new XAttribute(x + "Key", "BoolToVisibility")));
        combined.Add(new XElement(XName.Get("PinLabelConverter", "clr-namespace:MyCapture.App.Gallery;assembly=MyCapture"), new XAttribute(x + "Key", "PinLabel")));
        using Stream gallery = typeof(GalleryCaptionPreviewTests).Assembly.GetManifestResourceStream("GalleryFixture.Window.xaml")!;
        XElement product = XDocument.Load(gallery).Root!;
        CopyNamespaces(product, combined);
        string[] keys = ["Gallery.IconButton", "Gallery.TileTemplate", "Gallery.TileRowList"];
        foreach (XElement entry in product.Element(p + "Window.Resources")!.Element(p + "ResourceDictionary")!.Elements()
            .Where(element => keys.Contains((string?)element.Attribute(x + "Key"))))
        {
            var copy = new XElement(entry);
            // Event handlers belong to GalleryWindow and are irrelevant to the unchanged
            // card visuals. Keep all production bindings, templates and sizing intact.
            foreach (XAttribute handler in copy.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.LocalName == "Click").ToArray()) handler.Remove();
            combined.Add(copy);
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined.ToString(SaveOptions.DisableFormatting)));
    }

    private static void CopyNamespaces(XElement source, XElement destination)
    {
        foreach (XAttribute attribute in source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            string value = attribute.Value;
            if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal)) value += ";assembly=MyCapture";
            destination.SetAttributeValue(attribute.Name, value);
        }
    }

    private static void Drain(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SaveEvidence(Window window, ToolTip? tip, string name)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_GALLERY_CAPTION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var pixels = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        pixels.Render(window);
        if (tip is not null)
        {
            Point screen = tip.PointToScreen(new Point());
            Point offset = window.PointFromScreen(screen);
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(tip), null, new Rect(offset, tip.RenderSize));
            pixels.Render(visual);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var output = File.Create(Path.Combine(root, name));
        encoder.Save(output);
    }
}
