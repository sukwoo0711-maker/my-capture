using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// The PDF order column in every palette and both languages: row text stays readable on the
    /// window and on the selected row. With MYCAPTURE_THEME_PREVIEW_EVIDENCE set, each state is kept as a PNG.
    /// </summary>
    [Fact]
    public Task ContactSheetOrder_FollowsLivePaletteInBothLanguages() =>
        RunIsolated(nameof(ContactSheetOrder_FollowsLivePaletteInBothLanguages), () =>
        {
            var application = CreateLibraryMotionApplication();
            // As in the app, code run from this thread resumes awaits on its dispatcher.
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(application.Dispatcher));
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            var failures = new List<string>();
            try
            {
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var scope = UiText.UseLanguage(language);
                    string folder = OwnedTestDirectory.Create("mc-pdf-order-theme-");
                    GalleryContactSheetItem[] items = GalleryContactSheetOrderTests.Items(folder, Colors.IndianRed, Colors.SeaGreen,
                        Colors.SteelBlue, Colors.Goldenrod, Colors.SlateGray, Colors.Peru, Colors.MediumPurple);
                    var dialog = new GalleryContactSheetDialog(items, 0, () => true);
                    FluidMotion.SetWindowEntrance(dialog, false);
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Left = -10000; dialog.Top = -10000;
                    dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
                    try
                    {
                        dialog.Show();
                        dialog.OrderList.SelectedIndex = 1;
                        dialog.OrderRows[3].IsIncluded = false;
                        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                        // Loaded starts the preview after Show returns; wait for its picture, not only its task.
                        while (DateTime.UtcNow < deadline && (!dialog.PendingPreview.IsCompleted || dialog.PreviewImage is null
                            || dialog.OrderRows.Any(row => row.ThumbnailRequested && !row.PendingThumbnail.IsCompleted)))
                        {
                            Drain(dialog);
                            Thread.Sleep(20);
                        }
                        Assert.True(dialog.PendingPreview.IsCompletedSuccessfully,
                            $"{language}: the preview did not finish: {dialog.PendingPreview.Exception}");
                        Assert.True(dialog.PreviewImage is not null, $"{language}: no preview; status: "
                            + string.Join(" | ", Descendants(dialog).OfType<TextBlock>().Select(text => text.Text)));
                        foreach ((AppTheme theme, bool system) in BenchmarkPalettes())
                        {
                            ApplyBenchmarkPalette(theme, system);
                            Drain(dialog);
                            foreach (ListBoxItem row in Descendants(dialog.OrderList).OfType<ListBoxItem>())
                            {
                                Brush backdrop = row.IsSelected ? (Brush)application.FindResource("Surface.Selected") : dialog.Background;
                                foreach (TextBlock text in Descendants(row).OfType<TextBlock>().Where(text => text.IsVisible && text.Text.Length > 0))
                                    CheckBenchmarkContrast(text.Foreground, backdrop, $"{language}/{theme}{(system ? "/system" : "")}: {text.Text}", failures);
                            }
                            Assert.Same(application.FindResource("Button.Secondary"), dialog.MoveUpButton.Style);
                            SaveEvidence(dialog, $"contact-sheet-order-{language}-{theme}{(system ? "-system" : string.Empty)}.png");
                        }
                    }
                    finally { dialog.Close(); OwnedTestDirectory.Delete(folder); }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                ThemeService.SystemHighContrastOverrideForTest = null;
                ThemeService.SystemColorsOverrideForTest = null;
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        }, hungAfter: TimeSpan.FromSeconds(240));
}
