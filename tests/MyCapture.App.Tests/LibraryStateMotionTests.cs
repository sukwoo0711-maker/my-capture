using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using ShapePath = System.Windows.Shapes.Path;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryStateMotionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderSelectionAndDropTargetFadeWithoutReplacingTheChromeBrush(bool motion) =>
        ControlStateMotionTests.Run(motion, (theme, host) =>
        {
            ResourceDictionary folderResources = LoadFolderStyle();
            host.Resources.MergedDictionaries.Add(folderResources);
            var folder = new GalleryFolderCardViewModel(Guid.NewGuid());
            var button = new Button
            {
                Content = "Folder", DataContext = folder,
                Style = (Style)folderResources["Gallery.FolderCardButton"],
            };
            host.Children.Add(button);
            ControlStateMotionTests.Drain(host);
            var chrome = Assert.IsType<ShapePath>(button.Template.FindName("Chrome", button));
            var selected = Assert.IsType<ShapePath>(button.Template.FindName("SelectedLayer", button));
            var focus = Assert.IsType<ShapePath>(button.Template.FindName("FocusRing", button));
            var drop = Assert.IsType<ShapePath>(button.Template.FindName("DropRing", button));
            var restBrush = Assert.IsType<SolidColorBrush>(theme["Surface.Folder"]);
            var selectedBrush = Assert.IsType<SolidColorBrush>(theme["Surface.Selected"]);
            Color restColor = restBrush.Color;
            Color selectedColor = selectedBrush.Color;
            Assert.Equal(MotionSpeed.Normal, FluidMotion.GetTransition(selected));
            Assert.Equal(0, selected.Opacity);
            Assert.False(selected.HasAnimatedProperties);

            foreach ((bool isSelected, bool isDropTarget) in new[] { (false, true), (true, true), (true, false), (false, false) })
            {
                folder.IsSelected = isSelected;
                folder.IsDropTarget = isDropTarget;
                ControlStateMotionTests.Drain(host);

                Assert.Equal(isSelected || isDropTarget ? 1d : 0d,
                    (double)selected.GetAnimationBaseValue(UIElement.OpacityProperty));
                Assert.Equal(motion, selected.HasAnimatedProperties);
                Assert.Same(restBrush, chrome.Fill);
                Assert.Same(selectedBrush, selected.Fill);
                Assert.Equal(restColor, restBrush.Color);
                Assert.Equal(selectedColor, selectedBrush.Color);
                Assert.False(restBrush.HasAnimatedProperties);
                Assert.False(selectedBrush.HasAnimatedProperties);
                Assert.Equal(isSelected ? 1d : 0d, focus.Opacity);
                Assert.Equal(isDropTarget ? 1d : 0d, drop.Opacity);
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotationOwnsItsTransformEvenWhenAnExistingTransformIsMutable(bool frozen) => StaTestHost.Run(() =>
    {
        var shared = new RotateTransform(12);
        if (frozen) shared.Freeze();
        var first = new Border { RenderTransform = shared };
        var second = new Border { RenderTransform = shared };
        FluidMotion.SetRotateTo(first, 90);

        var owned = Assert.IsType<RotateTransform>(first.RenderTransform);
        Assert.NotSame(shared, owned);
        Assert.Same(shared, second.RenderTransform);
        Assert.Equal(12, shared.Angle);
        Assert.Equal(90, owned.Angle);
        Assert.False(owned.HasAnimatedProperties);
        Assert.Equal(new Point(0.5, 0.5), first.RenderTransformOrigin);
        FluidMotion.SetRotateTo(first, 0);
        Assert.Same(owned, first.RenderTransform);
    });

    [Fact]
    public void InitialStateAppearsWholeBeforeLoading() => ControlStateMotionTests.Run(true, (_, host) =>
    {
        var layer = new Border { Opacity = 0 };
        FluidMotion.SetFadeTo(layer, 1);
        FluidMotion.SetRotateTo(layer, 90);
        var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
        Assert.Equal(1, layer.Opacity);
        Assert.Equal(90, rotate.Angle);
        Assert.False(layer.HasAnimatedProperties);
        Assert.False(rotate.HasAnimatedProperties);
        host.Children.Add(layer);
        ControlStateMotionTests.Drain(host);
        Assert.True(layer.IsLoaded);
        Assert.Equal(1, layer.Opacity);
        Assert.Equal(90, rotate.Angle);
        Assert.False(rotate.HasAnimatedProperties);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RapidReversalCommitsTheLatestStateAndSettlesWithoutAStaleClock(bool motion) =>
        ControlStateMotionTests.Run(motion, (_, host) =>
        {
            var layer = new Border { Opacity = 0 };
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            FluidMotion.SetTransition(layer, MotionSpeed.Normal);
            FluidMotion.SetFadeTo(layer, 1);
            FluidMotion.SetRotateTo(layer, 90);
            var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
            Assert.Equal(1, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(90, (double)rotate.GetAnimationBaseValue(RotateTransform.AngleProperty));
            Assert.Equal(motion, layer.HasAnimatedProperties);
            Assert.Equal(motion, rotate.HasAnimatedProperties);
            PumpFor(layer, TimeSpan.FromMilliseconds(45));
            double opacity = layer.Opacity, angle = rotate.Angle;

            FluidMotion.SetFadeTo(layer, 0);
            FluidMotion.SetRotateTo(layer, 0);
            Assert.Equal(0, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(0, (double)rotate.GetAnimationBaseValue(RotateTransform.AngleProperty));
            Assert.Equal(motion, layer.HasAnimatedProperties);
            Assert.Equal(motion, rotate.HasAnimatedProperties);
            // A replacement WPF clock starts on the next render tick. Sample that
            // rendered transition rather than its pre-tick dependency-property value.
            if (motion)
            {
                PumpFor(layer, TimeSpan.FromMilliseconds(20));
                Assert.InRange(layer.Opacity, double.Epsilon, opacity);
                Assert.InRange(rotate.Angle, double.Epsilon, angle);
            }
            PumpFor(layer, TimeSpan.FromMilliseconds(300));
            Assert.Equal(0, layer.Opacity);
            Assert.Equal(0, rotate.Angle);
            // Stop fill releases the animated value; the committed target remains.
            Assert.Equal(0, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(0, (double)rotate.GetAnimationBaseValue(RotateTransform.AngleProperty));
        });

    [Fact]
    public void TurningMotionOffBeforeTheNextGestureCancelsAnInFlightTransition() =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            var layer = new Border { Opacity = 0 };
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            FluidMotion.SetFadeTo(layer, 1);
            FluidMotion.SetRotateTo(layer, 90);
            Assert.True(layer.HasAnimatedProperties);
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            FluidMotion.SetFadeTo(layer, 0);
            FluidMotion.SetRotateTo(layer, 0);
            var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
            Assert.Equal(0, layer.Opacity);
            Assert.Equal(0, rotate.Angle);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(rotate.HasAnimatedProperties);
        });

    [Fact]
    public void UnloadingCommitsTheFinalStateBeforeTheSamePartIsLoadedAgain() =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            var layer = new Border { Opacity = 0 };
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            FluidMotion.SetTransition(layer, MotionSpeed.Deliberate);
            FluidMotion.SetFadeTo(layer, 1);
            FluidMotion.SetRotateTo(layer, 90);
            var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
            Assert.True(layer.HasAnimatedProperties);
            Assert.True(rotate.HasAnimatedProperties);
            host.Children.Remove(layer);
            ControlStateMotionTests.Drain(host);
            Assert.False(layer.IsLoaded);
            Assert.Equal(1, layer.Opacity);
            Assert.Equal(90, rotate.Angle);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(rotate.HasAnimatedProperties);
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            Assert.True(layer.IsLoaded);
            Assert.Equal(1, layer.Opacity);
            Assert.Equal(90, rotate.Angle);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(rotate.HasAnimatedProperties);
        });

    private static ResourceDictionary LoadFolderStyle()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combined = new XElement(p + "ResourceDictionary");
        // Load only the actual folder template and its static dependencies. The plain
        // test host supplies theme brushes without constructing a library or its stores.
        foreach ((string resource, string[] keys) in new[]
        {
            ("ThemeFixture.Controls.xaml", new[] { "StatePart.Normal" }),
            ("GalleryFixture.Window.xaml", new[] { "LibraryUx.RestEdge", "LibraryUx.FolderCardShape", "Gallery.FolderCardButton" }),
        })
        {
            using Stream source = typeof(LibraryStateMotionTests).Assembly.GetManifestResourceStream(resource)!;
            XElement root = XDocument.Load(source).Root!;
            foreach (XAttribute declaration in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
                    && !value.Contains(";assembly=", StringComparison.Ordinal)) value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            XElement dictionary = root.Name == p + "ResourceDictionary" ? root
                : root.Element(p + "Window.Resources")!.Element(p + "ResourceDictionary")!;
            foreach (XElement entry in dictionary.Elements().Where(element => keys.Contains((string?)element.Attribute(x + "Key"))))
                combined.Add(new XElement(entry));
        }
        return (ResourceDictionary)XamlReader.Parse(combined.ToString(SaveOptions.DisableFormatting));
    }

    private static void PumpFor(DispatcherObject owner, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
