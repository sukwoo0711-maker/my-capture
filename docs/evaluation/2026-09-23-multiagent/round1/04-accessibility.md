# 접근성 평가 (라운드 1)

대상: Release 3.0.0 제품 소스 (`117b0ef`). 이 문서는 코드·테스트·XAML·디자인 문서 감사다. 이 환경에서 내레이터, 돋보기, Windows 고대비, 텍스트 크기 슬라이더를 실사용하지 못했다. 점수는 스크린 리더 통과 점수가 아니다.

평가 렌즈: WCAG 2.2 AA를 데스크톱에 적용한 이름, 역할, 키보드, 포커스, 대비, 모션 감소, 텍스트 크기. `docs/market/`는 인용하지 않았다. 경쟁 제품 창과 공식 페이지는 이번 세션에서 열지 않았다.

## 1. 점수

**3 / 5. 코드 감사 점수.**

공용 컨트롤, 설정, 라이브러리, 편집기 아이콘, 녹화 프레임, 타임라인 일부에는 이름·포커스 링·키보드 대안·대비 테스트·모션 감소가 있다. 제품의 첫 동작인 자유 영역 선택과 주석 도형의 배치·이동은 포인터 히트테스트에 남아 있다. OS 고대비는 콤보·스크롤바·캡션에만 붙고, 창 대부분은 앱 팔레트를 유지한다. 핵심 경로는 키보드로 일부 완료되지만, 영역과 도형은 체감 공백이다.

5는 이 제품 범위에서 캡처 도구 상위권(이름·키보드·포커스·대비가 커스텀 표면까지 닫힘)이다. 1은 이름·키보드·포커스가 사실상 없는 상태다. 현재는 그 사이, 공용 크롬은 갖춰졌고 커스텀 히트테스트 표면이 열려 있다.

## 2. 잘된 것

### 이름과 라이브 영역

- 편집기 아이콘은 생성 경로에서 이름을 붙인다. `AnnotationEditorControl.AddToolButton` / `IconButton` / 색상 견본(`AutomationName`)이 `AutomationProperties.SetName`과 `ToolTip`을 같이 설정한다. `tests/MyCapture.App.Tests/EditorLayoutAccessibilityTests.cs`의 `EveryIconControlHasAutomationNameAndTooltip`이 아이콘 전용 `ToggleButton` 8개와 모든 `Button` 이름을 검사한다. 상태 문장은 `AutomationLiveSetting.Polite`이고, `ListenerExists`일 때 `LiveRegionChanged`를 올린다 (`AnnotationEditorControl` 상태 갱신).
- 카운트다운은 숫자 레이블에 이름 `지연 캡처 카운트다운`과 Polite 라이브 영역을 두고, `Text` 변경만으로는 WPF가 이벤트를 안 올린다는 주석과 함께 틱마다 `LiveRegionChanged`를 발생시킨다. `Capture/CountdownWindow.cs`, `tests/MyCapture.App.Tests/CountdownWindowAccessibilityTests.cs`.
- OCR 결과 상태도 같은 이유로 `LiveTextBlock`이 이벤트를 올린다. `Ocr/OcrResultWindow.cs`.
- 설정 오류 요약은 Assertive 라이브 영역이고, 적용 실패 시 포커스를 요약으로 옮긴 뒤 이벤트를 올린다. `Settings/SettingsWindow.xaml` (LiveSetting), `SettingsWindow.xaml.cs` `AnnounceErrors`.
- 설정 XAML의 `AutomationProperties.Name`은 64곳이다. 유효성 오류는 `AutomationProperties.HelpText`로 연결된다.
- 라이브러리 카드의 핀·복사·편집·삭제·더보기는 아이콘만 보이고 이름과 툴팁이 있다. `Gallery/GalleryWindow.xaml`. 타일 `ListBoxItem`은 `AccessibleName`에 묶인다.
- 동영상 편집기 `MakeButton`은 이름과 툴팁을 항상 설정한다. 오버레이 목록 항목도 시간 범위가 포함된 이름을 받는다. `Recording/VideoEditorWindow.cs`.
- 레이어 타임라인 이름 `영상 레이어 타임라인`, 전체 스트립 이름 `전체 타임라인`, 세부 스트립 초기 이름 `세부 프레임 타임라인`. `VideoLayerTimeline.cs`, `TwoLineTimeline.cs`, 문자열 `Strings.resx`. 설계 `docs/design/video-editor-timeline-ux.md` 7절이 요구한 두 슬라이더 이름과 맞다. 세부 스트립 이름은 이후 캡션 문장으로 갱신된다 (아래 위험).
- 녹화 영역 프레임은 커스텀 `RegionFrameAutomationPeer`(역할 Pane)와 도움말 "Tab으로 선택한 뒤 방향키로 이동합니다"를 갖는다. `RecordingControlWindow.cs`, `tests/MyCapture.App.Tests/RecordingFeatureTests.cs`.
- 핀 창 이름 `MyCapture 화면 고정 창`과 도움말, 상황 메뉴 항목 이름이 있다. `Pinning/PinWindow.cs`.
- 트레이 풍선 팁은 앱 이름과 상태 문자열이다. `Platform/Shell/TrayIconService.cs` `BuildTooltip`. 트레이 메뉴 헤더 10개는 `&` 니모닉이다 (`설정(&O)`, `영역 캡처(&R)`, `라이브러리(&G)` 등). `Strings.resx`.

### 포커스

- 공용 `Button` / `ToggleButton` / `TextBox` / `CheckBox` / `RadioButton`은 `FocusVisualStyle={x:Null}`이지만 템플릿 안쪽 `FocusRing`을 `IsKeyboardFocused`에서 투명도만 바꾼다. 두께는 고정이다. `Themes/Controls.xaml`. `MASTER.md`의 "inset 1px, 레이아웃이 밀리지 않음"과 같다.
- 설정 분류 탭은 같은 방식의 1px 테두리 색 변경이다. `SettingsWindow.xaml` `Settings.TabItem`. 로드 시 선택 탭으로 포커스를 보낸다. `SettingsWindow.xaml.cs`.
- 라이브러리 타일은 기본 포커스 애드너를 끄고 `IsKeyboardFocusWithin`일 때 `Border.Focus`를 그린다. `GalleryWindow.xaml`.
- 레이어 타임라인은 키보드 포커스 때 외곽선을 다시 그린다. `VideoLayerTimeline.OnRender`.
- 녹화 프레임은 포커스 획득 시 `Border.Focus`로 바꾼다. `RecordingControlWindow.BuildRegionFrame`.
- 동영상 레이어 캔버스는 포커스 시 펜 두께를 1에서 2로 올린다. `VideoLayerCanvas.cs`.

### 키보드 대안이 있는 표면

- 캡처 오버레이는 포커스를 받고(`CaptureOverlayWindow`가 `_view.Focus()`), Esc 취소, Enter 확정, Ctrl+A로 프레임 전체, 선택이 이미 있을 때 방향키 이동과 Shift+방향키 크기 조절을 한다. `CaptureOverlayView.OnKeyDown`. 임의 사각형을 키보드로 새로 만드는 경로는 없다 (3절).
- 녹화 프레임은 포커스가 프레임 안에 있을 때 방향키로 이동한다. Shift는 10 DIP. Enter/Space는 시작. `RecordingControlWindow.OnKeyDown`. 이 창의 포인터 동작도 이동(`DragMove`)이라, 이동에 대한 키보드 대안은 있다. 크기 조절 핸들은 이 창에 없다.
- 동영상 편집기 창 키: 좌우 탐색, Home/End, I/O 트림, Space 재생, `,` `.` 프레임, Ctrl+Shift+± 확대, Ctrl+Z/Y, Delete, F2, G. `VideoEditorWindow.OnKeyDown`. 전송 막대에 확대·전체 보기 버튼이 있다.
- 트림 모드에서 타임라인이 포커스를 가진 동안 Tab이 In/Out 핸들을 바꾸고 방향키·Home/End가 그 핸들을 움직인다. `TwoLineTimeline.OnPreviewKeyDown`.
- 레이어 타임라인은 위/아래로 레이어를 바꾸고, 좌우로 시작(Shift면 끝)을 10ms 또는 Ctrl 시 100ms 단위로 조절한다. `VideoLayerTimeline.OnKeyDown`. 레이어 캔버스는 선택 항목을 방향키로 이동하고 Shift면 크기를 조절한다. `VideoLayerCanvas.OnKeyDown`.
- 편집기 도구 단축키 V/R/A/P/T/I, 실행취소 Ctrl+Z, 다시실행 Ctrl+Y, Delete, 회전 Q/W, 커밋 Ctrl+Enter, 복사 Ctrl+C. `AnnotationEditorControl.HandleShortcut`. 창이 `OnPreviewKeyDown`에서 이 처리기로 보낸다.
- 라이브러리 타일: 방향키, Enter, Delete, Space, Ctrl+A, Ctrl+C, P, 이미지 T, 영상 G. `GalleryWindow.xaml.cs`.
- 핀: 포커스가 창에 있는 동안 방향키 이동, ± 확대, 0 맞춤, Ctrl+C, Ctrl+S, Ctrl+Shift+S, Esc/Delete 닫기. `PinWindow.OnKeyDown`. `PinManager`는 `Show` 후 `Activate`한다.

### 대비, 고대비 팔레트, 모션, 타깃 크기

- `ThemeContrast`는 WCAG 상대 휘도다. `tests/MyCapture.Core.Tests/ThemeContrastTests.cs`는 모든 `AppTheme`에서 본문 쌍 4.5:1, 일차 텍스트 7:1, 유리 테마는 밝은/어두운 Mica 합성 4.5:1을 요구한다.
- 기본 토큰 사전 테스트는 `Border.Focus` 대 `Surface.Base`/`Raised`, `Overlay.SelectionBorder` 대 `Surface.Canvas`를 3:1 이상으로 본다. `ThemeResourceAvailabilityTests.FocusAndSelectionSemanticsUseTheBrightFocusPrimitive`. 소스 색을 같은 공식으로 계산하면 포커스 대 베이스는 Midnight 11.83, Daylight 3.44, HighContrast 14.27, Workspace 10.06이다. 포커스 지표 자체는 3:1을 넘는다.
- 앱 고대비 팔레트(`AppTheme.HighContrast`, 노란 강조 `#FFD000`, 흰 글자, 검정 바탕)는 설정 콤보 `high-contrast`로 고른다. `AppTheme.cs`, `SettingsWindow.xaml`.
- OS 고대비가 켜지면 캡션 색을 덮어쓰지 않는다. `ModernWindowChrome.cs`의 `SystemParameters.HighContrast` 가드. 콤보박스·항목·스크롤바 썸/트랙은 `SystemColors`로 바뀐다. `Controls.xaml` 해당 트리거 7곳.
- 모션은 불투명도와 변환만 쓰고, 제스처 시점에 `SystemParameters.ClientAreaAnimation`을 본다. `Themes/FluidMotion.cs`. 핀의 `BeginAnimation` 호출도 `FluidMotion.AnimationsEnabled`가 꺼지면 최종값으로 건너뛴다. `PinWindow.cs`. 감소 모션 테스트가 창 등장 변환을 비활성으로 고정한다. `ThemeResourceAvailabilityTests`.
- 히트 타깃 토큰은 36px, 컴팩트 32px. 레일 도구는 40px, 색상 견본은 40px. `Tokens.xaml`, `Controls.xaml` `Rail.ToolButton`. WCAG 2.2의 24px 최소보다 크다. 체크박스 글리프는 18px이지만 컨트롤 `MinHeight`는 32라 클릭 영역은 행 전체다.
- DPI는 `app.manifest`의 `PerMonitorV2`다.
- 타임라인 삭제 구간은 색 위에 해치를 그린다. `TwoLineTimeline` 해치 루프, `VideoLayerTimeline`의 `hatch: true`. `MASTER.md`의 "색만으로 상태를 전하지 않음"과 맞는다.
- 상태 색 옆에 툴팁·상태 문장이 있다. 브랜드 절의 "색만으로 상태를 전하지 않음"은 아이콘 버튼 툴팁·라이브 상태와 함께 구현돼 있다.

### 1.7 / 1.9 약속과 현재 코드

- `docs/ux-improvements-v1.7.0-plan.md` 9번(타이포·정렬·포커스·톤 검토)은 접근성 전용 항목이 아니다. `docs/design/1.7.0-ui-review.md`는 포커스 링이 필요한 콤보·스크롤바를 고쳤다고 적고, "DPI 및 키보드" 칸은 96/144/192 렌더만 확인했으며 조작별 키보드 결과는 별도 기록이 필요하다고 남겨 두었다. 3.0.0 코드에는 그 이후의 포커스 링·이름 테스트가 있다. 1.7 문서가 키보드 조작 통과를 증명하지는 않는다.
- `docs/ux-v1.9.0-plan.md`는 동영상 편집기의 키보드 대안과 최소 크기를 유지하라고 했다. 현재 `VideoEditorWindow`·`TwoLineTimeline`·`VideoLayerTimeline`·`VideoLayerCanvas`에 그 대안이 있다. 이 문장이 캡처 오버레이와 주석 캔버스까지 포함하지는 않는다.
- `docs/design/video-editor-timeline-ux.md` 7절(슬라이더 이름, 방향키·Home/End·I/O, 트림을 색만으로 표시하지 않음)은 코드와 맞다. 두 줄 타임라인의 포커스 링은 그 절에 없고, 구현에도 없다.

## 3. 실패와 위험

### 자유 영역 캡처 (키보드, 이름)

`CaptureOverlayView`와 `CaptureOverlayWindow`에는 `AutomationProperties`가 0건이다. 창 `Title`은 `MyCapture — 자유 영역 선택`이다. 안내·좌표·색 샘플은 `DrawingVisual` + `FormattedText`라 UI 자동화 트리에 텍스트로 없다. 안내 문자열은 "드래그해 캡처할 영역을 선택하세요 · Shift 정밀 이동 · 놓으면 확정 · Esc 취소"이고, Ctrl+A와 방향키를 말하지 않는다 (`Strings.resx` `Text_798E080A9262`).

키보드로 가능한 확정은 이미 있는 선택(또는 Ctrl+A로 만든 모니터 전체)이다. `NudgeSelection`은 `_selection`이 없으면 반환한다. 포인터 없이 임의 사각형을 만들 경로는 없다. 주석이 "Ctrl+A + Enter가 전체 모니터 대안"이라고 적은 것과 같다. 전체 모니터는 대안이고, 영역 캡처의 본래 동작은 아니다.

오버레이 뷰는 `Focusable`이고 표시 시 포커스를 받는다. 포커스 링을 그리는 코드는 없다.

### 주석 캔버스 (드래그 전용 기하)

도형·화살표·펜·모자이크·자르기·텍스트 배치·이미지 삽입은 `OnSurfaceMouseDown`의 포인터 좌표와 `_controller.HitTest` / `PointerDown`뿐이다. `HandleShortcut`에는 방향키 이동·크기 조절이 없다. 텍스트 상자는 캔버스를 클릭해야 `PlaceTextBox`가 열린다. 도구 전환, 삭제, 실행취소, 커밋은 키보드로 된다. 기하를 만드는 동작은 포인터만 된다.

레일 이름은 `{도구} 도구 ({단축키})`다. 모자이크는 `M`, 자르기는 `X`로 이름이 만들어진다 (`AddToolButton` 호출). `HandleShortcut`의 switch에는 `Key.M`과 `Key.X`가 없다. Tab으로 토글에 도달해 Space/Enter로 도구를 고를 수는 있다. 읽히는 글자 단축키와 동작이 어긋난다. 편집기 주석의 "every icon control carries a keyboard route"는 이 두 글자에 대해 코드와 다르다.

편집기 루트는 `FocusVisualStyle = null`이고, 창이 뜬 뒤 `Keyboard.Focus(_editor)`로 그 루트에 포커스를 둔다. `AnnotationEditorWindow.OnContentRendered`. 루트에는 포커스 링이 없다. 이후 Tab은 자식 버튼의 링으로 간다.

### 두 줄 타임라인 포커스

`TwoLineTimeline`은 `Focusable`이고 트림 키를 받지만, `IsKeyboardFocused`로 링을 그리지 않는다. 레이어 타임라인과 다르다. 재생헤드 이동의 키는 창에 있어서, 타임라인 자체에 포커스가 보여야 한다는 `MASTER.md` 문장("focus must remain visible while timelines scroll")은 레이어 타임라인에는 해당하고 두 줄 타임라인에는 약하다. 재생헤드는 탐색 때 세부 뷰 안으로 따라온다 (`SetPlayhead`의 `CenterDetailOn`).

`UpdateRangeCaptions`가 세부 스트립의 `AutomationProperties.Name`을 "세부 프레임 타임라인"에서 범위 캡션 문장으로 덮어쓴다. 이름이 없어지지는 않는다. 역할은 여전히 커스텀 드로잉 표면이다. 레이어 타임라인만 `TimelineAutomationPeer`를 갖는다.

레이어 바 높이는 20px, 핸들 존은 10px다 (`HandleZoneWidth`). 키보드 대안이 있으므로 조작 불가는 아니다. 포인터 타깃은 24px 미만이다.

### OS 고대비와 컴포넌트 경계

`ThemeService.Apply`는 `SystemParameters.HighContrast`를 보지 않는다. 고대비 팔레트는 사용자가 고를 때만 적용된다. OS 고대비 트리거는 `Controls.xaml`의 콤보·목록 항목·스크롤바와 `ModernWindowChrome` 캡션 가드뿐이다. 버튼, 텍스트 상자, 체크박스, 탭, 타임라인, 오버레이, 핀, 편집기 캔버스는 OS `SystemColors`로 바뀌지 않는다.

소스 색 계산 (렌더 측정 아님, `ThemeContrast`와 같은 상대 휘도):

| 쌍 | 대비 |
|---|---|
| Midnight `Border.Subtle` `#2B3A50` / `Surface.Base` `#0B0F17` | 1.67:1 |
| Daylight `Border.Subtle` `#C5D0DE` / `Surface.Base` `#F7F8FB` | 1.47:1 |
| Midnight `Border.Strong` / Base | 2.69:1 |
| Daylight `Border.Strong` / Base | 2.63:1 |
| Midnight `Surface.Overlay` / Base | 1.14:1 |

휴식 상태 버튼 경계가 이 색만으로 구분되면 비텍스트 3:1에 못 미친다. 포커스·선택 색은 3:1을 넘는다. 테스트는 텍스트 4.5:1을 모든 팔레트에서 잠그고, 포커스 3:1은 기본 토큰 사전에서만 잠근다. `Border.Subtle` 3:1 테스트는 없다. 유리 테마 테두리는 알파가 있어 위 표에 넣지 않았다.

### 핀으로 돌아가는 키보드 경로

핀은 `ShowInTaskbar = false`이고 `WindowStyleFacade.ExcludeFromAltTab`이다. 생성 시 `Activate`되므로 그 순간 방향키는 동작한다. 포커스가 떠난 뒤 Alt+Tab이나 작업 표시줄로 핀에 돌아올 코드는 없다. 클릭스루는 상황 메뉴로만 토글되고 (`ToggleClickThrough`), `OnKeyDown`에 대응 키가 없다. 클릭스루가 켜지면 포인터도 창을 지나간다. 피드백은 `TextBlock`이고 라이브 영역이 아니다.

### 포커스 비주얼을 끈 목록

암시적 `ListBoxItem`·`TabItem` 스타일이 `FocusVisualStyle`을 끄고, 목록 항목은 대체 템플릿이 없다. `Controls.xaml`. 설정 탭은 키 스타일 `Settings.TabItem`이 링을 다시 그린다. 동영상 오버레이 `ListBox`는 기본 항목 템플릿을 유지하므로 선택 하이라이트는 남는다. 포커스와 선택이 같은 표시일 수 있다. 키보드 포커스만 있고 선택이 아닌 상태는 이 스타일에서 링이 없다.

### 텍스트 크기

글자 크기는 DIP 토큰 12/13/14/17/24/28과 코드의 고정 `FontSize`다. `UserPreferenceChanged`, 텍스트 배율 API, `SystemFonts` 구독은 소스에 0건이다. 모니터 DPI 배율은 매니페스트로 받는다. Windows "텍스트 크기" 슬라이더가 런타임에 반영되는지는 확인하지 못했다.

## 4. 경쟁 비교

이번 세션에서 Snipaste, ShareX, Greenshot, 알캡처, PicPick, ScreenToGif, Windows 캡처 도구의 창이나 공식 문서를 열지 않았다. 아래는 그 한계 안에서만 적는다. 기능 유무를 경쟁 제품에 단정하지 않는다.

### 경쟁 우위

Windows 캡처 도구는 내레이터로 영역을 고르는 기대가 큰 편으로 알려져 있다. MyCapture 자유 영역은 창 제목과 Ctrl+A(전체)·Esc·Enter까지이고, 임의 사각형과 안내 문장은 자동화 트리 밖에 있다. 이 기대와 맞추려면 영역 선택 자체가 키보드와 이름으로 닫혀야 한다. 경쟁 빌드에서 그 경로를 재현하지는 않았다.

### 과제 고유 강점

로컬 주석 편집기, 핀, 오디오 없는 설명 녹화, 두 줄 타임라인이라는 범위 안에서 이름·라이브 영역·대비 테스트·모션 감소·레이어 키보드를 코드와 테스트로 고정한 점은 캡처 유틸에서 자주 비어 있는 층이다. 편집기 아이콘 이름 테스트, 카운트다운 라이브 영역, 녹화 프레임 피어, 전 팔레트 텍스트 대비 테스트가 그 증거다. 이 강점은 영역 선택과 도형 기하가 포인터 전용인 사실과 같이 있다.

### 공통 약점

캡처 유틸은 전체 화면 히트테스트, 아이콘 도구 레일, 타임라인 드래그를 많이 쓴다. 그 표면에서 내레이터 이름과 키보드 기하가 약한 것은 범주의 공통 문제다. MyCapture도 오버레이와 주석 캔버스에서 같은 문제를 가진다. 공용 버튼까지 이름이 없는 상태는 아니다.

### 범위 밖

OBS급 스트리밍, 오디오 믹서, 시스템 전체 내레이터 인증 배지는 이 제품의 목표 밖이다. 감점하지 않는다. 고대비 "인증"을 공공 조달 기준으로 요구하는 시장 문서는 현재 사실로 쓰지 않았다.

## 5. 보완과 OVERLAP

| 순위 | 항목 | OVERLAP | 주 소유 |
|---|---|---|---|
| P0 | 자유 영역: 키보드로 임의 사각형을 만들고, 안내를 자동화 트리에 넣기. 창 제목만으로는 좌표·단축키가 전달되지 않는다. | convenience (캡처 시작), learnability (안내 문구) | accessibility |
| P1 | 주석 선택 도형의 방향키 이동·크기. 텍스트 배치의 키보드 시작점. | functionality (편집 모델) | accessibility |
| P1 | 이름에 있는 M/X를 `HandleShortcut`에 연결하거나 이름에서 빼기. | learnability (툴팁·단축키 학습) | accessibility (이름과 동작 불일치). 문구 품질은 learnability |
| P1 | OS 고대비에서 버튼·텍스트·커스텀 표면이 `SystemColors` 또는 고대비 팔레트를 따르게. 수동 테마와 OS 모드를 구분. | aesthetics (팔레트) | accessibility |
| P1 | 휴식 상태 `Border.Subtle`/`Border.Strong`의 비텍스트 3:1. 포커스 색은 이미 통과. | aesthetics (경계 색) | accessibility가 대비 수치, aesthetics가 색 선택 |
| P1 | 편집기 초기 포커스를 링이 있는 컨트롤로. 두 줄 타임라인에 포커스 링. | aesthetics (포커스 표현) | accessibility |
| P1 | 핀: 포커스 복귀 경로. 클릭스루의 키보드 토글. 피드백을 라이브 영역으로. | convenience (핀 조작) | accessibility. 제스처 단축키 체계는 convenience |
| P2 | Windows 텍스트 크기 배율. 지금은 DPI만 선언. | learnability (읽기) | accessibility |
| P2 | 타임라인 핸들 10px, 스크롤바 트랙 높이 16 (`Controls.xaml` 가로 스크롤바 `Height=16`). 키보드 대안이 있는 핸들은 후순위. | aesthetics (밀도) | accessibility |
| P2 | 설정 폼 액세스 키. 트레이 메뉴 10개 니모닉은 이미 있다. XAML `AutomationProperties` 밑줄 니모닉은 0건. | convenience (단축키) | convenience가 전역 단축키, accessibility가 대화상자 니모닉 |
| P2 | 세부 타임라인 이름을 안정적인 "세부 프레임 타임라인"으로 유지하고 범위는 HelpText로. | learnability | accessibility |

이중 채점 방지: 포커스 시안/노랑 토큰의 브랜드 적합성, 아이콘 스타일, 모션의 시각적 톤은 aesthetics가 소유한다. 이 축은 대비 수치와 포커스 가시성만 소유한다. `Ctrl+Shift+C/X/Z` 체계와 편집 단축키의 기억 용이성은 convenience와 learnability가 소유한다. 이 축은 그 키가 포인터 전용 동작을 대체하는지, 그리고 자동화 이름과 일치하는지만 소유한다.

## 6. 미확인

- 내레이터, 돋보기, 음성 액세스, 실제 OS 고대비 세션, 실제 텍스트 크기 슬라이더.
- 창 `Title`만 있는 캡처 오버레이가 내레이터에서 어떻게 읽히는지. 코드는 제목을 설정하고 내부 뷰 이름은 설정하지 않는다.
- 라이브러리 "비우기" 버튼: 이름 특성이 자식 `TextBlock`에만 있다 (`GalleryWindow.xaml` `SummaryLabel`). 버튼 이름으로 승격되는지는 UIA 실측이 없다.
- 유리 테마 반투명 테두리의 합성 비텍스트 대비. 텍스트 합성 4.5:1만 테스트가 잠근다.
- `Border.Subtle` 계산은 `AppTheme.cs` 불투명 RGB다. 런타임 브러시 오버레이나 ClearType은 포함하지 않았다.
- 경쟁 제품의 현재 내레이터 지원 범위.
- 1.7 검토가 말한 "조작별 키보드 결과"의 후속 실기 기록. 저장소의 그 문장은 미기록으로 남아 있다.

## 검색 건수 (편중)

수치는 2026-09-23 작업 트리의 `src`/`tests` ripgrep 일치 줄 수다. 샘플만 인용하지 않기 위해 적는다.

| 질의 | 범위 | 일치 |
|---|---|---|
| `AutomationProperties` | `src` (cs+xaml) | 174줄, 21파일. 설정 XAML 70, 갤러리 XAML 21, `VideoEditorWindow.cs` 18이 상위. 캡처 오버레이 2파일은 0 |
| `AutomationProperties` | `tests` | 28줄, 7파일. `EditorLayoutAccessibilityTests.cs` 13이 최대 |
| `AutomationProperties.SetName` | `src` `*.cs` | 51호출, 16파일. 편집기 컨트롤은 1개의 헬퍼로 모은 뒤 버튼마다 호출 |
| `AutomationProperties.Name` | `src` `*.xaml` | 86. 설정 64, 갤러리 21, `Controls.xaml` 1 |
| `FocusVisualStyle` | `src` | 11, 모두 `{x:Null}` 또는 C# `null`. 대체 링은 `Controls.xaml`의 `IsKeyboardFocused` 7곳, 설정 탭 1곳, 갤러리 타일 1곳, 레이어 타임라인·녹화 프레임·레이어 캔버스의 수동 그리기 |
| `KeyboardNavigation` | `src` | 6. 설정 1, 컨트롤 2, 갤러리 3 |
| `SystemParameters.HighContrast` | `src` | 8. `Controls.xaml` 7, `ModernWindowChrome.cs` 1. `ThemeService.cs` 0 |
| `ClientAreaAnimation` / `AnimationsEnabled` | `src` | 판정은 `FluidMotion` 한 곳. `BeginAnimation`은 `FluidMotion.cs`와 `PinWindow.cs`뿐이고 핀은 플래그로 건너뜀 |
| `OnCreateAutomationPeer` | `src` | 2종류. `TimelineAutomationPeer`, `RegionFrameAutomationPeer` |
| `&amp;` 니모닉 | `Strings.resx` | 10. XAML 액세스 키 패턴 0 |
| `TextScale` / `UserPreferenceChanged` / `SystemFonts` | `src` | 0 |
| 접근성 이름·키보드를 단언하는 테스트 파일 | `tests` | 전용 클래스는 `EditorLayoutAccessibilityTests`(팩트 8), `CountdownWindowAccessibilityTests`(팩트 1). 이름 단언이 흩어진 파일: `RecordingFeatureTests`, `EnglishLayoutTests`, `VideoEditorWindowTests`, `LayerPropertiesDialogTests`, `RecordingWallClockWindowTests`, `LocalizationSelfTest`. 대비: `ThemeContrastTests` 팩트 3, `ThemeResourceAvailabilityTests`의 포커스 3:1·감소 모션. 내레이터 E2E는 0 |

편중: 이름과 텍스트 대비는 편집기·설정·갤러리·팔레트 테스트에 몰려 있다. 캡처 오버레이, 주석 기하의 키보드, OS 고대비의 버튼/캔버스, 텍스트 배율은 테스트 일치가 없다. 그 공백을 구현된 것으로 세지 않았다.
