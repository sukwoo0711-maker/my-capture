# 심미성·비주얼 시스템 — 라운드 1

대상: Release 3.0.0, 커밋 `117b0ef` (`Directory.Build.props`의 `Version` 3.0.0).
축: aesthetics. 제품 코드는 수정하지 않았다. `docs/market/`는 인용하지 않았다.
`docs/design/warm-yellow-charcoal-ux.md`는 파일 첫머리에서 **superseded**를 선언한다. 권위 문서는 `design-system/mycapture/MASTER.md`다.

점수: **4 / 5**

## 1. 점수와 이유

4는 “Focus Portal이 어두운 화면의 행동 색으로 읽히고, 캡처 픽셀을 가운데 두는 구조가 코드와 가이드 렌더에 있다. 다만 출고 마크·글래스·작업 공간·녹화 상태가 문서의 색 위계와 어긋나 상위권으로 닫히지 않는다”는 뜻이다.

스크린샷을 봤다. `docs/guides/shots/`의 PNG 네 장을 Read로 열었다.

| 파일 | 본 것 |
|---|---|
| `library-midnight.png` | 어두운 그래파이트 라이브러리, 카드 선택 테두리와 연필·검사기 **편집**이 시안, 썸네일은 우물 안 |
| `annotation-glass.png` | 가운데 캡처가 크고, 왼쪽 도구 레일은 조용하며, **완료**만 시안 채움 |
| `video-editor-glass.png` | 미리보기가 위 주인공, 아래는 개요+세부 타임라인, 산호 트림, 시안 재생헤드, 파란 텍스트 바 |
| `settings-glass.png` | 헤더 아이콘, 사이드바 선택 막대, **적용** 시안. 콤보 값은 **글래스 · Windows 기본**. 패널은 불투명에 가깝게 보임 |

이 PNG들은 라이브 3.0.0 바탕화면 촬영이 아니다. 라이브러리 제목 표시줄이 `MyCapture-2.3.7 — 라이브러리`다. `AppIdentity.FormatWindowTitle`이 어셈블리 버전을 붙이므로, 이미지는 2.3.7 시점의 `--selftest-ux-review` 렌더로 보는 것이 맞다. 레이아웃은 현재 `GalleryWindow.xaml`, `AnnotationEditorControl.cs`, `VideoEditorWindow.cs`/`TwoLineTimeline.cs`, `SettingsWindow.xaml`과 대응한다. 글래스 라이트·작업 공간·주간·고대비 화면은 이 네 장에 없다. DWM Mica는 오프스크린 렌더로 확인할 수 없다.

아이콘도 봤다. `app.ico`와 `tray-*.ico`를 래스터로 풀어 색 분류를 했다. `mycapture-mark.svg`와 Fluent SVG 두 장도 읽었다.

## 2. 잘 지키는 원칙 / 어긋나는 화면

### 지키는 원칙

- **행동 색은 시안 하나.** 자정·글래스 `Accent.Default`는 `#58C7F3` (`AppTheme.cs` 108, 262행대 Midnight을 글래스가 상속). 가이드의 라이브러리·주석·영상·설정에서 채움 버튼이 그 역할이다. `Controls.xaml` 163–166행 `Button.Primary`는 `Accent.Default` + `Text.OnAccent`. 포커스는 두께를 바꾸지 않고 인셋 링이다 (129–148행). MASTER의 “레이아웃이 밀리지 않는 1px 포커스”와 같다.
- **표면 사다리는 토큰 키로 흐른다.** `GalleryWindow.xaml`과 `SettingsWindow.xaml`의 배경·글자·테두리는 `Surface.*` / `Text.*` / `Border.*`다. XAML 속성 리터럴 `Color="#…"` / `Background="#…"` 검색은 `Themes/Tokens.xaml` 밖으로 나오지 않았다.
- **캡처가 주인공인 화면이 있다.** 주석 편집기는 뷰포트가 `Surface.Sunken`이고 도구·검사기는 옆이다 (`AnnotationEditorControl.cs` 1136행 근처, 가이드 `annotation-glass.png`). 핀은 이미지를 `Stretch.Fill`로 두고 크롬은 1px이다 (`PinWindow.cs` 109–146행). 호버만 `Accent.Cool`로 올라간다 (364–369행). 창 등장은 끈다 (101행). 캡처 오버레이도 등장을 끄고 (`CaptureOverlayWindow.cs` 52행) 선택선은 `Overlay.SelectionBorder`다 (`CaptureOverlayView.cs` 184행).
- **타임라인 위계는 설계안이 코드에 있다.** `docs/design/video-editor-timeline-ux.md` 머리말은 “단일 스크러버”라고 적지만, 구현은 `TwoLineTimeline.cs`다. 개요 높이 34, 세부 42 (24–25행). 개요는 굵은 눈금·뷰포트 브러시·바깥 스크림 (745–806행). 세부는 프레임 눈금 (835–881행). 연결선과 캡션이 있다 (815–830행). 삭제 구간은 `Timeline.TrimDelete*` 채움 + 해치 + 폭 48px 이상이면 라벨 (946–979행). 레이어 행은 텍스트/프레임 색이 다르고, 프레임만 해치, 선택 바는 재생헤드 펜이다 (`VideoLayerTimeline.cs` 258–259, 286–287, 331–350행). 색만으로 구분하지 말라는 MASTER 문장에 라벨·해치가 붙어 있다.
- **모션 토큰이 눌림과 등장에 연결된다.** `Tokens.xaml` 195–197행 `Motion.Fast` 83ms, `Normal` 167ms, `Deliberate` 250ms. `FluidMotion`은 투명도와 변환만 다루고 `SystemParameters.ClientAreaAnimation`을 제스처 시점에 본다. 버튼 스타일이 `PressFeedback`을 켠다 (`Controls.xaml` 113행). 핀 페이드도 같은 Fast/Normal을 쓴다 (`PinWindow.cs` 317, 382, 810행).
- **트레이 상태 색은 MASTER와 일치한다.** 래스터 최빈 불투명색: idle `(88,199,243)` = `#58C7F3`, capturing `(245,185,66)` = `#F5B942`, busy `(69,214,162)` = `#45D6A2`, error `(255,107,116)` = `#FF6B74`. 판의 구멍은 `#0B0F17`.
- **글자·간격 토큰이 문서와 같다.** `Tokens.xaml` 184–189행 12/13/14/17/24/28, 155–161행 4/8/12/16/24/32, 히트 36과 컴팩트 32 (204–205행). 라이브러리 아이콘 버튼은 `Button.Icon.Compact` (26행). 주석 색 견본은 40×40 (`AnnotationEditorControl.cs` 1237–1238행).

### 어긋나는 화면

**브랜드 마크가 세 갈래다.**

- MASTER 8–13행과 `Assets/mycapture-mark.svg` 10–14행: 그래파이트 둥근 사각, 뒤 판 시안 `#58C7F3` 스트로크, 앞 판 `#F6F8FC` 스트로크.
- 저장소 전체에서 `mycapture-mark` 문자열이 XAML/CS/csproj에 없다. 창과 헤더가 쓰는 것은 `Controls.xaml` 26행, 774–776행 `Brand.AppIcon` → `Assets/app.ico`.
- `app.ico` 256px를 32×32로 색 분류하면 시안 픽셀이 없고, 흰색 둥근 사각형 하나(`W`)가 판 위쪽에 있다. 두 개의 어긋난 판이 아니다. 라이브러리·설정 헤더에 보이는 마크는 이 파일이다 (`GalleryWindow.xaml` 436행, `SettingsWindow.xaml` 221행).
- 트레이 네 장은 상태 색으로 채운 둥근 사각과 가운데 그래파이트 구멍이다. 두 판 마크가 아니고, 16px에서 “구멍 난 색 판”으로 읽힌다. 상태 색은 맞고 형태는 문서와 다르다.

**런타임 글자 램프가 MASTER/Tokens보다 평평하다.** 대비 비율 숫자는 접근성 축으로 넘긴다. 여기에는 단계 차이만 적는다.

| 역할 | MASTER / `Tokens.xaml` | 자정 `ThemeCatalog`가 실행 중 덮어씀 |
|---|---|---|
| Text.Primary | `#F6F8FC` (101행, Warm100) | `#FFFFFF` (`AppTheme.cs` 103행) |
| Text.Secondary | `#C6D0DF` (102행) | `#ECF1F9` (104행) |
| Text.Muted | `#8E9CAF` (103행) | `#D5DEEA` (105행) |

글래스는 이 글자 색을 재정의하지 않아 자정 램프를 물려받는다 (`GlassColors` 262–280행). 가이드의 어두운 화면에서 시간 캡션·보조 문장·본문의 밝기 차이가 문서가 그린 3단보다 좁다. `ThemeService.ApplyResources`가 브러시 색을 바꾸므로, 사용자가 보는 값은 XAML 기본값이 아니다 (`ThemeService.cs` 33–50행).

**갤러리에서 시안이 한 화면의 여러 주인공이다.** MASTER는 악센트를 기본 행동·현재 선택·키보드 포커스에 아낀다. `GalleryWindow.xaml` 126–131행 고정 배지가 `Accent.Default` 채움이고, 198–206행 카드마다 `Button.Primary` 연필이 있으며, 539행과 572행 검사기에도 `Button.Primary`가 있다. `library-midnight.png`에서 선택 링, 카드 연필, 오른쪽 **편집**이 동시에 시안이다. 썸네일은 244×292 타일 안의 우물이다 (34–36, 58–69행). 메타·배지·버튼 다섯 개가 픽셀 주변을 차지한다. 주석 편집기·설정보다 캡처가 덜 주인공이다.

**녹화 중 화면 색이 앰버가 아니다.** MASTER 15행 Capturing `#F5B942`. 영역 프레임은 녹화 전후에 `Border.Accent`(`#58C7F3` 폴백)이고, 포커스일 때만 `Border.Focus`다 (`RecordingControlWindow.cs` 174행, 202–205행). 앰버로 바뀌는 분기가 없다. 앰버는 트레이 `tray-capturing.ico`에만 있다. 녹화본에 구워지는 커서 고리는 주석이 “warm amber / coral”라고 하지만 COLORREF는 `0x0000C7FF` → `#FFC700`, 눌림 `0x004040FF` → `#FF4040`이다 (`ScreenCaptureEngine.cs` 510–511행). 토큰 앰버 `#F5B942`, 코랄 `#FF6B74`와 다른 색이 캡처 픽셀 위에 올라간다.

**영상 선택 크롬이 토큰 밖이다.** `VideoLayerCanvas.OnRender`는 `Brushes.DeepSkyBlue`와 `Brushes.White`를 테마와 무관하게 그린다 (83–87행). `VideoLayerAssets.CreateShape`는 도형 레이어 비트맵을 `FromArgb(110, 30, 160, 255)`와 `DeepSkyBlue`로 굽는다 (16–17행). 가이드 영상 샷의 텍스트 핸들이 시안처럼 보이는 것은 이 고정색일 수 있다. 작업 공간 틸, 주간, 고대비 금색을 따라가지 않는다.

**타임라인 텍스트 레이어 색이 악센트와 같은 푸른 계열이다.** `Tokens.xaml` 136행 `Timeline.TextLayer` `#3B82F6`, 137행 `Timeline.FrameLayer` `#9B7EDE`. 프리미티브 램프에 없는 값이다. 자정 악센트 `#58C7F3`과 텍스트 바가 둘 다 파랑이라, `video-editor-glass.png` 아래쪽 막대가 위쪽 **내보내기** 버튼과 같은 무게로 경쟁한다. 프레임 레이어만 해치가 있어 텍스트 레이어는 색+라벨에 기대고 패턴이 없다 (`VideoLayerTimeline.cs` 259행 `hatch: false`).

**주석 잉크는 두 번째 팔레트다.** 크롬 위반으로 세지 않는다. `warm-yellow-charcoal-ux.md`는 원색을 폴백과 콘텐츠에 한정한다. 견본은 `#EF4444` `#FBBF24` `#34D399` `#3B82F6` `#111827` 흰색 (`AnnotationEditorControl.cs` 1222–1229행)이고 기본 스트로크도 `#EF4444` (`AnnotationEditorController.cs` 80행). 상태 색 `#FF6B74` / `#F5B942` / `#45D6A2` / `#58C7F3`과 다른 잉크다. 가이드 주석 샷의 견본 여섯 개와 일치한다. 캡처 위의 빨간 마크와 창의 시안 버튼이 한 가족이 아니다. 잉크를 브랜드에 묶을 의무는 약하다. 다만 기본 마크 색이 위험 상태 색과도 같지 않다.

**핀 주석만 옛 팔레트다.** `PinWindow.cs` 132–134행, 366–367행 주석은 “warm yellow”라고 적는다. 실행은 `Accent.Cool`이고, 리소스가 없으면 `#7DD7F8`이다 (400–402행). 화면은 시안이다. 주석이 남아 다음 수정이 노랑으로 되돌릴 여지가 있다.

**`Motion.Deliberate`는 정의만 있다.** `FluidMotion.DeliberateDuration` (87–88행)을 호출하는 화면이 없다. 83/167은 쓰인다.

### 하드코딩 색 규모

전수 나열 대신 검색 규모와 표본이다. `src`에서 `bin`/`obj`를 제외했다.

| 패턴 | 범위 | 규모 |
|---|---|---|
| `#[0-9A-Fa-f]{6,8}` | `Tokens.xaml` + `WorkspaceTheme.cs` | 115곳. 팔레트 원천 |
| `ThemeColor.Rgb` / `Argb` | `AppTheme.cs` `ThemeCatalog` | `#` 검색에 안 잡힘. 같은 키의 두 번째 표 |
| `Color.FromRgb` / `FromArgb` | App UI, Themes·Diagnostics 제외 | 주석 11, 캡처 6, 핀 6, 녹화 32, 셸(갤러리/설정/OCR) 8 |
| `Brushes.` (Transparent 제외) | 같은 UI | 녹화·캡처·핀·주석에 분산. 아래 표본 |
| XAML `Color="#` / `Background="#` | `src/MyCapture.App` | `Tokens.xaml`만 |

UI의 `FromRgb` 다수는 `ResolveBrush` / `TryBrush` / `Brush` / `ResourceBrush`의 실패 시 폴백이다. 문서가 허용한 범위다. 폴백이 아닌 것으로 확인한 표본은 위 “어긋나는 화면”의 DeepSkyBlue, 커서 COLORREF, 텍스트 오버레이 배경 `VideoFrameRenderPipeline.cs` 472행 `#C8080808`(고정, `Surface.Scrim` 아님), `ClipboardCodeRenderer.cs` 40–41행 Gainsboro/`#1E1E1E`(코드 핀에 구워지는 콘텐츠), `GalleryVideoPlayerWindow.cs` 84행 검정 레터박스(영상 픽셀), `CaptureOverlayView.cs` 79–80행 흑백 정밀 포인터(어떤 바탕에서도 보이게 고정)다.

팔레트는 세 곳에 있다. `Tokens.xaml` 정적 브러시, `ThemeCatalog`, 작업 공간만의 16진 사전 (`WorkspaceTheme.cs` 67–94행). 이미 글자색과 악센트가 어긋나 있다.

## 3. 테마별 리스크

기본 설정은 글래스다 (`AppSettings.cs` 303행). `ThemeService.Current`의 필드 초기값은 작업 공간이다 (`ThemeService.cs` 13행). 시작 시 `ApplyFromSettings`가 덮는다 (`App.xaml.cs` 180행).

| 테마 | 화면을 봤는가 | 리스크 |
|---|---|---|
| 글래스 | 설정·주석·영상 PNG (오프스크린). 라이브러리 PNG는 파일명이 midnight | 표면 알파가 `0xF4`–`0xF8` (약 96–97%, `AppTheme.cs` 264–279행). Mica를 켜도 (`ModernWindowChrome.cs` 161–166, 192–198행) 패널은 거의 불투명하다. `Controls.xaml` 5–6행은 “Mica가 비친다”고 적고, 설정 힌트(`SettingsWindow.xaml` 268행, 샷의 문구)도 Windows 11 Mica·반투명 패널을 말한다. 샷의 카드는 단색 그래파이트로 보인다. 라이브 Mica는 미확인. |
| 글래스 라이트 | 없음 | 주간 팔레트를 상속하고 표면만 알파 `0xF4`–`0xF8` (`283–297행`). 악센트는 어두운 시안 `#1A8FC2` (주간 156행). 글래스와 같은 “거의 불투명” 문제. 본 화면 없음. |
| 작업 공간 | 없음 | 갤러리는 주간, 영상 편집기는 자정을 바른 뒤 틸로 다시 덮는다 (`WorkspaceTheme.cs` 62–94행). 악센트 `#5AD9C5` / 밝은 쪽 `#167768`. MASTER의 시안이 아니다. 밝은 쪽 `Text.OnAccent`는 `#FFFFFF` (79행). MASTER와 `Tokens.xaml` 8행은 악센트 위 글자를 어두운 잉크로 두고 흰 글자+시안을 금한다. 밝은 작업 공간은 그 규칙을 깬다. 밝은 쪽 `Timeline.TextLayer`가 악센트와 같은 `#167768` (80행과 92행)이라 레이어와 재생헤드가 한 색이다. 재정의하지 않은 키(오버레이 선택선, 위험색, 배지)는 주간/자정에 남는다. 캡션 색은 `ThemeCatalog`의 주간/자정이고 (`ModernWindowChrome.cs` 212–218행) 틸 표면과 다를 수 있다. |
| 주간 | 없음 | 악센트 `#1A8FC2`는 시안을 밝기만 낮춘 적응으로 읽힌다. 글자 3단(`#0B121C` / `#162334` / `#2A394E`, 151–153행)도 자정보다 단계가 좁다. 비율은 접근성. |
| 자정 | 라이브러리 PNG가 이 계열로 보임. 파일명과 제목 버전은 위와 같음 | Focus Portal에 가장 가깝다. 글자 램프가 토큰 파일보다 밝게 압축된다. 카드마다 시안 버튼. |
| 고대비 | 없음 | 악센트·포커스·경고가 모두 `#FFD000` (`AppTheme.cs` 204–214행). 경고와 기본 행동이 같은 견본이다. 텍스트 레이어는 `#80C0FF`, 프레임은 `#D0B0FF`로 남아 금색 악센트와는 구분된다. OS 고대비가 켜지면 캡션만 시스템 색을 두고 (`ModernWindowChrome.cs` 200–203행), 콤보·스크롤 일부만 `SystemColors`로 바뀐다 (`Controls.xaml` 610행 근처). 앱 테마 “고대비”와 OS 고대비가 한 벌이 아니다. 비율 합격은 접근성. |

작업 공간·고대비·주간·글래스 라이트의 완성도는 **코드 팔레트만** 본 것이다.

## 4. 경쟁

이번 세션에서 연 공식 페이지만 적는다. 화면을 직접 보지 못했다. 페이지 추출에 갤러리 이미지가 없으면 “UI가 오래되었다”를 사실로 쓰지 않는다.

- Snipaste `https://www.snipaste.com/` — 문구는 F1로 자르고 F3로 붙이는 떠 있는 창, 확대·투명도·클릭 통과다. 추출 텍스트에 스크린샷 갤러리는 없었다. “단순한 핀”은 그 문구의 인상이다.
- ShareX `https://getsharex.com/` — 캡처·주석·GIF·편집기 기능 목록이다. 크롬이나 스크린샷은 추출되지 않았다. 오래된 UI라는 인상은 이 페이지에서 확인하지 못했다.
- ScreenToGif `https://www.screentogif.com/` — 추출본은 WinUI 계열 마케팅 셸 CSS(액센트 `#60CDFF`, mica 변수)였고 편집기 화면이 아니었다. 앱 크롬은 미확인. 사이트 CSS를 제품 UI로 쓰지 않는다.
- Windows 11 캡처 도구, Microsoft 지원 문서 `https://support.microsoft.com/en-us/windows/use-snipping-tool-to-capture-screenshots-00246869-1843-655f-f220-97299b865f6b` — 캡처 중 화면이 회색으로 바뀌고, 펜·형광펜·도형·텍스트 작업은 캡처 도구 창에 있으며, 영상은 Clipchamp로 넘긴다고 적는다. 색 토큰은 없다. “시스템 일체감”은 이 문서가 말하는 OS 오버레이와 OS 앱의 인상이다.

### 과제 고유 강점

로컬 캡처 → 설명 → GIF/MP4, 무계정, 핀, 비파괴 주석 안에서만 성립한다.

- 라이브러리·주석·영상·설정이 한 그래파이트/시안 크롬을 공유한다. 지원 문서의 캡처 도구는 영상 마무리를 다른 앱으로 넘긴다. MyCapture 샷 두 장(주석, 영상)은 같은 표면·같은 시안 기본 버튼이다.
- 핀은 Snipaste 문구와 같은 “이미지가 창”이다. 여기에 호버 시안, 토큰 피드백, 83/167ms, 감소 모션이 붙는다 (`PinWindow.cs`).
- 트레이 네 상태가 문서의 시안/앰버/에메랄드/코랄과 픽셀이 같다. 캡처 중·바쁨·오류를 색과 아이콘으로 나누고, 툴팁 문자도 있다 (`TrayIconService` 상태 문자열). 색만으로 상태를 말하지 말라는 문장과 맞다.
- 설명용 타임라인에 개요/세부, 해치, 라벨, 공유 재생헤드가 있다. ShareX·ScreenToGif 공식 추출에서는 이런 위계를 확인할 수 없었다. 강점은 “우리 코드와 우리 샷에 있다”까지다.

### 경쟁 우위 (상대가 더 잘하거나, 우리가 약한 것)

- Windows 11 캡처 도구 문서는 선택 순간 화면 전체가 회색이 되는 OS 오버레이다. MyCapture도 딤머가 있으나 (`Overlay.Dimmer`), 출고 기본인 글래스는 Mica를 약속하면서 표면 알파가 96%대라 시스템 재질로 읽히기 어렵다. 일체감은 문서 문구보다 코드가 약하다.
- Snipaste 문구의 핀은 제품의 거의 전부다. MyCapture 핀 크롬은 그에 가깝게 조용하지만, 갤러리로 돌아오면 카드 크롬이 썸네일과 경쟁한다. 핀의 단순함이 라이브러리까지 이어지지 않는다.
- ShareX·ScreenToGif의 실제 화면은 이 세션에서 보지 못했다. 그 공백을 우리 우위로 적지 않는다.

### 공통 약점

- 주석 색이 제품 크롬과 다른 잉크 세트인 것. 캡처 도구가 흔히 빨강·노랑·초록·파랑 견본을 둔다. MyCapture도 그렇다.
- 타임라인이 정밀해질수록 전송 버튼이 촘촘해지는 것. `video-editor-glass.png`의 재생·트림·프레임 줄이 그 밀도다. 기능이 늘면 위계가 어려워지는 문제는 편집기가 공유한다.

### 범위 밖

오디오 믹서, OBS급 미리보기 스킨, Clipchamp식 자막 생성 UI. 감점하지 않는다. 녹화 커서 고리의 **색이 토큰과 다른 것**은 범위 안이다. 고리 자체가 설명용 녹화의 시각 장치다 (`ScreenCaptureEngine.cs` 491–494행).

## 5. 보완

대비 수치(WCAG 비율)는 적지 않는다. 접근성이 주 소유자다.

### P0

없음. 캡처 픽셀을 가리거나 브랜드가 사라져 작업을 못 하게 하는 화면은 확인하지 못했다.

### P1

| 항목 | 제안 주 소유 | OVERLAP |
|---|---|---|
| `app.ico`를 두 판 마크와 맞추거나, MASTER/`mycapture-mark.svg`를 출고 아이콘에 맞게 고친다. 트레이 형태(색 판+구멍)를 16px 규칙으로 문서에 적는다. | aesthetics | 없음 |
| 글래스/글래스 라이트 표면 알파를 Mica가 읽히도록 낮추거나, 설정 문구를 “거의 불투명한 틴트”로 맞춘다. | aesthetics | learnability (힌트 문장) |
| 작업 공간 악센트를 시안 램프 안으로 들이거나, MASTER에 틸을 예외로 적는다. 밝은 쪽 `Text.OnAccent` `#FFFFFF`(79행)와 `Timeline.TextLayer`=`Accent`(92행)를 분리한다. | aesthetics | accessibility (밝은 틸 위 흰 글자의 비율) |
| 녹화 영역 프레임을 녹화 중 `State.Warning`으로 바꾸고, 커서 고리 COLORREF를 `#F5B942` / `#FF6B74`에 맞춘다. | aesthetics | 없음 |
| `VideoLayerCanvas`·`VideoLayerAssets`의 DeepSkyBlue를 `Accent`/`Overlay.Handle*`로 바꾼다. | aesthetics | accessibility (고대비에서 그 파랑의 비텍스트 대비) |
| 자정·글래스 글자 3단을 MASTER 단계에 가깝게 다시 벌리되, 비율 합격은 접근성 팔레트와 함께 본다. | aesthetics (위계) | accessibility (비율, 주 소유는 접근성) |
| 갤러리에서 카드 시안을 하나(선택 또는 연필)로 줄여 검사기 **편집**과 겹치지 않게 한다. | aesthetics | convenience (어느 행동이 주 행동인지) |
| 고대비에서 `State.Warning`과 `Accent.Default`가 둘 다 `#FFD000`인 것을 형태·아이콘으로 보강하거나 색을 나눈다. | aesthetics (위계) | accessibility (고대비 팔레트 자체의 적합성, 비율의 주 소유) |

### P2

- `Timeline.TextLayer` `#3B82F6`을 악센트 시안과 색상 차이가 나게 조정하고, 텍스트 행에도 해치 또는 끝 모양을 준다. 소유: aesthetics.
- `Motion.Deliberate`를 쓰거나 토큰에서 뺀다. 소유: aesthetics.
- `PinWindow.cs`의 warm yellow 주석을 시안으로 고친다. 소유: aesthetics.
- 주석 기본 잉크 `#EF4444`를 상태 코랄과 같게 할지는 선택. 콘텐츠 색으로 두어도 된다. 소유: aesthetics. 학습 문구와 겹치면 learnability.
- 참조되지 않는 `Assets/Fluent/*.svg` (`fill="#212121"`)를 정리한다. 라이브 아이콘은 `Symbols.xaml` 스트로크다. 소유: aesthetics.
- 팔레트 원천을 `ThemeCatalog` 하나로 모아 `Tokens.xaml` 정적 색과 `WorkspaceTheme` 문자열이 다시 어긋나지 않게 한다. 소유: aesthetics.

## 6. 미확인

- 글래스 라이트, 작업 공간, 주간, 고대비의 실제 창. 코드 팔레트만 봤다.
- 라이브 Windows 11에서 Mica가 96% 알파 뒤로 보이는지. 샷은 오프스크린이다.
- 테마를 연 창 위에서 바꿀 때 `SettingsWindow.xaml`의 `StaticResource`와, 생성 시 `Color`를 복사하는 핀 테두리(`PinWindow.ResolveChromeColor`)가 즉시 따라오는지. 브러시 교체 경로는 `ThemeService`에 있으나 실행하지 않았다.
- ShareX, ScreenToGif, Snipaste, 캡처 도구의 실제 픽셀. 공식 페이지 추출에 앱 화면이 없었다.
- `docs/guides/shots`가 3.0.0 레이아웃과 픽셀 단위로 같은지. 구조는 맞고 버전 문자열은 2.3.7이다.
- 감소 모션을 켠 데스크톱에서의 등장·핀 페이드. 코드는 `ClientAreaAnimation`을 본다. `UxReviewSelfTest`는 모션을 끄고 렌더한다.
- 트레이 아이콘이 16px로 줄어들었을 때의 구멍 가독성. 256px 프레임만 분류했다. `app.ico`도 256 한 프레임이다.
