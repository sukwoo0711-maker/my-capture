using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorInspectorOneRowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ko-KR", 1000)]
    [InlineData("en-US", 1053)]
    public void EverySingleTypeConfigurationHasOneRowAtTheAcceptedWindowWidth(string language, int width) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var host = new InspectorContractHost(width);
        var failures = new List<string>();
        int homogeneousSelections = 0;
        VisitConfigurations(host, (name, mixed) =>
        {
            if (mixed) return;
            if (Controller(host.Editor).SelectedItems.Count > 1) homogeneousSelections++;
            PropertyGroupsPanel panel = host.PropertyGroups;
            FrameworkElement[] visible = VisibleGroups(panel);
            int rows = visible.Select(group => Math.Round(LayoutInformation.GetLayoutSlot(group).Top, 3)).Distinct().Count();
            ScrollViewer scroll = PropertyScroll(host);
            double budget = Field<Border>(host.Editor, "_inspectorPanel").Height;
            if (rows > 1 || budget > 48.001 || scroll.ScrollableHeight > 0.001 || scroll.ScrollableWidth > 0.001)
                failures.Add($"{name}: rows={rows}, budget={budget:0.###}, contentWidth={visible.Sum(group => group.DesiredSize.Width):0.###}, available={panel.ActualWidth:0.###}, scroll={scroll.ScrollableWidth:0.###}/{scroll.ScrollableHeight:0.###}");
            Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
        });
        Assert.Equal(8, homogeneousSelections);
        foreach (string failure in failures) output.WriteLine($"{language}: {failure}");
        Assert.True(failures.Count == 0, $"{language}: {failures.Count} single-type configurations exceed the one-row budget at {width} DIP. {string.Join("; ", failures)}");
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void EveryReachableConfigurationKeepsTheSameViewportAndImageFit(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var host = new InspectorContractHost();
        var viewport = Field<FrameworkElement>(host.Editor, "_viewport");
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        double width = viewport.ActualWidth, height = viewport.ActualHeight, scale = surface.DipPerPixel;
        Rect bounds = surface.TransformToAncestor(host.Window).TransformBounds(surface.FrameRectDip);
        double budget = Field<Border>(host.Editor, "_inspectorPanel").Height;
        int count = 0;
        VisitConfigurations(host, (name, _) =>
        {
            count++;
            AssertClose(width, viewport.ActualWidth, name + " viewport width");
            AssertClose(height, viewport.ActualHeight, name + " viewport height");
            AssertClose(scale, surface.DipPerPixel, name + " image scale");
            AssertClose(budget, Field<Border>(host.Editor, "_inspectorPanel").Height, name + " budget");
            Rect current = surface.TransformToAncestor(host.Window).TransformBounds(surface.FrameRectDip);
            AssertClose(bounds.X, current.X, name + " image left");
            AssertClose(bounds.Y, current.Y, name + " image top");
            AssertClose(bounds.Width, current.Width, name + " image width");
            AssertClose(bounds.Height, current.Height, name + " image height");
        });
        Assert.True(count >= 40);
        output.WriteLine($"{language}: {count} configurations at 1000x640; stable budget={budget:0.###}, DipPerPixel={scale:R}, imageWidth={bounds.Width:R}, surfaceHeight={surface.ActualHeight:R}.");
    });

    [Theory]
    [InlineData("ko-KR", 1000)]
    [InlineData("en-US", 1053)]
    [InlineData("ko-KR", 680)]
    [InlineData("en-US", 680)]
    public void MixedSelectionsKeepEveryControlReachableByScrollingAndTabWithoutMovingTheImage(
        string language, int width) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var host = new InspectorContractHost(width);
        ViewportSnapshot baseline = Snapshot(host);
        int mixedCount = 0, overflowCount = 0, reachedControls = 0;
        VisitConfigurations(host, (name, mixed) =>
        {
            if (!mixed) return;
            mixedCount++;
            AssertSameGeometry(baseline, host, name);
            ScrollViewer scroll = PropertyScroll(host);
            var presenter = Descendants(scroll).OfType<ScrollContentPresenter>()
                .Single(candidate => ReferenceEquals(candidate.TemplatedParent, scroll));
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.Equal(0, scroll.ScrollableWidth);
            bool overflows = scroll.ScrollableHeight > 0.001;
            Assert.Equal(overflows ? Visibility.Visible : Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            Control[] controls = Descendants(host.PropertyGroups).OfType<Control>()
                .Where(control => control.IsVisible && control.IsEnabled
                    && control is ButtonBase or TextBox or ComboBox or Slider).ToArray();
            Assert.NotEmpty(controls);
            foreach (Control control in controls)
            {
                control.BringIntoView();
                host.Settle();
                AssertInside(control, presenter, name + " bring into view");
                AssertSameGeometry(baseline, host, name + " bring into view");
                reachedControls++;
            }

            Control[] tabStops = controls.Where(control => control.Focusable && control.IsTabStop).ToArray();
            Assert.NotEmpty(tabStops);
            scroll.ScrollToTop();
            host.Settle();
            Assert.True(tabStops[0].Focus());
            var visited = new HashSet<Control>();
            for (int index = 0; index < tabStops.Length; index++)
            {
                host.Settle();
                Control focused = Assert.IsAssignableFrom<Control>(Keyboard.FocusedElement);
                Assert.Contains(focused, tabStops);
                Assert.True(visited.Add(focused), $"{name}: Tab revisited a control before reaching all tab stops.");
                // WPF's Next traversal is the same focus order used by an unmodified Tab key.
                // Do not call BringIntoView here: focus navigation must reveal the control itself.
                AssertInside(focused, presenter, name + " Tab navigation");
                AssertSameGeometry(baseline, host, name + " Tab navigation");
                if (index + 1 < tabStops.Length)
                    Assert.True(focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            }
            Assert.Equal(tabStops.Length, visited.Count);

            if (overflows)
            {
                overflowCount++;
                host.Editor.Focus();
                scroll.ScrollToTop();
                host.Settle();
                MouseWheelEventArgs down = WheelOverBar(host.PropertyGroups, -120);
                host.Settle();
                Assert.True(down.Handled);
                Assert.True(scroll.VerticalOffset > 0, $"{name}: mouse wheel did not scroll the overflowing bar.");
                double afterDown = scroll.VerticalOffset;
                AssertSameGeometry(baseline, host, name + " wheel down");
                MouseWheelEventArgs up = WheelOverBar(host.PropertyGroups, 120);
                host.Settle();
                Assert.True(up.Handled);
                Assert.True(scroll.VerticalOffset < afterDown);
                AssertSameGeometry(baseline, host, name + " wheel up");
            }

            // Returning to one type must release the scrollbar's width, including at
            // the exact one-row threshold where that width would otherwise cause wrapping.
            AnnotationEditorController controller = Controller(host.Editor);
            controller.SetSelected(controller.Document.Items[0]);
            host.Settle();
            Assert.True(scroll.ScrollableHeight == 0,
                $"{language} {width} DIP, {name} -> {controller.Selected?.GetType().Name}: scrollbar remains with scrollable height={scroll.ScrollableHeight:R}, offset={scroll.VerticalOffset:R}, extent={scroll.ExtentWidth:R}x{scroll.ExtentHeight:R}, viewport={scroll.ViewportWidth:R}x{scroll.ViewportHeight:R}, visible content width={VisibleGroups(host.PropertyGroups).Sum(group => group.DesiredSize.Width):R}.");
            Assert.Equal(0, scroll.VerticalOffset);
            Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            AssertSameGeometry(baseline, host, name + " return to one type");
        });
        Assert.True(mixedCount >= 10);
        Assert.True(overflowCount > 0);
        output.WriteLine($"{language} {width} DIP: {mixedCount} mixed configurations, {overflowCount} overflowing, {reachedControls} controls reached; canvas unchanged.");
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ReportOneRowWidthAndNarrowWindowBudgets(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var host = new InspectorContractHost(width: 2000);
        double commonWidth = 0, mixedWidth = 0;
        string commonName = string.Empty, mixedName = string.Empty;
        var naturalGroups = new Dictionary<string, Size>();
        VisitConfigurations(host, (name, mixed) =>
        {
            FrameworkElement[] groups = VisibleGroups(host.PropertyGroups);
            foreach (FrameworkElement group in groups)
                naturalGroups[group.Name + (name == "crop:both-validation-errors" ? ":with-errors" : string.Empty)] = group.DesiredSize;
            double width = groups.Sum(group => group.DesiredSize.Width);
            if (mixed && width > mixedWidth) { mixedWidth = width; mixedName = name; }
            if (!mixed && width > commonWidth) { commonWidth = width; commonName = name; }
        });
        foreach ((string name, Size size) in naturalGroups)
            output.WriteLine($"{language}: natural group at 2000 DIP: {name}={size.Width:0.###}x{size.Height:0.###} DIP.");
        double chromeWidth = host.Window.ActualWidth - host.PropertyGroups.ActualWidth;
        output.WriteLine($"{language}: common configurations require {commonWidth:0.###} content DIP ({commonName}), minimum window approximately {Math.Ceiling(commonWidth + chromeWidth)} DIP.");
        output.WriteLine($"{language}: mixed selections require {mixedWidth:0.###} content DIP ({mixedName}), minimum window approximately {Math.Ceiling(mixedWidth + chromeWidth)} DIP; chrome={chromeWidth:0.###} DIP.");

        int noWrappingWidth = (int)Math.Ceiling(commonWidth + chromeWidth);
        output.WriteLine($"{language}: minimum window width for no wrapping across single-type configurations = {noWrappingWidth} DIP; mixed selections are excluded from the stable budget; maximum natural group height={naturalGroups.Values.Max(size => size.Height):0.###} DIP.");
        int low = 680, high = Math.Max(2000, noWrappingWidth + 10);
        host.Window.Width = high;
        host.Settle();
        if (Field<Border>(host.Editor, "_inspectorPanel").Height > 48.001)
        {
            output.WriteLine($"{language}: no width reaches the requested 48-DIP budget with current group heights; unwrapped budget={Field<Border>(host.Editor, "_inspectorPanel").Height:0.###} DIP at {high} DIP.");
        }
        else
        {
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                host.Window.Width = middle;
                host.Settle();
                if (Field<Border>(host.Editor, "_inspectorPanel").Height <= 48.001) high = middle;
                else low = middle + 1;
            }
            host.Window.Width = low;
            host.Settle();
            Assert.InRange(Field<Border>(host.Editor, "_inspectorPanel").Height, 0.001, 48.001);
            output.WriteLine($"{language}: measured minimum window width with a 48-DIP single-type stable budget = {low} DIP.");
        }
        foreach (int width in new[] { 1000, 760, 680 })
        {
            host.Window.Width = width;
            host.Settle();
            var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
            output.WriteLine($"{language}: {width}x640; budget={Field<Border>(host.Editor, "_inspectorPanel").Height:0.###}, desiredBudget={Field<double>(host.Editor, "_desiredInspectorHeight"):0.###}, availablePropertyWidth={host.PropertyGroups.ActualWidth:0.###}, DipPerPixel={surface.DipPerPixel:R}, imageWidth={surface.FrameRectDip.Width:R}, surfaceHeight={surface.ActualHeight:R}.");
        }
    });

    private static void VisitConfigurations(InspectorContractHost host, Action<string, bool> visit)
    {
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            Invoke(editor, "SelectTool", tool);
            controller.SetSelected(null);
            host.Settle();
            visit("tool:" + tool, false);
        }

        // These are actual SelectAll states. Order determines the primary item; mixed
        // shape/text and shape/blur selections expose the union of their applicable groups.
        string[][] selections =
        [
            ["text"], ["pen", "text"], ["blur", "shape", "text"],
            ["number"], ["pen", "number"], ["shape", "number"],
            ["pen"], ["shape"], ["ellipse"], ["arrow"],
            ["blur"], ["text", "blur"], ["pen", "blur"], ["text", "shape", "blur"],
            ["image"], ["text", "image"], ["pen", "image"], ["text", "shape", "image"],
            ["text", "text", "text"], ["number", "number", "number"],
            ["pen", "pen", "pen"], ["shape", "shape", "shape"],
            ["ellipse", "ellipse", "ellipse"], ["arrow", "arrow", "arrow"],
            ["blur", "blur", "blur"], ["image", "image", "image"],
            ["text", "shape"], ["shape", "ellipse"], ["shape", "pen"], ["text", "number"],
        ];
        foreach (string[] selection in selections)
        {
            Invoke(editor, "SelectTool", EditorTool.Select);
            controller.SetSelected(null);
            foreach (AnnotationItem item in controller.Document.Items.ToArray()) controller.Document.Remove(item);
            foreach (string kind in selection) controller.Document.Add(CreateItem(host, kind));
            controller.SelectAll();
            Assert.Equal(selection.Length, controller.SelectedItems.Count);
            host.Settle();
            visit("select-all:" + string.Join("+", selection), selection.Distinct().Count() > 1);
        }

        Invoke(editor, "SelectTool", EditorTool.StepNumber);
        controller.PointerDown(new PointD(60, 60));
        controller.PointerUp(new PointD(60, 60));
        Assert.True(Assert.IsType<TextAnnotation>(controller.Selected).IsStepNumber);
        host.Settle();
        visit("step-number:just-created", false);

        Invoke(editor, "SelectTool", EditorTool.Crop);
        Field<TextBox>(editor, "_cropWidth").Text = "invalid";
        Field<TextBox>(editor, "_cropHeight").Text = "1";
        editor.ApplyPendingCrop();
        Assert.Equal(EditorTool.Crop, controller.Tool);
        host.Settle();
        visit("crop:both-validation-errors", false);
        editor.CancelPendingCrop();
        host.Settle();
    }

    private static AnnotationItem CreateItem(InspectorContractHost host, string kind) => kind switch
    {
        "shape" => new RectangleAnnotation { Rect = new RectD(40, 40, 120, 80), StrokeThickness = 3 },
        "ellipse" => new EllipseAnnotation { Rect = new RectD(40, 40, 120, 80), StrokeThickness = 3 },
        "arrow" => PolylineAnnotation.CreateArrow(new PointD(40, 40), new PointD(160, 120), ColorRgba.Black, 3),
        "pen" => new PenAnnotation { Points = [new PointD(40, 40), new PointD(100, 80)] },
        "text" => new TextAnnotation { Text = "Synthetic caption", Rect = new RectD(180, 40, 160, 60), FontSize = 24, Background = ColorRgba.Transparent },
        "number" => new TextAnnotation { IsStepNumber = true, Text = "1", Rect = new RectD(180, 40, 40, 40), FontSize = 24 },
        "blur" => new BlurAnnotation { Rect = new RectD(180, 140, 40, 30) },
        "image" => new ImageAnnotation
        {
            AssetFileName = Field<AnnotationImageStore>(host.Editor, "_imageStore").RegisterInMemory(Solid(4, 4, 0x60)).AssetFileName,
            SourceWidth = 4, SourceHeight = 4, Rect = new RectD(180, 140, 40, 40),
        },
        _ => throw new ArgumentException(kind),
    };

    private static FrameworkElement[] VisibleGroups(PropertyGroupsPanel panel) => panel.Children.OfType<FrameworkElement>()
        .Where(group => group.IsVisible && group.ActualWidth > 0).ToArray();

    private static ScrollViewer PropertyScroll(InspectorContractHost host) => Descendants(host.Editor).OfType<ScrollViewer>()
        .Single(viewer => viewer.Name == "AnnotationPropertyBarScroll");

    private static MouseWheelEventArgs WheelOverBar(UIElement bar, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent, Source = bar,
        };
        bar.RaiseEvent(args);
        return args;
    }

    private static void AssertInside(Control control, FrameworkElement viewport, string context)
    {
        Rect bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5
            && bounds.Right <= viewport.ActualWidth + 0.5 && bounds.Bottom <= viewport.ActualHeight + 0.5,
            $"{context}: {AutomationProperties.GetName(control)} is clipped: {bounds} within {viewport.RenderSize}.");
    }

    private static ViewportSnapshot Snapshot(InspectorContractHost host)
    {
        var viewport = Field<FrameworkElement>(host.Editor, "_viewport");
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        return new ViewportSnapshot(viewport.ActualWidth, viewport.ActualHeight,
            Field<Border>(host.Editor, "_inspectorPanel").Height, surface.DipPerPixel,
            surface.TransformToAncestor(host.Window).TransformBounds(surface.FrameRectDip));
    }

    private static void AssertSameGeometry(ViewportSnapshot before, InspectorContractHost host, string context)
    {
        ViewportSnapshot after = Snapshot(host);
        AssertClose(before.Width, after.Width, context + " viewport width");
        AssertClose(before.Height, after.Height, context + " viewport height");
        AssertClose(before.Budget, after.Budget, context + " inspector budget");
        AssertClose(before.Scale, after.Scale, context + " image scale");
        AssertClose(before.Bounds.X, after.Bounds.X, context + " image left");
        AssertClose(before.Bounds.Y, after.Bounds.Y, context + " image top");
        AssertClose(before.Bounds.Width, after.Bounds.Width, context + " image width");
        AssertClose(before.Bounds.Height, after.Bounds.Height, context + " image height");
    }

    private readonly record struct ViewportSnapshot(double Width, double Height, double Budget, double Scale, Rect Bounds);

    private static void AssertClose(double expected, double actual, string context) =>
        Assert.True(Math.Abs(expected - actual) < 0.0000001, $"{context}: {expected:R} changed to {actual:R}.");
}

internal sealed class InspectorContractHost : IDisposable
{
    private readonly Func<AnnotationDefaults>? _read = AnnotationEditorPreferences.Read;
    private readonly Action<AnnotationDefaults>? _write = AnnotationEditorPreferences.Write;
    private readonly Func<bool>? _sourceTitle = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
    private readonly bool? _animations = FluidMotion.AnimationsEnabledOverrideForTest;

    internal InspectorContractHost(double width = 1000)
    {
        AnnotationEditorPreferences.Read = () => new AnnotationDefaults { LastTool = "Select", StrokeThickness = 3 };
        AnnotationEditorPreferences.Write = _ => { };
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => false;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var bitmap = Solid(900, 520, 0x30);
        var bounds = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        Editor = new AnnotationEditorControl(new FrozenFrame(bitmap, bounds, null, 0), bounds, bitmap, null, null);
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        Window = new Window
        {
            Content = Editor, Resources = resources, Width = width, Height = 640,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = -6000, Top = -6000, ShowActivated = false, ShowInTaskbar = false,
        };
        Window.Show();
        VisualTreeHelper.SetRootDpi(Window, new DpiScale(1, 1));
        foreach (UIElement element in Descendants(Window).Prepend(Window).OfType<UIElement>())
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
        }
        Settle();
    }

    internal Window Window { get; }
    internal AnnotationEditorControl Editor { get; }
    internal PropertyGroupsPanel PropertyGroups => Descendants(Editor).OfType<PropertyGroupsPanel>().Single();
    internal void Settle() => Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(Window.UpdateLayout));

    public void Dispose()
    {
        Window.Close();
        AnnotationEditorPreferences.Read = _read;
        AnnotationEditorPreferences.Write = _write;
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = _sourceTitle;
        FluidMotion.AnimationsEnabledOverrideForTest = _animations;
    }
}
