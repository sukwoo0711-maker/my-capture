using MyCapture.TutorialGenerator.Scenes;

namespace MyCapture.TutorialGenerator;

/// <summary>Maps each TutorialCatalog id to the scene that renders it.</summary>
internal static class SceneCatalog
{
    internal static TutorialScene Create(string id, string language)
    {
        if (EditorScene.Features.Contains(id)) return new EditorScene(id, language);
        throw new NotSupportedException($"No scene renders '{id}' yet.");
    }
}
