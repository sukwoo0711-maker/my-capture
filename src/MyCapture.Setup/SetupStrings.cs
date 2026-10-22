using System;
using System.Globalization;
using System.Linq;

namespace MyCapture.Setup;

public static class SetupInteraction
{
    internal static bool ShowUpdateProgress(Options options, UpdaterSession? validatedSession) =>
        validatedSession?.ShowProgress == true && !options.Quiet && !options.VerifyOnly;
    public static bool IsInteractive(string[] args, string? updateSession) =>
        string.IsNullOrWhiteSpace(updateSession) && !args.Any(a =>
            string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/q", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--cleanup-request", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--verify-only", StringComparison.OrdinalIgnoreCase));
}

public sealed class SetupStrings
{
    private readonly bool korean;
    public SetupStrings(CultureInfo culture) { korean = culture.TwoLetterISOLanguageName == "ko"; }
    private string Text(string ko, string en) => korean ? ko : en;
    public string Title(bool uninstall) => uninstall ? Text("MyCapture 제거", "MyCapture Uninstall") : Text("MyCapture 설치", "MyCapture Setup");
    public string UpdateTitle => Text("MyCapture 업데이트", "MyCapture Update");
    public string UpdateHeading(string? version = null) => Text("MyCapture" + (string.IsNullOrEmpty(version) ? "" : " " + version) + " 업데이트 중",
        "Updating MyCapture" + (string.IsNullOrEmpty(version) ? "" : " " + version));
    public string UpdateFailureHeading => Text("업데이트하지 못했습니다", "Could not update MyCapture");
    public string OpenApplication => Text("MyCapture 열기", "Open MyCapture");
    public string WorkingHeading(bool uninstall, string? version = null) => uninstall ? Text("MyCapture 제거 중", "Removing MyCapture") :
        Text("MyCapture" + (string.IsNullOrEmpty(version) ? "" : " " + version) + " 설치 중", "Installing MyCapture" + (string.IsNullOrEmpty(version) ? "" : " " + version));
    public string SuccessHeading(bool uninstall) => uninstall ? Text("제거 완료", "Uninstall complete") : Text("설치 완료", "Setup complete");
    public string SuccessBody(bool uninstall) => uninstall ?
        Text("MyCapture를 제거했습니다. 저장한 캡처와 설정은 그대로 남아 있습니다.", "MyCapture has been removed. Your saved captures and settings are preserved.") :
        Text("MyCapture를 시작 메뉴에서 열 수 있습니다.", "You can open MyCapture from the Start menu.");
    public string FailureHeading(bool uninstall) => uninstall ? Text("제거하지 못했습니다", "Could not uninstall MyCapture") : Text("설치하지 못했습니다", "Could not install MyCapture");
    public string Finish => Text("마침", "Finish");
    public string Close => Text("닫기", "Close");
    public string Launch => Text("MyCapture 실행", "Launch MyCapture");
    public string OpenLog => Text("로그 열기", "Open log");
    public string Files(int done, int total) => Text("파일 복사 중 (" + done + "/" + total + ")", "Copying files (" + done + "/" + total + ")");
    public string Stage(SetupStage stage)
    {
        switch (stage)
        {
            case SetupStage.VerifyingPackage: return Text("파일 확인 중", "Verifying package");
            case SetupStage.ExtractingFiles: return Text("파일 복사 중", "Copying files");
            case SetupStage.ReplacingVersion: return Text("이전 버전 교체 중", "Replacing the previous version");
            case SetupStage.RegisteringStartMenu: return Text("시작 메뉴 등록 중", "Registering the Start menu");
            case SetupStage.FinalizingInstallation: return Text("설치 마무리 중", "Finishing setup");
            case SetupStage.RollingBack: return Text("이전 버전 복원 중", "Restoring the previous version");
            case SetupStage.VerifyingInstallation: return Text("설치 파일 확인 중", "Verifying installed files");
            case SetupStage.StoppingApplication: return Text("MyCapture 종료 중", "Closing MyCapture");
            case SetupStage.WaitingForParent: return Text("앱 종료 대기 중", "Waiting for the app to close");
            case SetupStage.PreparingRemoval: return Text("제거 준비 중", "Preparing to uninstall");
            case SetupStage.RemovingFiles: return Text("파일 제거 중", "Removing files");
            case SetupStage.RemovingRegistration: return Text("시작 메뉴 정리 중", "Removing Start menu entries");
            default: throw new ArgumentOutOfRangeException(nameof(stage));
        }
    }
    public string Failure(int exitCode, bool uninstall = false)
    {
        switch (exitCode)
        {
            case ExitCodes.UnsupportedOs: return Text("Windows 10 22H2 이상의 데스크톱 환경에서 실행하십시오.", "Run setup on Windows 10 22H2 or later with a desktop environment.");
            case ExitCodes.Architecture: return Text("64비트 Windows 또는 Windows 11 ARM64에서 실행하십시오.", "Run setup on 64-bit Windows or Windows 11 ARM64.");
            case ExitCodes.UnsafePath: return Text("설치 폴더에 접근할 수 없습니다. 폴더 위치와 접근 권한을 확인하십시오.", "The installation folder is unavailable. Check its location and your access permissions.");
            case ExitCodes.Disk: return Text("디스크 여유 공간을 확인한 뒤 다시 실행하십시오.", "Check the available disk space, then run setup again.");
            case ExitCodes.Integrity: return Text("설치 파일이 손상되었거나 누락되었습니다. 다시 다운로드하십시오.", "Setup files are damaged or missing. Download the installer again.");
            case ExitCodes.ProcessStop: return Text("실행 중인 다른 MyCapture를 트레이 메뉴에서 종료한 뒤 다시 실행하십시오.", "Exit other running copies of MyCapture from the tray menu, then run setup again.");
            case ExitCodes.Staging: return Text("파일을 복사하지 못했습니다. 디스크 공간과 폴더 접근 권한을 확인하십시오.", "Files could not be copied. Check disk space and folder access permissions.");
            case ExitCodes.Commit: return Text("설치 파일을 교체하지 못했습니다. 로그를 확인한 뒤 다시 실행하십시오.", "Installed files could not be replaced. Check the log, then run setup again.");
            case ExitCodes.Concurrent: case ExitCodes.UninstallConcurrent: return Text("다른 설치 또는 제거 작업이 끝난 뒤 다시 실행하십시오.", "Wait for the other setup or uninstall operation to finish, then try again.");
            case ExitCodes.ComponentUnavailable: case ExitCodes.UninstallComponent: return Text("필요한 Windows 구성 요소를 사용할 수 없습니다. Windows를 복구하거나 업데이트하십시오.", "A required Windows component is unavailable. Repair or update Windows.");
            case ExitCodes.ExistingInstall: return Text("기존 설치를 확인할 수 없습니다. 설치 폴더를 확인하십시오.", "The existing installation could not be verified. Check the installation folder.");
            case ExitCodes.InvalidUninstall: return Text("설치 정보를 확인할 수 없습니다. MyCapture를 다시 설치한 뒤 제거하십시오.", "Installation information could not be verified. Reinstall MyCapture, then uninstall it.");
            case ExitCodes.UninstallProcess: return Text("MyCapture를 트레이 메뉴에서 종료한 뒤 다시 제거하십시오.", "Exit MyCapture from the tray menu, then uninstall it again.");
            case ExitCodes.CleanupLaunch: return Text("제거 작업을 시작하지 못했습니다. 다시 실행하십시오.", "The uninstall operation could not start. Try again.");
            default: return uninstall ? Text("제거 중 문제가 발생했습니다.", "A problem occurred while uninstalling MyCapture.") : Text("설치 중 문제가 발생했습니다.", "A problem occurred during setup.");
        }
    }
}
