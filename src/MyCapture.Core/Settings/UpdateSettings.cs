namespace MyCapture.Core.Settings;

/// <summary>Update preferences and local observations shared by the supported release channels.</summary>
public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? DismissedVersion { get; set; }
}
