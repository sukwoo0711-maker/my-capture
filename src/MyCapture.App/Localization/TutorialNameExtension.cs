using System.Windows.Markup;

namespace MyCapture.App.Localization;

/// <summary>
/// The accessible name of a "View tutorial" button: "{feature} tutorial", the same text the
/// tutorial window shows as its title.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TutorialNameExtension(string featureId) : MarkupExtension
{
    public string FeatureId { get; set; } = featureId;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        FeatureTutorialWindow.TutorialTitle(TutorialCatalog.Get(FeatureId));
}
