# 기능별 사용법 영상 생성기

F1 도움말과 설정의 **사용법 보기**가 재생하는 `src/MyCapture.App/Assets/Tutorials/{기능}.{ko|en}.mp4`와 영상이 열리기 전에 표시하는 첫 프레임 정지 화면 `{기능}.{ko|en}.jpg`를 현재 소스에서 다시 만듭니다. 기능 목록은 제품의 `TutorialCatalog`(38개)를 그대로 사용하므로 한국어와 영어 76개가 항상 카탈로그와 일치합니다.

```powershell
# 전체 76개를 artifacts/tutorials/<시각>에 만들고 검토용 시트를 함께 저장합니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release

# 일부 기능과 언어만 만듭니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release -- --features image-crop,pin-copy --languages ko

# 결과(영상과 정지 화면)를 제품 자산 폴더에 복사합니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release -- --install

# 만든 영상의 특정 시점을 원본 크기 PNG로 꺼냅니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release -- inspect artifacts/tutorials/<시각>/image-crop.ko.mp4 0,5200

# 검토용으로 다른 테마에서 만듭니다. 포인트 색은 빨간색으로 고정됩니다.
# 제품 자산(--install)은 재현 가능한 기본 렌더(어두운 테마, 빨간색)로만 만듭니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release -- --features pin-copy --languages ko --theme glass-light
```

깨끗한 클론에서 별도 준비 없이 실행됩니다. 생성기는 `MyCapture.App` 프로젝트를 참조해 현재 소스로 함께 빌드하고, 제품은 `InternalsVisibleTo("MyCapture.TutorialGenerator")`로 이 도구에만 내부 API를 엽니다. 출력은 Git이 무시하는 `artifacts/` 아래에 둡니다.

## 출력 형식

| 항목 | 값 |
| --- | --- |
| 형식 | H.264 High 프로필 MP4, 오디오 없음 |
| 해상도 | 960×540(`--width`로 변경). 사용법 창의 640×360 DIP 영역을 150% 배율에서 1:1로 채웁니다 |
| 프레임 | 25fps, 클립당 약 11초 |
| 화질 | Media Foundation 소프트웨어 H.264 인코더의 고정 품질 60(`--quality`)과 품질 우선 설정(QualityVsSpeed 100). GPU와 관계없이 같은 인코더를 씁니다. 품질 우선 설정은 키 표시가 사라진 자리에 남던 옅은 잔상도 없앱니다 |
| 색 범위 | 전체 범위(0–255)로 인코딩하고 스트림에 표시합니다. 제품 플레이어인 WPF `MediaElement`는 디코딩한 값을 범위 확장 없이 표시하므로, 녹화와 같은 제한 범위(16–235)로 만들면 흰색이 235, 검은색이 16으로 흐리게 보입니다 |
| 정지 화면 | 첫 렌더 프레임을 JPEG 품질 90으로 저장합니다. 사용법 창은 이 파일을 바로 표시하고 영상이 열리면 영상으로 넘어갑니다 |
| 테마 | 기본 렌더는 Glass(어두운 테마)와 빨간색입니다. 검토용 `--theme` 값은 system(시스템), glass-light(밝은 테마), glass(어두운 테마), high-contrast(고대비)입니다. 이전 테마 이름은 제품과 같은 별칭으로 받으며, 이전 violet 인수도 제품과 같이 빨간색으로 정규화합니다 |
| 단축키 표시 | `NewInstallDefaults`의 새 설치 설정(`AlwaysShowShortcutHints` 꺼짐)을 사용합니다. 제품처럼 버튼 아래 키 캡션은 Alt를 누르는 동안만 보이고 장면은 Alt를 누르지 않으므로, 영상에는 키 캡션이 없습니다. 아래 설명 띠의 키는 해당 장면에서 실행하는 조작을 나타냅니다 |

정지 구간은 움직임이 없어 거의 용량을 차지하지 않고, 첫 프레임은 키 프레임이라 애니메이션 줄이기 설정에서도 바로 보입니다.

검토 시트와 `inspect`는 하드웨어 디코딩으로 읽습니다. 소프트웨어 디코딩 경로는 스트림의 전체 범위 표시를 무시해 어두운 색이 뭉개진 화면을 돌려주므로, 하드웨어 디코더가 없는 PC에서는 시트의 대비가 실제 재생보다 강하게 보입니다.

## 구성

- `Stage`: 960×540 논리 좌표의 한 프레임입니다. 1 단위가 제품 UI의 1 DIP이며, 아래 52 DIP에는 설명과 누르는 키를 표시합니다. 포인터, 클릭 표시, 창 틀(Windows 11 캡션 모양)을 그립니다.
- `OffscreenWindow`: 실제 제품 창을 모든 모니터 밖에 띄워 로드·레이아웃·이벤트가 사용자 환경과 같게 실행되게 한 뒤 클라이언트 영역을 렌더합니다. 화면에 보이지 않고, 활성화되지 않으며, 작업 표시줄에도 나타나지 않습니다. 생성기 프로세스는 DPI를 인식하지 않으므로 창이 96 DPI로 배치되어 960×540 출력과 픽셀 단위로 맞습니다.
- `TutorialStoryboard`: 정지(Hold)와 움직임(Motion·Move·Click) 계획입니다. 정지 구간은 한 번만 렌더하고 길이만큼 반복 기록합니다.
- `Scenes/*`: 기능별 장면입니다. 생성자에서 제품 UI와 스토리보드를 만들고, `Draw`가 프레임마다 제품 상태를 바꾼 뒤(클릭, 단축키, 드래그) 화면을 그립니다. 제품 동작은 제품 코드가 수행합니다. 예를 들어 이미지 편집기는 `AnnotationEditorControl.SimulatePointerDown/Move/Up`과 실제 명령 버튼, `HandleShortcut`으로 조작합니다.
- `ClipWriter`: 제품의 `MediaFoundationVideoEncoder`로 MP4를 기록합니다(`FullRange`). `ClipReview`는 첫 프레임 정지 화면을 저장하고, 제품의 `MediaFoundationVideoFrameReader`로 완성된 MP4를 다시 읽어 검토 시트를 만듭니다.
- `Fixtures`: 다른 회사 앱을 대신하는 합성 콘텐츠(체크리스트 페이지, 메모, 터미널)입니다. MyCapture 화면은 모두 제품 UI를 그대로 렌더합니다.

## 장면 작성 규칙

- 제품의 `App` 클래스를 만들지 않습니다. WPF는 `Application` 생성 시 `OnStartup`을 예약하므로, 첫 디스패처 처리에서 상주 앱(사용자 설정, 라이브러리, 보존 기간 정리, 트레이, 전역 단축키, 편집 초안 복구)이 생성기 안에서 시작됩니다. 생성기는 `App.xaml`과 같은 리소스만 가진 일반 `Application`을 사용합니다.
- 사용자 프로필을 건드리지 않습니다. 라이브러리가 필요한 장면은 출력 폴더 아래 임시 `AppPaths`를 사용합니다. 클립보드 복사, 파일 저장, 휴지통 이동, OCR 모델 다운로드처럼 바깥에 흔적을 남기는 거래는 실행하지 않고, 제품이 그 결과로 보여 주는 화면 상태를 재현합니다. 편집기 커밋은 `CommitRequested`를 장면이 대신 받습니다.
- 캡처와 핀의 출처는 제품이 기록하는 대로 픽스처에 넣습니다. 체크리스트 페이지는 웹 주소(`Fixtures.ChecklistSource`), 메모장 캡처는 앱 이름(`Fixtures.NotepadSource`), 라이브러리 카드는 창 제목에 맞는 페이지나 앱을 씁니다. 전체 화면 캡처와 다른 앱에서 복사한 글자 핀에는 제품처럼 출처가 없습니다. 편집기 상태 줄의 출처 칩, 라이브러리 카드의 출처 배지, 핀 호버 막대의 원본 열기 단추는 그리기만 하고 누르지 않습니다. 누르면 브라우저나 앱이 실제로 열리므로 핀의 `OpenSource`도 장면이 막아 둡니다.
- 제품 흐름은 장면에서 다시 만들지 않고 제품 코드를 호출합니다. 라이브러리 삭제는 `LibraryPermanentDeletion.CreateRecycleConfirmation`이 만드는 확인 창에 답한 뒤 `GalleryWindow.DeleteCapturesAsync`를 실행하므로 상태 줄의 결과와 되돌리기가 제품 그대로 나옵니다. 픽스처의 `RecoverableLibraryService`는 휴지통 대신 픽스처 안 폴더로 옮기는 trash를 씁니다. 트레이 메뉴는 `Scenes/TrayMenu.cs`가 앱과 같은 명령(클립보드를 핀으로 띄우기, 핀 모두 숨기기/보이기, 모든 핀 클릭 허용 해제 포함)과 새 설치 단축키로 `TrayMenuLayout.Build`를 호출해 만듭니다.
- 자막이 제품의 버튼, 메뉴, 결과, 키를 가리킬 때는 문자열을 직접 쓰지 않고 제품 문구 키(`UiText`), `ShortcutCatalog`, `TutorialCatalog`의 고정 키, 또는 실제 컨트롤이 표시하는 글자에서 가져옵니다. 제품에서 이름이 바뀌면 다시 생성할 때 자막도 함께 바뀝니다. 키 입력도 `ShortcutCatalog`의 키로 `HandleShortcut`을 호출합니다.
- 실제 마우스를 잡지 않습니다. 화면 밖 창에서 마우스 캡처를 얻으면 사용자의 클릭이 생성기로 갈 수 있습니다.
- 팝업(ContextMenu, Popup, 드롭다운, 도구 설명)은 열지 않습니다. WPF는 팝업을 모니터 작업 영역 안으로 옮기므로 사용자 화면에 나타날 수 있습니다. 메뉴는 부모 없이 Measure/Arrange한 요소로 렌더합니다(`ThemedShellPresenter.CreateMenu`, 핀의 `ContextMenu`).
- 화면에 직접 자리를 잡는 창(알림 카드, 녹화 제어 창, 카운트다운, 핀 꽂힘 장식, 캡처 비행 장식)은 띄우지 않고 내용만 렌더합니다. 알림 카드는 `ThemedShellPresenter.CreateActionNotification`으로 만들고, 그림자 여백을 포함한 창 너비와 자연 높이로 렌더해 `ThemedShellPresenter.NotificationBounds`가 정하는 자리에 그립니다. 창 틀은 덧그리지 않습니다. 핀의 클릭 허용 손잡이와 첫 안내 말풍선은 제품이 화면 밖 핀 옆에 띄운 창의 표면을 `PinClickThroughHandle.Placement`가 정하는 핀 모서리 자리에 그리고, 말풍선은 제품 타이머 대신 영상 시간으로 같은 시간만큼 보여 줍니다.
- 새로 열거나 다시 연 핀의 꽂힘 효과는 `PinManager`처럼 핀이 열린 직후 시작합니다. 장식 창(`PinLandingDecoration`)은 핀의 장면 위치와 `LayoutFor`로 만들고, 제품의 벽시계 대신 영상 시간으로 `Advance`합니다. 테두리 빛(`BorderAccentAmount`)과 83ms 내용 페이드도 같은 시간으로 맞춥니다. 효과가 프레임마다 그려지도록, 핀이 열린 때부터 테두리가 원래 색으로 돌아오는 `OutlineEndMs`까지는 정지 구간 대신 프레임 단위 구간으로 둡니다. 이 구간은 장식이 닫히는 `DurationMs`도 덮습니다.
- 캡처가 빠른 작업 카드로 날아가는 효과(`CaptureFlight`)도 영상 시간으로 그립니다. 선택 화면이 닫히는 순간(영역 드래그를 놓을 때, 창 캡처 단축키, 전체 화면 명령) 캡처한 부분이 제자리에서 떠오르며 그림자가 167ms 동안 나타나고(`CaptureFlight.LiftFrame`), 카드가 나타나면 250ms 동안 카드 미리 보기로 날아갑니다(`CaptureFlight.FlightFrame`). `Scenes/CaptureFlightPicture.cs`는 제품의 `CaptureFlightDecoration`을 장면의 디스플레이 크기로 만들어 띄우지 않고 그 표면을 프레임마다 렌더합니다. 장면 단위가 제품 DIP이므로 그림자 배율은 1입니다. 카드는 `PrepareLanding`으로 투명하게 기다리다가 비행이 시작되면 `BeginLandingFade`와 같은 100ms 페이드(`LandingFadeDuration`)를 영상 시간으로 적용하고, 이미지가 내려앉는 프레임에 `RevealPreview`, 다음 프레임에 `CompleteLanding`을 부릅니다. 들어 올림(`LiftFrames`, 320ms)과 비행(`FlightFrames`)은 프레임 단위 구간이며, 카드 정지 구간을 비행 구간만큼 줄여 카드가 보이는 시간은 그대로 둡니다. 스크롤 캡처는 한 장으로 화면에 있던 적이 없으므로 제품처럼 비행 없이 카드가 나타납니다. 빠른 작업 영상은 카드 자체를 설명하므로 첫 프레임(정지 화면)부터 카드가 보이도록 비행 없이 시작합니다.
- 지연 캡처 카운트다운은 창 하나를 띄우지 않고 `CountdownWindow.PoseAt(남은 초, 그 초에서 지난 시간)`으로 프레임마다 맞춥니다. 새 숫자의 착지, 이전 숫자의 퇴장, 1초 동안 줄어드는 고리가 그려지고, 0이 되면 83ms 축소 퇴장 뒤에 선택 화면이 열립니다. 상태로 남는 표시는 제품 상태를 그대로 쓰고 시간만 영상에 맞춥니다. 녹화를 마친 막대의 저장됨은 `RecordingControlWindow.PreviewSavedForTest`로 만들고, 빠른 작업 카드의 복사 확인 표시는 제품의 벽시계 타이머를 멈춘 뒤 `QuickAccessWindow.SuccessCheckTime`(1.2초)의 영상 시간이 지나면 `ElapseSuccessCheckForTest`로 끝냅니다. 편집기 빠른 저장 뒤에는 `ShowQuickSaved`와 같은 제목, 파일 이름, 폴더 열기로 알림 카드를 그립니다.
- 창 감지 강조의 83ms 이동은 지금 영상에 나오지 않습니다. 창 캡처 영상은 선택 화면 없이 단축키로 포인터 아래 창을 캡처합니다. 창 감지 선택 화면에서 대상이 바뀌는 장면을 만들면 `CaptureOverlayView.ReadClock`을 영상 시간으로 바꾸고, 대상이 바뀌는 프레임에서 오버레이를 갱신하는 동안만 `FluidMotion.AnimationsEnabledOverrideForTest`를 켰다가 되돌려 이동을 프레임마다 그립니다.
- 파일이 필요한 장면(동영상 편집기가 여는 합성 녹화, 실제 GIF 내보내기 결과)은 `Program.WorkFolder`(출력 폴더의 `work`) 아래 장면별 폴더에 만들고 장면이 끝나면 지웁니다. 모달 대화 상자는 띄우지 않습니다. 동영상 편집기의 내보내기 대화 상자는 `ExportDialogHostForTest`로 화면 밖에 열고, Windows 저장 대화 상자처럼 운영체제가 그리는 창만 장면이 직접 그립니다.
- 설명 문구는 제품 용어와 같게 씁니다(예: 블러, 뒤 창 클릭 허용, 빠른 작업, 닫은 핀 다시 열기). 한국어 설명은 명사형 또는 합니다체로 쓰고 해요체, 느낌표, 감탄사, 이모지를 쓰지 않습니다.
- 클립 길이는 7–15초여야 하며 보통 11초 안팎으로 맞춥니다.
- 캡처 결과는 빠른 작업 카드로 먼저 보여 준 뒤 Alt+Q와 편집 단축키로 편집기에 진입합니다. 아이콘 버튼의 자막은 접근성 이름을 우선 사용하며, 이름이 없는 버튼은 생성 오류로 처리합니다.

## 출력물

출력 폴더에는 `{기능}.{언어}.mp4`, 첫 프레임 정지 화면 `{기능}.{언어}.jpg`, 주요 장면을 디코딩한 `{기능}.{언어}.sheet.png`, 전체 결과를 담은 `receipt.json`(제품 DLL SHA256, 설정, 클립과 정지 화면별 크기·SHA256, 프레임·길이, 사용한 제품 UI 설명)이 생깁니다.

검토 시트의 첫 줄은 정확한 첫 프레임·중간 프레임·마지막 프레임이고, 나머지는 주요 동작입니다. `inspect`는 원본 영상 옆에 PNG를 쓰므로 설치된 자산 폴더에서 실행하지 않습니다. 설치 자산을 검사할 때는 먼저 `artifacts/` 아래로 복사합니다.
