# 라운드 2 — 심미성

대상: Release 3.0.0, 커밋 `117b0ef`. 페르소나: aesthetics.
제품 소스는 열기만 했다. 점수는 라운드 1 그대로다. 이 문서는 CL-05, CL-12, AES-04/06/10/15와 대비·고대비·모션의 소유만 닫는다.

## 1. 점수

**4 / 5 를 유지한다.**

| 판단 | 라운드 1 | 라운드 2 | 근거 |
|---|---|---|---|
| 심미성 점수 | 4 / 5 | 4 / 5, 유지 | 자정·글래스에서 행동 색은 시안이고 캡처가 주인공인 화면이 있다. 출고 마크, 글래스 알파, 작업 공간 틸, 녹화 상태 색이 MASTER 위계와 어긋난다. |
| 대비 비율 | 이 축의 감점 아님 | 동일 | 본문 4.5:1, 일차 7:1, 포커스 3:1, `Border.Subtle` 1.47–1.67:1은 accessibility. |
| 커서 고리 | 토큰 밖 색만 위계 감점 | 동일. 존재·비용은 점수 4에 없음 | `DrawCursorHighlight`의 COLORREF. 고리가 있는 사실과 프레임당 GDI 시간은 이 점수가 아니다. |

접근성 점수 3이 이미 깎은 항목을 여기 4에서 한 번 더 깎지 않는다. 반대로 AES-04/06/10/15의 위계 감점을 접근성 3에 다시 넣지 않는다.

## 2. CL-05 — 커서 색이 프레임에 구워짐

**합의.** 토큰 밖 색이 캡처 프레임 픽셀에 구워지는 갈래는 심미성 단독이다. 존재·비용·색을 한 주장으로 합치지 않는다.

AES-09의 “겹치는 축 없음”은 색 갈래에만 맞다. **병합:** AES-09를 아래 색 행으로 좁힌다. 고리의 존재와 프레임당 비용은 AES-09에서 뺀다.

| 갈래 | 결정 | 주 소유 | 심미가 채점하는가 | 근거 |
|---|---|---|---|---|
| 존재. 녹화 프레임에 강조 링이 있고, 왼쪽 버튼에 따라 반지름·펜 두께가 바뀐다. | 합의 | functionality | 아니오. 링은 설명용 녹화의 시각 장치다. 있음이 심미 감점이 아니다. | `ScreenCaptureEngine.cs` 491–494행, 508–509행. `CaptureInto` 360–362행은 `_includeCursor && CursorHighlight`일 때만 그린다. 정지 캡처는 이 플래그를 켜지 않는다 (339–342행). |
| 비용. 프레임마다 GDI가 더해지고 시간은 미측정이다. | 합의 | performance | 아니오. | `DrawCursorHighlight` 495–536행의 `GetCursorInfo`, `CreatePen`, `Ellipse`. 성능 라운드 2가 이 갈래를 소유하고 색은 채점하지 않는다고 적었다. |
| 색. 유휴 `#FFC700`, 눌림 `#FF4040`이 토큰 앰버 `#F5B942`, 코랄 `#FF6B74`와 다르고, 그 픽셀이 캡처 프레임에 남는다. | 합의 (AES-09) | aesthetics | 예. 이 갈래만 심미 점수 4의 녹화 상태 위계에 들어 있다. | COLORREF `0x0000C7FF` → `#FFC700`, `0x004040FF` → `#FF4040` (`ScreenCaptureEngine.cs` 510–511행). 펜은 캡처 DC `_memoryDc`에 그려지고, 이어서 `CopyTopDownBgra`가 그 비트를 프레임 버퍼로 복사한다 (360–374행). 주석의 “warm amber / coral”와 실제 값이 다르다. |

심미성 단독이 가리키는 자리:

| 포함 | 제외 |
|---|---|
| 구워진 링의 색이 MASTER 상태 색(Capturing 앰버, Error 코랄)과 다른 것. | 링이 클릭을 보이게 한다는 기능. |
| 색이 창 크롬이 아니라 녹화 픽셀이라, 이후 테마를 바꿔도 이미 찍힌 프레임은 토큰을 따라가지 않는 것. | 프레임당 펜 생성 비용. |
| 녹화 중 화면 프레임이 시안으로 남는 AES-08. 그쪽은 창 테두리 `Border.Accent`이고 캡처 DC에 굽지 않는다 (`RecordingControlWindow.cs` 174, 202–205행). AES-08과 AES-09를 한 감점으로 합치지 않는다. | 링 대 임의 바탕의 대비 숫자. 바탕이 데스크톱 픽셀이라 팔레트 쌍이 아니다. accessibility의 4.5:1·3:1 표에 넣지 않는다. |

## 3. CL-12 — 대비 숫자와 브랜드 위계

**합의.** 측정된 텍스트 대비와 OS 고대비 미연동은 accessibility. 앱 고대비에서 악센트가 경고와 같은 견본인 것, 그리고 뮤트 글자 위계는 aesthetics. 한 사실을 두 점수에서 깎지 않는다.

| 사실 | 결정 | 주 소유 | 심미 점수 4 | 접근성 점수 3 |
|---|---|---|---|---|
| 전 팔레트 본문 4.5:1, 일차 텍스트 7:1. 포커스 대 베이스는 Midnight 11.83, Daylight 3.44, HighContrast 14.27, Workspace 10.06. | 합의 (A-09) | accessibility | 비율을 감점·가점으로 다시 세지 않음. 합격은 접근성의 강점. | 텍스트·포커스 비율의 합격과 테스트 범위를 소유. |
| Midnight `Border.Subtle` 1.67:1, Daylight 1.47:1, `Border.Strong` 약 2.6:1. | 합의 (A-09) | accessibility | 휴식 경계의 비텍스트 숫자를 위계 감점으로 올리지 않음. | 3:1 미달의 감점. |
| OS 고대비는 캡션·콤보·스크롤에만 닿고 `ThemeService`는 팔레트를 바꾸지 않음. | 합의 (A-10) | accessibility | OS 연동 부재를 4점의 감점 항으로 세지 않음. | 수동 테마와 OS 모드가 한 벌이 아닌 감점. |
| 앱 고대비에서 악센트·포커스·경고가 `#FFD000`. | 합의 후 AES-15로 병합 | aesthetics | 행동·포커스·경고가 한 견본인 위계 감점. | 같은 노랑의 14.27:1 합격은 접근성 강점. 견본이 같다는 이유로 한 번 더 깎지 않음. |
| 자정 실행 글자 3단이 토큰보다 좁다. Primary `#FFFFFF`, Secondary `#ECF1F9`, Muted `#D5DEEA`. | 합의 (AES-06) | aesthetics | 단계가 평평한 위계 감점. | 그 색이 바탕과 4.5:1을 넘는지는 접근성. 라운드 1은 텍스트 쌍 합격을 적었다. 램프가 좁다는 이유로 재감점하지 않음. |

고대비 노랑은 두 문장으로 나뉜다. 포커스 대 검정의 14.27:1은 가시성 합격이고 accessibility가 가진다. `Accent.Default`, `Border.Focus`, `Border.Accent`, `State.Warning`이 모두 `#FFD000`인 것(`AppTheme.cs` 204–214행)은 위계이고 aesthetics가 가진다. 대비가 높다는 사실이 위계 감점을 지우지 않고, 위계 감점이 대비 합격을 지우지 않는다.

## 4. AES-04 / 06 / 10 / 15 — 이중 감점 금지

### AES-04 작업 공간 틸과 흰 `Text.OnAccent`

**합의.** 주 소유는 aesthetics. 접근성은 이 주장을 재감점하지 않는다.

| 조각 | 결정 | 주 소유 | 근거 |
|---|---|---|---|
| 악센트가 시안 `#58C7F3`이 아니라 틸 `#5AD9C5` / `#167768`이다. | 합의 | aesthetics | `WorkspaceTheme.cs` 80–88행. MASTER 14행의 시안과 다르다. |
| 밝은 작업 공간 `Text.OnAccent`가 `#FFFFFF`이다. | 합의 | aesthetics | `WorkspaceTheme.cs` 79행. `Tokens.xaml` 8행은 악센트 채움에 어두운 잉크를 두고 흰 글자+시안을 금한다. 자정 작업 공간은 `#102B2B`라 그 규칙을 지킨다 (79행). |
| 밝은 쪽 `Timeline.TextLayer`가 악센트와 같은 `#167768`이다. | 합의 | aesthetics | `WorkspaceTheme.cs` 80행과 92행. 레이어와 재생헤드가 한 색이다. |
| 흰 글자 대 `#167768`의 WCAG 비율. | 합의 | accessibility | 숫자를 이 축에 적지 않는다. 접근성 라운드 1은 전 팔레트 본문 4.5:1·일차 7:1 합격을 적었고, 이 쌍을 별도 실패로 감점하지 않았다. 비율이 나중에 미달로 확인되면 그 숫자만 accessibility가 깎는다. |

### AES-06 자정 글자 램프

**합의.** 위계는 aesthetics, 비율 숫자는 accessibility.

| 역할 | 토큰 (`Tokens.xaml` 101–103행, MASTER 26행) | 자정 실행 (`AppTheme.cs` 103–105행) | 심미가 채점하는 것 |
|---|---|---|---|
| Text.Primary | `#F6F8FC` | `#FFFFFF` | 3단의 간격이 문서보다 좁다. |
| Text.Secondary | `#C6D0DF` | `#ECF1F9` | 본문과 보조가 거의 같은 밝기다. |
| Text.Muted | `#8E9CAF` | `#D5DEEA` | 뮤트가 보조 쪽으로 올라와 위계가 평평하다. |

글래스는 이 램프를 재정의하지 않고 자정을 물려받는다 (`AppTheme.cs` `GlassColors`). `ThemeService.ApplyResources`가 브러시를 바꾸므로 화면 값은 XAML 기본값이 아니다.

접근성이 소유하는 것은 이 세 색과 표면의 대비 비율이다. `Border.Subtle` 1.67:1은 다른 쌍(A-09)이라 AES-06과 합치지 않는다. 뮤트 램프 압축을 접근성 점수에서 다시 깎지 않는다.

### AES-10 `DeepSkyBlue` 고정

**합의.** 토큰 이탈은 aesthetics 단독 감점이다. 비텍스트 대비 숫자와 포커스 가시성은 accessibility다. 접근성 라운드 1은 이 파랑의 비율을 감점 근거로 쓰지 않았다.

| 조각 | 결정 | 주 소유 | 근거 |
|---|---|---|---|
| 선택 핸들 펜과 모서리 핸들이 `Brushes.DeepSkyBlue` / `Brushes.White`라 작업 공간 틸, 주간 시안, 고대비 금색을 따라가지 않는다. | 합의 | aesthetics | `VideoLayerCanvas.cs` 83–87행. |
| 도형 레이어 비트맵이 `FromArgb(110, 30, 160, 255)`와 `DeepSkyBlue`로 구워진다. | 합의 | aesthetics | `VideoLayerAssets.cs` 16–21행. `RenderTargetBitmap`이라 이후 테마 변경이 그 픽셀을 다시 칠하지 않는다. 커서 링과 같이 “구워진 토큰 밖 색”은 심미 위계다. |
| 키보드 포커스 때 펜 두께가 1에서 2로 는다. | 합의 | accessibility | `VideoLayerCanvas.cs` 83행. 포커스가 보이는지는 접근성. 라운드 1은 이 두께 변화를 포커스 표시가 있는 쪽으로 적었다. 두께를 심미 감점으로 쓰지 않는다. |
| 그 파랑의 비텍스트 3:1. | 합의 | accessibility | 고대비 팔레트 위에서의 비율은 접근성 숫자다. 현재 접근성 표에는 `Border.Focus`와 `Border.Subtle`만 있고 DeepSkyBlue 비율은 없다. 없는 숫자를 심미 감점으로 만들지 않는다. |

### AES-15 앱 고대비의 한 견본과 OS 고대비

**병합.** 인박스의 AES-15는 두 사실이다. 위계만 aesthetics에 남기고, OS 미연동은 A-10으로 넘긴다.

| 조각 | 결정 | 주 소유 | 감점 |
|---|---|---|---|
| 앱 테마 `HighContrast`에서 `Accent.Default`, `Accent.Cool`, `Border.Focus`, `Border.Accent`, `State.Warning`이 `#FFD000`이다. | 병합 → aesthetics | aesthetics | 심미 점수 4의 위계 감점. 경고와 기본 행동이 같은 견본이다 (`AppTheme.cs` 204–214행). 텍스트 레이어 `#80C0FF`, 프레임 `#D0B0FF`는 금색과 갈라져 있으므로 그 둘은 이 감점에 넣지 않는다 (221–222행). |
| 고대비 팔레트의 텍스트 4.5:1·포커스 14.27:1. | 병합 → accessibility | accessibility | 합격은 접근성 강점. 심미가 비율 미달로 다시 깎지 않는다. |
| OS `SystemParameters.HighContrast`가 캡션과 콤보·스크롤에만 연결되고, `ThemeService.Apply`는 팔레트를 고대비로 바꾸지 않는다. | 병합 → A-10 | accessibility | 접근성 점수 3의 감점. 심미 점수 4의 이유(“마크·글래스·작업 공간·녹화 상태”)에 이 항을 넣지 않는다. |

## 5. 모션과 포커스 색 — 같은 경계

대비·고대비 표와 같이, 모션도 준수와 톤을 나눈다.

| 사실 | 결정 | 주 소유 | 채점 |
|---|---|---|---|
| `Motion.Fast` 83ms, `Normal` 167ms가 버튼 눌림·창 등장·핀 페이드에 연결된다. `Deliberate` 250ms는 호출처가 없다. 캡처 오버레이 등장은 꺼져 있다. | 합의 (AES-13) | aesthetics | 토큰이 쓰이는 방식과 250ms의 미사용. 감점은 P2 정리이고 점수 4를 3으로 내리지 않는다. |
| `FluidMotion.AnimationsEnabled`가 제스처 시점에 `SystemParameters.ClientAreaAnimation`을 보고, 꺼지면 핀 포함 애니메이션을 건너뛴다. | 합의 (A-11) | accessibility | 감소 모션 준수는 접근성의 강점. 심미 점수는 그 준수를 두 번째 가점으로 올리지 않고, 미준수로 깎지도 않는다. 코드는 `FluidMotion.cs` 71–72행, `PinWindow.cs`가 그 플래그를 본다. |
| 포커스 링의 색이 시안 `#7DD7F8` / 고대비 `#FFD000`인 브랜드 적합성. | 합의 | aesthetics | 고대비에서 그 노랑이 경고와 같은 것은 AES-15 위계에 이미 포함. |
| 포커스 링이 보이는지, 편집기 루트와 두 줄 타임라인에 링이 없는지. | 합의 (A-06, A-07) | accessibility | 가시성과 부재는 접근성. 심미 라운드 1은 공용 컨트롤의 1px 인셋 링을 MASTER와 맞는 쪽으로 적었다. 없는 링을 심미 감점으로 가져오지 않는다. |

## 6. 라운드 2 이후 심미 점수가 계속 소유하는 감점

점수 4에 남는 위계만 적는다. 접근성 숫자와 OS 고대비 연동은 빠진다.

| 감점 | 주장 | 한 번 깎는 축 |
|---|---|---|
| 작업 공간 악센트가 틸이고, 밝은 쪽 악센트 위 글자가 흰색이며, 텍스트 레이어가 그 틸과 같다. | AES-04 | aesthetics |
| 자정·글래스 글자 3단이 토큰보다 평평하다. | AES-06 | aesthetics |
| 레이어 핸들과 도형 비트맵이 `DeepSkyBlue`로 고정되어 테마 밖에 있다. | AES-10 | aesthetics |
| 앱 고대비에서 행동·포커스·경고가 `#FFD000` 하나다. | AES-15 위계 조각 | aesthetics |
| 녹화 프레임에 구워지는 커서 링이 `#FFC700` / `#FF4040`이다. | AES-09, CL-05 색 갈래 | aesthetics |

| 이 축이 채점하지 않는 것 | 소유 |
|---|---|
| 텍스트 4.5:1·7:1, 포커스 3:1, `Border.Subtle`/`Border.Strong`의 비텍스트 비율 | accessibility |
| OS 고대비가 버튼·텍스트·캔버스·핀까지 팔레트를 바꾸지 않는 것 | accessibility (A-10) |
| 감소 모션 설정이 애니메이션을 끄는지 | accessibility (A-11) |
| 커서 링의 존재, 클릭 가독성, 픽셀 골든 테스트 | functionality |
| 커서 링의 프레임당 시간 | performance |
