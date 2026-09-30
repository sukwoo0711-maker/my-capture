# 편의성 라운드 1 — 워크플로 마찰

- 평가일: 2026-09-23
- 기준: Release 3.0.0 (`117b0ef`, `main`)
- 축: convenience (단축키, 트레이, 캡처 후 단계, 핀, 편집기 저장, 라이브러리 재검색, 녹화 시작/종료, 오류 시 막힘)
- 시각 미학·접근성 점수·성능 수치·서명 여부는 이 점수의 감점 근거로 쓰지 않았다.

## 1. 한 줄 판정과 점수

**판정: 핵심 여정은 단축키 몇 번으로 이어지고, 실패해도 트레이에 남는다. 반복 캡처의 기본 키 공백과 녹화의 추가 시작 확인, 완료와 클립보드가 한 동작으로 묶인 점이 매일의 마찰이다.**

**점수: 4 / 5** (유보 아님. 아래 여정은 해당 소스와 2026-09-23에 연 경쟁 문서로 확인했다.)

채점 이유: 영역 캡처는 전역 키 한 번과 드래그 놓기로 원본이 클립보드에 들어가고 편집기가 열린다. 창·전체·이전 영역·클릭 통과는 등록 가능한데 기본값이 비어 있어 트레이를 연다. 영역 녹화는 영역 확정 뒤에 시작 버튼 또는 Enter가 한 번 더 필요하다. 편집기 완료·빠른 저장·다른 이름 저장은 큐 기록과 클립보드 복사를 함께 요구하고, 복사 실패 문구가 저장 실패와 같다. 라이브러리 재검색과 F3 핀은 키보드로 완결되며, 닫은 핀을 되돌리는 경로는 설정 필드만 있고 호출되지 않는다.

## 2. 대표 사용자 여정

### 2.1 영역 캡처 → 주석 → 저장

실제 단계:

1. `Ctrl+Shift+C` (`HotkeySettings.Capture` 기본값, `src/MyCapture.Core/Settings/AppSettings.cs`). `App.OnGlobalHotkeyPressed`가 `HandleCaptureRequested`로 간다 (`src/MyCapture.App/App.xaml.cs`).
2. 오버레이가 뜨고, 드래그를 놓으면 선택 확정이다. Enter는 이미 있는 선택을 확정하는 보조 경로다. 안내 문구는 “놓으면 확정”이다 (`CaptureOverlayView.OnMouseLeftButtonUp`, `Strings.resx`의 `Text_798E080A9262`).
3. 선택 직후 원본 픽셀을 클립보드에 복사하고 편집기를 연다. 이 시점에 큐·저널·디스크 기록은 없다. 자동 핀도 만들지 않는다 (`OnCaptureSelectionCompletedAsync`, `CaptureCommitService.CopyCapturedRegionAsync`).
4. 주석 후 저장은 네 갈래다 (`EditorCommitAction`, `CaptureCommitService.CommitAsync`).
   - `Ctrl+Enter` 또는 완료: 큐에 최종본을 넣고 편집 이미지를 클립보드에 복사한 뒤 닫는다.
   - `Ctrl+C`: 같은 지속화 뒤에 클립보드 복사를 하고, 복사가 성공해야 닫는다.
   - `Ctrl+S`: 빠른 저장 PNG(`%USERPROFILE%\Pictures\Captures` 또는 설정 폴더)와 클립보드 복사가 둘 다 성공해야 닫는다.
   - `Ctrl+Shift+S`: 파일 대화상자를 먼저 연다. 취소하면 편집기가 남고 부작용이 없다.
5. Esc는 편집을 취소한다. 아직 커밋하지 않았으면 라이브러리에 항목이 생기지 않는다. 클립보드의 원본 복사는 유지된다.

마찰:

- 클립보드만 필요해도 편집기가 항상 열린다. 닫으려면 Esc가 한 번 더 있다. 제품이 고른 “검토 후 확정” 계약이다 (`docs/releases/2.1.0-ux-directions.md`의 confirmation 규칙과 `CreatePendingRecord` 주석).
- 완료의 열거 주석은 “클립보드 없음”이라고 적혀 있으나 `CommitAsync`의 `Done`은 `CopyEditedImageAsync`를 호출한다. 클립보드가 잠기면 큐 기록이 된 뒤에도 편집기가 남고, 상태 문구는 “저장이 완료되지 않았습니다”(`Text_06F58809C083`)다. 사용자는 저장과 복사 중 무엇이 실패했는지 구분할 수 없다.
- 편집기가 열린 상태에서 영역 캡처 키를 다시 누르면 현재 편집기를 닫고 재촬영한다 (`TryPrepareCapture`, README 3.0.0 절). 저장하지 않은 주석은 이 경로에서 버려진다.
- 도구 레일은 모자이크에 `M`, 자르기에 `X`를 보여 주지만 `HandleShortcut`은 `V R A P T I Q W`만 처리한다. `M`/`X`는 도구를 바꾸지 않는다 (`AnnotationEditorControl.BuildToolRail`, `HandleShortcut`). 자르기는 릴리스 노트의 “Enter로 확정”과 달리 마우스 업에서 바로 `ApplyCrop`한다 (`docs/releases/2.4.0-release-notes.md`, `OnSurfaceMouseUp`).
- 영역 오버레이는 창 스냅·Tab 대상이 없다 (`CaptureOverlayView` 주석). `AutoDetectWindows`는 설정 모델에만 있고 설정 창 바인딩이 없다.

### 2.2 F3 핀

실제 단계:

1. `F3` (`PasteToScreen` 기본값) → `HandlePasteToScreen` → `PinManager.PasteFromClipboardAsync`.
2. 성공하면 커서 모니터에 맞춰 새 핀을 연다. 이미지가 있으면 이미지, 표/텍스트면 렌더와 원문을 같이 보존한다.
3. 핀에 포커스가 있을 때: 휠 확대, Ctrl+휠 투명도, 방향키 이동, `+`/`-` 확대, `0` 100%, `Ctrl+C` 이미지 복사, `Ctrl+S` 원본 PNG 빠른 저장, `Ctrl+Shift+S` 다른 이름, `Ctrl+더블클릭`은 원문 또는 OCR, 더블클릭·Esc·Delete는 닫기 (`PinWindow`).
4. `Shift+F3`는 모든 핀을 숨기거나 다시 보인다. 다시 보일 때 클릭 통과를 끈다. 클릭 통과 전역 키의 기본값은 비어 있다 (`HotkeySettings.ToggleClickThrough`, `PinManager.HideOrShowAll`).

마찰:

- 클립보드가 비었거나 형식이 아니면 정보 풍선, 잠겨 있으면 경고 풍선이다. 프로세스는 죽지 않는다. 같은 요청이 진행 중이면 다음 F3는 무시된다.
- `PinSettings.ClosedWindowRestoreLimit` 기본값 20은 설정 파일·초안에만 있다. `PinManager`는 닫힌 핀 스택을 두지 않고, 설정 창에도 이 필드가 없다. F3는 항상 현재 클립보드를 붙인다. Snipaste Getting Started(2026-01-06 편집)는 닫은 이미지를 붙여넣기 키로 되돌린다.
- 핀에 회전·반전 키가 없다. 편집기 쪽 `Q`/`W` 회전은 핀으로 이어지지 않는다.

### 2.3 라이브러리 재검색

실제 단계:

1. `Ctrl+Shift+Z`, 트레이 왼쪽 클릭·더블클릭, 또는 이미 떠 있는 인스턴스에 다시 실행하면 라이브러리가 앞으로 온다 (`HandleGalleryRequested`, `TrayIconService`의 `WM_LBUTTONUP`, `StartActivationListener`).
2. `Ctrl+F`가 검색 칸에 포커스한다. 입력은 곧바로 `SearchQuery`가 된다. 공백으로 나눈 단어는 모두 포함되어야 하고, 제목·창 제목·OCR 텍스트·미디어 종류를 대소문자 무시로 찾는다 (`CaptureTextSearch`, `GalleryWindow.OnSearchTextChanged`).
3. 필터는 전체·이미지·동영상·보관(핀)이다. 날짜 제목 클릭은 그날 묶음을 선택하거나 해제한다. 방향키, Enter(이미지 재편집 / 동영상 재생), `P`(보관 토글), `Ctrl+C`(이미지 복사), `G`(동영상은 GIF 내보내기 대화상자), Delete, 바깥 폴더로 드래그 복사가 있다 (`GalleryWindow.xaml.cs`).
4. 인덱스가 덜 되면 배너가 “지금 색인”을 안내한다. 자동 색인 실패는 캡처를 막지 않고 로그만 남긴다 (`RefreshOcrCoverageBanner`, `RunAutomaticIndexing`).

마찰:

- 그림 속 글자로 찾으려면 그 세대의 OCR이 끝나 있어야 한다. 색인 전에는 제목·창 제목만 맞는다.
- 이미지 기본 보관은 168시간이다. 핀(보관 플래그)되거나 편집 중인 항목은 만료에서 빠지고, 동영상은 7일 만료 대상이 아니다 (`QueueSettings.ImageRetentionHours`, README). 보관하지 않은 이미지는 일주일이 지나면 검색 대상에서 사라진다. OVERLAP trust.
- 왼쪽 클릭이 캡처가 아니라 라이브러리다. 캡처는 오른쪽 메뉴의 “캡처” 하위이거나 전역 키다 (`ThemedShellPresenter` 메뉴, `TrayIconService`).

### 2.4 영역 녹화 → GIF

실제 단계:

1. `Ctrl+Shift+X` → `HandleRecordRegion` → `RegionRecordingCoordinator.Toggle`. 선택 프레임 준비는 항상 100ms를 기다린다 (`PrepareSelectionAsync`). 정지 캡처는 3.0.0에서 직전 창이 닫힌 직후만 100ms를 기다린다 (`CaptureOverlayCoordinator.AcquireAndShowAsync`, `docs/releases/3.0.0-release-notes.md`).
2. 드래그를 놓으면 녹화 컨트롤이 열린다. 녹화는 아직 시작되지 않는다.
3. 시작은 기본 버튼, Enter, Space다 (`RecordingControlWindow.OnPrimaryClicked`, `OnKeyDown`). `UseStartDelay` 기본값은 꺼져 있어 카운트다운은 없다 (`RecordingSettings`). 설정 주석은 “지연이 꺼지면 단축키 한 번으로 즉시 시작”이라고 적지만, 그 즉시 시작은 영역 선택 이후의 시작 동작이다.
4. 같은 `Ctrl+Shift+X`, 중지 버튼, 또는 녹화 중 Esc가 멈춘다. Esc는 클립을 버리지 않고 저장하는 중지로 처리한다 (`CancelSession` 주석).
5. MP4 마무리가 끝날 때까지 컨트롤 창은 닫히지 않는다. 끝나면 영상 편집기가 자동으로 열린다. 라이브러리 기록은 편집기를 열기 전에 `CompleteCaptureAsync`로 끝난다.
6. 편집기에서 `G`는 GIF 내보내기다. 20초를 넘으면 끝점을 20초로 당긴 뒤 대화상자를 연다 (`VideoEditorWindow.ExportGif`, `VideoExportDialog.TryAutoTrimForGif`). GIF를 고르면 창이 뜨자마자 크기 계산이 시작된다 (`Loaded`). 품질은 표준 960px/10fps, 작게 640px/10fps, 최소 480px/5fps.
7. 저장은 다른 이름 대화상자다. 빠른 GIF 폴더는 없다. 계산이 끝나야 저장 버튼이 켜진다.

마찰:

- 영역 선택과 녹화 시작이 분리되어, “키를 누르고 시연하고 같은 키로 끝낸다”가 되지 않는다. 시작 전에 Esc하면 클립 없이 취소된다.
- 녹화 중 실수 취소를 고르면 파일이 라이브러리에 남는다. 지우기는 그 다음 라이브러리 Delete다.
- 확정 중(`_finishing`)에 같은 키를 누르면 새 녹화가 아니라 기존 창을 앞으로 가져온다.
- 정지 캡처는 녹화 준비·마무리 중이면 풍선으로 거절된다 (`GuardStillCapture`, `Text_90290F46A7D5`).

## 3. 잘된 편의

### 과제 고유 강점

이 제품이 목표로 둔 범위(무계정, 로컬 OCR, 핀, 비파괴 주석, 오디오 없는 설명 녹화) 안에서 단계가 짧다.

- 영역 캡처와 화면 고정을 나눴다. 캡처는 편집기와 원본 클립보드만 하고, 참조 창은 F3일 때만 생긴다 (`OnCaptureSelectionCompletedAsync` 주석, `docs/competitive-matrix.md` 1.6 절의 구현과 코드가 일치).
- 핀은 텍스트·표 원문을 이미지와 분리해 `Ctrl+C`와 `Ctrl+더블클릭`으로 나눈다 (`PinWindow.HandleCtrlDoubleClick`).
- 클릭 통과 키가 비어 있어도 `Shift+F3`를 두 번(숨김, 표시) 하면 마우스가 다시 핀에 닿는다 (`PinManager.HideOrShowAll`).
- 라이브러리 검색이 캡처 속 글자를 대상으로 하고, 색인이 덜 되면 배너로 다음 행동을 보여 준다.
- 녹화에서 GIF까지 `G` 한 번, 20초 자동 자르기, 열자마자 계산이 이어진다 (`docs/releases/2.4.0-release-notes.md`와 `VideoExportDialog`가 일치).
- 같은 영역 크기를 반복할 때 녹화 프리셋은 드래그를 위치만 남긴다 (`RecordingSettings.PresetWidth/Height`, `StartRegionSelection`).
- 3.0.0에서 핀이 Alt+Tab에 쌓이지 않게 한 것은 작업 전환 마찰을 줄인다 (릴리스 노트. 창 스타일 구현의 미학 평가는 하지 않음). OVERLAP aesthetics는 아님. 전환 목록은 워크플로다.

### 경쟁 도구도 하는 강점

- 전역 단축키, 트레이 상주, 영역 드래그 후 편집, 빠른 저장, 실행 취소. Snipaste·Greenshot·ShareX·캡처 도구와 같은 층이다.
- 단축키 충돌 시 이전 조합을 되돌린다. 앱 전역 키가 통째로 빠지지 않는다 (`GlobalHotkeyService.Reconfigure`). 시작 시 등록 실패는 메시지 상자 대신 트레이 풍선이다 (`App.InitializeShell`).
- 설정 초안은 중복 조합을 적용 전에 막고, 적용 중 OS 등록이 실패하면 저장 파일의 단축키도 이전 값으로 남긴다 (`SettingsDraft.ValidateAllHotkeys`, `SettingsApplyService.Apply`). 저장 자체가 실패하면 핫키와 시작 프로그램 등록을 되돌려 창을 연 채로 둔다.
- 두 번째 실행은 새 프로세스를 만들지 않고 라이브러리를 연다.
- 캡처·핀·커밋·녹화 예외는 트레이 풍선 또는 편집기 유지로 끝나고, 상주 프로세스를 메시지 펌프 밖으로 던지지 않는다.

## 4. 경쟁 우위

코드에 없는 동작만 적는다. 이번 세션에서 본문을 연 문서와, 열지 못한 주장을 구분한다.

| 제품 | 이번 세션에 연 문서 | MyCapture보다 짧은 경로 (코드에 없음) |
|---|---|---|
| Snipaste | [Getting Started](https://github.com/Snipaste/feedback/wiki/Getting-Started), 위키 편집 2026-01-06 | 트레이 왼쪽 클릭이 캡처(F1). 캡처 중 `Ctrl+T`·휠 클릭으로 바로 핀. 닫은 핀을 붙여넣기 키로 복구. 핀 `1`/`2` 회전, `3`/`4` 반전. 확대경에서 `C`로 색 복사. 캡처 히스토리 `,`/`.` |
| ShareX | [이미지 편집기](https://getsharex.com/docs/image-editor) 본문 확인. 녹화 가이드 URL `https://getsharex.com/blog/how-to-record-screen-windows/` 는 404 | 캡처 후 작업을 저장·복사·편집·업로드로 나누고, Enter가 그 사슬을 이어 간다. 편집기에서 핀(`Ctrl+P`), 단계 번호, 형광펜, 블러가 같은 창에 있다. 업로드 자동화는 이 제품 목표가 아니므로 감점하지 않고 포지션 겹침으로만 둔다. OVERLAP positioning. 녹화 “영역 선택 후 컨트롤로 종료”는 검색 스니펫뿐이라 **문서상 주장, 이번 세션 미재확인**. |
| 알캡처 | [제품 페이지](https://altools.co.kr/product/ALCAPTURE), 2026-09-17 v3.28 이력 포함 | 공식 페이지가 확인한 것: 사각형·자유형·단위영역·창·전체·스크롤·지정크기 7모드, 최근 목록 최대 100장, 그리기 편집. 자유형·단위영역·캡처 직후 결과창을 끄는 옵션은 MyCapture 코드에 없다. 모드별 기본 단축키는 공식 페이지에 없어 **문서상 주장, 이번 세션 미재확인**. |
| Windows 11 캡처 도구 | [Microsoft Support](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots), Windows 11 절 | `Win+Shift+S` 한 오버레이에서 사각형·창·전체·자유형. `Win+Shift+R` 후 Start로 동영상. 캡처가 Screenshots 폴더에 자동 저장되고, 설정에서 자동 저장을 바꿀 수 있다. 텍스트 작업의 빠른 가리기(이메일·전화)는 캡처 직후 창에 있다. Copilot+ 전용 맞춤 캡처·색 선택은 일반 Windows 11 경로로 치지 않는다. |
| Greenshot | [getgreenshot.org/help](https://getgreenshot.org/help/), 도움말 1.2.10 | 기본 키가 모드마다 있다. 영역 Print, 창 Alt+Print, 전체 Ctrl+Print, 마지막 영역 Shift+Print. 영역 중 Space로 창 모드. 출력 대상을 캡처 직후 고른다. 편집기에서 형광펜·블러·픽셀화·Enter 확정 자르기. 여러 편집기를 동시에 연다. |
| PicPick | [picpick.app 핫키](https://picpick.app/en/help/hotkeys/) | 전체 PrintScreen, 활성 창 Alt+PrintScreen, 컨트롤 Ctrl+PrintScreen, 스크롤 Ctrl+Alt+PrintScreen, 영역 Shift+PrintScreen, 고정 영역 Shift+Ctrl+PrintScreen, 자유형 Shift+Ctrl+Alt+PrintScreen. 반복 캡처 기본값은 None으로, 이 항목은 MyCapture와 같다. |

MyCapture가 이 비교에서 막히지 않는 지점: F3 텍스트·표 핀, 로컬 OCR 검색, 비파괴 재편집, 오디오 없는 영역 녹화 후 20초 GIF. 이들은 3절의 고유 강점이다. 경쟁 문서에 “없다”고 단정하지 않는다.

## 5. 보완 과제

### P0

1. 영역 녹화는 영역을 놓은 뒤 시작 확인이 한 번 더 있다. 같은 키로 시작하고 끝낸다는 README 기대와 어긋나 시연 흐름이 끊긴다. 근거: `RecordingControlWindow.OnPrimaryClicked`, `RegionRecordingCoordinator.Toggle`, `RecordingSettings` 주석. 겹침: 없음. 주 소유: convenience.
2. 완료·빠른 저장·다른 이름 저장이 클립보드 복사에 닫힘을 건다. 복사가 실패하면 “저장이 완료되지 않았습니다”라 이미 큐에 들어간 뒤에도 사용자가 저장을 다시 시도한다. 근거: `CaptureCommitService.CommitAsync` `Done`/`QuickSave`, `AnnotationEditorControl.Commit`, `Text_06F58809C083`. 겹침: learnability (문구). 주 소유: convenience.

### P1

1. 창·전체·이전 영역·클릭 통과는 전역 명령인데 기본 키가 비어 트레이 “캡처” 하위로만 간다. 반복 캡처마다 메뉴다. 근거: `HotkeySettings`의 `Hotkey.None` 네 개, `App` 트레이 메뉴. 겹침: learnability, functionality. 주 소유: convenience.
2. 단축키 변경은 키를 누르는 칸이 아니라 `Ctrl+Shift+C` 문자열 칸이다. 오타는 적용 전에 막히고, 중복은 “‘{키}’ 단축키가 중복되었습니다”로 막히며, OS가 거부하면 “이전 값으로 되돌렸습니다” 상자가 뜬다. 되돌림은 안전하나 재지정은 느리다. 근거: `SettingsWindow.xaml` 단축키 탭, `SettingsDraft.ValidateAllHotkeys`, `SettingsApplyService`. 겹침: learnability. 주 소유: convenience.
3. 영역 캡처 키를 비우면 로드 시 `Ctrl+Shift+C`로 되돌린다. 다른 도구와 겹칠 때 그 키를 끄는 방법이 설정 창에 없다. 근거: `SettingsStore` “unassigned value is restored”. 겹침: trust (다른 앱과의 키 소유). 주 소유: convenience.
4. 편집기 `Ctrl+C`는 선택 도형이 아니라 화면 전체를 확정·복사·닫기한다. 주석 도중에 누르면 편집이 끝난다. 근거: `AnnotationEditorControl.HandleShortcut`. 겹침: learnability. 주 소유: convenience.
5. 모자이크 `M`, 자르기 `X`가 버튼에만 있고 키 처리에 없다. 자르기는 마우스 업에 즉시 적용된다. 근거: `BuildToolRail`, `HandleShortcut`, `OnSurfaceMouseUp`. 겹침: learnability. 주 소유: convenience.
6. 닫은 핀 복구 한도가 설정 모델에만 있고 동작이 없다. F3는 클립보드가 비어 있으면 방금 닫은 핀 대신 안내 풍선이다. 근거: `PinSettings.ClosedWindowRestoreLimit`, `PinManager.PasteFromClipboardAsync`. 겹침: functionality. 주 소유: convenience.
7. 보관하지 않은 라이브러리 이미지는 기본 7일 뒤 검색에서 사라진다. 다시 찾기 여정이 여기서 끊긴다. 근거: `QueueSettings.ImageRetentionHours`, README 보관 절. 겹침: trust. 주 소유: trust (보관 정책). convenience는 여정 마찰로만 기록하고 점수 이중 계산을 피한다.

### P2

1. 영역 선택에 창 호버가 없어 창 캡처는 별도 명령이다. 그 명령의 기본 키가 없다. 근거: `CaptureOverlayView` 주석, `HandleCaptureWindow`. 겹침: functionality. 주 소유: functionality.
2. 형광펜·번호 단계·블러·색 뽑기는 편집 경로에 없다. `HighlighterAlpha`·`ColorFormat`은 설정 모델에만 있다. 단계 설명 캡처가 사각형·화살표·텍스트로만 길어진다. 근거: `EditorTool`, `AppSettings.Capture.ColorFormat`. 겹침: functionality. 주 소유: functionality.
3. GIF는 계산 뒤 항상 다른 이름 저장이다. 이미지 빠른 저장 폴더에 해당하는 GIF 한 단계가 없다. 근거: `VideoExportDialog.SaveAsync`. 겹침: functionality. 주 소유: convenience.
4. 녹화 선택만 매번 100ms를 기다린다. 정지 캡처 3.0.0 개선이 녹화 시작에는 없다. 체감 지연의 수치 평가는 performance에 맡긴다. 근거: `RegionRecordingCoordinator.PrepareSelectionAsync`. 겹침: performance. 주 소유: performance.
5. 언어 변경은 다음 실행부터다. 트레이에서 바꿔도 즉시 메뉴 문구가 바뀌지 않고 재시작 안내가 뜬다. 근거: `ChangeTrayLanguage`, `SettingsApplyService` language 분기. 겹침: learnability. 주 소유: learnability.

## 6. 범위 밖

마이크, 시스템 오디오, OBS급 장면 구성, 스트리밍, 하드웨어 인코더 선택은 제품이 뺀 범위다. README와 `docs/competitive-matrix.md`가 오디오 제외를 명시한다. 이 축에서 감점하지 않는다. ShareX·캡처 도구 문서의 오디오·Clipchamp·업로드 사슬도 같은 이유로 점수에 넣지 않았다.

## 7. 미확인 목록

- 알캡처 모드별 기본 단축키. 공식 제품 페이지는 7모드와 최근 100장만 확인했다.
- ShareX 화면 녹화의 정확한 클릭 수. 가이드 URL이 404였다. 이미지 편집기 문서만 본문으로 확인했다.
- Snipaste 현재 설치본의 기본 캡처 키가 위키의 F1과 같은지. 위키는 2026-01-06 편집본이다. 설치 바이너리는 실행하지 않았다.
- Greenshot 1.2.10 도움말 이후 빌드의 키 변경. PicPick 핫키 페이지의 녹화 키(검색에 F9가 보였으나 핫키 페이지 본문에는 캡처 표만 있었다).
- 실제 Windows 세션에서 단축키 1409 충돌, 클립보드 잠김, 녹화 확정 중 닫기 거부의 체감. 코드 경로와 문구는 확인했고 GUI 재현은 하지 않았다.
- `AutoDetectWindows`를 읽는 런타임 분기가 설정 창 밖에 더 있는지. 캡처 오버레이는 창 스냅을 하지 않는다고 주석에 있다.
- 편집기 완료 후 클립보드 실패가 난 상태에서 Esc하면 큐 항목이 남는지. `HandleCommitAsync`는 첫 실패 전에 `_currentRecord`를 채우고, Esc는 그 레코드를 지우지 않는 것으로 읽힌다. 테스트 실행은 하지 않았다.
