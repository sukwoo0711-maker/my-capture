# 접근성 라운드 2

점수: **3 / 5 유지.** 코드 감사이며 내레이터 실측이 아니다. 아래는 소유를 가를 뿐 새 결함을 더하지 않아 점수를 올리거나 내리지 않는다.

## 판정

| ID | 판정 | 접근성 감점 | 근거 |
|---|---|---|---|
| CL-11 | 합의 | 한다. 임의 사각형은 포인터만. 키보드 확정은 이미 있는 선택과 Ctrl+A 전체 모니터뿐이다. | `CaptureOverlayView` 주석 18–20행, `OnKeyDown` 523–533행, `NudgeSelection` 568–570행이 선택 없으면 반환 |
| A-01 | 병합 | CL-11과 같은 사실 한 번. 안내가 `DrawingVisual`이라 자동화 트리 밖인 점만 이 감점에 붙인다. | `CaptureOverlayView.cs`·`CaptureOverlayWindow.cs`의 `AutomationProperties` 0건. `Strings.resx` `Text_798E080A9262` |
| CL-12 | 합의 | 한다. 측정된 텍스트·포커스·휴식 경계 비율과, OS 고대비가 팔레트를 바꾸지 않는 사실. | `ThemeContrastTests`, `AppTheme.cs` `HighContrastColors`, `ThemeService.cs`에 `HighContrast` 0건, `Controls.xaml`의 `SystemParameters.HighContrast` |
| CL-16 | 합의 | 한다. 자동화 이름이 말하는 M·X를 `HandleShortcut`이 처리하지 않는다. 형광펜 부재(CL-02)와 합치지 않는다. | `AddToolButton` 호출 1126–1127행 `"M"` `"X"`. `HandleShortcut` switch 293–355행에 `Key.M`·`Key.X` 없음 |
| A-03 | 병합 | CL-16과 같은 사실 한 번. Tab+Space로 토글 가능한 점은 감점을 지우지 않고, 거짓 이름의 범위를 한정한다. | 위와 동일. 이름 형식 `Text_1BA9F7001237` |
| A-02 | 합의 | 한다. 도형·펜·모자이크·자르기·텍스트 배치의 기하가 포인터 히트테스트다. | `OnSurfaceMouseDown`. `HandleShortcut`에 방향키 이동·크기 조절 없음 |
| A-12 | 병합 | 일부만. Alt+Tab·작업 표시줄 복귀 부재, 핀 포커스 중 클릭스루 키 부재, 피드백이 라이브 영역이 아닌 점. | `PinWindow` 94행 `ShowInTaskbar = false`, 355행 `ExcludeFromAltTab`, `OnKeyDown` 542–607행, `_feedback` `TextBlock` 117행에 `LiveSetting` 없음. `PinManager` 218행 `Activate` |

## 한 번만 감점

| 사실 | 감점 축 | 재감점하지 않는 축 | 가르는 기준 |
|---|---|---|---|
| 자유 영역의 임의 사각형이 포인터뿐이고, 안내가 자동화 트리 밖이며 Ctrl+A·방향키를 말하지 않음 (CL-11, A-01) | accessibility | convenience, learnability | 창 스냅 없음(C-09)은 별개이며 functionality. 오버레이가 진입 전역 키를 말하지 않음(LRN-19)은 learnability. 같은 문자열 `Text_798E080A9262`라도 그 두 조각은 이 감점이 아니다. |
| 텍스트 4.5:1·일차 7:1 잠금, 포커스 3:1 통과, `Border.Subtle` Midnight 1.67:1·Daylight 1.47:1, `Border.Strong` 약 2.6:1 (CL-12, A-09) | accessibility | aesthetics | 비율 숫자만 접근성. 자정 글자 단계가 좁다는 위계(AES-06)는 aesthetics. 텍스트 대비는 이미 테스트를 통과하므로 AES-06으로 접근성을 다시 깎지 않는다. |
| OS 고대비가 콤보·스크롤바·캡션에만 연결되고 `ThemeService`가 팔레트를 바꾸지 않음 (CL-12, A-10) | accessibility | aesthetics | 수동 고대비 테마에서 `Accent.Default`와 `State.Warning`이 둘 다 `FFD000`인 위계(AES-15, `AppTheme.cs` 204·214행)는 aesthetics. |
| 레이어 핸들·도형 비트맵이 `DeepSkyBlue` 고정 (AES-10) | aesthetics | accessibility | 테마를 안 따르는 색 선택은 심미성. 그 파랑의 고대비 비텍스트 비율은 이 라운드에서 측정하지 않았고 접근성 감점으로 올리지 않는다. |
| 모자이크·자르기 이름과 `HandleShortcut` 불일치 (CL-16, A-03, C-06의 이름 절반) | accessibility | convenience, learnability | 거짓 이름의 주 소유는 접근성. 학습성은 같은 사실의 보조라 반복 채점하지 않는다. 자르기가 마우스 업에 즉시 적용되는 절반은 convenience에 남긴다. 형광펜이 없는 사실(CL-02, C-10)과는 다른 감점이다. |
| 주석 기하에 방향키 이동·크기 조절이 없음 (A-02) | accessibility | functionality, convenience | 빠진 도구(형광펜·번호·블러)는 functionality. 도구 전환·삭제·실행취소·커밋 키가 있는 사실은 이 감점을 상쇄하지 않는다. |
| 핀이 Alt+Tab·작업 표시줄 밖에 있고, 포커스 중 클릭스루 키가 없으며 피드백이 라이브 영역이 아님 (A-12) | accessibility | convenience, learnability, aesthetics | 전역 `ToggleClickThrough` 기본값 `Hotkey.None`(`AppSettings.cs` 82행)은 C-02이며 convenience. 핀 툴팁이 진입 전역 키를 말하지 않음은 LRN-19. 핀 테두리 색·hover(AES-14)는 aesthetics. |
