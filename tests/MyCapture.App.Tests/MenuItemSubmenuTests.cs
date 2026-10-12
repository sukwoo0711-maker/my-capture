using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MenuItemSubmenuTests
{
    [Fact]
    public void RealMenuTemplateHostsLoadingEmptyAndRestorableItemsWithoutChangingLeafChrome() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.HighContrast })
        {
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
            var menu = new ContextMenu { Resources = resources, Style = (Style)resources[typeof(ContextMenu)] };
            var restore = new MenuItem { Header = UiText.Get("LibraryRecovery_Recent"), Style = (Style)resources[typeof(MenuItem)] };
            var leaf = new MenuItem { Header = "Other command", InputGestureText = "Ctrl+A", Style = (Style)resources[typeof(MenuItem)] };
            menu.Items.Add(restore);
            menu.Items.Add(leaf);
            var button = new Button { Content = "More", ContextMenu = menu };
            var window = new Window
            {
                Content = button, Resources = resources, Width = 300, Height = 120,
                Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false,
            };
            try
            {
                window.Show();
                menu.PlacementTarget = button;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
                PumpUntil(() => menu.IsLoaded);
                menu.ApplyTemplate();
                Layout(menu);
                restore.ApplyTemplate();
                leaf.ApplyTemplate();
                Popup popup = Assert.IsType<Popup>(restore.Template.FindName("PART_Popup", restore));
                Assert.Equal(PlacementMode.Right, popup.Placement);
                Assert.Same(restore, popup.PlacementTarget);
                Assert.False(popup.Focusable);
                Assert.True(popup.AllowsTransparency);
                Assert.False(popup.IsOpen);

                foreach ((string name, string header, bool enabled) in new[]
                {
                    ("loading", UiText.Get("LibraryRecovery_Loading"), false),
                    ("empty", UiText.Get("LibraryRecovery_Empty"), false),
                    ("record", "2026-10-12 10:00:00", true),
                })
                {
                    restore.Items.Clear();
                    var item = new MenuItem { Header = header, IsEnabled = enabled };
                    int clicks = 0;
                    item.Click += (_, _) => clicks++;
                    restore.Items.Add(item);
                    Layout(menu);
                    FrameworkElement popupBody = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                    Layout(popupBody);
                    var presenter = Assert.Single(Descendants(popupBody).OfType<ItemsPresenter>());
                    Assert.Equal(KeyboardNavigationMode.Cycle, System.Windows.Input.KeyboardNavigation.GetDirectionalNavigation(presenter));
                    Assert.Same(item, restore.ItemContainerGenerator.ContainerFromIndex(0));
                    Assert.True(item.ActualWidth > 0 && item.ActualHeight >= 34);
                    Assert.Equal(header, item.Header);
                    Assert.Equal(enabled, item.IsEnabled);
                    var arrow = Assert.IsType<SymbolIcon>(restore.Template.FindName("SubmenuArrow", restore));
                    Assert.Equal(Visibility.Visible, arrow.Visibility);
                    Assert.Equal(16, arrow.Size);
                    Assert.NotNull(arrow.Data);
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(restore.Template.FindName("GestureText", restore)).Visibility);
                    restore.Focus();
                    KeyEventArgs right = KeyPress(restore, Key.Right);
                    restore.RaiseEvent(right);
                    Assert.True(right.Handled);
                    PumpUntil(() => restore.IsSubmenuOpen && popup.IsOpen && item.IsLoaded);
                    Assert.True(item.IsVisible);
                    SaveEvidence(menu, popupBody, $"submenu-{theme}-{name}.png");
                    if (enabled)
                    {
                        item.Focus();
                        KeyEventArgs left = KeyPress(item, Key.Left);
                        item.RaiseEvent(left);
                        Assert.True(left.Handled);
                        PumpUntil(() => !popup.IsOpen);
                        restore.RaiseEvent(KeyPress(restore, Key.Right));
                        PumpUntil(() => popup.IsOpen);
                        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        Assert.Equal(1, clicks);
                    }
                    restore.IsSubmenuOpen = false;
                }

                Assert.Equal(Visibility.Collapsed, Assert.IsType<SymbolIcon>(leaf.Template.FindName("SubmenuArrow", leaf)).Visibility);
                TextBlock gesture = Assert.IsType<TextBlock>(leaf.Template.FindName("GestureText", leaf));
                Assert.Equal(Visibility.Visible, gesture.Visibility);
                Assert.Equal("Ctrl+A", gesture.Text);
                Assert.False(Assert.IsType<Popup>(leaf.Template.FindName("PART_Popup", leaf)).IsOpen);
                restore.Items.Clear();
                Assert.Equal(Visibility.Collapsed, Assert.IsType<SymbolIcon>(restore.Template.FindName("SubmenuArrow", restore)).Visibility);
            }
            finally { menu.IsOpen = false; window.Close(); }
        }
    });

    private static KeyEventArgs KeyPress(Visual visual, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(visual), Environment.TickCount, key)
        { RoutedEvent = Keyboard.KeyDownEvent };

    private static void PumpUntil(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void Layout(FrameworkElement visual)
    {
        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new Rect(visual.DesiredSize));
        visual.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SaveEvidence(FrameworkElement menu, FrameworkElement popupBody, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_MENU_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        BitmapSource Render(FrameworkElement element)
        {
            var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(element);
            return image;
        }
        BitmapSource parentImage = Render(menu), childImage = Render(popupBody);
        var drawing = new DrawingVisual();
        int width = parentImage.PixelWidth + childImage.PixelWidth + 24;
        int height = Math.Max(parentImage.PixelHeight, childImage.PixelHeight) + 16;
        using (DrawingContext dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawImage(parentImage, new Rect(8, 8, parentImage.PixelWidth, parentImage.PixelHeight));
            dc.DrawImage(childImage, new Rect(parentImage.PixelWidth + 16, 8, childImage.PixelWidth, childImage.PixelHeight));
        }
        var sheet = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        using var file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }
}
