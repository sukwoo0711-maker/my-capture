# inbox / accessibility

라운드 1 주장. 점수는 코드 감사 3/5이며 내레이터 실측이 아니다. 근거는 `round1/04-accessibility.md`.

| claim_id | 주장 | 주 소유 축 | 겹치는 축 | 근거 경로 |
|---|---|---|---|---|
| A-01 | 자유 영역 캡처는 Ctrl+A로 모니터 전체만 확정할 수 있고, 임의 사각형은 포인터 드래그만 된다. 안내 문장은 DrawingVisual이라 자동화 트리 밖이며 Ctrl+A·방향키를 말하지 않는다. | accessibility | convenience, learnability | `src/MyCapture.App/Capture/CaptureOverlayView.cs`, `CaptureOverlayWindow.cs`, `Strings.resx` `Text_798E080A9262` |
| A-02 | 주석 도형·펜·모자이크·자르기·텍스트 배치는 마우스 히트테스트다. 방향키로 선택 도형을 옮기거나 크기를 바꾸는 코드는 `HandleShortcut`에 없다. | accessibility | functionality | `src/MyCapture.App/Editing/AnnotationEditorControl.cs` `OnSurfaceMouseDown`, `HandleShortcut` |
| A-03 | 모자이크·자르기 자동화 이름은 단축키 M·X를 포함하지만 `HandleShortcut`은 그 키를 처리하지 않는다. Tab+Space로는 도구 토글이 가능하다. | accessibility | learnability | `AnnotationEditorControl.cs` `AddToolButton`, `HandleShortcut` |
| A-04 | 편집기 아이콘 8종과 명령 버튼은 이름·툴팁이 있고 테스트가 잠근다. 상태 영역은 Polite 라이브 영역이다. | accessibility | learnability | `EditorLayoutAccessibilityTests.cs`, `AnnotationEditorControl.cs` |
| A-05 | 카운트다운·OCR 상태·설정 오류는 LiveSetting과 `LiveRegionChanged`를 직접 올린다. WPF Text 변경만으로는 이벤트가 안 난다는 주석이 코드에 있다. | accessibility | — | `CountdownWindow.cs`, `OcrResultWindow.cs`, `SettingsWindow.xaml.cs` `AnnounceErrors` |
| A-06 | 공용 버튼·체크·텍스트·설정 탭·갤러리 타일은 기본 포커스 애드너 대신 두께가 고정된 포커스 링을 그린다. | accessibility | aesthetics | `Themes/Controls.xaml`, `SettingsWindow.xaml`, `GalleryWindow.xaml` |
| A-07 | 편집기 루트는 `FocusVisualStyle=null`인 채로 초기 포커스를 받는다. 두 줄 타임라인은 포커스 링을 그리지 않는다. 레이어 타임라인·녹화 프레임·레이어 캔버스는 포커스 표시가 있다. | accessibility | aesthetics | `AnnotationEditorWindow.cs`, `AnnotationEditorControl.cs`, `TwoLineTimeline.cs`, `VideoLayerTimeline.cs`, `RecordingControlWindow.cs`, `VideoLayerCanvas.cs` |
| A-08 | 동영상 편집 전송·트림·프레임 스텝·레이어 시간·레이어 위치는 키보드 대안이 있다. v1.9 계획이 요구한 편집기 키보드 대안은 이 범위에서 코드에 있다. | accessibility | convenience | `VideoEditorWindow.cs` `OnKeyDown`, `TwoLineTimeline.cs`, `VideoLayerTimeline.cs`, `docs/ux-v1.9.0-plan.md`, `docs/design/video-editor-timeline-ux.md` |
| A-09 | 모든 팔레트의 텍스트 대비는 테스트가 4.5:1(일차 7:1)로 잠그고, 포커스 색은 소스 기준 3:1을 넘는다. 휴식 경계 `Border.Subtle`은 Midnight 1.67:1, Daylight 1.47:1, `Border.Strong`은 약 2.6:1이다. | accessibility | aesthetics | `ThemeContrast.cs`, `ThemeContrastTests.cs`, `ThemeResourceAvailabilityTests.cs`, `AppTheme.cs` |
| A-10 | OS 고대비는 콤보·스크롤바·캡션에만 연결된다. `ThemeService`는 OS 고대비로 팔레트를 바꾸지 않는다. 고대비 팔레트는 설정에서 수동 선택이다. | accessibility | aesthetics | `Themes/Controls.xaml`, `ModernWindowChrome.cs`, `ThemeService.cs`, `AppTheme.cs` `HighContrastColors` |
| A-11 | 모션은 `SystemParameters.ClientAreaAnimation`을 제스처 시점에 보고, 꺼지면 핀 포함 애니메이션을 건너뛴다. | accessibility | aesthetics | `Themes/FluidMotion.cs`, `PinWindow.cs`, `ThemeResourceAvailabilityTests.cs` |
| A-12 | 핀은 활성화 직후 방향키·확대 키가 있으나 Alt+Tab·작업 표시줄에서 빠져 있고, 클릭스루는 키보드 토글이 없으며 피드백은 라이브 영역이 아니다. | accessibility | convenience | `PinWindow.cs`, `PinManager.cs` |
| A-13 | 녹화 영역 이동은 포커스된 프레임의 방향키로 된다. 커스텀 피어와 도움말이 테스트된다. | accessibility | convenience | `RecordingControlWindow.cs`, `RecordingFeatureTests.cs` |
| A-14 | DPI는 PerMonitorV2다. Windows 텍스트 크기 배율 구독은 소스에 없다. 슬라이더 반영은 미확인. | accessibility | learnability | `src/MyCapture.App/app.manifest`, `Themes/Tokens.xaml` `FontSize.*` |
| A-15 | 트레이 메뉴 문자열 10개에 니모닉이 있다. 설정 XAML 액세스 키는 0건이다. 전역 `Ctrl+Shift+C/X/Z`의 기억 용이성은 이 축의 감점이 아니다. | convenience | accessibility, learnability | `Strings.resx` `&amp;` 10건, `ThemedShellPresenter.cs`, `MASTER.md` Command family |
| A-16 | 타임라인 삭제 구간은 해치가 색을 보강한다. 레이어 핸들 존은 10px이고 키보드 대안이 있다. | accessibility | aesthetics | `TwoLineTimeline.cs`, `VideoLayerTimeline.cs` `HandleZoneWidth` |
| A-17 | 1.7 UX 계획의 포커스 검토는 렌더 기록까지이고, 그 문서는 조작별 키보드 결과를 미기록으로 남겼다. 3.0.0의 포커스 링 구현과 동일시하지 않는다. | accessibility | aesthetics | `docs/ux-improvements-v1.7.0-plan.md`, `docs/design/1.7.0-ui-review.md` |
| A-18 | 내레이터 실사용, OS 고대비 실세션, 경쟁 제품 창, `docs/market/`는 이 라운드 근거가 아니다. | accessibility | positioning | `docs/evaluation/2026-09-23-multiagent/PROTOCOL.md` |
