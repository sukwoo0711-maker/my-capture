using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

internal static class ShortcutHelpRowAssertions
{
    internal static string Title(UniformGrid row)
    {
        Assert.Equal(2, row.Children.Count);
        var title = Assert.IsType<DockPanel>(row.Children[0]);
        return Assert.Single(title.Children.OfType<TextBlock>()).Text;
    }

    internal static string Keys(UniformGrid row)
    {
        Assert.Equal(2, row.Children.Count);
        return AutomationProperties.GetName(Assert.IsType<WrapPanel>(row.Children[1]));
    }
}
