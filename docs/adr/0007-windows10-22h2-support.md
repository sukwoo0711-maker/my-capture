# ADR 0007 — Windows 10 22H2 지원 복원

상태: 채택 — Windows 10 실기 검증 대기
기준: MyCapture 4.2.0, `main@142cf79`
관련: [ADR 0004](0004-video-editor-responsive-preview.md), [ADR 0006](0006-wgc-recording-default.md)

## 배경

0.9.0은 Windows 10을 지원했습니다. 1.0.0(`d66549b`)에서 지원 하한을 Windows 11 21H2로 올렸지만,
현재 캡처·편집·OCR의 핵심 경로는 Windows 10 22H2에서 제공하는 API로 동작할 수 있습니다.
Windows 11 전용 테두리 제거 API와 창 장식을 선택 기능으로 분리하여 기존 x64 장비의 사용을 복원합니다.

Windows 10의 일반 지원은 2025-10-14에 종료됐으며 소비자 ESU도 2026-10-13에 종료됐습니다.
따라서 Windows 10 지원은 최선의 노력(best effort)과 실제 장비·VM 검증을 기준으로 합니다.
코드·빌드 검증만으로 실기 검증을 대신하지 않으며 OS 보안 지원을 제공하는 결정도 아닙니다.

## 결정

| 항목 | 결정 |
|---|---|
| x64 호스트 하한 | Windows 10 버전 22H2, build 19045. 더 낮은 빌드는 실행 게이트와 설치 preflight에서 거부합니다. |
| ARM64 | x64 에뮬레이션은 Windows 11 build 22000 이상으로 유지합니다. Windows 10 on ARM은 x64 앱을 실행할 수 없습니다. |
| 단일 출처 | `WindowsSupportPolicy`에 호스트·ARM64 하한을 두고 설치·패키지·Store 계약 테스트로 일치시킵니다. |
| API 계약 / TFM | `SupportedOSPlatformVersion=10.0.19041.0`은 22H2까지 공유하는 API 계약입니다. `net10.0-windows10.0.22000.0` TFM은 선택적 WinRT 멤버 컴파일을 위해 유지합니다. CA1416 전역 억제를 제거합니다. |
| Store | `MinVersion=10.0.19045.0`. `MaxVersionTested`는 상위 검증 버전을 유지하며 MinVersion보다 낮은 값은 거부합니다. |
| WGC | `ApiInformation`으로 border·access·cursor 멤버를 먼저 검사하고 결과를 캐시합니다. API가 없으면 호출하지 않으며 `InvalidCastException`도 방어합니다. Windows 10에서는 노란 테두리를 허용합니다. |
| 창 장식 | 지원 호스트에서 어두운 제목 표시줄(속성 20)을 적용합니다. 22000부터 모서리·caption/border/text 색상을 적용하고, 22621부터 system backdrop 속성을 적용합니다. Windows 10의 핀 외곽선·이미지 clip·알림 모서리는 0 DIP입니다. |
| Glass 테마 | `Surface.Base`를 WPF 배경 위에 그립니다. 시스템 Mica에 의존하지 않으며 지원되는 호스트에서도 backdrop을 none으로 유지합니다. |
| Print Screen | 레지스트리 값이 없으면 19045에서 꺼짐, 22621부터 켜짐으로 판단합니다. 안내 문구의 설정 경로는 빌드로 고릅니다. 영어는 Windows 10에서 `Ease of Access > Keyboard`, Windows 11에서 `Accessibility > Keyboard`이고, 한국어는 두 OS 모두 `접근성 > 키보드`입니다. Windows 10의 옵션 이름은 "PrtScn"을 쓰므로 설정 검색 안내는 쓰지 않습니다. |

Windows 10에서는 WPF의 픽셀별 알파 레이어드 창(`AllowsTransparency=true`)에
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`를 적용하면 오류 8이 반환됩니다.
[Microsoft의 제한 설명](https://learn.microsoft.com/en-us/archive/msdn-technet-forums/7ce400f0-ebda-4b95-869c-85b5b93f972d)에 따라
빌드 번호 대신 UI 스레드에서 화면 밖의 숨겨진 1×1 레이어드 창을 만들어 실제 API 성공 여부를 한 번 검사하고 캐시합니다.
미지원 환경에서는 녹화 제어창을 불투명 창으로 만들고, 물리 픽셀 기준 프레임 링과 팔레트의 합집합을
`SetWindowRgn`으로 적용하여 내부 클릭 통과를 유지합니다. 카운트다운·셸 알림·빠른 작업창·핀 도구막대도
불투명 배경과 사각 모서리를 사용하며 그림자 여백만 제거합니다. 녹화 옵션·핀 투명도 메뉴는 HWND 생성 전에
팝업 투명도를 끄고 별도로 캡처 제외를 적용합니다. 지원 환경의 레이어드 표시와 녹화에 포함되는 시각 표시는 유지합니다.

## 나머지 의존성

- WGC `CreateFreeThreaded`는 17763, `CreateForMonitor` interop은 18362, 커서 속성은 19041부터입니다.
  `MinUpdateInterval`, `IncludeSecondaryWindows`, `DirtyRegionMode`, WindowId/DisplayId 생성 API는 사용하지 않습니다.
- `WDA_EXCLUDEFROMCAPTURE`는 19041부터입니다. 고해상도 waitable timer는 17134부터이며 기존 sleep 폴백을 유지합니다.
- `StartupTask`, `AppInstance` 활성화, `ApplicationData`, Store 업데이트·평가, Windows OCR·이미지·언어 API는
  19041 이하에서 제공됩니다. 패키지 ID·기능 가용성·실패 처리는 기존 경로를 유지합니다.
- 녹화 출력은 Media Foundation H.264, BGRA/NV12 SDR입니다. HEVC·AV1·HDR 인코더를 필수로 요청하지 않습니다.
  하드웨어 디코더·인코더가 없으면 기존 소프트웨어 경로를 사용합니다. Windows N의 Media Feature Pack과 HDR 색상은 실기 확인 대상입니다.
- ONNX Runtime의 CPU 세션과 RapidOcrNet의 PP-OCR CPU 경로를 사용하며 DirectML을 필수로 요청하지 않습니다.
  모델·CPU·네이티브 라이브러리 가용성은 실제 장비에서 확인합니다.
- UI 글꼴의 Segoe UI Variable/Cascadia 선택에는 Segoe UI/Consolas/Malgun Gothic 폴백이 있습니다.
  제품 아이콘은 WPF 경로 도형이며 Fluent 전용 글리프에 의존하지 않습니다.
- Explorer 폴더 판정은 탭이 없는 단일 shell view도 처리합니다. Windows 11 컨텍스트 메뉴나 snap layout 전용 API는 사용하지 않습니다.
  설정 URI `ms-settings:easeofaccess-keyboard`는 두 OS에서 유효합니다. 테마·accent는 앱 팔레트와 WPF의
  `SystemColors`·`SystemParameters` 고대비 상태를 사용하며 Windows 11 전용 레지스트리 값에 의존하지 않습니다.
- MSIX `uap10:RuntimeBehavior`/`TrustLevel`은 19041에서 지원합니다. 이후 `uap11`은 IgnorableNamespaces에 포함되어
  Windows 10에서 무시됩니다. 서명 없는 `-LocalTest`/`-AllowUnsigned` 설치는 Windows 11 검증용으로 유지합니다.

## 영상 미리보기 색상 범위

`StudioRangeVideoEffect`는 WPF `MediaElement`가 스튜디오 범위(16–235) 영상을 스튜디오 범위 RGB로
넘긴다고 보고 셰이더로 넓혔습니다. Windows 10 22H2 실기(Intel HD 630/GTX 1050, #84)에서는 재생기가
이미 전체 범위로 넓혀 셰이더가 한 번 더 적용되었고, 원본 회색 16이 0으로 표시되었습니다.
스튜디오 입력의 확장을 해제한 뒤에도 전체 범위 입력은 회색 16이 0, 235가 253으로 표시되었습니다.
build 33602fe의 추가 관측에서는 갤러리의 톤 곡선이 세 표준 범위 모델 모두와 달랐습니다.
스튜디오 원본 0/16/64/192/235/255는 보정 없이 약 12/30/74/183/232/255로 표시됐습니다.
같은 영상의 편집기는 identity였으며 갤러리의 실제 제시 배율은 약 0.369였습니다.
Windows 11 검증 PC의 retained 오차는 0.211입니다. OS 빌드나 GPU 제조사로 분기하지 않고
각 플레이어의 실제 출력을 측정합니다.

2026-10-16의 build 062106e 실기에서는 같은 1280×720 색상 클립도 편집기는 정상,
갤러리는 스튜디오 범위 유지로 표시됐습니다. 320×180 프로브의 전역 판정은 실제 호스트의
배율·콘텐츠에 따른 표시를 예측하지 못했고, 자식 프로세스 보정도 응답성을 개선하지 못했습니다.
일반 미리보기의 숨겨진 프로브·보정 인코딩·보정 자식 프로세스를 제거합니다.
내부 `--measure-video-range` 스위치와 전송 구현도 제거합니다.

`StudioRangeVideoEffect.Apply`는 실제 MediaElement마다 검증 상태를 만듭니다. 초기 보정은 세션 캐시의
플레이어 종류(gallery/editor), 입력 범위, 실제 제시 배율 버킷(<0.5, 0.5 이상 1 미만, 1 이상)으로
선택합니다. 작은 갤러리와 큰 편집기, 1280/1920 입력의 실기 차이에 근거해 버킷을 둡니다.
캐시가 없으면 스튜디오 입력은 Expand, 전체 범위 입력은 None입니다. 하드웨어 셰이더가 없으면
효과와 검증을 시작하지 않습니다. 생성자·빈 플레이어도 디코드 작업을 시작하지 않습니다.

영상이 연결된 visual에서 두 번의 composition 기회를 지난 뒤, 마지막 탐색·스크럽 요청으로부터
300ms 이상 유휴 상태일 때 ApplicationIdle dispatcher 우선순위로 검증합니다. MediaOpened만으로
프레임 도착을 확정하지 않습니다. 투명한 샘플은 디코드하지 않고 재시도합니다. WPF에는 정확한
첫 표시 프레임 통지가 없어 실제 픽셀과 디코드 참조의 일치도까지 확인합니다.

현재 MediaElement를 VisualBrush로 최대 변 320픽셀의 종횡비 유지 RenderTargetBitmap에 렌더합니다.
효과 해제는 동기 Render 호출에만 적용하고 finally에서 즉시 복원합니다. 이 구간은 dispatcher
처리를 비활성화하여 composition이나 메시지 펌프가 끼어들 수 없도록 합니다. 표시 위치를 기록하고,
해당 위치의 참조는 UI 밖에서 RangeAwareVideoFrameDecoder로 정확 탐색·디코드한 뒤 면적 가중 축소합니다.
참조의 5×5 이웃이 채널별 6단계 이하로 평탄한 점만 사용합니다. 최소 64점과 최소 96단계의
참조 동적 범위가 필요합니다.

세 모델은 identity(x), studio-retained(16+x×219/255), expanded-as-studio(clamp((x−16)×255/219))입니다.
각 B/G/R 채널을 8개 밝기 구간으로 나누고 구간마다 절대 오차 상위 20%를 제외한 평균을 계산한 뒤
구간을 균등 가중합니다. 포화된 검정·흰색이 많아도 중간 회색의 구별력을 보존하고 10–20%의
움직임·이상치에 견디기 위한 선택입니다. 모든 채널의 최선 모델 오차가 2.5 이하이고,
차점 전체 오차가 최선 전체 오차의 두 배에 1을 더한 값 이상이어야 확정합니다.
2.5는 Windows 11의 retained 오차 0.211 및 코덱·양자화 잡음에 여유를 두면서,
Windows 10 갤러리의 최소 오차 4.494(스튜디오), 3.154(전체 범위)를 배제합니다.
실기의 세 모델 오차는 각각 4.494/6.843/10.119와 3.154/13.128/3.587입니다.
최종 화면 회귀 허용치(스튜디오 8, 전체 범위 12)는 유지합니다.

표준 모델이 맞지 않으면 같은 평탄점의 표시값→디코드 참조값으로 B/G/R별 역곡선을 구합니다.
표시값을 4단계 구간에 모으고 최소 4점 및 전체 채널 점 수의 1% 이상인 구간만 사용합니다.
참조값의 6단계를 넘는 간격으로 군집을 나누고, 폭 6 이하의 평탄한 참조 군집은 표시 구간 경계를
넘는 코덱 잡음을 함께 모아 안정화합니다. 각각의 교대 행 절반에서 `max(4, ceil(해당 절반 점 수 × 1%))`
이상 지지되는 군집만 개별 지지점으로 사용합니다. 전체 적합과 양쪽 절반은 같은 지지점 목록을 사용하며,
표시값과 참조값의 중앙값은 각 절반에서 독립적으로 구합니다. 한쪽만 지지하는 군집은 제외 사유와
양쪽 표본 수를 근거에 남깁니다. 연속 참조 자료나 양쪽 참조값이 달라 반복 군집을 만들 수 없는 구간은
양쪽에서 지지되는 동일한 표시 구간의 독립 통계를 유지하여 실제 행 차이를 검출합니다.

동일한 표시 구간 또는 인접한 두 구간에 여러 반복 지지 군집이 있으면 제한된 plateau 후보를 만듭니다.
전체·양쪽 절반 모두에서 표시 중앙값이 서로 다르고 참조 중앙값과 같은 순서이며 이웃 지지점의 순서를
침범하지 않을 때만 각 군집의 관측 중앙값을 별도 지지점으로 사용합니다. 인접 구간을 연쇄로 합쳐
범위를 늘리지 않습니다. 표시 중앙값이 같은 실제 클리핑은 군집별 참조 중앙값을 동등하게 반영한
하나의 지지점으로 유지합니다. 검정 패치의 면적이 작은 회색 16 패치를 지우지 않도록 구간 내 10% 대신
양쪽 절반의 전역 지지 기준을 사용합니다. 원시 표본은 잔차·홀드아웃 검사에 그대로 사용합니다.
기본 적합이 성공하면 곡선과 근거를 그대로 사용합니다. 기본 적합이 잔차 또는 홀드아웃 잔차에서
거부된 경우에만, 폭 6 이하인 기존 평탄 참조 군집의 제한된 대체 계획을 검사합니다. 해당 군집이
양쪽 절반 각각의 전역 지지 기준을 충족한 표시 구간을 3개 이상 포함하면, 양 끝의 지지 구간에서
각각 관측한 중앙값 두 개를 사용합니다. 전체·양쪽 절반의 좌표와 참조값은 독립적으로 계산하며,
표시 구간 0·63을 끝점으로 삼거나 기존 balanced/adjacent plateau를 확장하지 않습니다.
두 구간에 걸친 127/128 코덱 잡음은 기존 한 지지점으로 유지합니다. 원시 표본과 패치 근거는
제외하지 않고 커버리지·PAV·잔차·홀드아웃·전체 범위 split 4·패치 12 검사를 모두 다시 적용합니다.
후보가 모두 통과할 때만 채택하고 선택 계획과 거부된 기본 적합의 전체 진단을 기록합니다.
후보가 실패하면 기존 기본 적합의 실패 진단을 그대로 반환합니다. 이는 넓게 퍼진 평탄 구간을
한 점으로 대표하면서 생긴 실기 잔차를 줄이기 위한 제한이며 허용 오차를 바꾸지 않습니다.

구간별 동등 가중 pool-adjacent-violators 회귀로 단조 증가를 보장하고 지지점 사이를 선형 보간합니다.
양 끝은 첫째·마지막 지지 구간의 기울기로만 외삽한 뒤 0–255로 제한합니다. 임의의 (0,0),
(255,255) 지지점을 추가하지 않습니다.

채널마다 최소 6개 표시 구간, 5개 밝기 대역, 최솟값 32 이하, 최댓값 223 이상,
표시 범위 192 이상과 지지점 사이 최대 간격 112 이하가 필요합니다. 교대 샘플 행을 두 집합으로
나누고 양쪽 모두 같은 커버리지를 요구합니다. 전체 적합 잔차와 양방향 홀드아웃 잔차는 모든 채널에서
2.5 이하여야 합니다. 잔차도 참조 밝기 대역별 상위 20%를 제외하고 대역을 동등 가중합니다.
두 독립 곡선은 공통 지지 범위의 어느 값에서도 4단계 넘게 다르면 거부합니다.
이 범위는 기존처럼 양쪽 원시 표시 구간의 지지 범위 전체가 겹치는 구간이며, 안정화된 지지점 근처로
축소하지 않습니다. 같은 plateau 구간에 지지점 두 개가 있어도 커버리지의 표시 구간 수는 늘어나지 않습니다.
추가로 모든 반복 지지 패치에서 `|곡선[표시 중앙값] − 참조 중앙값| ≤ 12`를 요구합니다.
절사 평균에 가려지는 작은 패치의 오차도 개별로 검사하고, 초과하면 `mapping-patch-error`로 거부합니다.
낮은 커버리지·비단조 자료·분할 불일치는 보정 근거로 사용하지 않습니다.

거부 원인을 구별하기 위해 전체·두 절반의 채널별 지지점(표시값, PAV 전후 참조값, 표본 수),
최대 분할 차이의 채널·입력값·양쪽 곡선값·좌우 지지점·최근접 거리를 기록합니다. 양쪽 모두
4단계 이내에 지지점이 있으면 `supported`, 나머지는 `gap`으로 표시하고 각각의 최대 차이를 보존합니다.
`AlignedSplit`은 공통 지지점 목록에서 전체 적합의 표시 좌표에 정렬한 양쪽 PAV 값의 최대 차이입니다.
동일 구간의 별도 plateau 지지점도 각각 보존합니다. 제외된 군집과 절반에 3점 미만인 구간은
결측값과 실제 표본 수로 남기며 실제 곡선에는 넣지 않습니다.
참조값/32 밴드별 절사 평균 오차·전체/잔존 표본 수·최악 밴드, 하나의 표시 구간에 합쳐진 지지
참조 군집의 중앙값·표본 수, 채널별 상위 3개 표시/참조 군집의 오차도 기록합니다.
Information과 보고서의 `Details`에는 최대 위치·supported/gap/aligned 차이·최악 밴드·병합 구간 수만
짧게 추가하며, 전체 근거는 검증 결과의 `Evidence`와 구조화된 JSON 속성, 갤러리·편집기 테스트
실패 메시지에 포함합니다. 군집별 양쪽 지지 수·반복 여부·제외/plateau 결정·보정 중앙값 오차와
최대 패치 오차도 JSON 및 근거에 기록합니다. 기존 모델 선택, 원시 표본, 커버리지 규칙, 잔차·홀드아웃 2.5,
전체 공통 범위의 split 4, PAV·보간·외삽 및 최종 화면 8/12 허용치는 유지합니다.

identity는 None, retained는 Expand, expanded는 Compress이며 입력 범위 플래그와 무관하게 선택합니다.
측정 곡선은 Measured 보정과 함께 같은 플레이어 종류×입력 범위×배율 캐시에 보관합니다.
Information에는 확정 종류(model/measured/fallback), 모델별 오차, 곡선 적합·홀드아웃 잔차,
분할 차이·채널별 커버리지와 256개 B/G/R 곡선의 무손실 Base64를 한 번 기록합니다.
점 수·동적 범위·시도 수·위치도 유지합니다.
불확정·실패는 0.5초, 2초, 5초의 증가 간격으로 최대 세 번 더 시도한 뒤 None을 적용하고
해당 키의 이전 캐시를 무효화하며 마지막 오류를 한 번 기록합니다.
새 탐색은 진행 중인 작업을 취소하고 유휴 대기를 다시 시작합니다. 클립 변경·창 닫힘·새 검증은
이전 결과를 폐기합니다. 캐시는 초기 추정이며 다음 플레이어도 자체 검증합니다.

MeasuredRangeVideoEffect는 영상 Input(s0)과 1024×256 BGRA 조회 텍스처(s1)를 사용하는 ps_2_0
셰이더입니다. 256개 LUT 항목을 각각 4열과 모든 256행에 반복합니다. RGB를 alpha로 나눈 뒤
`entry=floor(value×255+0.5)`로 항목을 정하고 `(entry×4+1.5)/1024`, `128.5/256`의 내부 텍셀 중심에서
조회한 색상에 alpha를 다시 곱합니다. 최근접·bilinear 조회 모두 같은 항목의 내부 텍셀을 읽으며,
반투명 입력도 가장 가까운 8비트 항목으로 고정합니다. 텍스처의 alpha는 255이며 투명 영상의 alpha는
보존합니다. HLSL Source와 체크인 바이트코드의 재컴파일 명령 일치 및 독립 채널·alpha 검사를 둡니다.
WPF 소프트웨어 ps_2_0의 근사 역수가 반투명 입력의 반올림 경계를 넘는 경우에는 바이트 단위
분자·분모로 계산한 정확한 반항목 경계와 비교하여 항목을 보정합니다. alpha 64, 채널 31에서
항목 124 대신 123을 선택하던 사례를 재현했으며, 허용 오차를 늘리지 않고 이 경계 계산을 수정합니다.
실제 갤러리·편집기의 모델 선택만 건너뛰는 테스트 훅으로 측정 경로를 강제하고 기존 최종 픽셀 비교를
수행합니다. 샘플링·참조 디코드·적합 검증은 제품 경로를 그대로 사용합니다.

작은 표시 크기 회귀 검사는 회색 256단계와 색상 패치 16개를 정적 격자로 배치하여 물리 폭
32/64/128/200/256/400/1280에서 실제 WPF RenderTargetBitmap 출력과 채널별 조회값을 비교합니다.
96/120/144 DPI 및 0.87/1.13 비정수 배율, alpha 64/128/192를 검사하며 ±1 기준을 유지합니다.
이전 96/120 DPI의 스튜디오 역곡선·급경사 합성 곡선 검사에서 ImageBrush의 회색 최대 오차는
각 폭에서 5/3/2/0/2/0 및 26/14/7/0/7/0이었습니다. 효과 크기로 다시 그려지는 ImageBrush 대신
1024×256의 분리된 DrawingVisual을 RenderAtScale=1의
[BitmapCacheBrush](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcachebrush)로 제공합니다.
[WPF의 캐시 샘플러 경로](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/ShaderEffect.cpp)는
효과 크기의 중간 표면 대신 캐시 텍스처를 사용합니다. 이전 256×1 캐시는 Windows 11 개발 PC의
64–1280 폭에서 회색과 색상 패치 최대 오차 0으로 통과했습니다. 당시 하드웨어 합성 경로(렌더링 계층 2, 화면에 표시한 창을
PrintWindow로 읽고 효과 없는 기준 화면이 원본과 정확히 같음을 먼저 확인)에서도 64~1280 폭의 오차는 0이었습니다.
그러나 Windows 10 장비에서는 1텍셀 높이의 경계·필터링·캐시 재샘플링에 취약해 작은 출력이 너무 어두워졌습니다.
반복 텍스처는 수평 이웃과 수직 방향에 같은 값을 제공하고 경계에서 떨어진 곳을 조회합니다.
ps_2_0 프로필을 유지한 채 HLSL과 체크인 바이트코드를 함께 갱신하고 재컴파일 일치 검사를 수행합니다.
1024×256 텍스처도 개발 PC의 하드웨어 합성 경로(렌더링 계층 2, PrintWindow 기준 화면 일치 확인)에서
32~1280 폭의 오차가 0이었습니다.
이 검사는 WPF 렌더링 회귀 근거이며 Windows 10 장비의 실제 MediaElement·GPU 검증은 별도로 수행합니다.

2026-10-16 및 2026-10-17 06:43 UTC의 #84 실기 보고는 Windows 10 22H2 build 19045.6466,
Intel HD 630 + GTX 1050, 125% DPI 조건입니다. `cd41e96`에서 작은 출력 8개 사례와 실제 갤러리
studio/full × 일반/강제 측정 4개 색상 검사가 실패했습니다. 실기 테스터는 반복 텍스처·반복 지지·제한된
plateau 후보·패치별 12 제한을 적용한 로컬 수정으로 전체 5,349개가 통과했다고 보고했습니다.
보정 후 갤러리 최대 오차는 studio 2(한도 8), full 8(한도 12)이었고,
`Open(false) → Show → 재생 버튼 → 일시 정지 버튼`의 네 시점에서도 통과했습니다.
해당 로컬 코드는 제공되지 않았으므로 이 변경의 Windows 10 검증 결과와 구분합니다.
이전 win10.7의 갤러리 studio는 잔차 0.464·홀드아웃 0.838·split 7.000, full은
잔차 2.787·홀드아웃 2.844·split 2.000으로 거부됐으며, 편집기 강제 full은 잔차 2.528·split 0이었습니다.
H8에서는 같은 dispatcher 턴의 `Play(); Pause();`만 편집기의 identity와 일치했고, 한 번 재생·정지하면
갤러리 톤 곡선으로 돌아왔습니다. 제품의 열기 순서를 유지하고 실제 사용자 버튼 경로의 효과 적용 출력을
검증 직후·재생 중·정지 후·두 번째 재생/정지 후에 일반/강제 측정 각각 검사합니다.

Compress는 premultiplied alpha에 맞춘 `rgb * 219/255 + a * 16/255`입니다. 재생기가 잘라낸
원본 16 미만과 235 초과 값은 복원할 수 없어 16/235로 표시됩니다. 측정 곡선도 클리핑으로 합쳐진
원본값을 구분할 수 없습니다. 측정 보정의 최종 픽셀 검사에는 원본과 동일한 8/12 허용치를 적용하며
Compress의 끝값 예외를 적용하지 않습니다. 원본 파일·내보내기는 변경하지 않습니다.
인프로세스 프로브는 명시적 `--selftest-video-range`의 H5/H6 관측용으로만 남습니다.
기존 프레임 저장 테스트의 주입 계약은 호환용으로 유지하며 제품에서 설정하거나 시작하지 않습니다.

프레임 편집에 전달하는 픽셀은 정확 탐색 요청 시각을 고정한 뒤 별도 STA 스레드에서
`VideoFrameRenderPipeline.RenderSingleFrame`으로 디코드하고 기존 프레임·텍스트 레이어를 합성합니다.
`MediaElement`의 표시 픽셀은 프레임 저장에 사용하지 않습니다. 따라서 미리보기의 Expand/Compress와
presenter의 범위 오류는 회전·블러·모자이크 레이어 PNG에 포함되지 않습니다. 회전만 한 편집도 레이어로 저장합니다.
썸네일·포스터·filmstrip·GIF·MP4는 기존 Media Foundation 디코드 경로를 유지합니다.
`RenderCurrentFrameForTest`는 실제 미리보기의 색상과 탐색을 검사하는 시각 전용 훅입니다.

### RGB 영상 내보내기의 NV12 변환

RGB 경로의 MP4 내보내기는 `BgraToNv12Converter`에서 BGRA를 NV12로 변환한 뒤
`VideoEncoderInputFormat.Nv12`로 인코더에 전달합니다. WPF로 합성한 프레임과 합성을 생략한
디코드 BGRA 프레임에 같은 변환을 적용하며, 출력 NV12 버퍼는 내보내기 동안 재사용합니다.
NV12 원본 통과 경로, `RegionRecorder`, 인코더의 전체 범위 RGB 녹화 경로는 유지합니다.

이전 RGB 경로는 Sink Writer가 삽입하는 OS RGB→Y′CbCr 변환기에 의존했습니다. 수정 전 리드의
이 PC 측정에서 소프트웨어 강제(`AllowHardwareEncoder=false`)는 회색 128을 Y=127,
흰색을 Y=237로 인코드했고, 하드웨어 허용 경로는 각각 Y=126, Y=235를 기록했습니다.
GPU가 없는 GitHub `windows-2025` 실행기에서는 RGB 내보내기의 회색 128이 디코드 후
(133,128,133) 또는 (132,129,134)으로 관측되어 Cb/Cr 약 +2에 해당하는 자홍 편향이 나타났습니다.
같은 실행기의 NV12 통과 출력은 통과했습니다. 출력 색상 계약을 OS 구성에 맡기지 않도록 변환을 명시합니다.

색상 메타데이터가 없는 출력의 디코드 계약인 `DefaultYuvEncoding(height)`에 맞춰 스튜디오 범위를
사용합니다. 높이를 16줄 매크로블록 단위로 올린 coded height가 720 이상이면 BT.709,
그보다 작으면 BT.601을 사용합니다. 8비트 R′G′B′에서
`L=Kr·R+(1−Kr−Kb)·G+Kb·B`로 두고 BT.709는 `(Kr,Kb)=(0.2126,0.0722)`,
BT.601은 `(0.299,0.114)`를 사용합니다. 변환식은 `Y=16+219·L/255`,
`Cb=128+224·(B−L)/(255·2·(1−Kb))`, `Cr=128+224·(R−L)/(255·2·(1−Kr))`입니다.

계수는 소수부 16비트 정수로 최근접 반올림하며 정확히 절반이면 짝수로 반올림합니다.
각 행의 계수 하나를 나머지에서 도출해 색차 계수 합을 0으로 유지합니다. 최종 표본은 half-up으로
반올림하고 0–255로 제한하므로 모든 회색의 Cb와 Cr은 정확히 128이며 검정과 흰색의 Y는 16과 235입니다.
4:2:0 색차는 2×2 블록의 RGB 합을 사용해 소수 평균을 보존한 채 변환한 다음 한 번만 반올림합니다.
이는 선형 변환 전 RGB 평균과 같으며 픽셀별 색차를 먼저 반올림할 때의 오차를 피합니다.
입력은 위에서 아래로 저장하며 불투명 채널 또는 검정 위에 합성된 premultiplied 채널을 사용하고
alpha로 다시 나누지 않습니다. 기존 NV12 짝수 크기 검사를 유지합니다. 홀수 크기이거나
인코더가 NV12 입력을 거부하면 이전처럼 RGB32 입력과 Sink Writer 변환기를 사용합니다.

독립 실수 수식으로 16색, 256단계 회색, 혼합 2×2 블록을 검증합니다. 실제 RGB 내보내기는
BT.601/BT.709와 하드웨어 허용/소프트웨어 강제 조합에서 합성·비합성 프레임을 NV12로 다시 읽어
회색의 Y 오차 ±1과 Cb=Cr=128을 검사합니다. 1080p 60프레임 시간 비교는 선택 실행 진단이며
CI의 속도 통과 조건으로 사용하지 않습니다.


## 노란 테두리 검증

기존 `--selftest-recording-backends`의 테두리 프로브는 GDI로 읽은 화면 가장자리만 관찰했습니다.
전용 `--selftest-wgc-border`는 주 모니터의 작업 영역(물리 픽셀, 작업 표시줄 제외)을 자체 단색 창과 움직이는 표식으로 덮고
`requestBorderless: false`로 그 작업 영역을 40프레임(20fps, 2초) 녹화합니다. 원본 BGRA 프레임과 디코드한 MP4 40프레임의 바깥 10픽셀을 모두 검사합니다.
작업 표시줄이 차지하는 가장자리는 fixture와 녹화 범위 밖이므로 이 검사가 다루지 않습니다. Windows 10 실기 PASS도
1920×1080 모니터 중 하단 작업 표시줄 50픽셀을 뺀 1920×1030 작업 영역에 대한 결과입니다. 그 가장자리는 육안 확인으로 대신합니다.
노란 픽셀이 0이고 가장자리가 fixture 색상과 일치하며, 원본과 디코드 프레임 모두에서 중앙 표식이 바뀌어야 통과합니다. 빈 화면·프레임 정지·검은 디코드·가려진 창은 실패로 처리합니다. 진단 전체에 30초 제한이 있으며, 초과하면 fixture 창을 닫고 실패를 기록합니다.
Windows 11에서는 `IsBorderRequired=true` 읽기 결과도 확인합니다. MP4와 JSON을 보존합니다.

일반 데스크톱 세션에서 저장소 루트를 기준으로 아래 명령을 실행합니다. Windows 10 VM에서는 ``를 설치된 `MyCapture.exe` 경로로 바꿉니다.

```powershell
$repo = (git rev-parse --show-toplevel)
$run = Join-Path $env:TEMP ('MyCapture-wgc-border-' + [guid]::NewGuid().ToString('N'))
$env:MYCAPTURE_DATA_ROOT = Join-Path $run 'data'
$exe = Join-Path $repo 'src\MyCapture.App\bin\Release\net10.0-windows10.0.22000.0\MyCapture.exe'
$output = Join-Path $run 'border'
& $exe --selftest-wgc-border $output | Out-Null
$LASTEXITCODE
Get-Content -LiteralPath (Join-Path $output 'wgc-border-report.txt')
Get-Content -LiteralPath (Join-Path $output 'wgc-border.json')
```

기대 결과는 exit code 0, `RESULT: PASS`, `RawYellowPixels=0`, `DecodedYellowPixels=0`,
`UnexpectedEdgePixels=0`, `FramesCopied >= 2`이고 원본·디코드 프레임 검사가 모두 유효해야 합니다. 녹화 중 노란 테두리가 실제 화면에 보이는지는 육안으로도 확인합니다.
다른 앱의 최상위 창을 닫고 SDR에서 먼저 검증한 뒤 HDR·혼합 DPI를 추가합니다. 기존 MyCapture 프로세스는 종료하지 않습니다.
Store 서명본에서도 같은 스위치로 검증합니다. Windows 10의 진단에는 border API가 `unavailable (pre-Windows 11)`로 표시됩니다.

## 실기 수용 조건

### MediaElement 색상 범위 관측

일반 데스크톱 세션에서 다음 명령으로 장비의 범위 동작을 수집합니다. 설치본은 `$exe`만 해당
`MyCapture.exe`의 절대 경로로 바꿉니다. 기존 MyCapture 프로세스를 종료하지 않습니다.

```powershell
$run = Join-Path $env:TEMP ('MyCapture-video-range-' + [guid]::NewGuid().ToString('N'))
$env:MYCAPTURE_DATA_ROOT = Join-Path $run 'data'
$exe = 'C:\GitRepositories\my-capture\.claude\worktrees\admiring-greider-0655a6\src\MyCapture.App\bin\Release\net10.0-windows10.0.22000.0\MyCapture.exe'
$output = Join-Path $run 'range'
& $exe --selftest-video-range $output | Out-Null
$LASTEXITCODE
Get-Content -LiteralPath (Join-Path $output 'video-range-report.txt')
Get-Content -LiteralPath (Join-Path $output 'video-range.json')
```

공개 인수 형식은 `--selftest-video-range <절대 출력 디렉터리>`입니다. 상대 경로·드라이브 루트·UNC·
링크/junction·모호한 경로는 거부합니다. 자체 실행 파일의 내부 스위치로 셀마다 새 자식 프로세스를
시작합니다. studio/full × 320×180/640×360/1280×720/1920×1080 × probe/gallery/editor의 24개 셀,
gallery와 probe 각각의 studio→full→studio 및 full→studio 순서 셀 4개, 1280×720 studio gallery의
추가 독립 반복 3개를 유지합니다. H6의 studio 1280×720/1920×1080 × gallery/editor ×
in-process 동시 보정 4개 셀과 H7의 1280×720 studio gallery 색상 패턴 1개 셀을
추가하여 36개 셀 프로세스·42개 최초 관측 단계를 수집합니다. 그 뒤에 H8/H9 경로 셀 24개를 덧붙여
총 60개 셀·66개 최초 관측 단계가 됩니다. 자식은 직렬로 실행하며 셀당 20초, 전체 13분(기존 5분 +
경로 셀 24개×20초) 제한을 적용합니다. 시간 초과 시 해당 진단이 시작한 자식만 종료합니다.

각 gallery/editor 단계는 제품의 플레이어별 검증을 실행하고 완료 결과를 원시 표본 옆에 기록합니다.
원시 RGBA는 동기 샘플링 중에만 효과를 해제하여 수집합니다. probe는 진단 전용 창·MediaElement를 사용합니다. 모든 창은 화면 밖에
배치하고, probe는 원본 크기의 DIP, gallery는 제품 기본 크기, editor는 1100×760 DIP로 측정합니다.
순서 셀은 같은 창을 유지하며 gallery는 같은 MediaElement를 재사용하고 probe는 진단 프로브처럼
단계마다 새 MediaElement를 만듭니다. 다섯 표본의 원시 RGBA를 모두 저장하고 마지막 두 판정이
일치하면 확정합니다. 불일치·알 수 없는 패턴은 `unknown` 관측으로 보존합니다.

기존 H1–H5 셀은 모든 단계가 끝난 뒤 같은 프로세스에서 명시적인 진단용 인프로세스 보정을 한 번 실행합니다.
제품의 플레이어별 검증과 별도로 이전 H5 실험의 의미를 유지합니다. 보정의 full-range 영상이
최초 studio 관측이나 순서 실험에 앞서 재생되지 않도록 순서를 고정합니다. 갤러리·편집기 셀은 창을 연 채로
보정을 실행하고 같은 재생기를 한 번 더 측정합니다(`after calibration`). 보정 영상이 재생된 뒤 이미 열린
미리보기의 표시가 바뀌는지는 H5로 요약합니다. 순서 셀의 두 번째 이후 단계에서 영상을 바꾸다 재생기가
실패하면 `media-failed <HRESULT>` 관측으로 기록하고 다음 단계를 계속합니다. 그 밖의 열기 실패는 원시
표본이 없으므로 셀 실패(FAIL)로 처리합니다. 같은 갤러리 재생기에서 studio→full로 영상을 바꿀 때
`MILAVERR_UNEXPECTEDWMPFAILURE`가 나던 문제는 영상을 바꾸기 전에 이전 미디어를 닫도록 고쳤습니다. JSON과 gallery/editor 보고서 셀에는
플레이어 검증의 결정 종류·모델별 및 채널별 오차·곡선 잔차·커버리지·256개 채널 곡선(Base64),
점 수·동적 범위·시도 수·위치, 진단 보정 결과·실패,
창/콘텐츠 DIP 크기, 실제 제시 픽셀 크기와 원본 대비 배율, DPI, WPF tier·PS 2.0 지원,
DXGI 어댑터 목록(또는 조회 실패)을 포함합니다. 어댑터 목록은 MediaElement가 실제 선택한 GPU나
하드웨어/소프트웨어 디코더를 확정하지 않습니다.

`video-range-report.txt`에는 재실행 명령·관측 표·H1 크기별/H2 호스트별/H3 순서별/H4 동일 조건 반복별/
H5 보정 전후 판정 차이를 기록합니다. H6/H7 요약은 별도로 기록하며 기존 H1–H5 집계에는 넣지 않습니다.
`Calibration differs`는 먼저 관측한 결과와 이후 인프로세스 보정의 차이입니다.
차이는 인과관계의 확정이 아니므로 JSON의 원시 표본과 배율도 함께 비교합니다. `c-<8자>` 아래
계획 순서의 두 자리 번호 폴더(`00`, `01`, …)에 셀별 `video-range.json`, `video-range-report.txt`,
합성 `studio.mp4`/`full.mp4`를 보존하며, 각 셀의 id는 JSON에 있습니다. Media Foundation의 sink writer가
MAX_PATH를 넘는 경로를 받지 않으므로 폴더 이름을 짧게 유지하고, 진단 프로브는 격리된 임시 폴더에 보정 영상을 씁니다.
자식의 `MYCAPTURE_DATA_ROOT`와 TEMP/TMP는 셀의 `data` 하위에 격리하며, 진단 프로브의 임시 클립에 대한
기존 삭제·재시도 호출은 유지합니다. 실제 사용자 영상·설정·클립보드는 사용하지 않습니다.

H6는 플레이어를 여는 동일 dispatcher 작업에서 보정을 시작하고 기다리지 않은 채 열기를 진행합니다.
원시 표본은 효과를 동기적으로 해제하여 수집합니다. `opening-with-calibration` 표본과 보정 완료 후 `after-calibration` 표본을
각각 수집하며 열기·보정 시작/종료·표본 시각(ms)을 JSON에 기록합니다. 이미 열린 플레이어에 보정을
실행하던 H5와 구분합니다. 같은 크기/호스트의 새 프로세스 기준 표본과 인프로세스 진단 프로브의 영향을 비교합니다.
제품에서는 동시 프로브를 시작하지 않습니다. H6는 과거 드라이버 상태 변경 가설을 위한 명시적 진단으로만 남습니다.

H7은 회귀 테스트 `RangePatternClip`과 동일한 공유 인코더·8×2 패턴을 사용합니다. 검정,
grey 16/64/128/192/235, 흰색, 빨강, 초록, 파랑, 노랑, 청록, 자홍, orange (242,177,52),
navy (29,42,60), accent (46,111,216)의 중심 RGBA 16개를 모두 보존합니다. 분류에는 기존 회색 바와
동일한 0/64/192/255 네 패치를 사용하며 나머지 색도 원시 값으로 비교합니다. 최초 H7 표본은 보정 전에
수집하므로 콘텐츠 가설을 동시 보정 가설과 구분할 수 있습니다. 두 가설의 상호작용은 이 단일 셀로 확정하지 않습니다.

H8은 Windows 10 실기에서 같은 색상 패턴을 편집기는 identity로, 갤러리는 비표준 톤 곡선으로 표시한 원인을
조건 하나씩 바꿔 분리합니다. 1280×720 색상 패턴(10초 정지 영상)을 studio/full로 만들고 제품 갤러리,
즉시 일시정지 열기(편집기와 같은 `Play(); Pause();`), 재생 중 관측, 큰 갤러리(배율 0.9 이상), 캡처 제외 없음,
둥근 클립 없음, 일반 창(Topmost·ToolWindow 아님), 제품 편집기, 갤러리 배율의 작은 편집기, 편집기와 같은
조건을 모두 적용한 갤러리의 10가지로 표시합니다. 진단 전용 옵션은 진단만 전달하며 제품 생성자의 동작은
그대로입니다. 준비 후 약 300/1000/2500/5000ms에 효과를 해제한 16개 패치를 관측하고, 즉시 일시정지 셀은
Play→500ms→Pause 뒤 한 번 더 관측합니다. 셀마다 제품 검증 결과(근거 포함)를 함께 기록하고, 단일 조건 셀이
제품 갤러리·제품 편집기와 판정 종류와 회색 패치(4단계 초과 차이)에서 일치하는지 요약합니다. H9는 같은
호스트·배율에서 색상 패턴과 회색 바를 비교해 콘텐츠 의존성을 확인합니다. 경로 셀은 진단 보정을 시작하지
않습니다. 개발 PC에서는 모든 경로 셀이 studio=StudioRetained, full=Correct로 같았고 회색 차이는 0이었습니다.

완료 조건은 exit code 0 및 `RESULT: PASS`, 60개 셀·66개 최초 관측과 경로 셀 시계열 98개 수집입니다.
열린 gallery/editor의 보정 후 재표본 26개를 합치면 정상 실행의 표본 묶음은 68개입니다. 색상 판정이 비정상이거나
`unknown`이어도 관측을 완료하면 PASS입니다. 열기·인코딩·자식 실행·시간 초과 등 수집 인프라 오류는
FAIL이며 누락 셀을 성공으로 처리하지 않습니다. 플레이어 검증의 불확정·실패/폴백도 관측 필드로 기록합니다.

### 편집기 응답성 검증 비교

`--selftest-video-editor <절대 출력 디렉터리>`에서만 환경 변수
`MYCAPTURE_VIDEO_EDITOR_SELFTEST_CALIBRATION`을 읽습니다. `none`은 검증을 끄고 초기 효과 정책을
유지하며, `default`(기본값)는 제품 검증을 실행합니다. 잘못된 값은 실행 오류입니다.
일반 앱 실행에는 이 환경 변수를 적용하지 않습니다. 각 모드를 새 프로세스에서 3회 이상 실행합니다.
매번 새 TEMP 하위 출력 및 데이터 디렉터리를 사용합니다.

보고서는 cycle p50/p99/max, 스크럽 동안의 실제 샘플링·디코드 횟수, 진행 중인 검증과의 겹침,
플레이어 검증 결과와 효과를 기록합니다. 스크럽 중 검증 작업이 0이어야 한다는 검사를 추가합니다.
기존 p99 ≤ 50ms, max ≤ 150ms 게이트와 filmstrip 준비·프레임 편집·탐색 게이트를 유지합니다.

```powershell
$run = Join-Path $env:TEMP ('MyCapture-editor-verification-' + [guid]::NewGuid().ToString('N'))
foreach ($mode in 'none', 'default') {
    foreach ($iteration in 1..3) {
        $output = Join-Path $run ($mode + '-' + $iteration)
        $env:MYCAPTURE_DATA_ROOT = Join-Path $output 'data'
        $env:MYCAPTURE_VIDEO_EDITOR_SELFTEST_CALIBRATION = $mode
        & $exe --selftest-video-editor $output | Out-Null
        $LASTEXITCODE
        Get-Content -LiteralPath (Join-Path $output 'video-editor-selftest-report.txt')
    }
}
```

프레임 저장 수용 검사에서는 Expand와 Compress 각각에서 검정 0·흰색 255가 디코드 허용 오차 내로
프레임 편집 PNG와 MP4 재디코드 결과에 남는지 확인합니다. 미리보기 Compress의 16/235 클리핑 허용을
저장 결과에 적용하지 않습니다.

Windows 10 22H2 x64에서 Store 설치·업데이트와 zip 실행, 기본 WGC 녹화와 GDI 폴백, 커서 포함·제외,
캡처 제외 창, 영역 모드 녹화 제어창의 작은 영역·전체 화면 녹화 시작과 프레임 내부 클릭 통과,
팔레트 위아래 전환·녹화 시작·DPI 변경 후 영역 갱신, 녹화 중 카운트다운·알림·메뉴의 프레임 제외,
모든 테마의 제목 표시줄·핀 모서리, 100/150% DPI, PrtSc 설정 열기, Explorer 경로,
Windows OCR·향상 OCR, MP4/GIF 편집, 스튜디오·전체 범위 영상의 갤러리·편집기 미리보기 색상(두 입력의 보정 판정과 잘린 값의 한계 포함)을 확인합니다. Windows 11에서는 테두리 제거·둥근 모서리·Store 동작 회귀를 확인합니다.
실기 확인 전에는 Windows 10에서 노란 테두리의 프레임 제외와 각 GPU/코덱 조합을 검증 대기 상태로 유지합니다.
