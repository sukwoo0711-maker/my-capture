namespace MyCapture.App.Updates;

/// <summary>All updater UI text is centralized for subsequent localization.</summary>
internal static class UpdateStrings
{
    internal const string Title = "업데이트";
    internal const string Check = "업데이트하고 다시 시작";
    internal const string InstallPortable = "설치 버전으로 전환하고 다시 시작";
    internal const string Cancel = "다운로드 취소";
    internal const string Install = "설치하고 다시 시작";
    internal const string Description = "공식 GitHub 저장소에서 최신 안정 버전을 확인합니다. 다운로드한 설치 파일의 SHA-256을 확인한 뒤 설치합니다.";
    internal const string Checking = "최신 버전을 확인하는 중…";
    internal const string Ready = "다운로드와 검증이 완료되었습니다. 작업을 저장한 뒤 설치해 주세요.";
    internal const string UpToDate = "최신 버전을 사용하고 있습니다.";
    internal const string Cancelled = "업데이트를 취소했습니다.";
    internal const string Busy = "녹화, 캡처 또는 편집을 마치고 설정을 적용한 뒤 다시 시도해 주세요.";
    internal const string Starting = "앱을 종료하고 업데이트를 설치합니다…";
    internal const string Failed = "업데이트하지 못했습니다. 네트워크와 저장 공간을 확인한 뒤 다시 시도해 주세요.";
    internal const string HelperFailed = "업데이트 설치 또는 재시작을 확인하지 못했습니다. 이 창의 로그 경로를 확인하고 MyCapture를 다시 실행해 주세요.";
    internal static string TargetDescription(UpdateTarget target) => Description + Environment.NewLine + Environment.NewLine +
        (target.IsPortableMigration
            ? $"설치 버전으로 전환합니다. 설치 위치: {target.InstallRoot}{Environment.NewLine}다운로드 후 설치한 앱을 다시 시작합니다. 현재 포터블 폴더와 그 안의 파일은 그대로 둡니다."
            : $"업데이트 위치: {target.InstallRoot}{Environment.NewLine}다운로드 후 이 위치의 앱을 업데이트하고 다시 시작합니다.");
    internal static string Current(UpdateVersion version) => $"현재 버전 {version}";
    internal static string Download(UpdateProgress progress) => progress.Phase switch
    {
        UpdatePhase.DownloadingInstaller => $"설치 파일 다운로드 중… {progress.Percent.GetValueOrDefault():F0}%",
        UpdatePhase.DownloadingChecksums => "검증 정보 다운로드 중…",
        UpdatePhase.VerifyingIntegrity => "설치 파일 검증 중…",
        UpdatePhase.Ready => Ready,
        _ => Checking,
    };
    internal static string Error(UpdateErrorKind kind) => kind switch
    {
        UpdateErrorKind.AlreadyUpToDate => UpToDate,
        UpdateErrorKind.Cancelled => Cancelled,
        UpdateErrorKind.RateLimited => "GitHub 요청 한도에 도달했습니다. 잠시 후 다시 시도해 주세요.",
        UpdateErrorKind.HashMismatch or UpdateErrorKind.InvalidUrl or UpdateErrorKind.ChecksumParseFailed or UpdateErrorKind.PayloadTooLarge => "업데이트 파일의 안전성을 확인할 수 없습니다. 설치하지 않았습니다.",
        _ => Failed,
    };
}
