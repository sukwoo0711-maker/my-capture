namespace MyCapture.App.Updates;

/// <summary>All updater UI text is centralized for subsequent localization.</summary>
internal static class UpdateStrings
{
    internal static string Title => UiText.Get("Text_9EA9BD59E20C");
    internal static string Check => UiText.Get("Text_05184F5EA537");
    internal static string InstallPortable => UiText.Get("Text_1764E00FE2EE");
    internal static string Cancel => UiText.Get("Text_1125C68F3C22");
    internal static string Install => UiText.Get("Text_F91180061AA3");
    internal static string Description => UiText.Get("Text_1C944F0E89C3");
    internal static string Checking => UiText.Get("Text_9DB22E7B0ADA");
    internal static string Ready => UiText.Get("Text_C68C66E8758A");
    internal static string UpToDate => UiText.Get("Text_2BEE8F5FACA0");
    internal static string Cancelled => UiText.Get("Text_0226F79FE6FB");
    internal static string Busy => UiText.Get("Text_D2ABBB3968D3");
    internal static string Starting => UiText.Get("Text_5C2CD3EFE79B");
    internal static string Failed => UiText.Get("Text_B0F8B4B6D520");
    internal static string HelperFailed => UiText.Get("Text_42DA25F8F77F");
    internal static string TargetDescription(UpdateTarget target) => Description + Environment.NewLine + Environment.NewLine +
        (target.IsPortableMigration
            ? UiText.Format("Text_195F0315EBCE", target.InstallRoot, Environment.NewLine)
            : UiText.Format("Text_7926541DAB63", target.InstallRoot, Environment.NewLine));
    internal static string Current(UpdateVersion version) => UiText.Format("Text_E285D4BC2FA3", version);
    internal static string Download(UpdateProgress progress) => progress.Phase switch
    {
        UpdatePhase.DownloadingInstaller => UiText.Format("Text_CE2F7B272674", progress.Percent.GetValueOrDefault()),
        UpdatePhase.DownloadingChecksums => UiText.Get("Text_42B9E5111FE2"),
        UpdatePhase.VerifyingIntegrity => UiText.Get("Text_18AB379A433A"),
        UpdatePhase.Ready => Ready,
        _ => Checking,
    };
    internal static string Error(UpdateErrorKind kind) => kind switch
    {
        UpdateErrorKind.AlreadyUpToDate => UpToDate,
        UpdateErrorKind.Cancelled => Cancelled,
        UpdateErrorKind.RateLimited => UiText.Get("Text_875E3EA22A90"),
        UpdateErrorKind.HashMismatch or UpdateErrorKind.InvalidUrl or UpdateErrorKind.ChecksumParseFailed or UpdateErrorKind.PayloadTooLarge => UiText.Get("Text_C48278DA0670"),
        _ => Failed,
    };
}
