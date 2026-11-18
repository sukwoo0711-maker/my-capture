using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A check mark sits at the centre of the box it is drawn in. Its coordinates start inside the
/// box border, so a mark drawn for the outer box lands low and right and runs into the edge.
/// </summary>
public sealed class CheckMarkGeometryTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("src/MyCapture.App/Themes/Controls.xaml", "Check", 1)]
    [InlineData("src/MyCapture.App/Gallery/GalleryWindow.xaml", "SelectionCheck", 2)]
    public void MarksAreCentredInsideTheirBoxBorder(string file, string name, int count) => StaTestHost.Run(() =>
    {
        XDocument document = XDocument.Load(Path.Combine(RepositoryRoot(), file));
        XElement[] marks = document.Descendants(Presentation + "Path")
            .Where(path => (string?)path.Attribute(Xaml + "Name") == name).ToArray();
        Assert.Equal(count, marks.Length);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml"),
        };
        foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.HighContrast })
        {
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
            foreach (XElement mark in marks)
            {
                XElement box = mark.Parent!;
                Thickness border = ResolvedThickness(box, "BorderThickness", resources);
                Assert.Equal(border.Left, border.Top);
                Assert.Equal(border.Left, border.Right);
                Assert.Equal(border.Left, border.Bottom);
                double inset = border.Left;
                Thickness margin = ResolvedThickness(mark, "Margin", resources);
                double size = box.Attribute("Width") is null ? Number(box.Parent!, "Width") : Number(box, "Width");
                double stroke = Number(mark, "StrokeThickness");
                var pen = new Pen(Brushes.Black, stroke)
                {
                    StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
                };
                // The indeterminate dash replaces the mark through a template setter.
                IEnumerable<string> figures = mark.Ancestors(Presentation + "ControlTemplate").Descendants(Presentation + "Setter")
                    .Where(setter => (string?)setter.Attribute("TargetName") == name && (string?)setter.Attribute("Property") == "Path.Data")
                    .Select(setter => (string)setter.Attribute("Value")!)
                    .Prepend((string)mark.Attribute("Data")!);
                foreach (string figure in figures)
                {
                    Rect bounds = Geometry.Parse(figure).GetRenderBounds(pen);
                    bounds.Offset(inset + margin.Left, inset + margin.Top);
                    double centre = size / 2;
                    Assert.InRange(bounds.Left + bounds.Width / 2, centre - .1, centre + .1);
                    // A check's weight sits at its vertex, so the mark may rise half a DIP; it never sits low.
                    Assert.InRange(bounds.Top + bounds.Height / 2, centre - .5, centre + .1);
                    Assert.True(bounds.Left >= inset + 1 && bounds.Top >= inset + 1
                        && bounds.Right <= size - inset - 1 && bounds.Bottom <= size - inset - 1,
                        $"{file}: {figure} touches the {size}-DIP box edge ({bounds}).");
                }
            }
        }
    });

    private static Thickness ResolvedThickness(XElement element, string attribute, ResourceDictionary resources)
    {
        string? value = (string?)element.Attribute(attribute);
        if (value is null) return new Thickness(0);
        const string prefix = "{DynamicResource ";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith('}'))
            return Assert.IsType<Thickness>(resources[value[prefix.Length..^1]]);
        return (Thickness)new ThicknessConverter().ConvertFromInvariantString(value)!;
    }

    private static double Number(XElement element, string attribute) =>
        double.Parse((string)element.Attribute(attribute)!, CultureInfo.InvariantCulture);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout is required for the check mark audit.");
    }
}
