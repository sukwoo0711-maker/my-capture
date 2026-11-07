using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class CaptureFlowPolishTests
{
    private static readonly string[] SubmenuKinds = ["tray branch", "library and editor submenu"];

    [Theory]
    [InlineData(AppTheme.Daylight)]
    [InlineData(AppTheme.Midnight)]
    [InlineData(AppTheme.HighContrast)]
    public void SubmenusHaveTheirMenusChrome(AppTheme theme) => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        foreach (string kind in SubmenuKinds)
        {
            (ContextMenu menu, MenuItem branch) = SubmenuFixture(presenter, kind, theme);
            LayOut(menu);
            Border chrome = MenuChrome(menu);
            Border body = SubmenuBody(branch);
            string what = $"{theme}, {kind}";
            Assert.True(body.Margin == menu.Margin && menu.Margin == new Thickness(12),
                $"{what}: the submenu's popup band is {body.Margin}, the menu's is {menu.Margin}; expected 12 DIP for both.");
            Assert.True(body.BorderThickness == chrome.BorderThickness && chrome.BorderThickness == new Thickness(1),
                $"{what}: the submenu's edge is {body.BorderThickness}, the menu's is {chrome.BorderThickness}; expected 1 DIP for both.");
            Assert.True(SameColor(body.BorderBrush, chrome.BorderBrush) && SameColor(chrome.BorderBrush, (Brush)menu.FindResource("Border.Subtle")),
                $"{what}: the submenu's edge is {body.BorderBrush}, the menu's is {chrome.BorderBrush}; expected Border.Subtle for both.");
            Assert.True(SameColor(body.Background, chrome.Background), $"{what}: the submenu's surface is {body.Background}, the menu's is {chrome.Background}.");
            Assert.True(body.CornerRadius == chrome.CornerRadius, $"{what}: the submenu's corners are {body.CornerRadius}, the menu's are {chrome.CornerRadius}.");
            Assert.True(body.Padding == chrome.Padding, $"{what}: the submenu's padding is {body.Padding}, the menu's is {chrome.Padding}.");
            Assert.True(body.Effect is not null && ReferenceEquals(body.Effect, chrome.Effect)
                    && ReferenceEquals(chrome.Effect, menu.FindResource("Shadow.Card")),
                $"{what}: the submenu's shadow is {body.Effect}, the menu's is {chrome.Effect}; expected the theme's Shadow.Card for both.");
            // A list taller than the work area scrolls inside it, band and all.
            if (kind != SubmenuKinds[0])
                Assert.True(Math.Abs(body.MaxHeight - (SystemParameters.WorkArea.Height - body.Margin.Top - body.Margin.Bottom)) < 1e-6,
                    $"{what}: the submenu may grow to {body.MaxHeight} DIP; with its band that is more than the {SystemParameters.WorkArea.Height}-DIP work area.");
        }
    });

    [Fact]
    public Task SubmenuShadowEndsInsideItsPopupBand() => SettingsThemePreviewTests.RunIsolated(
        nameof(SubmenuShadowEndsInsideItsPopupBand), () => WithTheme(() =>
        {
            using var presenter = new ThemedShellPresenter(() => false);
            // Every case renders and is measured; the failures are reported together at the end.
            var failures = new List<string>();
            foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight, AppTheme.HighContrast })
                foreach (string kind in SubmenuKinds)
                    foreach (double scale in Scales)
                    {
                        (ContextMenu menu, MenuItem branch) = SubmenuFixture(presenter, kind, theme);
                        LayoutShadowMenu(menu, scale);
                        Border body = SubmenuBody(branch);
                        // The popup is closed: its child is a root of its own, as it is in the popup window.
                        VisualTreeHelper.SetRootDpi(body, new DpiScale(scale, scale));
                        LayOut(body);
                        string name = $"{(kind == SubmenuKinds[0] ? "tray" : "plain")}-{theme}-{Percent(scale)}";
                        string what = $"{theme}, {kind}, {Percent(scale)}%";
                        if (branch.ItemContainerGenerator.ContainerFromIndex(0) is not MenuItem first || !first.IsDescendantOf(body))
                        {
                            failures.Add($"{what}: the submenu generated no rows.");
                            continue;
                        }
                        RenderTargetBitmap image = Render(body, scale, desiredSize: true);
                        Save(image, $"submenu-shadow-{name}.png");
                        SaveMenuPair(menu, branch, body, scale, name);
                        var pixels = new Pixels(image);
                        int edge = pixels.EdgeAlpha();
                        // 4 DIP below the submenu, inside its band; a popup without a band ends at the submenu's edge.
                        int probeX = (int)((body.Margin.Left + (body.ActualWidth / 2)) * scale);
                        int probeY = (int)((body.Margin.Top + body.ActualHeight + 4) * scale);
                        int? below = probeY < pixels.Height ? pixels.Alpha(probeX, probeY) : null;
                        ShadowMeasurement($"submenu-shadow-{name}.txt",
                            $"{what}: edge alpha={edge}; 4 DIP below alpha={below?.ToString() ?? "outside the popup"}.");
                        if (body.Margin != new Thickness(12)) failures.Add($"{what}: the submenu's popup band is {body.Margin}, expected 12 DIP.");
                        if (edge > 1) failures.Add($"{what}: outermost popup ring alpha is {edge}, expected <= 1.");
                        if (below is not { } alpha)
                            failures.Add($"{what}: the popup ends less than 4 DIP below the submenu: no band for its shadow.");
                        // Contrast themes draw no shadow: the band stays clear.
                        else if (theme == AppTheme.HighContrast ? alpha != 0 : alpha == 0)
                            failures.Add($"{what}: 4 DIP below the submenu has alpha {alpha}; expected {(theme == AppTheme.HighContrast ? "none" : "its shadow")}.");
                    }
            Assert.True(failures.Count == 0, $"{failures.Count} submenu cases failed:\n" + string.Join("\n", failures.Take(12)));
        }), typeof(CaptureFlowPolishTests));

    [Fact]
    public Task SubmenusOpenWhereTheyDidWithTheirShadowBand() => SettingsThemePreviewTests.RunIsolated(
        nameof(SubmenusOpenWhereTheyDidWithTheirShadowBand), () => WithTheme(() =>
        {
            using var mode = new ExclusionMode(true);
            using var presenter = new ThemedShellPresenter(() => false);
            Rect work = SystemParameters.WorkArea;
            var anchor = new Border { Width = 40, Height = 24, Background = Brushes.Transparent };
            var host = new Canvas();
            Canvas.SetLeft(anchor, 20);
            Canvas.SetTop(anchor, 12);
            host.Children.Add(anchor);
            var owner = new Window
            {
                Content = host, Width = 80, Height = 48, WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                // Room to the right and below, so each menu opens below the anchor and its submenu to the right.
                Left = work.Left + (work.Width / 4), Top = work.Top + (work.Height / 8),
                ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                TestDpiContext.Initialize96DpiWindow(owner);
                owner.Show();
                NativeExclusion.Drain();
                foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight, AppTheme.HighContrast })
                    foreach (string kind in SubmenuKinds)
                    {
                        Vector styled = OpenAndMeasure(kind, theme, keepBand: true);
                        Vector zero = OpenAndMeasure(kind, theme, keepBand: false);
                        string what = $"{theme}, real {kind} popup, primary monitor";
                        ShadowMeasurement($"submenu-placement-{(kind == SubmenuKinds[0] ? "tray" : "plain")}-{theme}.txt",
                            $"{what}: submenu body from its row's top-right corner with the band={styled}; without it={zero} (physical pixels).");
                        Assert.True(Math.Abs(styled.X - zero.X) <= 1 && Math.Abs(styled.Y - zero.Y) <= 1,
                            $"{what}: the submenu opens {styled} from its row with its band and {zero} without it (physical pixels).");
                    }
            }
            finally { owner.Close(); }

            Vector OpenAndMeasure(string kind, AppTheme theme, bool keepBand)
            {
                (ContextMenu menu, MenuItem branch) = SubmenuFixture(presenter, kind, theme);
                menu.Placement = PlacementMode.Bottom;
                menu.PlacementTarget = anchor;
                string what = $"{theme}, {kind}, {(keepBand ? "12" : "0")}-DIP submenu band";
                try
                {
                    menu.IsOpen = true;
                    DrainUntil(() => branch.IsLoaded);
                    menu.UpdateLayout();
                    Assert.True(menu.IsOpen && PresentationSource.FromVisual(menu) is not null && branch.IsLoaded, $"{what}: the real menu did not open.");
                    Border body = SubmenuBody(branch);
                    if (!keepBand) body.Margin = new Thickness(0);
                    branch.IsSubmenuOpen = true;
                    DrainUntil(() => PresentationSource.FromVisual(body) is not null && body.IsLoaded);
                    body.UpdateLayout();
                    Assert.True(PresentationSource.FromVisual(body) is not null, $"{what}: the real submenu did not open.");
                    Assert.True(body.Margin == new Thickness(keepBand ? 12 : 0), $"{what}: actual margin is {body.Margin}.");
                    Point row = branch.PointToScreen(new Point(branch.ActualWidth, 0));
                    Point origin = body.PointToScreen(new Point(0, 0));
                    Assert.True(origin.X >= row.X - 1, $"{what}: the submenu opened at {origin}, left of its row's right edge {row}.");
                    return origin - row;
                }
                finally { branch.IsSubmenuOpen = false; menu.IsOpen = false; NativeExclusion.Drain(); }
            }
        }), typeof(CaptureFlowPolishTests));

    /// <summary>
    /// A menu with a submenu: the tray's (a ThemedShellPresenter branch, CreateSubmenuTemplate) or
    /// one in the shared MenuItem style that the library's and the editor's submenus use, such as
    /// the stamp's opacity.
    /// </summary>
    private static (ContextMenu Menu, MenuItem Branch) SubmenuFixture(ThemedShellPresenter presenter, string kind, AppTheme theme)
    {
        if (kind == SubmenuKinds[0])
        {
            ContextMenu tray = ShadowTray(presenter, theme);
            return (tray, tray.Items.OfType<MenuItem>().First(row => row.HasItems));
        }
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
        var menu = new ContextMenu { Resources = resources };
        var branch = new MenuItem { Header = "불투명도" };
        foreach (int percent in new[] { 100, 75, 50 })
            branch.Items.Add(new MenuItem { Header = $"{percent}%", IsCheckable = true, IsChecked = percent == 100 });
        menu.Items.Add(new MenuItem { Header = "도장 넣기", InputGestureText = "S" });
        menu.Items.Add(branch);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "도장 지우기" });
        return (menu, branch);
    }

    /// <summary>Lets the dispatcher go idle until <paramref name="ready"/> holds, for at most five seconds.</summary>
    private static void DrainUntil(Func<bool> ready)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        do NativeExclusion.Drain();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>The menu's own chrome: the Border its template draws inside the popup band.</summary>
    private static Border MenuChrome(ContextMenu menu)
    {
        menu.ApplyTemplate();
        return Assert.IsType<Border>(VisualTreeHelper.GetChild(menu, 0));
    }

    /// <summary>The submenu's chrome: the Border its popup holds, beside the branch row.</summary>
    private static Border SubmenuBody(MenuItem branch)
    {
        branch.ApplyTemplate();
        Popup popup = Assert.IsType<Popup>(branch.Template.FindName("PART_Popup", branch));
        return Assert.IsType<Border>(popup.Child);
    }

    /// <summary>
    /// Saves the menu with its submenu beside the branch row, where their popups open them, over a
    /// white page and a dark one. Each image starts at its popup window's corner, its margin band
    /// before its chrome, as the window holds it.
    /// </summary>
    private static void SaveMenuPair(ContextMenu menu, MenuItem branch, FrameworkElement body, double scale, string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EvidenceGate))) return;
        Popup popup = Assert.IsType<Popup>(branch.Template.FindName("PART_Popup", branch));
        RenderTargetBitmap parent = Render(menu, scale, desiredSize: true), child = Render(body, scale, desiredSize: true);
        Point row = branch.TranslatePoint(new Point(branch.ActualWidth, 0), menu);
        const double Page = 16;
        double childLeft = Page + menu.Margin.Left + row.X + popup.HorizontalOffset - body.Margin.Left;
        double childTop = Page + menu.Margin.Top + row.Y + popup.VerticalOffset - body.Margin.Top;
        double width = Math.Max(Page + parent.Width, childLeft + child.Width) + Page;
        double height = Math.Max(Page + parent.Height, childTop + child.Height) + Page;
        foreach ((string page, Color color) in new[] { ("on-white", Colors.White), ("on-dark", Color.FromRgb(31, 31, 31)) })
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
                dc.DrawImage(parent, new Rect(Page, Page, parent.Width, parent.Height));
                dc.DrawImage(child, new Rect(childLeft, childTop, child.Width, child.Height));
            }
            var sheet = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            sheet.Render(visual);
            Save(sheet, $"submenu-pair-{name}-{page}.png");
        }
    }

    private static bool SameColor(Brush? actual, Brush? expected) =>
        actual is SolidColorBrush first && expected is SolidColorBrush second && first.Color == second.Color;
}
