# MyCapture 설치와 배포

이 문서는 4.3.1까지 README에 있던 설치, 배포 파일, 사용자 데이터 위치, 개인정보와 네트워크 안내를 옮긴 것입니다. 소스 빌드와 자체 진단은 [소스에서 빌드](build.md)에 있습니다.

## 설치와 실행

MyCapture의 기본 배포 경로는 [Microsoft Store](https://apps.microsoft.com/detail/9PB33MC095VR)입니다. Store 설치본은 Microsoft가 서명하고 업데이트도 Store가 제공합니다. 설정의 업데이트 탭은 Microsoft Store의 MyCapture 페이지를 엽니다. 같은 버전의 설치 파일과 포터블 ZIP은 이 저장소의 [GitHub 릴리스](https://github.com/sukwoo0711-maker/my-capture/releases)에도 올립니다. 이 두 파일은 코드 서명이 없고 앱 안 업데이트를 받지 않으므로, 함께 올린 `SHA256SUMS.txt`로 내려받은 파일을 확인하십시오. 사용자 지원과 개인정보처리방침은 공개 [지원 저장소](https://github.com/sukwoo0711-maker/mycapture-support)에 있습니다.

배포 패키지는 .NET 런타임을 포함하는 **self-contained** 빌드이므로 사용자 PC에 .NET을 따로 설치할 필요가 없습니다. 캡처, 편집, 녹화와 OCR은 PC 안에서 처리하며, 앱이 자동으로 인터넷에 연결하는 경우는 PC에 없는 OCR 모델 파일을 내려받을 때뿐입니다. 네트워크 연결 전체 목록은 [개인정보처리방침](https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md)에서 확인할 수 있습니다. Store 패키지를 만들고 제출하는 절차는 [Microsoft Store 배포](store/README.md)를 참고하십시오.

`build\package.ps1`이 만드는 설치 파일과 포터블 ZIP은 Store 패키지의 원본 게시 폴더이자 GitHub 릴리스 자산이며, 서명되지 않습니다. 릴리스 절차 밖에서 만든 파일은 배포하지 마십시오.

내부 확인용 `MyCapture-<version>-win-x64-setup.exe`는 IExpress 패키지이며, 압축 해제 후 `MyCaptureSetup.exe`를 직접 실행합니다. 설치·업데이트 설치·제거에는 PowerShell을 사용하지 않습니다. 설치기는 Windows 10 22H2와 Windows 11에 기본 포함된 .NET Framework 4.8을 사용하며, 관리자 권한을 요청하지 않습니다. 시작 메뉴와 사용자별 제거 등록을 만들고, 같은 실행 파일의 복사본인 `MyCaptureUninstall.exe /uninstall`로 제거합니다.

두 실행 파일은 서명되지 않았으므로 SmartScreen이 다운로드한 IExpress 파일 또는 압축 해제된 실행 파일을 경고할 수 있습니다. 배포 경로와 `SHA256SUMS.txt`의 해시를 확인하고 신뢰하는 경우에만 Windows가 제공하는 **추가 정보 → 실행**을 사용합니다. 조직에서 실행을 차단한 경우 해당 조직의 허용된 설치 경로를 사용합니다. SHA-256은 파일 변경 검증이며 게시자 서명을 대신하지 않습니다.

설치 로그는 `%TEMP%\MyCapture-Install-yyyyMMdd-HHmmss-PID.log`, 제거 로그는 `%TEMP%\MyCapture-Uninstall-yyyyMMdd-HHmmss-PID.log`입니다. 종료 코드 19는 필수 Windows 구성 요소를 사용할 수 없다는 뜻이며, 설치기는 `%TEMP%\MyCapture-Setup-Help.txt`에 도움말을 복사합니다. 포터블 ZIP 또는 Microsoft Store 버전을 대안으로 사용할 수 있습니다. 이전 스크립트 시작 실패 코드 22는 사용하지 않습니다. CLR 4 자체가 손상되어 로드되지 않는 경우에는 Windows의 오류 창이 표시됩니다. 상세 계약은 [ADR 0008](adr/0008-powershell-free-installer.md)에 기록합니다.

기본 사용자 데이터 위치는 다음과 같습니다.

- Store 설치본의 설정, 인덱스, 라이브러리: 앱 전용 폴더 `%LOCALAPPDATA%\Packages\<패키지 패밀리 이름>\LocalState` (앱을 제거하면 함께 삭제)
- 개발 빌드(비패키지)의 설정, 인덱스, 로그: `%APPDATA%\MyCapture`, 라이브러리 관리 사본: `%APPDATA%\MyCapture\captures`
- 빠른 저장 PNG: `%USERPROFILE%\Pictures\Captures`

캡처 저장 폴더와 빠른 저장 폴더는 설정에서 변경할 수 있습니다. 라이브러리 이미지는 생성 후 기본 7일(168시간, 설정에서 변경 가능)이 지나면 오래된 순서대로 자동 삭제하며, 보관한 이미지와 편집 중인 이미지는 지우지 않습니다. 동영상에는 7일 만료를 적용하지 않습니다. 따로 내보낸 파일은 관리 큐 정리 대상이 아닙니다. 개수·용량 제한은 별도로 적용됩니다.

4.3.1의 변경 사항은 [릴리스 노트](releases/4.3.1-release-notes.md)에서 확인할 수 있습니다. 이전 [4.3.0](releases/4.3.0-release-notes.md), [4.2.1](releases/4.2.1-release-notes.md), [4.2.0](releases/4.2.0-release-notes.md), [4.1.5](releases/4.1.5-release-notes.md), [4.1.4](releases/4.1.4-release-notes.md), [4.1.3](releases/4.1.3-release-notes.md), [4.1.2](releases/4.1.2-release-notes.md), [4.1.1](releases/4.1.1-release-notes.md), [4.1.0](releases/4.1.0-release-notes.md), [4.0.2](releases/4.0.2-release-notes.md), [4.0.1](releases/4.0.1-release-notes.md), [4.0.0](releases/4.0.0-release-notes.md), [3.4.0](releases/3.4.0-release-notes.md), [3.2.1](releases/3.2.1-release-notes.md), [3.2.0](releases/3.2.0-release-notes.md), [3.1.7](releases/3.1.7-release-notes.md), [3.1.5](releases/3.1.5-release-notes.md), [3.1.4](releases/3.1.4-release-notes.md), [3.1.3](releases/3.1.3-release-notes.md), [3.1.2](releases/3.1.2-release-notes.md), [3.1.1](releases/3.1.1-release-notes.md), [3.1.0](releases/3.1.0-release-notes.md), [3.0.0](releases/3.0.0-release-notes.md), [2.4.1](releases/2.4.1-release-notes.md), [2.4.0](releases/2.4.0-release-notes.md), [2.3.10](releases/2.3.10-release-notes.md), [2.3.9](releases/2.3.9-release-notes.md), [2.3.8](releases/2.3.8-release-notes.md), [2.3.7](releases/2.3.7-release-notes.md), [2.3.6](releases/2.3.6-release-notes.md), [2.3.5](releases/2.3.5-release-notes.md), [2.3.4](releases/2.3.4-release-notes.md), [2.3.3](releases/2.3.3-release-notes.md), [2.3.2](releases/2.3.2-release-notes.md), [2.3.1](releases/2.3.1-release-notes.md), [2.3.0](releases/2.3.0-release-notes.md), [2.2.2](releases/2.2.2-release-notes.md), [2.2.1](releases/2.2.1-release-notes.md), [2.1.1](releases/2.1.1-release-notes.md), [2.1.0](releases/2.1.0-release-notes.md), [2.0.0](releases/2.0.0-release-notes.md), [1.9.2](releases/1.9.2-release-notes.md), [1.9.0](releases/1.9.0-release-notes.md) 기록도 보존합니다.

### 1.8.3 실행 및 배포 형식 안내

- 일반 실행과 바로가기 더블클릭은 라이브러리 창을 엽니다. 로그인 자동 실행은 `--background`로 트레이에만 상주합니다.
- 설치형과 포터블 ZIP의 앱·런타임 파일은 동일합니다. 설치형은 `%LOCALAPPDATA%\Programs\MyCapture`에 설치하고 시작 메뉴, 제거 프로그램, `install-manifest.json` 소유권 기록을 추가합니다.
- 포터블은 ZIP 전체를 별도 폴더에 풀고 `MyCapture.exe`를 실행하십시오. EXE만 옮기면 DLL과 Assets가 없어 실행할 수 없습니다. 두 형식의 기본 사용자 데이터는 `%APPDATA%\MyCapture`입니다.
- 설치 전 다른 위치의 포터블/설치형을 트레이 메뉴에서 종료하십시오. 두 복사본은 한 Windows 세션에서 동시에 상주하지 않습니다.
- 설치형과 포터블은 앱 안에서 업데이트를 확인하거나 내려받지 않습니다. 업데이트 탭의 **Microsoft Store에서 열기**로 Store 버전을 설치하십시오. Store 버전은 설정·라이브러리를 공유하지 않으므로 설정 내보내기·가져오기로 설정을 옮기고, Store 버전을 실행하기 전에 기존 복사본을 트레이 메뉴에서 종료하십시오. 기존 폴더와 사용자 파일은 자동 삭제하지 않습니다.
- [1.8.3 수정 내역](releases/1.8.3-release-notes.md)과 [검증 기록](releases/1.8.3-validation.md)을 확인하십시오.

## 개인정보와 네트워크

MyCapture는 화면 픽셀, 캡처 기록, 설정, OCR 텍스트를 로컬 파일에 저장합니다. OCR은 Windows의 온디바이스 `Windows.Media.Ocr` 엔진과 PC에 내려받은 PP-OCRv5 모델로 처리하며, 현재 코드에는 캡처 자동 업로드나 제품 분석 텔레메트리가 없습니다. 필요한 OCR 모델 파일이 PC에 없으면 앱 시작 시 ModelScope(modelscope.cn)에서 내려받고, 선택 기능인 이미지 확대 모델은 처음 사용할 때 Amazon S3에서 내려받습니다. GitHub 이미지 첨부는 사용자가 직접 실행할 때만 연결합니다. 업데이트 확인은 앱이 직접 연결하지 않고 Microsoft Store 앱이나 브라우저에서 MyCapture 페이지를 엽니다. 전체 목록은 [개인정보처리방침](https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md)에 정리되어 있습니다. 개인정보 가리기의 탐지 결과에는 인식 문자열을 복제하지 않고 종류·좌표만 유지하며, 추가된 가림막은 저장 전에 이동·삭제·실행 취소할 수 있습니다. 민감한 화면을 캡처했다면 빠른 저장 폴더, 캡처 큐, 클립보드와 로그의 보관 정책도 함께 확인하십시오.

보안 문제는 공개 이슈에 재현 자료를 올리지 말고 [`SECURITY.md`](../SECURITY.md)의 비공개 신고 절차를 따르십시오. 개발 참여 방법은 [`CONTRIBUTING.md`](../CONTRIBUTING.md)를 참고하십시오.

## English summary

**English summary:** MyCapture is a Windows 10 version 22H2 or later (x64; ARM64 x64 emulation requires Windows 11) screenshot, pin, enhanced local OCR, OCR-assisted privacy-redaction, unified image/video library, image editing, cross-monitor region-recording, timed-text video editing, and GIF-export app. MyCapture is distributed mainly through the [Microsoft Store](https://apps.microsoft.com/detail/9PB33MC095VR), which signs the package and delivers updates; the app never checks for or downloads updates itself, and Settings > Update opens the MyCapture page in the Store. The same version's setup file and portable ZIP are also published on this repository's [GitHub releases](https://github.com/sukwoo0711-maker/my-capture/releases); they are unsigned, receive no in-app updates and are verified with the attached `SHA256SUMS.txt`. Support lives in the public [support repository](https://github.com/sukwoo0711-maker/mycapture-support). Packages are self-contained and work without a preinstalled .NET runtime. Capture, editing, recording and OCR run on the PC; the only automatic network access is downloading missing OCR model files (see the [privacy policy](https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md)). The included bootstrap script can install the repository-pinned .NET 10 SDK for source builds without admin rights. Choose Korean, English, or the Windows display language in Settings; the saved choice applies on the next app start. Recording intentionally has no audio.
