# PMv2 testhost startup hook

기존 VSTest/xUnit을 유지하면서 **테스트 호스트 프로세스**를 WPF 초기화 전에 PMv2로 시작하는 도구입니다. Windows의 AppTests 기본 실행에서 사용합니다. WPF나 제품 프로젝트를 참조하지 않습니다. `Assembly.GetEntryAssembly()`가 `testhost`, 엔트리 파일이 `testhost.dll`, 프로세스가 `testhost.exe` 또는 `dotnet.exe`인 경우에만 동작합니다. 환경을 상속한 MyCapture, Setup, dotnet CLI 자식은 이 조건에서 제외됩니다.

`SetProcessDpiAwarenessContext` 실패나 process/thread native PMv2 검증 실패는 예외로 호스트를 종료합니다. 이미 PMv2이면 native 검증 후 그 사실을 기록합니다. 성공한 PID를 환경에 기록하고 canary는 현재 PID와 일치하는지 검사합니다. 창·디스플레이·레지스트리·설치 파일·전역 환경을 변경하지 않습니다. 테스트에서 만든 창은 `finally`에서 닫습니다.

AppTests의 비참조 ProjectReference가 hook을 함께 restore/build하고 실제 결과 DLL을 AppTests 출력 디렉터리에 복사합니다. 순수 managed hook의 기본 PlatformTarget은 AnyCPU여서 x86/x64 testhost가 같은 hook을 사용할 수 있습니다. 기본 `dotnet test`는 SDK가 확정한 `IntermediateOutputPath` 아래 runsettings를 MSBuild 작업으로 생성합니다. Python과 셸 환경 변경은 필요하지 않습니다. 구성·플랫폼·RID·SDK·hook 입력과 복사된 DLL의 SHA256을 빌드 시 기록하여 `--no-build`에서 대조하며, 없거나 달라졌으면 테스트 전에 재빌드를 요구합니다. 이는 hook의 신선도 검사이며 모든 테스트 소스의 최신 빌드 여부를 대신하지 않습니다.

동적 DLL 경로는 XML text와 MSBuild item-list escaping을 별도로 적용합니다. 기존 `DOTNET_STARTUP_HOOKS`는 보존하고 같은 복사 DLL 경로는 중복 제거하여 이 hook 뒤에 연결합니다. 명시한 `--settings` 또는 `RunSettingsFilePath`는 그대로 우선합니다. 그런 사용자 설정에는 필요한 hook·mode 환경을 직접 포함해야 하며, 기본 설정과 자동 병합하지 않습니다. 제품·Setup 테스트 프로젝트에는 이 targets를 import하지 않습니다. 프로젝트 직접 `dotnet test` 및 solution의 build 후 test/no-build가 통합 대상입니다. DLL 직접 `dotnet vstest`에는 MSBuild target이 없으므로 아래 명시 runsettings를 사용합니다.

저장소 루트의 기본 명령입니다. 빌드와 테스트는 순서대로 실행합니다.

```cmd
dotnet build tests\MyCapture.App.Tests\MyCapture.App.Tests.csproj -c Release
dotnet test tests\MyCapture.App.Tests\MyCapture.App.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TestHostDpiCanaryTests --logger "trx;LogFileName=canary.trx"
```

기본 설정은 특정 모니터 DPI를 고정하지 않습니다. Canary는 실제 monitor DPI와 HWND/WPF/composition/frame 관계를 확인합니다. 별도로 125% 장비의 전제를 고정하는 재현 명령은 다음과 같습니다.

```cmd
dotnet build build\tools\TestHostDpiStartupHook\TestHostDpiStartupHook.csproj -c Release
dotnet build tests\MyCapture.App.Tests\MyCapture.App.Tests.csproj -c Release
python build\tools\TestHostDpiStartupHook\create-runsettings.py --output artifacts\validation-quality-implementation\testhost-pmv2.runsettings --expected-monitor-dpi 120
dotnet test tests\MyCapture.App.Tests\MyCapture.App.Tests.csproj -c Release --no-build --no-restore --settings artifacts\validation-quality-implementation\testhost-pmv2.runsettings --filter FullyQualifiedName~TestHostDpiCanaryTests --logger "trx;LogFileName=canary.trx" --logger "console;verbosity=normal" --results-directory artifacts\validation-quality-implementation\testhost-pmv2-canary
```

생성기는 빌드된 DLL의 절대 경로를 `.runsettings`에 기록합니다. 환경변수는 RunConfiguration/EnvironmentVariables를 통해 **시작할 testhost에만** 전달되므로 셸에 `DOTNET_STARTUP_HOOKS`를 설정하지 않습니다. 파일은 저장소 `artifacts` 아래 새 경로에만 생성하며 기존 파일을 덮어쓰거나 삭제하지 않습니다. 다른 빌드 경로라면 `--hook <absolute-dll-path>`를 지정합니다.

Canary 1개는 같은 PID/STA에서 native 창 → synthetic 96 DPI 창 → native 창 순으로 검사합니다. 96 DPI 창이 표시된 뒤 TextBox를 붙이고 template과 TextBoxLineDrawingVisual의 존재, 폭 축소에 따른 실제 줄 수 증가, 추가 텍스트 뒤의 모든 visual DPI 정합을 확인합니다. native 창은 primary monitor DPI, HWND/WPF/composition matrix, 전체 visual, 실제 HWND frame과 `AdjustWindowRectExForDpi` 계산을 엄격히 비교합니다. `MYCAPTURE_TESTHOST_DPI_EXPECTED_MONITOR`가 있으면 실제 monitor와의 exact 비교를 추가합니다. 120은 이 검증 PC의 125% 선행조건이며 기본 CI 설정에는 넣지 않습니다.

PASS 조건은 startup hook의 `VERIFIED` 기록, canary **1 passed / 0 failed / 0 skipped**, exit 0 및 TRX 확인입니다. 기본 설정이 적용되는 실행에서는 canary도 실행됩니다. 명시 설정 등으로 mode를 제공하지 않는 실행에서는 기존 opt-in attribute에 따라 skip되므로 PMv2 검증 성공으로 취급하지 않습니다. 기존의 calibrated canary 실행과 기본 MSBuild 통합 검증은 서로 다른 실행 근거로 기록합니다.

기본 통합의 확인 항목은 fresh restore/build 후 일반 test, 동일 구성의 no-build 반복, 명시 runsettings 우선, x86/RID 및 artifacts 경로, 공백·`&` 경로, hook DLL 또는 입력 변경 시 no-build 오류입니다. SDK 기본 작업은 소유한 generated runsettings/fingerprint만 덮어씁니다. 재현용 Python 생성기는 계속 artifacts의 새 파일만 생성하며 기존 파일을 보존합니다. 별도 삭제·Clean target은 추가하지 않습니다.

설계 근거: [.NET startup hook](https://github.com/dotnet/runtime/blob/v10.0.11/docs/design/features/host-startup-hook.md), [CoreCLR의 entry assembly 설정 후 hook 호출 순서](https://github.com/dotnet/runtime/blob/v10.0.11/src/coreclr/vm/assembly.cpp), [VSTest host 환경변수](https://learn.microsoft.com/en-us/visualstudio/test/configure-unit-tests-by-using-a-dot-runsettings-file#specify-environment-variables-in-the-runsettings-file).
