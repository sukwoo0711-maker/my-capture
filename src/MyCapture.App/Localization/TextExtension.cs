using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;

namespace MyCapture.App.Localization;

/// <summary>
/// The <c>loc:Text</c> markup extension: a translated resource. In a Korean interface, text that a wrapping
/// <see cref="TextBlock"/> shows keeps each word whole (<see cref="KoreanWordWrap"/>); accessible
/// names, tooltips, button captions and any other target get the resource unchanged.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        string text = UiText.Get(Key);
        if (!KoreanWordWrap.Applies
            || serviceProvider?.GetService(typeof(IProvideValueTarget)) is not IProvideValueTarget { TargetProperty: DependencyProperty property } target)
            return text;
        return target.TargetObject switch
        {
            TextBlock block when property == TextBlock.TextProperty => KoreanWordWrap.ForTextBlock(block, text),
            // A settings row shows its label and its longer note in wrapping text blocks.
            SettingsField when property == HeaderedContentControl.HeaderProperty || property == SettingsField.NoteProperty =>
                KoreanWordWrap.KeepWords(text),
            _ => text,
        };
    }
}
