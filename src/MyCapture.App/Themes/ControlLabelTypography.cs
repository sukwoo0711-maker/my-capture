using System.Windows.Controls;
using System.Windows.Documents;

namespace MyCapture.App.Themes;

/// <summary>Role-one labels keep their original weight in either contrast mode.</summary>
internal static class ControlLabelTypography
{
    internal const string ResourceKey = "FontWeight.ControlLabel";

    internal static TextBlock Apply(TextBlock label)
    {
        label.SetResourceReference(TextBlock.FontWeightProperty, ResourceKey);
        return label;
    }

    internal static Run Apply(Run label)
    {
        label.SetResourceReference(TextElement.FontWeightProperty, ResourceKey);
        return label;
    }
}
