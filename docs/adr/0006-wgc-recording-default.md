# ADR 0006 — 단일 모니터 영역 녹화의 기본 캡처를 Windows Graphics Capture로 전환

상태: 채택(사용자 실기 검증 전). 실기 검증에서 아래 중단 조건이 확인되면 기본값을 GDI로 되돌린다.
기준: `e45f458`(`origin/main`, #67·#68·#69 병합 후), 브랜치 `feat/wgc-recording`
관련: [ADR 0005](0005-wgc-recording-spike.md)(WGC 스파이크 측정과 조건부 진행 권고),
[ADR 0002](0002-region-recording.md)(GDI BitBlt 채택)

## 배경

ADR 0005는 WGC가 단일 모니터 영역에서 캡처 시간과 60fps 고해상도 드롭률을 크게 줄이지만, 기본 타이머 해상도에서는
녹화 루프 대기(`Thread.Sleep`)가 30fps를 21.4fps로 묶고 정지 장면 60fps에서 WGC가 32.0fps로 GDI보다 낮다는 점을
들어 "고해상도 대기 타이머 없이 WGC를 기본값으로 바꾸지 않는다"고 권고했다. 그 전제였던 녹화 루프 대기는 #68에서
`RecordingFrameDelay`(`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` 대기 타이머, 실패 시 기존 대기)로 바뀌었다.
이 ADR은 같은 PC에서 새 루프로 다시 잰 값과 함께 기본값 전환을 기록한다.

## 결정

| 항목 | 내용 |
|---|---|
| 기본값 | 녹화 영역이 디스플레이 하나 안에 있고 OS가 WGC를 지원하면 WGC로 녹화한다. |
| 폴백 | 여러 모니터에 걸친 영역, WGC 미지원, 장치·항목 생성 실패, 세션 열기 5초 초과, 녹화 중 `Map` 실패(장치 제거 포함)·디스플레이 제거·해상도 변경으로 영역이 화면을 벗어남. 모두 그 녹화의 나머지를 GDI로 이어 간다(ADR 0005 폴백 표와 같다). |
| 정지 스크린샷 | GDI 그대로(ADR 0005 위험 4). `RegionFrameGrabber`만 WGC를 쓰고 `ScreenCaptureEngine` 정지 캡처 경로는 바뀌지 않는다. |
| 사용자 설정 | 설정 > 녹화 > "녹화 캡처 방식": "자동(Windows Graphics Capture 우선)"(기본) / "호환 모드(GDI)". `settings.json`의 `recording.captureMode` = `"auto"` / `"gdi"`. 필드가 없거나 알 수 없는 이름·숫자는 `auto`로 읽고 파일의 나머지 값은 그대로 둔다(속성 수준 JSON 변환기). 다음 녹화부터 적용된다. |
| 진단 스위치 | 환경 변수 `MYCAPTURE_RECORDING_BACKEND=gdi|wgc`가 설정보다 우선한다. 다른 값은 무시한다. 앱이 이 값을 쓰지는 않는다. |
| 진단 로그 | 녹화마다 요청 근거(백엔드, 설정 값, 환경 변수), 실제 백엔드, WGC 세션 생성 시간·첫 프레임 대기·D3D 드라이버·테두리 없음 접근 결과·`IsBorderRequired`, 폴백 사유(여러 모니터·미지원은 정보, 그 밖의 실패는 경고와 예외), WGC 세션 종료 시 받은·복사·재사용 프레임 수를 남긴다. 사용자 알림 UI는 두지 않는다. |
| 유지한 스파이크 구조 | `Open`에서 WGC 세션을 작업 스레드로 열어 인코더 초기화와 겹침, 최신 프레임 한 장만 보관, 일시정지 중 캡처 0회, 열기·녹화 중 GDI 폴백, 커서 강조 링을 관리 코드로 그림, `RequestAccessAsync(Borderless)`와 `IsBorderRequired = false`. |

### MSIX capability

`build/store/AppxManifest.xml`에 `uap11:Capability Name="graphicsCaptureWithoutBorder"`를 선언하고 `uap11`
네임스페이스(`http://schemas.microsoft.com/appx/manifest/uap/windows10/11`)를 `IgnorableNamespaces`에 넣었다.

- [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations):
  `graphicsCaptureWithoutBorder`는 일반 capability이며 `GraphicsCaptureSession.IsBorderRequired`를 쓰는 데 필요하다.
  `graphicsCaptureProgrammatic`은 `GraphicsCaptureItem`을 `WindowId` 또는 `DisplayId`로 만들 때 필요하다. 녹화는
  `IGraphicsCaptureItemInterop.CreateForMonitor`(HMONITOR)로 항목을 만들므로 선언하지 않았다.
- [uap11:Capability](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap11-capability):
  네임스페이스 URI, 허용 이름(`graphicsCaptureProgrammatic`, `graphicsCaptureWithoutBorder`), 최소 OS 빌드 19645.
  매니페스트 `MinVersion` 10.0.22000.0이 충족한다.
- [GraphicsCaptureSession.IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired):
  테두리를 끄려면 `RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)`로 사용자 동의를 받아야 하고(확인 창이
  표시된다고 문서화됨), 이 호출에는 `graphicsCaptureWithoutBorder` 선언이 필요하다. 사용자가 거부하면 `false` 설정은
  성공하지만 무시되고 테두리가 보인다. 다른 앱이 같은 디스플레이를 테두리 필요로 캡처하면 테두리가 다시 보인다.

검증: 매니페스트 계약 테스트(`StoreManifestContractTests`)가 capability 목록, uap11 네임스페이스와 접두어,
`IgnorableNamespaces`, 최소 빌드를 확인한다. `build/package-store.ps1`과 같은 고정 `makeappx`
(Microsoft.Windows.SDK.BuildTools 10.0.28000.2705)로 `-LocalTest`와 같은 토큰 값을 넣은 최소 페이로드를 패키징해
"Package creation succeeded"를 확인했고, capability 이름을 일부러 틀리게 바꾼 대조 실행은 스키마 열거형 오류
(C00CE169)로 실패했다. 전체 publish 폴더로 만든 Store 패키지 설치·실행은 하지 않았다.

## 새 측정(고해상도 대기 타이머 적용 후)

- PC·디스플레이: ADR 0005와 같다(Ryzen 7 9800X3D, RTX 5080, DISPLAY1 3440x1440 119Hz SDR, Windows 11 Home 10.0.26200).
- 명령: `MyCapture.exe --selftest-recording-backends <절대 경로>`, `MYCAPTURE_BENCH_SECONDS=5`, `MYCAPTURE_BENCH_REPEATS=1`,
  `MYCAPTURE_DATA_ROOT` 격리, 한 번 실행(칸마다 n=1). 표본별 시스템 CPU 15~32%. 장면·지표 정의는 ADR 0005와 같다.
  ADR 0005는 10초 × n=4였으므로 칸별 수치는 같은 조건의 반복 측정이 아니라 방향 확인용이다.

### 움직이는 장면(렌더 루프 동작)

| 영역 | fps | 백엔드 | 실효 fps | 드롭률 | 캡처 p50 / p95 ms | 프로세스 CPU % | DWM CPU % | 시스템 GPU 3D % | 시작→첫 프레임 ms |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1920x1080 | 30 | GDI | 30.1 | 0.0% | 12.61 / 13.43 | 2.61 | 1.28 | 5.4 | 574 |
| 1920x1080 | 30 | WGC | 30.0 | 0.1% | 0.65 / 0.79 | 2.58 | 0.68 | 6.0 | 437 |
| 1920x1080 | 60 | GDI | 59.8 | 0.3% | 8.96 / 9.98 | 4.45 | 1.89 | 6.1 | 426 |
| 1920x1080 | 60 | WGC | 60.1 | 0.0% | 0.58 / 0.66 | 3.57 | 0.80 | 6.7 | 439 |
| 2560x1440 | 30 | GDI | 30.0 | 0.0% | 20.05 / 21.33 | 5.12 | 1.69 | 5.5 | 454 |
| 2560x1440 | 30 | WGC | 29.9 | 0.3% | 1.01 / 1.26 | 3.98 | 0.91 | 6.4 | 419 |
| 2560x1440 | 60 | GDI | 51.3 | 14.5% | 14.04 / 21.54 | 8.63 | 2.64 | 6.4 | 439 |
| 2560x1440 | 60 | WGC | 60.0 | 0.0% | 0.85 / 1.02 | 7.69 | 0.97 | 7.4 | 491 |
| 3440x1440 | 30 | GDI | 30.0 | 0.0% | 23.88 / 25.44 | 6.58 | 2.20 | 5.5 | 455 |
| 3440x1440 | 30 | WGC | 30.0 | 0.0% | 1.19 / 1.46 | 4.72 | 0.78 | 6.7 | 521 |
| 3440x1440 | 60 | GDI | 39.7 | 33.9% | 20.20 / 21.89 | 10.07 | 2.77 | 6.2 | 482 |
| 3440x1440 | 60 | WGC | 60.0 | 0.1% | 1.09 / 1.29 | 9.91 | 0.86 | 8.0 | 414 |

### 정지 장면(렌더 루프 없음, 1920x1080)

| 조건 | GDI 실효 fps / 드롭 / 간격 p50 | WGC 실효 fps / 드롭 / 간격 p50 | ADR 0005(기본 타이머) GDI / WGC |
|---|---:|---:|---:|
| 30fps | 29.9 / 0.2% / 33.3ms | 29.9 / 0.3% / 33.3ms | 21.4 / 21.4~21.5 |
| 60fps | 60.1 / 0.0% / 16.7ms | 59.9 / 0.1% / 16.7ms | 47.7 / **32.0** |

같은 날 프로브 재실행(`MYCAPTURE_BENCH_MATRIX=0`)에서도 30fps GDI 29.9 / WGC 29.9, 60fps GDI 60.0 / WGC 60.1이었다.
ADR 0005의 "정지 장면 60fps에서 WGC 32fps" 문제는 새 대기 타이머에서 재현되지 않는다. 정지 장면 WGC 캡처 p50/p95는
0.00ms(새 프레임이 없어 직전 버퍼 재사용)다.

### 기능 검사(같은 실행)

- 일시정지·재개: GDI·WGC 모두 일시정지 중 캡처 0회, 재개 직후 세 프레임의 마젠타 픽셀 0, 최대 간격 34ms.
- 노란 테두리 프로브: 기본 테두리 세션과 테두리 없음 세션 모두 GDI로 읽은 가장자리 노란 픽셀 0. 테두리 없음 세션은
  `BorderlessAccess=Allowed`, `IsBorderRequired=false`(비패키지 실행).
- 제외 검사: 두 백엔드 모두 FAIL로 기록됐다(2회 실행, GDI 컨트롤 창 단계 3,520/3,513 외에는 같은 값). MyCapture 창이
  없는 단계에서도 GDI 3,524~4,050, WGC 3,517~3,536픽셀이 고정 색과 달랐다. 실행 시 작업 영역 오른쪽 아래에 다른
  프로그램의 최상위 창(트레이 오버플로 창, pythonw 도구 창)이 있었고, 단색 최상위 창으로 화면을 덮은 뒤 GDI로 읽어도
  `[2949,1282 335x41]` 안의 2,389픽셀이 남았다. 컨트롤 창을 띄워도 늘지 않았고(GDI 4,050→3,520·3,513, WGC
  3,536→3,507) 녹화 시계만 5,130~5,148픽셀을 더해 ADR 0005의 시계 5,127~5,134와 같은 범위다. 외부 창이 없는
  상태에서 다시 확인해야 한다.

### 기존 self-test

| 실행 | `--selftest-recording` | `--selftest-recording-performance` |
|---|---|---|
| 기본값(환경 변수 없음) | PASS, Requested/Active = WindowsGraphicsCapture | PASS, Requested/Active = WindowsGraphicsCapture, 30fps 목표 지속 녹화 2회 182/182프레임·동시 작업 녹화 157/157프레임(실효 30.15~30.19fps), 녹화별 캡처 합계 32~68ms |
| `MYCAPTURE_RECORDING_BACKEND=gdi` | PASS, Requested/Active = Gdi | PASS, Requested/Active = Gdi, 같은 프레임 수(실효 30.12~30.17fps), 녹화별 캡처 합계 631~1,335ms |

ADR 0005 당시 같은 성능 self-test는 두 백엔드 모두 약 6초에 131~136프레임(21.7~22.5fps)이었다.

## 판단

새 대기 타이머에서 30fps는 두 백엔드 모두 목표를 지키고, WGC는 같은 조건의 GDI보다 캡처 시간(p50)이 약 15~20배 짧다.
60fps 2560x1440·3440x1440에서 GDI는 51.3fps(드롭 14.5%)·39.7fps(33.9%)이고 WGC는 60.0fps(0.0~0.1%)다. 정지 장면
60fps의 WGC 역전은 사라졌다. DWM CPU는 WGC가 GDI의 31~54%이고 시스템 GPU 3D는 0.6~1.8%p 높다. ADR 0005 권고 2·3 중
코드로 할 수 있는 부분(기본값 전환, 폴백 유지, MSIX capability)을 이 변경에 넣고, 실기 항목은 사용자 검증으로 남긴다.

## 사용자 실기 검증 항목

1. Windows 11 22000 실기기: 기본값 녹화 PASS, 로그의 `active WindowsGraphicsCapture`, 30/60fps 실효 fps.
2. 노란 테두리 육안 확인: 비패키지 실행과 MSIX 설치본 각각에서 녹화 중 모니터 가장자리에 노란 테두리가 보이지 않는지.
   MSIX에서는 첫 녹화 때 테두리 없는 캡처 동의 창이 뜨는지, 허용·거부 후 테두리 동작, 로그의 `borderless access` 값.
3. 4K(3840x2160) 디스플레이 30/60fps, 혼합 DPI 두 모니터에서 각 모니터 안 영역(WGC)과 걸친 영역(GDI 폴백 로그).
4. HDR 켠 디스플레이에서 GDI(호환 모드)와 WGC 녹화의 색 비교(B8G8R8A8 풀의 시스템 SDR 변환, ADR 0005 위험 3).
5. 하이브리드 GPU 노트북, 내장 GPU 전용 PC, 가상 디스플레이(IddCx)에서 열기 성공 또는 GDI 폴백.
6. 녹화 중 디스플레이 분리·해상도 변경·드라이버 재설치(TDR): 녹화가 실패하지 않고 GDI로 이어지는지와 경고 로그.
7. UAC 보안 데스크톱·잠금 화면·DRM 재생 중 녹화: GDI와 비교해 더 나쁜 실패가 없는지.
8. 설정에서 호환 모드로 바꾼 뒤 다음 녹화 로그가 `requested Gdi, active Gdi`인지, 다시 자동으로 돌리면 WGC인지.
9. 외부 최상위 창이 없는 화면에서 `--selftest-recording-backends`(5초 1회)의 제외 검사 PASS.

## 중단 조건과 되돌리기

ADR 0005 중단 조건을 그대로 따른다. 22000 실기기나 MSIX에서 테두리를 끌 수 없거나, HDR에서 GDI보다 품질이 나쁘고
셰이더 비용이 크거나, 보안 데스크톱·장치 제거에서 GDI보다 나쁜 실패가 확인되면 기본값을 GDI로 되돌리고 WGC는
선택지로만 남긴다. 되돌리기는 `RecordingCaptureBackendSelection.Resolve`의 `Auto` 매핑 한 곳이며, 사용자는 그 전에도
설정의 호환 모드로 바로 GDI를 쓸 수 있다.

## 재현

```powershell
$exe = '<worktree>\src\MyCapture.App\bin\Release\net10.0-windows10.0.22000.0\MyCapture.exe'
$env:MYCAPTURE_DATA_ROOT = '<격리 폴더>'
Start-Process -Wait -PassThru -FilePath $exe -ArgumentList '--selftest-recording', '<빈 폴더>'
Start-Process -Wait -PassThru -FilePath $exe -ArgumentList '--selftest-recording-performance', '<빈 폴더>'
$env:MYCAPTURE_RECORDING_BACKEND = 'gdi'   # 같은 두 명령을 GDI로
$env:MYCAPTURE_RECORDING_BACKEND = $null
$env:MYCAPTURE_BENCH_SECONDS = '5'; $env:MYCAPTURE_BENCH_REPEATS = '1'
Start-Process -Wait -PassThru -FilePath $exe -ArgumentList '--selftest-recording-backends', '<빈 폴더>'
```
