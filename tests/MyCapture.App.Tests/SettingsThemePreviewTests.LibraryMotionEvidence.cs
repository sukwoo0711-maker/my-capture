using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
/// Evidence only: renders the library's final hover and selection states, emulating hover by
/// applying the template's own IsMouseOver trigger setters with trigger precedence, so the same
/// code renders any template version. Set MYCAPTURE_LIBRARY_MOTION_EVIDENCE to an output
/// directory and optionally MYCAPTURE_LIBRARY_MOTION_BASELINE to a previous output to diff;
/// MYCAPTURE_LIBRARY_MOTION_EXPECT_CHANGED names the renders a change is meant to alter.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    private const string MotionEvidenceVariable = "MYCAPTURE_LIBRARY_MOTION_EVIDENCE";

    [Fact]
    public Task LibraryMotion_RealTemplatesCommitSelectionExpansionAndKeepSharedBrushes() =>
        RunIsolated(nameof(LibraryMotion_RealTemplatesCommitSelectionExpansionAndKeepSharedBrushes), () =>
        {
            var application = CreateLibraryMotionApplication();
            bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
            try
            {
                using var language = UiText.UseLanguage("ko-KR");
                ThemeService.Apply(AppTheme.Glass);
                foreach (bool motion in new[] { false, true })
                {
                    FluidMotion.AnimationsEnabledOverrideForTest = motion;
                    LibraryUxFixture fixture = CreateLibraryUxFixture("mc-library-motion-regression-");
                    GalleryWindow window = fixture.Window;
                    try
                    {
                        var brushes = new[] { "Surface.Selected", "Text.Primary", "Surface.Hover", "Text.Secondary" }
                            .Select(key => (Brush: (SolidColorBrush)application.FindResource(key), Key: key))
                            .Select(entry => (entry.Brush, entry.Key, Color: entry.Brush.Color)).ToArray();
                        window.Show();
                        var list = (ListBox)window.FindName("RowsList");
                        PumpLibraryUntil(window, () => Descendants(list).OfType<ListBoxItem>()
                            .Any(item => item.DataContext is GalleryItemViewModel), "motion: capture cards");
                        ListBoxItem tile = Descendants(list).OfType<ListBoxItem>()
                            .First(item => item.DataContext is GalleryItemViewModel record && record.Id == fixture.Records[0].Id);
                        fixture.ViewModel.Select(fixture.Records[0].Id);
                        Assert.True(((GalleryItemViewModel)tile.DataContext).IsSelected);
                        var selected = (Border)tile.Template.FindName("SelectedLayer", tile);
                        Assert.Equal(1, (double)selected.GetAnimationBaseValue(UIElement.OpacityProperty));
                        Assert.Equal(motion, selected.HasAnimatedProperties);
                        Assert.Equal(MotionSpeed.Normal, FluidMotion.GetTransition(selected));
                        // Hovering a selected card leaves the selected final fill intact.
                        SimulateTemplateTrigger(tile, "IsMouseOver");
                        Assert.Equal(0, FluidMotion.GetFadeTo((Border)tile.Template.FindName("HoverLayer", tile)));

                        var dates = (Expander)window.FindName("DateSection");
                        dates.IsExpanded = true;
                        Assert.True(dates.IsExpanded);
                        var header = (Control)dates.Template.FindName("HeaderSite", dates);
                        var chevron = (FrameworkElement)header.Template.FindName("Chevron", header);
                        var rotation = Assert.IsType<RotateTransform>(chevron.RenderTransform);
                        Assert.Equal(90, (double)rotation.GetAnimationBaseValue(RotateTransform.AngleProperty));
                        Assert.Equal(motion, rotation.HasAnimatedProperties);
                        dates.IsExpanded = false;
                        Assert.False(dates.IsExpanded);
                        Assert.Equal(0, (double)rotation.GetAnimationBaseValue(RotateTransform.AngleProperty));
                        var tree = (TreeView)window.FindName("FolderTree");
                        TreeViewItem folder = Descendants(tree).OfType<TreeViewItem>()
                            .First(item => item.DataContext is GalleryFolderNode node && node.FolderId == fixture.Empty.Id);
                        var folderNode = (GalleryFolderNode)folder.DataContext;
                        SimulateTemplateTrigger(folder, "IsMouseOver");
                        folderNode.IsDropTarget = true;
                        Assert.Equal(Visibility.Collapsed, ((Border)folder.Template.FindName("HoverLayer", folder)).Visibility);
                        Assert.Equal(Visibility.Collapsed, ((Border)folder.Template.FindName("SelectedLayer", folder)).Visibility);
                        var dropBrush = Assert.IsType<SolidColorBrush>(
                            ((Border)folder.Template.FindName("Chrome", folder)).Background);
                        Assert.Equal(((SolidColorBrush)application.FindResource("Surface.Selected")).Color, dropBrush.Color);
                        Assert.False(dropBrush.HasAnimatedProperties);
                        folderNode.IsDropTarget = false;
                        Assert.Equal(Visibility.Visible, ((Border)folder.Template.FindName("HoverLayer", folder)).Visibility);
                        fixture.ViewModel.ClearSelection();
                        Assert.False(((GalleryItemViewModel)tile.DataContext).IsSelected);
                        Assert.Equal(0, (double)selected.GetAnimationBaseValue(UIElement.OpacityProperty));
                        foreach (var entry in brushes)
                        {
                            Assert.Same(entry.Brush, application.FindResource(entry.Key));
                            Assert.Equal(entry.Color, entry.Brush.Color);
                            Assert.False(entry.Brush.HasAnimatedProperties);
                        }
                    }
                    finally { window.CloseForExit(); }
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = previous;
                application.Shutdown();
            }
        });

    private static Application CreateLibraryMotionApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        return application;
    }

    [Fact]
    public Task LibraryMotionEvidence_RendersFinalHoverAndSelectionStates() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MotionEvidenceVariable))
            ? Task.CompletedTask
            : RunIsolated(nameof(LibraryMotionEvidence_RendersFinalHoverAndSelectionStates), () =>
            {
                var application = CreateLibraryMotionApplication();
                bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
                FluidMotion.AnimationsEnabledOverrideForTest = false;
                try
                {
                    using var scope = UiText.UseLanguage("ko-KR");
                    foreach (AppTheme theme in Enum.GetValues<AppTheme>())
                        RenderLibraryMotionStates(theme, AppThemeNames.ToSetting(theme));
                    // Windows contrast themes over the glass palette: dark Night sky and light Desert.
                    ThemeService.SystemHighContrastOverrideForTest = true;
                    ThemeService.SystemColorsOverrideForTest = role => SystemHighContrastThemeTests.NightSky[role];
                    RenderLibraryMotionStates(AppTheme.Glass, "windows-contrast");
                    ThemeService.SystemColorsOverrideForTest = role => DesertContrast[role];
                    RenderLibraryMotionStates(AppTheme.Glass, "windows-contrast-desert");
                }
                finally
                {
                    ThemeService.SystemHighContrastOverrideForTest = null;
                    ThemeService.SystemColorsOverrideForTest = null;
                    FluidMotion.AnimationsEnabledOverrideForTest = previous;
                    ThemeService.Apply(AppTheme.Midnight);
                    application.Shutdown();
                }
            });

    // Windows 11 "Desert": a light contrast theme, so evidence covers both contrast polarities.
    private static readonly IReadOnlyDictionary<SystemColorRole, Color> DesertContrast = new Dictionary<SystemColorRole, Color>
    {
        [SystemColorRole.Window] = Color.FromRgb(0xFF, 0xFA, 0xEF),
        [SystemColorRole.WindowText] = Color.FromRgb(0x3D, 0x3D, 0x3D),
        [SystemColorRole.ButtonFace] = Color.FromRgb(0xFF, 0xFA, 0xEF),
        [SystemColorRole.ButtonText] = Color.FromRgb(0x20, 0x20, 0x20),
        [SystemColorRole.Highlight] = Color.FromRgb(0x90, 0x39, 0x09),
        [SystemColorRole.HighlightText] = Color.FromRgb(0xFF, 0xF5, 0xE3),
        [SystemColorRole.GrayText] = Color.FromRgb(0x67, 0x67, 0x67),
        [SystemColorRole.HotTrack] = Color.FromRgb(0x1C, 0x5E, 0x75),
    };

    private static void RenderLibraryMotionStates(AppTheme theme, string file)
    {
        ThemeService.Apply(theme);
        LibraryUxFixture f = CreateLibraryUxFixture("mc-library-motion-");
        GalleryWindow window = f.Window;
        GalleryViewModel vm = f.ViewModel;
        try
        {
            window.Show();
            var rowsList = (ListBox)window.FindName("RowsList");
            PumpLibraryUntil(window, () => FolderCardButtons(rowsList).Length == 2
                && Descendants(rowsList).OfType<ListBoxItem>().Count(item => item.DataContext is GalleryItemViewModel) >= 3
                && Descendants(rowsList).OfType<Image>().Where(image => image.IsVisible).All(image => image.Source is not null),
                file + ": cards");

            // Root: hovered card A, selected card B, selected and hovered card C, a hovered view,
            // a hovered folder card, a hovered folder row and the hovered search field.
            vm.Select(f.Records[1].Id);
            vm.Select(f.Records[2].Id, control: true);
            SettleGalleryLayout(window);
            ListBoxItem Tile(int index) => Descendants(rowsList).OfType<ListBoxItem>()
                .First(item => item.DataContext is GalleryItemViewModel tile && tile.Id == f.Records[index].Id);
            SimulateTemplateTrigger(Tile(0), "IsMouseOver");
            SimulateTemplateTrigger(Tile(2), "IsMouseOver");
            Button design = FolderCardButtons(rowsList).Single(card => ((GalleryFolderCardViewModel)card.DataContext).FolderId == f.Design.Id);
            SimulateTemplateTrigger(design, "IsMouseOver");
            RadioButton images = Descendants(window).OfType<RadioButton>().Single(view => (string)view.Tag == "Images");
            SimulateTemplateTrigger(images, "IsMouseOver");
            var tree = (TreeView)window.FindName("FolderTree");
            TreeViewItem emptyRow = Descendants(tree).OfType<TreeViewItem>()
                .First(item => item.DataContext is GalleryFolderNode node && node.FolderId == f.Empty.Id);
            SimulateTemplateTrigger(emptyRow, "IsMouseOver");
            SimulateSearchHover(window);
            SettleGalleryLayout(window);
            SaveMotionEvidence(window, $"library-root-{file}.png");

            // An opened folder: its selected folder row, a hovered compact card, a selected compact
            // card and the hovered save-here switch.
            vm.ClearSelection();
            vm.NavigateTo(CaptureFolderScope.ForFolder(f.Design.Id));
            var chip = (CheckBox)window.FindName("CurrentFolderCaptureCheck");
            PumpLibraryUntil(window, () => chip.IsVisible
                && Descendants(rowsList).OfType<ListBoxItem>().Count(item => item.DataContext is GalleryItemViewModel) == 2
                && Descendants(rowsList).OfType<Image>().Where(image => image.IsVisible).All(image => image.Source is not null),
                file + ": folder");
            vm.Select(f.Records[5].Id);
            SettleGalleryLayout(window);
            SimulateTemplateTrigger(Tile(4), "IsMouseOver");
            SimulateTemplateTrigger(chip, "IsMouseOver");
            emptyRow = Descendants(tree).OfType<TreeViewItem>()
                .First(item => item.DataContext is GalleryFolderNode node && node.FolderId == f.Empty.Id);
            SimulateTemplateTrigger(emptyRow, "IsMouseOver");
            SettleGalleryLayout(window);
            SaveMotionEvidence(window, $"library-folder-{file}.png");

            // A date: the expanded date section with its hovered header and the selected, hovered date row.
            vm.ClearSelection();
            var dates = (Expander)window.FindName("DateSection");
            dates.IsExpanded = true;
            SettleGalleryLayout(window);
            var dateList = (ListBox)window.FindName("DateList");
            dateList.SelectedIndex = 0;
            PumpLibraryUntil(window, () => vm.SelectedScope == vm.FolderNodes[0].Scope, file + ": date");
            SettleGalleryLayout(window);
            var header = (Control)dates.Template.FindName("HeaderSite", dates);
            SimulateTemplateTrigger(header, "IsMouseOver");
            // The fixture has one date: hover the selected date row.
            SimulateTemplateTrigger((Control)dateList.ItemContainerGenerator.ContainerFromIndex(0), "IsMouseOver");
            SettleGalleryLayout(window);
            SaveMotionEvidence(window, $"library-date-{file}.png");
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static void SimulateSearchHover(Window window)
    {
        if (window.FindName("SearchHoverLayer") is UIElement layer)
        {
            FluidMotion.SetFadeTo(layer, 1);
            FluidMotion.SetFadeTo((UIElement)window.FindName("SearchEdge"), 0);
        }
        else
            ((Border)window.FindName("SearchField")).SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
    }

    /// <summary>Applies a template trigger's setters as if its property were true, honouring later active triggers.</summary>
    private static void SimulateTemplateTrigger(Control control, string property)
    {
        control.ApplyTemplate();
        ControlTemplate template = control.Template;
        TriggerBase[] triggers = template.Triggers.ToArray();
        for (int index = 0; index < triggers.Length; index++)
        {
            if (triggers[index] is not Trigger trigger || trigger.Property.Name != property || !Equals(trigger.Value, true))
                continue;
            foreach (Setter setter in trigger.Setters.OfType<Setter>())
            {
                bool overridden = triggers.Skip(index + 1).Any(later => IsTriggerActive(later, control, template)
                    && SettersOf(later).Any(other => other.TargetName == setter.TargetName && other.Property == setter.Property));
                if (overridden) continue;
                var target = setter.TargetName is null ? control : (DependencyObject)template.FindName(setter.TargetName, control);
                if (setter.Value is DynamicResourceExtension resource)
                    ((FrameworkElement)target).SetResourceReference(setter.Property, resource.ResourceKey);
                else
                    target.SetValue(setter.Property, setter.Value);
            }
        }
    }

    private static IEnumerable<Setter> SettersOf(TriggerBase trigger) => trigger switch
    {
        Trigger t => t.Setters.OfType<Setter>(),
        DataTrigger d => d.Setters.OfType<Setter>(),
        _ => [],
    };

    private static bool IsTriggerActive(TriggerBase trigger, Control control, ControlTemplate template)
    {
        switch (trigger)
        {
            case Trigger t:
                var source = t.SourceName is null ? control : (DependencyObject)template.FindName(t.SourceName, control);
                return SameValue(source.GetValue(t.Property), t.Value);
            case DataTrigger d:
                var probe = new FrameworkElement { DataContext = control.DataContext };
                BindingOperations.SetBinding(probe, FrameworkElement.TagProperty, d.Binding);
                return SameValue(probe.Tag, d.Value);
            default:
                return false;
        }
    }

    private static bool SameValue(object? actual, object? expected) => string.Equals(
        Convert.ToString(actual, CultureInfo.InvariantCulture), Convert.ToString(expected, CultureInfo.InvariantCulture),
        StringComparison.OrdinalIgnoreCase);

    private static void SaveMotionEvidence(Window window, string name)
    {
        string directory = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(MotionEvidenceVariable)!);
        Drain(window);
        var clientRoot = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        double width = clientRoot.ActualWidth, height = clientRoot.ActualHeight;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var content = new DrawingVisual();
        using (DrawingContext context = content.RenderOpen())
        {
            // WPF's black clear under the (possibly translucent) window background.
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(clientRoot)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill,
            }, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = DiagnosticOutputPaths.Child(directory, name);
        using (FileStream stream = File.Create(path)) encoder.Save(stream);

        string? baseline = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_MOTION_BASELINE");
        if (string.IsNullOrWhiteSpace(baseline)) return;
        Assert.True(File.Exists(Path.Combine(baseline, name)), "Missing matched baseline: " + name);
        WriteMotionDiff(Path.Combine(baseline, name), bitmap, directory, name);
    }

    private static void WriteMotionDiff(string beforePath, BitmapSource after, string directory, string name)
    {
        BitmapSource before;
        using (FileStream stream = File.OpenRead(beforePath))
            before = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
                PixelFormats.Bgra32, null, 0);
        var converted = new FormatConvertedBitmap(after, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        string report = Path.Combine(directory, Path.ChangeExtension(name, ".diff.txt"));
        if (before.PixelWidth != width || before.PixelHeight != height)
        {
            File.WriteAllText(report, $"size differs: before {before.PixelWidth}x{before.PixelHeight}, after {width}x{height}");
            Assert.Fail("Library motion changed the matched render size: " + name);
            return;
        }

        int stride = width * 4;
        byte[] a = new byte[stride * height], b = new byte[stride * height];
        before.CopyPixels(a, stride, 0);
        converted.CopyPixels(b, stride, 0);
        byte[] diff = new byte[stride * height];
        int changed = 0, maxDelta = 0;
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = y * stride + x * 4;
                int delta = Math.Max(Math.Abs(a[o] - b[o]), Math.Max(Math.Abs(a[o + 1] - b[o + 1]), Math.Abs(a[o + 2] - b[o + 2])));
                // Dimmed after image; changed pixels in magenta.
                diff[o] = (byte)(b[o] / 4); diff[o + 1] = (byte)(b[o + 1] / 4); diff[o + 2] = (byte)(b[o + 2] / 4); diff[o + 3] = 255;
                if (delta == 0) continue;
                changed++;
                maxDelta = Math.Max(maxDelta, delta);
                diff[o] = 255; diff[o + 1] = 0; diff[o + 2] = 255;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        }

        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, diff, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (FileStream stream = File.Create(Path.Combine(directory, Path.ChangeExtension(name, ".diff.png"))))
            encoder.Save(stream);
        File.WriteAllText(report, changed == 0
            ? "identical"
            : $"{changed} pixels differ (max channel delta {maxDelta}); bounds x {left}-{right}, y {top}-{bottom}");
        // Renders named in MYCAPTURE_LIBRARY_MOTION_EXPECT_CHANGED (comma-separated name parts) are the
        // ones a change is meant to alter; every other render must stay pixel-identical.
        bool expectChanged = (Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_MOTION_EXPECT_CHANGED") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => name.Contains(part, StringComparison.Ordinal));
        Assert.True(expectChanged ? changed > 0 : changed == 0,
            $"Library render {(expectChanged ? "was expected to change" : "changed")}: {changed} final-state pixels in {name}. See {report}.");
    }
}
