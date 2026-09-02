# MyCapture

MyCapture는 캡처, 주석 편집, 화면 고정(pin), OCR, 라이브러리, 영역 녹화를 한곳에 모은 Windows용 데스크톱 도구입니다. 캡처와 인식 결과는 기본적으로 사용자 PC 안에서 처리하며, 반복 작업은 전역 단축키와 트레이 메뉴로 빠르게 이어집니다.

> 현재 지원 하한은 **Windows 11 21H2(build 22000), x64**입니다. Windows 11 ARM64에서는 x64 에뮬레이션으로 실행할 수 있습니다. Windows 10은 지원하지 않습니다.

## 주요 기능

- 모니터 경계를 하나의 가상 데스크톱처럼 넘나드는 자유 영역 캡처·녹화와 창, 전체 화면, 지연 및 스크롤 캡처
- 사각형, 화살표, 연필, 텍스트, 이미지 삽입과 실행 취소/다시 실행을 제공하는 비파괴 주석 편집
- 클립보드 이미지를 항상 위에 보이는 창으로 고정하고 확대, 투명도, 클릭 통과, 복사, OCR 수행
- 고정한 원본 이미지를 우클릭 메뉴 또는 `Ctrl+S`로 빠르게 PNG 저장하고, `Ctrl+Shift+S`로 저장 위치 선택
- Windows 로컬 OCR을 이용한 한국어·영어 혼합 텍스트, 작은 글자와 회전 이미지 인식 및 라이브러리 검색
- 로컬 OCR로 이메일·한국 전화번호·주민번호 형태·카드번호·IP·주요 비밀 키를 찾아 편집 가능한 가림막을 한 번에 추가하는 **빠른 가리기**
- 이미지와 MP4를 명확히 구분해 함께 찾고, 별도 플레이어 없이 창 안에서 영상을 재생·편집하는 통합 라이브러리
- 영역 MP4 녹화, 삭제 범위 핸들 자르기, 프레임 탐색, 시간 텍스트와 프레임 편집을 분리해 보존하는 비파괴 영상 레이어
- 최대 20초 구간을 선택한 배속의 10fps 애니메이션 GIF로 내보내며 짧은 레이어 경계도 10ms 단위로 보존
- 캡처 수와 저장 용량을 함께 제한하는 로컬 큐, 썸네일, 빠른 저장 폴더 설정
- 시스템 애니메이션 설정을 존중하는 짧고 일관된 전환과 키보드·스크린 리더 접근성

녹화는 화면 설명·편집·GIF 워크플로에 집중하며 마이크/시스템 오디오는 의도적으로 포함하지 않습니다. UI는 한국어 중심입니다. 구현 범위와 검증 기록은 [`docs/`](docs/)에서 확인할 수 있습니다.

## 기본 단축키

| 동작 | 기본값 | 비고 |
|---|---:|---|
| 영역 캡처 | `Ctrl+Shift+C` | 놓는 즉시 클립보드에 복사하고 화면에 고정한 뒤 편집기로 이동 |
| 라이브러리 열기 | `Ctrl+Shift+Z` | 이미지와 동영상을 함께 조회하고 동영상을 창 안에서 재생 |
| 영역 녹화 시작/중지 | `Ctrl+X` | 같은 키로 녹화 종료 |
| 클립보드 이미지를 화면에 고정 | `F3` | PNG를 우선 읽어 투명도 보존 |
| 모든 고정 이미지 숨기기/표시 | `Shift+F3` | 일괄 전환 |
| 편집기 빠른 저장 | `Ctrl+S` | PNG 저장 후 편집된 이미지를 클립보드에 복사 |
| 편집기 다른 이름으로 저장 | `Ctrl+Shift+S` | PNG 저장 후 편집된 이미지를 클립보드에 복사 |
| 편집기 빠른 가리기 | `Ctrl+Shift+R` | 로컬 OCR 민감정보 후보를 편집 가능한 가림막으로 추가 |
| 고정 이미지 빠른 저장 | `Ctrl+S` | 고정 창에 포커스가 있을 때 원본 PNG 저장 |
| 고정 이미지 다른 이름으로 저장 | `Ctrl+Shift+S` | 우클릭 메뉴에서도 실행 가능 |

창/전체 화면 캡처, 이전 영역 반복, 고정 창 클릭 통과 등의 전역 단축키는 설정에서 원하는 조합으로 지정할 수 있습니다. 단축키 충돌이 발생하면 기존 설정을 보존하고 적용을 취소합니다.

## 설치와 실행

릴리스용 `win-x64` 빌드는 설치 파일과 포터블 ZIP으로 생성됩니다. 두 배포 형식 모두 .NET 런타임을 포함하는 **self-contained** 빌드이므로 사용자 PC에 .NET을 따로 설치할 필요가 없고, 실행 중 인터넷 연결도 요구하지 않습니다. 공식 배포가 게시되면 저장소의 Releases에서 버전 태그와 체크섬을 함께 확인하세요.

배포 바이너리는 현재 Authenticode로 서명되지 않습니다. Windows가 “알 수 없는 게시자” 경고를 표시할 수 있으므로, 패키지와 함께 생성되는 `SHA256SUMS.txt`의 체크섬을 확인한 뒤 실행하세요. SHA-256은 파일 변조 여부를 확인하지만 게시자 신원 자체를 증명하지는 않습니다.

기본 사용자 데이터 위치는 다음과 같습니다.

- 설정, 인덱스, 로그, 캡처 큐: `%APPDATA%\MyCapture`
- 빠른 저장 PNG: `%USERPROFILE%\Pictures\Captures`

빠른 저장 폴더는 설정에서 변경할 수 있습니다. 앱 데이터와 캡처 큐의 기본 루트는
현재 UI에서 변경하지 않습니다.

## 소스에서 빌드

배포본 실행과 달리 **소스 빌드에는 .NET 10 SDK가 필요**합니다. 이 저장소의 [`global.json`](global.json)은 검증한 SDK `10.0.400`을 기준으로 하고, 같은 .NET 10 계열의 더 최신 호환 feature band로 안전하게 roll-forward합니다. 런타임만 설치된 환경은 빌드할 수 없습니다.

필수 조건:

- Windows 11
- .NET 10 SDK `10.0.400` 이상
- Windows PowerShell 5.1 이상

```powershell
# SDK가 없다면 global.json에 고정된 버전을 현재 사용자 전용 폴더에 설치
# (관리자 권한, 레지스트리, 사용자 PATH 변경 없음)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\bootstrap-sdk.ps1

# SDK, dotnet 경로와 호스트 정보를 진단
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\doctor.ps1

# Debug 빌드
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1

# Release 빌드와 전체 단위/통합 테스트
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Configuration Release -Test
```

`bootstrap-sdk.ps1`은 Microsoft의 공식 `dotnet-install.ps1`을 사용해 정확한 SDK를 `%LOCALAPPDATA%\MyCapture\dotnet-sdk`에 설치합니다. `doctor.ps1`, `build.ps1`, `package.ps1`은 이 격리 SDK와 `%LOCALAPPDATA%\Microsoft\dotnet`의 일반 사용자별 SDK를 자동으로 찾고, 빌드 실패 시 `build\logs\`에 UTF-8 진단 로그를 남깁니다.

직접 실행하려면 다음 명령을 사용합니다.

```powershell
dotnet run --project src\MyCapture.App\MyCapture.App.csproj
```

앱은 트레이에 상주하므로 디버깅을 마칠 때 트레이 메뉴의 **종료**를 선택하세요.

## 자체 진단(self-test)

출하용 실행 파일에는 실제 Windows 캡처·셸·OCR·녹화 경로를 확인하는 self-test가 포함되어 있습니다. 각 명령은 지정한 폴더에 보고서를 쓰며, WPF 실행 파일의 종료를 기다리도록 `Start-Process -Wait`를 사용해야 합니다.

```powershell
$exe = '<publish-folder>\MyCapture.exe'
$out = '<empty-output-folder>'

Start-Process -Wait -FilePath $exe -ArgumentList '--selftest-capture', $out
Start-Process -Wait -FilePath $exe -ArgumentList '--selftest-shell', $out
Start-Process -Wait -FilePath $exe -ArgumentList '--selftest-advanced', $out
Start-Process -Wait -FilePath $exe -ArgumentList '--selftest-settings', $out
Start-Process -Wait -FilePath $exe -ArgumentList '--selftest-ocr', $out
Start-Process -Wait -FilePath $exe -ArgumentList '--selftest-recording', $out
Start-Process -Wait -FilePath $exe -ArgumentList '--selftest-video-editor', $out
```

셸 self-test는 실제 전역 단축키를 등록하므로 다른 MyCapture 인스턴스와 단축키 도구를 먼저 종료하는 편이 좋습니다. OCR 결과는 설치된 Windows 언어 팩에 영향을 받을 수 있습니다.

## 개인정보와 네트워크

MyCapture는 화면 픽셀, 캡처 기록, 설정, OCR 텍스트를 로컬 파일에 저장합니다. OCR은 Windows의 온디바이스 `Windows.Media.Ocr` 엔진을 사용하며, 현재 코드에는 캡처 자동 업로드나 제품 분석 텔레메트리가 없습니다. 빠른 가리기의 탐지 결과에는 인식 문자열을 복제하지 않고 종류·좌표만 유지하며, 추가된 가림막은 저장 전에 이동·삭제·실행 취소할 수 있습니다. 민감한 화면을 캡처했다면 빠른 저장 폴더, 캡처 큐, 클립보드와 로그의 보관 정책도 함께 확인하세요.

보안 문제는 공개 이슈에 재현 자료를 올리지 말고 [`SECURITY.md`](SECURITY.md)의 비공개 신고 절차를 따라 주세요. 개발 참여 방법은 [`CONTRIBUTING.md`](CONTRIBUTING.md)를 참고하세요.

## 라이선스

MyCapture 자체 코드는 [MIT License](LICENSE)로 공개됩니다. 배포 패키지에 포함되는 .NET/WPF 런타임과 기타 구성요소는 각각의 라이선스를 따르며, 자세한 출처와 배포 고지는 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)에서 확인할 수 있습니다.

---

**English summary:** MyCapture is a Windows 11 screenshot, pin, enhanced local OCR, OCR-assisted privacy-redaction, unified image/video gallery, annotation, cross-monitor region-recording, timed-text video editing, and GIF-export app. Release binaries are self-contained and work without a preinstalled .NET runtime; the included bootstrap script can install the repository-pinned .NET 10 SDK for source builds without admin rights. The current UI is Korean-first, recording intentionally has no audio, and distributed binaries are not Authenticode-signed.
