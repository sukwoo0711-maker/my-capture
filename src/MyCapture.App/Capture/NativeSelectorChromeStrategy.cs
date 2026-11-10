namespace MyCapture.App.Capture;

/// <summary>Mutually exclusive experiments; the native selector itself remains opt-in.</summary>
internal enum NativeSelectorChromeStrategy
{
    Retained,
    Fresh,
    Parallel,
    Dib,
}
