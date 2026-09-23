# aesthetics inbox

라운드 1 주장. 점수는 `round1/03-aesthetics.md`. 대비 비율 숫자는 이 축의 감점이 아니다.

| claim_id | 주장 | 주 소유 축 | 겹치는 축 | 근거 |
|---|---|---|---|---|
| AES-01 | 자정·글래스에서 채움 행동과 포커스는 시안 `#58C7F3` / `#7DD7F8`이고, 가이드 4장에서도 기본 버튼만 그 색으로 채워진다. | aesthetics | 없음 | `AppTheme.cs` 108행; `Controls.xaml` 163–166, 129–148행; `docs/guides/shots/*.png` |
| AES-02 | 출고 창·헤더 아이콘 `app.ico`는 시안 없는 흰 둥근 사각형 하나다. 두 판 마크 SVG는 코드에서 참조되지 않는다. | aesthetics | 없음 | `app.ico` 색 분류; `mycapture-mark.svg` 13–14행; `Controls.xaml` 774행; 저장소에 `mycapture-mark` 참조 없음 |
| AES-03 | 트레이 4종은 MASTER 상태 색과 픽셀이 같고, 형태는 색 판+가운데 구멍이라 두 판 마크가 아니다. | aesthetics | 없음 | `tray-idle.ico` `(88,199,243)`, `tray-capturing.ico` `(245,185,66)`, `tray-busy.ico` `(69,214,162)`, `tray-error.ico` `(255,107,116)` |
| AES-04 | 작업 공간 테마는 악센트를 틸 `#5AD9C5`/`#167768`로 바꾸고, 밝은 갤러리의 `Text.OnAccent`를 흰색으로 둔다. | aesthetics | accessibility | `WorkspaceTheme.cs` 79–92행; MASTER는 악센트 위 어두운 잉크 |
| AES-05 | 글래스 표면 알파는 약 96–97%라 Mica가 패널 뒤로 거의 비치지 않는다. 설정 힌트는 반투명 패널을 말한다. | aesthetics | learnability | `AppTheme.cs` 264–279행; `ModernWindowChrome.cs` 161–198행; `settings-glass.png`; `SettingsWindow.xaml` 261–268행 |
| AES-06 | 실행 중 자정 글자색은 토큰 파일보다 단계가 좁다. Primary `#FFFFFF`, Secondary `#ECF1F9`, Muted `#D5DEEA`. | aesthetics | accessibility | `Tokens.xaml` 101–103행 `#F6F8FC`/`#C6D0DF`/`#8E9CAF`; `AppTheme.cs` 103–105행. 비율 숫자의 주 소유는 accessibility |
| AES-07 | 갤러리는 카드마다 시안 연필, 고정 배지, 검사기 시안 버튼을 동시에 둔다. 썸네일은 244×292 타일의 우물 안이다. | aesthetics | convenience | `GalleryWindow.xaml` 34–36, 126–131, 198–206, 539, 572행; `library-midnight.png` |
| AES-08 | 녹화 영역 프레임은 녹화 중에도 `Border.Accent` 시안이다. 앰버는 트레이에만 있다. | aesthetics | 없음 | `RecordingControlWindow.cs` 174, 202–205행; `tray-capturing.ico` |
| AES-09 | 녹화 커서 고리는 `#FFC700` / `#FF4040`이라 토큰 앰버 `#F5B942`, 코랄 `#FF6B74`와 다르다. 색이 캡처 픽셀에 구워진다. | aesthetics | 없음 | `ScreenCaptureEngine.cs` 510–511행 |
| AES-10 | 영상 레이어 선택 핸들과 도형 비트맵이 `Brushes.DeepSkyBlue` 고정이라 테마를 따르지 않는다. | aesthetics | accessibility | `VideoLayerCanvas.cs` 83–87행; `VideoLayerAssets.cs` 16–17행. 고대비 비텍스트 대비의 주 소유는 accessibility |
| AES-11 | 텍스트 레이어 `#3B82F6`가 시안 악센트와 같은 푸른 계열이고, 작업 공간 밝은 팔레트에서는 악센트와 같은 `#167768`이다. 프레임 레이어만 해치가 있다. | aesthetics | 없음 | `Tokens.xaml` 136–137행; `WorkspaceTheme.cs` 92행; `VideoLayerTimeline.cs` 259, 287, 331–350행; `video-editor-glass.png` |
| AES-12 | 타임라인은 개요 34px + 세부 42px, 삭제 구간 해치·라벨, 공유 재생헤드를 그린다. 설계 문서 머리말의 “단일 스크러버”보다 구현이 앞선다. | aesthetics | 없음 | `TwoLineTimeline.cs` 24–25, 745–830, 946–979행; `video-editor-timeline-ux.md` |
| AES-13 | 모션 83/167ms는 버튼·창 등장·핀에 연결되고 캡처 오버레이 등장은 꺼져 있다. 250ms 토큰은 호출처가 없다. | aesthetics | accessibility | `Tokens.xaml` 195–197행; `FluidMotion.cs` 71–72, 87–88행; `Controls.xaml` 35, 113행; `CaptureOverlayWindow.cs` 52행. 감소 모션 준수의 주 소유는 accessibility |
| AES-14 | 핀은 1px 토큰 테두리와 이미지 채움이고, 호버만 `Accent.Cool`이다. 코드 주석은 아직 warm yellow라고 적는다. | aesthetics | 없음 | `PinWindow.cs` 101, 132–146, 364–402행. 핀 창 스크린샷은 없음 |
| AES-15 | 앱 고대비 테마에서 악센트와 경고가 둘 다 `#FFD000`이다. OS 고대비는 캡션과 일부 컨트롤만 시스템 색이다. | aesthetics | accessibility | `AppTheme.cs` 204–214행; `ModernWindowChrome.cs` 200–203행; `Controls.xaml` 610행 근처. 고대비 적합성의 주 소유는 accessibility |
| AES-16 | 주석 견본 `#EF4444` `#FBBF24` `#34D399` `#3B82F6`은 상태 색과 다른 콘텐츠 팔레트다. | aesthetics | learnability | `AnnotationEditorControl.cs` 1222–1229행; `annotation-glass.png` |
| AES-17 | 가이드 PNG 네 장을 봤다. 제목은 `MyCapture-2.3.7`이라 3.0.0 라이브 촬영이 아니고, 글래스 라이트·작업 공간·주간·고대비는 화면이 없다. | aesthetics | 없음 | `docs/guides/shots/`; `Directory.Build.props` Version 3.0.0; `AppIdentity.cs` 7–19행 |
| AES-18 | Snipaste 공식 문구는 떠 있는 핀이 제품의 중심이다. ShareX·ScreenToGif 공식 추출에서는 앱 크롬을 보지 못했다. Windows 지원 문서는 회색 오버레이와 Clipchamp 인계를 적는다. | aesthetics | positioning | 2026-09-23 조회: snipaste.com, getsharex.com, screentogif.com, support.microsoft.com 캡처 도구 문서. 가격·광고는 적지 않음 |
