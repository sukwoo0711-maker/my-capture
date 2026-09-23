# inbox — performance (라운드 1)

대상 커밋 `117b0ef`. 이번 세션 실측 없음. 숫자는 과거 검증 문서에만 있고 조건은 `round1/05-performance.md`에 있다.

| claim_id | 주장 | 주 소유 축 | 겹치는 축 | 근거 |
|---|---|---|---|---|
| perf-gdi-only | 정지·녹화 캡처 백엔드는 GDI `BitBlt + CAPTUREBLT + GetDIBits`뿐이고, Graphics Capture·Desktop Duplication 구현은 없다. 경쟁 행렬 P0(1080p/4K에서 GDI 대비 CPU·드롭·복구 개선, Windows 11 22000 실기기)는 미구현이다. | performance | functionality, convenience | `ScreenCaptureEngine.cs`, `RegionFrameGrabber.cs`, 저장소 검색, `docs/competitive-matrix.md` P0 |
| perf-still-full-desktop | 영역 선택 전에 가상 데스크톱 전체를 워커에서 읽는다. UI 스레드는 비우되, 프레임이 붙기 전까지 선택기가 기다린다. 1.9.0 픽스처(4806×2466, 트레이·물리 핫키 제외) 요청→렌더는 199–282ms대다. 3.0.0의 조건부 100ms는 그 표에 없다. | performance | convenience | `CaptureOverlayCoordinator.cs`, `CaptureVirtualDesktop`, `docs/releases/1.9.0-validation.md`, `1.8.1-validation.md` |
| perf-record-below-30 | 기본 목표 30fps인데, 2026-09-08 정적 픽스처(320·1280, 약 6초, 16논리 프로세서) 실효 FPS는 약 19–25이고 드롭이 남았다. 캡처 벽시계가 인코드보다 컸다. 30fps 달성으로 인용하면 안 된다. 1080p/4K·애니메이션·커서 강조 비용은 미계측. | performance | convenience | `RecordingSettings.FrameRate` 기본 `Fps30`, `RecordingClock`, `docs/performance/v1.8.0-recording-capture-evidence.md`, `v1.8.0-plan.md` 통합 진단 |
| perf-hw-encode-flag | MF Sink Writer에 하드웨어 변환 플래그가 있다. 선택된 MFT가 GPU였는지는 로그·계측에 없다. 화면 획득은 하드웨어 경로가 아니다. OBS 스트리밍·오디오는 범위 밖이라 이 주장의 감점 근거가 아니다. | performance | functionality | `MediaFoundationVideoEncoder.cs` `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`, ADR 0002, 경쟁 행렬 |
| perf-cursor-highlight | 3.0.0 녹화는 커서 강조 링이 기본이며 프레임마다 GDI를 추가한다. 드롭에 미치는 시간은 미확인. | performance | convenience | `RecordingSettings.CursorHighlight`, `CaptureSession.CaptureInto`, `docs/releases/3.0.0-release-notes.md` |
| perf-pin-drag | 드래그 중 모니터 재열거는 제스처 시작 시 한 번 캐시하고, 같은 픽셀은 건너뛴다. 2026-09-08 합성 입력·실제 HWND(OS 포인터 제외) 콜백 중앙값은 약 1.0–1.2ms다. 60Hz 마우스가 증명된 것은 아니다. | performance | convenience | `PinWindow.cs`, `docs/performance/v1.8.0-pin-storage-evidence.md`, `docs/releases/1.7.1-validation.md` |
| perf-clipboard-off-ui | 큰 PNG 인코드와 클립보드 OLE는 UI 디스패처 밖이다. UI에 쌓이던 재시도 `Thread.Sleep`은 제거됐다. OLE STA 안의 WPF 내부 대기는 남을 수 있다. | performance | convenience | `ClipboardImageService.cs`, `docs/competitive-matrix.md` 1.1절 |
| perf-ocr-serial | 자동 색인은 한 장씩, 회전 검색 없이, ApplicationIdle에서 돈다. 3.0.0 병렬은 PP-OCR 한 장 안의 인식 단계이고 기본 품질은 Fast(Windows OCR)다. 병렬 전후 밀리초는 문서에 없다. | performance | functionality | `OcrIndexingService.cs`, `NeuralOcrEngine.cs`, `OcrQuality.cs`, `App.RequestAutomaticIndexing`, 3.0.0 노트 |
| perf-ocr-png | 인식 전에 PNG 인코드(Windows OCR은 `Task.Run`, PP-OCR은 인코드 후 SKBitmap 디코드)가 있다. 720×360 Windows OCR 6샘플 벽시계 중앙값은 텍스트 115.475ms, 공백 91.305ms(2026-09-08, 색인 모드). | performance | functionality | `WindowsOcrRecognizer.cs`, `NeuralOcrEngine.cs`, 핀 저장 증거 문서 |
| perf-thumb-bound | 갤러리 썸네일 디코드는 동시 2, 캐시 16MiB 또는 64장. 이미지 썸네일 긴 변 기본 320·JPEG 82. 동영상 썸네일은 640. 화면 밖 타일은 요청하지 않는다. | performance | aesthetics | `GalleryThumbnailLoader.cs`, `GalleryViewModel.cs`, `ImageCodec.cs`, `VideoLibraryService.cs`, `GalleryTests.ThumbnailCache_IsBounded_*` |
| perf-queue-caps | 기본 300장·2GiB, 설정 상한 5000장·512GiB. 인덱스 전량 메모리. 발행 슬롯 8. 1,000레코드 인덱스 저장 중앙값 10.675ms(16샘플, p95=최댓값 13.166ms, 같은 날 다른 런 p95 73.389ms). | performance | functionality | ADR 0001, `QueueSettings`, `CaptureQueue._publicationSlots`, 핀 저장 증거 |
| perf-startup-sync | 첫 인스턴스 UI 스레드가 `CaptureQueue.Load`와 동영상 복구 재귀 열거를 끝낸 뒤에야 셸이 끝난다. 포그라운드면 이어서 갤러리를 연다. 이 구간의 시간은 미측정. 프리웜·OCR 모델 다운로드는 대기하지 않는다. | performance | convenience | `App.OnStartup`, `InitializeQueue`, `VideoLibraryService` 생성자 |
| perf-single-instance | 세션 뮤텍스 하나. 두 번째 실행은 활성화 이벤트만 올리고 종료한다. | performance | convenience | `App.xaml.cs` `SingleInstanceMutexName`, `ActivationEventName` |
| perf-recovery | 인덱스 `.bak`, 펜딩 원본, 동영상 발행·파이널라이즈 저널을 시작 시 복구하고, 그 동안 용량 축출을 미룬다. | performance | trust | `AtomicFile`, `CaptureQueue.Load`, `VideoLibraryService.Recover*`, `CaptureTransactionRecoveryTests.Startup_*`, `VideoLibraryServiceTests` |
| perf-finalize-off-dispatcher | MP4 종료 join·파이널라이즈는 `Task.Run`이다. COM 완료 상한 시간은 없다. | performance | convenience | `RecordingControlWindow.StopRecording`, `docs/releases/1.3.0-validation.md`, 녹화 증거 문서 잔여 작업 |
| perf-recorder-spin | 녹화 스레드만 `sleep >= 1`일 때 `Thread.Sleep`하고, 1ms 미만은 스핀한다. UI sleep 제거와 별개이며 CPU는 미측정. | performance | — | `RegionRecorder.CaptureLoop`, `docs/performance/v1.8.0-recording-capture-evidence.md` |
| perf-file-replace | 2026-09-08 인덱스 `File.Replace` 실패 1건은 원인 미해결이다. 조용한 재실행에서 재발하지 않은 것을 수정으로 치지 않는다. | performance | trust | `docs/performance/v1.8.0-pin-storage-evidence.md`, `AtomicFile.WriteAllBytesCore` |
| perf-editor-seek | 2.1.0 실제 MP4 픽스처의 exact seek p95는 22.800ms와 29.586ms, 스크럽 포인터 p95 0.107ms다. 일반 성능 보장이 아니라고 문서가 적는다. 캡처 FPS와 섞지 말 것. | performance | convenience | `docs/releases/2.1.0-work.md` |
| perf-scope-av | 스트리밍·마이크·시스템 사운드·다중 소스는 의도적 비범위다. 성능 점수에서 감점하지 않는다. | performance | positioning | ADR 0002, `docs/releases/1.3.0-validation.md` §4, `docs/competitive-matrix.md` |
