# functionality inbox

라운드 1 주장. 다른 축은 주 소유가 정해지기 전에 같은 사실을 감점하지 않는다.

| claim_id | 주장 | 제안 주 소유 축 | 겹치는 축 | 근거 경로 |
|---|---|---|---|---|
| F-01 | 기능 깊이 점수는 4/5다. 캡처→벡터 주석 재편집→라이브러리→무음 영역 녹화→비파괴 트림/GIF의 척추는 닫혀 있고, 형광펜·번호·블러·핀 복구·GIF 20초·영상 주석의 PNG화가 설명 흐름을 끝에서 끊는다. | functionality | positioning | `src/MyCapture.App/Editing/`, `src/MyCapture.Core/Queue/CaptureRecord.cs`, `src/MyCapture.Core/Recording/VideoEditDocument.cs`, `src/MyCapture.App/Recording/AnimatedGifExporter.cs` |
| F-02 | 모자이크 도구는 구현되어 있다. 경쟁 매트릭스의 “모자이크 UI를 추가해야 한다”는 3.0.0에서 틀리다. | functionality | positioning | `src/MyCapture.App/Editing/EditorTool.cs`, `AnnotationEditorControl.cs` `ApplyMosaic`, `docs/competitive-matrix.md` |
| F-03 | 모자이크 설정 `MosaicBlockSize`는 제스처에 연결되지 않는다. 마우스 업은 블록 14를 넘기고, 적용부는 4–64로 다시 자른다. | functionality | learnability | `AnnotationEditorControl.cs` 614행 근처 `blockSize: 14`, `src/MyCapture.Core/Settings/AppSettings.cs`, `SettingsWindow.xaml` |
| F-04 | 형광펜은 도메인·렌더·설정만 있고 편집기 도구가 `IsHighlighter`를 켜지 않는다. 설정 알파는 그리기에 쓰이지 않는다. | functionality | learnability | `src/MyCapture.Core/Annotations/PenAnnotation.cs`, `AnnotationRenderer.cs`, `tests/MyCapture.Core.Tests/DomainTests.cs` |
| F-05 | 가우시안 블러와 번호 배지·녹화 단계 마커는 미구현이다. | functionality | positioning | `src/` 전체 검색에 편집용 Blur/번호 배지/단계 마커 없음 |
| F-06 | 녹화 중 커서 강조 링은 3.0.0에 구현되어 있고 기본이 켜져 있다. 매트릭스 P1이 클릭 강조를 아직 없는 기능으로 묶은 것은 틀리다. 링 픽셀 테스트는 없다. | functionality | performance | `src/MyCapture.Platform/Capture/ScreenCaptureEngine.cs` `DrawCursorHighlight`, `RecordingSettings.CursorHighlight`, `docs/releases/3.0.0-release-notes.md` |
| F-07 | 닫은 핀 복구는 동작하지 않는다. `ClosedWindowRestoreLimit`는 저장만 되고 `PinManager`와 설정 창이 읽지 않는다. | functionality | convenience, learnability | `src/MyCapture.Core/Settings/AppSettings.cs` `PinSettings`, `src/MyCapture.App/Pinning/PinManager.cs` |
| F-08 | 핀 회전·반전은 없다. 정지 편집기의 90도 캔버스 회전은 있다. 매트릭스의 “회전/반전 미구현”을 제품 전체 회전 부재로 읽으면 안 된다. | functionality | positioning | `AnnotationDocumentRotator.cs`, `AnnotationEditorControl.RotateCapture`, `PinWindow.cs` |
| F-09 | 얼굴 감지, APNG, 플러그인 파이프라인은 없다. 오디오 부재는 인코더에 오디오 타입이 없고 매트릭스가 범위 밖으로 명시한 의도다. 기능 점수에서 오디오를 감점하지 않는다. | functionality | positioning, trust | `MediaFoundationVideoEncoder.cs`, `docs/competitive-matrix.md`, `src/`에 Face/APNG/IPlugin 없음 |
| F-10 | 전문 검색은 이미지 색인까지만 닫힌다. `OcrIndexingService`는 영상을 빼고 회전 검색을 끈다. ADR 0003의 갤러리 버튼 배선은 현재 있다. | functionality | performance | `src/MyCapture.App/Ocr/OcrIndexingService.cs`, `GalleryWindow.xaml.cs` `OnOcrIndexClick`, `docs/adr/0003-capture-fulltext-search.md` |
| F-11 | 영상 트림·시간 텍스트는 원본 MP4를 유지한다. 프레임에서 연 주석과 도형 레이어는 PNG라 벡터 재편집이 안 된다. 프레임 편집 레이어의 초기 길이는 한 프레임이고, 도형 레이어는 약 3초다. | functionality | convenience | `VideoLibraryService.CommitEditAsync`, `VideoEditorWindow.AddFrameEditLayer`, `VideoLayerAssets.CreateLayer` |
| F-12 | GIF 내보내기는 20초, 긴 변 960, 5/10fps, 속도 0.25–4배에서 멈춘다. 그 밖은 예외다. | functionality | performance | `AnimatedGifExporter.cs` `MaximumDurationMs`, `GifExportQuality.cs` |
| F-13 | GitHub 이미지 URL의 현재 기본 단축키는 F9다. 순정 F4만 F9로 이주하고 사용자 조합의 F4는 남는다. | functionality | learnability | `AppSettings.cs` `UploadGitHubImage`, `SettingsStore.cs`, `tests/MyCapture.App.Tests/RecordingFeatureTests.cs` |
| F-14 | 창 후보 서비스와 `AutoDetectWindows`, `ColorFormat`은 영역 선택 워크플로에 연결되지 않는다. 돋보기는 헥스를 표시만 한다. 지정 크기 정지 캡처는 서비스·테스트만 있고 셸 진입점이 없다. 녹화 폭·높이 프리셋은 연결되어 있다. | functionality | convenience | `WindowCandidateService.cs`, `CaptureOverlayView.cs`, `AdvancedCaptureService.CaptureFixedSize`, `RegionRecordingCoordinator.cs` |
| F-15 | 캡처 백엔드 교체(WGC/Desktop Duplication)는 기능 축 P0이 아니다. 영역 캡처와 녹화는 GDI로 이미 동작한다. | performance | functionality | `ScreenCaptureEngine.cs`, `docs/adr/0002-region-recording.md`, `docs/competitive-matrix.md` 다음 우선순위 P0 |
| F-16 | Accurate/Enhanced OCR의 PP-OCR·초해상은 모델이 없으면 다운로드하거나 Windows OCR로 떨어진다. 오프라인에서 그 품질 단계가 항상 같은 엔진으로 끝나지는 않는다. | functionality | trust | `src/MyCapture.Ocr/OcrModelStore.cs`, `CaptureOcrService.cs`, `OcrQualityProfile` |
