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
powershell.exe -NoProfile -File build\bootstrap-sdk.ps1

# SDK, dotnet 경로와 호스트 정보를 진단
powershell.exe -NoProfile -File build\doctor.ps1

# Debug 빌드
powershell.exe -NoProfile -File build\build.ps1

# Release 빌드와 전체 단위/통합 테스트
powershell.exe -NoProfile -File build\build.ps1 -Configuration Release -Test
```

`bootstrap-sdk.ps1`은 Microsoft의 공식 `dotnet-install.ps1`을 사용해 정확한 SDK를 `%LOCALAPPDATA%\MyCapture\dotnet-sdk`에 설치합니다. `doctor.ps1`, `build.ps1`, `package.ps1`은 이 격리 SDK와 `%LOCALAPPDATA%\Microsoft\dotnet`의 일반 사용자별 SDK를 자동으로 찾고, 빌드 실패 시 `build\logs\`에 UTF-8 진단 로그를 남깁니다.

Windows PowerShell의 실행 정책으로 `.ps1` 실행이 차단된 환경에서는 정책을 변경하지 않고 다음 진입점을 사용할 수 있습니다. 같은 `global.json`의 SDK 선택과 전체 솔루션 빌드·테스트를 사용하며, 현재 셸의 PATH와 실행 정책을 변경하지 않습니다.

```cmd
build\build.cmd -Doctor
build\build.cmd -Configuration Release -Test
```

`-NoRestore`는 이미 복원한 의존성으로 빌드할 때 사용합니다. 로그는 기존 `build\logs\build.log`, `test.log`와 SDK 확인용 `sdk-cmd.log`에 저장됩니다.

전체 테스트에는 설치 테스트가 포함됩니다. 기존 MyCapture가 실행 중이면 설치 보호 검사에서 종료 코드 `15`가 발생하므로, 작업을 저장하고 앱을 종료한 상태에서 실행합니다.

직접 실행하려면 다음 명령을 사용합니다.

```powershell
dotnet run --project src\MyCapture.App\MyCapture.App.csproj
```

앱은 트레이에 상주하므로 디버깅을 마칠 때 트레이 메뉴의 **종료**를 선택하십시오.

## 실제 OCR 모델 검증

`NeuralOcrEngineLiveTests`는 `MYCAPTURE_OCR_LIVE_MODELS`를 지정한 경우 실제 모델을 격리된 테스트 프로필에 복사하고 크기·SHA-256을 확인한 뒤 실행합니다. 모델을 지정하지 않은 일반 테스트에서는 다섯 검사가 건너뜀으로 기록됩니다. 필수 검증 모드(`MYCAPTURE_REQUIRE_OCR_LIVE=1`)에서 모델이 없거나 손상되면 실패합니다.

Release 빌드 후 Python 3.9 이상에서 다음 명령을 실행합니다. 출력 경로는 새 디렉터리여야 합니다.

```powershell
python build/validate-ocr-live.py --dotnet '<SDK>/dotnet.exe' --models '<catalog-model-folder>' --output artifacts/validation/ocr-live-01
```

이 검증기는 다섯 검사 모두의 실제 통과·모델 해시·TRX를 확인하고, 실행 명령과 소스 상태 및 실행한 바이너리 해시를 `validation.json`에 기록합니다. 모델을 다운로드하지 않으며 기존 모델과 결과를 삭제하지 않습니다. OCR 성능·300건 품질 평가는 별도 실행 항목입니다.

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

정식 패키지는 깨끗한 커밋에서 `build/package.ps1 -Version 5.3.0 -Architecture x64` 다음 `build/package.ps1 -Version 5.3.0 -Architecture x86` 순서로 생성합니다. x86 산출물은 `artifacts/release/5.3.0/win-x86`에 저장됩니다. 두 아키텍처를 함께 게시·검증할 때에는 `build/publish-github-release.ps1`과 `build/verify-github-release.ps1`에 각각 `-IncludeX86` 옵션을 전달합니다. x86 체크섬 파일 이름은 `SHA256SUMS-win-x86.txt`입니다. 이미 있는 산출물은 자동으로 지우지 않으므로 재생성 전에 보존하거나 휴지통으로 이동합니다.

## Windows ARM64 네이티브판

`win-arm64` 프로필은 ARM64 PC에서 에뮬레이션 없이 실행하는 판을 만듭니다. AI OCR과 로컬 번역을 포함하며 ONNX Runtime, SkiaSharp, WebView2 로더는 각 패키지의 `win-arm64` 네이티브 파일을 씁니다. 지원 하한은 ARM64 PC에서 x64판을 실행할 때와 같은 Windows 11(build 22000)입니다.

x64 PC에서 교차 빌드합니다. ReadyToRun 코드도 x64 PC에서 arm64용으로 컴파일합니다.

```powershell
dotnet publish src/MyCapture.App/MyCapture.App.csproj -c Release -r win-arm64 --self-contained true -p:Platform=arm64 -p:PublishProfile=win-arm64-self-contained -o '<새 arm64 출력 폴더>'
```

x64 PC는 arm64 실행 파일을 시작할 수 없으므로 self-test와 패키지 자체 검사는 ARM64 PC와 CI의 `arm64-native` 작업에서 실행합니다. x64 PC에서는 `build/publish-architecture.ps1`의 `Assert-PublishArchitecture`가 게시 폴더의 모든 PE 파일 헤더를 읽어, 네이티브 파일과 ReadyToRun 이미지가 대상 아키텍처용인지 확인합니다. `build/package.ps1`은 세 아키텍처 모두에서 ZIP을 만들기 전에 이 검사를 실행합니다.

```powershell
. .\build\publish-architecture.ps1
Assert-PublishArchitecture -PublishRoot '<arm64 출력 폴더>' -Architecture arm64
```

정식 패키지는 깨끗한 커밋에서 x64 패키지를 만든 다음 `build/package.ps1 -Version <버전> -Architecture arm64`로 만들며 산출물은 `artifacts/release/<버전>/win-arm64`에 저장됩니다. x86 패키지와의 순서는 상관없습니다. 파일 이름은 `MyCapture-<버전>-win-arm64-setup.exe`, `MyCapture-<버전>-win-arm64-portable.zip`, `installer-manifest-win-arm64.json`, `release-manifest-win-arm64.json`, `SHA256SUMS-win-arm64.txt`, `README-OFFLINE-win-arm64.txt`입니다. arm64 자산을 함께 게시·검증할 때에는 `build/publish-github-release.ps1`과 `build/verify-github-release.ps1`에 `-IncludeArm64`를 전달합니다.

- 설치기 `MyCaptureSetup.exe`는 arm64 패키지에서도 x64 .NET Framework 4.8 프로그램입니다. ARM64 네이티브 프로세스에는 .NET Framework 4.8.1이 필요하기 때문입니다. Windows 11 on ARM은 x64 패키지의 설치기와 같은 방식으로 이를 에뮬레이션으로 실행합니다. 설치기는 arm64 패키지를 ARM64 PC에만 설치하고, 다른 PC에서는 종료 코드 11로 끝납니다.
- x64 PC에서 패키징하면 IExpress 추출기도 x64이며, ARM64 Windows 11이 에뮬레이션으로 실행합니다.
- 앱 안 업데이트는 실행 중인 프로세스의 아키텍처로 자산을 고릅니다. ARM64 PC에서 x64판을 쓰는 설치는 계속 `win-x64` 설치 파일을 받습니다. arm64판은 `win-arm64` 설치 파일과 `SHA256SUMS-win-arm64.txt`만 받으며, 이 자산이 없는 릴리스에서는 업데이트를 제안하지 않습니다.
- Microsoft Store 패키지는 x64입니다. `build/package-store.ps1`은 arm64 패키지를 만들지 않습니다.

플랫폼을 지정하지 않으면 모든 빌드 PC에서 x64로 빌드합니다. ARM64 PC에서 테스트를 네이티브로 실행할 때는 `-p:Platform=arm64`를 붙입니다. 설치기와 설치기 테스트는 이 플랫폼에서도 x64로 빌드합니다.

```powershell
dotnet test tests/MyCapture.Core.Tests/MyCapture.Core.Tests.csproj -c Release -p:Platform=arm64
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Release -p:Platform=arm64 --filter "FullyQualifiedName!~GitHubUpdateServiceTests.DownloadedSessionPassesActualInstallerPathGuard"
dotnet test tests/MyCapture.Setup.Tests/MyCapture.Setup.Tests.csproj -c Release
```

제외한 테스트는 x64 설치기 어셈블리를 Windows 기본 PowerShell에 올립니다. ARM64 Windows의 기본 PowerShell은 ARM64 프로세스라 x64 어셈블리를 올릴 수 없습니다. CI의 `arm64-native` 작업(`windows-11-arm` 러너)은 위 세 명령, arm64 publish와 아키텍처 검사, 패키지 self-test 8종을 실행합니다.

## 릴리스 크기와 Windows API 보존

세 self-contained Release publish 프로필은 `MyCaptureTrimWindowsProjection=true`로 `Microsoft.Windows.SDK.NET.dll`만 링크합니다. 애플리케이션·.NET·WPF·`WinRT.Runtime.dll`의 컴파일과 ReadyToRun 설정은 유지합니다. 개발 빌드와 테스트 빌드에는 적용하지 않습니다. Store·패키지 식별·시작 작업 및 COM 경계에서 사용하는 형식은 모든 멤버를 보존합니다. 보존 목록은 `build/WindowsProjectionRoots.xml`입니다.

ILLink 버전과 패키지 해시는 `build/WindowsProjectionTrim.targets`에 고정합니다. 애플리케이션의 PackageReference나 trim analyzer를 켜지 않습니다.

고정 버전은 `global.json`이 고르는 SDK의 런타임 버전과 같게 둡니다(SDK 10.0.400의 런타임은 10.0.11). 링커는 publish가 SDK의 `dotnet`으로 시작하는 도구이고, 자기 패치보다 낮은 런타임에서는 시작되지 않기 때문입니다. `build/package.ps1`은 사용자별 SDK를 PATH 맨 앞에 두므로, 고정 버전이 그 SDK의 런타임보다 새로우면 패키징이 멈춥니다. 이 경우 publish는 이유를 적은 오류를 냅니다. `global.json`의 SDK를 올릴 때 고정 버전과 해시도 함께 올립니다(계약 시험이 둘을 한 쌍으로 확인합니다).

- 프로필로 직접 publish할 때는 준비 단계가 없습니다. 복원이 고정한 버전의 ILLink 패키지를 NuGet 캐시에 내려받고(참조하지 않고 받기만 하는 `PackageDownload`), publish는 NuGet이 그 패키지 옆에 적어 둔 SHA-512가 고정값과 같을 때만 링커를 실행합니다. 패키지가 fallback 폴더에 이미 있으면 복원은 그것을 그대로 두므로, publish는 NuGet 패키지 폴더를 모두 찾아봅니다. `--no-restore`로 publish하려면 같은 속성으로 먼저 복원해 둡니다. CI의 x86·arm64 publish가 이 경로입니다.
- `build/package.ps1`은 `build/prepare-projection-linker.ps1`으로 도구를 준비합니다. 캐시의 패키지는 NuGet이 적어 둔 SHA-512가 고정값과 같을 때만 씁니다. 캐시에 없거나 값이 다르면 고정 패키지를 임시 폴더에 받아 SHA-512를 검증하고 `MYCAPTURE_PROJECTION_LINKER_PATH`로 넘깁니다. 이 변수가 있으면 복원은 패키지를 받지 않습니다.

```powershell
dotnet publish src/MyCapture.App/MyCapture.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishProfile=win-x64-self-contained -o '<새 x64 출력 폴더>'
dotnet publish src/MyCapture.App/MyCapture.App.csproj -c Release -r win-x86 --self-contained true -p:Platform=x86 -p:PublishProfile=win-x86-self-contained -o '<새 x86 출력 폴더>'
dotnet publish src/MyCapture.App/MyCapture.App.csproj -c Release -r win-arm64 --self-contained true -p:Platform=arm64 -p:PublishProfile=win-arm64-self-contained -o '<새 arm64 출력 폴더>'
```

진단용 원본 SDK와 비교하려면 같은 명령에 **`-p:MyCaptureTrimWindowsProjection=false`**를 추가하고 별도의 새 출력 폴더를 사용합니다. 이 진단 publish는 SDK 크기 제한을 적용하지 않지만 정식 패키징은 제한을 다시 검사하므로 원본 SDK를 실수로 출하할 수 없습니다. 출력 파일을 덮어 섞지 마십시오.

```cmd
build\tools\size-report.cmd "<원본 publish>" "<축소 publish>" --verify-only-projection --json
```

설치 파일 합계, portable ZIP 및 SDK DLL의 바이트 예산은 **`build/ReleaseSizeBudgets.props` 한 곳**에 있습니다. publish는 설치 파일 합계와 SDK DLL을 검사하고, 패키징은 법적 고지 파일을 포함한 설치 파일 합계를 다시 검사한 뒤 실제 생성한 ZIP도 검사합니다. Store 패키징도 설치 파일과 SDK 제한을 검사합니다. 초과 시 실패하며, 의도한 증가는 파일별 크기 보고서와 실제 ZIP 측정값을 검토한 다음 해당 아키텍처의 예산을 수정합니다.

패키지 자체 검사는 전달받은 publish의 SDK를 확인해 축소본에 WinRT 검사와 엄격한 실제 WGC 프레임 검사를 적용합니다. 원본 여부를 확인할 수 없어도 이 검사를 적용합니다. 기존 8종 검사·시간 제한·보고서는 그대로 유지합니다. `packaged-validation.yml`의 수동 실행에는 새 입력이 필요하지 않습니다.

## 디버거 지원 파일과 고객 덤프 분석

모든 아키텍처의 Release publish에는 다음 디버거 지원 파일을 포함하지 않습니다. CLR 실행에 필요한 `coreclr.dll`, `hostfxr.dll`, `hostpolicy.dll` 등은 유지합니다.

- `mscordaccore.dll`: 디버거가 CLR 내부 자료를 읽는 DAC(Data Access Component).
- `mscordaccore_<아키텍처>_<아키텍처>_<런타임 빌드>.dll`: 같은 DAC의 버전 식별 이름.
- `mscordbi.dll`: 관리 코드 디버깅 지원 인터페이스.
- `Microsoft.DiaSymReader.Native.amd64.dll`, `.x86.dll` 또는 `.arm64.dll`: 디버거의 네이티브 심볼 판독 지원.

앱 실행·OCR·녹화에는 필요하지 않지만, 고객 PC에서 앱 폴더만으로 관리 코드 디버깅이나 덤프 분석을 수행할 수는 없습니다. 덤프 파일 생성 자체는 유지됩니다. 분석 담당자는 덤프와 정확한 배포 버전·아키텍처·`coreclr.dll` 파일 버전 및 모듈 식별 정보를 함께 보존합니다. 분석 PC에서 WinDbg/SOS 등 대상 아키텍처를 지원하는 도구와 Microsoft 심볼 서버(`https://msdl.microsoft.com/download/symbols`)를 사용하고, **덤프의 CLR 런타임 빌드와 정확히 일치하는 DAC**를 내려받습니다. 제품 버전이나 .NET 주 버전만 같은 DAC를 대신 사용하지 않습니다. 분석 도구의 심볼 캐시를 통해 DAC를 로드하고, SOS에서 CLR 버전과 DAC 일치 여부를 확인한 뒤 스택·힙을 분석합니다. 분석 PC가 오프라인이면 연결된 분석 PC에서 해당 DAC·심볼을 미리 확보해 전달합니다. 디버거와 심볼은 제품 설치 폴더에 재배포하지 않습니다.
