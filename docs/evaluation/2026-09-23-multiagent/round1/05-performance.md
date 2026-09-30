# 라운드 1 — 성능·안정성

대상: Release 3.0.0, 커밋 `117b0ef` (`main`).
판단 근거: `docs/performance/`, `docs/adr/0001`·`0002`·`0003`, `docs/competitive-matrix.md`, `docs/releases/` 검증·노트, 현재 소스와 테스트 이름.
`docs/market/`는 0.x 스냅샷이라 인용하지 않는다.
이번 세션은 Linux라 Windows 11 WPF 앱을 실행해 FPS·CPU·지연을 재지 못했다. **이번 세션 실측은 없다.** 아래 숫자는 문서에 적힌 과거 계측만이며, 각 수치 옆에 그 문서의 조건을 붙인다. Release 3.0.0에서 다시 잰 값은 없다.

## 1. 점수와 신뢰도

**점수: 3 / 5.**

핵심 경로는 있다. 정지 캡처의 BitBlt는 UI 스레드 밖이고, 녹화는 전용 MTA 스레드에서 적응형으로 프레임을 버리며, MP4 확정 join도 디스패처 밖이다. 큐·녹화는 크래시 창을 사이드카로 복구한다. 다만 캡처 백엔드는 GDI `BitBlt + CAPTUREBLT + GetDIBits`뿐이고, 문서화된 정적 녹화는 제품 기본 30fps에 못 미치는 실효 FPS와 드롭을 남긴다. 영역 선택 전에 가상 데스크톱 전체를 읽는다. 경쟁 행렬의 P0(Windows Graphics Capture 또는 Desktop Duplication)는 코드에 없다. 그래서 “위험하거나 부재”(1)는 아니고, “경쟁 상위”(5)도 아니다. 핵심은 되나 캡처·녹화 흐름이 자기 목표 프레임에서 끊긴다.

**신뢰도**

| 층 | 상태 |
|---|---|
| 코드 감사 | 백엔드, 스레드, 복구, 큐 상한, 썸네일 예산은 `117b0ef` 소스로 확정 |
| 과거 계측 문서 | 1.7.0–2.1.0 검증·`docs/performance/v1.8.0-*`. 3.0.0의 조건부 100ms, 기본 켜진 커서 강조, PP-OCR 줄 단위 병렬은 그 계측에 없음 |
| 이번 세션 실측 | 불가. FPS·CPU·핫키 지연·혼합 DPI를 이 환경에서 확인하지 못함 |

ADR 0002의 “4K 한 프레임을 수십 ms”는 조건(해상도, 샘플 수, 장비, 커밋)이 없다. 측정치로 인용하지 않는다. 조건이 있는 대형 프레임 수치는 1.8.1의 4806×2466이다.

## 2. 경로별 병목 가설

### 캡처

**가설.** 핫키 한 번의 비용은 선택 영역이 아니라 가상 데스크톱 전체의 GDI 복사다. UI는 그 동안 멈추지 않게 바뀌었고, 사용자는 프레임이 붙기 전까지 선택 화면을 기다린다.

코드:

- 유일 백엔드: `src/MyCapture.Platform/Capture/ScreenCaptureEngine.cs`. 주석이 Desktop Duplication을 고르지 않은 이유를 적는다. `CaptureVirtualDesktop`가 `MonitorEnumerator.GetVirtualDesktopBounds()` 전체를 `CaptureRegion`으로 넘긴다. 소비 경로는 `GetDIBits`를 잠긴 `WriteableBitmap` 백버퍼에 직접 쓴다(1.8.1에서 전체 프레임 관리형 스테이징 배열을 제거).
- 저장소 전체에서 `IDXGIOutputDuplication`, `Windows.Graphics.Capture`, Graphics Capture 구현은 없다. 문자열 검색은 이 주석과 `docs/competitive-matrix.md` P0뿐이었다.
- `CaptureOverlayCoordinator.Start`는 세션을 먼저 예약하고, 획득은 `Task.Run`이다. 직전 오버레이·트레이 메뉴가 닫힌 뒤 750ms(`PresentationSettleMs`) 안이면 `Task.Delay(100)`을 타고, 그 외 콜드 캡처는 대기 없이 획득한다(`src/MyCapture.App/Capture/CaptureOverlayCoordinator.cs`). 3.0.0 릴리스 노트의 “매번 100ms” 제거와 일치한다.
- 창은 프레임 전에 만들되, 프레임이 붙기 전에는 불투명 선택기를 보이지 않게 둔다(1.8.4 검증이 설명하는 계약. 현재 `Start`도 `frame: null`로 숨긴 채 연다).
- 프리웜은 트레이 기동 직후 백그라운드 1픽셀 캡처다(`App.StartCapturePrewarm` → `ScreenCaptureEngine.Prewarm`). 주석은 테스트 장비에서 첫 3440×1440이 827ms, 이후 21–51ms였다고 적는다. 샘플 수·날짜·커밋이 없어 본문 측정표에는 넣지 않는다.

과거 계측 (이번 세션 재현 아님):

- 1.8.1 검증. 같은 4806×2466 가상 데스크톱, 전용 STA, 오버레이·핫키·저장 없음, 구현당 웜 5샘플·콜드 1샘플. 직접 리드백 이후 웜 경과 중앙값 81.285ms(이전 110.791ms), 호출 스레드 관리형 할당 중앙값 1,880바이트(이전 47,408,472바이트), 콜드 219.817ms(이전 323.274ms), 획득 스레드 CPU 중앙값 46.875ms(이전 31.250ms). GC는 양쪽 모두 샘플마다 Gen0/1/2 +1. 문서는 CPU 감소나 스터터 제거를 주장하지 않는다.
- 같은 문서의 디스패처 진단 3샘플, 요청 간격 16ms. 동기 획득은 틱 0, 비동기 획득은 틱 4. 최대 간격 99.111–103.194ms → 31.556–35.814ms. 16ms 예산을 보장하지 않는다고 명시.
- 같은 문서, 지속 STA·PerMonitorV2·96 DPI 합성 픽스처, 소스 `84352391…`. 획득 131.384 / 119.091 / 108.092ms, 요청→첫 렌더 279.778 / 160.939 / 168.422ms.
- 1.9.0 검증, 2026-09-10 03:48 KST, 클린 Release DLL, `CaptureOverlayCoordinator.Start`부터 MTA `CaptureVirtualDesktop`와 `ContentRendered`까지. 트레이 기동·물리 핫키·부팅 직후 첫 캡처는 범위 밖. 프레임은 모두 4806×2466 Bgr32. 1픽셀 프리웜 1회 23.789ms. 콜드 획득 121.428 / 139.976 / 132.859ms, 요청→렌더 267.002 / 250.196 / 199.761ms, 최대 하트비트 간격 135.589 / 79.191 / 130.400ms. 프리웜 획득 103.297 / 167.658 / 143.860ms, 요청→렌더 240.982 / 226.538 / 281.926ms, 하트비트 최대 166.312ms, 취소 반환 최대 177.744ms. 문서는 프리웜을 보편적 지연 수정으로 보지 않는다.
- 1.8.4 `--selftest-capture-performance`. 3840×2160 합성, **주입 지연 150ms**, 실선택기, 20라운드. 프레임 없는 관찰 구간 평균 143.60ms → 0ms, Start→첫 프레임 평균(주입 150ms 포함) 260.15ms → 207.13ms. 이 명령은 1.9.0 문서가 “실제 첫 데스크톱 캡처 지연이 아니다”라고 구분한다.
- 1.9.2는 획득 전 100ms 대기를 항상 넣었다. 3.0.0은 최근 닫힘 750ms 안으로 좁혔다. 위 1.8.1·1.9.0 표는 그 조건부 대기 이전이다.

### 핀

**가설.** 드래그 콜백 자체는 짧다. 남은 비용은 클립보드 PNG, OCR, 그리고 핀마다 두는 최상위 HWND다. 드래그 중 모니터 재열거는 마우스 다운에서 한 번 캐시한다.

코드: `PinWindow.OnMouseMove`는 캡처가 있고 왼쪽 버튼이 눌렸으며 커서 픽셀이 바뀔 때만 `MovePhysical`을 호출한다. 데스크톱 사각형은 드래그 시작 시 `MonitorEnumerator.GetVirtualDesktopBounds()`를 `_dragDesktop`에 넣는다. 이동은 `PhysicalWindowPositioner.Move`. 실패·캡처 상실·버튼 업·창 닫힘은 `EndDrag`로 제스처를 끝낸다. 복사·저장은 `ClipboardImageService` / `PinImageSaveService`의 `Task.Run`·`StaThreadTask`다. 핀 OCR은 레코드에 캐시하지 않고 `OcrResultPresenter`로만 간다.

과거 계측: `docs/performance/v1.8.0-pin-storage-evidence.md`, 2026-09-08, Release, 소스 기반 `776da73`, 명령 `dotnet …MyCapture.dll --selftest-pin-storage-performance`. 격리 큐, 사용자 큐·클립보드를 읽지 않음. 합성 입력으로 실제 HWND를 옮기며 OS 포인터는 움직이지 않음. 유휴는 진단 호스트이지 완전 기동한 트레이가 아님. 논리 프로세서 16. 제스처 6×이동 60, 요청 16ms 타이머.

| 항목 | 값 |
|---|---|
| 콜백 중앙값 | 0.985–1.239ms (제스처별) |
| 콜백 p95 | 1.237–1.851ms |
| 콜백 최대 | 1.512–2.975ms |
| 디스패치 간격 중앙값 | 약 30.0–30.5ms |
| 제스처당 콜백 할당 | 63,360바이트 |
| 마우스 다운 | 0.056–0.123ms |

문서는 30ms 간격을 하드웨어 마우스 지연이나 60Hz 증명으로 쓰지 말라고 한다. 숨은 유휴 3샘플은 약 2초 동안 프로세스 CPU 0. 보이는 유휴는 62.5 / 0 / 0ms(16논리 프로세서 정규화 0.196% / 0% / 0%). 긴 트레이 유휴의 증거가 아니라고 명시. 정렬 발행 이후 조용한 재측정에서도 핀 중앙값은 약 1.080–1.299ms, p95 1.752–2.323ms로, 저장 변경이 핀 가속이 아니라고 적는다.

1.8.4 선택기 포인터(핀이 아님): 핸들러 할당 702.21 → 68.00바이트/업데이트, 라운드 P95 0.0940 → 0.0075ms. 조건은 위의 주입 지연 픽스처.

### OCR

**가설.** 인식 자체는 UI 밖에서 도나, 기본 경로는 여전히 한 장씩이고 픽셀을 PNG로 왕복한다. 3.0.0의 코어 수 병렬은 PP-OCR 인식 단계 한 장 내부이며, 큐 색인은 직렬이다. 속도 향상 수치는 문서에 없다.

코드:

- 기본 품질은 `OcrQuality.Fast`(`AppSettings`). Fast/Balanced는 Windows OCR, Accurate/Enhanced만 `UseNeuralModel`로 PP-OCR(`OcrQualityProfile`, `CaptureOcrService`).
- `NeuralOcrEngine`은 `ImageCodec.EncodePng` 뒤 `SKBitmap.Decode`를 하고, `RecMaxDegreeOfParallelism = clamp(ProcessorCount, 1, 8)`이다. `DoAngle`은 켜 둔다. `lock (_sync)`라 엔진 인스턴스 동시 인식은 하나다.
- `WindowsOcrRecognizer`는 PNG 인코드를 `Task.Run`으로 보낸다.
- `OcrIndexingService`는 레코드 하나씩, 색인 요청은 `searchRotatedOrientations: false`. 앱은 `DispatcherPriority.ApplicationIdle`에서 자동 색인을 건다(`App.RequestAutomaticIndexing`). 수동 결과 창은 `OcrResultPresenter`가 UI에 붙어 있고, `RecognizeAsync` 완료 뒤에만 창을 만진다.
- ADR 0003은 “Windows OCR, 한 건씩, 핫키와 경쟁하지 않음”이다. 이후 PP-OCR이 추가됐다. ADR의 “패키지 0개”는 현재 Accurate 경로와 다를 수 있다. 패키지 구성은 이번 축에서 재감사하지 않았다.

과거 계측: 같은 핀 저장 문서. Windows OCR, 생성 720×360 PNG, 색인 모드(방향 검색 없음), 텍스트·공백 각 6샘플. 텍스트는 기대 문자열 `Success`, 공백은 `NoText`. 텍스트 벽시계 중앙값/p95 115.475 / 208.065ms(파이프라인 99.888 / 202.349ms). 공백 91.305 / 123.873ms(파이프라인 78.588 / 120.684ms). 이 샘플은 PP-OCR 병렬이 아니다.

1.0.0 검증의 `--selftest-ocr`: 언어 `ko`, 720×360, 5줄, 106.6ms, PASS. 장비·커밋 세부 조건은 그 절의 Windows NT 10.0.26200.0 x64 self-test 문맥이다. 3.0.0 엔진 구성과 같다고 단정하지 않는다.

### 라이브러리

**가설.** 300장 메타 인덱스는 메모리에서 검색·정렬한다. 체감 비용은 시작 시 인덱스·복구 열거, 그리고 타일 디코드다. 디코드는 동시 2장, 캐시는 16MiB 또는 64장에서 끊는다.

코드:

- ADR 0001: `index.json` + `File.Replace` + 항목 `meta.json`. 기본 상한 300장, 2GiB(`QueueSettings`). 클램프는 개수 10–5000, 바이트 128MiB–512GiB(`SettingsRanges`). 썸네일 긴 변 기본 320, 범위 96–1024. JPEG 품질 82(`ImageCodec.ThumbnailJpegQuality`).
- 발행 슬롯 8개(`CaptureQueue._publicationSlots`). 비동기 원본·OCR은 슬롯을 기다리고, 동기 `Save`는 같은 FIFO에 합류한다(핀 저장 문서가 `c0e65ec` 계약을 설명. 슬롯 상수는 현재 소스와 같다).
- 원본 PNG·`thumb.jpg`는 `StaThreadTask`의 `WriteOriginalFiles` 안이다(`CapturePersistenceService`). UI 스레드가 PNG를 인코드하지 않는다.
- `GalleryThumbnailLoader`는 `SemaphoreSlim(2, 2)` 뒤 `Task.Run`으로 `TryLoadScaled` 후 Freeze. `GalleryViewModel.ThumbnailCacheBudgetBytes`는 16MiB, 장수 상한 64. 화면 밖 타일은 요청하지 않는 테스트가 있다.
- 동영상 썸네일 긴 변은 이미지 설정과 별도로 640(`VideoLibraryService.ThumbnailLongEdge`).
- 검색은 `CaptureTextSearch`의 메모리 매치다. 전문 검색이 디스크를 다시 읽는 경로는 여기 없다.

과거 계측 (동기 발행 구간, 격리 생성 이미지, 크기당 16장, 사용자 큐 아님):

| 크기 | 총 벽시계 중앙값/p95 | UI 발행 중앙값/p95 |
|---|---:|---:|
| 320×240 | 28.963 / 94.175ms | 8.976 / 10.735ms |
| 1280×720 | 33.558 / 56.928ms | 9.065 / 10.581ms |

인덱스 저장 중앙값/p95: 32레코드(17,273바이트) 6.742 / 8.814ms, 300(156,051) 8.344 / 16.440ms, 1,000(518,500) 10.675 / 13.166ms. 16샘플이라 p95는 최댓값. 직전 실행의 1,000장 p95는 73.389ms라 꼬리 분포가 안정하다고 말하지 않는다.

정렬 발행 이후 조용한 샘플(`c0e65ec`+진단, 2026-09-08T15:25:15Z, 빌드·네이티브 픽스처를 멈춘 상태, 다른 앱 부하는 미통제): OCR 소유 스레드 서비스 중앙값은 0.232 / 0.940 / 1.121ms(32 / 300 / 1,000)로 줄었고, 대기 포함 벽시계 중앙값은 30.207 / 23.266 / 22.109ms로 베이스라인 UI 구간보다 길다. 문서는 “소유 스레드 해소이지 종단 가속이 아님”이라고 한다. 원본 320×240 총 벽시계 중앙값은 34.371ms(p95 122.078), 1280×720은 46.069ms(p95 47.926).

편집기 스크럽은 캡처 FPS와 별개다. `docs/releases/2.1.0-work.md`의 실제 MP4 진단: 스크럽 포인터 p95 0.107ms, UI 사이클 p99 45.296ms, exact seek p95 22.800ms. 이어진 절대 경로 진단의 exact seek p95는 29.586ms. 문장은 “픽스처 측정이며 일반 성능 보장이 아님”. 0.9.0·1.0.0의 320×240/15fps 에디터 게이트(스크럽 p95 0.065–0.082ms, UI 사이클 최대 46ms대, exact seek p95 22–25ms)는 더 이전 픽스처다.

### 녹화

**가설.** 인코드보다 GDI 프레임 획득이 길다. 기본 30fps(간격 33.3ms)에서 문서화된 캡처 중앙값·p95는 그 간격을 넘나들고, 시계는 밀린 간격을 통째로 버린다. 하드웨어 MFT 플래그는 켜져 있으나, 어떤 샘플도 실제 GPU 인코더가 선택됐는지는 적지 않는다. 3.0.0부터 기본인 커서 강조 링은 프레임마다 GDI를 더하고, 그 비용은 계측되지 않았다.

코드:

- `RegionRecorder` 이름 `MyCapture.RegionRecorder`, MTA, `ThreadPriority.BelowNormal`. 루프는 UI가 아니다. 인코더 생성·`WriteFrame`·`Complete`·`Dispose`는 그 스레드다.
- 그랩은 `RegionFrameGrabber` → `ScreenCaptureEngine.CaptureSession`. DC·비트맵을 재사용하고 프레임마다 `BitBlt(SRCCOPY|CAPTUREBLT)`와 `GetDIBits`. `CursorHighlight`가 켜지면 `DrawCursorHighlight`를 추가한다. `RecordingSettings.CursorHighlight` 기본값은 true이고 `IncludeCursor`도 true, `FrameRate` 기본값은 `Fps30`. 선택지는 10/15/24/30/60.
- `RecordingClock.TryClaimFrame`은 여러 간격이 지났으면 현재 시각의 프레임 하나만 내보낸다. `DroppedFrames`는 기대 간격과 배출의 차이다.
- 대기: `sleep >= 1`이면 `Thread.Sleep(min(sleep, 50))`. 1ms 미만이면 루프가 다시 돈다. v1.8.0 녹화 문서는 이 서브밀리초 나머지가 스핀이며 CPU를 재기 전에 케이던스를 바꾸지 말라고 했다. 그 스핀은 현재 소스에 남아 있다.
- 정지는 `RecordingControlWindow.StopRecording`이 `await Task.Run(recorder.Stop)`으로 join·파이널라이즈를 디스패처 밖으로 보낸다. 핫키 콜백은 join 뒤 `Dispatcher.InvokeAsync`로 돌아온다.
- `MediaFoundationVideoEncoder`는 `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`를 넣는다. 주석은 OS가 GPU H.264 MFT를 고르고 없으면 소프트웨어로 폴백한다고 한다. 선택된 MFT 이름을 로그로 남기는 코드는 보이지 않았다.
- 녹화 중 정지 캡처는 막지 않는다. `RequiresCaptureExclusion`이 녹화 중이면 선택기·편집기에 `WDA_EXCLUDEFROMCAPTURE`가 성공해야 창을 연다. 1.7.1 검증의 “녹화 중 스크린샷 차단”은 그 이후 공존으로 바뀌었다.
- ADR 0002의 기본 15fps·“별도 D3D 불필요”는 현재 기본 30/60과 경쟁 행렬 P0보다 낡은 설계 메모다. 구현의 기본값은 코드의 30이다.

과거 계측: `docs/performance/v1.8.0-recording-capture-evidence.md`. `--selftest-recording-performance`. 파란 정적 픽스처, 영역은 그 안. 320×240과 1280×720, 경로당 64샘플(웜 후), 준비 뒤 약 6초. CPU는 프로세스 CPU ms / 벽 ms / 16논리 프로세서 × 100. 애니메이션·혼합 DPI·30/60fps 달성을 증명하지 않는다고 명시. 다른 앱 부하는 미통제.

수락 런(`5372bab`+진단, `artifacts/validation/recording-performance-run3/`):

| 영역 | 원래 경로 중앙값/p95 | 재사용 세션 중앙값/p95 | 64회 할당 원래/재사용 | GDI 델타 |
|---|---:|---:|---:|---:|
| 320×240 | 25.263 / 41.555ms | 27.638 / 52.233ms | 6,144 / 40바이트 | 0 |
| 1280×720 | 28.981 / 45.895ms | 28.693 / 47.743ms | 6,144 / 0바이트 | 0 |

| 작업 | 프레임/길이 | 실효 fps | 드롭 간격 | 인코더 초기화 | 캡처 합/최대 | 인코드 합 | 정규화 CPU |
|---|---:|---:|---:|---:|---:|---:|---:|
| 320×240 | 132 / 6,043.471ms | 21.842 | 50/182 | 658.681ms | 3,292.729 / 61.709ms | 14.876ms | 0.228% |
| 1280×720 | 135 / 6,044.533ms | 22.334 | 47/182 | 532.501ms | 2,979.299 / 51.193ms | 135.212ms | 0.894% |
| 동시 UI, 320×240 | 105 / 5,543.546ms | 18.941 | 62/167 | 540.227ms | 4,666.137 / 138.010ms | 7.961ms | 2.798% |

캡처 벽시계가 동기 인코드보다 크다. 할당 제거를 30fps로 말하지 말라고 문서가 적는다. 디코드 프로브: 일반·제외된 선택기·제외된 편집기는 시계/오버레이 영역 변화 0픽셀, 포함 시계는 5,130픽셀.

조용한 반복(리드 UI/빌드를 멈춤, OS 부하는 여전히 미통제, 실험적 리소스 선생성은 채택하지 않음): 원래/재사용 중앙값 320에서 47.373/50.269ms, 1280에서 52.017/51.642ms. 녹화 116프레임/6,025.973ms = 19.250fps(320), 141/6,030.566ms = 23.381fps(1280). 초기화 771.001ms(320).

`docs/performance/v1.8.0-plan.md`의 통합 진단(`6abf1f9`, 디스패처를 펌프하는 하네스, 디코드 프로브 plain/selector/editor 0, 시계 5,130): 320은 149프레임/6,044.454ms = 24.651fps, 1280은 126/6,058.694ms = 20.797fps, 동시 123/5,473.227ms = 22.473fps. 같은 문단이 30fps나 지연 개선 주장을 거부한다. run1은 MP4 seek HRESULT `0xC00D36E5`, run2는 15초 준비 타임아웃으로 남아 있다.

0.9.0 검증은 실제 MF가 700ms 테스트 창보다 초기화가 길면 샘플 0으로 `MF_E_SINK_NO_SAMPLES_PROCESSED (0xC00D4A44)`가 났고, 타임스탬프 0 프레임 이후 3/3 통과했다고 적는다. 그 테스트는 `RecordingCaptureLifecycleTests.SlowInitialization_StopStillEmitsZeroFrameAndExcludesStartupFromDuration`로 남아 있다.

### 시작

**가설.** 단일 인스턴스와 백그라운드 프리웜은 갖춰져 있다. 첫 인스턴스의 UI 스레드는 설정 로드 뒤 큐 `Load()`와 동영상 복구 열거가 끝날 때까지 셸 초기화를 마치지 않는다. 포그라운드 실행은 그 다음 갤러리를 연다. 이 구간의 벽시계는 문서에 없다.

코드: `App.OnStartup`.

- OS가 Windows 11 미만이면 캡처 전에 종료(`HostRequirementGate`).
- 진단 스위치는 뮤텍스보다 먼저.
- `Local\MyCapture.SingleInstance.{…}` 뮤텍스. 두 번째 실행은 백그라운드 스위치가 아니면 `Local\MyCapture.Activate.{…}`를 올리고 종료한다. 첫 프로세스가 큰 라이브러리를 읽는 중에도 신호를 받도록, 소유권 획득 전에 이벤트를 만든다.
- `InitializeShell`: 설정 `Load` → 테마 → 트레이·핫키 서비스 → **그 다음** `StartCapturePrewarm`(대기하지 않음) → `InitializeQueue`.
- `CaptureQueue.Load`는 `index.json`을 복구 읽기하고, 스키마 1이면 사이드카 전량, 이후 스키마는 펜딩만 병합한 뒤 파일 없는 항목을 떨어뜨리고 상한을 적용한다. 인덱스 자체가 깨지면 `RebuildFromDisk()`로 폴더를 다시 읽는다(ADR 0001 안 C 폴백).
- `VideoLibraryService` 생성자가 곧바로 `RecoverAbandonedCaptureWrites`, `RecoverInterruptedCapturePublications`, `RecoverInterruptedFinalizes`, `RepairCompletedPendingCaptures`를 호출한다. 포기된 쓰기는 캡처 루트를 재귀 열거해 `VideoPending`을 찾는다.
- `--background`가 아니면 `Dispatcher.BeginInvoke`로 갤러리를 연다. OCR 모델 다운로드는 시작을 막지 않고 백그라운드이며, 네이티브 세션 생성은 첫 Accurate OCR까지 미룬다(`StartOcrModelDownload` 주석).
- 로그인 실행은 `StartupRegistrationService.ReconcileOnStartup`으로 Run 키 경로만 맞춘다.

과거 계측:

- v1.8.0 계획 문서의 시작 관찰: 설치본 PID 73812, 버전 1.7.0.0, 5초 읽기 전용 CPU 샘플 3회 각 프로세스 CPU 0ms, 워킹 셋 약 476.8MB, 핸들 1025, 스레드 19, 논리 프로세서 16. 상시 유휴 CPU를 재현하지 않았고 다른 시나리오의 건강을 증명하지 않는다고 명시.
- 1.8.4 조사 시작의 상주 프로세스: 워킹 셋 156,282,880바이트, 프라이빗 132,091,904바이트. 사용자가 말한 1GB를 재현하지 않음. 같은 문서의 20라운드 픽스처 피크 워킹 셋 824.07 → 773.76MiB, 10초 유휴 후 217.92 → 222.54MiB. 1GB 상주를 이 픽스처가 설명하지 못한다고 적는다.
- 핀 문서의 2초 유휴 샘플은 위와 같이 트레이 전체가 아니다.
- 1.8.0 검증은 패키지 업데이트 하네스가 “resident startup remains outside this harness”라고 못 박는다.

시작부터 갤러리까지, 또는 300·5000장에서의 `Load`+동영상 복구 시간은 **미확인**.

## 3. 이미 처리한 안정성과 남은 위험

처리된 쪽 (코드와 회귀 이름이 있음. 3.0.0에서 다시 실행한 결과는 아님):

- **획득을 UI 밖으로.** `CaptureOverlayCoordinatorTests.PreparingFrame_LeavesDispatcherResponsiveAndReservesOneSessionUntilCancelledCaptureDrains`, `DisposeOrDispatcherShutdown_DiscardsLateFrameWithoutWindowOrFailure`. 녹화 선택도 `RecordingSelectionPreparationTests.DelayedAcquisition_LeavesDispatcherResponsive_AndCancelDiscardsLateFrame`.
- **MP4 확정을 디스패처 밖으로.** 1.3.0 검증의 blocking join 이전. 현재는 `Task.Run(recorder.Stop)`. 종료 중 창을 닫아 중간 상태를 남기지 않는 계약은 1.3.0 문서와 `VideoLibraryService` 저널이 이어 받는다.
- **인코더 웜업 중 정지.** 타임스탬프 0 프레임. `SlowInitialization_StopStillEmitsZeroFrameAndExcludesStartupFromDuration`.
- **클립보드 `Thread.Sleep`과 큰 PNG를 UI에서 제거.** `docs/competitive-matrix.md` 1.1절. 현재 `ClipboardImageService`는 PNG를 `Task.Run`, OLE는 `StaThreadTask` 한 번. 재시도 루프를 UI에 쌓지 않는다고 주석이 적는다. WPF `Clipboard.SetDataObject` 내부의 동기 sleep은 격리 STA에 남는다.
- **STA 작업 스레드 디스패처 종료.** 1.8.4. `StaThreadTaskTests.CompletionWaitsForWorkerDispatcherShutdown`.
- **인덱스·원본·동영상 복구.** `AtomicFile.ReadAllTextWithRecovery`(`.bak`). 펜딩 원본은 `CaptureTransactionRecoveryTests.Startup_*`. 동영상은 `VideoLibraryServiceTests.PublishReadyCrashBoundary_RecoversExactMetadataAndPromotesCompletedEncoderFile`, `CompletedAbandonedPrivateEncoderOutput_IsRecoveredOnStartup`, `StartupCleansPreJournalStagesAndBlocksWhenOneCannotBeRemoved`, `FinalizeCleanupFailure_KeepsCommittedGenerationAndRecoversWithoutRollback`, `CommitMarkerRecovery_RehydratesExactNextMetadataIncludingOcrInvalidation`. 용량 축출은 복구 동안 `SuspendEviction`(`CaptureRetentionTests.RetentionDefersDuringRecoveryAndNeverTouchesQuickSaveExports`).
- **발행 순서.** `CapturePublicationTests`, `AsyncCapturePublicationTests.AcceptedOcr_CannotWriteAfterEviction_AndFinalSaveDrainsIt`. 실패해도 슬롯을 돌려주고 펜딩 마커를 유지한다.
- **단일 인스턴스**와 두 번째 실행의 갤러리 활성화 신호.
- **녹화 중 정지 캡처의 창 제외.** v1.8.0 녹화 문서의 디코드 픽셀 0(선택기·편집기) / 5,130(포함 시계).
- **핀 제스처 수명.** 1.7.1 검증이 모니터 재열거 캐시, 동일 픽셀 생략, `SWP_NOSIZE|NOZORDER|NOACTIVATE`를 적는다. 성능 문서는 실패 캡처·캡처 상실·버튼 업 없이 놓음·닫기 정리 포함 23개 핀 테스트 통과를 적는다. 그 개수는 2026-09-08 진단 기준이다.

남은 위험:

- GDI만으로는 문서화된 1280×720 정적 구간도 기본 30fps를 유지하지 못했다(실효 20–23fps, 드롭 47/182 등). 60fps 선택과 1080p/4K·애니메이션은 그 샘플에 없다. 커서 강조가 프레임마다 더해진 3.0.0은 더 느려질 수 있으나 **실측 없음**.
- 서브밀리초 페이싱 스핀의 CPU는 여전히 미측정.
- `File.Replace` 실패가 한 차례 있었다. 핀 문서: 2026-09-08T15:13:53Z, 300레코드 단계 첫 인덱스 저장, 원본 벽시계 중앙값 7.4–7.9초, 32레코드 인덱스 중앙값 909ms. 빌드·러너 장애와 겹쳤고 인과는 미증명. 조용한 재실행에서는 재발하지 않았으며 문서는 “고쳐졌다고 보지 않음”. 현재 `AtomicFile`은 `WriteThrough`와 `File.Replace(..., ignoreMetadataErrors: true)`이며 재시도 루프는 없다.
- Media Foundation 팩토리·`Finalize`는 여전히 블로킹이다. 비동기 정지가 디스패처를 살릴 뿐 COM 완료의 상한 시간은 없다(녹화 문서의 남은 작업).
- 시작 복구가 UI 스레드에서 캡처 루트를 재귀 열거한다. 손상 저널이 많거나 파일이 잠기면 트레이가 늦게 뜬다. 시간 미측정.
- 자동 OCR은 핫키와 프로세서를 공유한다. 한 장씩이어도 Windows OCR 720×360이 벽시계 약 90–200ms였고, Accurate는 그 위에 PNG 왕복·각도·병렬 인식을 얹는다. 캡처와 겹칠 때의 CPU는 **미확인**.
- 프리웜 실패는 삼키고 진행한다. 첫 핫키가 콜드 GDI+WPF 초기화를 만날 수 있다. 1.8.1 콜드 1샘플은 219.817ms(획득만, 프리웜 제외, 상주 앱의 첫 핫키가 아님).
- 1.8.4 패키지 검증 1회차는 MediaPlayer 재오픈 실패(세션 1, 22프레임)였고 원인은 미확정인 채 같은 SHA 재시도가 통과했다. 콜드 클립 재생 보장이 아니라고 적혀 있다.

## 4. 경쟁

범위는 로컬 캡처 → 설명 → GIF/MP4다. OBS의 스트리밍·오디오·다중 소스는 **범위 밖**이라 감점하지 않는다. 하드웨어 **화면 획득**은 경쟁 행렬 P0가 이미 이 제품의 캡처 과제로 올려 두었으므로 범위 밖으로 밀지 않는다.

**경쟁 우위 (상대가 더 잘하는 것)**

- ShareX·OBS는 하드웨어 지향 캡처(Desktop Duplication / Windows Graphics Capture 계열)와 하드웨어 인코드를 제품 경로로 다룬다. 경쟁 행렬은 ShareX를 “유연한 FFmpeg”, OBS를 “하드웨어 인코딩 중심의 전문 녹화”로 적고, MyCapture 칸에 “고부하 캡처 백엔드가 필요”라고 적는다. 코드는 획득이 GDI이고, 인코드만 MF 하드웨어 변환 플래그다. 플래그가 실제 GPU 캡처가 된 증거는 없다.
- Snipaste의 가벼움은 경쟁 행렬이 핀 생산성의 기준으로 둔다. MyCapture는 상주 WPF이고, 문서화된 워킹 셋은 1.7.0 관찰 약 476.8MB, 1.8.4 조사 시 약 149MiB, 4K 합성 픽스처 피크 약 774–824MiB다. 조건이 서로 달라 하나의 “메모리 점수”로 묶지 않는다. Snipaste의 MB를 이 세션에서 재지 않았으므로 상대 숫자는 적지 않는다.
- 이 GDI 잔존은 경쟁 행렬 P0와 일치한다. P0 문구는 “현재 GDI 경로 대비 1080p/4K에서 CPU·프레임 드롭·복구가 계측상 개선되고 Windows 11 22000 실기기 통과”다. 1080p/4K **녹화** 드롭 표는 문서에 없고, Graphics Capture·Duplication 타입도 없다. P0는 미구현이다.

**과제 고유 강점**

- 계정·업로드 없이 Media Foundation만으로 MP4를 만들고, 드롭 수·실효 FPS를 로그와 편집기에 남긴다(`RecordingResult`, `VideoEditorWindow`의 녹화 건강 문구).
- 정지 캡처·클립보드 PNG·MP4 확정·썸네일 디코드를 UI 스레드 밖으로 빼 두었다.
- 녹화 실패 창(인코더 완료와 발행 사이, 파이널라이즈 저널, 인덱스 `.bak`)을 시작 시 복구한다. 축출은 그 구간을 피한다.
- 창 제외로 녹화 중 정지 캡처가 컨트롤을 영상에 넣지 않게 하는 계약과 픽셀 프로브가 있다(v1.8.0, 정적 시계 영역).

**공통 약점**

- 소프트웨어 캡처가 목표 프레임보다 느리면 프레임을 버리는 문제는 범용 녹화도 겪는다. MyCapture는 버린 간격을 실시간에 맞춰 타임스탬프만 늘린다.
- 레이어드 창을 빠뜨리지 않으려 `CAPTUREBLT`를 항상 켠다. DWM·보안 데스크톱·권한 경계에서 GDI가 비는 문제는 다른 GDI 캡처도 공유한다. 그 실패율은 **미확인**.

**범위 밖**

- OBS급 스트리밍, 오디오, 다중 소스, 장면 전환. ADR 0002와 1.3.0 검증이 마이크·시스템 사운드를 의도적으로 빼 두었다. 감점하지 않는다.
- FFmpeg 번들은 ADR 0002가 설치 표면 때문에 기각했다. “FFmpeg가 없다”를 성능 감점으로 쓰지 않는다. 감점 대상은 그 대체 경로인 GDI 획득이 자기 30fps를 문서상 못 지킨 것이다.

## 5. 보완

기능이 아예 없으면 functionality, 느려서 캡처·녹화·시작 흐름이 끊기면 convenience와 OVERLAP. 아래 주 소유는 성능·안정성으로 제안한다.

### P0

**Windows Graphics Capture 또는 Desktop Duplication 스파이크.** 경쟁 행렬 P0와 동일한 승격 조건: 현재 GDI 대비 1080p/4K CPU·드롭·복구가 계측으로 나아지고 Windows 11 22000 실기기 통과. 기본 30fps와 선택 60fps는 그 계측 전에는 목표 달성으로 적지 않는다. 커서 강조 기본 켜짐을 같은 조건에 넣어야 3.0.0과 맞다.
주 소유: performance. OVERLAP: functionality(백엔드 부재), convenience(선택 화면·녹화 흐름이 끊김).

### P1

**3.0.0 재계측.** 과거 표는 1.8–2.1이다. 같은 기계에서 조건부 100ms, 커서 강조 on/off, 320·1280·1080p·4K, 목표 30과 60, 프로세스 CPU, 드롭, 핫키→`ContentRendered`를 남겨야 점수 3을 올리거나 내릴 근거가 생긴다. 이번 세션에서는 불가.
주 소유: performance. OVERLAP: convenience(체감 대기).

**시작 경로에서 큐 로드와 동영상 복구 열거를 UI 밖으로.** 실패 시 트레이는 뜨고 복구 실패는 라이브러리에 표시. 상한 5000과 재귀 `VideoPending` 열거가 로그온을 막는 시간을 측정할 것.
주 소유: performance. OVERLAP: convenience(실행 직후 갤러리). trust는 데이터 유실이 아니라 복구 지연만 겹친다.

**캡처가 프레임 간격보다 길 때의 기본값.** 문서화된 1280p95는 45–52ms로 30fps 간격 33.3ms를 넘는다. 백엔드 전환 전까지는 기본 15fps로 낮추거나, 드롭률이 쌓이면 편집기에 이미 있는 건강 문구를 녹화 중에도 보이게 하는 쪽이 정직하다. 기능 추가는 아니고 페이싱 정책이다.
주 소유: performance. OVERLAP: convenience.

### P2

**녹화 루프의 1ms 미만 스핀 CPU를 측정한 뒤 대기 방식으로 교체.** `Thread.Sleep`은 UI가 아니라 녹화 스레드에 남아 있다. UI 클립보드 sleep 제거와 혼동하지 말 것.
주 소유: performance.

**`File.Replace` 실패의 HRESULT를 제품 로그에 남기고, 조용한 재실행으로 고쳤다고 보지 않기.** 진단은 이미 HRESULT를 남기게 바뀌었고 제품 `AtomicFile`은 재시도하지 않는다.
주 소유: performance. OVERLAP: trust(인덱스 손상 가능성). 한 번의 미설명 실패이므로 P0로 올리지 않는다.

**PP-OCR 병렬의 전후 시간.** 3.0.0 노트는 영수증처럼 줄이 많은 이미지에서 인식 단계를 코어 수만큼 병렬화했다고 한다. 코드와 일치하나 밀리초 표는 없다. 색인은 여전히 한 장씩이다.
주 소유: performance. OVERLAP: functionality(Accurate/Enhanced라는 기능은 이미 있음).

## 6. 미확인

- 이번 세션의 FPS, CPU, 워킹 셋, 핫키 지연, 혼합 DPI, 장시간 트레이 유휴. Linux라 실행하지 못했다.
- Release 3.0.0(`117b0ef`)에서의 어떤 타이밍이든. 인용한 숫자는 전부 이전 검증이다.
- 1080p/4K 녹화 드롭, 애니메이션 소스, 커서 강조의 프레임당 비용, 60fps 실효 FPS.
- `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`가 측정 기계에서 GPU MFT를 골랐는지.
- 조건부 100ms가 1.9.0 요청→렌더(약 200–282ms, 4806×2466 픽스처)에서 얼마를 걷어 내는지.
- 300장·5000장·동영상 저널이 있는 라이브러리의 콜드 스타트.
- `File.Replace` 실패의 원인. 재발하지 않은 것이 수정은 아니다.
- PP-OCR 병렬의 전후 시간, Accurate 모드가 캡처 핫키와 겹칠 때의 CPU.
- Snipaste·ShareX·OBS의 동일 장비 숫자. 공식 페이지를 이번 세션에 열어 가격·벤치마크를 가져오지 않았다.
- ADR 0002의 “4K 수십 ms”의 원래 실험 조건.
- 코드 주석의 첫 3440×1440 = 827ms, 이후 21–51ms의 샘플 설계.
- DWM 보안 데스크톱·권한 상승 창에서 GDI가 비는 비율.
- 패키지 self-test 7종이 3.0.0 태그에서 다시 통과했는지. 릴리스 노트가 설치형·포터블과 `SHA256SUMS.txt`를 말할 뿐, 이 평가가 CI를 다시 돌리지는 않았다.

## 테스트·진단 이름 (성능·복구·드롭)

실행하지 않았다. 이름만 코드에서 확인했다.

- 드롭·시계: `RecordingDomainTests`( `TryClaimFrame`, `MillisecondsUntilNextFrame` ), `RecordingFeatureTests.RecordingResult_ReportsAdaptiveFrameDropMetrics`, `RecordingResult_DropMetricsNeverReportNegativeValues`.
- 녹화 수명: `RecordingCaptureLifecycleTests.SlowInitialization_StopStillEmitsZeroFrameAndExcludesStartupFromDuration`, `RecordingSelectionPreparationTests.DelayedAcquisition_LeavesDispatcherResponsive_AndCancelDiscardsLateFrame`.
- 캡처 디스패처: `CaptureOverlayCoordinatorTests.PreparingFrame_LeavesDispatcherResponsiveAndReservesOneSessionUntilCancelledCaptureDrains`.
- 복구: `CaptureTransactionRecoveryTests.Startup_PendingOriginalMergesIntoValidIndexAndRepairsDerivedFiles` 외 `Startup_*`, `StorageAndSettingsTests.ReadAllTextWithRecovery_*`, `VideoLibraryServiceTests`의 `PublishReadyCrashBoundary_*`, `CompletedAbandonedPrivateEncoderOutput_IsRecoveredOnStartup`, `StartupCleansPreJournalStagesAndBlocksWhenOneCannotBeRemoved`.
- 발행: `CapturePublicationTests`, `AsyncCapturePublicationTests`.
- 썸네일: `GalleryTests.ThumbnailCache_IsBounded_AndHideReleasesSourcesUntilReopened`, `Realization_OffscreenTileDoesNotRequestThumbnail`.
- 축출: `DomainTests.Eviction_*`, `CaptureRetentionTests.RetentionDefersDuringRecoveryAndNeverTouchesQuickSaveExports`.
- 진단 스위치: `--selftest-recording-performance`, `--selftest-pin-storage-performance`, `--selftest-capture-performance`(후자는 주입 지연 픽스처).
