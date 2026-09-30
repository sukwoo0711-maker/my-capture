# inbox — learnability

라운드 1 주장. 주 소유가 다른 축이면 학습성 점수를 그 사실로 다시 깎지 않는다.

| claim_id | 주장 | 주 소유 축 | 겹치는 축 | 근거 |
|---|---|---|---|---|
| LRN-01 | 점수 2/5. 캡처 진입은 있으나 첫 10분에 C/X/Z/F3/F4(실제 기본 F9)를 한 세트로 가르치지 않는다. | learnability | — | `ResidentReadyNotification.cs`, `Shell.HelpText`, `GalleryWindow.xaml`, `AppSettings.cs` |
| LRN-02 | 준비 알림은 등록된 캡처 키 하나만 말하고, 프로세스당 한 번, 5초, 소리 없음. 실패 시 재표시하지 않는다. X/Z/F3/F9는 없다. | learnability | — | `ResidentReadyNotification.cs`, `ThemedShellPresenter.cs` 타이머 5초, `ShellIdentityTests.cs`, `Strings.resx` `ShellRetake_ReadyMessage` |
| LRN-03 | `IsFirstRun`은 저장·복원만 된다. 환영 창, 코치마크, 첫 실행 분기는 `src/MyCapture.App`에 없다. | learnability | — | `SettingsStore.cs`, `AppSettings.cs`. `src/MyCapture.App`에서 `IsFirstRun` 검색 0건 |
| LRN-04 | 일반 실행은 라이브러리를 연다. 닫기는 `Hide()`. `--background`와 로그인 Run 키는 창을 열지 않는다. 숨김·다음 로그온을 설명하는 문구가 없다. | learnability | convenience | `App.OnStartup`, `GalleryWindow.OnClosing`, `StartupRegistrationService.BackgroundSwitch` |
| LRN-05 | `LaunchAtLogin` 기본값 true. 첫 셸이 Run 키를 `--background`로 맞춘다. 동의 없는 자동 실행은 trust가 소유한다. | trust | learnability | `AppSettings.cs` `LaunchAtLogin = true`, `App.InitializeShell`의 `ReconcileOnStartup` |
| LRN-06 | 빈 라이브러리는 「캡처하거나 녹화하면 표시」까지만 말한다. 키 이름은 없다. | learnability | — | `GalleryViewModel.EmptyStateText`, `Text_1FC9ECE11C93`, `GalleryWindow.xaml` `EmptyStateLabel` |
| LRN-07 | 라이브러리 캡처 버튼과 트레이 항목은 `영역 캡처(&R)\tCtrl+Shift+C`를 재사용한다. 녹화 라벨 `화면 녹화`에는 키가 없다. 트레이 왼쪽 클릭은 라이브러리다. | learnability | aesthetics | `App.xaml.cs` 메뉴, `Text_2C20ADDA2018`, `Text_4F5AF20FAA6E`, `TrayIconService` `WM_LBUTTONUP` |
| LRN-08 | 앱 안 「사용 방법」은 캡처 단축키를 이름 없이 말하고, X/Z/F3/F9·핀·녹화가 없다. 「갤러리」「Language」가 남아 있다. 가이드 링크가 없다. | learnability | — | `Strings.resx` `Shell.HelpText`, `ThemedShellPresenter.ShowHelp`. `src`에서 `docs/guides` 검색 0건 |
| LRN-09 | 설정 일반 카드는 C/X/Z만 XAML에 고정한다. F3은 없고, 사용자가 키를 바꿔도 카드는 따라가지 않는다. 단축키 탭을 열면 현재 값은 보인다. | learnability | functionality | `SettingsWindow.xaml` 288–308행, `HotkeySettings` |
| LRN-10 | 코드·설정 문구의 GitHub 기본 키는 F9다. 루트 README는 F4로 적는다. 가이드는 핀과 F3/F9를 다루지 않는다. | learnability | positioning | `AppSettings.cs`, `SettingsStore.cs` 스키마 4, `Settings.GitHubIssueUrlHint`, `README.md` 47–48행, `docs/guides/README.md` |
| LRN-11 | 가이드 본문은 한국어로 C/X/Z·GIF·언어 적용 시점을 설명하고 스크린샷 4개가 있다. 그 품질이 앱 안 도움을 대체하지 못한다. 영어 가이드 파일은 없다. | learnability | — | `docs/guides/README.md`, `docs/guides/shots/` 4 PNG, `README.md` 영어 요약 1단락 |
| LRN-12 | 리소스 키는 한국어 897 = 영어 897, 한쪽 누락 0, 영어 한글 0, 자리 표시자 불일치 0. 완전 동일 값은 In/Out과 해상도 두 줄뿐이다. | learnability | — | `Strings.resx`, `Strings.en.resx`. `<data name>` 검색. `LocalizationTests` 하한 724는 실제 개수가 아님 |
| LRN-13 | 언어는 다음 시작에 적용된다고 설정 힌트·메시지 상자가 말한다. 트레이 경로는 `ko`/`en`을 저장하고 `UiText.Configure`를 즉시 호출하며 알림 제목은 `"Language"`다. | learnability | — | `Settings.LanguageHint`, `SettingsApplyService`, `ThemedShellPresenter.ShowMenu`, `App.ChangeTrayLanguage` |
| LRN-14 | 설정 설명 28줄 중 5줄(범위·GiB·px)과 OCR 탭 Header `"OCR"`은 resx 밖이다. | learnability | — | `SettingsWindow.xaml` `Settings.Description` 28, 리터럴 5, Header 680행 |
| LRN-15 | 사용자 표면 하드코딩으로 메시지 상자·GitHub 알림 제목 `"MyCapture"`, 언어 메뉴 `"Language"`/`한국어`/`English`가 있다. 검색 색인 토큰은 UI 누락으로 세지 않는다. | learnability | — | `App.xaml.cs` 438–491행 등, `ThemedShellPresenter.cs`, `GalleryWindow.ShowStatus` |
| LRN-16 | `ex.Message` 계열은 Diagnostics 제외 13파일 46곳. 화면으로 붙는 대표는 풍선, 라이브러리, 영상 편집, 녹화 실패, 설정 가져오기다. 46곳 전부가 화면인지는 확인하지 않았다. | learnability | functionality | `rg` `ex.Message\|exception.Message\|e.Exception.Message` in `src/**/*.cs` |
| LRN-17 | 미서명 설치 경고를 설치 UI가 설명하지 않는다. 서명 부재의 감점 주 소유자는 trust다. | trust | learnability | `README.md`, `build/package.ps1` INTEGRITY 절, `SECURITY.md`. `install.ps1`에 Authenticode 안내 없음 |
| LRN-18 | 기능 수는 Windows 캡처 도구와 ShareX 사이다. 첫 10분 가르침은 OS 제스처나 Snipaste의 F1/F3 시작 문장보다 약하다. ShareX 온보딩 문서는 404로 미확인. | learnability | positioning | Snipaste Getting Started (2026-01-06), Microsoft Snipping Tool Support, `docs/competitive-matrix.md`. `docs/market/` 미사용 |
| LRN-19 | 첫 캡처 오버레이와 핀 툴팁, 녹화 조작 창은 그 단계에 들어온 뒤의 조작(드래그, Esc, Ctrl+C, 휠, 정지 버튼)은 말한다. 그 화면에 들어오는 전역 키는 말하지 않는다. | learnability | — | `Text_798E080A9262`, `Text_73D6AF7EE15B`, `RecordingControlWindow.cs` |
