namespace MyCapture.App.Capture;

/// <summary>
/// What the quick access card's actions do with its saved capture: copy again, edit, pin, save,
/// open in the library, prepare a file to drag, and copy its text (null when recognition is
/// unavailable). A card shown before its capture is saved receives them once the save succeeds
/// (<see cref="QuickAccessWindow.BindCapture"/>).
/// </summary>
internal sealed record QuickAccessCardActions(
    Func<Task<bool>> Copy,
    Action Edit,
    Func<Task> Pin,
    Func<Task<bool>> Save,
    Action Library,
    Func<Task<string>> Export,
    Func<Task<TextCaptureOutcome>>? CopyText);
