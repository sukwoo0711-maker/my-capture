# ADR 0008 — PowerShell 없는 설치·제거 실행 파일

상태: 채택 — 패키지 및 일반 데스크톱 통합 검증 대기
기준: MyCapture 4.2.1, 기존 설치기 `4769935`
관련: [ADR 0007](0007-windows10-22h2-support.md)

## 배경

기존 IExpress 패키지는 배치 파일에서 PowerShell 설치 스크립트를 실행했습니다. Windows의 스크립트 실행 정책이나 제한된 언어 모드 때문에 설치 본문이 시작되지 않는 경로가 있었습니다. 사용자는 설치·제거 스크립트를 작은 실행 파일로 교체하고 해시 검증과 롤백을 유지하도록 결정했습니다.

## 결정

`MyCapture.Setup`을 .NET Framework 4.8 x64 콘솔 실행 파일로 빌드합니다. Windows 10 22H2와 Windows 11에 포함된 Framework를 사용하므로 설치기에 별도 런타임을 함께 배포하지 않습니다. 새 NuGet 의존성은 추가하지 않습니다. JSON은 `DataContractJsonSerializer`, ZIP은 `System.IO.Compression`, 바로가기는 `IShellLinkW`와 `IPersistFile`, 등록은 HKCU 레지스트리를 사용합니다.

IExpress는 `AppLaunched=MyCaptureSetup.exe`로 직접 실행하며 두 quiet 명령은 `MyCaptureSetup.exe /quiet`입니다. 설치 경로에는 배치·PowerShell 호스트가 없습니다. 설치 위치의 `MyCaptureUninstall.exe`는 같은 바이너리의 복사본이며 `/uninstall`로 실행합니다. 실행 manifest는 `asInvoker`, `uiAccess=false`, Windows 10/11 호환성을 명시하여 이름에 Setup 또는 Uninstall이 있어도 관리자 승격을 요청하지 않습니다.

| 대안 | 장점 | 이번 결정의 이유 |
| --- | --- | --- |
| 앱 자체 `--install` 모드 | 별도 프로젝트가 줄어듭니다. | WPF 앱과 .NET 10 런타임을 설치 전에 로드하고 설치 중인 앱과 설치기 수명 주기가 얽힙니다. |
| .NET 10 단일 파일·NativeAOT | 최신 런타임과 AOT 실행을 사용할 수 있습니다. | 자체 포함 런타임 또는 AOT 빌드 도구와 interop 검증이 추가됩니다. |
| 네이티브 C++ | CLR 의존성이 없습니다. | JSON·ZIP·해시·COM 및 오류 정리 구현과 유지 비용이 커집니다. |
| MSI/WiX | Windows Installer 표준 관리와 복구 기능을 제공합니다. | 기존 사용자별 경로·영수증·소유권·업데이트 흐름의 포트보다 패키지 체계 변경 범위가 큽니다. |
| PowerShell 유지 | 기존 동작을 유지하기 쉽습니다. | 실행 정책 의존성이 남으므로 선택한 요구를 충족하지 못합니다. |
| .NET Framework 4.8 실행 파일 | 지원 OS에 기본 포함되어 있고 작은 실행 파일로 기존 로직을 옮길 수 있습니다. | 이번 구현으로 선택합니다. CLR 손상 시의 한계는 아래와 같습니다. |

## 유지하는 계약

- 사용자별 기본 경로 `%LOCALAPPDATA%\Programs\MyCapture`, 시작 메뉴 `MyCapture`, 기존 HKCU 제거 키와 표시 값은 유지합니다. 앱과 사용자 데이터 경로는 분리되어 있으며 설치기는 `%APPDATA%\MyCapture`를 열거나 수정하지 않습니다.
- OS 하한은 Windows 10 22H2 build 19045입니다. ARM64의 x64 에뮬레이션은 Windows 11 build 22000 이상이며 `IsWow64Process2`로 네이티브 아키텍처를 확인합니다. Core/Nano 환경은 거부합니다.
- 스키마 1, SemVer, 파일 수 1–10,000, 파일당 최대 1 GiB, 합계 최대 2 GiB, SHA-256과 길이 검증을 유지합니다. 절대 경로, ADS, 역슬래시, 상위 경로, 중복 ZIP 항목, 경로 이탈, junction/symlink를 거부합니다. 링크 검사 범위는 구형과 같습니다. 직접 설치는 설치 루트 자체만 검사하므로 프로필 폴더를 junction으로 옮긴 PC에서도 설치됩니다. 모든 상위 폴더까지 검사하는 규칙은 업데이트 세션에만 적용합니다. Windows 예약 파일명과 끝의 점·공백도 거부합니다.
- 인접 stage/backup 폴더에서 검증 후 이동하며 `AfterBackup`·`AfterCommit` 실패는 이전 트리를 복원합니다. 설치된 파일 목록을 다시 검증합니다. 업데이트 세션 설치는 이전 설치 목록에 없는 사용자 추가 파일을 보존하고 새 패키지와 충돌하면 중단합니다. 직접 재설치는 구형 설치기와 같이 설치 루트 전체를 새 패키지로 교체합니다.
- `Local\MyCapture.InstallOrUninstall` mutex, 기본 로그 이름과 `ISO-8601 [LEVEL] message` 행 형식, 대화형 결과 창과 설치 후 실행을 유지합니다. 프로세스 종료는 정확한 전체 실행 경로가 일치할 때만 수행합니다. 다른 복사본이 있으면 설치를 중단합니다. 업데이트 세션에서는 다시 시작된 앱을 종료하지 않습니다.
- 옵션은 `/quiet`(`/Q`), `--install-root`, `--payload`, `--manifest`, `--log`, `--verify-only`, `--no-shell-integration`, `--minimum-free-bytes`, `--test-fault`입니다. payload와 manifest 기본값은 실행 파일 폴더에서 찾습니다. 알 수 없는 옵션은 21로 거부합니다.
- 공개 자산 이름, portable ZIP, `README-OFFLINE.txt`, `SHA256SUMS.txt`, `installer-manifest.json` 및 Store 경로는 유지합니다. portable 앱은 계속 자체 포함 .NET 10 빌드입니다.

## 정방향·역방향 호환성

이미 배포된 앱이 IExpress setup에 `/Q`와 `MYCAPTURE_UPDATE_SESSION`을 전달하는 경로를 유지합니다. 세션 디렉터리 이름·부모 경로·토큰·버전·소유권·부모 PID·커스텀 경로의 `NoShellIntegration`을 검사합니다. `install-result.json`은 `Token`, `Version`, `ExitCode`, `InstallRoot`, `LogPath` 다섯 필드이며 UTF-8 BOM 없이 `.tmp`를 `CreateNew`로 쓴 후 이동합니다.

구형 `install-manifest.json`과 제품 이름이 MyCapture인 `MyCapture.dll`은 기존 설치로 인식합니다. 구형 제거 bootstrap 세 파일은 소유 파일로 인식하므로 새 설치 루트에 보존하지 않습니다. 새 설치 완료 manifest의 `Files`에는 `MyCaptureUninstall.exe`, 그 `.config`, `SETUP-HELP.txt`를 추가하고 개수·합계를 조정합니다. 이 기록은 구형 설치기가 새 제거 파일을 사용자 파일로 보존하지 않게 하며 역방향 재설치 후 새 제거기 잔여 파일을 없앱니다.

설치 완료 목록의 세 bootstrap 항목은 payload 한도와 별도로 계산합니다. 입력 payload의 10,000개·2 GiB 한도를 줄이지 않습니다. 구형 설치기로 되돌리는 검증은 구형 직접 재설치 경로를 사용합니다. 구형 업데이트 세션의 installed-manifest 파서는 원래 10,000개 한도이므로 최대 크기 경계의 신형 설치 목록은 읽지 못할 수 있습니다.

제거 전 검증은 구형 `Assert-VerifiedInstallation`과 같습니다. 드라이브 루트, 루트 자체의 junction·symlink, manifest의 존재·유효성·Product, 설치된 제거 실행 파일의 무결성을 확인하며 업데이트 경계 검사는 하지 않습니다. 구형과 다른 점은 하나입니다. 설치 루트 안쪽에 junction·symbolic link가 있으면 제거를 30으로 거부하고 루트를 남깁니다. 새 삭제 코드는 링크를 따라가지 않지만, 링크 대상에 있을 수 있는 사용자 데이터를 보호하기 위한 의도적 차이입니다. 제거기는 자체 파일을 검증한 뒤 현재 사용자와 SYSTEM만 접근하는 고유 TEMP 폴더로 복사합니다. 후속 프로세스는 원래 프로세스의 PID와 시작 시각으로 종료를 기다리고 mutex를 다시 취득하며, manifest 해시와 소유권을 재확인합니다. 설치 루트가 제거된 후 정확히 해당 설치를 가리키는 시작 프로그램 값, 바로가기, 제거 등록만 정리합니다. 구형 cleanup과 같이 manifest의 셸 통합 기록과 관계없이 정리를 시도합니다. 바로가기는 대상 경로가, 제거 등록은 `InstallLocation`이 설치 루트와 일치할 때만 삭제합니다. 테스트 대상이 기록된 설치는 그 격리 대상만 사용하며 실제 시작 메뉴와 HKCU 키에는 접근하지 않습니다. `NoShellIntegration` 신규 설치는 셸 항목을 만들지 않으며 기존 설치를 업데이트할 때에는 앞선 설치의 셸 소유권을 유지합니다.

시작 프로그램 값은 앱이 쓰는 값이므로 셸 통합 여부와 관계없이 구형 cleanup의 규칙을 그대로 따릅니다. `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`의 `MyCapture` 명령에서 실행 파일 경로를 구형과 같은 방식으로 추출합니다. 그 경로가 `<설치 루트>\MyCapture.exe`와 같을 때만 값을 삭제하고, 다른 경로이면 값을 보존하고 로그에 남깁니다. 테스트 대상이 지정된 설치는 Run 키 대신 `<테스트 등록 키>\Run` 하위 키를 사용합니다.

구형 설치에서 `NoShellIntegration`으로 업데이트할 때에는 새 셸 항목을 만들지 않습니다. 다만 기존 등록의 설치 경로와 제거 명령, 또는 바로가기 대상이 정확히 해당 구형 설치를 가리키면 제거 명령만 새 실행 파일로 고칩니다. 이 소유권은 새 manifest에 기록하여 제거 시 정리합니다. 격리 테스트는 이 읽기·수정 경로에도 테스트 전용 키와 TEMP 시작 메뉴를 전달합니다. 각 설치에 새 식별자를 부여하므로 같은 버전을 재설치했더라도 대기 중인 이전 제거 작업은 manifest 해시 변경을 감지합니다.

파일 정리는 구형 설치기와 같은 영구 삭제입니다. 쓰기 검사 파일은 만든 직후 삭제합니다. staging 폴더, 성공 후 rollback backup, rollback 시 새로 넣은 설치 루트는 재귀 삭제하며 최대 6회 시도하고 실패할 때마다 180×n ms를 기다립니다. 제거 시 설치 루트는 manifest 해시·소유권·링크 검사를 다시 통과한 뒤 최대 30회 삭제를 시도하고 min(1000, 150+75×n) ms를 기다립니다. 읽기 전용 파일도 삭제하며 링크는 따라가지 않습니다. 바로가기는 대상이 일치할 때만 삭제하고 시작 메뉴 폴더는 비었을 때만 삭제합니다. 제거 완료와 작업 예약은 로그에서 구분합니다. 설치 폴더 안의 추가 파일은 제거 시 해당 폴더와 함께 삭제하며 외부 사용자 데이터는 유지합니다.

구형 후속 작업 스크립트는 마지막에 자신을 삭제했습니다. 실행 중인 실행 파일은 자신을 삭제할 수 없으므로 후속 작업은 자기 폴더에서 실행 파일을 제외한 `.config`, 도움말, `cleanup.json`을 삭제하고 남은 폴더 경로를 로그에 남깁니다. 이후 설치(`--verify-only` 제외)와 제거 실행은 `%TEMP%` 바로 아래의 `.MyCapture-uninstall-cleanup-<소문자 32자리 hex>` 폴더 중 후속 작업 파일만 들어 있는 폴더를 영구 삭제합니다. 실행 파일을 먼저 삭제하므로 실행 중인 후속 작업의 폴더는 그대로 남습니다. 다른 내용이 있거나 접근할 수 없는 폴더도 남기고 로그에 기록합니다. 관리자 권한이 필요한 재부팅 시 삭제 예약과 cmd·PowerShell 호출은 사용하지 않습니다.

## 런타임과 종료 코드

`.exe.config`는 `<supportedRuntime version="v4.0" />`를 사용하며 4.8 SKU를 고정하지 않습니다. `Main`의 초기 단계에서 HKLM Framework Full Release가 528040 이상인지 확인하고 Compression 어셈블리를 로드합니다. 실패 시 코드 19를 기록하고 도움말을 `%TEMP%\MyCapture-Setup-Help.txt`에 복사합니다. 대화형 모드의 user32 메시지 창은 portable ZIP과 Microsoft Store를 대안으로 제시합니다. CLR 4 자체가 로드되지 않으면 관리 코드 실행 전이므로 Windows의 자체 오류 창이 남습니다. 지원 OS에서 CLR 4는 운영체제 구성 요소입니다.

| 코드 | 의미 |
| --- | --- |
| 0 | 설치·검증 완료 또는 제거 후속 작업 예약 |
| 10 / 11 | OS·Desktop Experience / 아키텍처 미지원 |
| 12 / 13 / 14 | 안전하지 않거나 쓸 수 없는 경로 / 공간 부족 / 무결성 실패 |
| 15 / 16 / 17 | 프로세스 종료 또는 다른 복사본 / 압축 해제 / commit·rollback 실패 |
| 18 | 설치 mutex 충돌 |
| 19 | 필수 Windows 구성 요소 사용 불가. 기존 제한된 PowerShell 의미를 대체합니다. |
| 20 / 21 | 기존 폴더 소유권·사용자 파일 충돌 / 옵션·예상하지 못한 오류 |
| 22 | 사용 종료. 스크립트 시작 bootstrap이 없으므로 반환하지 않습니다. |
| 30 / 31 / 33 / 34 / 35 | 제거 소유권·무결성 / mutex / 프로세스 / 후속 작업 시작 / 예상하지 못한 제거 오류 |
| 32 | 구형 제거 호스트 오류의 보존된 상수입니다. 새 런타임 실패는 19입니다. |

## 검증과 격리

`MyCapture.Setup.Tests`는 net48에서 manifest·경로·ZIP·해시·공간·소유권·사용자 파일·프로세스 선택·양쪽 rollback·mutex·영수증·옵션·실제 제거·시작 프로그램 값 정리·영구 삭제 재시도·후속 작업 폴더 정리·실행 manifest를 검사합니다. 런타임 release/compression probe는 테스트에서 주입하며 제품 CLI에서 바꾸지 않습니다.

실제 바로가기와 등록은 `--test-uninstall-key Software\MyCapture.SetupTests\<32자리 guid>`와 `--test-start-menu <TEMP 아래 정규 절대 경로>`를 함께 지정해 격리합니다. 기본값에는 테스트 대상이 없습니다. 등록 루트 자체·추가 하위 키·다른 HKCU 키·TEMP 루트·TEMP 밖 경로·링크를 거부합니다. 설치 manifest에 해당 대상을 저장해 제거 후속 프로세스도 같은 격리를 유지합니다.

개발 PowerShell 스크립트는 실행 정책을 변경하지 않고 빌드한 실행 파일을 검사합니다. 기존 bootstrap의 시작 마커·정책 오류 텍스트·Notepad 모의 실행 사례는 스크립트 호스트가 없어 적용 대상이 사라졌습니다. `installer-bootstrap-tests.ps1`에서 직접 IExpress 명령, quiet 오류 코드, 특수 문자 경로, 영속 로그, BOM 도움말, CLR 설정, release probe 검사로 대체합니다. 실제 IExpress 패키지 실행과 구형·신형 설치기 왕복은 일반 데스크톱에서 추가로 검증합니다.

`installer-compatibility-tests.ps1`은 `git show 4769935:build/installer/...`에서 구형 소스를 가져옵니다. 실행 정책을 바꾸지 않기 위해 테스트 사본에서 정책 완화 인수만 제거하며 구형 삭제 동작은 그대로 실행합니다. 구형 설치기는 `-NoShellIntegration`으로 실행하며 구형 cleanup의 실제 셸 정리 호출은 비활성화합니다. 따라서 구형 실제 HKCU·시작 메뉴 재등록은 실행하지 않습니다. 신형의 실제 등록과 바로가기는 격리된 테스트 대상으로 확인합니다. 로컬 스크립트 실행이 정책으로 차단되면 정책 목록과 SKIP을 출력합니다.

## 앱 안 업데이트 도우미

`src/MyCapture.App/Updates/UpdateHelper.ps1`, `UpdateInstaller`, 업데이트 도우미 리소스 리더는 4.1.2(ee4581b, #72)부터 제품에 연결되어 있지 않습니다. 업데이트는 Microsoft Store로만 제공합니다. 사용자 결정에 따라 4.3.0에서는 이 파일들을 그대로 둡니다.

4.1.1 이하의 GitHub 앱 안 업데이트는 새 setup.exe를 `/Q`와 `MYCAPTURE_UPDATE_SESSION`으로 실행합니다. 새 설치기는 이 계약을 지킵니다. 이번 결정의 PowerShell 제거 범위는 IExpress 안의 설치기, 업데이트 설치 본문, 설치된 제거기입니다.
