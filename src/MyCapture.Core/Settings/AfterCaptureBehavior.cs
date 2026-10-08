namespace MyCapture.Core.Settings;

public static class AfterCaptureBehavior
{
    /// <summary>A failed original save keeps the recovery editor available in every mode.</summary>
    public static AfterCaptureAction Resolve(AfterCaptureAction preference, bool originalSaved) =>
        originalSaved && Enum.IsDefined(preference) ? preference : AfterCaptureAction.Editor;
}
