using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ReleaseReadinessDialogTests
{
    [Fact]
    public void CompareKeepsTabForNavigationAndSwapsThroughSeparateCommand() => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        bitmap.Freeze();
        var window = new GalleryCompareWindow(bitmap, bitmap, "First", DateTimeOffset.UnixEpoch,
            "Second", DateTimeOffset.UnixEpoch);
        try
        {
            var root = Assert.IsType<DockPanel>(window.Content);
            Grid headers = root.Children.OfType<Grid>().Single();
            UIElement[] sides = headers.Children.Cast<UIElement>().ToArray();
            KeyBinding swap = Assert.Single(window.InputBindings.OfType<KeyBinding>());
            Assert.Equal(Key.S, swap.Key);
            Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, swap.Modifiers);
            Assert.Same(GalleryCompareWindow.SwapSidesCommand, swap.Command);

            // Tab reaches the normal WPF navigation path from both the surface and toolbar.
            DockPanel toolbar = root.Children.OfType<DockPanel>().Single();
            foreach (UIElement target in toolbar.Children.Cast<UIElement>().Append(root.Children[^1]))
            {
                var tab = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Tab)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                target.RaiseEvent(tab);
                Assert.False(tab.Handled);
                Assert.Equal(0, Grid.GetColumn(sides[0]));
                Assert.Equal(1, Grid.GetColumn(sides[1]));
            }

            GalleryCompareWindow.SwapSidesCommand.Execute(null, window);
            Assert.Equal(1, Grid.GetColumn(sides[0]));
            Assert.Equal(0, Grid.GetColumn(sides[1]));
            GalleryCompareWindow.SwapSidesCommand.Execute(null, window);
            Assert.Equal(0, Grid.GetColumn(sides[0]));
            Assert.Equal(1, Grid.GetColumn(sides[1]));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void CopyOptionsUsesPrimaryContinueAndSecondaryCancel(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var panel = new ImageExportOptionsPanel(new ImageExportOptions(), full: false);
        Window dialog = ImageExportOptionsPanel.CreateCopyOptionsDialog(null, panel);
        try
        {
            dialog.Resources.MergedDictionaries.Add(SystemHighContrastThemeTests.LoadTheme());
            WrapPanel actions = Assert.Single(panel.Children.OfType<WrapPanel>());
            Button[] buttons = actions.Children.OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Length);
            Assert.Equal(UiText.Get("ExportOptions.Continue"), buttons[0].Content);
            Assert.Equal(UiText.Get("Dialog.Cancel"), buttons[1].Content);
            Assert.True(buttons[0].IsDefault);
            Assert.False(buttons[0].IsCancel);
            Assert.False(buttons[1].IsDefault);
            Assert.True(buttons[1].IsCancel);
            Assert.Equal(HorizontalAlignment.Right, actions.HorizontalAlignment);
            Assert.Same(dialog.FindResource("Button.Primary"), buttons[0].Style);
            Assert.Same(dialog.FindResource("Button.Secondary"), buttons[1].Style);
            Assert.All(buttons, button =>
            {
                Assert.Equal(ThemedDialog.ActionMinWidth, button.MinWidth);
                Assert.Equal(ThemedDialog.ActionHeight, button.MinHeight);
                Assert.Equal(ThemedDialog.ActionSpacing, button.Margin.Left);
            });
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "삭제하지 못했습니다", "휴지통")]
    [InlineData("en-US", "Could not delete", "Recycle Bin")]
    public void UpdatedCopyDescribesSwapShortcutAndRetainedTemporaryFile(string language, string deletion, string recycleBin)
    {
        using var scope = UiText.UseLanguage(language);
        string controls = UiText.Get("Gallery.Compare.Controls");
        Assert.Contains("Ctrl+Shift+S:", controls);
        Assert.DoesNotContain("Tab:", controls);
        const string path = @"C:\export\contact.partial";
        string retained = UiText.Format("ContactSheet.StageRetained", path);
        Assert.Contains(deletion, retained);
        Assert.Contains(path, retained);
        Assert.DoesNotContain(recycleBin, retained);
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
