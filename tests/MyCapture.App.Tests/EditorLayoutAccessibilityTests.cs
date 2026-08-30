using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Structural and accessibility guarantees for the redesigned annotation editor: the calm
/// top-bar / left-rail / canvas / right-inspector / status hierarchy, the absence of any
/// horizontal toolbar scroll, an announced live status region, and a name + tooltip on every
/// icon control. These are visual-tree assertions rather than pixel checks, so they stay stable
/// while catching regressions in the information architecture and automation surface.
/// </summary>
public sealed class EditorLayoutAccessibilityTests
{
    [Fact]
    public void DefaultHierarchyIsTopBarBodyStatus_WithRailViewportInspectorColumns()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            // Root editor is a 3-row grid: command bar, body, status.
            Grid root = host.Editor;
            Assert.Equal(3, root.RowDefinitions.Count);
            Assert.Equal(GridUnitType.Auto, root.RowDefinitions[0].Height.GridUnitType);
            Assert.Equal(GridUnitType.Star, root.RowDefinitions[1].Height.GridUnitType);
            Assert.Equal(GridUnitType.Auto, root.RowDefinitions[2].Height.GridUnitType);

            // The body grid (row 1) has three columns: 52px tool rail, star viewport,
            // and a 232px inspector at the default expanded width.
            Grid body = FindDescendants<Grid>(root)
                .Single(g => g.ColumnDefinitions.Count == 3
                             && g.ColumnDefinitions[0].Width.IsAbsolute
                             && g.ColumnDefinitions[0].Width.Value == 52
                             && g.ColumnDefinitions[1].Width.IsStar
                             && g.ColumnDefinitions[2].Width.IsAbsolute
                             && g.ColumnDefinitions[2].Width.Value == 232);
            Assert.Equal(52, body.ColumnDefinitions[0].Width.Value);
            Assert.True(body.ColumnDefinitions[1].Width.IsStar);
            Assert.Equal(232, body.ColumnDefinitions[2].Width.Value);
        });
    }

    [Fact]
    public void EditorHasNoScrollViewer_SoToolbarNeverScrollsHorizontally()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            List<ScrollViewer> scrollViewers = FindDescendants<ScrollViewer>(host.Editor).ToList();
            Assert.Empty(scrollViewers);
        });
    }

    [Fact]
    public void EveryIconControlHasAutomationNameAndTooltip()
    {
        StaTestHost.Run(() =>
        {
            using EditorHost host = EditorHost.Create();

            // Tool-rail toggle buttons (Select/Rectangle/Arrow/Pen/Text/Image) and every command
            // bar button carry a name; icon-bearing controls also carry a tooltip.
            IReadOnlyList<ToggleButton> tools = FindDescendants<ToggleButton>(host.Editor).ToList();
            Assert.Equal(6, tools.Count);
            foreach (ToggleButton tool in tools)
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(tool)),
                    "A tool-rail button is missing an automation name.");
                Assert.NotNull(tool.ToolTip);

                Viewbox icon = Assert.IsType<Viewbox>(tool.Content);
                Canvas coordinateGrid = Assert.IsType<Canvas>(icon.Child);
                Assert.Equal(20, coordinateGrid.Width);
                Assert.Equal(20, coordinateGrid.Height);
                Path glyph = Assert.IsType<Path>(Assert.Single(coordinateGrid.Children));
                Assert.Equal(1.6, glyph.StrokeThickness, 3);
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
        });
    }

    [Fact]
    public void ColorSwatchesWrapInsideTheInspectorWithoutClipping()
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
            Assert.True(panel.ActualHeight >= 80,
                "The swatch panel did not wrap to a second row inside the compact inspector.");

            foreach (Button swatch in swatches)
            {
                Point topLeft = swatch.TranslatePoint(new Point(0, 0), panel);
                Assert.True(topLeft.X >= -0.5 && topLeft.X + swatch.ActualWidth <= panel.ActualWidth + 0.5,
                    $"A color swatch extends outside the inspector panel: x={topLeft.X}, width={swatch.ActualWidth}, panel={panel.ActualWidth}.");
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

        int count = VisualTreeHelper.GetChildrenCount(root);
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

            // A default-size window keeps the inspector expanded (above the 860px collapse width)
            // so the three-column body is exercised at the design's default hierarchy.
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
