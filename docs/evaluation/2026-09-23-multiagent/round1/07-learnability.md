# 라운드 1 — 학습성·온보딩·현지화

대상: Release 3.0.0 (`117b0ef`). 제품 소스는 수정하지 않았다. `docs/market/`는 인용하지 않았다. 이 환경은 Linux라 Windows 11에서 설치·첫 실행을 재현하지 못했다. 아래는 코드·리소스·가이드·이번에 연 공개 문서를 읽은 결과다.

## 1. 점수: 2 / 5

캡처를 시작하는 버튼과 준비 알림은 있다. 첫 10분에 C/X/Z와 F3/F4를 한 세트로 가르치는 표면은 없다. `IsFirstRun`은 저장만 되고 환영 창·코치마크·첫 실행 분기는 없다. 앱 안 「사용 방법」은 단축키 이름을 빠뜨리고, 저장소 README는 GitHub 단축키를 코드 기본값 F9가 아니라 F4로 적는다. 한국어/영어 리소스 키는 대칭이다. 그 강점이 첫 실행의 침묵을 메우지는 못한다.

5는 이 제품 범위에서 단축키 가족이 첫 화면과 도움에 살아 있는 상태, 3은 핵심 흐름을 앱 안에서 따라갈 수 있고 공백이 설정·심화에 머무는 상태다. 지금은 캡처 한 가지 외에는 문서를 열거나 설정 단축키 탭을 열어야 한다.

## 2. 첫 10분 시나리오

기본 단축키는 코드에 있다. 영역 캡처 `Ctrl+Shift+C`, 라이브러리 `Ctrl+Shift+Z`, 영역 녹화 `Ctrl+Shift+X`, 화면 고정 `F3`, 모든 핀 숨기기 `Shift+F3`, GitHub 이미지 URL `F9`. 클릭 통과·이전 영역·창·전체 화면은 비어 있다. 근거: `src/MyCapture.Core/Settings/AppSettings.cs`의 `HotkeySettings`. 2.2.0 이전 파일의 맨 F4만 F9로 올리고, 새 설정의 빈 GitHub 단축키도 F9로 되돌린다. 근거: `src/MyCapture.Core/Settings/SettingsStore.cs`.

| 단계 | 사용자에게 무엇이 보이도록 짜여 있나 | 그 단계의 가르침 |
|---|---|---|
| 설치 경고 (미서명) | 앱이 뜨기 전에 Windows가 게시자 경고를 낼 수 있다. 설치 스크립트는 그 경고를 설명하지 않는다. | 저장소 `README.md`와 `build/package.ps1`가 생성하는 오프라인 안내가 미서명과 SHA-256을 말한다. 설치 마법사 문구는 아니다. **OVERLAP trust.** 서명 부재의 주 소유자는 trust. 학습성 쪽 공백은 “경고가 뜬 순간에 제품이 다음 행동을 말하지 않는다”이다. |
| 트레이 상주 | 바로가기 실행은 라이브러리 창을 연다. `--background`(로그인 자동 실행)는 창 없이 트레이만 남긴다. 라이브러리의 일반 닫기는 취소되고 `Hide()`다. | 준비 알림 한 번: 「{캡처 단축키}로 화면을 캡처하세요. 트레이 아이콘에서 설정과 라이브러리를 열 수 있습니다.」 5초, 소리 없음, 실패하면 다시 띄우지 않는다. 창을 닫아도 프로세스가 남는지, 종료는 트레이의 「종료」인지, 다음 로그온에는 창이 없는지는 말하지 않는다. |
| 첫 캡처 | 라이브러리 헤더 버튼이 트레이용 문자열 `영역 캡처(&R)\tCtrl+Shift+C`를 그대로 쓴다. 좌클릭 트레이는 캡처가 아니라 라이브러리다. | 오버레이에 들어오면 「드래그… Shift 정밀 이동 · 놓으면 확정 · Esc 취소」. 편집기 상태 줄은 Ctrl+C / Ctrl+S / Esc. 전역 단축키가 무엇인지, 녹음과 어떻게 다른지는 오버레이가 가르치지 않는다. |
| 첫 핀 | 트레이·준비 알림·「사용 방법」·빈 라이브러리에 F3이 없다. 캡처가 생긴 뒤 타일의 핀 아이콘 툴팁은 「화면에 떠 있는 창으로 고정」이다. | 핀이 열린 뒤 툴팁이 우클릭 저장, Ctrl+더블클릭 원문, Ctrl+C 이미지, 드래그, 휠을 말한다. F3으로 들어오라는 안내는 없다. 클립보드가 비면 F3을 누른 뒤에야 「이미지, 텍스트 또는 표가 없습니다」가 나온다. |
| 첫 녹화 | 라이브러리 헤더와 트레이 항목 라벨은 `화면 녹화` / `Screen recording`이다. 단축키가 들어 있지 않다. | 녹화 조작 창은 시작·정지 버튼, 방향키 도움, 시계 툴팁을 갖는다. 「같은 키로 중지」는 `HotkeySettings` 주석과 README에 있고, 조작 창 문구에는 없다. |

준비 알림은 단축키 등록에 성공했을 때만 나온다. 충돌이 있으면 그 대신 「단축키 충돌」 경고가 나가고 준비 알림은 생략된다. 근거: `src/MyCapture.App/App.xaml.cs`, `src/MyCapture.App/ResidentReadyNotification.cs`.

`GeneralSettings.IsFirstRun`은 설정 파일이 없을 때 true로 저장되고, 초안 복제 때 보존된다. `src/MyCapture.App`에서 이 플래그를 읽어 창을 여는 코드는 검색되지 않았다. 코치마크·환영 창·온보딩 타입 이름도 `src`에서 검색되지 않았다.

로그인 시 창이 사라지는 경로는 기본값과 연결된다. `LaunchAtLogin` 기본값은 true이고, 셸 초기화가 `ReconcileOnStartup`으로 현재 사용자 Run 키에 `"…\MyCapture.exe" --background`를 맞춘다. 다음 로그온은 라이브러리를 열지 않는다. 근거: `AppSettings.cs`, `StartupRegistrationService.cs`, `App.OnStartup`. 동의 없는 자동 실행 자체는 **OVERLAP trust**, 주 소유자 trust. 학습성은 「창이 없는 이유와 다시 여는 법」을 소유한다.

## 3. 가이드 문서와 제품 안 도움

둘을 같은 점수로 치지 않는다. 문서가 설명해도 앱이 침묵하면 학습성이다.

### 문서에서 되는 것

`docs/guides/README.md`는 한국어로 라이브러리, 주석, 영상 편집, GIF/MP4, 설정을 순서대로 적는다. C/X/Z, 편집기 Ctrl+C·Ctrl+S, 빠른 가리기 Ctrl+Shift+R, 언어는 다음 시작·테마는 즉시, 녹화에 오디오가 없다는 점이 본문에 있다. 같은 폴더에 스크린샷 4개(`library-midnight.png`, `annotation-glass.png`, `video-editor-glass.png`, `settings-glass.png`)가 있다. 이 이미지가 3.0.0 창과 같은지는 열어서 대조하지 않았다.

루트 `README.md`의 단축키 표는 C/X/Z와 F3, Shift+F3, 편집기 저장을 표로 갖는다. 영어는 맨 아래 요약 한 단락뿐이다. 영어 사용법 가이드 파일은 `docs/guides/`에 없다.

### 문서가 오히려 가르치는 잘못된 키

루트 README 기본 단축키 표는 GitHub 첨부를 `F4`로 적고, F3 행에 「F4 이후에는 추출한 GitHub 이미지 URL을 고정」이라고 적는다. 3.0.0 코드의 기본값과 설정 문구·오류 문구는 F9다 (`Settings.GitHubIssueUrlHint`, `GitHub.NeedImage`, `GitHub.Failed`). 가이드 README는 F3/F4/F9를 아예 다루지 않는다. 그래서 가이드는 핀을 가르치지 않고, README는 없는 기본 키를 가르친다.

### 앱 안이 침묵하는 곳

트레이 메뉴의 「사용 방법」(`Shell.Help` / `Shell.HelpText`)이 제품 안의 유일한 도움 창이다. 한국어 본문 전체는 다음이다.

> 영역 캡처 / 다시 캡처: 설정에 지정된 캡처 단축키  
> 드래그로 영역을 정한 뒤 도형과 텍스트를 편집하세요.  
> 확인 또는 저장을 눌러야 갤러리에 추가됩니다. 취소하거나 다시 캡처한 초안은 저장되지 않습니다.  
> Shift를 누르면 픽셀 단위로 정밀하게 선택합니다.  
> 트레이에서 갤러리, 설정, Language를 선택할 수 있습니다.

Ctrl+Shift+C/X/Z, F3, F9, 핀, 녹화가 없다. 라이브러리를 「갤러리」라고 부르고, 메뉴 이름 「Language」를 영어 그대로 둔다. 라이브러리 창에는 이 도움으로 가는 버튼이 없다. `docs/guides`로 나가는 링크도 `src`에서 검색되지 않았다.

설정 일반 탭의 「빠른 작업 흐름」 카드는 `Ctrl+Shift+C/X/Z` 세 줄을 XAML에 고정 문자열로 그린다. F3은 없다. 사용자가 단축키를 바꿔도 이 카드는 설정값을 따라가지 않는다. 단축키 탭의 입력란에는 현재 값이 보이므로, 설정을 연 사람은 F3과 GitHub 항목을 발견할 수 있다. 첫 라이브러리에서는 그 탭까지 가지 않아도 된다.

빈 라이브러리 제목은 「아직 표시할 항목이 없습니다」, 본문은 「아직 이미지나 동영상이 없습니다. 캡처하거나 녹화하면 여기에 표시됩니다.」 검색이 비면 「검색 결과가 없습니다.」 키 이름은 없다. 헤더의 캡처 버튼이 옆에 있으므로 빈 화면이 막다른 길은 아니다. 녹화 버튼은 키를 보여 주지 않는다.

## 4. 현지화 대칭

카탈로그는 두 파일뿐이다. `Strings.resx`(중립, 한국어), `Strings.en.resx`.

검색: `<data name="…">` 를 두 파일에서 세고, `<value>`를 짝지었다.

| 항목 | 결과 |
|---|---|
| 키 수 | 한국어 897, 영어 897, 고유 키도 각각 897 |
| 한쪽에만 있는 키 | 0 |
| 빈 값 | 0 |
| 영어 값 안의 한글 | 0 |
| 자리 표시자 `{\d}` 불일치 | 0 |
| 값이 완전히 같은 키 | 4개: `Video.MarkIn` In, `Video.MarkOut` Out, `Recording_Fixed720` 1280×720, `Recording_Fixed1080` 1920×1080 |

`LocalizationTests`는 키 집합·형식 문자열·C#/XAML의 `UiText.Get`/`Format`/`{loc:Text }` 참조가 두 언어에서 비어 있지 않은지를 검사하고, 하한만 `>= 724`로 둔다. 위의 897은 그 하한이 아니라 이번 검색 횟수다.

문화 선택: 빈 설정은 Windows 표시 언어, `ko`면 한국어, 그 외와 지원하지 않는 값은 영어. 없는 키는 영어 문장으로 떨어지지 않고 `MissingManifestResourceException`이다. 키 집합이 같으므로 영어 누락으로 한국어가 새는 경로는 이 두 파일 기준으로는 닫혀 있다.

적용 시점: 설정 힌트와 `Settings.LanguageRestart`는 「적용하면 저장되고, 다음 시작에 모든 창과 메뉴에 적용, 진행 중 작업은 종료하지 않음」이다. 설정 적용은 언어가 바뀌면 그 문장을 메시지 상자로 보여 창을 바로 닫지 않는다. 테마는 `ThemeService.ApplyFromSettings`로 즉시 바뀐다. 여기까지는 문서 `docs/localization.md`와 같다.

트레이의 언어 항목은 따로 동작한다. 머리글이 하드코딩 `"Language"`이고, 항목은 `"한국어"`/`"English"`, 저장 값은 `"ko"`/`"en"`이다. 설정 콤보의 Tag는 `""`, `"ko-KR"`, `"en-US"`뿐이다. 트레이에서 고른 값이 콤보 선택과 맞지 않을 수 있다. 이 불일치를 Windows에서 콤보가 빈칸으로 보이는지까지는 확인하지 않았다. 트레이 변경은 `UiText.Configure`를 즉시 호출한 뒤, 제목이 `"Language"`인 알림으로 재시작을 말한다. 이미 열린 창은 리소스를 다시 읽지 않는다.

설정 설명 문구: `Settings.Description` 스타일의 한 줄 `TextBlock` 28개 중 23개는 `{loc:Text …}`, 5개는 리터럴 `0.125 ~ 512 GiB.`, `96 ~ 1024px.`, `1 ~ 64.`, `6 ~ 400.`, `0 ~ 255.`다. OCR 탭의 보이는 Header는 `"OCR"`이고, 접근성 이름과 본문 제목은 리소스(`OCR 설정`, `문자 인식(OCR)`)다.

`src`의 `UiText.Get` 호출 694, `UiText.Format` 124, `{loc:Text ` 237. 같은 키를 여러 번 부르므로 고유 키 수가 아니다.

사용자에게 보이는 하드코딩(리소스 밖, 검색으로 모은 것):

- 설정 XAML: 위 5개 범위, `Ctrl+Shift+C/X/Z` 세 줄, 콤보 `한국어`/`English`, Header `OCR`, `AutomationProperties.Name="MyCapture"`
- 라이브러리 XAML: `AutomationProperties.Name="MyCapture"`
- `ThemedShellPresenter.cs`: `"Language"`, `"한국어"`, `"English"`
- `App.xaml.cs` GitHub 알림 제목 `"MyCapture"` 6곳(438, 450, 460, 467, 481, 491행)과 언어 알림 제목 `"Language"`
- 메시지 상자 제목 `"MyCapture"`: `App.xaml.cs` 3곳, `GalleryWindow.xaml.cs` `ShowStatus`, `AnnotationEditorControl.cs` 이미지 삽입 실패

창 제목 리터럴 `"MyCapture"`는 `WindowIdentity`가 `MyCapture-{버전}`으로 바꾼다. 제품명 자체는 고유명사라 번역 누락으로 치지 않는다. 검색 색인 토큰(`동영상 비디오 video recording` 등)과 GitHub 웹 문구 탐지는 `docs/localization.md`가 번역하지 말라고 한 검색 어휘에 가깝다. UI 카피로 세지 않았다.

설치 프로그램 오류는 resx 밖이다. `build/installer/install.ps1`의 `Throw-InstallerError` 메시지는 영어다. 예: Windows 11 21H2 미만일 때의 문장. 한국어 Windows에서 설치가 거절되면 영어 문장을 본다.

오류 문구: `ex.Message` / `exception.Message` / `e.Exception.Message`를 `src/**/*.cs`에서 찾고 `Diagnostics`를 빼면 13개 파일, 합계 46곳이다. 로그로만 쓰이는 줄이 섞여 있어 46곳 전부가 화면은 아니다. 화면으로 이어지는 대표는 트레이 풍선과 메시지 상자(`App.xaml.cs`), 라이브러리 상태(`GalleryWindow.xaml.cs`), 영상 편집기 상태 줄, 녹화 실패 상자, 설정 가져오기 실패다. .NET 예외 문장은 한국어 UI 문장 뒤에 그대로 붙는다.

## 5. 경쟁: 학습 비용의 위치

이번에 연 공개 문서:

- [Snipaste Getting Started](https://github.com/Snipaste/feedback/wiki/Getting-Started) (위키 편집일 2026-01-06). 기능을 Snip, 주석, Paste 세 덩이로 시작하고, 기본 키를 F1(캡처), F3(붙이기), Shift+F3(모두 숨기기)으로 적는다. 트레이 왼쪽 클릭은 캡처, 가운데 클릭은 붙이기다.
- [Microsoft Support — Snipping Tool](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots). Windows 11에서 Win+Shift+S는 이미지 오버레이, Win+Shift+R은 동영상 오버레이다. 시작 메뉴 검색으로도 연다. 캡처 후 창 안에서 펜·도형·텍스트 추출을 고른다.
- `docs/competitive-matrix.md`가 가리키는 ShareX 가이드 `https://getsharex.com/blog/guides/` 는 이번 세션에서 404였다. ShareX의 현재 온보딩 문장은 확인하지 못했다. 같은 매트릭스가 적은 범위(캡처 후 작업, 업로드 자동화, FFmpeg 녹화)만 기능 폭의 근거로 둔다.

Windows 캡처 도구는 이미 알고 있는 OS 제스처 두 개와, 화면이 어두워진 뒤의 작은 도구막대로 시작한다. 별도의 상주 프로세스를 배우지 않는다. Snipaste는 기능이 적어도 키가 제품의 첫 문장이다. ShareX는 확인된 범위만 봐도 캡처 이후 작업이 많아 배울 항목이 길다. 오디오·스트리밍은 MyCapture 범위 밖이라 여기서 감점하지 않는다.

MyCapture의 기능 수는 그 사이에 있다. 계정 없는 로컬 라이브러리, 핀, 비파괴 주석, 오디오 없는 설명용 녹화, GIF 20초 제한이 Windows 캡처 도구보다 많고, ShareX식 작업 파이프라인은 기본 경로가 아니다. 학습 설계는 그 중간에 있지 않다. 첫 화면은 OS 도구처럼 짧지 않고, Snipaste처럼 키 가족을 시작 문장에 올리지도 않는다. 키는 설정 카드와 README에 있고, 준비 알림과 「사용 방법」은 캡처만 말한다. 트레이 왼쪽 클릭은 캡처가 아니라 라이브러리다. 닫으면 종료처럼 보이고 프로세스는 남는다.

## 6. 보완과 OVERLAP 주 소유자

### P0 — 학습성 주 소유

1. 첫 실행(또는 처음 뜨는 라이브러리)에서 현재 설정된 C, X, Z, F3, F9를 말로 보여 준다. `IsFirstRun`을 소비하거나, 지금 「사용 방법」을 그 다섯 키로 다시 쓴다. 준비 알림 한 줄에 캡처만 두지 않는다.
2. 라이브러리를 숨길 때 「알림 영역에 남아 있습니다. 종료는 트레이의 종료」를 한 번 말한다. 로그인 `--background`로 창 없이 올라올 때도 같은 문장을 쓴다.
3. `README.md`의 F4를 코드와 맞춘다. 기본값은 F9이고, F4는 예전에 옮겨 간 키다. 가이드에 핀(F3)과 녹화 시작 키를 넣되, 앱 안 도움이 문서를 대신하게 둔 채 가이드만 고치지 않는다.

### P1

- 트레이·라이브러리의 녹화/라이브러리 라벨에 실제 단축키를 넣는다. 설정 카드의 `Ctrl+Shift+C/X/Z` 고정 문자열을 현재 `HotkeySettings`에서 만든다.
- 트레이 언어 저장값을 설정 콤보 Tag(`ko-KR`/`en-US`/빈 값)와 같게 하고, `"Language"` 머리글과 도움 문장의 Language를 리소스로 옮긴다.
- 사용자에게 붙는 `ex.Message`를 원인별 리소스 문장으로 바꾼다. 예외 원문은 로그로 남긴다. 실패의 기능 동작이 잘못된 경우는 functionality, 문장이 배우기에 쓸모없는 경우는 학습성. 주 소유자는 학습성.

### P2

- `docs/guides`의 영어 대응. 루트 README의 영어는 요약 한 단락이다.
- 설정 범위 리터럴 5개와 OCR 탭 Header를 resx로 옮긴다.
- 라이브러리 아이콘 버튼 중 툴팁이 없는 것을 정리한다. `GalleryWindow.xaml`의 `<Button` 17개, `ToolTip=` 11개다. 글자가 있는 버튼은 툴팁이 없어도 읽히므로, 아이콘만 있는 버튼을 따로 세지는 않았다.

### 주 소유자를 넘기는 OVERLAP

| 사실 | 주 소유 | 학습성이 남기는 것 |
|---|---|---|
| Authenticode 미서명, SmartScreen/알 수 없는 게시자 | trust | 경고 순간에 체크섬·계속 실행을 알려 주지 않음. 서명 도입 자체는 채점하지 않음 |
| `LaunchAtLogin` 기본 true, Run 키에 `--background` | trust | 다음 시작에 창이 없는 이유를 알려 주지 않음 |
| 단축키 문자열이 설정과 어긋나게 고정된 것 | learnability | 등록 실패·롤백의 동작은 functionality |

## 7. 미확인

- Windows 11 한국어/영어에서 5초 준비 창을 실제로 보는지, 알림 영역 오버플로로 아이콘이 숨는지, 닫기 직후 사용자가 프로세스가 끝났다고 판단하는지는 실행하지 못했다.
- 라이브러리 버튼에 `(&R)`과 탭 문자가 글자로 보이는지.
- 트레이에서 고른 `ko`/`en`이 설정 콤보에서 빈 선택으로 보이는지.
- SmartScreen 문구가 22H2/24H2에서 어떻게 다른지. 코드는 그 대화상자를 만들지 않는다.
- `docs/guides/shots/*.png` 4장이 3.0.0 UI와 같은지.
- ShareX 공식 가이드 URL은 404였다. Greenshot, 알캡처, PicPick, ScreenToGif의 온보딩 문서는 이번 세션에서 열지 않았다.
