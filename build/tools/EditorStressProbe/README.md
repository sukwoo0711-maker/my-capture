# 이미지 편집 최악 조건 재현

최종 publish의 실제 편집 control, surface, controller, renderer, draft session/store를 읽어 사용하는 독립 WPF 진단 프로세스입니다. 실제 화면 캡처, 클립보드, 앱 초기화 및 시작 프로그램 등록을 실행하지 않습니다. 텍스트 입력 성능은 이 probe의 범위가 아닙니다.

저장소 root에서 publish 절대 경로를 지정합니다. baseline과 after는 같은 self-contained/ReadyToRun publish 설정으로 준비합니다. 기존 publish와 실행 중인 사용자 앱을 수정하지 않습니다.

```powershell
dotnet build build/tools/EditorStressProbe/EditorStress.csproj -c Release -p:AppPayload=C:/absolute/publish -o artifacts/stress-editor/harness-build -nologo -v quiet
& ./build/tools/EditorStressProbe/run-editor-stress.ps1 -Payload C:/absolute/publish -Width 3840 -Items 1000 -StrokePoints 5000 -Label baseline
```

Width는 3840 또는 7680, Items는 1000 또는 10000입니다. 20개의 ink stroke는 각각 StrokePoints개 point를 가지며 나머지는 rectangle입니다. 실제 rectangle 추가 10회, undo/redo, 선택 이동, 1280×720 viewport 렌더링, 150% fit 크기, 첫 PNG encode/초안 쓰기 중 동시 편집을 수행합니다. 주석 수·기존 ID·ink point 수와 실제 복구 문서 완전성을 확인합니다. 초기 viewport PNG/pixel SHA 및 원본/최신 문서를 보존합니다.

압축 부하 진단은 `-PixelPattern entropy`로 같은 고정 xorshift32 seed의 불투명 RGB 원본을 만듭니다. baseline/after에서 같은 Width/Items/StrokePoints/PixelPattern을 지정합니다. 최종 복구 PNG를 실제 blob resolver로 열어 Bgra32로 디코딩하고 원본 픽셀 SHA/크기와 대조합니다. 이 검증은 heartbeat 종료 후 수행하며 guard의 전체 시간·메모리에는 포함됩니다. 기본 solid는 기존 단색 fixture와 같습니다.

`-EncoderMode independent`는 원인 분리를 위한 fixture 경계입니다. 이 owned synthetic bitmap의 실제 제품 PNG cache entry만 독립 인코더 Lazy로 바꿉니다. 실제 제품 background STA가 Lazy를 평가할 때 원본 CopyPixels 후 같은 DPI/format/palette로 독립 BitmapSource를 만들고 Freeze하여 PNG를 인코딩합니다. 기존 BitmapFrame의 metadata/thumbnail/ColorContexts도 전달합니다. UI thread에서 호출되면 실패하며 제품 source는 변경하지 않습니다. 기본 product mode는 실제 제품 인코더를 그대로 사용합니다. 같은 payload에서 두 mode의 pixel/PNG SHA, heartbeat 및 추가 메모리를 비교합니다.

runner는 고유 output/receipt/stdout/stderr/runtimeconfig를 생성합니다. 별도 config에 Core/Desktop 10.0.11 및 rollForward Disable을 명시하며 설치된 해당 runtime이 없으면 실행 실패합니다. 제품 config와 파일은 변경하지 않습니다. 실제 runtime/WPF 버전, 제품 DLL hash/version, 각 phase 시간·전체 process allocation 증가량, 16ms Dispatcher heartbeat max/p95, blob 수/크기, private memory를 기록합니다. allocation은 동일 process의 동시 background 작업을 포함할 수 있습니다. heartbeat는 제품 작업에 더해 소량의 phase 근거 쓰기를 포함하며 초기 PNG/hash 저장은 제외합니다.

각 owned process는 시작 후 60초 또는 private memory 2GB 도달 시 partial 근거를 남기고 자체 exit124로 종료합니다. guard를 성능 성공 기준으로 사용하지 않습니다. 출력 디렉터리가 이미 있으면 실행을 중단합니다. 모든 생성물은 보존하며 cleanup 또는 삭제를 수행하지 않습니다.

runner는 시작 전에 hash를 수집하고 Start 이후 try/finally로 정확한 Process 객체를 보존합니다. 외부 대기60초/종료확인5초/stdout·stderr수집 각5초 한도가 있으며 timeout 또는 근거쓰기 예외에서는 그 owned process만 종료하고 확인합니다. 종료/출력 확인 실패는 별도 stopReason으로 기록하고 실패 코드로 반환합니다. 사용자 앱을 이름으로 조회하거나 종료하지 않습니다.

Detached surface 렌더링 및 Show 없는 실제 Window 생성/Close를 사용합니다. HWND presentation, 실제 zoom UI, 비압축 원본, 사용자 입력 주입은 포함하지 않습니다. synthetic 단색 이미지는 실제 큰 사진의 PNG 부하를 대표하지 않습니다. 반복 Window weakref는 timed 작업 밖의 forced GC 이후 관측값이며 단독으로 누수를 판정하지 않습니다. CPU/GUI 측정은 다른 성능 검사를 동시에 실행하지 않는 슬롯에서 수행합니다.
