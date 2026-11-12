namespace MyCapture.App.Pinning;

internal sealed partial class PinWindow
{
    // The reason the next Failed state shows instead of the general sentence; null outside that call.
    private string? _translationFailureMessage;

    /// <summary>Raised when the user invokes the command while a translation checks, downloads or translates.</summary>
    internal event Action? TranslationCancelRequested;

    /// <summary>A model check, download or translation runs; the command then cancels it.</summary>
    internal bool IsTranslationRunning => TranslationState is PinTranslationState.Checking
        or PinTranslationState.Downloading or PinTranslationState.Translating;

    /// <summary>Test hook: the command's label, help and whether it can be invoked.</summary>
    internal (string Label, string Help, bool Enabled) TranslationActionForTest =>
        (TranslationActionLabel, TranslationButtonHelp, TranslationActionEnabled);

    /// <summary>The translation stopped for <paramref name="message"/>: the command offers a retry and the message says why.</summary>
    internal void SetTranslationFailed(string message)
    {
        if (_isClosed) return;
        _translationFailureMessage = message;
        try { SetTranslationState(PinTranslationState.Failed); }
        finally { _translationFailureMessage = null; }
    }
}
