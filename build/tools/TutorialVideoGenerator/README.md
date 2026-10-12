# 기능별 사용법 영상 생성기

F1 도움말과 설정의 **사용법 보기**가 재생하는 `src/MyCapture.App/Assets/Tutorials/{기능}.{ko|en}.mp4`를 현재 소스에서 다시 만듭니다. 기능 목록은 제품의 `TutorialCatalog`(38개)를 그대로 사용하므로 한국어와 영어 76개가 항상 카탈로그와 일치합니다.

```powershell
# 전체 76개를 artifacts/tutorials/<시각>에 만들고 검토용 시트를 함께 저장합니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release

# 일부 기능과 언어만 만듭니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release -- --features image-crop,pin-copy --languages ko

# 결과를 제품 자산 폴더에 복사합니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release -- --install

# 만든 영상의 특정 시점을 원본 크기 PNG로 꺼냅니다.
dotnet run --project build/tools/TutorialVideoGenerator -c Release -- inspect artifacts/tutorials/<시각>/image-crop.ko.mp4 0,5200
```

깨끗한 클론에서 별도 준비 없이 실행됩니다. 생성기는 `MyCapture.App` 프로젝트를 참조해 현재 소스로 함께 빌드하고, 제품은 `InternalsVisibleTo("MyCapture.TutorialGenerator")`로 이 도구에만 내부 API를 엽니다. 출력은 Git이 무시하는 `artifacts/` 아래에 둡니다.

## 출력 형식

| 항목 | 값 |
| --- | --- |
| 형식 | H.264 High 프로필 MP4, 오디오 없음 |
| 해상도 | 960×540(`--width`로 변경). 사용법 창의 640×360 DIP 영역을 150% 배율에서 1:1로 채웁니다 |
| 프레임 | 25fps, 클립당 약 11초 |
| 화질 | Media Foundation 소프트웨어 H.264 인코더의 고정 품질 60(`--quality`). GPU와 관계없이 같은 결과를 냅니다 |
| 테마 | 기본 테마(Glass, 어두운 테마) |

정지 구간은 움직임이 없어 거의 용량을 차지하지 않고, 첫 프레임은 키 프레임이라 애니메이션 줄이기 설정에서도 바로 보입니다.

## 구성

- `Stage`: 960×540 논리 좌표의 한 프레임입니다. 1 단위가 제품 UI의 1 DIP이며, 아래 52 DIP에는 설명과 누르는 키를 표시합니다. 포인터, 클릭 표시, 창 틀(Windows 11 캡션 모양)을 그립니다.
- `OffscreenWindow`: 실제 제품 창을 모든 모니터 밖에 띄워 로드·레이아웃·이벤트가 사용자 환경과 같게 실행되게 한 뒤 클라이언트 영역을 렌더합니다. 화면에 보이지 않고, 활성화되지 않으며, 작업 표시줄에도 나타나지 않습니다. 생성기 프로세스는 DPI를 인식하지 않으므로 창이 96 DPI로 배치되어 960×540 출력과 픽셀 단위로 맞습니다.
- `TutorialStoryboard`: 정지(Hold)와 움직임(Motion·Move·Click) 계획입니다. 정지 구간은 한 번만 렌더하고 길이만큼 반복 기록합니다.
- `Scenes/*`: 기능별 장면입니다. 생성자에서 제품 UI와 스토리보드를 만들고, `Draw`가 프레임마다 제품 상태를 바꾼 뒤(클릭, 단축키, 드래그) 화면을 그립니다. 제품 동작은 제품 코드가 수행합니다. 예를 들어 이미지 편집기는 `AnnotationEditorControl.SimulatePointerDown/Move/Up`과 실제 명령 버튼, `HandleShortcut`으로 조작합니다.
- `ClipWriter`: 제품의 `MediaFoundationVideoEncoder`로 MP4를 기록합니다. `ClipReview`는 제품의 `MediaFoundationVideoFrameReader`로 완성된 MP4를 다시 읽어 검토 시트를 만듭니다.
- `Fixtures`: 다른 회사 앱을 대신하는 합성 콘텐츠(체크리스트 페이지, 메모, 터미널)입니다. MyCapture 화면은 모두 제품 UI를 그대로 렌더합니다.

## 장면 작성 규칙

- 제품의 `App` 클래스를 만들지 않습니다. WPF는 `Application` 생성 시 `OnStartup`을 예약하므로, 첫 디스패처 처리에서 상주 앱(사용자 설정, 라이브러리, 보존 기간 정리, 트레이, 전역 단축키, 편집 초안 복구)이 생성기 안에서 시작됩니다. 생성기는 `App.xaml`과 같은 리소스만 가진 일반 `Application`을 사용합니다.
- 사용자 프로필을 건드리지 않습니다. 라이브러리가 필요한 장면은 출력 폴더 아래 임시 `AppPaths`를 사용합니다. 클립보드 복사, 파일 저장, 휴지통 이동, OCR 모델 다운로드처럼 바깥에 흔적을 남기는 거래는 실행하지 않고, 제품이 그 결과로 보여 주는 화면 상태를 재현합니다. 편집기 커밋은 `CommitRequested`를 장면이 대신 받습니다.
- 실제 마우스를 잡지 않습니다. 화면 밖 창에서 마우스 캡처를 얻으면 사용자의 클릭이 생성기로 갈 수 있습니다.
- 설명 문구는 제품 용어와 같게 씁니다(예: 블러, 뒤 창 클릭 허용, 빠른 작업, 닫은 핀 다시 열기). 한국어 설명은 명사형 또는 합니다체로 쓰고 해요체, 느낌표, 감탄사, 이모지를 쓰지 않습니다.
- 클립 길이는 7–15초여야 하며 보통 11초 안팎으로 맞춥니다.

## 출력물

출력 폴더에는 `{기능}.{언어}.mp4`, 주요 장면을 디코딩한 `{기능}.{언어}.sheet.png`, 전체 결과를 담은 `receipt.json`(제품 DLL SHA256, 설정, 클립별 크기·프레임·길이·SHA256·사용한 제품 UI 설명)이 생깁니다.
