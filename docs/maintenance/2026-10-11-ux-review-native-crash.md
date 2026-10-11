# UX review self-test 네이티브 충돌 조사 기록 (2026-10-11)

판정: **원인 확인, 앱 수정 적용, 실제 사용에도 영향 있음**. 원인은 .NET WPF 합성 엔진(wpfgfx_cor3)의 리스너 집합 결함입니다. 앱은 공유 테마 브러시를 제자리에서 다시 칠하지 않도록 바꿔 이 결함이 충돌로 이어지는 경로를 막았습니다.

## 대상과 환경

- 조사 소스: PR #72 head `5755f51` (`fix/realdevice-qa-round1`). 수정은 그 뒤 병합된 `main` `dbaff2f` 위의 `fix/theme-brush-swap-crash`로 옮겼습니다.
- 명령: `MyCapture.exe --selftest-ux-review <outdir>` (출력은 `<outdir>`가 아니라 `%TEMP%\MyCapture-ux-review-*`에 생성됩니다)
- Windows 11 Home 10.0.26200 (UBR 9457), ucrtbase.dll 10.0.26100.9444
- .NET 10.0.12, Microsoft.WindowsDesktop.App 10.0.12, wpfgfx_cor3.dll 10.0.1226.42308
- GPU: NVIDIA GeForce RTX 5080 (32.0.16.1088), AMD Radeon Graphics (32.0.21030.2001), Easy&Light Display HUB 가상 디스플레이
- 도구: Sysinternals procdump64 12.01, WinDbg 1.2610.1001.0의 cdb, dotnet-dump 10.0.750501 (모두 Microsoft 서명 확인)

## 증상

| 시각 | 빌드 | 이벤트 | 내용 |
|---|---|---|---|
| 2026-10-10 03:07:07 | `integration` worktree | Application Error 1000 / APPCRASH | wpfgfx_cor3.dll 10.0.1226.42308, 0xC0000005, offset 0xf0212 |
| 2026-10-11 04:06:53 | `qa-integration` (PR #72 15d36cd) | Application Error 1000 / BEX64 | ucrtbase.dll 10.0.26100.9444, 0xC0000409, offset 0xa527e, P9=7 |

두 실행의 출력 폴더(`MyCapture-ux-review-oatmf0bc.dch`, `MyCapture-ux-review-i34mtnlf.ppk`)는 모두 파일 49개, 마지막 파일 `gallery-compact-high-contrast-layout.json`에서 멈췄습니다. 갤러리 테마 순회가 끝나고 다음 테마를 적용하는 지점입니다. 10-10 빌드는 EditorCursors(10-11 03:43 도입), WicGifFrameEncoder(10-10 21:38), WGC 기본값(10-11 00:56)보다 이르므로 이 세 변경은 원인이 아닙니다.

`ucrtbase+0xa527e`는 `ucrtbase!abort+0x4e`(`int 29h`, FAST_FAIL_FATAL_APP_EXIT)입니다. `wpfgfx_cor3+0xf0212`는 객체의 vtable에서 `[vtable+0x40]`을 읽는 명령입니다.

## 재현

수정 전 빌드(`bin\Release`, MyCapture.dll SHA-256 `f70b885b…`)로 `MYCAPTURE_DATA_ROOT`, `TMP`, `TEMP`를 실행마다 `%LOCALAPPDATA%\Temp\mcux\<run>`로 격리해 반복 실행했습니다. 작업용 폴더 경로가 길면 Media Foundation `MFCreateSinkWriterFromURL`이 MAX_PATH를 넘어 0x80070003으로 실패하므로 짧은 경로를 썼습니다.

```
procdump64.exe -accepteula -nobanner -ma -e -x <dumps> MyCapture.exe --selftest-ux-review <out>
DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=4 MyCapture.exe --selftest-ux-review <out>
```

| 방식 | 실행 | 충돌 | 증상 |
|---|---|---|---|
| procdump 부착, 레인 A | 40 | 0 | |
| procdump 부착, 레인 B | 16 | 1 | `b-200147-16`: Unhandled C0000005, wpfgfx_cor3+0xf0212, 804 MB 덤프 |
| 디버거 없음, 레인 C | 11 | 1 | `c-200204-11`: 종료 코드 -1073740791 (0xC0000409), WER P9=0xA (FAST_FAIL_GUARD_ICALL_CHECK_FAILURE) |
| 디버거 없음, 보류 모드 `hold-1` | 1 | 1 | WER BEX64 ucrtbase.dll offset 0xa527e, P9=7 (04:06과 같은 서명) |
| 디버거 없음, 기준 렌더 `base-1~5` | 5 | 1 | `base-1`: 종료 코드 -1073740791 (0xC0000409) |
| 디버거 없음, 교차 실행의 수정 전 빌드분 | 24 | 3 | 0xC0000409 2회, 0xC0000005 1회(createdump 덤프: 같은 `NotifyOnChanged` 가상 호출에서 쓰레기 함수 주소 `0x7ffa8a6b9d60`으로 점프) |

디버거 없는 수정 전 실행은 41회 중 6회(약 15%) 충돌했고, procdump를 붙이면 56회 중 1회였습니다. 네 증상(데이터 AV, 실행 AV, CFG fast fail 0xA, abort 7)을 모두 수정 전 빌드에서 재현했습니다. 모든 충돌은 갤러리 창의 테마 적용 시점에 났습니다.

## 덤프 분석 (`b-200147-16`)

- 충돌 스레드는 WPF 렌더 스레드(8번)이고 UI 스레드는 `UxReviewSelfTest.Pump`의 `Dispatcher.PushFrame`에서 대기 중이었습니다.
- 스택: `wpfgfx+0xf0212` ← `+0xf02c4`(같은 함수의 재귀 호출) ← `+0xd9eac`(리소스 갱신 처리) ← `+0xf24b3`(명령 분배, 리소스 타입 0x4B) ← 렌더 스레드 루프.
- 리소스 타입 0x4B는 PresentationCore의 `DUCE.ResourceType.TYPE_SOLIDCOLORBRUSH`입니다.
- 함수 구조는 dotnet/wpf `CMilSlaveResource::NotifyOnChanged`와 같습니다. 재진입 카운터, `OnChanged`(vtable+0x40) 가상 호출, 리스너 멀티셋 순회로 이루어져 있습니다.
- 갱신 중인 브러시는 RTTI가 `.?AVCMilSolidColorBrushDuce@@`이고 색이 scRGB (0.0284, 0.0578, 0.0437), 즉 #2F443B입니다. 갤러리 Workspace 덮어쓰기 `Text.Muted`이며, Glass→Workspace 테마 적용 직후입니다.
- 이 브러시의 리스너 멀티셋은 항목 194개, 용량 211, 정렬 비트와 제거 표식 49개 상태입니다. 해제된 객체 `0x2b39924f980`(vtable 자리 `0x01fffc0000001ff8`)이 **살아 있는 항목 5개와 제거 표식 9개**로 남아 있습니다. 14번 등록되고 9번만 해제된 채 파괴된 리스너(`CMilSlaveRenderData`)입니다.

## 근본 원인: dotnet/wpf `ptrset.cpp`

`src/Microsoft.DotNet.Wpf/src/WpfGfx/core/common/ptrset.cpp`(2019-10-18 공개 이후 변경 없음)의 `CPtrMultisetBase`는 150개를 넘으면 정렬 후 이진 탐색으로 제거하고, 제거한 항목에는 하위 비트 `3`을 표시합니다. 결함은 다음 순서로 드러납니다.

1. 150개 초과 상태에서 제거하면 정렬 비트가 켜집니다.
2. 제거 표식이 30%를 넘으면 압축되고, 150개 이하로 줄어도 정렬 비트는 남습니다.
3. 150개 이하의 선형 제거는 마지막 원소를 빈자리로 옮겨 순서를 깹니다(`pElements[pos] = pElements[cEntries]`). 이때 정렬 비트를 지우지 않습니다.
4. 이후 추가가 기존 마지막 원소보다 큰 주소로만 이어지면 정렬 비트가 유지된 채 150개를 다시 넘습니다.
5. 다음 제거는 정렬되지 않은 배열을 이진 탐색해 실제로 있는 리스너를 찾지 못하고 `false`를 반환합니다. `UnRegisterNotifierInternal`은 조용히 넘어가고, 리스너는 파괴되지만 브러시 목록에는 남습니다.
6. 그 브러시가 다시 칠해지면 `NotifyOnChanged`가 해제된 리스너를 호출합니다. 메모리 상태에 따라 AV, CFG fast fail, `_purecall`→`abort`로 끝납니다.

같은 알고리즘을 C#으로 옮긴 시뮬레이션에서, 압축 후 105개, 선형 제거 후 `sortedBit=True actuallySorted=False`, 151개로 재성장한 뒤 어긋난 원소의 `Remove`가 `False`를 반환하고 그 원소가 살아 있는 리스너로 남는 것을 확인했습니다.

## 실제 사용 영향

있습니다. 결함이 충돌로 이어지려면 리스너가 150개를 넘는 공유 브러시가 다시 칠해져야 합니다. MyCapture는 테마를 바꿀 때 `ThemeService.ApplyResources`와 `WorkspaceTheme.Apply`에서 공유 토큰 브러시를 `SetCurrentValue`로 제자리 변경했습니다. 설정 창의 테마 미리보기와 저장, 시작 시 테마 적용이 이 경로입니다.

UX review 고정 데이터로 모든 창을 띄운 상태의 덤프(`dotnet-dump collect --type Full`)에서 리스너 수는 다음과 같았습니다.

- 앱 전역 `Text.Primary` 605, 앱 전역 `Text.Muted` 238, 앱 전역 `Text.Secondary` 136
- 갤러리 `Text.Muted` 155(정렬 비트와 제거 표식 21개)
- 갤러리·동영상 편집기 창을 띄우지 않은 측정에서도 앱 전역 브러시가 605, 238로 150을 넘었습니다.

한국어 자동 줄바꿈 텍스트는 글리프 런이 많아 렌더 데이터 하나가 같은 브러시를 수십 번 등록합니다. 그래서 일반 창에서도 기준을 넘습니다. 상주 앱이 설정 창 사용(탭 전환, 크기 조정) 뒤 테마를 바꾸면 같은 충돌이 날 수 있습니다. self-test는 창마다 테마 6종 × 크기 2종을 실시간 전환하므로 발생 빈도가 높을 뿐입니다. 같은 제자리 변경 코드가 `main`과 배포된 v4.0.0~v4.0.2에도 있습니다.

## 수정

- `Themes/ThemeBrushes.cs` 추가: `Recolored`는 고정된 새 브러시를 만들고, `Follow`는 호출자 전용 브러시를 만들어 테마를 따라가게 합니다. `Refresh`는 고정되지 않은 전용 브러시만 다시 칠합니다.
- `ThemeService.ApplyResources`, `WorkspaceTheme.Create/Apply`: 토큰 브러시를 제자리 변경하지 않고 새 고정 브러시로 교체합니다. 창별 복제본의 바인딩은 제거했습니다.
- 브러시를 잡아 두던 도우미(`AnnotationEditorControl.Brush`, `AnnotationEditorSurface.Resource`, `OcrResultWindow.ResourceBrush`, `RecordingControlWindow.TryBrush`, `RecordingWallClockWindow.TryBrush`, `TimedTextOverlayDialog.ResourceBrush`, `VideoEditorWindow.TryBrush`)와 `PinWindow` 피드백 문구는 `ThemeBrushes.Follow`를 씁니다. `GalleryEditorWindow` 배너는 `SetResourceReference`를 씁니다.
- `TwoLineTimeline`과 `VideoLayerTimeline`은 바꾸지 않았습니다. 펜을 `Freeze`하므로 일부만 따라가면 밝은 배경에 흰 눈금이 남아 수정 전보다 나빠지기 때문입니다. 교체 방식에서는 잡아 둔 고정 브러시가 다시 칠해지지 않으므로 충돌 위험도 없습니다.

## 검증

| 항목 | 명령 | 결과 |
|---|---|---|
| 빌드 | `dotnet build src/MyCapture.App/MyCapture.App.csproj -c Release` | 경고 0, 오류 0 |
| 앱 테스트 | `dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Release` | 통과 1883, 건너뜀 7, 실패 0 (3분 37초) |
| 새 테스트 | `ThemeBrushesTests` 4개 | 통과. 첫 테스트는 제자리 변경하던 이전 코드에서 `IsFrozen` 단언으로 실패합니다 |
| 렌더 결정성 | 수정 전 `base-2`~`base-5` PNG 452장 상호 비교 | 차이 0 |
| 수정본 렌더 | `fix-3`, `fix-4` 대 수정 전 | 422장 동일, 30장 차이(주석 편집기의 실시간 전환 테마만). 보고서 문구는 UTC 줄 외 동일. `fix-3` 대 `fix-4` 차이 0 |
| 주석 편집기 차이 판정 | 테마별로 창을 새로 연 렌더와 비교 | Glass·Daylight·GlassLight·Workspace에서 수정본 6/6 일치, 수정 전 0/6. 수정 전 코드의 실시간 전환 색 잔류가 함께 고쳐졌습니다. HighContrast는 둘 다 0/6(생성 시점 키 선택 차이, 수정 전부터) |
| 충돌 교차 실행 | 3레인에서 수정 전 빌드와 수정본을 번갈아 실행, 디버거 없음, createdump 사용 | 수정 전 24회 중 충돌 3회(0xC0000409 2회, 0xC0000005 1회), 수정본 24회 중 0회 |
| 수정본 추가 반복 | 3레인 수정본 전용, 디버거 없음 | 36회 모두 종료 코드 0, `RESULT: PASS` |
| 합계 | 디버거 없는 실행 | 수정 전 41회 중 충돌 6회, 수정본 64회 중 0회(Fisher 정확 검정 한쪽 p ≈ 0.003) |

위 결과는 PR #72 head `5755f51` 기준입니다. `main` `dbaff2f`로 옮긴 뒤 다시 확인한 결과는 다음과 같습니다.

| 항목 | 결과 |
|---|---|
| 빌드 | 경고 0, 오류 0 |
| 앱 테스트 | 통과 2002, 건너뜀 7, 실패 0 (4분 30초) |
| 렌더 비교(PNG 446장) | 수정 전 `main`과 416장 동일, 30장 차이(주석 편집기의 실시간 전환 테마만, 위와 같은 범주). 수정본 2회 상호 차이 0. 보고서 문구 동일 |
| 교차 실행(디버거 없음, 3레인) | 수정 전 `main` 9회, 수정본 9회 모두 종료 코드 0. 이 표본에서는 수정 전 빌드도 충돌하지 않았습니다(재현율 약 15%면 9회 연속 통과 확률 약 24%). 충돌 경로인 제자리 변경 코드는 `main`에 그대로 있습니다 |

## 하지 않은 일

- WER LocalDumps는 `HKLM` 설정과 관리자 권한이 필요해 설정하지 않았습니다. procdump와 .NET createdump로 대신했습니다.
- dotnet/wpf에 결함을 보고하지 않았습니다. 공개 게시라서 별도 승인이 필요합니다. 아래 초안을 남깁니다.
- `MyCapture.Core.Tests`는 실행하지 않았습니다. Core 코드는 바뀌지 않았습니다.
- 수정 전부터 있던 동작은 그대로 두었습니다. 동영상 편집기를 연 채 테마를 바꾸면 타임라인과 레이어 줄이 처음 색에 남고, HighContrast 주석 편집기의 이미지 테두리 색이 새로 연 창과 다릅니다.

## dotnet/wpf 보고 초안 (영문, 미게시)

> **CPtrMultisetBase::Remove leaves a stale "sorted" flag, so listeners leak and NotifyOnChanged calls freed render data**
>
> `ptrset.cpp`: the linear branch of `Remove` (count ≤ 150) overwrites the removed slot with the last element but keeps the sorted flag set by an earlier sparse-mode `Sort`/compaction. When the set grows past 150 again with ascending appends (`Add` only compares with the previous last element), `Remove` trusts the flag, `bsearch_lastoccurence` misses an existing entry, and `UnRegisterNotifierInternal` silently keeps the listener (and its AddRef). After the listener (`CMilSlaveRenderData`) is destroyed, the next update of the notifier (e.g. a `SolidColorBrush` colour change) runs `CMilSlaveResource::NotifyOnChanged` over the freed object: AV in wpfgfx_cor3, CFG fast fail (0xC0000409, code 0xA) or `_purecall` abort (code 7). Observed on .NET 10.0.12 (wpfgfx_cor3 10.0.1226.42308) with a shared text brush that had 194 listener entries; the dump shows the freed listener five times untagged and nine times tagged. Fix: call `SetIsDataSorted(false)` in the linear removal path, where an element is moved.
