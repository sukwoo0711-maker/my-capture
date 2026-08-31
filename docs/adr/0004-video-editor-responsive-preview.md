# ADR 0004 — 반응형 동영상 편집 타임라인과 프리뷰 파이프라인

상태: 채택 — Phase 1은 0.9.0에 구현·검증 완료, 네이티브 미디어 엔진은 조건부 실험
기준선: MyCapture 0.7.0 (`main@76d402d`)
검증 릴리스: MyCapture 0.9.0 (2026-08-30 UTC)

> **개정 주석 (1.0.0, 2026-08-31):** MyCapture 1.0.0이 호스트 지원 하한을 Windows 11 21H2(build
> 22000)로 올렸다. 따라서 이 문서에서 Windows 10 1809 / build 17763 / TFM
> `net10.0-windows10.0.19041.0`을 언급하는 부분은 **0.9.0 시점의 기록**이며 현재 계약이 아니다.
> 아래 Flyleaf 승격 조건 2번의 "Windows 10 1809 x64 실장비"와 테스트 행렬의 동일 항목은
> Windows 11 21H2 실장비로 대체된다. 현재 계약의 단일 출처는
> `MyCapture.Core.Platform.WindowsSupportPolicy`이고, 실측은
> `docs/releases/1.0.0-validation.md`에 기록한다.

## 배경

0.7.0은 다음 사용자 계약을 이미 제공한다.

- 상단 전체 overview와 하단 프레임-detail의 2단 타임라인
- overview viewport pan/edge zoom, detail frame snap, shared playhead/trim
- `,` / `.` 프레임 이동, 기존 방향키 이동, `Ctrl+Shift +/-`, Fit All
- 올바른 영상 방향, 짧은 영상 로딩, 최소 760px 창의 고정 2행 도구 모음
- 실제 Media Foundation 영상, 실제 WPF 창, setup/portable 산출물 검증

이 기능은 유지해야 하지만 현재 입력, 그리기, 디코더 seek가 한 UI 이벤트 경로에 결합돼 있다.

1. `TwoLineTimeline.Redraw()`는 drag/wheel/seek마다 세 `Canvas`의 `Children`을 지우고 `Line`, `Rectangle`, `Polygon`, `TextBlock`을 다시 만든다.
2. pointer event coalescing이 없어 같은 composition frame 안의 중간 위치도 모두 그린다.
3. `VideoEditorWindow.OnTimelinePlayhead()`가 모든 playhead 이벤트에서 UI 스레드로 `MediaElement.Pause()`와 `Position`을 즉시 호출한다.
4. `TrimReencoder`는 호출 dispatcher에서 매 프레임 seek, 15ms dispatcher pump, `RenderTargetBitmap`, 새 `byte[]` 할당을 수행한다.

따라서 새 미디어 엔진만 붙여도 timeline visual-tree churn은 남고, timeline control만 바꿔도 decoder stall은 남는다. 문제는 컨트롤 선택보다 **상호작용 의도, 화면 합성, 디코딩 완료를 서로 다른 속도로 처리하지 않는 구조**다.

### 0.7.0 배포 기준선

- portable ZIP: 75,728,813 bytes
- publish: 258 files / 196,678,974 bytes
- 현재 앱 패키지: Microsoft.Extensions DI/logging 3개뿐
- target: `net10.0-windows10.0.19041.0`, runtime support floor Windows 10 1809 x64
- ADR 0002의 기본 정책: 녹화/인코딩은 OS 내장 Media Foundation을 유지하고 불필요한 네이티브 번들을 피한다.

## 0.9.0 Phase 1 구현 결과

최종 구현은 production dependency를 추가하지 않고 다음 경계를 만들었다.

- `TimelineRenderSurface` 3개가 각각 고정 `DrawingVisual` 3개를 보유한다. 전체 visual 수는 항상 9개다.
- `CompositionFrameScheduler`가 dirty 요청을 composition cadence로 합치고 idle 시 정적 이벤트 구독을 해제한다.
- `PreviewSeekCoordinator`는 nullable capacity-one pending slot, 최대 in-flight 1, 45ms preview sampling, generation stale suppression, release `Exact` 우선순위를 제공한다.
- `MediaElementPreviewEngine`은 기존 WPF `MediaElement`를 dispatcher 뒤에 격리한다. pointer call stack에서 직접 `Pause`/`Position`을 호출하지 않는다.
- recorder warm-up 중 Stop이 먼저 들어와도 timestamp-zero frame을 반드시 기록해 `MF_E_SINK_NO_SAMPLES_PROCESSED`를 방지한다.
- `--selftest-video-editor`가 실제 Media Foundation MP4, 실제 760×620 WPF 창, 실제 `MediaElement` seek를 측정하고 PNG/보고서를 남긴다.

최종 packaged portable 실행의 대표 결과는 다음과 같다.

| 항목 | 0.9.0 실측 | 기준 | 결과 |
|---|---:|---:|---|
| pointer intent p95 / p99 | 0.000ms / 0.000ms | ≤16.7ms / ≤33ms | PASS |
| 6,000 intent allocation / Gen2 | 424B / 0 | ≤1,500,000B / 0 | PASS |
| 5초 scrub pointer p95 / p99 | 0.065ms / 0.189ms | ≤16.7ms / ≤33ms | PASS |
| 최대 UI cycle | 46.322ms | ≤50ms | PASS |
| composition draws | 300 / 300 samples | ≤1/sample | PASS |
| decoder seek | in-flight ≤1, pending ≤1 | 각각 ≤1 | PASS |
| exact seek p95 | 24.754ms | ≤250ms | PASS |
| layout | fixed visuals 9, controls 2행, 760px fit | 기존 계약 | PASS |

자동 검증은 Core 246/246 + App 178/178 = 424/424, warning 0 / error 0이다. 방향 회귀 `Encode_PreservesOrientation_NoVerticalFlip_NoHorizontalMirror`도 별도 1/1 통과했다. sampling timing test에서 request-completion 시간을 dispatch 간격으로 잘못 간주한 flake를 발견해 실제 engine dispatch timestamp 비교로 수정했고 독립 프로세스 20/20 및 전체 suite에서 통과했다. Exact가 preview-delay 등록 직전에 들어오는 cancellation race도 같은 수정에서 닫았다.

동일 프로세스 실창 soak는 두 차례 수행했다. 첫 50회는 매 회 실제 MP4까지 새로 녹화하며 546.115초 동안 50/50 PASS했지만 recorder/MFT cache가 memory 수치에 섞여 memory 판정에서는 제외했다. 수정된 probe는 실제 MP4 하나를 재사용해 editor open/scrub/exact/close만 50회, 421.072초 동안 50/50 PASS했다. 후자의 retained managed 증가는 -1,520B, retained private 증가는 3,936,256B, peak working set은 239,443,968B로 20MiB 지속 증가 gate를 통과했다. 두 campaign 합계는 실창 100회와 약 16.1분이며 crash는 0이다.

최종 0.9.0 self-contained offline package는 다음과 같다.

- publish: 258 files / 196,789,566 bytes — file count +0, bytes +110,592
- portable: 75,782,219 bytes — +53,406 bytes
- setup: 74,153,984 bytes — +57,344 bytes
- 추가/삭제 publish file: 0/0; 바뀐 6개는 MyCapture 자체 assembly/host/deps 산출물이다.
- production package graph: 기존 top-level 3개와 transitive 4개 그대로, dependency +0
- installer hostile suite: 13/13 PASS
- portable video-editor, portable recording, 격리 설치본 video-editor: 모두 `RESULT: PASS`
- runtime: self-contained/offline win-x64, minimum build 17763, preinstalled .NET/Internet 불필요

상세 명령·hash·한계는 `docs/releases/0.9.0-validation.md`에 기록한다.

## 결정

### 1. 즉시 채택: WPF 경량 drawing + 독립 interaction state

`Canvas.Children` 기반 shape 재생성을 제거하고, MIT 라이선스의 open-source WPF가 제공하는 `DrawingVisual`/`DrawingContext`로 타임라인을 그린다. SkiaSharp는 도입하지 않는다.

- `TwoLineTimeline`의 외부 동작과 `TimelineViewport`, `TrimSelection`, `FrameStepCalculator`는 유지한다.
- overview, connector, detail은 각각 하나의 `TimelineRenderSurface : FrameworkElement`로 바꾼다.
- 각 surface는 고정된 `DrawingVisual` layer를 보유한다.
  - static: 배경, coarse/frame ticks, labels
  - range: viewport, dimming, connector, trim
  - transient: pointer/playhead/hover/active handle
- pointer hit testing은 shape event가 아니라 현재처럼 시간↔좌표 수학으로 수행한다.
- brush, pen, typeface, 반복 label layout을 cache/freeze한다.
- caption `TextBlock`과 automation name은 그대로 두어 설명·접근성을 잃지 않는다.

Microsoft의 WPF 성능 문서는 `Drawing`이 `Shape`보다 단순하고 성능 특성이 좋으며, `DrawingVisual`이 layout/event handling을 제공하지 않아 경량이라고 명시한다. 이 타임라인은 복잡한 widget tree가 아니라 선·면·텍스트를 그리는 surface이므로 이 계층이 맞다.

### 2. 즉시 채택: composition cadence coalescing

`CompositionFrameScheduler`를 두어 한 composition frame에 최신 상태 한 번만 반영한다.

- mouse move는 `TimelineInteractionState`의 최신 위치만 즉시 갱신한다.
- 이벤트 핸들러 안에서 전체 `Redraw()`나 decoder seek를 실행하지 않는다.
- dirty layer만 다음 `CompositionTarget.Rendering`에서 다시 연다.
- scheduler는 dirty일 때만 `CompositionTarget.Rendering`에 연결하고 idle/unloaded 시 즉시 해제한다. 정적 이벤트 구독 누수를 허용하지 않는다.
- playhead guide는 decoder 결과가 아니라 pointer의 **intent position**을 사용하므로 다음 composition frame에 반응한다.

세 위치를 명시적으로 분리한다.

- `IntentPosition`: 사용자가 현재 가리키는 위치. 타임라인 guide를 즉시 구동한다.
- `RequestedPreviewPosition`: seek coordinator가 디코더에 마지막으로 요청한 위치.
- `PresentedPosition`: 엔진이 실제로 제시했다고 보고한 frame 위치.

drag 중에는 느린 `PresentedPosition`이 빠른 `IntentPosition`을 뒤로 끌어당기지 않는다. release 후 exact seek가 완료되면 셋을 다시 동기화한다.

### 3. 즉시 채택: latest-wins preview seek coordinator

`PreviewSeekCoordinator`를 timeline과 media engine 사이에 둔다. lock으로 보호한 nullable capacity-one pending slot이 one-in-flight + latest-one-pending 의미를 가장 단순하게 제공하므로 `System.Threading.Channels`나 System.Reactive를 추가하지 않는다.

정책은 다음과 같다.

- drag/click input: 타임라인 visual은 즉시 이동한다.
- drag preview: 같은 target frame 요청을 중복하지 않고 40~50ms 간격으로 sampling한다.
- 동시에 실행되는 seek는 최대 1개다. 대기열에는 최신 요청만 남긴다.
- 새 generation이 시작되면 이전 completion은 화면 상태를 갱신하지 못한다.
- mouse release, keyboard single-step, trim commit: pending preview보다 우선하는 `Exact` 요청을 한 번 수행한다.
- 연속 key repeat도 visual은 즉시 움직이고 decode request는 latest-wins로 합친다. key-up 시 exact 요청으로 마무리한다.
- 엔진이 실제 cancellation을 지원하지 않더라도 stale result suppression과 마지막 exact assignment는 보장한다.

초기 production adapter는 현재 `MediaElement`다. 이 adapter는 UI dispatcher에서 호출해야 하고 true frame-accurate/cancelable seek를 보장하지 않는다고 capability에 명시한다. 구조 변경의 효과를 먼저 측정한 뒤 decoder 교체 필요성을 판단한다.

### 4. 엔진 경계: display, frame extraction, export를 분리

하나의 거대한 “video engine” interface를 만들지 않는다.

```text
IVideoPreviewEngine   open/play/pause/fast seek/exact seek/frame-presented signal
IFrameExtractor       frame index 또는 timestamp -> BGRA frame/thumbnail
ITrimExporter         [in, out] -> MP4, progress, cancellation
```

공통 request/result는 UI 타입 없이 정의한다.

```text
PreviewSeekRequest
  Generation
  TargetTime
  TargetFrameIndex?
  Mode = Fast | Exact | Frame

PresentedFrame
  Generation
  RequestedTime
  ActualTime
  ActualFrameIndex?
  IsExact
```

`IVideoPreviewEngine`은 capability를 보고한다.

```text
CancelableSeek
ExactTimestampSeek
ExactFrameSeek
PresentedTimestamp
HardwareDecode
```

이 경계 덕분에 recording/encoding은 계속 Media Foundation을 사용하면서 preview만 비교할 수 있다. 0.7.0 rollback도 adapter 선택 한 곳으로 제한된다.

### 5. 조건부 채택 후보: Flyleaf core 3.11.3

MediaElement adapter가 아래 승격 gate를 통과하지 못할 때만 별도 실험 브랜치에서 Flyleaf를 비교한다. 비교 대상 중 Flyleaf가 유일한 우선 후보다.

- `FlyleafLib` core만 사용한다. `FlyleafLib.Controls.WPF`는 Dragablz, MaterialDesignThemes, WpfColorFontDialog와 완성형 player UI를 추가하므로 사용하지 않는다.
- custom editor UI 위에 Flyleaf의 Direct3D host만 adapter로 감싼다.
- recording과 MP4 encoding은 Media Foundation에 그대로 둔다.
- 첫 실험 범위는 preview seek와 frame extraction뿐이다.

Flyleaf 공식 API/문서는 다음을 제공한다.

- FFmpeg + DirectX hardware accelerated surface와 일반 WPF overlay
- cancellation을 고려한 open/play/pause/stop/seek 구현
- nearest-keyframe `Seek(ms, forward)`
- half-frame-distance 정확도의 `SeekAccurate(ms)`
- frame index를 받는 decoder `GetFrame(frameNumber, backwards)`
- frame stepping과 `SeekCompleted`

이 기능은 상세 편집에 직접 맞지만 배포 비용과 LGPL 준수 비용이 있다. 그러므로 “기능이 많다”가 아니라 **측정된 exact-frame/latency 개선이 비용을 정당화할 때만** 승격한다.

### 6. 현재 기각

- **SkiaSharp.Views.WPF**: 단순 timeline 선·텍스트에 필요 이상의 OpenTK/Skia native stack이다. 현재 4.151.1은 net10 WPF 임시 검증에서 `NU1701` compatibility warning을 냈다. WPF DrawingVisual보다 이 프로젝트에서 얻는 결정적 이점이 없다.
- **LibVLCSharp.WPF**: codec 범위는 넓지만 WPF `VideoView`가 detached window/airspace 우회 구조이고 rotate/skew transform을 지원하지 않는다. native package와 plugin 수가 지나치게 크다. `Time`, `SeekTo`, `NextFrame`은 있지만 Flyleaf처럼 frame-number exact seek 계약을 제공하지 않는다.
- **raw FFmpeg interop/FFmpeg.AutoGen 직접 구현**: demux/decode clock, hardware surface, cancellation, pixel conversion, build/license 공급망을 앱이 직접 소유하게 된다. Flyleaf보다 migration/보안/테스트 비용이 크다.
- **System.Reactive**: capacity-one latest-wins queue 하나를 위해 새 dependency와 새로운 lifetime model을 추가할 이유가 없다.
- **Flyleaf 완성형 WPF control**: 현재 2단 timeline과 고유 도구 모음을 보존하지 못하고 불필요한 UI dependency를 추가한다.

## 후보 비교

아래 크기는 동일한 빈 net10 WPF self-contained win-x64 publish에서 후보 타입을 실제 참조한 뒤 측정한 **상대 증가량**이다. 최종 MyCapture package delta는 실험 브랜치에서 다시 측정해야 한다.

| 후보 | 반응/정확도 적합성 | WPF 통합 | 라이선스 | 실측 publish 증가 | 실측 ZIP 증가 | 판정 |
|---|---|---|---|---:|---:|---|
| WPF DrawingVisual | timeline에 최적 | native, airspace 없음 | MIT | 0 | 0 | **채택** |
| MediaElement + coordinator | fast preview는 보통, exact frame 보장 없음 | 현재 구현 | WPF/.NET | 0 | 0 | **Phase 1 adapter** |
| FlyleafLib 3.11.3 core | exact timestamp/frame API, HW decode | normal overlay/Direct3D | LGPL-3.0-or-later + FFmpeg 조건 | +36,450,453 B / +86 files, FFmpeg 제외 | +11,826,610 B, FFmpeg 제외 | **조건부 spike** |
| LibVLCSharp.WPF 3.10.1 + LibVLC 3.0.23.1 | broad codec, next-frame | detached-window airspace | LGPL-2.1-or-later | +293,085,372 B / +1,264 files | +133,478,996 B | 기각 |
| SkiaSharp.Views.WPF 4.151.1 | timeline에는 과잉 | OpenTK GL WPF | MIT | +107,762,408 B / +7 files | +28,595,412 B | 기각 |

Flyleaf의 공식 v3.11.3 AIO에 포함된 minimal FFmpeg v9 DLL은 7개, 87,599,616 bytes이며 동일 파일만 ZIP으로 압축하면 37,974,737 bytes다. 따라서 core 측정과 합친 portable 증가 추정치는 49,801,347 bytes이고, 0.7.0 portable 기준 예상치는 약 125,530,160 bytes다. 이는 약 66% 증가이므로 자동 채택하지 않는다.

정확한 후보 그래프에 대한 `dotnet list package --vulnerable --include-transitive` 및 `--deprecated` 검사는 알려진 NuGet 항목을 찾지 못했다. 다만 다음은 별도 위험이다.

- Flyleaf stable package가 `SharpGen.Runtime 2.4.2-beta`와 `SharpGen.Runtime.COM 2.4.2-beta`를 transitive dependency로 사용한다.
- NuGet audit는 수동으로 배포하는 FFmpeg DLL과 LibVLC plugin의 전체 native CVE/SBOM을 대신하지 않는다.
- “현재 알려진 advisory 없음”은 향후 안전 또는 법적 적합성을 보증하지 않는다.

## 목표 구조

```text
WPF mouse / keyboard
        |
        v
TimelineInteractionController -----> TimelineViewport / TrimSelection
        |                                      |
        | latest intent                        | pure geometry/state
        v                                      v
CompositionFrameScheduler ------> DrawingVisual layers (60 Hz, dirty only)
        |
        | sampled preview request (capacity 1)
        v
PreviewSeekCoordinator
        |
        +---- MediaElementPreviewEngine (Phase 1 production)
        |
        +---- FlyleafPreviewEngine (isolated spike; promotion by gate)
                    |
                    +---- IFrameExtractor -> thumbnail/edit-current-frame

ITrimExporter
        +---- dedicated STA current TrimReencoder (first isolation step)
        +---- frame-provider + MF Sink Writer (later, if validated)
```

### 권장 파일 경계

```text
MyCapture.Core/Recording/
  TimelineViewport.cs                 유지
  TimelineInteractionState.cs         UI 독립 intent/drag state
  PreviewSeekRequest.cs               generation/mode/target

MyCapture.App/Recording/
  TwoLineTimeline.cs                  외부 계약 유지, orchestration 축소
  TimelineRenderSurface.cs            DrawingVisual host
  TimelineInteractionController.cs    hit test/capture/state transition
  CompositionFrameScheduler.cs        dirty/latest frame scheduling
  PreviewSeekCoordinator.cs           throttle/latest-wins/exact commit
  IVideoPreviewEngine.cs
  MediaElementPreviewEngine.cs
  IFrameExtractor.cs
  TrimExportCoordinator.cs

실험 전용 별도 project/branch:
  MyCapture.Preview.Flyleaf/
    FlyleafPreviewEngine.cs
    FlyleafFrameExtractor.cs
```

Flyleaf project/package reference는 승격 전 production solution과 `Directory.Packages.props`에 넣지 않는다.

## 상세 동작

### pointer drag

1. mouse move가 좌표를 time/frame으로 변환한다.
2. `IntentPosition`과 active range/handle state를 갱신한다.
3. transient/range layer를 dirty로 표시한다.
4. 다음 composition frame이 최신 state 하나를 그린다.
5. target frame이 바뀌고 sampling interval이 지났으면 preview request를 capacity-one queue에 쓴다.
6. decoder가 느려도 1~4는 계속 진행한다.
7. mouse up이 pending preview generation을 종료하고 exact request를 우선 실행한다.

### viewport pan/zoom

- overview static ticks는 유지한다.
- viewport body/edge, dimming, connector는 range layer만 다시 그린다.
- detail의 time transform이 바뀌므로 detail static/range layer를 composition cadence로 다시 그린다.
- wheel burst도 최신 zoom state만 한 frame에 한 번 그린다.

### playback

재생 중에는 engine playback position을 최대 composition cadence로 읽되, label/text 갱신은 10~15Hz로 제한한다. playhead line과 텍스트 layout을 같은 빈도로 묶지 않는다. 사용자가 drag를 시작하면 playback position 추종을 멈추고 intent가 우선한다.

### thumbnail과 frame editing

- thumbnail generation은 별도 low-priority capacity-one/range-generation queue를 사용한다.
- viewport가 바뀌면 아직 시작하지 않은 이전 범위 작업을 버린다.
- UI에는 먼저 placeholder를 그리고 frame 결과는 immutable/frozen bitmap으로 전달한다.
- LRU cache는 frame index + output pixel size + orientation을 key로 하고 pixel budget으로 제한한다.
- 현재 프레임 이미지 편집은 `PresentedFrame`이 exact임을 확인한 뒤 기존 `FrozenFrame`/`AnnotationEditorWindow` 경로로 전달한다.

### trim/export

첫 단계에서 현재 `TrimReencoder`를 editor dispatcher에서 제거한다.

- 전용 background STA thread와 자체 dispatcher에서 기존 WPF `MediaPlayer` loop를 실행한다.
- UI에는 progress와 cancellation을 전달한다.
- frame boundary에서 cancellation을 확인한다.
- 이 변경만으로도 export 중 editor stall을 제거하지만 frame extraction 방식 자체의 정확도/할당 문제를 해결했다고 간주하지 않는다.

Flyleaf가 승격되면 `IFrameExtractor`의 exact frame을 기존 Media Foundation `IVideoEncoder`로 보낸다. FFmpeg encoder나 libx264를 추가하지 않는다. Flyleaf가 승격되지 않으면 Media Foundation Source Reader 기반 extractor를 별도 비교할 수 있지만, 기존 COM interop 회귀 위험 때문에 동일한 contract/device test를 통과해야 한다.

## 0.7.0 호환 불변식

리팩터링과 모든 adapter는 다음을 깨뜨리면 안 된다.

- overview/detail 2단 구조와 현재 높이·coarse/detail hierarchy
- 초기 detail 범위가 한 coarse interval인 동작
- overview 외부 seek 시 detail range follow
- trim/frame snap과 frame caption
- `,`, `.`, 방향키, `Ctrl+Shift +/-`, Fit All, In/Out/Edit/Save
- 최소 760px에서 스크롤바 없는 2행 control fit
- 2초 이하 짧은 clip의 open/first frame
- 회전 metadata와 수직 flip/수평 mirror 없음
- Media Foundation recording/encoding 및 offline operation
- setup과 portable에서 동일 동작

## 측정과 승격 gate

성능 수치는 Release, 실제 x64 창, warm/cold를 구분해 기록한다. 평균만 보고 결정하지 않는다.

### Phase 1 성공 기준

| 항목 | 기준 |
|---|---|
| pointer input → visual playhead | p95 ≤ 16.7ms, p99 ≤ 33ms |
| 5초 연속 drag의 최대 visual 정지 | ≤ 50ms |
| render coalescing | composition frame당 draw ≤ 1 |
| decoder concurrency | in-flight seek ≤ 1, pending preview ≤ 1 |
| allocation | 현 구현 대비 10초 drag managed allocation 90% 이상 감소, Gen2 GC 0 |
| 기능 | 기존 415 tests + 새 coordinator/render tests 통과 |
| package | production dependency +0, publish file count +0, byte delta 기록 |

### preview engine 비교 기준

동일 clip/동일 target sequence로 MediaElement와 Flyleaf를 비교한다.

| 항목 | 1080p H.264 목표 |
|---|---|
| first frame, 2초 clip | cold p95 ≤ 500ms |
| sampled preview seek | p50 ≤ 60ms, p95 ≤ 150ms |
| mouse-up exact seek | p95 ≤ 250ms |
| frame correctness | 자체 CBR recording 100개 target에서 오차 ≤ 0.5 frame |
| stale result | release exact 뒤 이전 preview가 표시되는 경우 0 |
| orientation | 0°, 90°, 180°, 270° 및 기존 no-flip pixel test 통과 |
| stability | 10분 반복 scrub/open/close 후 crash 0, 지속 memory growth ≤ 20MB |

Flyleaf 승격에는 위 기준 외에 모두 필요하다.

1. MediaElement 대비 exact-frame correctness를 실질적으로 개선하고, seek p95를 30% 이상 개선하거나 MediaElement가 gate를 실패한다.
2. Windows 10 1809 x64 실장비와 Windows 11에서 hardware/software decode가 모두 통과한다.
3. 최종 portable ≤ 130MB, publish file count ≤ 400이라는 초기 배포 상한을 통과하거나 상한 변경을 별도 승인받는다.
4. restore/build warning 0. `SharpGen.Runtime` beta transitive dependency를 명시적으로 수용하거나 제거 가능한 버전을 확인한다.
5. exact FFmpeg build의 source commit, configure flags, DLL hashes, SBOM, CVE scan을 보관한다.
6. LGPL/FFmpeg notices와 교체 가능한 DLL 배포 방식에 대한 법률 검토를 완료한다.

## 라이선스·공급망 조건

이 문서는 법률 자문이 아니다. 상업 배포 전 별도 검토가 필요하다.

### WPF

- dotnet/wpf: MIT
- 새 binary, notice, source-offer 부담 없음

### Flyleaf

- `FlyleafLib`: LGPL-3.0-or-later
- DLL을 별도 파일로 동적 배포하고 사용자가 호환 수정본으로 교체할 수 있어야 한다.
- library 사용 고지와 GPL/LGPL license 사본을 포함한다.
- library 수정 디버깅 목적의 reverse engineering을 EULA가 금지하지 않도록 검토한다.
- 수정한 library가 있으면 해당 source/변경 사항 제공 의무를 따른다.

### FFmpeg

FFmpeg 공식 체크리스트에 따라 최소한 다음을 확인한다.

- `--enable-gpl`, `--enable-nonfree` 비활성
- Windows DLL 동적 연결
- 배포 binary와 정확히 일치하는 source와 configure/build 설명 제공
- download page, About, EULA/third-party notices에 사용·license·source link 명시
- libx264 등 GPL external library 미포함
- codec patent 문제는 open-source license와 별개로 배포 지역별 검토

prebuilt AIO의 “minimal/no encoders” 설명만 믿지 않고 실제 build config와 binary inventory를 확인한다.

## 단계별 실행

### Phase 0 — 계측과 기준선

- input timestamp, composition render timestamp, seek issue/completion, presented timestamp를 기록한다.
- 2초, 12초, 1080p/4K, 15/30fps, orientation clip corpus와 100-target seek sequence를 고정한다.
- 현재 Canvas/MediaElement 수치를 먼저 저장한다.

### Phase 1 — zero-dependency interaction refactor (0.9.0 완료)

1. Canvas shape tree를 `TimelineRenderSurface`의 고정 DrawingVisual layers로 교체했다.
2. `CompositionFrameScheduler`를 연결하고 layer별 dirty invalidation/lifetime test를 추가했다.
3. timeline event에서 직접 MediaElement를 호출하지 않고 nullable latest-wins `PreviewSeekCoordinator`로 보낸다.
4. 기존 MediaElement를 `MediaElementPreviewEngine` adapter로 감쌌다.
5. pure concurrency, STA rendering, 실제 MP4/실창, recorder warm-up 회귀 테스트를 추가했다.
6. 0.7.0 real-window/device/package 회귀와 50회 same-process soak를 재실행했다.

새 package는 없으며 기존 timeline public/internal interaction API를 유지했다. 별도 `TimelineInteractionState`/controller 분리는 현재 수학 기반 hit-test가 renderer와 이미 독립적이어서 회귀 위험 대비 이점이 없는 것으로 판단해 보류했다.

### Phase 2 — precision engine spike

- production dependency를 건드리지 않는 별도 project/branch에서 `FlyleafLib 3.11.3`을 정확히 pin한다.
- Flyleaf WPF control 대신 core host adapter만 만든다.
- 동일 contract test와 benchmark를 MediaElement adapter와 실행한다.
- license/SBOM/package 검증을 함께 완료한다.
- gate를 통과할 때만 ADR 상태를 “채택”으로 갱신하고 production package를 추가한다.

### Phase 3 — continuous editing flow

- viewport-aware background thumbnail strip와 bounded LRU cache
- exact presented frame 기반 image editing
- trim/export 전용 STA worker, progress, cancellation
- 필요 시 validated extractor + Media Foundation encoder pipeline
- optional kinetic pan은 기본 drag/keyboard와 충돌하지 않고 reduced-motion/설정으로 비활성화 가능하게 추가

## 테스트 전략

### pure/unit

- N개의 preview request가 들어와도 latest 하나만 남는다.
- in-flight engine call은 항상 1개다.
- stale generation completion이 intent/presented state를 바꾸지 못한다.
- release exact request는 drop되지 않고 preview보다 우선한다.
- duplicate frame request가 제거된다.
- overview/detail/trim hit-test 결과가 기존 경계값과 같다.
- dirty layer 분류가 playhead, trim, viewport, resize별로 맞다.

### STA/real window

- 실제 760px 창에서 기존 2행 도구 폭과 timeline 높이 유지
- 5초 자동 drag 중 UI heartbeat와 composition latency 수집
- 2초 clip open/first frame/seek
- 12초 clip 초기 0–1초 detail과 overview follow
- unload 후 `CompositionTarget.Rendering` subscriber와 media engine 해제

### real media/device/package

- 기존 `Encode_PreservesOrientation_NoVerticalFlip_NoHorizontalMirror`
- 15/30fps frame-number target pixel comparison
- Intel iGPU hardware decode, software fallback, Windows 10 1809 x64
- setup과 portable offline install/open/scrub/export
- published self-test와 package version/hash/file-count 검증

## 결과와 trade-off

장점:

- 사용자가 느끼는 guide 반응은 decoder latency와 분리돼 즉시성이 생긴다.
- shape allocation/layout churn과 불필요한 seek를 동시에 제거한다.
- 0.7.0 interaction/keyboard/layout 계약을 유지한다.
- MediaElement, Flyleaf, 향후 MF extractor를 같은 측정으로 비교·롤백할 수 있다.
- 네이티브 dependency는 성능 증거와 법적 준비가 있을 때만 들어온다.

비용:

- custom DrawingVisual host와 scheduler lifetime test가 필요하다.
- intent와 presented 위치를 구분하므로 state model이 현재보다 명시적으로 복잡해진다.
- MediaElement를 유지하는 동안 visual responsiveness는 개선돼도 exact decoded frame은 제한될 수 있다.
- Flyleaf 승격 시 portable 약 50MB 증가 추정과 LGPL/FFmpeg 공급망 유지 비용을 수용해야 한다.

## 롤백

- Phase 1은 renderer와 coordinator를 interface 뒤에 두고 기존 public/internal timeline API를 유지한다.
- 이중 runtime path는 장기적으로 미검증 상태를 늘리므로 남기지 않았다. 문제가 생기면 `MediaElementPreviewEngine` 경계와 renderer 파일 단위로 source rollback한다.
- Flyleaf spike는 승격 전 production project에 참조하지 않는다.
- 승격 후에도 `MediaElementPreviewEngine`을 한 release 동안 fallback으로 유지한다.
- recording과 Media Foundation encoder는 이 결정으로 변경하지 않으므로 preview rollback이 녹화 파일 형식에 영향을 주지 않는다.

## 공식 근거

- WPF 2D graphics 성능: <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-2d-graphics-and-imaging>
- dotnet/wpf 저장소 및 MIT license: <https://github.com/dotnet/wpf>
- Flyleaf 공식 저장소: <https://github.com/SuRGeoNix/Flyleaf>
- Flyleaf NuGet 3.11.3: <https://www.nuget.org/packages/FlyleafLib/3.11.3>
- Flyleaf LGPL-3.0 license: <https://github.com/SuRGeoNix/Flyleaf/blob/master/LICENSE.txt>
- LibVLCSharp: <https://github.com/videolan/libvlcsharp>
- LibVLCSharp WPF airspace/transform 제한: <https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.WPF/README.md>
- VideoLAN.LibVLC.Windows 3.0.23.1: <https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1>
- SkiaSharp: <https://github.com/mono/SkiaSharp>
- SkiaSharp.Views.WPF 4.151.1: <https://www.nuget.org/packages/SkiaSharp.Views.WPF/4.151.1>
- FFmpeg license/compliance checklist: <https://ffmpeg.org/legal.html>
