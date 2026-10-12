# 기능별 렌더러 계약

공통 소스는 `TutorialStoryboard`, `ITutorialSceneRenderer`, `TutorialDrawing`, `TutorialGifPipeline`입니다. 새 기능별 작성자는 자기 `*SceneRenderer.cs` 파일만 소유하고 공통 파일을 수정하지 않습니다. 호출 등록과 입력 CLI는 통합 담당자가 연결합니다.

```csharp
internal sealed class ExampleSceneRenderer : ITutorialSceneRenderer
{
    public string Id => "feature-id";
    public TutorialStoryboard Storyboard { get; } = new TutorialStoryboard()
        .Hold("ready", 100, new PointD(60, 210))
        .Motion("approach", 35, new PointD(60, 210), new PointD(210, 120))
        .Motion("press", 8, new PointD(210, 120), new PointD(210, 120), pressed: true)
        .Hold("result", 200, new PointD(210, 120));
    public IReadOnlyList<int> KeyFrames => [0, 35, 43];
    public BitmapSource Render(TutorialFramePlan frame)
    {
        BitmapSource scene = TutorialDrawing.DrawScene(dc => { /* fixture/product UI */ });
        return TutorialDrawing.Compose(scene, "단계 문구", key: "",
            demoCursor: frame.Cursor, clickAt: frame.Cursor,
            clickProgress: frame.Pressed ? frame.Progress : 0);
    }
    public void Dispose() { /* product renderer ReleaseResources; stop owned timers */ }
}
```

예제는 signature 설명이며 완전한 길이의 클립이 아닙니다. 실제 storyboard는 10–12초, 전체 ≤160프레임, 동작 구간 25fps(4cs), 읽기 hold는 단일 프레임의 긴 지연입니다. `Motion`은 cubic smoothstep 가감속을 적용하며 `Progress`는 0..1 원래 진행률, `Cursor`는 가감속이 적용된 논리 좌표입니다. 버튼/도구/입력 상태는 해당 단계에 즉시 바꾸고, 포인터·클릭 피드백·장면 표현을 부드럽게 그립니다.

`Render`는 생성기 STA에서 storyboard 순서대로 프레임마다 한 번 호출됩니다. 정적 scene·현재 제품 UI 상태만 보관하고 출력 비트맵 목록을 누적하지 않습니다. 반환은 전체 불투명 640×360입니다. 고정 키는 제품이 실제 지원하는 키만 표시하며 사용자 변경 가능한 전역 키는 자산 밖 UI에서 안내합니다. 단일 기능의 source/selected/result 내용은 같은 샘플 정의를 사용합니다.

그리기 함수:

- `TutorialDrawing.DrawScene(Action<DrawingContext>)`: 논리 640×312 콘텐츠를 864×421 backing bitmap에 렌더링합니다.
- `TutorialDrawing.DrawFrame(Action<DrawingContext>)`: 640×360 출력입니다.
- `TutorialDrawing.Compose(BitmapSource scene, string caption, string key = "", PointD? demoCursor = null, PointD clickAt = default, double clickProgress = 0)`: scene 축소, 48px 캡션, 키 표시, 선택적 화살표/클릭 링입니다.
- `TutorialDrawing.Blend(from, to, progress)`: smoothstep 불투명 장면 전환입니다.
- `TutorialDrawing.Text(dc, value, x, y, size, brush, mono = false)`와 `Brush(resourceKey)`: 제품 폰트·테마 브러시를 사용합니다. 공유 브러시를 제자리 수정하지 않습니다.

```csharp
using var renderer = new ExampleSceneRenderer();
TutorialGifPipeline.Generate(renderer, newGifPath);
object receipt = TutorialGifPipeline.Validate(newGifPath, language, renderer.Storyboard, out GifSampleFrames decoded);
```

`Generate`는 프레임 하나씩 렌더→Indexed8 nearest palette→제품 GIF 인코더→제품 writer로 처리합니다. 이미 존재하는 출력 파일은 `CreateNew`로 거부합니다. `Validate`는 최종 GIF를 제품 decoder/player로 읽고 전체 크기/불투명/disposal1/무한반복/지연/길이/≤3MiB/디코딩≤40MiB를 확인합니다. 모든 motion segment의 디코딩 프레임 변화와 최대 포인터 간격(≤25px)을 확인하므로 가만히 있는 장면을 4cs 복제로 채우지 않습니다.

생성 호스트는 QA13 payload를 로드하고 `Tokens`, `Symbols`, `Controls`와 Midnight 제품 테마를 준비하며, 각 renderer 생성 전 `UiText.UseLanguage("ko"/"en")`를 사용합니다. fixture runtime은 .NET Core/WindowsDesktop10.0.11 두 개를 고정합니다. friend assembly `MyCapture.App.Tests`를 사용합니다.

작업자 금지 범위: 실제 native input, 화면 획득, 사용자 프로필/클립보드/persistence, App.Run/Window.Show, 원격 변경, 파일 삭제. 모든 시도와 작업 사본을 보존합니다. 제품 지원 흐름과 합성 fixture를 개발 provenance에 구분하며 제품 GIF 안에는 개발 면책 문구를 넣지 않습니다.
