# convenience 겹침 후보

라운드 1 편의성 평가에서 다른 축과 같이 집계될 수 있는 주장만 둔다. 점수 이중 계산을 피하려면 제안 주 소유 축만 감점한다.

| claim_id | 한 줄 주장 | 제안 주 소유 축 | 겹치는 축 | 근거 경로 |
|---|---|---|---|---|
| C-01 | 완료·빠른 저장은 클립보드 복사 실패를 “저장이 완료되지 않았습니다”로 보여 편집기를 붙잡는다. | convenience | learnability | `src/MyCapture.App/Editing/CaptureCommitService.cs`, `src/MyCapture.App/Editing/AnnotationEditorControl.cs`, `src/MyCapture.Core/Localization/Strings.resx` (`Text_06F58809C083`) |
| C-02 | 창·전체·이전 영역·클릭 통과 전역 명령의 기본 키가 비어 반복 작업이 트레이 하위 메뉴로 간다. | convenience | learnability, functionality | `src/MyCapture.Core/Settings/AppSettings.cs` (`HotkeySettings`), `src/MyCapture.App/App.xaml.cs` |
| C-03 | 단축키 설정은 키 입력 캡처가 아니라 문자열 칸이며, 충돌 시 이전 조합으로 되돌린다. | convenience | learnability | `src/MyCapture.App/Settings/SettingsWindow.xaml`, `src/MyCapture.Core/Settings/SettingsDraft.cs`, `src/MyCapture.App/Settings/SettingsApplyService.cs` |
| C-04 | 영역 캡처 키를 비우면 로드 시 `Ctrl+Shift+C`로 복구되어 다른 프로그램과 키를 나누기 어렵다. | convenience | trust | `src/MyCapture.Core/Settings/SettingsStore.cs` |
| C-05 | 편집기 `Ctrl+C`는 선택 주석이 아니라 전체 이미지를 확정하고 닫는다. | convenience | learnability | `src/MyCapture.App/Editing/AnnotationEditorControl.cs` (`HandleShortcut`) |
| C-06 | 모자이크 `M`·자르기 `X`가 버튼 안내와 키 처리에서 어긋나고, 자르기는 마우스 업에 즉시 적용된다. | convenience | learnability | `src/MyCapture.App/Editing/AnnotationEditorControl.cs`, `docs/releases/2.4.0-release-notes.md` |
| C-07 | 닫은 핀을 붙여넣기 키로 되돌리는 한도 설정은 저장만 되고 실행 경로가 없다. | convenience | functionality | `src/MyCapture.Core/Settings/AppSettings.cs` (`ClosedWindowRestoreLimit`), `src/MyCapture.App/Pinning/PinManager.cs` |
| C-08 | 보관하지 않은 이미지는 기본 168시간 뒤 라이브러리 검색에서 빠진다. | trust | convenience | `src/MyCapture.Core/Settings/AppSettings.cs` (`ImageRetentionHours`), `README.md` |
| C-09 | 영역 오버레이에 창 스냅이 없고 창 캡처는 기본 키 없는 별도 명령이다. | functionality | convenience | `src/MyCapture.App/Capture/CaptureOverlayView.cs`, `src/MyCapture.App/App.xaml.cs` (`HandleCaptureWindow`) |
| C-10 | 형광펜·번호 단계·블러·색 뽑기 없이 설명용 주석 단계가 길어진다. `HighlighterAlpha`와 `ColorFormat`은 모델에만 있다. | functionality | convenience | `src/MyCapture.App/Editing/EditorTool.cs`, `src/MyCapture.Core/Settings/AppSettings.cs` |
| C-11 | ShareX식 캡처 후 작업 사슬(저장·복사·업로드를 핫키마다 구성)이 없다. 업로드 부재 자체는 범위 설계다. | positioning | convenience, functionality | `docs/competitive-matrix.md`, `https://getsharex.com/docs/image-editor` |
| C-12 | GIF 내보내기는 자동 계산 뒤에도 다른 이름 저장 대화상자가 필요하다. | convenience | functionality | `src/MyCapture.App/Recording/VideoExportDialog.cs` |
| C-13 | 녹화 영역 선택만 매번 100ms를 기다린다. 지연 수치는 성능 축이 맡는다. | performance | convenience | `src/MyCapture.App/Recording/RegionRecordingCoordinator.cs`, `src/MyCapture.App/Capture/CaptureOverlayCoordinator.cs`, `docs/releases/3.0.0-release-notes.md` |
| C-14 | 언어를 바꿔도 다음 실행 전까지 트레이·창 문구가 그대로다. | learnability | convenience | `src/MyCapture.App/App.xaml.cs` (`ChangeTrayLanguage`), `src/MyCapture.App/Settings/SettingsApplyService.cs` |
