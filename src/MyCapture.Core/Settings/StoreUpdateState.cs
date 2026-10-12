namespace MyCapture.Core.Settings;

/// <summary>Local version observations; never an inferred installation percentage.</summary>
public sealed class StoreUpdateState
{
    public string? LastSeenVersion { get; set; }
    public string? PendingFromVersion { get; set; }
    public string? CompletionNoticeVersion { get; set; }

    public bool ObserveLaunch(string currentVersion, bool packaged)
    {
        if (!packaged || !Version.TryParse(currentVersion, out Version? current)) return false;
        bool changed = LastSeenVersion != currentVersion;
        if (Version.TryParse(LastSeenVersion, out Version? previous) && current > previous)
        {
            CompletionNoticeVersion = currentVersion;
            PendingFromVersion = null;
        }
        LastSeenVersion = currentVersion;
        return changed;
    }
}
