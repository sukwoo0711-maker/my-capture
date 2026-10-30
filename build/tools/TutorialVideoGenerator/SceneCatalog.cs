using MyCapture.TutorialGenerator.Scenes;

namespace MyCapture.TutorialGenerator;

/// <summary>Maps each TutorialCatalog id to the scene that renders it.</summary>
internal static class SceneCatalog
{
    internal static TutorialScene Create(string id, string language)
    {
        if (PracticePinScene.Features.Contains(id)) return new PracticePinScene(id, language);
        if (PracticeGuideScene.Features.Contains(id)) return new PracticeGuideScene(id, language);
        if (EditorScene.Features.Contains(id)) return new EditorScene(id, language);
        if (CaptureScene.Features.Contains(id)) return new CaptureScene(id, language);
        if (VideoScene.Features.Contains(id)) return new VideoScene(id, language);
        if (PinScene.Features.Contains(id)) return new PinScene(id, language);
        if (LibraryScene.Features.Contains(id)) return new LibraryScene(id, language);
        if (QuickAccessScene.Features.Contains(id)) return new QuickAccessScene(id, language);
        throw new NotSupportedException($"No scene renders '{id}'.");
    }
}
