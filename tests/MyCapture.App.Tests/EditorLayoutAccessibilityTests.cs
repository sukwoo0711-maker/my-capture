using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Structural and accessibility guarantees for the redesigned annotation editor: the calm
/// command-bar / property-bar / left-rail / full-width canvas / status hierarchy, the absence of any
/// horizontal toolbar scroll, an announced live status region, and a name + tooltip on every
/// icon control. These are visual-tree assertions rather than pixel checks, so they stay stable
/// while catching regressions in the information architecture and automation surface.
/// </summary>
public sealed class EditorLayoutAccessibilityTests : KoreanCaptionTest
{
    [Fact]
    public void DefaultHierarchyIsCommandBarBodyStatus_WithPropertiesAboveTheFullWidthViewport()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            // Properties sit below the command bar inside the canvas column. The rail retains
            // the entire body height while the viewport no longer shares width with an inspector.
            Grid root = host.Editor;
            Assert.Equal(3, root.RowDefinitions.Count);
            Assert.Equal(GridUnitType.Auto, root.RowDefinitions[0].Height.GridUnitType);
            Assert.Equal(GridUnitType.Star, root.RowDefinitions[1].Height.GridUnitType);
            Assert.Equal(GridUnitType.Auto, root.RowDefinitions[2].Height.GridUnitType);

            Grid body = FindDescendants<Grid>(root)
                .Single(g => g.ColumnDefinitions.Count == 2
                             && g.ColumnDefinitions[0].Width.IsAbsolute
                             && g.ColumnDefinitions[0].Width.Value == 94
                             && g.ColumnDefinitions[1].Width.IsStar);
            Assert.Equal(1, Grid.GetRow(body));
            Assert.Equal(94, body.ColumnDefinitions[0].Width.Value);
            Assert.True(body.ColumnDefinitions[1].Width.IsStar);
            Assert.Equal(root.ActualWidth, body.ActualWidth, 1);
            Border properties = (Border)typeof(AnnotationEditorControl)
                .GetField("_inspectorPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host.Editor)!;
            Assert.Same(body, properties.Parent);
            Assert.Equal(0, Grid.GetRow(properties));
            Assert.Equal(1, Grid.GetColumn(properties));
            Assert.Equal(2, body.RowDefinitions.Count);
            Assert.True(body.RowDefinitions[0].Height.IsAuto);
            Assert.True(body.RowDefinitions[1].Height.IsStar);
            Border rail = FindDescendants<Border>(body).Single(border => border.Name == "AnnotationToolRail");
            Assert.Equal(2, Grid.GetRowSpan(rail));
            FrameworkElement viewport = body.Children.OfType<FrameworkElement>()
                .Single(child => Grid.GetColumn(child) == 1 && Grid.GetRow(child) == 1);
            Assert.Equal(properties.ActualWidth,
                viewport.ActualWidth + viewport.Margin.Left + viewport.Margin.Right, 1);
        });
    }

    [Fact]
    public void RectangleIsTheInitiallyCheckedTool()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            IReadOnlyList<ToggleButton> tools = FindDescendants<ToggleButton>(host.Editor).ToList();
            ToggleButton rectangle = tools.Single(
                tool => AutomationProperties.GetName(tool) == "사각형 도구 (R)");
            ToggleButton select = tools.Single(
                tool => AutomationProperties.GetName(tool) == "선택 도구 (V)");

            Assert.True(rectangle.IsChecked);
            Assert.False(select.IsChecked);
        });
    }

    [Fact]
    public void OnlyPropertyBarMayScrollVertically_ToolRailNeverScrolls()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            List<ScrollViewer> scrollViewers = FindDescendants<ScrollViewer>(host.Editor).ToList();
            ScrollViewer inspector = Assert.Single(scrollViewers, viewer => viewer.Name == "AnnotationPropertyBarScroll");
            Assert.Equal(ScrollBarVisibility.Disabled, inspector.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Auto, inspector.VerticalScrollBarVisibility);
            Assert.DoesNotContain(scrollViewers, viewer => viewer.Name == "AnnotationToolRailScroll");
            Assert.DoesNotContain(scrollViewers, viewer => viewer.Name == "AnnotationInspectorScroll");
            // Measuring the reserved crop command panel also realizes its native ratio
            // ComboBox dropdown. It is hidden in edit mode and is not a scrolling bar.
            List<ScrollViewer> inspectorChildren = FindDescendants<ScrollViewer>(inspector).ToList();
            ComboBox cropRatio = EditorKeysTests.Field<ComboBox>(host.Editor, "_cropRatio");
            List<ScrollViewer> cropDropdown = FindDescendants<ScrollViewer>(cropRatio).ToList();
            Assert.All(cropDropdown, viewer => Assert.False(viewer.IsVisible));
            Assert.All(scrollViewers.Where(viewer => !ReferenceEquals(viewer, inspector)),
                viewer => Assert.Contains(viewer, inspectorChildren.Concat(cropDropdown)));
        });
    }

    [Fact]
    public void EveryIconControlHasAutomationNameAndTooltip()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            // Tool-rail toggle buttons (every tool except Crop, which is a command bar button
            // since the rail lost its second crop entry point) and every command bar button carry
            // a name; icon-bearing controls also carry a tooltip.
            IReadOnlyList<ToggleButton> tools = FindDescendants<ToggleButton>(host.Editor)
                .Where(button => !string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(button))).ToList();
            // Every tool but Crop (one less than the enum), plus the stamp command beside the image tool.
            Assert.Equal(Enum.GetValues<EditorTool>().Length, tools.Count);
            foreach (ToggleButton tool in tools)
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(tool)),
                    "A tool-rail button is missing an automation name.");
                Assert.NotNull(tool.ToolTip);

                Viewbox icon = Assert.Single(FindDescendants<Viewbox>(tool));
                Canvas coordinateGrid = Assert.IsType<Canvas>(icon.Child);
                Assert.Equal(20, coordinateGrid.Width);
                Assert.Equal(20, coordinateGrid.Height);
                Path glyph = Assert.IsType<Path>(Assert.Single(coordinateGrid.Children));
                // The 20-unit geometry is enlarged inside the resting 24-DIP icon. Check the
                // rendered pen in both axes, including the Viewbox and inner render transforms.
                GeneralTransform transform = glyph.TransformToAncestor(tool);
                Point origin = transform.Transform(new Point());
                double scaleX = (transform.Transform(new Point(1, 0)) - origin).Length;
                double scaleY = (transform.Transform(new Point(0, 1)) - origin).Length;
                Assert.Equal(1.6, glyph.StrokeThickness * scaleX, 3);
                Assert.Equal(1.6, glyph.StrokeThickness * scaleY, 3);
                Assert.Equal(Stretch.None, glyph.Stretch);
                Assert.False(ContainsText(icon),
                    "A tool-rail button must not reintroduce a visible label that can clip.");
            }

            // Buttons whose content is purely an icon (a Viewbox/Path, no text) must still be
            // named and have a tooltip. Buttons with text content are allowed to omit a tooltip.
            foreach (Button button in FindDescendants<Button>(host.Editor))
            {
                bool named = !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button));
                Assert.True(named, "A command button is missing an automation name.");

                if (IsIconOnly(button))
                {
                    Assert.True(button.ToolTip is not null,
                        "An icon-only button is missing a tooltip.");
                }
            }
            foreach (string name in new[] { "NewCapture", "CaptureOptions" })
            {
                Button capture = Assert.Single(FindDescendants<Button>(host.Editor), button => button.Name == name);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(capture)));
                Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(capture.ToolTip)));
                Assert.True(capture.Focusable && capture.IsTabStop);
            }
        });
    }

    [Fact]
    public void SixColorSwatchesStayInOneCompactRowInsideThePropertyBar()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            ToggleButton rectangleTool = FindDescendants<ToggleButton>(host.Editor)
                .Single(tool => AutomationProperties.GetName(tool) == "사각형 도구 (R)");
            rectangleTool.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            host.Editor.UpdateLayout();

            IReadOnlyList<Button> swatches = FindDescendants<Button>(host.Editor)
                .Where(button => AutomationProperties.GetName(button)
                    .StartsWith("색상 #", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(6, swatches.Count);

            WrapPanel panel = Assert.IsType<WrapPanel>(swatches[0].Parent);
            Assert.All(swatches, swatch => Assert.Same(panel, swatch.Parent));
            double firstRow = swatches[0].TranslatePoint(default, panel).Y;
            Assert.All(swatches, swatch => Assert.Equal(firstRow, swatch.TranslatePoint(default, panel).Y, 1));

            foreach (Button swatch in swatches)
            {
                Point topLeft = swatch.TranslatePoint(new Point(0, 0), panel);
                Assert.True(topLeft.X >= -0.5 && topLeft.X + swatch.ActualWidth <= panel.ActualWidth + 0.5,
                    $"A color swatch extends outside its property group: x={topLeft.X}, width={swatch.ActualWidth}, panel={panel.ActualWidth}.");
            }
        });
    }

    [Fact]
    public void StatusRegionIsAPoliteLiveRegionWithAName()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            // The single status sentence is the editor's live feedback channel. Find the polite
            // live-region TextBlock and confirm it is named for assistive technology.
            TextBlock? liveStatus = FindDescendants<TextBlock>(host.Editor)
                .FirstOrDefault(t =>
                    AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);

            Assert.True(liveStatus is not null, "No polite live-region status TextBlock was found.");
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(liveStatus)),
                "The live status region has no automation name.");
        });
    }

    [Fact]
    public void SelectingRestoredShape_SynchronizesStyleWithoutMutatingOrAddingUndo()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();
            var controller = (AnnotationEditorController)typeof(AnnotationEditorControl)
                .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host.Editor)!;
            var rectangle = new RectangleAnnotation
            {
                Rect = new RectD(20, 20, 100, 50),
                StrokeStyle = AnnotationStrokeStyle.Dotted,
                StrokeThickness = 0,
                Fill = new ColorRgba(128, 12, 34, 56),
            };
            controller.Document.Add(rectangle);
            string before = controller.Document.ToJson();
            controller.SetSelected(rectangle);

            ComboBox style = FindDescendants<ComboBox>(host.Editor)
                .Single(combo => AutomationProperties.GetName(combo) == "도형 선 종류");
            Slider fill = FindDescendants<Slider>(host.Editor)
                .Single(slider => AutomationProperties.GetName(slider) == "도형 내부 투명도 (퍼센트)");
            Assert.Equal(2, style.SelectedIndex);
            Assert.Equal(50, fill.Value);
            Assert.False(controller.CanUndo);
            Assert.Equal(before, controller.Document.ToJson());
        });
    }

    [Fact]
    public void CompactEditor_WrapsShapePropertiesAboveTheCanvasWithoutHorizontalScrolling()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();
            Window window = Window.GetWindow(host.Editor);
            window.Width = 680;
            window.Height = 560;
            window.UpdateLayout();
            ScrollViewer inspector = FindDescendants<ScrollViewer>(host.Editor)
                .Single(viewer => viewer.Name == "AnnotationPropertyBarScroll");
            WrapPanel properties = FindDescendants<WrapPanel>(inspector)
                .Single(panel => panel.Name == "AnnotationPropertyBar");
            ComboBox style = FindDescendants<ComboBox>(inspector)
                .Single(combo => AutomationProperties.GetName(combo) == "도형 선 종류");
            Slider fill = FindDescendants<Slider>(inspector)
                .Single(slider => AutomationProperties.GetName(slider) == "도형 내부 투명도 (퍼센트)");
            Assert.True(inspector.IsVisible);
            Assert.True(style.IsVisible);
            Assert.True(fill.IsVisible);
            Assert.True(inspector.ActualWidth >= 500);
            Assert.Equal(ScrollBarVisibility.Disabled, inspector.HorizontalScrollBarVisibility);
            Assert.Equal(0, inspector.ScrollableWidth);
            FrameworkElement[] groups = properties.Children.OfType<FrameworkElement>().Where(item => item.IsVisible).ToArray();
            Assert.True(groups.Select(item => Math.Round(item.TranslatePoint(default, properties).Y)).Distinct().Count() >= 2,
                "Shape property groups must wrap at 680 DIP.");
            IReadOnlyList<Button> swatches = FindDescendants<Button>(inspector)
                .Where(button => AutomationProperties.GetName(button).StartsWith("색상 #", StringComparison.Ordinal)).ToList();
            Assert.Equal(6, swatches.Count);
            foreach (Button swatch in swatches)
            {
                Point origin = swatch.TranslatePoint(new Point(0, 0), inspector);
                Assert.True(origin.X >= -0.5 && origin.X + swatch.ActualWidth <= inspector.ViewportWidth + 1,
                    "A color swatch extends beyond the actual property viewport.");
            }
            fill.BringIntoView();
            window.UpdateLayout();
            Assert.True(inspector.VerticalOffset > 0 || inspector.ScrollableHeight == 0);
        });
    }

    private static bool IsIconOnly(Button button)
    {
        // A button is "icon only" when its content tree contains no non-empty text: the visible
        // affordance is a vector, so the accessible name and tooltip carry all meaning.
        return button.Content switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? true : false,
            null => true,
            DependencyObject content => !ContainsText(content),
            _ => false,
        };
    }

    private static bool ContainsText(DependencyObject root)
    {
        if (root is TextBlock { Text: { Length: > 0 } })
        {
            return true;
        }

        // Grid's logical tree also contains RowDefinition/ColumnDefinition objects.
        // They remain part of the logical walk but are not valid VisualTreeHelper roots.
        int count = root is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < count; i++)
        {
            if (ContainsText(VisualTreeHelper.GetChild(root, i)))
            {
                return true;
            }
        }

        // Content may not be in the visual tree yet; also inspect logical children.
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependency && ContainsText(dependency))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            DependencyObject current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (!ReferenceEquals(current, root) && current is T match)
            {
                yield return match;
            }

            int visualCount = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetChildrenCount(current)
                : 0;
            for (int i = 0; i < visualCount; i++)
            {
                stack.Push(VisualTreeHelper.GetChild(current, i));
            }

            foreach (object child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dependency)
                {
                    stack.Push(dependency);
                }
            }
        }
    }

    private sealed class EditorHost : IDisposable
    {
        private readonly Window _window;

        private EditorHost(Window window, AnnotationEditorControl editor)
        {
            _window = window;
            Editor = editor;
        }

        public AnnotationEditorControl Editor { get; }

        public static EditorHost Create()
        {
            BitmapSource bitmap = Solid(320, 180, 0x44);
            var frame = new FrozenFrame(bitmap, new RectD(0, 0, 320, 180), null, 1);
            var editor = new AnnotationEditorControl(frame, new RectD(0, 0, 320, 180), bitmap);

            // Exercise the default command / property / canvas / status hierarchy offscreen.
            var window = new Window
            {
                Content = editor,
                Width = 1000,
                Height = 640,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
                ShowActivated = false,
                Left = -4000,
                Top = -4000,
            };

            window.Show();
            window.UpdateLayout();
            return new EditorHost(window, editor);
        }

        public void Dispose() => _window.Close();
    }

    private static BitmapSource Solid(int width, int height, byte value)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
