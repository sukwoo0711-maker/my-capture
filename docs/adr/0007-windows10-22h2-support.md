# ADR 0007 — Windows 10 22H2 지원 복원

상태: 채택 — Windows 10 실기 검증 대기
기준: MyCapture 4.2.0, `main@142cf79`
관련: [ADR 0004](0004-video-editor-responsive-preview.md), [ADR 0006](0006-wgc-recording-default.md)

## 배경

0.9.0은 Windows 10을 지원했습니다. 1.0.0(`d66549b`)에서 지원 하한을 Windows 11 21H2로 올렸지만,
현재 캡처·편집·OCR의 핵심 경로는 Windows 10 22H2에서 제공하는 API로 동작할 수 있습니다.
Windows 11 전용 테두리 제거 API와 창 장식을 선택 기능으로 분리하여 기존 x64 장비의 사용을 복원합니다.

Windows 10의 일반 지원은 2025-10-14에 종료됐으며 소비자 ESU도 2026-10-13에 종료됐습니다.
따라서 Windows 10 지원은 최선의 노력(best effort)과 실제 장비·VM 검증을 기준으로 합니다.
코드·빌드 검증만으로 실기 검증을 대신하지 않으며 OS 보안 지원을 제공하는 결정도 아닙니다.

## 결정

| 항목 | 결정 |
|---|---|
| x64 호스트 하한 | Windows 10 버전 22H2, build 19045. 더 낮은 빌드는 실행 게이트와 설치 preflight에서 거부합니다. |
| ARM64 | x64 에뮬레이션은 Windows 11 build 22000 이상으로 유지합니다. Windows 10 on ARM은 x64 앱을 실행할 수 없습니다. |
| 단일 출처 | `WindowsSupportPolicy`에 호스트·ARM64 하한을 두고 설치·패키지·Store 계약 테스트로 일치시킵니다. |
| API 계약 / TFM | `SupportedOSPlatformVersion=10.0.19041.0`은 22H2까지 공유하는 API 계약입니다. `net10.0-windows10.0.22000.0` TFM은 선택적 WinRT 멤버 컴파일을 위해 유지합니다. CA1416 전역 억제를 제거합니다. |
| Store | `MinVersion=10.0.19045.0`. `MaxVersionTested`는 상위 검증 버전을 유지하며 MinVersion보다 낮은 값은 거부합니다. |
| WGC | `ApiInformation`으로 border·access·cursor 멤버를 먼저 검사하고 결과를 캐시합니다. API가 없으면 호출하지 않으며 `InvalidCastException`도 방어합니다. Windows 10에서는 노란 테두리를 허용합니다. |
| 창 장식 | 지원 호스트에서 어두운 제목 표시줄(속성 20)을 적용합니다. 22000부터 모서리·caption/border/text 색상을 적용하고, 22621부터 system backdrop 속성을 적용합니다. Windows 10의 핀 외곽선·이미지 clip·알림 모서리는 0 DIP입니다. |
| Glass 테마 | `Surface.Base`를 WPF 배경 위에 그립니다. 시스템 Mica에 의존하지 않으며 지원되는 호스트에서도 backdrop을 none으로 유지합니다. |
| Print Screen | 레지스트리 값이 없으면 19045에서 꺼짐, 22621부터 켜짐으로 판단합니다. 안내 문구의 설정 경로는 빌드로 고릅니다. 영어는 Windows 10에서 `Ease of Access > Keyboard`, Windows 11에서 `Accessibility > Keyboard`이고, 한국어는 두 OS 모두 `접근성 > 키보드`입니다. Windows 10의 옵션 이름은 "PrtScn"을 쓰므로 설정 검색 안내는 쓰지 않습니다. |

Windows 10에서는 WPF의 픽셀별 알파 레이어드 창(`AllowsTransparency=true`)에
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`를 적용하면 오류 8이 반환됩니다.
[Microsoft의 제한 설명](https://learn.microsoft.com/en-us/archive/msdn-technet-forums/7ce400f0-ebda-4b95-869c-85b5b93f972d)에 따라
빌드 번호 대신 UI 스레드에서 화면 밖의 숨겨진 1×1 레이어드 창을 만들어 실제 API 성공 여부를 한 번 검사하고 캐시합니다.
미지원 환경에서는 녹화 제어창을 불투명 창으로 만들고, 물리 픽셀 기준 프레임 링과 팔레트의 합집합을
`SetWindowRgn`으로 적용하여 내부 클릭 통과를 유지합니다. 카운트다운·셸 알림·빠른 작업창·핀 도구막대도
불투명 배경과 사각 모서리를 사용하며 그림자 여백만 제거합니다. 녹화 옵션·핀 투명도 메뉴는 HWND 생성 전에
팝업 투명도를 끄고 별도로 캡처 제외를 적용합니다. 지원 환경의 레이어드 표시와 녹화에 포함되는 시각 표시는 유지합니다.

## 나머지 의존성

- WGC `CreateFreeThreaded`는 17763, `CreateForMonitor` interop은 18362, 커서 속성은 19041부터입니다.
  `MinUpdateInterval`, `IncludeSecondaryWindows`, `DirtyRegionMode`, WindowId/DisplayId 생성 API는 사용하지 않습니다.
- `WDA_EXCLUDEFROMCAPTURE`는 19041부터입니다. 고해상도 waitable timer는 17134부터이며 기존 sleep 폴백을 유지합니다.
- `StartupTask`, `AppInstance` 활성화, `ApplicationData`, Store 업데이트·평가, Windows OCR·이미지·언어 API는
  19041 이하에서 제공됩니다. 패키지 ID·기능 가용성·실패 처리는 기존 경로를 유지합니다.
- 녹화 출력은 Media Foundation H.264, BGRA/NV12 SDR입니다. HEVC·AV1·HDR 인코더를 필수로 요청하지 않습니다.
  하드웨어 디코더·인코더가 없으면 기존 소프트웨어 경로를 사용합니다. Windows N의 Media Feature Pack과 HDR 색상은 실기 확인 대상입니다.
- ONNX Runtime의 CPU 세션과 RapidOcrNet의 PP-OCR CPU 경로를 사용하며 DirectML을 필수로 요청하지 않습니다.
  모델·CPU·네이티브 라이브러리 가용성은 실제 장비에서 확인합니다.
- UI 글꼴의 Segoe UI Variable/Cascadia 선택에는 Segoe UI/Consolas/Malgun Gothic 폴백이 있습니다.
  제품 아이콘은 WPF 경로 도형이며 Fluent 전용 글리프에 의존하지 않습니다.
- Explorer 폴더 판정은 탭이 없는 단일 shell view도 처리합니다. Windows 11 컨텍스트 메뉴나 snap layout 전용 API는 사용하지 않습니다.
  설정 URI `ms-settings:easeofaccess-keyboard`는 두 OS에서 유효합니다. 테마·accent는 앱 팔레트와 WPF의
  `SystemColors`·`SystemParameters` 고대비 상태를 사용하며 Windows 11 전용 레지스트리 값에 의존하지 않습니다.
- MSIX `uap10:RuntimeBehavior`/`TrustLevel`은 19041에서 지원합니다. 이후 `uap11`은 IgnorableNamespaces에 포함되어
  Windows 10에서 무시됩니다. 서명 없는 `-LocalTest`/`-AllowUnsigned` 설치는 Windows 11 검증용으로 유지합니다.

## 영상 미리보기 색상 범위

`StudioRangeVideoEffect`는 WPF `MediaElement`가 스튜디오 범위(16–235) 영상을 스튜디오 범위 RGB로
넘긴다고 보고 셰이더로 넓혔습니다. Windows 10 실기(Intel HD 630/GTX 1050)에서는 재생기가 이미 전체 범위로
넓혀 셰이더가 한 번 더 적용되었고, 원본 회색 16이 0으로 표시되었습니다. 이 동작은 OS보다 GPU 드라이버의
비디오 처리에 달려 있으므로 빌드나 제조사로 분기하지 않습니다. 첫 스튜디오 범위 미리보기에서 녹화와 같은
인코더로 1초짜리 보정 영상을 만들고, 별도 UI 스레드의 화면 밖 `MediaElement` 출력을 읽어 셰이더 필요 여부를
프로세스마다 한 번 판정합니다. 측정은 5초로 제한하며, 실패하거나 시간을 넘기면 기존 정책을 유지합니다.
보정 영상은 측정 뒤 지웁니다.

## 노란 테두리 검증

기존 `--selftest-recording-backends`의 테두리 프로브는 GDI로 읽은 화면 가장자리만 관찰했습니다.
전용 `--selftest-wgc-border`는 주 모니터를 자체 단색 창과 움직이는 표식으로 덮고 `requestBorderless: false`로
전체 모니터를 40프레임(20fps, 2초) 녹화합니다. 원본 BGRA 프레임과 디코드한 MP4 40프레임의 바깥 10픽셀을 모두 검사합니다.
노란 픽셀이 0이고 가장자리가 fixture 색상과 일치하며, 원본과 디코드 프레임 모두에서 중앙 표식이 바뀌어야 통과합니다. 빈 화면·프레임 정지·검은 디코드·가려진 창은 실패로 처리합니다. 진단 전체에 30초 제한이 있으며, 초과하면 fixture 창을 닫고 실패를 기록합니다.
Windows 11에서는 `IsBorderRequired=true` 읽기 결과도 확인합니다. MP4와 JSON을 보존합니다.

일반 데스크톱 세션에서 저장소 루트를 기준으로 아래 명령을 실행합니다. Windows 10 VM에서는 ``를 설치된 `MyCapture.exe` 경로로 바꿉니다.

```powershell
$repo = (git rev-parse --show-toplevel)
$run = Join-Path $env:TEMP ('MyCapture-wgc-border-' + [guid]::NewGuid().ToString('N'))
$env:MYCAPTURE_DATA_ROOT = Join-Path $run 'data'
$exe = Join-Path $repo 'src\MyCapture.App\bin\Release\net10.0-windows10.0.22000.0\MyCapture.exe'
$output = Join-Path $run 'border'
& $exe --selftest-wgc-border $output | Out-Null
$LASTEXITCODE
Get-Content -LiteralPath (Join-Path $output 'wgc-border-report.txt')
Get-Content -LiteralPath (Join-Path $output 'wgc-border.json')
```

기대 결과는 exit code 0, `RESULT: PASS`, `RawYellowPixels=0`, `DecodedYellowPixels=0`,
`UnexpectedEdgePixels=0`, `FramesCopied >= 2`이고 원본·디코드 프레임 검사가 모두 유효해야 합니다. 녹화 중 노란 테두리가 실제 화면에 보이는지는 육안으로도 확인합니다.
다른 앱의 최상위 창을 닫고 SDR에서 먼저 검증한 뒤 HDR·혼합 DPI를 추가합니다. 기존 MyCapture 프로세스는 종료하지 않습니다.
Store 서명본에서도 같은 스위치로 검증합니다. Windows 10의 진단에는 border API가 `unavailable (pre-Windows 11)`로 표시됩니다.

## 실기 수용 조건

Windows 10 22H2 x64에서 Store 설치·업데이트와 zip 실행, 기본 WGC 녹화와 GDI 폴백, 커서 포함·제외,
캡처 제외 창, 영역 모드 녹화 제어창의 작은 영역·전체 화면 녹화 시작과 프레임 내부 클릭 통과,
팔레트 위아래 전환·녹화 시작·DPI 변경 후 영역 갱신, 녹화 중 카운트다운·알림·메뉴의 프레임 제외,
모든 테마의 제목 표시줄·핀 모서리, 100/150% DPI, PrtSc 설정 열기, Explorer 경로,
Windows OCR·향상 OCR, MP4/GIF 편집, 스튜디오 범위 녹화의 갤러리·편집기 미리보기 색상(보정 판정 포함)을 확인합니다. Windows 11에서는 테두리 제거·둥근 모서리·Store 동작 회귀를 확인합니다.
실기 확인 전에는 Windows 10에서 노란 테두리의 프레임 제외와 각 GPU/코덱 조합을 검증 대기 상태로 유지합니다.
