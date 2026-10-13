using System.Windows.Markup;

namespace MyCapture.App.Localization;

/// <summary>
/// The accessible name of a "View tutorial" button: "{feature} 사용법 보기" / "{feature} — View
/// tutorial". It names the feature and contains the visible label, so speech input that says the
/// label reaches the button.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TutorialNameExtension(string featureId) : MarkupExtension
{
    public string FeatureId { get; set; } = featureId;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        FeatureTutorialWindow.OpenButtonName(TutorialCatalog.Get(FeatureId));
}
