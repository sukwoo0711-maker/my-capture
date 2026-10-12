using System.Windows.Media.Imaging;

/// <summary>
/// One feature per renderer. Constructor and Render use fixture content only, never native input,
/// screen acquisition, user profile, clipboard, persistence, or App.Run/Window.Show.
/// Render is called in storyboard order on the generator STA. Keep only static scenes and current
/// product UI state; do not accumulate rendered frame buffers. Return a full opaque 640x360 frame.
/// Product input state changes immediately. Cursor, click feedback and presentation may ease.
/// </summary>
internal interface ITutorialSceneRenderer : IDisposable
{
    string Id { get; }
    TutorialStoryboard Storyboard { get; }
    IReadOnlyList<int> KeyFrames { get; }
    BitmapSource Render(TutorialFramePlan frame);
}
