using System.Runtime.InteropServices;

namespace MyCapture.App;

/// <summary>Existing choices and injectable effects for the first-use guide.</summary>
internal sealed class WelcomeOptions
{
    internal bool Replay { get; init; }
    internal bool? StartAtSignIn { get; init; }
    internal bool? UseAccurateOcr { get; init; }
    internal IWelcomeClipboard? Clipboard { get; init; }
    internal bool? ReducedMotion { get; init; }
    internal double TextScale { get; init; } = 1;

    /// <summary>How long a step may pass without anything happening before its next action is said again.</summary>
    internal TimeSpan HintDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The monitor the guide centres on and is limited to; the one under the pointer unless a test says otherwise.</summary>
    internal Func<MyCapture.Platform.Display.MonitorInfo>? MonitorProvider { get; init; }
    internal Func<ITutorialMedia>? MediaFactory { get; init; }
}

/// <summary>A write is requested only by the explicit practice-table copy action.</summary>
internal interface IWelcomeClipboard
{
    bool TryCopyText(string text);
}

internal sealed class WelcomeClipboard : IWelcomeClipboard
{
    public bool TryCopyText(string text)
    {
        try
        {
            ClipboardWriter.SetText(text);
            return true;
        }
        catch (ExternalException) { return false; }
    }
}

internal enum WelcomeStep { Capture = 1, Pin = 2, Library = 3, Finish = 4 }
