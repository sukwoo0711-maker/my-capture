# 기능 깊이 — 라운드 1

대상: Release 3.0.0, 커밋 `117b0ef`. 축: functionality.
근거는 이 세션에서 연 소스·테스트·ADR·`docs/competitive-matrix.md`와, 아래에서 URL을 적은 공식 페이지다. `docs/market/`는 인용하지 않았다. 다른 라운드 1 산출물은 읽지 않았다.

## 1. 점수

**4 / 5.**

로컬 캡처에서 설명용 정지 화면, 라이브러리, 무음 영역 녹화, GIF/MP4까지 이어지는 척추는 코드와 테스트로 닫혀 있다. 주석은 래스터로 구워 버리지 않고 `layers.json`으로 남고, 영상 트림·시간 텍스트는 `source.mp4`를 바꾸지 않는 편집 문서다. 핀은 이미지와 텍스트·표를 구분하고, 민감정보 가리기는 되돌릴 수 있는 사각형 레이어다.

5가 아닌 이유는 설명 워크플로가 도구 몇 개에서 끊기기 때문이다. 형광펜·번호 배지·가우시안 블러는 편집기 도구가 아니다. 모자이크 도구는 있으나 설정 블록 크기를 쓰지 않고 픽셀을 이미지 패치로 굳힌다. 닫은 핀 복구는 설정 필드만 있다. GIF는 20초를 넘기면 내보내기가 거부된다. 영상 위 그림 레이어는 벡터 주석이 아니라 PNG다.

3이 아닌 이유는 공백이 “핵심 경로가 비어 있음”이 아니라, 이미 닫힌 경로 옆의 특정 도구·설정 계약이기 때문이다.

## 2. 기능 맵

### 구현됨, 테스트 있음

| 기능 | 경로 | 테스트 |
|---|---|---|
| 영역 선택. 가상 데스크톱 물리 픽셀, 드래그 확정, Esc 취소, Ctrl+A는 그 프레임 전체 | `src/MyCapture.App/Capture/CaptureOverlayCoordinator.cs`, `CaptureOverlayView.cs`, `src/MyCapture.Platform/Capture/ScreenCaptureEngine.cs` (`CaptureVirtualDesktop`) | `tests/MyCapture.App.Tests/CaptureOverlayCoordinatorTests.cs` |
| 창 캡처. 커서 아래 창 경계, 모니터를 가로지르면 한 사각형으로 자름 | `src/MyCapture.App/Capture/AdvancedCaptureService.cs` `CaptureWindow` | `tests/MyCapture.App.Tests/AdvancedCaptureServiceTests.cs` |
| 전체 캡처. 커서 아래 모니터 한 장. 모든 모니터가 아님 | 같은 파일 `CaptureFullScreen` | 같은 테스트, `src/MyCapture.App/Diagnostics/AdvancedCaptureSelfTest.cs` |
| 지연 캡처. 카운트다운 창을 닫은 다음 영역 캡처 | `src/MyCapture.App/App.xaml.cs` `HandleDelayedCapture`, `Capture/CountdownWindow.cs` | `tests/MyCapture.App.Tests/CountdownWindowAccessibilityTests.cs` |
| 스크롤 스티치. 겹침 검증, 고정 헤더, 높이·바이트 상한. 셸은 창 클라이언트에 휠 3칸, 최대 40프레임 | `src/MyCapture.Core/Capture/ScrollStitcher.cs`, `AdvancedCaptureService.CaptureScrollingAsync`, `App.xaml.cs` `HandleScrollingCapture` | `tests/MyCapture.Core.Tests/AdvancedCaptureCoreTests.cs` |
| 이전 영역 재생과 DPI·모니터 원점 재매핑 | `src/MyCapture.Core/Capture/LastRegionStore.cs` | 같은 Core 테스트 `RegionHistoryEntry_RemapsOriginAndDpiThenClamps` |
| 혼합 DPI 가상 데스크톱 좌표. 음수 원점, 물리 픽셀 유지 | `ScreenCaptureEngine.cs` 주석, `FrozenFrame.ToBitmapSpace` | `tests/MyCapture.App.Tests/MultiMonitorRecordingLayoutTests.cs`, `ScreenCaptureReadbackTests.cs` |
| 주석: 선택, 사각형, 화살표, 펜, 텍스트, 이미지 삽입, 이동·크기, Undo/Redo | `src/MyCapture.App/Editing/EditorTool.cs`, `AnnotationEditorController.cs`, `src/MyCapture.Core/Annotations/` | `tests/MyCapture.App.Tests/AnnotationEditorControllerTests.cs`, `ShapeRenderingTests.cs`, `tests/MyCapture.Core.Tests/DomainTests.cs`, `ShapeStyleTests.cs` |
| 캔버스 90도 회전. 비트맵과 레이어를 함께 돌리고 한 번의 Undo | `AnnotationEditorControl.RotateCapture`, `src/MyCapture.Core/Annotations/AnnotationDocumentRotator.cs` | `AnnotationEditorControllerTests.cs`, `AnnotationDocumentRotatorTests.cs`, `EditorRotationAndTextTests.cs` |
| 레이어 JSON 재편집. `original.png` + `layers.json` + `rendered.png` | `src/MyCapture.Core/Queue/CaptureRecord.cs` `CaptureFileNames`, `src/MyCapture.App/Gallery/GalleryReeditLoader.cs`, `Editing/CapturePersistenceService.cs` | `tests/MyCapture.App.Tests/GalleryTests.cs`, `GalleryEditorWindowTests.cs` |
| 핀: 이미지, 일반 텍스트, 탭 구분 표. 빈 셀 유지, Ctrl+C는 렌더 이미지, Ctrl+더블클릭은 원문 | `src/MyCapture.App/Pinning/PinContent.cs`, `ClipboardTextRenderer.cs`, `PinWindow.cs`, `PinManager.cs` | `PinManagerTests.cs`, `ClipboardTextRendererTests.cs`, `ClipboardImageReaderTests.cs` |
| 핀 이동·확대·투명도·클릭 통과·전체 숨김·PNG 저장 | `PinWindow.cs`, `src/MyCapture.Core/Pin/PinViewState.cs`, `PinGeometry.cs`, `src/MyCapture.Platform/Display/WindowStyleFacade.cs` | `PinViewStateTests.cs`, `PinGeometryTests.cs`, `PinHitTestingTests.cs`, `PinDragLifecycleTests.cs`, `PinImageSaveServiceTests.cs` |
| 핀 OCR 요청과 결과 창 | `PinWindow.RequestOcr`, `src/MyCapture.App/Ocr/OcrResultPresenter.cs` | `PinTextCopyServiceTests.cs`, `OcrTests.cs` |
| OCR. Windows OCR, 품질에 따라 PP-OCRv5와 Real-ESRGAN. 실패 시 Windows OCR 폴백 | `src/MyCapture.Ocr/CaptureOcrService.cs`, `WindowsOcrService.cs`, `NeuralOcrEngine.cs`, `OcrQuality.cs` | `tests/MyCapture.App.Tests/OcrTests.cs`, `SuperResolutionEngineLiveTests.cs` |
| 빠른 가리기. 이메일·한국 전화·주민번호 형태·Luhn 카드·IPv4·일부 비밀 토큰. 평문 대신 좌표, 불투명 사각형을 한 Undo 배치로 추가 | `src/MyCapture.Core/Privacy/PrivacyDetector.cs`, `src/MyCapture.App/Ocr/PrivacyRedactionService.cs`, `AnnotationEditorController.AddPrivacyRedactions` | `PrivacyDetectionTests.cs`, `PrivacyRedactionServiceTests.cs`, `AnnotationEditorControllerTests.cs` |
| 라이브러리 검색. 제목·창 제목·OCR의 다중어 AND. 갤러리 색인 버튼 | `src/MyCapture.Core/Queue/CaptureTextSearch.cs`, `src/MyCapture.App/Ocr/OcrIndexingService.cs`, `Gallery/GalleryWindow.xaml.cs` | `CaptureTextSearchTests.cs`, `OcrIndexingAndAvailabilityTests.cs`, `GalleryTests.cs` |
| 이미지/MP4 보관. 개수 300·2GiB, 이미지는 기본 168시간 후 만료, 핀·영상·내보내기 파일은 그 만료에서 제외 | `src/MyCapture.Core/Queue/CaptureQueue.cs`, `CaptureRetention.cs` | `CaptureRetentionTests.cs`, `CaptureQueueVideoTests.cs`, `CaptureQueueBatchRemovalTests.cs` |
| 영역 녹화. Media Foundation H.264 MP4, 10/15/24/30/60fps, 적응 드롭, 시작 지연, 커서 포함 | `docs/adr/0002-region-recording.md`, `src/MyCapture.Core/Recording/RecordingSettings.cs`, `src/MyCapture.Platform/Recording/`, `App/Recording/RegionRecordingCoordinator.cs` | `RecordingDomainTests.cs`, `RecordingScenarioTests.cs`, `RecordingFeatureTests.cs`, `RecordingCaptureLifecycleTests.cs` |
| 비파괴 트림·시간 텍스트·프레임/도형/이미지 레이어. `source.mp4` 유지, `video-edits.json`과 `rendered.mp4`만 교체. 편집기 Undo 50단계 | `src/MyCapture.Core/Recording/VideoEditDocument.cs`, `VideoLibraryService.CommitEditAsync`, `VideoEditorWindow.cs`, `docs/adr/0004-video-editor-responsive-preview.md` | `VideoEditDocumentTests.cs`, `TextLayerTimingTests.cs`, `VideoLibraryServiceTests.cs`, `VideoEditorWindowTests.cs`, `VideoCompositionIntegrationTests.cs`, `TimelineRenderingTests.cs`, `PreviewSeekCoordinatorTests.cs` |
| GIF. 트림 구간, 텍스트·프레임 레이어 합성, 속도 0.25–4배, 프리셋 960/10fps·640/10·480/5. 20초 초과는 예외 | `src/MyCapture.App/Recording/AnimatedGifExporter.cs`, `GifExportQuality.cs` | `VideoCompositionIntegrationTests.cs`, `AnimatedGifWriterTests.cs`, `VideoExportCalculationTests.cs` |
| GitHub 첨부 URL. 기본 키는 F9. 저장값이 순정 F4이면 F9로 옮기고, Ctrl+F4 같은 사용자 조합은 유지. 토큰이 있으면 REST, 실패하면 이슈 페이지에 붙여 넣고 URL을 기다림 | `src/MyCapture.Core/Settings/AppSettings.cs` `UploadGitHubImage`, `SettingsStore.cs`, `src/MyCapture.Core/GitHub/GitHubIssueImageUrl.cs`, `App/GitHub/` | `RecordingFeatureTests.cs` `UploadGitHubImageHotkey_DefaultsToF9`, `StorageAndSettingsTests.cs`, `GitHubIssueImageUrlTests.cs`, `GitHubTokenUploadServiceTests.cs`, `GitHubIssueImageUploadServiceTests.cs` |
| 녹화 크기 프리셋. 설정 폭·높이가 있으면 선택 오버레이가 그 크기로만 이동 | `RecordingSettings.PresetWidth`, `RegionRecordingCoordinator.cs`, `CaptureOverlayView.PresetSize` | 설정 매핑은 `SettingsDraftTests.cs`. 오버레이 배치 수학은 고정 영역 테스트와 별개 |

### 부분 구현

| 기능 | 실제로 닫히는 범위 | 끊기는 지점 | 경로 |
|---|---|---|---|
| 모자이크 | 편집기 도구 `M`이 영역 평균 픽셀 패치를 `ImageAnnotation`으로 추가하고 Undo된다 | 마우스 업이 `blockSize: 14`를 고정한다. 설정 `MosaicBlockSize`(기본 12, 범위 2–128)는 저장·설정 화면만 거친다. `ApplyMosaic`은 다시 4–64로 클램프한다. 패치라서 블록 크기를 나중에 바꿀 수 없고, 이동하면 아래 원본이 다시 보인다 | `AnnotationEditorControl.cs` `ApplyMosaic`, `SettingsWindow.xaml` 주석 탭, `AppSettings.cs` |
| 형광펜 | `PenAnnotation.IsHighlighter`와 렌더러의 알파 상한 110, 설정 `HighlighterAlpha` | 편집기가 `IsHighlighter = true`를 넣는 곳이 없다. 테스트의 도메인 왕복만 있다. 설정 알파는 그리기에 쓰이지 않는다 | `PenAnnotation.cs`, `AnnotationRenderer.cs`, `DomainTests.cs` |
| 타원 | JSON 종류 `ellipse`와 거리·렌더 | 정지 편집기 `EditorTool`에 타원이 없다. 영상 편집기는 별도의 래스터 타원 레이어를 약 3초 구간으로 추가한다 | `ShapeAnnotation.cs` `EllipseAnnotation`, `VideoEditorWindow.AddShapeLayer`, `VideoLayerAssets.cs` |
| 색 읽기 | 영역 선택 돋보기가 커서 픽셀을 `#RRGGBB`로 그린다 | 클립보드 복사, `ColorFormat` 설정, 핀으로의 색 붙이기는 없다 | `CaptureOverlayView.UpdateMagnifier`, `AppSettings.ColorFormat` |
| UI 창 감지 | 창 캡처 명령은 커서 아래 최상위 창을 잡는다. `WindowCandidateService`는 z-order 후보를 모을 수 있다 | `GetCandidates` 호출이 없다. 영역 오버레이 주석은 창 호버·스냅·Tab을 하지 않는다고 적는다. `AutoDetectWindows`는 설정 저장만 된다 | `CaptureOverlayView.cs` 주석, `WindowCandidateService.cs`, `SettingsDraft.cs` |
| 지정 크기 정지 캡처 | `CaptureFixedSize`와 `FixedRegionPlanner`는 커서 중심 배치를 계산하고 서비스 테스트가 있다 | `App.xaml.cs`와 트레이는 이 메서드를 부르지 않는다. 사용자가 고를 프리셋 UI가 없다 | `AdvancedCaptureService.cs`, `FixedRegionPlanner.cs` |
| 클릭 강조 | 녹화 프레임에 커서 링. 왼쪽 버튼을 누르는 동안 붉은 링. 설정으로 끌 수 있고 기본은 켜짐 | 링을 그린 픽셀을 검증하는 테스트가 없다. 클릭 순간을 타임라인 마커로 남기지 않는다 | `ScreenCaptureEngine.DrawCursorHighlight`, `RecordingSettings.CursorHighlight`, `SettingsWindow.xaml` |
| 영상 위 주석 | 현재 프레임에서 정지 편집기를 열고, 그린 결과만 투명 PNG로 `FrameEditLayer`에 넣는다. 구간은 타임라인에서 늘일 수 있다. 도형·이미지 레이어는 처음부터 약 3초 | 벡터 `AnnotationDocument`는 영상 문서에 다시 저장되지 않아, 나중에 화살표만 고칠 수 없다. 프레임 편집으로 만든 레이어의 초기 길이는 한 프레임이다 | `VideoEditorWindow.AddFrameEditLayer`, `VideoEditDocument.cs` |
| 전문 검색의 범위 | 이미지 세대가 색인되면 그 글자로 갤러리를 찾는다. 빈 결과도 세대에 기록해 재시도를 막는다 | 색인 대상은 `record.IsImage`만이다. 배경 색인은 `searchRotatedOrientations: false`로 품질 프로필의 회전 검색을 끈다. 영상 화면 글자는 검색되지 않는다 | `OcrIndexingService.cs` |
| 스크롤 캡처 | 세로 겹침이 맞으면 이어 붙이고, 불연속이면 버린다 | 가로 스크롤·요소 단위 자동 선택은 없다. 휠 입력 실패 시 부분 결과 또는 실패로 끝난다. 네이티브 스크롤 싱크의 장치 테스트는 이 저장소의 순수 스티처 테스트와 다르다 | `ScrollInputSink.cs`, `HandleScrollingCapture` |
| GitHub URL | 클립보드 이미지가 있고 토큰 또는 브라우저 자동 붙이기가 성공하면 URL이 클립보드에 돌아온다 | 기본 키는 사용자가 말한 F4가 아니라 F9다. 브라우저 경로는 GitHub 작성란 UI 자동화라 페이지 구조가 바뀌면 워크플로가 끝에서 실패한다 | `App.xaml.cs` `HandleGitHubImageUpload`, `GitHubCommentUploadAutomation.cs` |
| 닫은 핀 복구 | 설정 `ClosedWindowRestoreLimit` 기본 20, 범위 0–100, 주석은 F3로 복구한다고 적는다 | `PinManager`는 열린 핀만 들고, 이 값을 읽지 않는다. 설정 창 XAML에도 없다 | `AppSettings.cs` `PinSettings`, `PinManager.cs` |

### 의도적 제외

| 항목 | 근거 |
|---|---|
| 마이크·시스템 오디오 | `MediaFoundationVideoEncoder.cs`에 오디오 미디어 타입이 없다. `docs/competitive-matrix.md`는 오디오를 제품 범위 밖이라고 적는다. README도 같은 결정을 말한다. 감점하지 않는다. |
| OBS급 다중 소스·스트리밍 | ADR 0002와 경쟁 매트릭스. 녹화는 한 영역 MP4다. |
| FFmpeg 번들 | ADR 0002가 설치 표면 때문에 기각하고 Media Foundation을 채택. |

### 미구현

저장소 `src`에서 해당 심볼·호출이 없다.

| 항목 | 확인 |
|---|---|
| 가우시안 블러 주석 | `Blur`/`Gaussian` 편집 경로 없음. 모자이크는 박스 평균 픽셀화다. |
| 번호 배지·단계 마커 | 주석 타입·편집 도구·녹화 마커 없음. |
| 핀 회전·반전·썸네일 모드·그룹·색/파일 경로 붙이기 | `PinWindow.cs`에 회전·반전 없음. 클립보드 색·경로를 핀으로 만드는 분기 없음. |
| 얼굴 감지 | `Face` 검색 없음. |
| APNG·이미지 시퀀스 내보내기 | `Apng`/`APNG` 없음. GIF와 MP4만. |
| 캡처 후 외부 명령·플러그인 | `IPlugin` 등 없음. |
| 자유형 캡처 | 영역은 사각형만. |
| 요소 단위 스냅 | 위 “부분 구현”의 미연결 서비스. |
| 지정 크기 정지 캡처의 사용자 진입점 | 서비스는 있으나 셸 미연결. |

`competitive-matrix.md`의 “남은 격차” 대조:

| 매트릭스 문장 | 3.0.0 코드 |
|---|---|
| 모자이크·블러·번호 배지·형광펜 UI가 아직 없다 | **모자이크 UI는 있다.** 블러·번호 배지·형광펜 UI는 없다. 형광펜은 도메인 플래그만 있다. |
| 닫은 핀 복구, 회전/반전이 없다 | 복구는 설정만 있고 동작은 없다. 핀 회전/반전은 없다. **정지 화면 편집기의 캔버스 회전은 있다.** 매트릭스가 둘을 한 줄로 묶으면 회전 전체가 없는 것처럼 읽힌다. |
| 얼굴 자동 감지·블러 없음 | 맞다. |
| APNG·이미지 시퀀스 없음 | 맞다. |
| 플러그인·선택 업로드 파이프라인 없음 | 일반 플러그인은 없다. GitHub 이슈 첨부 URL만 별도 기능으로 있다. |
| 오디오 부재는 의도 | 맞다. 범위 밖. |
| 녹화 중 클릭 강조가 다음 투자(P1) | **클릭 링은 3.0.0에 들어 있다.** 단계 마커는 없다. P1 한 줄을 통째로 “미구현”으로 두면 틀리다. |
| UI 요소 자동 감지, 색상 피커, 사용자 프리셋 없음 | 요소 스냅과 색 복사, 정지 캡처 프리셋 진입점은 없다. 돋보기 헥스 표시와 녹화 크기 프리셋은 있다. |

## 3. 경쟁 제품과의 깊이

이번 세션에 본문을 연 페이지:

- Snipaste: <https://www.snipaste.com/>
- 알캡처: <https://altools.co.kr/product/ALCAPTURE>
- Greenshot: <https://getgreenshot.org/>
- Windows 캡처 도구: <https://support.microsoft.com/en-us/windows/use-snipping-tool-to-capture-screenshots-00246869-1843-655f-f220-97299b865f6b>

연 뒤 본문이 비거나 실패한 페이지:

- <https://www.screentogif.com/> 는 스타일 시트만 돌아왔다. ScreenToGif 기능 문장은 WebSearch가 돌려준 GitHub README·내보내기 위키 요약이다. README blob 직접 조회는 빈 문서였다.
- <https://getsharex.com/blog/how-to-record-screen-windows/> 는 WebFetch가 404였다. ShareX의 캡처 종류·오디오·워크플로 문장은 그 검색 스니펫과 `docs/competitive-matrix.md`가 가리키는 <https://getsharex.com/blog/guides/> 에 의존한다. guides 페이지 자체는 열지 못했다.
- Snipaste 번호 배지는 연 홈페이지의 주석 목록(사각형, 타원, 선, 화살표, 연필, 마커, 텍스트, 모자이크, 가우시안 블러, 지우개, Undo)에 없다. 번호 배지를 Snipaste 공식 기능으로 단정하지 않는다. 그 문장은 경쟁 매트릭스 초안에만 있다.

### 경쟁 우위

- Snipaste 홈페이지: UI 요소 자동 감지, 색 복사(`F1`, `C`, `F3`), 캡처 히스토리 `,`/`.`, 핀 회전·반전·GIF 프레임·썸네일·그룹·자동 백업, 마커·모자이크·가우시안 블러. MyCapture는 핀 확대·투명도·클릭 통과·텍스트/표 원문까지는 닫지만 회전·복구·썸네일·색 붙이기는 닫지 않는다.
- 알캡처 제품 페이지: 자유형·단위영역·지정사이즈를 포함한 7개 캡처, 스크롤, AI 얼굴 모자이크/블러, AI 텍스트·화질·배경 제거·지우개, 최근 100장. MyCapture 스크롤은 세로 스티치까지이고, 얼굴 가리기와 자유형·단위영역·지정사이즈 진입점은 없다. 최근 목록은 갤러리가 더 길다(기본 300개, OCR 검색).
- Greenshot 홈페이지: 영역·창·전체, IE 스크롤, 하이라이트·가리기, 프린터·메일·Office·사진 사이트 내보내기. 플러그인 이름은 <https://getgreenshot.org/faq/how-remove-plugins-or-destinations-from-greenshot/> 검색 결과에 Box, Dropbox, Jira, Office, OCR, 외부 명령이 있다. 이 FAQ 본문은 WebFetch로 다시 열지 않았다. MyCapture에는 그 목적지 파이프라인이 없다.
- Windows 지원 문서: 자유형, 형광펜, 도형, 자르기, 지연, 비디오 스닙, 로컬 OCR, 이메일·전화 빠른 가리기. 비디오 오디오·자막은 그 문서에서 Clipchamp로 넘긴다. 학습 센터 쪽의 “녹화 중 마이크” 문장은 이 지원 문서와 다르므로 미확인에 둔다. MyCapture는 형광펜과 자유형이 없고, 가리기 종류는 이메일·전화보다 넓다(주민번호 형태, 카드, IPv4, 일부 토큰).
- ScreenToGif README 검색 요약: 화면·웹캠·스케치 녹화 후 GIF, APNG, 비디오, PSD, PNG. 위키 검색 요약은 프레임을 이미지 시퀀스로도 보낸다. MyCapture는 프레임 목록 재정렬, APNG, PSD, 웹캠이 없다.
- ShareX 검색 스니펫·매트릭스: 영역·창·모니터·마지막 영역·스크롤·GIF·FFmpeg 오디오·업로드 워크플로. 오디오와 범용 업로드는 MyCapture가 얕거나 범위 밖이다.

### 과제 고유 강점

이 제품이 목표로 둔 무계정 로컬 설명 흐름 안에서만 비교한다.

- 주석이 객체로 남는다. `AnnotationItem` 주석은 Snipaste식 즉시 래스터와 달리 선택·이동·스타일·삭제를 큐에 저장된 뒤에도 한다 (`AnnotationItem.cs`, `layers.json`).
- 핀이 표의 셀 구조를 원문으로 보존한다. 렌더 이미지 복사와 Unicode 원문 복사가 갈라져 있고 테스트가 있다.
- 민감정보 가리개가 일반 레이어라 검토·이동·삭제·한 번에 Undo된다. Windows 지원 문서의 빠른 가리기는 이메일·전화이고, 여기서는 그 결과를 다시 편집할 수 있다.
- 과거 이미지의 글자 검색이 갤러리·큐·백그라운드 색인으로 연결되어 있다 (ADR 0003, `CaptureTextSearch`). OS 캡처 도구의 OCR은 방금 찍은 한 장의 텍스트 동작에 가깝다.
- 무음 설명 녹화는 원본 MP4를 유지한 채 트림·시간 텍스트·오버레이를 고친다. ScreenToGif는 프레임 편집이 더 깊지만, MyCapture는 프레임 전체를 메모리에 펼치지 않고 Media Foundation만으로 MP4와 제한된 GIF를 만든다 (ADR 0002, ADR 0004).
- 계정·자동 업로드 없이 GitHub 첨부 URL만 선택적으로 만든다. ShareX식 다중 목적지와는 범위가 다르다.

### 공통 약점

- 스크롤 캡처는 페이지 구조에 따라 겹침이 깨진다. 알캡처 FAQ도 스크롤 영역을 맞추는 절차를 안내한다.
- GIF는 색 수·길이·해상도 한계가 있다. ScreenToGif 위키도 GIF를 256색 형식으로 설명한다.
- 로컬 OCR은 언어 팩·글자 크기에 좌우된다. MyCapture는 엔진이 없으면 검색 불가를 메시지로 낸다 (`OcrAvailability`).

### 범위 밖

오디오, 웹캠, 스트리밍, 하드웨어 인코더 선택은 이 축의 감점이 아니다.

## 4. 워크플로가 끊기는 구멍

주 소유는 functionality다. 단계 수가 문제인 항목은 convenience로 넘긴다.

1. **설명 마크업이 화살표·사각형·텍스트에서 멈춘다.** 형광펜과 번호 배지 없이 “1, 2, 3단계”를 그리려면 텍스트를 손으로 놓아야 한다. 블러는 없고 모자이크는 설정과 분리된 일회성 패치다. OVERLAP: convenience(도구를 찾는 단계). 주 소유 functionality.
2. **모자이크 설정이 동작을 약속하고 지키지 않는다.** 설정 화면은 블록 크기와 형광펜 알파를 받는다. 제스처는 14px 패치만 만들고 형광펜 도구는 없다. OVERLAP: learnability. 주 소유 functionality. 사용자는 기능이 있는 줄 알고 저장한다.
3. **핀을 닫으면 참조가 끝난다.** `ClosedWindowRestoreLimit`는 복구를 설명하지만 `PinManager`는 닫힌 핀을 스택에 넣지 않는다. 라이브러리에 없는 클립보드 핀은 사라진다. OVERLAP: convenience. 주 소유 functionality.
4. **색과 요소 스냅이 선택 중에 닫히지 않는다.** 헥스는 보이지만 복사되지 않고, 창 후보는 계산 코드만 있다. 반복 캡처의 “같은 UI 조각”은 손 드래그에 남는다. OVERLAP: convenience. 주 소유 functionality. 드래그 미세 조정 단계 수는 convenience.
5. **지정 크기 정지 캡처가 제품 표면에 없다.** Core 배치와 서비스는 테스트되어 있으나 단축키·트레이가 없다. 녹화 프리셋만 설정에 연결되어 있다. OVERLAP: convenience(진입점). 주 소유 functionality.
6. **영상 설명 레이어를 다시 벡터로 고치지 못한다.** 트림과 텍스트 문구는 비파괴다. 프레임에서 연 주석 편집의 도형은 PNG로만 남는다. 화살표 위치를 바꾸려면 레이어를 지우고 다시 그린다. OVERLAP: convenience. 주 소유 functionality.
7. **GIF로 설명을 끝내는 길이에 상한이 있다.** `MaximumDurationMs = 20_000`, 긴 변 최대 960, 5 또는 10fps. 더 긴 설명은 MP4로 남고 GIF 경로는 예외로 끊긴다. 의도된 예산이면 문서화된 한계다. 매트릭스는 이를 현재 구현으로 이미 적었다.
8. **글자 검색이 이미지에서 멈춘다.** ADR 0003이 락인으로 둔 “예전에 찍은 화면의 글자”는 영상 프레임에 적용되지 않는다. 색인은 사용자가 갤러리에서 시작해야 하고, 그 경로는 회전 검색을 끈다.
9. **GitHub URL의 브라우저 경로는 외부 UI에 의존한다.** 토큰 경로는 앱 안에서 끝난다. 토큰이 없거나 만료되어 폴백하면 60초 안에 첨부 URL을 못 읽으면 실패다 (`GitHubIssueImageUploadService`). OVERLAP: trust(토큰 보관). 주 소유 functionality.
10. **Accurate/Enhanced OCR은 모델 다운로드가 있어야 PP-OCR로 닫힌다.** 모델이 없으면 Windows OCR로 떨어진다 (`OcrModelStore`, `CaptureOcrService`). 완전 오프라인에서 “향상” 단계가 같은 품질로 끝난다고 보면 안 된다. OVERLAP: trust(modelscope.cn 다운로드). 주 소유 functionality.

GDI `BitBlt`라서 고부하 녹화의 프레임 드롭이 남는 문제는 매트릭스 P0과 ADR 0002에 있다. 기능이 없어서가 아니라 캡처 품질이라 주 소유는 performance. 여기서 감점하지 않는다. OVERLAP: performance.

## 5. 보완 과제

매트릭스 “다음 우선순위”와 맞춘다.

| 순위 | 결과 (사용자에게 생기는 능력) | 매트릭스와 관계 | 겹침 |
|---|---|---|---|
| P0 | 형광펜을 `IsHighlighter`와 `HighlighterAlpha`에 연결하고, 번호 배지를 Undo·`layers.json` 왕복이 되는 주석으로 추가한다. 단계 설명 화면을 다시 열어 번호와 강조를 고친다. | 매트릭스 P1 “모자이크·블러·번호·형광펜” 중 형광펜·번호만 승격. 모자이크 “추가”에는 **반대**. 이미 `EditorTool.Mosaic`가 있다. | learnability(설정이 이미 형광펜 알파를 보여 줌). 주 소유 functionality |
| P0 | 모자이크 제스처가 `AnnotationDefaults.MosaicBlockSize`를 쓰게 하고, 가능하면 패치가 아닌 다시 샘플할 수 있는 효과로 둔다. 설정에 저장한 블록 크기가 다음 가리기에 반영된다. | 매트릭스 P1의 모자이크 항목을 “신규 도구”가 아니라 “설정 연결과 재편집”으로 **수정 동의**. | 없음 |
| P1 | 닫은 핀을 `ClosedWindowRestoreLimit`만큼 복구한다. 메모리 상한과 원본 픽셀 불변은 매트릭스 승격 조건 그대로. | 매트릭스 P1 **동의**. 회전/반전은 핀에만 해당한다고 분리. 편집기 회전은 이미 있다. | convenience. 주 소유 functionality |
| P1 | 클릭을 타임라인 단계 마커로 남긴다. 커서 링 자체는 구현됐으므로 매트릭스 P1 문장에서 뺀다. 링 픽셀 회귀 테스트를 추가한다. | 클릭 강조 “미구현”에는 **반대**. 단계 마커에는 **동의**. | performance(프레임 타이밍), accessibility(오버레이가 입력을 가리지 않음). 주 소유 functionality |
| P1 | 영상 프레임 주석의 벡터 문서를 `video-edits.json`에 남겨 다시 연다. PNG는 미리보기·인코드용으로 둔다. | 매트릭스에 없는 구멍. 비파괴 트림은 이미 있으므로 APNG보다 설명 흐름에 가깝다. | 없음 |
| P1 | 갤러리 색인이 영상 썸네일 또는 추출 프레임의 OCR을 선택적으로 채운다. 배경 색인의 회전 검색을 품질 설정과 맞춘다. | ADR 0003의 미결(UI 배선)은 갤러리 버튼으로 닫혔다. 영상 공백은 남아 있다. | performance(색인 비용). 주 소유 functionality |
| P1 | 블러 레이어와, 그 다음 얼굴 후보. 얼굴은 로컬·오탐 검토·Undo가 있을 때만. | 블러는 매트릭스 P1 **동의**. 얼굴은 매트릭스 P1을 유지하되 텍스트 가리기가 이미 있으므로 블러 다음이다. | trust. 주 소유 functionality, 얼굴 모델 공급망은 trust와 공동 |
| P2 | 지정 크기 정지 캡처를 트레이/단축키에 연결한다. `CaptureFixedSize`는 이미 테스트되어 있다. | 매트릭스 “사용자 프리셋 없음”에 **부분 동의**. 녹화 프리셋은 이미 있다. | convenience(진입점). 주 소유 functionality |
| P2 | GIF 20초·10fps를 넘기는 내보내기 또는 APNG. | 매트릭스 P2 APNG에 **동의**. 길이 제한 완화가 설명 워크플로에는 더 직접적이다. | performance, trust(코덱). |
| P2 | 로컬 후처리 플러그인. | 매트릭스 P2 **동의**. 설명 흐름의 구멍은 아니라 순위가 맞다. | trust. 주 소유는 보안 조건이 핵심이면 trust, 기능 표면이면 functionality. 제안 주 소유 trust. |
| 아님 | Windows Graphics Capture / Desktop Duplication. | 매트릭스 P0에는 기능 축으로 **반대**. 녹화·캡처는 이미 동작한다. 승격 조건이 CPU·드롭·실기기라 주 소유는 performance. | performance |

## 6. 미확인

- 실제 다중 모니터·혼합 DPI 장치에서 BitBlt가 가상 데스크톱을 한 장으로 따는지. 테스트는 좌표 변환과 작은 비트맵이다 (`MultiMonitorRecordingLayoutTests`).
- 스크롤 캡처가 브라우저·Electron에서 휠 3칸으로 스티치되는지. 검증된 것은 `ScrollStitcher`의 픽셀 규칙이다.
- 커서 강조 링이 녹화된 MP4 픽셀에 남는지. 그리기 코드는 있고 프레임 골든 테스트는 없다.
- 라이브 github.com 작성란에서 `GitHubCommentUploadAutomation`이 지금 UI로 URL을 회수하는지. 테스트는 시임 주입이다.
- ShareX 공식 guides 페이지와 ScreenToGif features 페이지의 전문. 위 3절의 실패 기록 참조.
- Windows 캡처 도구가 녹화 중에 마이크를 직접 넣는지, Clipchamp에서만 오디오를 붙이는지. 연 지원 문서는 Clipchamp 경로다.
- Snipaste·ShareX의 번호 배지. 이번 세션에 연 Snipaste 홈페이지에는 없고, 매트릭스에만 있다.
- PicPick. 프로토콜의 비교 목록에는 있으나 이번 산출물의 경쟁 집합에서 페이지를 열지 않았다.
