using System.Windows.Markup;
using MyCapture.Core.Localization;

namespace MyCapture.App.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(Key);
}
