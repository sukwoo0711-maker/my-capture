# 텍스트 복사 튜토리얼 GIF 생성기

`CaptureOverlayView`와 `CaptureModeBar`, 제품 Midnight 테마를 오프스크린으로 렌더링합니다. 합성 터미널과 메모에 `npm run build` / `Build completed` 두 줄을 표시합니다. `T` 키 안내 뒤 텍스트 모드, 드래그 선택, 자동 인식·복사 알림, `Ctrl+V` 붙여넣기 결과를 보여 줍니다. 캡처 시작 단축키는 변경할 수 있으므로 GIF에 넣지 않습니다. 현재 설정 값은 튜토리얼 UI에서 별도로 표시합니다.

제품 렌더러는 864×421 선택 화면을 그린 뒤 640×312로 축소합니다. 합성 샘플 본문은 최종 GIF에서 약 22px이며 아래 48px 영역에 단계 문구와 고정 키를 표시합니다. 640×360, 11프레임, 11.2초, 무한 반복, 전체 불투명 프레임으로 기록합니다. 한·영 자산은 같은 샘플, 같은 선택 경로, 같은 지연을 사용합니다. 홀드는 GIF 지연으로 표현합니다.

색상은 프레임별 정확한 RGB 빈도 상위 256색 팔레트와 가장 가까운 RGB로 Indexed8 변환합니다. 디더링 없이 정적 배경을 유지한 뒤 제품 `AnimatedGifWriter`의 `AddEncodedFrame` 경로와 WPF GIF 인코더를 사용합니다. 검증·미리보기는 최종 GIF를 제품 `GifSampleDecoder`로 다시 읽습니다.

이 자료는 사용법을 설명하기 위한 합성 fixture입니다. OS 화면 녹화, 실제 OCR 수행 또는 실제 클립보드 트랜잭션의 증거가 아닙니다. `App.Run`/`Window.Show`, 사용자 프로필, 화면 획득, 실제 입력, 클립보드를 사용하지 않습니다. OCR 성공과 붙여넣기는 제품의 실제 지원 흐름을 설명하는 연출입니다. 알림은 `ThemedShellPresenter.ShowActionNotification`과 같은 제품 문자열, 여백, 기본/강조 버튼 스타일로 그리며 선택적인 고급 인식 버튼을 누르지 않습니다.

`T`의 오프스크린 전환은 제품 `SelectMode(Text)` 호출을 사용합니다. 드래그는 제품 `OnMouseMove`와 합성 포인터/앵커를 사용합니다. 제품의 `CountCharacters`는 공백과 줄바꿈을 제외한 문자 수를 반환하므로 길이 29인 샘플의 제품 알림은 25자입니다. `Sample` 상수와 `CountCharacters(Sample)`를 함께 사용합니다.

기준: `a8ef489`, 제품 `33f6652`, QA12 published payload. friend assembly `MyCapture.App.Tests` 이름은 기존 내부 렌더러와 GIF API 접근에만 사용합니다.

PowerShell 작업 위치 `C:/GitRepositories/my-capture-edit-recovery`:

```powershell
dotnet build build/tools/TutorialGifGenerator/TutorialGifGenerator.csproj -c Release -v q -nologo -p:AppPayload=C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load
dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-4
```

`fixture.runtimeconfig.json`은 설치된 .NET Core/WindowsDesktop 10.0.11 양쪽과 `rollForward: Disable`을 지정합니다. `RuntimeFrameworkVersion` MSBuild 속성은 Windows SDK 참조 버전과 충돌하므로 사용하지 않습니다. QA12 자체는 self-contained 설정이므로 실행 호스트 설정으로 사용하지 않습니다.

출력 폴더가 이미 있으면 실행을 거부합니다. 모든 시도, 출력, 작업 사본을 보존하며 파일 정리·삭제를 수행하지 않습니다. 승인된 `review/`에는 실제 최종 GIF에서 디코딩한 키프레임 6장과 컨택트 시트, SHA256·프레임·지연·메모리·런타임 영수증을 보관합니다. 제품에는 `Assets/Tutorials/text-copy.ko.gif`와 `text-copy.en.gif`만 포함합니다.

검증 항목: 640×360, ≤30프레임, 10–12초, ≤1MiB, 정확한 지연, 무한 반복 NETSCAPE 메타데이터, 전체 크기/오프셋 0, disposal 1, 모든 알파 255, ≤30MiB 디코딩 메모리, 서로 다른 단계, 제품 타임라인의 모든 경계 및 반복, 제품 플레이어의 모든 프레임 탐색·재생·정지. 제품 전체 OCR·UI·실기 검증은 통합 작업에서 수행합니다.
