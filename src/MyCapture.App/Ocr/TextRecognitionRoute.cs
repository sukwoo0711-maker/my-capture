using System.Windows.Media.Imaging;

namespace MyCapture.App.Ocr;

/// <summary>
/// The app's route from an image on screen (the editor's current image) to the shared text
/// recognition window.
/// </summary>
/// <remarks>
/// The app sets it once its recognition service exists; diagnostics and isolated controls leave
/// it unset, and a command that needs it then says recognition is unavailable.
/// </remarks>
internal static class TextRecognitionRoute
{
    /// <summary>Recognises an image and shows the result window, with a short label for its status line.</summary>
    internal static Action<BitmapSource, string>? Show { get; set; }

    /// <summary>Whether a recognition engine can run on this PC.</summary>
    internal static Func<bool>? IsAvailable { get; set; }

    /// <summary>A route is set and its engine can run.</summary>
    internal static bool CanShow => Show is not null && IsAvailable?.Invoke() != false;
}
