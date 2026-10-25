using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class SetupPresentationTests
{
    [Theory]
    [InlineData("/quiet")][InlineData("/QUIET")][InlineData("/q")][InlineData("/Q")]
    [InlineData("--verify-only")][InlineData("--VERIFY-ONLY")]
    [InlineData("--cleanup-request")][InlineData("--CLEANUP-REQUEST")]
    public void UnattendedSwitchesNeverOpenAWindow(string option)
    {
        Assert.False(SetupInteraction.IsInteractive(new[] { option }, null));
        Assert.False(SetupInteraction.IsInteractive(new[] { "/uninstall", option }, null));
        Assert.False(SetupInteraction.IsInteractive(new[] { "--unknown", option }, null));
    }

    [Theory][InlineData(null)][InlineData("")][InlineData(" \t ")]
    public void DirectInstallAndUninstallAreInteractive(string? updateSession)
    {
        Assert.True(SetupInteraction.IsInteractive(Array.Empty<string>(), updateSession));
        Assert.True(SetupInteraction.IsInteractive(new[] { "/uninstall" }, updateSession));
        Assert.True(SetupInteraction.IsInteractive(new[] { "--no-shell-integration" }, updateSession));
    }

    [Theory][InlineData("session.json")][InlineData("invalid-session")][InlineData(" C:\\update.json ")]
    public void AnyNonemptyUpdateSessionSuppressesTheWindowBeforeValidation(string updateSession)
    {
        Assert.False(SetupInteraction.IsInteractive(Array.Empty<string>(), updateSession));
        Assert.False(SetupInteraction.IsInteractive(new[] { "/uninstall" }, updateSession));
    }

    [Theory][InlineData("--quiet")][InlineData("/quietly")][InlineData("--verify-only-other")]
    public void SimilarArgumentsDoNotSuppressInteractiveErrors(string option) =>
        Assert.True(SetupInteraction.IsInteractive(new[] { option }, null));

    [Theory][InlineData(false, 30)][InlineData(true, 1)]
    public void InteractiveCleanupKeepsItsFailureCodeWhileQuietCleanupKeepsTheLegacyCode(bool quiet, int expected)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string request = Path.Combine(directory, "invalid-cleanup-request.json");
        var options = Options.Parse(new[] { "--uninstall-ui", request }, directory);
        options.Quiet = quiet;
        Assert.True(options.Uninstall); Assert.Equal(request, options.UninstallUiRequest); Assert.Null(options.CleanupRequest);
        var log = new SetupLog(Path.Combine(directory, "setup-cleanup-result-tests.log"), directory, true, true);
        Assert.Equal(expected, Uninstaller.Run(options, directory, log));
        var legacy = Options.Parse(new[] { "--cleanup-request", request }, directory);
        Assert.True(legacy.Quiet); Assert.Null(legacy.UninstallUiRequest);
        Assert.Equal(1, Uninstaller.Run(legacy, directory, log));
    }

    [Fact] public void BuiltSetupUsesTheWindowsGuiSubsystem()
    {
        using (var stream = File.OpenRead(Program.Executable))
        using (var reader = new BinaryReader(stream))
        {
            Assert.Equal((ushort)0x5a4d, reader.ReadUInt16());
            stream.Position = 0x3c;
            int pe = reader.ReadInt32();
            Assert.InRange(pe, 0x40, checked((int)stream.Length - 94));
            stream.Position = pe;
            Assert.Equal(0x00004550u, reader.ReadUInt32());
            stream.Position = pe + 24;
            Assert.Contains(reader.ReadUInt16(), new ushort[] { 0x10b, 0x20b });
            stream.Position = pe + 24 + 68;
            Assert.Equal((ushort)2, reader.ReadUInt16());
        }
    }

    [Theory][InlineData("ko-KR")][InlineData("ko")]
    public void KoreanResultPagesUseTheRequestedCopy(string culture)
    {
        var text = new SetupStrings(CultureInfo.GetCultureInfo(culture));
        Assert.Equal("MyCapture 설치", text.Title(false));
        Assert.Equal("MyCapture 제거", text.Title(true));
        Assert.Equal("MyCapture 4.5.2 설치 중", text.WorkingHeading(false, "4.5.2"));
        Assert.Equal("MyCapture 제거 중", text.WorkingHeading(true));
        Assert.Equal("설치 완료", text.SuccessHeading(false));
        Assert.Equal("제거 완료", text.SuccessHeading(true));
        Assert.Equal("MyCapture를 시작 메뉴에서 열 수 있습니다.", text.SuccessBody(false));
        Assert.Equal("MyCapture를 제거했습니다. 저장한 캡처와 설정은 그대로 남아 있습니다.", text.SuccessBody(true));
        Assert.Equal("MyCapture 실행", text.Launch);
        Assert.Equal("마침", text.Finish); Assert.Equal("닫기", text.Close); Assert.Equal("로그 열기", text.OpenLog);
    }

    [Theory][InlineData("en-US")][InlineData("ja-JP")][InlineData("de-DE")][InlineData("")]
    public void OtherCulturesUseEnglish(string culture)
    {
        var text = new SetupStrings(CultureInfo.GetCultureInfo(culture));
        var english = new SetupStrings(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("MyCapture Setup", text.Title(false)); Assert.Equal("MyCapture Uninstall", text.Title(true));
        foreach (int code in FailureCodes()) Assert.Equal(english.Failure(code), text.Failure(code));
        Assert.Equal("Finish", text.Finish); Assert.Equal("Close", text.Close); Assert.Equal("Open log", text.OpenLog);
    }

    [Theory]
    [InlineData(10, "Windows 10 22H2 이상의 데스크톱 환경에서 실행하십시오.", "Run setup on Windows 10 22H2 or later with a desktop environment.")]
    [InlineData(11, "PC의 시스템 종류에 맞는 32비트 또는 64비트 설치 파일을 사용하십시오.", "Use the 32-bit or 64-bit installer that matches this PC.")]
    [InlineData(12, "설치 폴더에 접근할 수 없습니다. 폴더 위치와 접근 권한을 확인하십시오.", "The installation folder is unavailable. Check its location and your access permissions.")]
    [InlineData(13, "디스크 여유 공간을 확인한 뒤 다시 실행하십시오.", "Check the available disk space, then run setup again.")]
    [InlineData(14, "설치 파일이 손상되었거나 누락되었습니다. 다시 다운로드하십시오.", "Setup files are damaged or missing. Download the installer again.")]
    [InlineData(15, "실행 중인 다른 MyCapture를 트레이 메뉴에서 종료한 뒤 다시 실행하십시오.", "Exit other running copies of MyCapture from the tray menu, then run setup again.")]
    [InlineData(16, "파일을 복사하지 못했습니다. 디스크 공간과 폴더 접근 권한을 확인하십시오.", "Files could not be copied. Check disk space and folder access permissions.")]
    [InlineData(17, "설치 파일을 교체하지 못했습니다. 로그를 확인한 뒤 다시 실행하십시오.", "Installed files could not be replaced. Check the log, then run setup again.")]
    [InlineData(18, "다른 설치 또는 제거 작업이 끝난 뒤 다시 실행하십시오.", "Wait for the other setup or uninstall operation to finish, then try again.")]
    [InlineData(19, "필요한 Windows 구성 요소를 사용할 수 없습니다. Windows를 복구하거나 업데이트하십시오.", "A required Windows component is unavailable. Repair or update Windows.")]
    [InlineData(20, "기존 설치를 확인할 수 없습니다. 설치 폴더를 확인하십시오.", "The existing installation could not be verified. Check the installation folder.")]
    [InlineData(21, "설치 중 문제가 발생했습니다.", "A problem occurred during setup.")]
    [InlineData(30, "설치 정보를 확인할 수 없습니다. MyCapture를 다시 설치한 뒤 제거하십시오.", "Installation information could not be verified. Reinstall MyCapture, then uninstall it.")]
    [InlineData(31, "다른 설치 또는 제거 작업이 끝난 뒤 다시 실행하십시오.", "Wait for the other setup or uninstall operation to finish, then try again.")]
    [InlineData(32, "필요한 Windows 구성 요소를 사용할 수 없습니다. Windows를 복구하거나 업데이트하십시오.", "A required Windows component is unavailable. Repair or update Windows.")]
    [InlineData(33, "MyCapture를 트레이 메뉴에서 종료한 뒤 다시 제거하십시오.", "Exit MyCapture from the tray menu, then uninstall it again.")]
    [InlineData(34, "제거 작업을 시작하지 못했습니다. 다시 실행하십시오.", "The uninstall operation could not start. Try again.")]
    [InlineData(35, "제거 중 문제가 발생했습니다.", "A problem occurred while uninstalling MyCapture.")]
    public void ExitCodesHaveActionableKoreanAndEnglishResults(int code, string korean, string english)
    {
        Assert.Equal(korean, new SetupStrings(CultureInfo.GetCultureInfo("ko-KR")).Failure(code, code >= 30));
        Assert.Equal(english, new SetupStrings(CultureInfo.GetCultureInfo("en-US")).Failure(code, code >= 30));
    }

    [Theory][InlineData("ko-KR", "설치 중 문제가 발생했습니다.", "제거 중 문제가 발생했습니다.")]
    [InlineData("en-US", "A problem occurred during setup.", "A problem occurred while uninstalling MyCapture.")]
    public void UnknownFailuresUseTheOperationSpecificFallback(string culture, string install, string uninstall)
    {
        var text = new SetupStrings(CultureInfo.GetCultureInfo(culture));
        Assert.Equal(install, text.Failure(999)); Assert.Equal(uninstall, text.Failure(999, true));
    }

    [Fact] public void KoreanCopyUsesFormalSentencesAndNoExclamations()
    {
        var text = new SetupStrings(CultureInfo.GetCultureInfo("ko-KR"));
        var values = new List<string> { text.Finish, text.Close, text.Launch, text.OpenLog, text.Files(1, 10) };
        foreach (bool uninstall in new[] { false, true })
        {
            values.AddRange(new[] { text.Title(uninstall), text.WorkingHeading(uninstall, "4.5.2"), text.SuccessHeading(uninstall), text.SuccessBody(uninstall), text.FailureHeading(uninstall) });
            values.AddRange(FailureCodes().Select(code => text.Failure(code, uninstall)));
        }
        values.AddRange(Enum.GetValues(typeof(SetupStage)).Cast<SetupStage>().Select(text.Stage));
        foreach (string value in values)
        {
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.DoesNotContain("요.", value); Assert.DoesNotContain("!", value);
        }
    }

    private static IEnumerable<int> FailureCodes() => Enumerable.Range(10, 12).Concat(Enumerable.Range(30, 6)).Append(999);
}
