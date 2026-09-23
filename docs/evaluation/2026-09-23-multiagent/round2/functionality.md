# 라운드 2 — 기능

대상: Release 3.0.0, 커밋 `117b0ef`. 페르소나: functionality.
읽은 것: `CONFLICTS.md`, inbox 전부, `round1/02-functionality.md`, `round1/08-positioning.md`와 `round1/05-performance.md`의 결론, `round1/06-trust.md`의 OCR 평문·가리기·모델 다운로드. 제품 소스는 열기만 했다. 다른 `round2` 파일은 수정하지 않았다.

기능 점수 4를 유지한다.

## 1. 점수

| 판단 | 라운드 1 | 라운드 2 | 근거 |
|---|---|---|---|
| 기능 깊이 | 4 / 5 | 4 / 5, 유지 | 캡처 → `layers.json` 재편집 → 라이브러리 → 무음 영역 MP4 → 비파괴 트림/GIF의 척추는 그대로다. 5로 올리지 않는 이유는 아래 네 공백이다. |

4를 5로 올리지 않는 이유 (기능이 계속 소유):

| 공백 | 경로 |
|---|---|
| 형광펜 도구가 없다. `HighlighterAlpha`는 설정만 있다. | `EditorTool.cs`, `AnnotationEditorController.cs` `new PenAnnotation` (694행, `IsHighlighter` 미설정), `SettingsWindow.xaml` 758행 |
| 모자이크 도구는 있으나 제스처가 블록 14를 고정하고, 설정 기본 12를 읽지 않는다. 결과는 다시 샘플할 수 없는 이미지 패치다. | `AnnotationEditorControl.cs` 614행 `blockSize: 14`, 494행 클램프 4–64, `AppSettings.cs` `MosaicBlockSize = 12` |
| 닫은 핀 복구는 저장·클램프만 되고 실행이 없다. | `AppSettings.cs` `ClosedWindowRestoreLimit`, `SettingsStore.cs` 271행. `src/MyCapture.App`의 cs/xaml에 이 이름 없음. `PinManager.cs` |
| 영상 프레임 주석은 벡터로 다시 열리지 않고 PNG다. | `VideoEditorWindow.AddFrameEditLayer`, `VideoEditDocument.cs` |

라운드 1이 “5가 아닌 이유”에 넣었던 GIF 20초는 이번 라운드에서 기능 감점에서 뺀다 (CL-13). GDI, 평문 색인, 모델 다운로드, `original.png` 잔존, 커서 링의 비용·색도 기능 점수에서 뺀다. 그 사실을 빼도 위 네 공백이 남아 3으로 내리지 않고 4에 둔다.

## 2. 판정

| ID | 논점 | 판정 | 기능 점수가 하는 일 |
|---|---|---|---|
| CL-13 | GIF 20초 | 합의 | 범위 규율. ScreenToGif 대비 감점하지 않는다. |
| CL-09 | 오프라인 / 평문 색인 | 병합 | 검색은 강점으로 유지. 평문·다운로드 감점은 trust로 넘긴다. |
| CL-02 | 형광펜·모자이크 14 | 병합 | 빠진 도구와 어긋난 설정의 주 소유. C-10을 여기로 합친다. |
| CL-04 | GDI는 기능 P0인가 | 합의 | 아니다. “캡처가 된다”만 보유한다. |
| CL-03 | 핀 복구 소유 | 병합 | 주 소유는 functionality. C-07·POS-04의 복구 감점을 여기로 합친다. |
| CL-05 | 커서 강조 세 갈래 | 합의 | 존재만 소유. 비용·색과 합치지 않는다. |
| CL-10 | `original.png` | 합의 | 잔존 파일은 trust. 편집 가능한 가림막은 기능 강점. |

## 3. CL-13 — GIF 20초

**합의.** 20초 상한은 설명용 짧은 GIF의 범위 규율이다. 기능 점수에서 ScreenToGif 대비 감점하지 않는다.

| 사실 | 결정 | 주 소유 | 경로 |
|---|---|---|---|
| 내보내기는 20초, 긴 변 960/640/480, 10 또는 5fps, 속도 0.25–4배에서 멈춘다. 그 밖은 예외다. | 합의. 구현된 예산이지 빠진 기능이 아니다. | positioning이 문장과 일치하는 범위로 인용. functionality는 재감점하지 않음 | `AnimatedGifExporter.cs` `MaximumDurationMs = 20_000`. `GifExportQuality.cs` Standard/Compact/Smallest |
| 상한을 제품이 숨기면 학습성이 소유한다. | 합의하되 조건은 성립하지 않는다. 대화상자가 상한을 먼저 말한다. | 숨김이 없으므로 learnability로 넘길 감점이 없다 | `VideoExportDialog.cs` 82행 `MediaExport_GifLimits`, 153–155행 초과 시 계산 버튼 비활성·`MediaExport_TrimGif`. `Strings.resx` `MediaExport_GifLimits` “GIF는 최대 20초, 200프레임입니다.” |
| ScreenToGif의 프레임 재정렬·APNG·PSD·웹캠 | 합의. 그 폭을 따라 기능 점수를 깎지 않는다. | positioning의 범위 밖. 라운드 1 기능 P2 “길이 제한 완화”는 점수 근거에서 철회 | `round1/08-positioning.md` §4 APNG 행, §1 GIF 상한이 포지션 문장과 맞는다는 결론 |

라운드 1 F-01·§4-7이 20초 거부를 “5가 아닌 이유”에 넣은 것은 철회한다. 더 긴 설명은 MP4로 남는다. 매트릭스가 이미 현재 구현으로 적은 상한이다.

## 4. CL-09 — 오프라인 / 평문 색인

**병합.** 검색 기능은 functionality의 강점이다. 자동 색인의 평문과 첫 실행 모델 다운로드는 trust가 한 번만 감점한다. positioning은 “항상 오프라인” 문장만 좁힌다. 기능 점수는 이 사실로 깎지 않는다.

| 사실 | 결정 | 주 소유 | 기능 축이 남기는 문장 | 경로 |
|---|---|---|---|---|
| 이미지 전문 검색이 갤러리·큐·배경 색인으로 닫힌다. 영상은 빠지고 배경 색인은 회전 검색을 끈다. | 합의. 강점. 영상 공백은 기능의 부분 구현으로 남기되 평문 보존과 합산하지 않는다. | functionality | F-10 유지 | `OcrIndexingService.cs` 129–132행 `IsImage`, 163행 `searchRotatedOrientations: false`. `GalleryWindow.xaml.cs` `OnOcrIndexClick` |
| `CacheResults == false`여도 자동 색인이 `OcrText`를 `index.json`·`meta.json`에 쓴다. | 병합. 기능 감점에서 제외. | trust (T04) | 검색이 글을 저장해야 찾을 수 있다는 동작 설명만. 설정 무시의 신뢰 감점은 반복하지 않음 | 자동 경로 `OcrIndexingService.cs`는 `CacheResults`를 읽지 않는다 (`src/MyCapture.App/Ocr` 검색 0건). 수동 경로만 `GalleryWindow.xaml.cs` 866행에서 `settings.CacheResults`를 본다. |
| 첫 실행 PP-OCR 가중치 다운로드. Accurate/Enhanced는 모델이 없으면 Windows OCR로 떨어진다. | 병합. 네트워크 표면과 “항상 오프라인” 문장 수정은 trust·positioning. 같은 폴백을 기능 점수로 다시 깎지 않는다. | trust (T02). positioning은 문장만 | F-16의 “주 소유 functionality”는 철회. 폴백이 있어 기본 Fast 검색은 모델 없이도 끝난다는 설명만 남긴다. | `App.xaml.cs` `StartOcrModelDownload` 1500행 → `OcrModelStore.EnsureAsync`. `CaptureOcrService.cs` |

## 5. CL-02 — 형광펜·모자이크 14

**병합.** 빠진 도구와 어긋난 설정의 주 소유는 functionality다. convenience C-10은 이 감점으로 합친다. positioning(POS-03)은 범위가 중간에 끊긴 증거로만 인용하고 일관성 점수에서 한 번 더 깎지 않는다.

| 주장 | 결정 | 주 소유 | 경로 |
|---|---|---|---|
| 형광펜 UI가 없다. `IsHighlighter`는 도메인·렌더·테스트 왕복만 있다. | 병합 (F-04, C-10, POS-03) | functionality. 점수 4의 감점 | `EditorTool`에 형광펜 없음. `AnnotationEditorController.cs` 694행 펜 생성에 `IsHighlighter = true` 없음. `AnnotationRenderer.cs` 207행은 플래그가 켜진 펜만 알파 110 이하로 줄인다. `DomainTests.cs` |
| `HighlighterAlpha`(기본 90, 범위 0–255)가 설정 칸에만 있다. | 병합. 그리기에 쓰이지 않는 설정은 기능 계약 파손 | functionality | `AppSettings.cs` 249행. `SettingsWindow.xaml` 758행. 편집 그리기 경로에서 이 값을 읽는 코드 없음 (`src`에서 설정 모델·초안·클론·에디터의 이전 값 복사만) |
| 모자이크 도구는 있다. 경쟁 매트릭스의 “모자이크 UI를 추가해야 한다”는 틀리다. | 합의 (F-02) | functionality의 정정. 감점이 아님 | `EditorTool.Mosaic`. `AnnotationEditorControl.cs` 1126행 도구 버튼 |
| 마우스 업은 `blockSize: 14`. 설정 `MosaicBlockSize` 기본 12는 제스처가 읽지 않는다. `ApplyMosaic`는 4–64로 다시 자른다. | 병합 (F-03) | functionality. 점수 4의 감점 | `AnnotationEditorControl.cs` 482–494행, 614행. `SettingsWindow.xaml` 750행 |
| 번호 배지·가우시안 블러 | 병합. C-10에 들어 있으므로 기능이 한 번만 감점 | functionality (F-05) | `src/` 편집 경로에 Blur/번호 배지 도구 없음 |
| 색 뽑기·`ColorFormat` | 병합의 일부이나 감점 폭은 “전문 색 도구 부재”가 아니다. 돋보기는 헥스를 표시만 하고 설정은 보존만 된다. | functionality (F-14). POS-05는 재감점 안 함 | `CaptureOverlayView.cs` `UpdateMagnifier`, `AppSettings.cs` `ColorFormat` |
| 모자이크·자르기 자동화 이름이 M·X인데 `HandleShortcut`이 그 키를 처리하지 않음 | 합의로 CL-02와 합치지 않는다. | accessibility (CL-16). learnability는 보조. convenience C-06의 거짓 이름은 여기로 넣지 않음 | `AnnotationEditorControl.cs` `AddToolButton`, `HandleShortcut` |

조율자가 다시 연 사실과 같다. `IsHighlighter = true`는 테스트와 복사 생성자 쪽이고, 편집기가 형광펜 도구로 켜지 않는다.

## 6. CL-04 — GDI는 기능 P0인가

**합의.** 아니다. 주 소유는 performance다. 기능 점수는 “캡처가 된다”로 유지한다. F-15를 유지한다.

| 주장 | 결정 | 주 소유 | 경로 |
|---|---|---|---|
| 정지·녹화 획득은 `BitBlt + CAPTUREBLT + GetDIBits`뿐이다. | 합의. 기능 감점 아님 | performance | `ScreenCaptureEngine.cs` 65–70행 주석 “Desktop Duplication API를 쓰지 않는다”, 356–358행 `BitBlt`. `RegionFrameGrabber.cs`가 같은 경로를 녹화에 재사용 |
| 영역 캡처와 영역 MP4는 그 경로로 이미 동작한다. | 합의 | functionality는 동작만 확인 | `CaptureSession.CaptureInto`, ADR 0002 |
| 매트릭스 P0(1080p/4K CPU·드롭·복구, Windows 11 22000) | 합의. 기능 축 P0로 올리지 않는다 | performance | `docs/competitive-matrix.md`. 라운드 1 기능 §5 “아님” 행 |

30fps 미달과 드롭은 성능 점수에만 둔다. 기능 점수 4의 근거로 쓰지 않는다.

## 7. CL-03 — 핀 복구 소유

**병합.** 닫은 핀 복구가 저장만 되는 사실의 주 소유는 functionality다. convenience C-07과 positioning POS-04의 복구 감점은 여기로 합치고 다시 깎지 않는다.

| 사실 | 결정 | 주 소유 | 경로 |
|---|---|---|---|
| `ClosedWindowRestoreLimit` 기본 20, 범위 0–100은 모델·초안·클램프에만 있다. | 병합 (F-07, C-07) | functionality. 점수 4의 감점 | `AppSettings.cs` `PinSettings`, `SettingsDraft.cs` 366행, `SettingsStore.cs` 271행 |
| 설정 창과 `Pinning/` 동작 코드가 이 값을 읽지 않는다. `PinManager`는 열린 핀만 든다. | 병합 | functionality | `src/MyCapture.App` cs/xaml 검색 0건. `src/MyCapture.App/Pinning/PinManager.cs` |
| 핀 회전·반전 | CL-03에 합치지 않는다. 복구와 별개인 기능 공백(F-08). positioning이 복구와 한 줄로 묶어 일관성 점수를 다시 깎지 않는다. | functionality | `PinWindow.cs`에 회전·반전 없음. 정지 편집기 `AnnotationEditorControl.RotateCapture`는 별개이며 이 감점에 넣지 않는다. |
| 붙여넣기 키(F3)로 되돌린다는 주석 | 주석의 약속이 실행되지 않는 것은 기능 공백. 키 안내 문장의 학습 문제는 learnability가 따로 갖지 않는다. | functionality | `AppSettings.cs` `PinSettings` 주석 |

## 8. CL-05 — 커서 강조를 세 주장으로

**합의.** 존재, 비용, 색을 한 주장으로 합치지 않는다. 기능은 존재와 “매트릭스 P1의 미구현 묶음은 틀리다”만 소유한다.

| 갈래 | 판정 | 주 소유 | 기능이 채점하는가 | 경로 |
|---|---|---|---|---|
| 존재. 녹화 중 링이 3.0.0에 있고 기본이 켜져 있다. 왼쪽 버튼을 누르는 동안 반지름·색이 바뀐다. | 합의 (F-06) | functionality | 감점 아님. 구현된 설명 기능으로 매트릭스를 정정 | `RecordingSettings.cs` 51행 `CursorHighlight = true`. `RegionRecordingCoordinator.cs` 277행이 그랩에 전달. `ScreenCaptureEngine.cs` 360–362행, `DrawCursorHighlight` 495행 |
| 비용. 프레임마다 GDI가 더해지고 드롭에 미치는 시간은 미측정. | 합의 | performance | 아니오 | `DrawCursorHighlight`의 `GetCursorInfo`, `CreatePen`, `Ellipse`. 밀리초는 `docs/performance/`에 없음 |
| 색. 유휴 `#FFC700`, 눌림 `#FF4040`이 토큰 밖이고 프레임 픽셀에 구워진다. | 합의 (AES-09) | aesthetics | 아니오 | `ScreenCaptureEngine.cs` 510–511행 COLORREF `0x0000C7FF` / `0x004040FF`. 캡처 DC에 그린 뒤 프레임으로 들어간다 |
| 링 픽셀 골든 테스트가 없다. | 합의. 존재 주장과 비용 주장을 합치지 않는다. | functionality의 미확인 | 점수 4를 3으로 내리지 않는다. 그리기 코드는 있다. | 프레임 골든 테스트 없음. 라운드 1 §6 |
| 클릭을 타임라인 단계 마커로 남기는 일 | 세 갈래 밖에 둔다. 링이 있다고 마커까지 있는 것이 아니다. | functionality (F-05, 라운드 1 P1) | 마커 부재는 기능 공백. 링 존재의 감점이 아님 | 녹화 마커 타입 없음 |

## 9. CL-10 — `original.png`

**합의.** 가리기 뒤 `original.png`에 가려지지 않은 픽셀이 남는 것은 trust의 감점이다. 편집 가능한 가림막은 functionality의 강점이며 기능 점수를 깎지 않는다.

| 사실 | 결정 | 주 소유 | 경로 |
|---|---|---|---|
| 가리기 커밋은 불투명 사각형 주석을 한 Undo 묶음으로 넣는다. 탐지 문자열은 주석에 복사하지 않는다. | 합의. 강점 유지 (T06의 도구 절) | functionality | `AnnotationEditorController.AddPrivacyRedactions`, `PrivacyRedactionService.cs`, `PrivacyDetector.cs` |
| 확정 시 `original.png`는 다시 그리지 않는다. `rendered.png`와 `layers.json`만 바뀐다. | 합의. 잔존 데이터의 감점은 trust (T07) | trust | `CapturePersistenceService.cs` 215행 “unmodified capture”, 419행 “original.png is unchanged” |
| 다시 열 때의 편집 기준은 항상 `original.png`다. | 합의. 이 계약은 기능 강점이다. trust가 원본을 가린 픽셀로 덮어 쓰면 재편집 약속이 깨진다. 그 수정 방법의 채점 소유는 trust에 남긴다. | 잔존은 trust. 재편집 계약의 설명은 functionality | `GalleryReeditLoader.cs` 67–69행 |
| 이후 색인은 `rendered.png`를 우선한다. | 합의. 색인이 가린 그림을 읽는다는 동작은 기능. 원본 파일이 남는 신뢰 문제는 반복 감점하지 않음 | 평문·원본 잔존은 trust. 검색 동작은 functionality | `OcrIndexingService.cs` `ResolveImagePath` 279–288행 |

미탐·오탐(줄 안 패턴만, 주민번호 체크 자릿수 없음)은 가리기 버튼의 기능 완성도가 아니라 trust의 신뢰 공백이다 (T08). 기능은 “검토·이동·삭제가 되는 레이어”만 강점으로 센다.
