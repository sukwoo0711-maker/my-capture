# 기능별 사용법 GIF 생성기

38개 기능을 한국어와 영어로 각각 제작합니다. 각 자산은 640×360, 10–12초이며 움직임 구간은 4cs(25fps), 읽기 구간은 단일 프레임의 긴 지연입니다. 포인터는 smoothstep으로 가감속하고 클릭·드래그·키 입력·기능 결과를 별도로 표시합니다. 전역 단축키는 사용자 설정에 따라 달라지므로 자산에 넣지 않으며 제품의 튜토리얼 창에서 현재 값을 표시합니다. `T`, `C`, `Ctrl+V`, 편집기의 고정 키 등 해당 화면의 제품 고정 입력만 GIF에 표시합니다.

`ITutorialSceneRenderer`는 기능별 장면과 `TutorialStoryboard`의 좌표·진행률·지연 계획을 제공합니다. 공통 signature와 작성 경계는 [RENDERER-API.md](RENDERER-API.md)에 있습니다. renderer가 반환한 프레임을 즉시 quantize/encode하며 프레임 비트맵 목록을 미리 쌓지 않습니다. 완성된 GIF 하나만 제품 `GifSampleDecoder`로 읽고 검증·PNG 생성 후 해제한 다음 자산으로 넘어갑니다.

기능 목록:

| Renderer | 기능 ID |
| --- | --- |
| TextCopy | `text-copy` |
| Capture | `capture-region`, `record-region`, `record-pause`, `capture-window`, `capture-fullscreen`, `capture-scroll`, `capture-delay`, `color-copy` |
| Workspace | `library-open`, `quick-access`, `folder-create`, `folder-rename`, `library-move`, `library-delete`, `pin-clipboard`, `pin-hide-all`, `pin-click-through`, `pin-restore`, `pin-copy`, `pin-save` |
| Editor | `image-rectangle`, `image-ellipse`, `image-arrow`, `image-pen`, `image-highlighter`, `image-text`, `image-step-number`, `image-mosaic`, `image-blur`, `image-crop`, `image-rotate`, `image-save`, `image-copy` |
| Video | `video-play`, `video-frame-step`, `video-trim`, `video-gif-export` |

제품 Midnight 테마, 실제 `CaptureOverlayView`/선택 표시, `CountdownWindow.Content`, `PinWindow.Content`/`PinHoverBar.Content`, `QuickAccessWindow.Content`, `GalleryFolderEditDialog.Content`, `AnnotationRenderer`, `TwoLineTimeline`, 제품 프레임 이동 계산과 GIF encoder/decoder/player를 사용합니다. 주변 데스크톱·터미널·메모·축약 보관함·녹화 제어 바·샘플 영상과 거래 결과는 안전한 합성 fixture입니다. 각 receipt에 사용한 실제 제품 UI와 합성 부분을 기록합니다. `Window.Show`/`App.Run`, 화면 획득, 실제 입력/클립보드/사용자 프로필/영속화/실제 파일 삭제를 수행하지 않습니다.

텍스트 샘플은 `npm run build\nBuild completed`입니다. 실제 제품 문자수 함수는 공백과 줄바꿈을 제외하여 25자를 반환합니다. 선택 박스는 두 번째 줄의 실제 글자 높이와 4px 여백을 포함합니다. 녹화 선택은 제품의 plain recording instruction을 사용하며 시작/중지 버튼의 클릭 구간이 끝난 뒤 다음 상태로 전환합니다. 모자이크는 제품 `ApplyMosaic`와 같은 14px 블록 평균/블록 복제 계산을 사용합니다. 회전 결과는 회전각의 외곽 크기를 기준으로 viewport에 균일 비율로 맞춥니다.

프레임별 RGB 빈도 상위 256색과 가장 가까운 RGB로 디더링 없이 전체 불투명 Indexed8 프레임을 만들고 제품 `AnimatedGifWriter.AddEncodedFrame`과 WPF GIF encoder로 저장합니다. 실제 최종 디코딩 검사: 정확한 크기·지연·무한 반복 NETSCAPE 메타데이터·전체 프레임/오프셋0·disposal1·모든 알파255·≤160프레임·≤3MiB 자산·≤40MiB decoded Indexed8 payload·모션 구간의 실제 프레임 변화 및 ≤25px 최대 포인터 간격·제품 타임라인 경계/반복·제품 플레이어 탐색/재생/정지. preflight는 프레임 수/총시간/좌표 간격/키프레임 인덱스를 먼저 확인하며 실제 decode 검사를 대신하지 않습니다.

기준은 `2bb6cd6`/제품 `b4ae87e`/QA13 payload입니다. `MyCapture.App.Tests` assembly 이름은 기존 friend 접근 계약에 사용합니다. 작업 위치는 `C:/GitRepositories/my-capture-edit-recovery`입니다.

```powershell
dotnet build build/tools/TutorialGifGenerator/TutorialGifGenerator.csproj -c Release -v q -nologo -p:AppPayload=C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial

# 전체 계획 검사. 출력 폴더는 매번 새 경로를 사용합니다.
dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-preflight-new preflight:all

# 전체 76개 생성. 마지막 인수를 생략하거나 all로 지정합니다.
dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-all-new all

# 지정 기능만 두 언어로 생성합니다.
dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-subset-new record-region,image-mosaic,video-trim
```

위 `new` 명령은 재현용 예시이며 실행 기록은 [smooth-development-log.md](smooth-development-log.md)에 있습니다. CLI는 `payload output [all|ID,ID|preflight:all|preflight:ID,ID]`를 받습니다. 이미 존재하는 출력 폴더는 모든 시도를 보존하기 위해 거부합니다. `fixture.runtimeconfig.json`은 설치된 .NET Core/WindowsDesktop 10.0.11 양쪽과 `rollForward: Disable`을 지정합니다. self-contained payload의 runtimeconfig나 전역 `RuntimeFrameworkVersion`은 실행 설정으로 사용하지 않습니다.

출력에는 자산별 storyboard, SHA256/전체 frameSHA/지연/동작/메모리/런타임/timing receipt, 실제 GIF의 decoded keyframe PNG와 contact/motion sheet가 있습니다. 실패 시 가능한 raw decode metrics와 failure receipt를 남깁니다. 기존 11frame AS-IS 및 모든 실패/완료 attempt와 작업 사본을 보존합니다. 제품에는 `Assets/Tutorials/*.gif`를 포함하고 개발 자료는 generator의 LOG/review에 보관합니다. 최종 selected-source manifest는 76개 원본 경로·receipt·SHA·bytes·frames·duration·decoded bytes와 제품에 복사한 hash를 연결합니다.
