using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Saves PNG evidence of the library's cards (full and compact; rest, hover, selected, keyboard focus and a
/// search result) in both languages, a dark and a light theme and two DPIs. Set
/// MYCAPTURE_LIBRARY_OVERHAUL_EVIDENCE to the output directory (and MYCAPTURE_LIBRARY_OVERHAUL_PHASE to a
/// folder name such as "before" or "after"); without it the test does nothing.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryOverhaulEvidence_RendersCardStatesInBothLanguagesThemesAndDpis() =>
        Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_OVERHAUL_EVIDENCE") is not { Length: > 0 } requested
            ? Task.CompletedTask
            : RunIsolated(nameof(LibraryOverhaulEvidence_RendersCardStatesInBothLanguagesThemesAndDpis), () =>
            {
                string phase = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_OVERHAUL_PHASE") ?? "after";
                string directory = DiagnosticOutputPaths.Create(Path.Combine(requested, phase));
                Application application = CreateLibraryMotionApplication();
                bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
                FluidMotion.AnimationsEnabledOverrideForTest = false;
                try
                {
                    foreach (string language in new[] { "ko-KR", "en-US" })
                    foreach ((AppTheme theme, string themeName) in new[] { (AppTheme.Midnight, "dark"), (AppTheme.Daylight, "light") })
                    foreach (double scale in new[] { 1d, 1.5d })
                    {
                        using var scope = UiText.UseLanguage(language);
                        ThemeService.Apply(theme);
                        LibraryUxFixture f = CreateLibraryUxFixture("mc-overhaul-evidence-");
                        // A long title and a video, so truncation, the duration badge and the kept mark are drawn.
                        f.Records[0].Title = "Capture review - toolbar and keyboard navigation";
                        f.Records[0].SourceAppName = "ChatGPT";
                        f.Records[1].Title = "ChatGPT";
                        f.Records[2].Title = "모델주인";
                        f.Records[3].Title = "김진경";
                        f.ViewModel.Refresh();
                        GalleryWindow window = f.Window;
                        string stem = $"{language[..2]}-{themeName}-{(int)(scale * 100)}";
                        try
                        {
                            window.Show();
                            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                            foreach (UIElement element in Descendants(window).Prepend(window).OfType<UIElement>())
                            {
                                element.InvalidateMeasure();
                                element.InvalidateArrange();
                            }
                            var rows = (ListBox)window.FindName("RowsList");
                            PumpLibraryUntil(window, () => Descendants(rows).OfType<ListBoxItem>()
                                .Count(item => item.DataContext is GalleryItemViewModel) >= 3, "cards");
                            Func<Guid, ListBoxItem> card = id => PumpLibraryUntil(window,
                                () => Descendants(rows).OfType<ListBoxItem>().FirstOrDefault(item => item.DataContext is GalleryItemViewModel tile && tile.Id == id),
                                item => item is not null, "card");

                            SaveOverhaulEvidence(window, directory, $"{stem}-full-rest.png");
                            SetLibraryTileState440(card(f.Records[1].Id), "IsMouseOver", true);
                            SettleGalleryLayout(window);
                            SaveOverhaulEvidence(window, directory, $"{stem}-full-hover.png");
                            SetLibraryTileState440(card(f.Records[1].Id), "IsMouseOver", false);
                            f.ViewModel.Select(f.Records[0].Id);
                            f.ViewModel.Select(f.Records[2].Id, control: true);
                            SettleGalleryLayout(window);
                            SaveOverhaulEvidence(window, directory, $"{stem}-full-selected.png");
                            f.ViewModel.ClearSelection();
                            ListBoxItem focused = card(f.Records[0].Id);
                            SetLibraryTileState440(focused, "IsKeyboardFocusWithin", true);
                            ForceKeyboardInput(focused);
                            SettleGalleryLayout(window);
                            SaveOverhaulEvidence(window, directory, $"{stem}-full-keyboard.png");
                            SetLibraryTileState440(focused, "IsKeyboardFocusWithin", false);

                            var search = (TextBox)window.FindName("SearchBox");
                            search.Text = "capture";
                            PumpLibraryUntil(window, () => f.ViewModel.HasSearchQuery, "search");
                            SettleGalleryLayout(window);
                            SaveOverhaulEvidence(window, directory, $"{stem}-full-search.png");
                            search.Text = string.Empty;
                            SettleGalleryLayout(window);

                            f.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
                            PumpLibraryUntil(window, () => f.ViewModel.IsCompactLayout, "compact");
                            SaveOverhaulEvidence(window, directory, $"{stem}-compact-rest.png");
                            SetLibraryTileState440(card(f.Records[4].Id), "IsMouseOver", true);
                            SettleGalleryLayout(window);
                            SaveOverhaulEvidence(window, directory, $"{stem}-compact-hover.png");
                            SetLibraryTileState440(card(f.Records[4].Id), "IsMouseOver", false);
                            f.ViewModel.Select(f.Records[5].Id);
                            SettleGalleryLayout(window);
                            SaveOverhaulEvidence(window, directory, $"{stem}-compact-selected.png");
                        }
                        finally { window.CloseForExit(); }
                    }
                }
                finally
                {
                    FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
                    ThemeService.Apply(AppTheme.Midnight);
                    application.Shutdown();
                }
            });

    /// <summary>
    /// Makes the keyboard the last input device, when the build has a notion of it (a card draws its
    /// checkbox and ring for keyboard focus only). Builds without it ignore the call.
    /// </summary>
    private static void ForceKeyboardInput(UIElement card)
    {
        MethodInfo? hook = typeof(GalleryWindow).Assembly.GetType("MyCapture.App.Gallery.GalleryCardInput")
            ?.GetMethod("SetKeyboardFocusVisibleForTest", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        hook?.Invoke(null, [card, true]);
    }

    private static void SaveOverhaulEvidence(Window window, string directory, string name)
    {
        SettleGalleryLayout(window);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
