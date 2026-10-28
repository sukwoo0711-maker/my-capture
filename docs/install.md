# MyCapture 설치와 배포

라이브러리에서 연 이미지를 자르면 결과가 깨지던 문제의 수정은 [4.9.1 릴리스 노트](releases/4.9.1-release-notes.md)에서 확인할 수 있습니다.

이 문서는 4.3.1까지 README에 있던 설치, 배포 파일, 사용자 데이터 위치, 개인정보와 네트워크 안내를 옮긴 것입니다. 소스 빌드와 자체 진단은 [소스에서 빌드](build.md)에 있습니다.

## 설치와 실행

MyCapture의 권장 배포 경로는 [Microsoft Store](https://apps.microsoft.com/detail/9PB33MC095VR)입니다. Store 설치본은 Microsoft가 서명하고 업데이트도 Store가 제공합니다. 같은 버전의 설치 파일과 포터블 ZIP은 이 저장소의 [GitHub 릴리스](https://github.com/sukwoo0711-maker/my-capture/releases)에도 올립니다. 설치 파일과 ZIP은 코드 서명이 없으므로, 직접 내려받을 때는 x64용 `SHA256SUMS.txt` 또는 x86용 `SHA256SUMS-win-x86.txt`로 파일을 확인하십시오. 사용자 지원과 개인정보처리방침은 공개 [지원 저장소](https://github.com/sukwoo0711-maker/mycapture-support)에 있습니다.

배포 패키지는 .NET 런타임을 포함하는 **self-contained** 빌드이므로 사용자 PC에 .NET을 따로 설치할 필요가 없습니다. 캡처, 편집, 녹화와 OCR은 PC 안에서 처리합니다. 앱은 새 버전을 자동으로 확인하며, 인식 품질에 따라 PC에 없는 OCR 모델 파일도 내려받습니다. 네트워크 연결 안내는 아래 [개인정보와 네트워크](#개인정보와-네트워크)와 [개인정보처리방침](https://mimiloaf.com/mycapture/privacy/)에서 확인할 수 있습니다. Store 패키지를 만들고 제출하는 절차는 [Microsoft Store 배포](store/README.md)를 참고하십시오.

### 아키텍처 선택

- 64비트 Windows에서는 x64판을 사용합니다. Windows 11 ARM64 PC는 x64 에뮬레이션으로 실행합니다.
- Windows 10 버전 22H2의 32비트 PC에는 x86 호환판을 설치합니다. x86판은 Windows 기본 OCR을 사용하며, AI OCR과 로컬 번역 및 해당 모델 내려받기는 x64판에서 제공합니다.
- GitHub 릴리스에서 `win-x64` 또는 `win-x86`이 붙은 설치 파일·포터블 ZIP을 고릅니다. Microsoft Store 패키지는 x64입니다.

### 업데이트

Store 설치본, GitHub 설치형, 포터블 ZIP은 기본적으로 앱 시작 약 2분 후 새 버전을 확인합니다. 캡처·녹화·편집 중에는 작업이 끝날 때까지 미루며, 마지막 확인에서 20시간이 지나지 않았으면 다시 확인하지 않습니다. 앱을 계속 실행하면 24시간마다 확인합니다. **설정 > 정보 > 새 버전 자동 확인**을 끄면 자동 확인이 중단되며, 같은 화면의 **업데이트 확인**으로 직접 확인할 수 있습니다. 개발 빌드는 자동 확인하지 않습니다.

새 버전이 있으면 라이브러리나 설정 창에 안내 카드가 표시됩니다. 카드의 **업데이트**, 설정의 **업데이트 설치**, 트레이의 **업데이트 설치 (버전)**에서 진행할 수 있습니다. GitHub 업데이트 카드를 닫으면 같은 버전은 다시 알리지 않으며, 다음 새 버전은 다시 표시합니다. Store 카드를 닫으면 현재 설치 버전에서는 다시 표시하지 않습니다.

- Store 설치본은 Microsoft Store에서 확인하고 Store 설치 화면을 통해 업데이트합니다.
- GitHub 설치형은 `api.github.com/repos/sukwoo0711-maker/my-capture/releases/latest`에서 확인합니다. 업데이트를 선택하면 `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`을 통해 현재 앱의 아키텍처에 맞는 설치 파일과 체크섬(x64: `SHA256SUMS.txt`, x86: `SHA256SUMS-win-x86.txt`)을 내려받고 SHA-256을 검증한 뒤, 진행 중인 작업을 보호하고 설치기에 넘깁니다. 설치기는 앱이 끝나기를 기다린 뒤 업데이트 진행 창을 보여 주고, 설치가 끝나면 앱을 다시 시작합니다.
- 포터블 ZIP은 같은 GitHub 주소에서 확인하며, 업데이트를 선택하면 브라우저에 릴리스 페이지를 엽니다. 새 ZIP 전체를 내려받아 압축을 풀어 사용하십시오.

`build\package.ps1`이 만드는 설치 파일과 포터블 ZIP은 GitHub 릴리스 자산이며, 서명되지 않습니다. x64 게시 폴더는 Store 패키지를 만들 때에도 사용합니다. 릴리스 절차 밖에서 만든 파일은 배포하지 마십시오.

`MyCapture-<version>-win-x64-setup.exe`와 `MyCapture-<version>-win-x86-setup.exe`는 IExpress 패키지이며, 압축 해제 후 `MyCaptureSetup.exe`를 직접 실행합니다. 설치·업데이트 설치·제거에는 PowerShell을 사용하지 않습니다. 설치기는 Windows 10 22H2와 Windows 11에 기본 포함된 .NET Framework 4.8을 사용하며, 관리자 권한을 요청하지 않습니다. 설치와 제거는 명령 창 없이 진행 막대가 있는 작은 창으로 진행하며, 설치가 끝나면 MyCapture 실행 여부를 고릅니다. 시작 메뉴와 사용자별 제거 등록을 만들고, 같은 실행 파일의 복사본인 `MyCaptureUninstall.exe /uninstall`로 제거합니다. 앱 안 업데이트에서는 현재 앱이 종료될 때까지 기다린 후 업데이트 창에 진행 상황을 표시하고, 설치와 결과 저장을 마치면 앱을 다시 엽니다. 업데이트에 실패하면 로그를 확인하거나 복원된 앱을 열 수 있습니다.

두 실행 파일은 서명되지 않았으므로 SmartScreen이 다운로드한 IExpress 파일 또는 압축 해제된 실행 파일을 경고할 수 있습니다. 배포 경로와 해당 아키텍처 체크섬 파일의 해시를 확인하고 신뢰하는 경우에만 Windows가 제공하는 **추가 정보 → 실행**을 사용합니다. 조직에서 실행을 차단한 경우 해당 조직의 허용된 설치 경로를 사용합니다. SHA-256은 파일 변경 검증이며 게시자 서명을 대신하지 않습니다.

설치 로그는 `%TEMP%\MyCapture-Install-yyyyMMdd-HHmmss-PID.log`, 제거 로그는 `%TEMP%\MyCapture-Uninstall-yyyyMMdd-HHmmss-PID.log`입니다. 종료 코드 19는 필수 Windows 구성 요소를 사용할 수 없다는 뜻이며, 설치기는 `%TEMP%\MyCapture-Setup-Help.txt`에 도움말을 복사합니다. 포터블 ZIP 또는 Microsoft Store 버전을 대안으로 사용할 수 있습니다. 이전 스크립트 시작 실패 코드 22는 사용하지 않습니다. CLR 4 자체가 손상되어 로드되지 않는 경우에는 Windows의 오류 창이 표시됩니다. 상세 계약은 [ADR 0008](adr/0008-powershell-free-installer.md)에 기록합니다.

기본 사용자 데이터 위치는 다음과 같습니다.

- Store 설치본의 설정, 인덱스, 라이브러리: 앱 전용 폴더 `%LOCALAPPDATA%\Packages\<패키지 패밀리 이름>\LocalState` (앱을 제거하면 함께 삭제)
- 개발 빌드(비패키지)의 설정, 인덱스, 로그: `%APPDATA%\MyCapture`, 라이브러리 관리 사본: `%APPDATA%\MyCapture\captures`
- 빠른 저장 PNG: `%USERPROFILE%\Pictures\Captures`
- 번역 모델(번역을 켠 경우만): `%LOCALAPPDATA%\MyCapture\translation-models`, Store 설치본은 같은 패키지 폴더의 `LocalCache\translation-models`. 설정 > 일반 > 번역의 **모델 삭제**로 지웁니다.

캡처 저장 폴더와 빠른 저장 폴더는 설정에서 변경할 수 있습니다. 4.4.0부터 새로 설치하면 라이브러리 항목에 보관 기간 제한이 없습니다. 폴더 밖 항목이 1,000개나 5 GiB를 넘을 때만 오래된 항목부터 자동 정리하며, 이 개수와 용량에는 동영상도 포함됩니다. 보관으로 표시한 항목, 폴더에 넣은 항목, 편집 중인 항목은 지우지 않습니다. 이전 버전에서 업데이트한 설치는 저장된 설정을 그대로 씁니다. 이전 기본값은 이미지 168시간(7일), 300개, 2 GiB이며, 보관 기간은 이미지에만 적용합니다. 보관 기간, 최대 항목 수, 최대 용량은 설정 > 저장 공간에서 바꿉니다. 따로 내보낸 파일은 정리 대상이 아닙니다.

4.8.3의 변경 사항은 [릴리스 노트](releases/4.8.3-release-notes.md)에서 확인할 수 있습니다. 이전 [4.8.2](releases/4.8.2-release-notes.md), [4.7.0](releases/4.7.0-release-notes.md), [4.6.0](releases/4.6.0-release-notes.md), [4.5.2](releases/4.5.2-release-notes.md), [4.5.1](releases/4.5.1-release-notes.md), [4.5.0](releases/4.5.0-release-notes.md), [4.4.0](releases/4.4.0-release-notes.md), [4.3.1](releases/4.3.1-release-notes.md), [4.3.0](releases/4.3.0-release-notes.md), [4.2.1](releases/4.2.1-release-notes.md), [4.2.0](releases/4.2.0-release-notes.md), [4.1.5](releases/4.1.5-release-notes.md), [4.1.4](releases/4.1.4-release-notes.md), [4.1.3](releases/4.1.3-release-notes.md), [4.1.2](releases/4.1.2-release-notes.md), [4.1.1](releases/4.1.1-release-notes.md), [4.1.0](releases/4.1.0-release-notes.md), [4.0.2](releases/4.0.2-release-notes.md), [4.0.1](releases/4.0.1-release-notes.md), [4.0.0](releases/4.0.0-release-notes.md), [3.4.0](releases/3.4.0-release-notes.md), [3.2.1](releases/3.2.1-release-notes.md), [3.2.0](releases/3.2.0-release-notes.md), [3.1.7](releases/3.1.7-release-notes.md), [3.1.5](releases/3.1.5-release-notes.md), [3.1.4](releases/3.1.4-release-notes.md), [3.1.3](releases/3.1.3-release-notes.md), [3.1.2](releases/3.1.2-release-notes.md), [3.1.1](releases/3.1.1-release-notes.md), [3.1.0](releases/3.1.0-release-notes.md), [3.0.0](releases/3.0.0-release-notes.md), [2.4.1](releases/2.4.1-release-notes.md), [2.4.0](releases/2.4.0-release-notes.md), [2.3.10](releases/2.3.10-release-notes.md), [2.3.9](releases/2.3.9-release-notes.md), [2.3.8](releases/2.3.8-release-notes.md), [2.3.7](releases/2.3.7-release-notes.md), [2.3.6](releases/2.3.6-release-notes.md), [2.3.5](releases/2.3.5-release-notes.md), [2.3.4](releases/2.3.4-release-notes.md), [2.3.3](releases/2.3.3-release-notes.md), [2.3.2](releases/2.3.2-release-notes.md), [2.3.1](releases/2.3.1-release-notes.md), [2.3.0](releases/2.3.0-release-notes.md), [2.2.2](releases/2.2.2-release-notes.md), [2.2.1](releases/2.2.1-release-notes.md), [2.1.1](releases/2.1.1-release-notes.md), [2.1.0](releases/2.1.0-release-notes.md), [2.0.0](releases/2.0.0-release-notes.md), [1.9.2](releases/1.9.2-release-notes.md), [1.9.0](releases/1.9.0-release-notes.md) 기록도 보존합니다.

### 1.8.3 실행 및 배포 형식 안내

- 일반 실행과 바로가기 더블클릭은 라이브러리 창을 엽니다. 로그인 자동 실행은 `--background`로 트레이에만 상주합니다.
- 설치형과 포터블 ZIP의 앱·런타임 파일은 동일합니다. 설치형은 `%LOCALAPPDATA%\Programs\MyCapture`에 설치하고 시작 메뉴, 제거 프로그램, `install-manifest.json` 소유권 기록을 추가합니다.
- 포터블은 ZIP 전체를 별도 폴더에 풀고 `MyCapture.exe`를 실행하십시오. EXE만 옮기면 DLL과 Assets가 없어 실행할 수 없습니다. 두 형식의 기본 사용자 데이터는 `%APPDATA%\MyCapture`입니다.
- 설치 전 다른 위치의 포터블/설치형을 트레이 메뉴에서 종료하십시오. 두 복사본은 한 Windows 세션에서 동시에 상주하지 않습니다.
- 설치형과 포터블의 업데이트 방법은 위 [업데이트](#업데이트)를 참고하십시오. 권장 배포 경로인 Store로 옮기려면 **설정 > 정보 > Microsoft Store에서 열기**를 사용합니다. Store 버전은 설정·라이브러리를 공유하지 않으므로 설정 내보내기·가져오기로 설정을 옮기고, Store 버전을 실행하기 전에 기존 복사본을 트레이 메뉴에서 종료하십시오. 기존 폴더와 사용자 파일은 자동 삭제하지 않습니다.
- [1.8.3 수정 내역](releases/1.8.3-release-notes.md)과 [검증 기록](releases/1.8.3-validation.md)을 확인하십시오.

## 개인정보와 네트워크

x86 호환판은 Windows 기본 OCR만 사용하며 AI OCR·로컬 번역 모델을 내려받지 않습니다. 아래 모델 내려받기와 번역 안내는 x64판에 적용됩니다.

MyCapture는 화면 픽셀, 캡처 기록, 설정, OCR 텍스트를 로컬 파일에 저장합니다. OCR은 Windows의 온디바이스 `Windows.Media.Ocr` 엔진과 PC에 내려받은 PP-OCRv5 모델로 처리하며, 현재 코드에는 캡처 자동 업로드나 제품 분석 텔레메트리가 없습니다. 인식 품질이 빠름(기본값)이나 보통이면 Windows 기본 인식 기능만 쓰므로 모델 파일을 내려받지 않습니다. 인식 품질은 빠름, 보통, 정확 중에서 선택합니다. 정확은 작은 글자, 대비가 낮은 글자와 코드를 PP-OCRv5와 Windows 인식기로 읽고 줄별로 비교합니다. 정확으로 바꾸면 PP-OCRv5 모델 파일 약 102 MB를 ModelScope(modelscope.cn)에서 내려받습니다. 정확을 사용하는 동안에는 앱이 시작할 때 빠진 파일을 확인해 받습니다. 이전에 저장한 가장 정확 설정은 정확으로 바뀝니다. 텍스트 인식 결과 창의 번역 보기는 영어를 이 PC 안에서 한국어로 옮깁니다. 번역은 기본으로 꺼져 있고, 사용자가 번역 보기에서 번역을 켜면 그때 번역 모델 약 247 MB를 이 저장소의 GitHub 릴리스(`github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`)에서 내려받아 파일마다 SHA-256을 확인합니다. 번역할 글자와 이미지는 보내지 않습니다. GitHub 이미지 첨부는 사용자가 직접 실행할 때만 연결합니다. 새 버전 자동 확인이 켜져 있으면 Store 설치본은 Microsoft Store에, GitHub 설치형과 포터블은 `api.github.com`에 연결합니다. GitHub 설치형의 업데이트 설치를 선택하면 `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`에서 설치 파일과 해시 목록을 내려받습니다. 자동 확인은 **설정 > 정보 > 새 버전 자동 확인**에서 끌 수 있습니다. 그 밖의 연결은 [개인정보처리방침](https://mimiloaf.com/mycapture/privacy/)에 정리되어 있습니다. 개인정보 가리기의 탐지 결과에는 인식 문자열을 복제하지 않고 종류·좌표만 유지하며, 추가된 가림막은 저장 전에 이동·삭제·실행 취소할 수 있습니다. 민감한 화면을 캡처했다면 빠른 저장 폴더, 캡처 큐, 클립보드와 로그의 보관 정책도 함께 확인하십시오.

보안 문제는 공개 이슈에 재현 자료를 올리지 말고 [`SECURITY.md`](../SECURITY.md)의 비공개 신고 절차를 따르십시오. 개발 참여 방법은 [`CONTRIBUTING.md`](../CONTRIBUTING.md)를 참고하십시오.

## English summary

**English summary:** MyCapture is a Windows 10 version 22H2 or later (x64 or x86; ARM64 x64 emulation requires Windows 11) screenshot, pin, local OCR, OCR-assisted privacy-redaction, unified image/video library, image editing, cross-monitor region-recording, timed-text video editing, and GIF-export app. The recommended source is the [Microsoft Store](https://apps.microsoft.com/detail/9PB33MC095VR), which signs the package and delivers updates. The same version's unsigned setup file and portable ZIP are also published on this repository's [GitHub releases](https://github.com/sukwoo0711-maker/my-capture/releases); verify manual downloads with `SHA256SUMS.txt` for x64 or `SHA256SUMS-win-x86.txt` for x86. The x86 compatibility edition for 32-bit Windows 10 version 22H2 uses Windows OCR only. AI OCR, local translation and their model downloads are available in the x64 edition. GitHub provides both architectures; the Store package is x64.

Store, GitHub setup installs and portable copies automatically check for new versions about two minutes after startup, while idle, no sooner than 20 hours after the last check and every 24 hours while running. Turn this off in **Settings > About > Check for new versions automatically**, or use **Check for updates** there to check manually. Development builds do not check automatically. The update card's **Update** button uses the Store installation flow for Store builds. For GitHub setup installs, it downloads the installer and hash list, verifies SHA-256, protects current work and starts the installer, which restarts the app after installation. Portable copies open the release page in a browser. Closing a GitHub update card dismisses that version; a newer version can show again. Closing a Store card suppresses it for the currently installed version.

Store builds connect to Microsoft Store for updates. GitHub installs and portable copies check `api.github.com/repos/sukwoo0711-maker/my-capture/releases/latest`; GitHub in-app installation downloads from `github.com`, `objects.githubusercontent.com` and `release-assets.githubusercontent.com`. Automatic network access also includes downloading missing OCR model files for the selected recognition quality (see the [privacy policy](https://mimiloaf.com/en/mycapture/privacy/)). Translation in the text recognition window is off by default; turning it on downloads the translation model (about 247 MB) once from this repository's GitHub releases and checks each file's SHA-256. Translation runs on the PC, and the text to translate is never sent. Capture, editing, recording and OCR run on the PC. Support lives in the public [support repository](https://github.com/sukwoo0711-maker/mycapture-support). Packages are self-contained and work without a preinstalled .NET runtime. The included bootstrap script can install the repository-pinned .NET 10 SDK for source builds without admin rights. Choose Korean, English, or the Windows display language in Settings; the saved choice applies on the next app start. Recording intentionally has no audio.
