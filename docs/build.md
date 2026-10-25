# MyCapture 소스에서 빌드

이 문서는 4.3.1까지 README에 있던 소스 빌드와 자체 진단 안내를 옮긴 것입니다. 기여 절차는 [CONTRIBUTING.md](../CONTRIBUTING.md)에 있습니다.

## 소스에서 빌드

배포본 실행과 달리 **소스 빌드에는 .NET 10 SDK가 필요**합니다. 이 저장소의 [`global.json`](../global.json)은 검증한 SDK `10.0.400`을 기준으로 하고, 같은 .NET 10 계열의 더 최신 호환 feature band로 안전하게 roll-forward합니다. 런타임만 설치된 환경은 빌드할 수 없습니다.

필수 조건:

- Windows 10 버전 22H2(build 19045) 이상, x64. ARM64 x64 에뮬레이션은 Windows 11(build 22000) 이상
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

앱은 트레이에 상주하므로 디버깅을 마칠 때 트레이 메뉴의 **종료**를 선택하십시오.

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

녹화 self-test는 설정과 관계없이 자동 방식(Windows Graphics Capture 우선)으로 녹화하며, 환경 변수 `MYCAPTURE_RECORDING_BACKEND`를 `gdi` 또는 `wgc`로 지정하면 그 방식으로 실행합니다. 셸 self-test는 실제 전역 단축키를 등록하므로 다른 MyCapture 인스턴스와 단축키 도구를 먼저 종료하는 편이 좋습니다. OCR 결과는 설치된 Windows 언어 팩에 영향을 받을 수 있습니다.

## Windows 10 32비트 호환판

Windows 10 22H2(build 19045) 이상에서 사용할 x86판은 Windows 기본 OCR을 사용하며 AI OCR·로컬 번역 모델을 내려받지 않습니다. x64판의 기존 Windows 10 지원과 AI 기능은 유지합니다.

```powershell
dotnet publish src/MyCapture.App/MyCapture.App.csproj -c Release -r win-x86 -p:Platform=x86 -p:PublishProfile=win-x86-self-contained --self-contained true -o artifacts/local-win-x86
```

정식 패키지는 깨끗한 커밋에서 `build/package.ps1 -Version 4.8.2 -Architecture x64` 다음 `build/package.ps1 -Version 4.8.2 -Architecture x86` 순서로 생성합니다. x86 산출물은 `artifacts/release/4.8.2/win-x86`에 저장됩니다. 두 아키텍처를 함께 게시·검증할 때에는 해당 스크립트의 `-IncludeX86` 옵션을 사용합니다. x86 체크섬 파일 이름은 `SHA256SUMS-win-x86.txt`입니다. 이미 있는 산출물은 자동으로 지우지 않으므로 재생성 전에 보존하거나 휴지통으로 이동합니다.
