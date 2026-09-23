# 라운드 2 — 성능·안정성

대상: Release 3.0.0, 커밋 `117b0ef`. 페르소나: performance.
이번 세션 실측은 없다. Linux에서 Windows 11 WPF를 실행하지 않았고, FPS·CPU·핫키 지연을 다시 재지 않았다. 아래 시간은 라운드 1이 조건과 함께 인용한 과거 검증 문서의 숫자다. 제품 소스는 열기만 했다.

닫는 대상은 CL-04, CL-05, 기능 축 F-15, 그리고 점수 3의 유지다.

## 1. 점수

**3 / 5 를 유지한다.**

| 판단 | 라운드 1 | 라운드 2 | 근거 |
|---|---|---|---|
| 성능·안정성 점수 | 3 / 5 | 3 / 5, 유지 | 핵심 경로는 코드에 있다. 문서화된 정적 녹화는 기본 30fps에 못 미친다. 3.0.0을 이 세션에서 재계측하지 못했다. |
| 신뢰도 | 코드는 `117b0ef`, 숫자는 1.7–2.1 | 동일 | `docs/performance/v1.8.0-recording-capture-evidence.md` 등은 커서 강조·조건부 100ms·PP-OCR 병렬 이전이다. |

점수를 움직이지 않는 이유:

| 방향 | 하지 않는 이유 |
|---|---|
| 4 또는 5로 올림 | 경쟁 행렬 P0(Graphics Capture 또는 Desktop Duplication, 1080p/4K에서 GDI 대비 CPU·드롭·복구, Windows 11 22000 실기기)는 코드에 없다. `src/`에 `IDXGIOutputDuplication`, `Windows.Graphics.Capture` 구현이 없다. 과거 수락 런의 실효 FPS는 30이 아니다. |
| 2 또는 1로 내림 | 정지 캡처와 영역 녹화는 GDI로 동작한다. 획득·MP4 확정은 UI 스레드 밖이고, 큐·녹화는 사이드카로 복구한다. “위험하거나 부재”가 아니다. 미계측을 제품 결함으로 한 단계 더 깎지 않는다. |

30fps 미달의 인용 조건은 그대로다. `--selftest-recording-performance`, 파란 정적 픽스처, 320×240과 1280×720, 준비 뒤 약 6초, 16논리 프로세서, 애니메이션·혼합 DPI 아님, 다른 앱 부하 미통제. 수락 런(`5372bab`+진단) 실효 FPS는 320에서 21.842(드롭 50/182), 1280에서 22.334(드롭 47/182), 동시 UI 320에서 18.941이다. 조용한 반복은 19.250 / 23.381fps, 통합 진단(`6abf1f9`)은 24.651 / 20.797fps다. 이 표에는 3.0.0 커서 강조가 없다. 그 추가 비용으로 점수를 더 깎거나 올리지 않는다.

## 2. CL-04 — GDI만 사용

**합의.** 주 소유는 performance다. 기능 점수는 “캡처가 된다”로 남긴다.

| 주장 | 결정 | 주 소유 | 코드 | 성능이 채점하는 것 |
|---|---|---|---|---|
| 정지·녹화 획득은 `BitBlt + CAPTUREBLT + GetDIBits`뿐이다. Desktop Duplication·Graphics Capture 구현은 없다. | 합의 | performance | `ScreenCaptureEngine.cs` 65–70행 주석, 356–358행 `BitBlt`. `RegionFrameGrabber.cs` 12–16행이 같은 경로를 녹화에 재사용. 저장소 검색상 DXGI Output Duplication·Windows Graphics Capture 타입 없음. | 백엔드가 하나뿐인 사실, 문서화된 30fps 미달, P0 승격 조건(1080p/4K CPU·드롭·복구, Windows 11 22000)이 비어 있는 것. 이것이 점수 3의 감점이다. |
| 영역 캡처와 녹화는 그 GDI 경로로 이미 동작한다. | 합의 | functionality가 “된다”를 보유. performance는 재감점하지 않음 | `CaptureSession.CaptureInto`, `RegionFrameGrabber.GrabInto`, ADR 0002의 적응 드롭. | “동작 여부”를 기능 공백으로 세지 않는다. 느린 획득과 드롭만 센다. |
| 기능 축이 GDI를 기능 감점에서 뺀다 (F-15). | 합의 | performance | 기능 라운드 1 §4 말미, §5 “아님” 행. inbox F-15. | 기능 점수 4에서 GDI를 빼는 것에 동의한다. 같은 사실을 성능 점수 3에서 유지한다. 한 번만 감점한다. |

F-15에 대한 답:

| 기능 축의 문장 | 성능 축의 답 |
|---|---|
| 캡처 백엔드 교체(WGC/Desktop Duplication)는 기능 축 P0이 아니다. | 맞다. 영역 선택, 창·전체 캡처, 영역 MP4는 현재 경로로 닫혀 있다. 백엔드 교체는 기능 추가가 아니라 획득 비용·드롭·모드 전환 복구의 과제다. |
| 영역 캡처와 녹화는 GDI로 이미 동작한다. | 맞다. “동작”은 프레임이 나온다는 뜻이다. 기본 `Fps30`(`RecordingSettings`)을 지킨다는 뜻이 아니다. 30fps 달성 여부는 기능 점수에 넣지 않는다. |
| 매트릭스 P0의 승격 조건이 CPU·드롭·실기기라 주 소유는 performance. | 맞다. P0 문구 그대로 performance가 소유한다. 계측 전에는 30fps·60fps를 달성으로 적지 않는다. |
| 기능 점수에서 이 항목을 감점하지 않는다. | 동의. functionality는 이 사실을 반복 채점하지 않는다. |

경계. `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`는 인코드 플래그다. 선택된 MFT가 GPU였는지는 로그가 없고, 화면 획득을 하드웨어 경로로 만들지 않는다. OBS의 스트리밍·오디오·다중 소스는 범위 밖이라 이 감점의 근거가 아니다. DWM·보안 데스크톱에서 GDI가 비는 비율은 미확인이며, 확인 전에는 기능 부재로 옮기지 않는다.

## 3. CL-05 — 커서 강조를 세 갈래로

**합의.** 존재, 비용, 색을 한 주장으로 합치지 않는다. 성능은 비용 미측정만 소유한다.

| 갈래 | 결정 | 주 소유 | 성능이 채점하는가 | 근거 |
|---|---|---|---|---|
| 존재. 녹화 중 커서 강조 링은 3.0.0에 있고 기본이 켜져 있다. 매트릭스 P1이 클릭 강조를 미구현으로 묶은 것은 틀리다. 링 픽셀 테스트는 없다. | 합의 (F-06) | functionality | 아니오. 없음을 감점하지 않고, 픽셀 회귀 테스트 부재도 성능 점수가 아니다. | `RecordingSettings.CursorHighlight` 기본값 `true` (51행). `RegionRecordingCoordinator`가 그 값을 `RegionFrameGrabber`에 넘긴다. `CaptureInto` 360–362행이 `_includeCursor && CursorHighlight`일 때 `DrawCursorHighlight`를 호출. 주석 341–342행: 정지 캡처는 이 플래그를 켜지 않는다. 그랩 생성자 기본값 `cursorHighlight = false`(36행)는 제품 기본값이 아니다. 제품 기본은 설정 쪽 `true`다. |
| 비용. 프레임마다 GDI가 더해지고, 드롭에 미치는 시간은 미측정이다. | 합의 (`perf-cursor-highlight`) | performance | 예. 다만 “미측정” 자체를 3점 아래로 내리는 감점으로 쓰지 않는다. | `DrawCursorHighlight` 495–536행: 프레임마다 `GetCursorInfo`, `GetAsyncKeyState`, `CreatePen`, `Ellipse`, `DeleteObject`. `docs/performance/`에는 이 경로의 밀리초가 없다. v1.8.0 녹화 표는 강조 링 이전이다. |
| 색. `#FFC700` / `#FF4040`이 토큰 밖이고 프레임 픽셀에 구워진다. | 합의 (AES-09) | aesthetics | 아니오. 색 불일치를 성능 감점으로 쓰지 않는다. | COLORREF `0x0000C7FF`(유휴) → `#FFC700`, `0x004040FF`(왼쪽 버튼) → `#FF4040`. `ScreenCaptureEngine.cs` 510–511행. 펜이 캡처 DC에 그려진 뒤 `GetDIBits`로 프레임에 들어간다. 토큰 앰버·코랄과의 차이는 aesthetics. |

성능이 비용 갈래에서 말하는 범위:

| 포함 | 제외 |
|---|---|
| 링이 기본으로 켜진 3.0.0에서 프레임당 추가 GDI의 시간이 문서에 없다. | 링이 있다는 사실 (functionality). |
| 재계측은 같은 기계에서 강조 on/off, 320·1280·1080p·4K, 목표 30과 60, 드롭, 프로세스 CPU를 남겨야 한다. 그 표가 있기 전에는 점수 3을 올리지 않는다. | 토큰과 다른 노랑·빨강 (aesthetics). |
| 1080p/4K 백엔드 스파이크의 승격 조건에 기본 켜진 강조를 넣어야 3.0.0과 맞다. | 클릭을 타임라인 단계 마커로 남기는 일 (functionality P1). 성능은 그 마커의 부재를 채점하지 않는다. |
| convenience는 이 미측정 밀리초를 따로 감점하지 않는다. 체감 끊김을 말하려면 측정이 먼저다. | 링 픽셀 골든 테스트의 부재. 그리기 코드는 있고, 골든 프레임은 기능 회귀의 공백이다. |

## 4. 이중 채점 경계

| 사실 | 한 번 깎는 축 | 보조 축 |
|---|---|---|
| GDI만 있고 P0 백엔드가 없다. 문서화된 정적 녹화 실효 FPS가 기본 30 아래다. | performance (점수 3) | functionality는 “된다”만 확인하고 감점하지 않는다. positioning은 품질 투자로 인용만 한다. |
| 커서 링이 구현되어 있고 기본이 켜져 있다. | 감점 아님. 매트릭스의 “미구현”을 정정하는 사실은 functionality | performance는 비용을 미확인으로만 둔다. |
| 링의 프레임당 시간 | 아직 감점 숫자가 없다. 소유만 performance | 측정 전에는 convenience도 깎지 않는다. |
| 링 색이 토큰 밖이고 프레임에 구워짐 | aesthetics | performance는 색을 채점하지 않는다. |
| 단계 마커·링 픽셀 테스트 부재 | functionality | performance의 재계측 과제와 합치지 않는다. |

## 5. 이 라운드에서 점수를 바꿀 수 없는 미확인

| 항목 | 상태 |
|---|---|
| `117b0ef`의 FPS, CPU, 워킹 셋, 핫키→첫 렌더 | 이번 세션 실측 없음 |
| 커서 강조 on/off의 프레임당 밀리초와 드롭 변화 | 없음. 과거 19–25fps대는 링 없는 표 |
| 1080p/4K·애니메이션·60fps 실효 FPS | 없음 |
| 조건부 100ms가 1.9.0 요청→렌더(4806×2466, 약 199–282ms)에서 걷어 내는 시간 | 없음 |
| 하드웨어 MFT가 실제로 선택됐는지 | 로그 없음 |

이 칸이 채워지기 전에는 성능 점수는 3이다.
