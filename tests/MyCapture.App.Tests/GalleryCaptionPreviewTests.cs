using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
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
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryCaptionPreviewTests
{
    private const string LongCaption = "2026년 3분기 제품 개발 계획과 고객 피드백 검토 — 화면 캡처 라이브러리 사용성 개선 및 최종 검증 결과 보고서 📷 — MyCapture 기능 개선 회의.xlsx";

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 2.0)]
    [InlineData(true, 2.0)]
    public void SourceIconsStayOutsideImagesAndRevealOnlyOnDemand(bool compact, double textScale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var window = new Window { Width = compact ? 720 : 1100, Height = 420, ShowInTaskbar = false, ShowActivated = false };
        LoadProductTemplates(window);
        FluidMotion.SetWindowEntrance(window, false);
        window.SetResourceReference(Window.BackgroundProperty, "Surface.Base");
        window.Resources["FontSize.Body"] = 14 * textScale;
        window.Resources["FontSize.Caption"] = 12 * textScale;
        var row = new ListBox
        {
            Style = (Style)window.FindResource(compact ? "Gallery.CompactTileRowList" : "Gallery.TileRowList"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24),
            Width = compact ? 656 : 1040,
        };
        CaptureRecord[] records =
        [
            new() { Title = LongCaption, SourcePageUrl = "https://example.com/design?token=private" },
            new() { Title = "문서", SourceAppName = "Excel", SourceDocumentPath = @"C:\Work\사양서.xlsx" },
            new() { Title = "앱", SourceAppName = "Claude", SourceWindowTitle = "사용성 검토" },
            new() { Title = "메모" },
        ];
        var thumbnail = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgr24, null, new byte[320 * 180 * 3], 320 * 3);
        thumbnail.Freeze();
        var loader = new GalleryThumbnailLoader((_, _) => thumbnail);
        foreach (CaptureRecord record in records)
        {
            record.IsPinned = true;
            record.HasAnnotations = true;
            record.CreatedAt = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(9));
            var tile = new GalleryItemViewModel(record, _ => "synthetic-preview", 320);
            tile.ConfigureAsyncLoading(loader, Dispatcher.CurrentDispatcher);
            tile.SetFolderBadge(("제품 기획 자료", "작업/제품 기획 자료"));
            row.Items.Add(tile);
        }
        window.Content = row;
        try
        {
            window.Show();
            Drain(window);
            long started = Stopwatch.GetTimestamp();
            while (row.Items.Cast<GalleryItemViewModel>().Any(tile => tile.Thumbnail is null)
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            {
                Thread.Sleep(25);
                Drain(window);
            }
            Assert.All(row.Items.Cast<GalleryItemViewModel>(), tile => Assert.NotNull(tile.Thumbnail));
            for (int index = 0; index < records.Length; index++)
            {
                var item = Assert.IsType<ListBoxItem>(row.ItemContainerGenerator.ContainerFromIndex(index));
                Button source = Assert.Single(Descendants(item).OfType<Button>(), button => button.Name == "SourceBadge");
                Border preview = Assert.Single(Descendants(item).OfType<Border>(), border => border.Name == "TilePreview");
                if (index == 3)
                {
                    Assert.False(source.IsVisible);
                    continue;
                }
                Assert.Equal(32, source.ActualWidth);
                Assert.Equal(32, source.ActualHeight);
                Rect iconBounds = new(source.TranslatePoint(new Point(), window), source.RenderSize);
                Rect imageBounds = new(preview.TranslatePoint(new Point(), window), preview.RenderSize);
                Assert.False(iconBounds.IntersectsWith(imageBounds));
                Rect itemBounds = new(item.TranslatePoint(new Point(), window), item.RenderSize);
                Assert.True(itemBounds.Contains(iconBounds), $"Source icon escapes card: {iconBounds} / {itemBounds}");
                Grid badges = Assert.Single(Descendants(item).OfType<Grid>(), grid => grid.Name == "TileStatusBadges");
                foreach (Border badge in badges.Children.OfType<Border>().Where(badge => badge.IsVisible))
                {
                    Rect bounds = new(badge.TranslatePoint(new Point(), window), badge.RenderSize);
                    Assert.True(itemBounds.Contains(bounds), $"Status badge escapes card: {badge.Name}: {bounds} / {itemBounds}");
                    Assert.False(bounds.IntersectsWith(iconBounds), $"Status badge overlaps source: {badge.Name}");
                }
                TextBlock metadata = Assert.Single(Descendants(item).OfType<TextBlock>(), text => text.Name == "TileMetadataText");
                Assert.True(metadata.ActualWidth >= 8, $"Metadata lost all space: {metadata.ActualWidth}");
                var tile = Assert.IsType<GalleryItemViewModel>(item.DataContext);
                tile.SetSearchMatch(true, CaptureMatchField.Title);
                Drain(window);
                foreach (Border badge in badges.Children.OfType<Border>().Where(badge => badge.IsVisible))
                {
                    Rect bounds = new(badge.TranslatePoint(new Point(), window), badge.RenderSize);
                    Assert.True(itemBounds.Contains(bounds));
                    Assert.False(bounds.IntersectsWith(iconBounds), $"Search badge overlaps source: {badge.Name}");
                }
                Assert.True(metadata.ActualWidth >= 8);
                tile.SetSearchMatch(false, CaptureMatchField.None);
                Drain(window);
                Size before = item.RenderSize;
                var tip = Assert.IsType<ToolTip>(source.ToolTip);
                Assert.False(tip.IsOpen);
                item.IsSelected = true;
                Drain(window);
                Assert.False(tip.IsOpen);
                GallerySourceBadge.ShowForFocus(source, false);
                Assert.False(tip.IsOpen);
                RaiseFocusChange(null, source);
                GallerySourceBadge.ShowForFocus(source, true);
                Drain(window);
                Assert.True(tip.IsOpen);
                Assert.Equal(before, item.RenderSize);
                Assert.Equal(32, source.ActualWidth);
                Assert.DoesNotContain("private", Assert.IsType<TextBlock>(tip.Content).Text);
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = row };
                row.RaiseEvent(escape);
                Assert.True(escape.Handled);
                Assert.False(tip.IsOpen);
                GallerySourceBadge.ShowForFocus(source, true);
                Assert.False(tip.IsOpen);
                RaiseFocusChange(source, item);
                RaiseFocusChange(item, row);
                Assert.Null(item.ToolTip);
                Assert.False(TitleTip(item).IsOpen);
            }
            SaveEvidence(window, null, $"gallery-source-{(compact ? "compact" : "full")}-{textScale}.png");
            var first = Assert.IsType<ListBoxItem>(row.ItemContainerGenerator.ContainerFromIndex(0));
            Button firstSource = Assert.Single(Descendants(first).OfType<Button>(), button => button.Name == "SourceBadge");
            RaiseFocusChange(row, firstSource);
            GallerySourceBadge.ShowForFocus(firstSource, true);
            Drain(window);
            var activeTip = Assert.IsType<ToolTip>(firstSource.ToolTip);
            SaveEvidence(window, activeTip, $"gallery-source-focus-{(compact ? "compact" : "full")}-{textScale}.png");
            GallerySourceBadge.SetProvenance(firstSource, CaptureProvenance.None);
            Assert.False(activeTip.IsOpen);
            Assert.Empty(Assert.IsType<TextBlock>(Assert.IsType<ToolTip>(firstSource.ToolTip).Content).Text);
        }
        finally { window.Close(); }
    });

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
            Assert.True(GalleryCaptionPreview.IsTruncated(caption));
            GalleryCaptionPreview.ShowForFocus(first, false);
            Assert.False(TitleTip(first).IsOpen);
            Assert.Equal(Assert.IsType<GalleryItemViewModel>(first.DataContext).ExportToolTip,
                AutomationProperties.GetHelpText(first));
            SaveEvidence(window, null, "gallery-caption-idle.png");

            Assert.Null(first.ToolTip);
            var tip = TitleTip(first);
            Assert.True(GalleryCaptionPreview.GetIsEnabled(first));
            tip.PlacementTarget = first;
            tip.IsOpen = true;
            Drain(window);
            Assert.True(tip.IsOpen);
            TextBlock expanded = Assert.Single(Descendants(tip).OfType<TextBlock>(), text => text.Text == LongCaption);
            Assert.Single(Descendants(tip).OfType<TextBlock>());
            Assert.Equal(TextWrapping.Wrap, expanded.TextWrapping);
            Assert.Equal(TextTrimming.None, expanded.TextTrimming);
            // Measure the full text with the popup's actual font. Tooltip text is
            // smaller than the card caption, and CI may use a different Korean font
            // fallback; a ratio against the caption does not prove wrapping.
            var singleLine = new TextBlock
            {
                Text = LongCaption, TextWrapping = TextWrapping.NoWrap,
                FontFamily = expanded.FontFamily, FontSize = expanded.FontSize,
                FontWeight = expanded.FontWeight, FontStyle = expanded.FontStyle,
                FontStretch = expanded.FontStretch, Language = expanded.Language,
                FlowDirection = expanded.FlowDirection,
            };
            singleLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(expanded.ActualHeight > singleLine.DesiredSize.Height + .5,
                $"Full caption did not wrap: actual={expanded.RenderSize}; singleLine={singleLine.DesiredSize}; font={expanded.FontFamily}/{expanded.FontSize}");
            Assert.True(expanded.ActualWidth < singleLine.DesiredSize.Width,
                $"Expanded caption must wrap within the popup, not widen it to {singleLine.DesiredSize.Width}.");
            var textViewport = Assert.Single(Descendants(tip).OfType<ScrollViewer>());
            Assert.True(textViewport.ExtentHeight <= textViewport.ViewportHeight + .5,
                $"This caption should be fully visible: extent={textViewport.ExtentHeight}; viewport={textViewport.ViewportHeight}");
            Assert.InRange(tip.ActualWidth, 100, 440);
            Assert.Equal(beforeSize, first.RenderSize);
            Assert.Equal(beforeSecond, second.TranslatePoint(new Point(), window));
            SaveEvidence(window, tip, "gallery-caption-expanded.png");
            tip.IsOpen = false;
            Drain(window);
            Assert.False(tip.IsOpen);

            // Drive the framework's routed focus notifications, the production
            // behavior's input boundary. Hosted test desktops cannot reliably grant
            // foreground focus; no assertion depends on that OS permission.
            RaiseFocusChange(null, first);
            Drain(window);
            Assert.True(tip.IsOpen);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = second,
            };
            second.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(tip.IsOpen);
            GalleryCaptionPreview.ShowForFocus(first, true);
            Assert.False(tip.IsOpen);
            RaiseFocusChange(first, second);
            Drain(window);
            Assert.False(tip.IsOpen);
            first.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.False(tip.IsOpen);
            RaiseFocusChange(second, first);
            Drain(window);
            Assert.True(tip.IsOpen);
            Assert.Same(caption, tip.PlacementTarget);
            caption.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.False(tip.IsOpen);
            Drain(window);
            tip.IsOpen = true;
            Drain(window);
            caption.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.True(tip.IsOpen);
            // Source badges can precede the action row and be collapsed for this fixture.
            // Exercise the keyboard-reachable footer menu and its own hint.
            Button actionButton = Assert.Single(Descendants(first).OfType<Button>(),
                button => AutomationProperties.GetName(button) == UiText.Get("Text_5E8959D18390"));
            Assert.True(actionButton.IsVisible && actionButton.Focusable && actionButton.IsTabStop);
            RaiseFocusChange(first, actionButton);
            Assert.False(tip.IsOpen); // Action buttons retain their own hints.
            Assert.NotNull(actionButton.ToolTip);
            RaiseFocusChange(actionButton, first);
            Drain(window);
            Assert.True(tip.IsOpen);
            first.Visibility = Visibility.Hidden;
            Assert.False(tip.IsOpen);
            first.Visibility = Visibility.Visible;
            Drain(window);

            var secondTip = TitleTip(second);
            secondTip.PlacementTarget = second;
            secondTip.IsOpen = true;
            Drain(window);
            Assert.Contains(Descendants(secondTip).OfType<TextBlock>(), text => text.Text == "간단한 메모");
            Assert.DoesNotContain(Descendants(secondTip).OfType<TextBlock>(), text => text.Text == LongCaption);
            secondTip.IsOpen = false;
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

    private static void RaiseFocusChange(UIElement? previous, UIElement next)
    {
        previous?.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, previous, next)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent,
        });
        next.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, previous, next)
        {
            RoutedEvent = Keyboard.GotKeyboardFocusEvent,
        });
        if (next is ListBoxItem item) GalleryCaptionPreview.ShowForFocus(item, true);
    }

    private static ToolTip TitleTip(ListBoxItem item) =>
        Assert.IsType<ToolTip>(Assert.Single(Descendants(item).OfType<TextBlock>(), text => text.Name == "TileTitle").ToolTip);

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
        // Import the card's local motion wash as well as its overflow menu and folder badge.
        // Every entry comes from product markup; the fixture preserves the real state layers.
        string[] keys = ["Gallery.ThumbnailHeight", "LibraryUx.HoverWash", "Gallery.IconButton", "Gallery.SelectionCheckBox", "Gallery.TileContextMenu", "LibraryUx.Icon.Folder", "LibraryUx.Icon.Kept", "Gallery.FolderBadge",
            "Gallery.FolderBadgeContent", "Gallery.SourceBadge", "Gallery.TileTemplate", "Gallery.TileRowList",
            "Gallery.CompactTileRowList"];
        foreach (XElement entry in product.Element(p + "Window.Resources")!.Element(p + "ResourceDictionary")!.Elements()
            .Where(element => keys.Contains((string?)element.Attribute(x + "Key"))))
        {
            var copy = new XElement(entry);
            // Event handlers belong to GalleryWindow and are irrelevant to the unchanged
            // card visuals. Keep all production bindings, templates and sizing intact.
            foreach (XAttribute handler in copy.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.LocalName is "Click" or "PreviewKeyDown" or "LostKeyboardFocus").ToArray()) handler.Remove();
            // x:Shared is honoured only in compiled dictionaries; XamlReader rejects it.
            copy.Attribute(x + "Shared")?.Remove();
            // Product types used as elements (the card row panel) keep the window's
            // assembly-less namespace; outside the compiled window they need the assembly.
            foreach (XElement element in copy.DescendantsAndSelf())
            {
                if (element.Name.NamespaceName.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                    && !element.Name.NamespaceName.Contains(";assembly=", StringComparison.Ordinal))
                    element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=MyCapture");
                foreach (XAttribute attached in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration
                    && attribute.Name.NamespaceName.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                    && !attribute.Name.NamespaceName.Contains(";assembly=", StringComparison.Ordinal)).ToArray())
                {
                    attached.Remove();
                    element.SetAttributeValue(XName.Get(attached.Name.LocalName,
                        attached.Name.NamespaceName + ";assembly=MyCapture"), attached.Value);
                }
            }
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
