**1. 총평**

MyCapture의 감성품질을 가장 크게 낮추는 요소는 **좁은 창에서 깨지는 배치, 화면마다 달라지는 명령·피드백 규칙, 지나치게 강한 장식 테두리**입니다.  
색·타이포·모션 토큰과 접근성 지원은 이미 갖춰져 있지만, 개별 화면의 구현 방식이 달라 일관된 제품으로 느껴지는 힘이 약합니다.  
우선순위는 잘리는 콘텐츠와 짧게 사라지는 오류 안내를 해결하고, 공통 컨트롤의 상태 표현과 배치를 통일하는 것입니다.  
확인 범위에서 P0는 발견하지 않았습니다. 이미 별도로 수정 중인 폴더 저장 스위치, 동영상 출력 버튼 순서, Excel 핀, 코드 핀 모서리, 첫 캡처 안내는 아래 목록에서 제외했습니다.

조사는 `fix/realdevice-qa-round4`의 소스, 제공 PNG, 공식 비교 자료를 대상으로 했습니다. **파일 생성·수정·삭제와 빌드는 수행하지 않았습니다.** 아래 PNG 경로는 `artifacts/astra-ux-audit/inputs/` 기준이며, 수치는 별도 표시가 없으면 WPF의 **DIP**입니다.

**2. 우선순위 목록**

P1은 기능 발견·조작·접근성에 영향을 주는 문제, P2는 시각적 일관성과 조작 완성도 개선입니다. 제안값은 구현 후 검증할 시작값입니다.

1. **P1 — 라이브러리의 좁은 창에서 두 번째 카드가 잘립니다.**  
   **근거:** `source-details/after-narrow-selection-glass-ko-KR.png` 중앙 오른쪽 카드. [GalleryRowBuilder.cs:32](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Gallery/GalleryRowBuilder.cs:32)는 최소 두 열을 강제합니다. `GalleryWindow.xaml:1473`의 좌측 224·상세 240 고정 폭과 카드 트랙 256이 함께 작용합니다.  
   **이유·개선:** 콘텐츠와 카드 명령 일부를 볼 수 없습니다. 실제 가용 폭에 따라 **최소 한 열**을 허용하고, 트랙은 현재 256을 기준으로 계산합니다. 상세 패널을 접는 기능은 후속 개선으로 둡니다.  
   **예상 수정:** `GalleryRowBuilder.cs`, `GalleryWindow.xaml`, 열 수·키보드 이동 관련 코드. **난이도: 중.**

2. **P1 — Windows 대비 테마가 앱 전체에 일관되게 적용되지 않습니다.**  
   **근거:** [ThemeService.cs:39](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Themes/ThemeService.cs:39)의 시스템 고대비 변경 처리는 `Crop.*`·`CaptureGuide.*`만 갱신합니다. `Core/Themes/AppTheme.cs:193`의 앱 고대비 팔레트는 고정 검정·흰색·노랑입니다.  
   **이유·개선:** 일부 컨트롤과 나머지 화면이 서로 다른 대비색을 사용할 수 있습니다. 시스템 고대비에서는 전체 의미 토큰을 `SystemColors.Window/WindowText/Control/Highlight/HighlightText/GrayText`에 연결하고, 불투명도 1·그림자 없음·포커스 링 2로 통일합니다.  
   **예상 수정:** `ThemeService.cs`, `Controls.xaml`, `WorkspaceTheme.cs`. **난이도: 중.** 코드상 발견이며 실제 시스템 전환은 미확인입니다.

3. **P1 — 라이브러리 상세 패널에서 핵심 행동이 메타데이터 아래로 밀립니다.**  
   **근거:** `source-details/source-details-page-glass-ko-KR.png` 오른쪽의 패널 스크롤과 URL 내부 스크롤. [GalleryWindow.xaml:1666](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Gallery/GalleryWindow.xaml:1666), `:1687`, `:1710`.  
   **이유·개선:** 편집·복사에 도달하려면 출처 정보를 지나야 하고, 휠이 어느 영역을 움직일지도 달라집니다. **편집·핀·복사를 상단의 고정 행동 영역**에 두고 메타데이터만 스크롤합니다. URL·경로는 2줄 요약과 전체 값 펼침, 복사 명령으로 표현합니다. 패널 안쪽 여백 16, 구역 간격 16.  
   **예상 수정:** `GalleryWindow.xaml`, `GalleryWindow.Source.cs`. **난이도: 중.**

4. **P1 — 빠른 작업 카드에서 작업 시작과 결과가 충분히 구별되지 않습니다.**  
   **근거:** `app-renders/quick-access-normal-daylight-96dpi.png` 하단. [QuickAccessWindow.cs:356](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Capture/QuickAccessWindow.cs:356)는 작업 시작 시 상태 영역만 표시하고 완료 후 문구를 설정합니다. 상태 글자는 11이며 결과 뒤 표시 시간을 재설정하지 않습니다.  
   **이유·개선:** 느린 작업에서 반응을 알기 어렵고, 결과를 읽을 시간이 부족할 수 있습니다. 시작 즉시 “저장 중”을 표시하고 동일 작업 버튼을 비활성화합니다. 200ms 이상 지속하면 진행 표시, 성공 후 최소 4초, 오류는 닫기까지 유지합니다. 상태는 12–13, 최대 2줄로 표시합니다.  
   **예상 수정:** `QuickAccessWindow.cs`, `Strings.resx`. **난이도: 중.**

5. **P1 — 성공·실패·복구 안내의 수명이 구분되지 않습니다.**  
   **근거:** [PinWindow.cs:46](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Pinning/PinWindow.cs:46)의 피드백은 1초이며 `:1103`의 공통 경로를 저장·실패·복구 안내가 공유합니다. 캡처 피드백은 1.6초, 라이브러리는 8초입니다.  
   **이유·개선:** 짧은 완료 확인과 읽고 조치해야 하는 메시지가 같은 방식으로 사라집니다. **성공 3초, 긴 결과·경고 6초, 복구가 필요한 실패는 해소·닫기까지**로 의미를 분리합니다. 표시 시간은 모션 시간과 별도 관리하고, 동적 결과에 UI Automation 알림을 연결합니다.  
   **예상 수정:** `PinWindow.cs`, `CaptureOverlayView.cs`, `GalleryWindow.xaml.cs`, 공통 피드백 정책. **난이도: 중.**

6. **P1 — 단축키 도움말 검색에서 다른 탭의 결과를 놓칩니다.**  
   **근거:** [ShortcutHelpWindow.cs:89](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/ShortcutHelpWindow.cs:89)는 모든 탭의 행을 숨기거나 표시하지만 선택 탭·결과 수·빈 상태를 갱신하지 않습니다. PNG의 검색 문구는 “기능 또는 단축키 검색”입니다.  
   **이유·개선:** 현재 탭 밖에 일치 항목이 있어도 빈 화면으로 보입니다. 검색 중에는 **전체 탭 결과를 하나의 목록**으로 표시하고 12 크기의 범주명을 붙입니다. 행 높이 36 이상, 좌우 여백 12. 0건에는 “검색 결과 없음”과 “검색어 지우기”를 제공합니다.  
   **예상 수정:** `ShortcutHelpWindow.cs`, `Strings.resx`. **난이도: 중.**

7. **P1 — 녹화 크기의 가로·세로 입력이 분리되어 의미가 흐려집니다.**  
   **근거:** `app-renders/settings-page-3-compact-144dpi.png`의 `0 ×` 아래에 놓인 두 번째 `0`. [SettingsWindow.xaml:539](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Settings/SettingsWindow.xaml:539)는 세로 입력을 첫 Grid 밖에 둡니다.  
   **이유·개선:** 값의 관계와 `0`의 의미를 해석해야 합니다. `[가로 120] × [세로 120] px`를 한 Grid에 넣고 간격 8, 입력 높이 36으로 맞춥니다. “마지막 영역 사용”을 명시적 선택으로 제공하고 사용자 지정일 때 숫자를 활성화하는 방식은 후속 개선입니다.  
   **예상 수정:** `SettingsWindow.xaml`, `Strings.resx`. **난이도: 정렬은 소, 선택 방식 변경은 중.**

8. **P1 — 설정이 허용한 선 두께를 편집기가 조용히 줄입니다.**  
   **근거:** [SettingsRanges.cs:39](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.Core/Settings/SettingsRanges.cs:39)는 기본 선 두께 1–64를 허용하지만, [AnnotationEditorControl.cs:215](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Editing/AnnotationEditorControl.cs:215)는 1–24로 제한합니다. 슬라이더 상한도 24입니다.  
   **이유·개선:** 설정 저장과 실제 표현 사이의 약속이 어긋납니다. 기본값의 허용 범위를 공통 상수로 관리하고, 현재 설정 계약인 **1–64를 편집기에도 적용**하는 방향을 우선 검토합니다. 슬라이더 옆에 현재 값과 `px`를 표시합니다.  
   **예상 수정:** `SettingsRanges.cs`, `AnnotationEditorControl.cs`, 관련 검증. **난이도: 소~중.** 실기 재현은 수행하지 않았습니다.

9. **P2 — 확인·취소 버튼 순서가 대화상자마다 바뀝니다.**  
   **근거:** `app-renders/organization-compact-glass-96dpi.png`는 `[취소][저장]`. [GalleryWindow.Organization.cs:121](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Gallery/GalleryWindow.Organization.cs:121)와 폴더 대화상자가 이 순서이고, 설정·편집기 닫기는 확정 행동부터 시작합니다.  
   **이유·개선:** 같은 결정을 내릴 때 강조 버튼 위치를 다시 찾아야 합니다. 일반 대화상자를 **`[확정][취소]`**, 간격 8·최소 폭 88·높이 36으로 통일합니다. 이미지 편집기의 `[파일로 저장][복사][완료][취소]`를 기준으로 유지합니다.  
   **예상 수정:** `GalleryWindow.Organization.cs`, `GalleryFolderDialogs.cs`, 공통 행동 행. **난이도: 소.**

10. **P2 — 눌림 모션과 호버·선택 전환의 성격이 다릅니다.**  
    **근거:** [Controls.xaml:149](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Themes/Controls.xaml:149), `:447`, `:504`의 호버·체크·라디오는 즉시 Setter로 교체됩니다. 체크·라디오의 눌림은 설명문을 포함한 컨트롤 전체에 적용됩니다.  
    **이유·개선:** 상태는 갑자기 바뀌는데 문장은 축소되어 움직입니다. 호버 표면 opacity는 **83ms**, 체크·선택 표시는 **167ms**로 전환하고, 체크·라디오의 눌림은 18×18 표시 영역에만 적용합니다. 레이블은 scale 1을 유지합니다.  
    **예상 수정:** `Controls.xaml`, `FluidMotion.cs`. **난이도: 중.** 실제 모션 감각은 미확인입니다.

11. **P2 — glass에서 장식선이 조작 경계만큼 강합니다.**  
    **근거:** `app-renders/annotation-compact-glass-96dpi.png`의 캔버스·패널·도구 구분선. [AppTheme.cs:286](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.Core/Themes/AppTheme.cs:286)의 glass `Border.Subtle`은 밝은 색의 60% alpha입니다.  
    **이유·개선:** 여러 상자와 선이 콘텐츠보다 먼저 보입니다. `Border.Decorative`와 `Border.Control`을 분리하고, 장식선은 **`#33F6F8FC`–`#3DF6F8FC`, 1 DIP**부터 비교합니다. 입력 경계는 별도 대비를 유지하고 패널 구분에는 12/16 여백을 우선합니다.  
    **예상 수정:** `AppTheme.cs`, `Tokens.xaml`, `Controls.xaml`. **난이도: 소~중.**

12. **P2 — 같은 아이콘 계열의 시각 크기가 화면마다 달라집니다.**  
    **근거:** `quick-access-normal-daylight-96dpi.png`의 명령 행. [Controls.xaml:823](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Themes/Controls.xaml:823)과 빠른 작업·핀·동영상은 경로 외곽을 `Stretch.Uniform`으로 확대하고, 이미지 편집기는 고정 20×20 좌표계를 사용합니다.  
    **이유·개선:** 가늘고 긴 핀과 넓은 아이콘이 각각 박스를 채워 무게가 달라집니다. 원본 **20×20·선 1.6·Stretch.None**을 공통 렌더러로 유지하고, 바깥 크기만 메뉴 16·일반 명령 20으로 구분합니다.  
    **예상 수정:** `Controls.xaml`, `Tokens.xaml`, 공통 아이콘 렌더러와 소비 화면. **난이도: 중.**

13. **P2 — 상시 단축키 표시가 지나치게 작습니다.**  
    **근거:** 동영상·빠른 작업 PNG의 아이콘 아래 문자. [ShortcutPresentation.cs:32](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/ShortcutPresentation.cs:32)는 일부 범주에 9를 지정합니다.  
    **이유·개선:** 읽기 어려운 글자가 화면 밀도만 높입니다. 상시 표시는 **`FontSize.Caption=12`**를 사용하고, 긴 조합은 툴팁으로 옮깁니다. 재생·이전/다음처럼 의미가 중요한 명령은 아이콘 또는 짧은 기능명으로 먼저 이해되게 합니다.  
    **예상 수정:** `ShortcutPresentation.cs`, 동영상 명령 행. **난이도: 소~중.**

14. **P2 — 슬라이더의 외형과 값 확인 방식이 다른 컨트롤과 어긋납니다.**  
    **근거:** `app-renders/annotation-normal-daylight-96dpi.png` 오른쪽 선 두께. [Controls.xaml:811](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Themes/Controls.xaml:811)는 Slider에 색·최소 높이만 지정하며, 선 두께에는 상시 숫자가 없습니다.  
    **이유·개선:** 기본 WPF 모양의 사각 손잡이가 남아 있고 정밀 조절 결과도 읽기 어렵습니다. 트랙 4·손잡이 16·조작 영역 높이 32 이상으로 맞추고 값 입력 폭 56을 붙입니다. 방향키·Home/End·UI Automation의 기존 의미는 보존합니다.  
    **예상 수정:** `Controls.xaml`, `AnnotationEditorControl.cs`, 내보내기 슬라이더. **난이도: 중.**

15. **P2 — 동영상 편집기의 작은 창에서 보조 정보가 미리보기를 압박합니다.**  
    **근거:** `app-renders/video-compact-glass-96dpi.png`의 작은 미리보기, 별도 줄의 “원래 길이”, 레이어 목록·속성·트랙. [VideoEditorWindow.cs:1002](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Recording/VideoEditorWindow.cs:1002), `:1086`.  
    **이유·개선:** 같은 시간·레이어 정보가 여러 곳에 반복되고 줄바꿈 규칙이 작업 영역을 줄입니다. 좁은 폭에서는 트림을 **행동·요약 / 시작·끝·초기화의 의도된 두 행**으로 구성합니다. 레이어 속성은 선택 항목에 집중하고, 편집 완료 행동과 재생·선택 강조를 서로 다른 역할로 유지합니다.  
    **예상 수정:** `VideoEditorWindow.cs`, 레이어 패널 구성. **난이도: 중.**

16. **P2 — 용량 줄이기 조작 중 비교할 미리보기가 사라집니다.**  
    **근거:** [ImageReductionExportDialog.cs:83](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Editing/ImageReductionExportDialog.cs:83)는 슬라이더 변경마다 이미지와 결과를 지우며, 다시 “크기 확인”을 눌러야 합니다.  
    **이유·개선:** 조절 전후 비교가 끊깁니다. 이전 미리보기를 유지하고 계산 상태를 표시합니다. 드래그 종료 또는 마지막 변경 **250ms 뒤** 재계산하는 방안을 검토하되, 계산 비용이 크면 수동 확인을 유지하면서 “이전 설정의 결과”를 표시합니다.  
    **예상 수정:** `ImageReductionExportDialog.cs`, `Strings.resx`. **난이도: 중.**

17. **P2 — compact 설정 화면의 세로 밀도가 낮습니다.**  
    **근거:** `app-renders/settings-page-4-compact-144dpi.png`에서 세 입력 뒤 네 번째 입력이 화면 아래로 밀립니다. [SettingsWindow.xaml:57](/C:/GitRepositories/my-capture/.claude/worktrees/beautiful-spence-49d55b/src/MyCapture.App/Settings/SettingsWindow.xaml:57)의 필드 블록과 공통 입력 스타일을 사용합니다.  
    **이유·개선:** 레이블·범위 설명·큰 입력을 모두 쌓아 관련 설정을 함께 보기 어렵습니다. 숫자 옵션을 **레이블 / 입력 / 단위** 행으로 정리하고 입력 높이 32–36, 행 간격 12–16, 구역 간격 24를 적용합니다. 유효 범위와 오류 정보는 보존합니다.  
    **예상 수정:** `SettingsWindow.xaml`, 설정용 밀도 토큰. **난이도: 중.**

**3. 앱 전체 일관성 규칙 제안**

| 영역 | 권장 규칙 |
|---|---|
| 모션 | 기존 **Fast 83ms / Normal 167ms / Deliberate 250ms**를 유지합니다. Fast는 호버·짧은 퇴장, Normal은 선택·토글·작은 표면 등장, Deliberate는 패널 전환에 사용합니다. |
| 이징·감축 설정 | 기존 `Motion.Ease.Standard`의 Cubic EaseOut과 `SoftLanding`의 Quadratic EaseOut을 역할별로 재사용합니다. `ClientAreaAnimation=false`에서는 최종 상태를 즉시 표시합니다. 포커스·클릭 처리·캡처 좌표는 애니메이션을 기다리지 않습니다. |
| 버튼 위계 | 같은 결정 묶음에서는 완료 행동 하나를 Primary로 둡니다. 파일 저장·복사 등 독립 출력은 Secondary, 취소·일반 보조는 Ghost를 기본으로 합니다. 선택된 도구는 옅은 선택 배경과 표시자로 구별합니다. |
| 배치·간격 | 관련 버튼 간격 8, 명령 그룹 간격 16, 패널 안쪽 16, 구역 간격 24. 일반 조작 높이 36, compact 최소 32. 파괴적 명령은 기본 Enter 대상으로 지정하지 않습니다. |
| 모서리·테두리 | 현재 `Radius.Sm/Md/Lg=6/8/12`는 유지하되 작은 명령 6, 입력 8, 독립 패널 12처럼 역할을 고정합니다. 임의의 5·7·10 사용을 줄이고, 맞닿는 내부 경계에는 중복 선을 두지 않습니다. |
| 호버·포커스·누름 | 호버는 표면 밝기 변화, 포커스는 즉시 표시되는 독립 링, 누름은 표면 변화와 제한된 변형으로 구분합니다. 포커스 때문에 크기나 배치가 바뀌지 않아야 합니다. |
| 글자·아이콘 | 본문 14, 컨트롤 13, 보조 12의 기존 토큰을 중심으로 사용합니다. 제목의 굵기와 크기를 동시에 과도하게 높이지 않습니다. 아이콘은 공통 좌표계와 선 굵기를 유지합니다. |
| 피드백 | 진행·성공·실패를 별도 상태로 관리합니다. 성공색은 결과에 사용하고, 자동 닫힘 타이머는 중립색으로 표시합니다. 복구가 필요한 실패에는 시간 제한을 두지 않습니다. |

Windows 공식 모션 값도 **83/167/250ms**이므로 기존 토큰은 좋은 기반입니다. 다만 MyCapture의 토큰명 `Normal`은 WinUI의 `Fast`와 같은 167ms이므로 이름보다 역할을 기준으로 연결해야 합니다. [Microsoft: Timing and easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing)

Windows의 기본 모서리는 내부 컨트롤 4, 상위 표면 8입니다. MyCapture의 6/8/12 선택 자체를 결함으로 볼 근거는 없으며, 중첩 관계와 역할의 일관성이 우선입니다. [Microsoft: Geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry)

비교 앱은 직접 실행하지 않았으며, 아래는 **공식 문서에서 확인한 관례**입니다.

| 비교 대상 | MyCapture에 적용할 비교 기준 |
|---|---|
| [Snipping Tool](https://support.microsoft.com/en-au/windows/apps/use-snipping-tool-to-capture-screenshots) | 자동 보관과 별도 파일 저장을 구별합니다. 저장 위치와 출력 결과가 명령명에서 분명해야 합니다. |
| [Snipaste](https://github.com/Snipaste/feedback/wiki/Getting-Started), [PixPin](https://pixpin.cn/docs/pin/base-use) | 캡처·클립보드에서 복사·핀으로 이어지는 짧은 작업 흐름이 핵심입니다. 빠른 작업의 즉각적인 반응과 결과 확인이 중요합니다. |
| [ShareX](https://getsharex.com/docs/image-editor) | 편집 완료 후 다음 작업으로 진행하는 명령과 개별 복사·저장·핀 명령을 구별합니다. MyCapture의 “완료”도 이 역할이 일관되어야 합니다. |
| [Snagit](https://support.techsmith.com/hc/en-us/articles/360029426931-Automatically-Copy-to-Clipboard-in-Snagit-Video) | 자동 클립보드 복사처럼 반복 전달 단계를 줄이는 옵션을 제공합니다. 설정한 자동 행동과 수동 명령의 결과를 명확하게 피드백해야 합니다. |

문구는 확인한 `Strings.resx` 범위에서 **해요체·느낌표·이모지 위반을 발견하지 못했습니다.** 문구 개선의 중심은 말투 교체보다 검색 범위, `0` 같은 설정값, 저장·완료 결과의 의미를 분명히 하는 데 있습니다.

**4. 바로 고칠 것과 큰 개편**

- **첫 1~2시간 묶음:** ⑦ 녹화 크기 정렬, ⑨ 확인·취소 순서 통일을 우선 권합니다. 수정 범위가 작고 화면에서 개선을 바로 확인할 수 있습니다.
- **각각 1~2시간 후보:** ⑧ 선 두께 범위 일치, ⑬ 작은 단축키 표시 정리, ⑯ 기존 미리보기 유지. 관련 입력·줄바꿈·저장 경로 확인을 포함하면 시간이 늘어날 수 있습니다.
- **여러 화면을 함께 검증할 개편:** ① 라이브러리 열 계산·키보드 이동, ② 시스템 대비 테마, ④⑤ 공통 작업·피드백 상태, ⑩⑫⑭ 공통 컨트롤.
- **정보 구조 개편:** ③ 상세 패널, ⑮ 동영상 작은 창 배치, ⑰ 설정 밀도. 같은 콘텐츠로 normal·compact를 나란히 검토하는 방식이 적합합니다.

**5. 확인하지 못한 것**

- 실제 애니메이션의 끊김·반복 입력·전환 중 시스템 애니메이션 설정 변경. 코드에 감축 설정 대응이 있는 것은 확인했습니다.
- 실제 Windows 대비 테마 전환, 내레이터 낭독, 전체 Tab 순서와 포커스 복귀.
- 96/144dpi 정적 렌더 외의 125%·175%·200% 배율, 모니터 간 이동, Windows 텍스트 확대.
- glass가 실제 바탕화면·DWM 합성 위에 놓였을 때의 대비와 그림자. 테마별 대비를 수치로 전수 측정하지 않았습니다.
- 인코딩·OCR·클립보드 지연, 오류 복구의 실제 소요 시간.
- 조사 중 별도 작업에서 변경된 스위치·동영상 명령의 실행 결과. 해당 변경은 이번 개선 목록에서 제외했습니다.
- TO-BE 이미지는 파일 쓰기 금지에 따라 생성·렌더하지 않았습니다. 시각 근거는 제공된 AS-IS PNG이며, 위 개선값은 **미적용 제안**입니다.