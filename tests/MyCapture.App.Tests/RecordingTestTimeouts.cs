namespace MyCapture.App.Tests;

/// <summary>Bounds for tests that wait on a recorder's capture thread.</summary>
internal static class RecordingTestTimeouts
{
    // A default recording takes its first frame through Windows Graphics Capture: the first grab waits
    // up to 5 s for the session to open, including the process's first borderless-access request, and
    // then up to 2 s for a frame before RegionFrameGrabber falls back to GDI. The capture thread also runs
    // below normal priority, so on a busy runner each of its steps can wait seconds for a CPU. On hosted
    // runners the first recording in a test process has outlasted 5 s waits and used up a 3 s one. Waits on
    // that thread (first frame, encoder factory, park, stop and join) that allowed only 3 to 5 s use this
    // bound; each still returns as soon as its event arrives.
    internal static readonly TimeSpan CaptureThread = TimeSpan.FromSeconds(30);
}
