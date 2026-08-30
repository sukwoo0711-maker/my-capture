# ADR 0002 — 영역 동영상 녹화 및 편집 (v0.5.0)

상태: 채택
관련 커밋 기준: feature/0.5.0-region-recording

## 배경

캡처(정지 이미지) 파이프라인 위에 **영역 동영상 녹화**와 **동영상 편집기**를 얹는다.
요구사항은 다음과 같다.

- `Ctrl+Shift+X`로 시작. 캡처와 동일한 영역 선택 UX를 재사용한다.
- 선택된 녹화 영역 창은 **드래그로 이동** 가능해야 한다.
- 재생(녹화 시작) 버튼. **시작 지연(delay)** 여부는 UX에서 선택한다.
- 녹화 완료 시 **편집기**가 뜬다.
  - 구간 선택으로 **길이(트림) 조정**.
  - **프레임 이동 모드**: 좌/우 방향키로 1프레임씩 이동.
  - 프레임 이동 모드가 아니면 좌/우 방향키가 **일반 편집 앱 수준의 구간(스텝)**을 이동.
  - 편집 중 특정 프레임에서 시작하는 **이미지 편집** 가능(기존 캡처 주석 편집기 재사용).
- **오프라인 PC**에서 동작(인터넷 불가). **성능이 낮은 PC**에서도 빠르게.
- 0.4.0 디자인과 동등하거나 우위인 시각 품질.

## 인코더 선택

| 후보 | 오프라인 | 네이티브 의존 | 저사양 성능 | 판정 |
|---|---|---|---|---|
| FFmpeg 번들 | O | 외부 exe 번들 필요(수십 MB), ShareX식 설치 안내 | 우수 | 기각(설치 표면·용량) |
| `System.Drawing` + GIF | O | GDI+ | 큰 파일·저품질 | 기각(품질) |
| **Media Foundation Sink Writer** | **O(윈도우 내장)** | **P/Invoke만(패키지 0개)** | **HW 인코더 우선, SW MFT 폴백** | **채택** |

Media Foundation(`mfplat.dll`, `mfreadwrite.dll`)은 Windows에 내장되어 있어 인터넷 없이 동작하고,
프로젝트의 "네이티브 패키지 의존 0" 원칙(ADR 0001)과 일치한다. H.264 인코딩은 GPU가 지원되면
하드웨어 MFT로, 아니면 소프트웨어 MFT로 자동 폴백하므로 저사양 PC에서도 CPU 부담이 낮다.
결과물은 표준 MP4라 어디서나 재생된다.

`Directory.Packages.props`에 새 패키지를 추가하지 않는다. 기존 `NativeMethods` P/Invoke 방식과 동일하게
MF 인터페이스를 얇게 감싼다.

## 성능 설계 (저사양 우선)

- **프레임 그랩**: 기존 `ScreenCaptureEngine`의 GDI `BitBlt + CAPTUREBLT` 경로를 재사용한다.
  이미 4K 한 프레임을 수십 ms에 잡는 것이 실측되어 있어 별도 D3D 복제 세션이 필요 없다.
- **캡처 스레드 분리**: 녹화 루프는 UI 스레드가 아닌 전용 백그라운드 스레드에서 돈다.
  `RecordingClock`이 목표 FPS(기본 15, 설정 가능 10/15/24/30)에 맞춰 프레임 페이싱을 하고,
  인코더가 밀리면 프레임을 드롭해 UI/전체 시스템 반응성을 지킨다(적응형 프레임 드롭).
- **메모리**: 프레임은 즉시 인코더로 흘려보내고 원본 비트맵을 보관하지 않는다.
  편집기는 완성된 MP4를 `MediaElement`로 seek 재생하므로 전체 프레임을 램에 펼치지 않는다.
- **기본 FPS 15**: 소프트웨어 인코더 저사양 PC에서 매끄러운 화면 데모에 충분하며 CPU를 아낀다.

## 레이어 구조 (기존 3계층 정렬 + 테스트 용이성)

```
MyCapture.Core/Recording      순수 도메인(테스트 100%): 설정, 타임라인, 트림/프레임스텝 계산, 클럭
MyCapture.Platform/Recording  IVideoEncoder 추상화 + MediaFoundationVideoEncoder(P/Invoke),
                              RegionFrameGrabber(BitBlt 재사용), RegionRecorder(오케스트레이션)
MyCapture.App/Recording       RecordingControlWindow(이동식 영역·녹화/정지·지연),
                              RegionRecordingCoordinator, VideoEditorWindow(스크러버·트림·프레임스텝·프레임→이미지편집)
```

`IVideoEncoder` 이음새는 `IHotkeyRegistrar`와 같은 목적: 도메인·오케스트레이션을 네이티브 호출 없이
가짜 인코더로 단위 테스트한다. `RegionRecorder`는 시계·그랩·인코더를 주입받아 프레임 드롭/타이밍을
결정론적으로 검증한다.

## 프레임 이동 UX 규칙 (ScreenToGif·NLE 벤치마크)

- **프레임 이동 모드 ON**: `←/→` = ±1 프레임. `Shift+←/→` = ±10 프레임.
- **프레임 이동 모드 OFF**(기본): `←/→` = 일반 편집기 스텝(기본 5초 / 클립이 짧으면 클립의 1/20, 250ms~1s로 클램프).
  `Shift`는 큰 스텝. 이는 Premiere/DaVinci류가 방향키를 "재생헤드 이동"에 쓰는 관습과 일치한다.
- **트림**: 구간 In/Out 핸들을 드래그하거나 `I`(In)/`O`(Out) 키로 현재 위치를 In/Out으로 설정.
  트림은 비파괴적: 원본 MP4는 유지하고 In/Out만 저장했다가 완료 시 잘라 인코딩한다.
- **현재 프레임에서 이미지 편집**: `E` 또는 버튼 → 현재 프레임을 `BitmapSource`로 추출,
  이를 `FrozenFrame`으로 감싸 기존 `AnnotationEditorWindow`를 그대로 연다(캡처 편집과 100% 동일).

## 상호작용 안전

- 지연 시작은 기존 `CountdownWindow` 패턴을 재사용해 카운트다운 창이 녹화 첫 프레임에 찍히지 않도록
  창을 먼저 닫은 뒤 다음 디스패처 턴에 첫 프레임을 잡는다(capture-before-wait 불변식과 동일).
- 녹화 중 영역 창은 테두리만 남기고 내부는 클릭 통과(hit-test 투명)하여 대상 앱을 가리지 않는다.
- `Ctrl+Shift+X`를 다시 누르거나 정지 버튼/`Esc`로 정지.

## 저장

- 녹화 원본과 편집 결과는 `AppPaths.CapturesRoot` 아래 `recordings/yyyy-MM/`에 MP4로 저장.
- 편집기의 "완료"는 트림된 MP4를 저장하고, 프레임에서 만든 이미지 편집은 기존 캡처 큐로 흘려보내
  갤러리·영속성·재편집 이점을 그대로 승계한다.
