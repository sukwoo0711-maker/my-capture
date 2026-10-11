using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task UpdateTab_OpensTheMicrosoftStoreListing_InEveryBuild() =>
        RunIsolated(nameof(UpdateTab_OpensTheMicrosoftStoreListing_InEveryBuild), VerifyStoreUpdateTab);

    private static void VerifyStoreUpdateTab()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (bool packaged in new[] { true, false })
            {
                // The isolated child process owns this override, so no other test sees it.
                AppPackage.IsPackagedOverride = packaged;
                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var language = UiText.UseLanguage(locale);
                    VerifyStoreUpdateTabFor(packaged, locale);
                }
            }
        }
        finally
        {
            application.Shutdown();
            AppPackage.IsPackagedOverride = null;
        }
    }

    private static void VerifyStoreUpdateTabFor(bool packaged, string locale)
    {
        string build = packaged ? "store" : "unpackaged";
        SettingsWindow window = CreateOffscreenSettingsWindow(new AppSettings());
        try
        {
            window.Show();
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            tabs.SelectedItem = window.FindName("UpdateTab");
            Drain(window);

            // The download, progress and cancel controls of the GitHub updater are gone.
            Assert.Null(window.FindName("UpdateCancel"));
            Assert.Null(window.FindName("UpdateDownloadProgress"));
            Assert.Null(window.FindName("UpdateStatus"));

            var description = Assert.IsType<TextBlock>(window.FindName("UpdateDescription"));
            var action = Assert.IsType<Button>(window.FindName("UpdateAction"));
            var rating = Assert.IsType<Button>(window.FindName("StoreRatingButton"));
            string expectedDescription = packaged
                ? UiText.Get("StoreUpdate_StoreDescription")
                : UiText.Get("StoreUpdate_UnpackagedDescription") + Environment.NewLine + Environment.NewLine
                    + UiText.Get("StoreUpdate_UnpackagedNote");
            string expectedLabel = UiText.Get(packaged ? "StoreUpdate_CheckInStore" : "StoreUpdate_OpenStore");
            string expectedHelp = UiText.Get(packaged ? "StoreUpdate_CheckInStoreHelp" : "StoreUpdate_OpenStoreHelp");
            Assert.Equal(expectedDescription, description.Text);
            Assert.Equal(expectedLabel, action.Content);
            Assert.Equal(expectedLabel, AutomationProperties.GetName(action));
            Assert.Equal(expectedHelp, AutomationProperties.GetHelpText(action));
            Assert.True(action.IsVisible && action.IsEnabled && action.Focusable);
            Assert.Equal(packaged, rating.IsVisible);
            // The Store page action and the review action share one row that wraps when narrow.
            Assert.Same(VisualTreeHelper.GetParent(action), VisualTreeHelper.GetParent(rating));
            Assert.IsType<WrapPanel>(VisualTreeHelper.GetParent(action));
            AssertUpdateTabFits(window, description, action, rating);
            SaveStoreUpdateEvidence(window, $"update-tab-{build}-{locale}-glass-940x700.png");

            // A click hands over to the Store app; nothing is reported in the window.
            var opened = new List<string>();
            window.StoreLink = new StoreUpdateLink(opened.Add);
            action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain(window);
            Assert.Equal(new[] { StoreUpdateLink.StoreAppUri }, opened);
            var status = Assert.IsType<TextBlock>(window.FindName("SettingsStatus"));
            Assert.True(string.IsNullOrEmpty(status.Text));
            Assert.False(status.IsVisible);

            // With neither the Store app nor a browser available, the status line gives the address.
            opened.Clear();
            window.StoreLink = new StoreUpdateLink(uri =>
            {
                opened.Add(uri);
                throw new Win32Exception(2);
            });
            action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain(window);
            Assert.Equal(new[] { StoreUpdateLink.StoreAppUri, StoreUpdateLink.WebUri }, opened);
            Assert.Equal(UiText.Format("StoreUpdate_OpenFailed", StoreUpdateLink.WebUri), status.Text);
            Assert.Contains(StoreUpdateLink.WebUri, status.Text, StringComparison.Ordinal);
            Assert.True(status.IsVisible);
            AssertStatusFits(window, status);
            SaveStoreUpdateEvidence(window, $"update-tab-{build}-{locale}-glass-open-failed.png");

            // The smallest window and the high-contrast palette keep every part readable.
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            ThemeService.ApplyFromSettings(AppThemeNames.HighContrast);
            Drain(window);
            Assert.Equal(AppTheme.HighContrast, ThemeService.Current);
            AssertUpdateTabFits(window, description, action, rating);
            AssertStatusFits(window, status);
            SaveStoreUpdateEvidence(window, $"update-tab-{build}-{locale}-high-contrast-780x560.png");
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static void AssertUpdateTabFits(SettingsWindow window, TextBlock description, Button action, Button rating)
    {
        // Within the window, and within the tab page's scroll viewport once scrolled to.
        var scroll = Assert.IsType<ScrollViewer>(window.FindName("UpdateScroll"));
        Rect text = description.TransformToAncestor(scroll).TransformBounds(new Rect(description.RenderSize));
        Assert.True(text.Left >= -0.5 && text.Right <= scroll.ViewportWidth + 0.5, $"Description {text} wider than {scroll.ViewportWidth}");
        foreach (Button button in new[] { action, rating }.Where(button => button.IsVisible))
        {
            button.BringIntoView();
            Drain(window);
            AssertFits(window, button);
            Rect bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= scroll.ViewportWidth + 0.5
                && bounds.Bottom <= scroll.ViewportHeight + 0.5,
                $"{button.Content}: {bounds} outside viewport {scroll.ViewportWidth}x{scroll.ViewportHeight}");
            foreach (ContentPresenter presenter in Descendants(button).OfType<ContentPresenter>())
            {
                Rect content = presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize));
                Assert.True(content.Left >= 0 && content.Right <= button.ActualWidth + 0.5,
                    $"{button.Content}: label {content} outside button {button.RenderSize}");
            }
        }
    }

    private static void SaveStoreUpdateEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_STORE_UPDATE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass leaves the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
