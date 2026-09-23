# 08 — 제품 범위·경쟁 포지션 (라운드 1)

대상: Release 3.0.0, 커밋 `117b0ef` (`main`). 확인일: 2026-09-23.
이 점수는 기능 완성도가 아니라 **범위 일관성**이다. 도구가 비어 있는 사실의 채점은 functionality, 서명·토큰·브라우저 자동화의 안전은 trust, 조작 체감은 convenience가 주 소유다. positioning은 그 사실이 한 문장 포지션을 지키는지, 아니면 경쟁 제품을 흉내 내다 표면이 끊기는지만 본다.

`docs/market/`의 점수·가격·해자는 쓰지 않았다. `docs/competitive-matrix.md`는 투자 순서 초안으로만 읽고, 구현은 코드로 다시 확인했다. 다른 라운드 1 산출물은 읽지 않았다.

## 1. 포지션과 일관성

**한 문장:** MyCapture는 계정 없이 PC 안에서 화면을 찍어, 주석·핀·로컬 OCR로 설명하고, 마이크와 시스템 소리가 없는 MP4와 짧은 GIF로 남기는 Windows 11 도구다.

**일관성 점수: 4 / 5.**

트레이에 나온 일은 그 문장과 같다. 영역·창·전체·지연·이전 영역·스크롤, 주석 편집, 라이브러리, 영역 녹화(`src/MyCapture.App/App.xaml.cs` 메뉴). 녹화 설정에는 오디오 필드가 없고(`src/MyCapture.Core/Recording/RecordingSettings.cs`), `src/`에서 Audio·Telemetry 검색은 0건이다. MP4는 Windows Media Foundation(`src/MyCapture.Platform/Recording/MediaFoundationVideoEncoder.cs`)이고, GIF는 20초·최대 200프레임·장변 960/10fps, 640/10fps, 480/5fps로 설명 클립에 맞춰 잠겨 있다(`AnimatedGifExporter.cs`, `GifExportQuality.cs`). 배포는 self-contained·오프라인 매니페스트다(`build/package.ps1`). README도 오디오 제외, 텔레메트리 없음, .NET 선설치 불필요를 같은 말로 적는다.

4인 이유: 핵심 고리는 끊기지 않았다. 깎인 1점은 경쟁 표면을 **설정·단축키·기본 URL까지 끌어오고 도구는 끝내지 않은 자리**다.

- 형광펜 알파(0–255)는 설정 탭에 있다(`SettingsWindow.xaml`). `PenAnnotation.IsHighlighter`와 합성 분기는 있다. 연필을 만들 때는 그 플래그를 켜지 않는다(`AnnotationEditorController.cs`의 `new PenAnnotation`). 에디터 도구 목록에도 형광펜이 없다(`EditorTool.cs`).
- 모자이크 도구는 있다(`EditorTool.Mosaic`). 설정 블록 크기 기본값은 12인데, 드래그가 끝날 때 넘기는 값은 **14로 고정**이다(`AnnotationEditorControl.cs` `blockSize: 14`). 설정 칸은 저장될 뿐 그 제스처가 읽지 않는다. `RememberPreferences`는 이전 값을 그대로 다시 쓴다.
- `PinSettings.ClosedWindowRestoreLimit` 기본 20은 저장·클램프만 된다(`AppSettings.cs`, `SettingsStore.cs`). 설정 창 XAML과 `src/MyCapture.App/Pinning/`에는 복구 동작이 없다. 핀 회전·반전도 없다. 주석 캔버스 90도 회전(`AnnotationEditorControl.RotateCapture`)은 별개다.
- `CaptureSettings.ColorFormat`은 초안에 보존만 된다. 확대경은 `#RRGGBB`를 보여줄 뿐(`CaptureOverlayView.cs` `_sampleLabel`), `OnKeyDown`에 색 복사 키가 없다.
- GitHub 이슈 이미지 URL은 전역 단축키다. 코드 기본키는 **F9**(`HotkeySettings.UploadGitHubImage`). README 단축키 표는 아직 **F4**다. 빈 이슈 URL은 `https://github.com/sukwoo0711-maker/my-capture/issues/new`로 떨어진다(`GitHubIssueImageUrl.DefaultIssueUrl`). 토큰이 있으면 REST, 실패하면 브라우저에 이슈를 열고 붙여 넣는 경로로 넘어간다(`App.xaml.cs` `HandleGitHubImageUpload`). 로컬 설명 도구의 옆에 계정·네트워크 목적지가 붙어 있고, 문서와 키가 어긋난다.
- `docs/competitive-matrix.md`의 “현재 구현”은 3.0.0보다 뒤처져 있다. 모자이크 UI와 녹화 중 커서 강조(`RecordingSettings.CursorHighlight`, 기본 켜짐, `RegionRecordingCoordinator`)는 이미 있다. 매트릭스를 현재 점수로 쓰면 구현된 설명 기능을 공백으로 세게 된다.

5가 아닌 이유만 위 이음새다. 1–3이 아닌 이유: 제품이 ShareX 파이프라인이나 ScreenToGif 프레임 에디터를 흉내 내다 멈춘 상태가 아니다. 그 폭은 대부분 아직 없어서, 포지션은 오히려 남아 있다.

## 2. 경쟁 맵

확인은 2026-09-23 이 세션의 WebFetch와 HTTP 응답이다. 가격 숫자, 점유율, 매출은 적지 않는다. PicPick 페이지가 말한 라이선스 구분만, 금액 없이 적는다.

| 제품 | 한 줄 포지션 | MyCapture가 이기는 지점 | MyCapture가 지는 지점 | 확인 | 날짜 |
|---|---|---|---|---|---|
| Snipaste | 캡처, 주석, F3 붙여넣기(핀) 세 덩어리의 키보드 도구. | 캡처와 핀을 분리하고, 텍스트·엑셀 표의 원문을 이미지와 따로 둔다. 설명용 MP4/GIF와 로컬 가리기가 같은 앱에 있다. | 닫은 핀 복구, 핀 회전·반전, 색 카드·파일 경로 핀, 확대경에서 색 복사. 형광펜은 설정만 있다. | 열림. [Getting Started](https://github.com/Snipaste/feedback/wiki/Getting-Started) (위키 편집 Jan 6, 2026) | 2026-09-23 |
| ShareX | 캡처 뒤 작업·업로드·생산성 도구를 한 파이프라인으로 묶는 Windows 도구. 홈페이지는 무료·광고 없음이라고 적는다. | 설명 클립이 계정과 목적지 없이 로컬 MP4/GIF로 끝난다. FFmpeg를 받지 않는다. | 자유 영역, 흐림·단계 번호·말풍선, 캡처 후 외부 명령. GitHub 첨부는 그 파이프라인의 좁은 옆길이다. | **공식 가이드 인덱스 실패.** `https://getsharex.com/blog/guides/` 와 `https://getsharex.com/blog/how-to-record-screen-windows/` 는 HTTP 404. 대신 열린 페이지: [홈](https://getsharex.com/), [영역 캡처](https://getsharex.com/docs/region-capture), [Pin to Screen](https://getsharex.com/docs/pin-to-screen), [Actions](https://getsharex.com/actions.html) | 2026-09-23 |
| ScreenToGif | 선택 영역·웹캠·스케치보드를 찍어 GIF, APNG, 비디오, PSD, PNG로 프레임 편집하는 도구. README는 .NET 9 데스크톱 런타임이 필요하다고 적는다. | 런타임을 넣은 self-contained 빌드. 스틸·핀·OCR·라이브러리가 녹화와 한 앱이다. GIF 상한이 설명 클립에 맞춰져 있다. | 프레임 삭제·재정렬·지연, 키 입력 표시, 워터마크, APNG. 이 폭을 따라가면 포지션이 흐려진다. 주 소유는 functionality. | **features URL은 기능 목록 확인 실패.** `https://www.screentogif.com/features` 는 301로 `https://nicke.tech/n-studio?migrated=true` 에 닿고, 본문에서 읽힌 기능 설명은 없다. 보충으로 연 공식 글: [README](https://github.com/NickeManarin/ScreenToGif), [에디터 리본 위키](https://github.com/NickeManarin/ScreenToGif/wiki/Help-%E2%96%AA-Editor-%E2%9C%8F%EF%B8%8F-%E2%96%AA-Ribbon) | 2026-09-23 |
| 알캡처 | 사각형·자유형·단위영역·창·전체·스크롤·지정크기와 그리기, 최근 목록, AI 텍스트·화질·배경 제거·지우개·얼굴 모자이크. v3.25 이력에 제품 내 로그인이 있다. | 기본 경로에 로그인·AI 보정·클라우드가 없다. 레이어를 남긴 채 다시 고치고, 소리를 넣지 않은 녹화와 핀이 있다. | 트레이에 자유형·단위영역·지정크기가 없다. 지정크기 API는 self-test만 호출한다(`AdvancedCaptureService.CaptureFixedSize`). 자동 얼굴 모자이크는 없다. | 열림. [제품 페이지](https://altools.co.kr/product/ALCAPTURE) (히스토리 v3.28, 2026-09-17). 가격은 이 페이지에 없었다. | 2026-09-23 |
| Greenshot | 가벼운 스크린샷. 영역·창·전체·Internet Explorer 스크롤, 주석·강조·가리기, 파일·프린터·클립보드·메일·Office·사진 사이트로 보내기. 홈은 완전 무료·오픈소스라고 적는다. | 설명용 영상과 핀·OCR이 있고, 내보내기 목적지를 제품 정의에 넣지 않는다. | 강조·가리기 편집의 폭, 외부 명령 플러그인. | 열림. [getgreenshot.org](https://getgreenshot.org/) | 2026-09-23 |
| PicPick | 캡처, 시스템 소리·마이크 녹화, 리본 편집, 흐림·모자이크·워터마크, 클라우드·FTP·메일 전송, 색·자·돋보기·화이트보드를 한 서랍에 둔 도구. FAQ: 개인 사용은 무료, 업무는 라이선스, 도구 구성은 같다고 함. 금액은 이 페이지에 없었다. | 오디오와 전송을 빼서 범위가 “화면을 설명하는 클립”으로 좁다. PicPick도 사용 데이터를 모으지 않고 인터넷 없이 동작한다고 적어, 프라이버시만으로 이기지는 않는다. | 번호 스탬프, 흐림, 디자인 보조 도구, 원클릭 전송. 오디오 녹화는 범위 밖이라 열세로 세지 않는다. | 열림. [기능 페이지](https://picpick.app/en/features/) | 2026-09-23 |
| Windows 11 캡처 도구 | OS에 들어 있는 사각형·창·전체·자유형 스냅샷과 사각형 비디오. 펜·형광펜·도형, 로컬 OCR로 글자 복사와 이메일·전화 빠른 가리기, 자동 저장 후 공유. 캡션과 오디오는 Clipchamp로 넘긴다. | 핀, 스크롤, 비파괴 레이어, 라이브러리, GIF가 설치형 앱 안에서 끝난다. 영상 설명을 다른 편집기로 보내지 않는다. | OS 기본이라 설치와 “알 수 없는 게시자”가 없다. 자유형·형광펜. Copilot+ 전용 Perfect screenshot·색 선택은 그 하드웨어에만 있으므로 일반 열세로 세지 않는다. | 열림. [Microsoft 지원](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots) | 2026-09-23 |

OBS Studio는 비교 칸에 넣지 않는다. [빠른 시작](https://obsproject.com/kb/quick-start-guide) (문서 날짜 2021-08-25, 조회 2026-09-23)은 장면·소스, 데스크톱 소리와 마이크, 녹화와 스트리밍이다. 범위 밖 참조만 한다.

## 3. 과제 고유 강점

전략적 의미만 적는다. 같은 사실을 기능·편의 점수로 다시 세지 않도록 주 소유를 붙인다.

1. **캡처와 핀의 분리.** 영역 캡처는 편집과 클립보드로 가고 자동으로 뜨지 않는다. 참조가 필요할 때만 F3이다(`README.md`, 매트릭스 1.6절이 코드의 단축키 분리와 일치: `PasteToScreen` = F3). Snipaste는 캡처 중 붙여넣기가 성공한 캡처의 한 출구다. 제스처 품질의 주 소유는 **convenience**. positioning이 가지는 것은 “찍기”와 “띄워 두기”를 다른 일로 둔 전략뿐이다.
2. **설명 클립의 코덱을 OS에 묶음.** 외부 FFmpeg 없이 Media Foundation MP4, GIF는 WPF 코덱과 상한. ShareX Actions 페이지의 변환 예는 ffmpeg.exe다. ScreenToGif README는 .NET 9 런타임이 필요하다고 적는다. 패키지가 self-contained인지는 **trust**, 프레임 드롭은 **performance**. positioning은 “설명 녹화에 코덱 설치와 계정이 필요 없다”는 문장만 가진다.
3. **가리기가 레이어로 남음.** 로컬 OCR로 이메일·전화·주민번호 형태·카드·IP·비밀키 후보를 가림막으로 넣고, 저장 전에 옮기거나 취소할 수 있다(`PrivacyRedactionService.cs`, README). Windows 캡처 도구도 이메일·전화를 로컬에서 가린다고 지원 문서에 적는다. 탐지 종류와 오탐의 주 소유는 **functionality**와 **trust**. positioning은 “설명해서 보내기 전에 검토 가능한 로컬 가리기”가 업로드형 도구의 사후 처리와 다르다는 점만 가진다.

핀 확대·투명도·클릭 통과, 모자이크 도구, 커서 링, GIF 프리셋은 위 문장을 구현한 기능이다. 강점 목록에 다시 넣지 않는다.

## 4. 범위 밖이라 감점하지 않은 유혹

| 유혹 | 왜 감점하지 않았나 |
|---|---|
| 마이크·시스템 오디오, OBS식 장면·스트리밍 | README가 의도적 제외라고 말하고, 녹화 설정과 `src/`에 오디오 경로가 없다. PicPick·ShareX 홈의 소리 포함 녹화, OBS 빠른 시작의 믹서는 다른 제품이다. |
| 웹캠·스케치보드 | ScreenToGif README와 리본 위키의 입구다. 화면 설명 문장에 없다. |
| APNG·이미지 시퀀스·프레임 재정렬·요요·시네마그래프·전환 | 매트릭스 P2와 ScreenToGif 리본. 약속은 GIF/MP4다. 없으면 포지션이 흐려지지 않는다. |
| 캡처 후 임의 실행 파일, 클라우드·FTP·메일·Office 목적지 | ShareX Actions, Greenshot 홈, PicPick 전송. 매트릭스 P2 “로컬 후처리 플러그인”은 아직 없다. 없는 것이 범위와 맞다. |
| AI 화질·배경 제거·지우개·얼굴 자동 모자이크 | 알캡처 제품 페이지의 사진 보정이다. 빠른 가리기는 이미 로컬 텍스트 후보다. 얼굴 감지는 `src/`에 없고, 없어도 문장이 깨지지 않는다. |
| 색 피커 전문 도구, 자, 각도기, 화이트보드, QR, 해시 | PicPick·ShareX 생산성 서랍. 확대경이 헥스를 보여주는 것까지가 캡처 보조다. |
| 자유형·단위 영역 | 알캡처·캡처 도구·ShareX 영역 종류. 사각형 설명 캡처의 공백이지, 제품이 다른 카테고리로 실패한 것은 아니다. 모드 자체는 **functionality**. |
| FFmpeg 하드웨어 인코더·다중 오디오 트랙 | OBS·ShareX 체인지로그급 녹화 제품. P0 백엔드 교체는 같은 문장의 품질이고, 인코더 쇼핑이 아니다. 측정은 **performance**. |

## 5. 포지션을 선명하게 하는 보완

기능 목록이 아니다. 다섯을 넘기지 않는다.

1. **도구가 없는 설정을 빼거나, 그 도구를 끝내라.** 형광펜 알파, 모자이크 블록(제스처는 14 고정), 닫은 핀 복구 한도, 쓰이지 않는 색 형식. 반쪽 칸이 “Snipaste를 따라가다 멈춤”으로 읽힌다. 주 소유: **positioning**(표면과 문장의 일치). 도구를 만들지로 넘어가면 **functionality**.
2. **GitHub 첨부를 제품 정의 밖으로 내려라.** 옵트인 목적지로 두고, 기본 이슈 URL과 README의 F4를 코드의 F9과 맞춰라. 보안 세부는 여기 점수에 넣지 않는다. 주 소유: **positioning**. 토큰·브라우저 붙여넣기·DPAPI는 **trust** (OVERLAP).
3. **신뢰 문장은 체크섬·무텔레메트리·self-contained로 두고, 게시자 신원은 주장하지 마라.** 업데이트 부재는 사실이 아니다. 설정에 확인 탭이 있고, SHA-256 검증 뒤 설치로 넘긴다(`SettingsWindow.Updates.cs`, `GitHubUpdateService.cs`, `UpdateInstaller.cs`). Authenticode가 없어 SmartScreen “알 수 없는 게시자”가 나는 것은 README가 이미 말한다. 그 공백이 “신뢰할 수 있는 로컬 도구”를 깨는지는 **trust**가 소유한다 (OVERLAP). positioning은 포지션 문장에 게시자 신뢰를 넣지 말라는 제한만 둔다.
4. **설명 녹화에서 다음 하나는 단계 표시뿐이다.** 커서 링은 3.0.0에 있다. 번호·클릭 단계는 ShareX 영역 캡처의 Step, ScreenToGif 리본의 Mouse Clicks와 같은 *설명* 언어다. 프레임 효과·APNG로 넓히지 않는다. 주 소유: **functionality**. 단축키·오버레이가 녹화를 방해하지 않는지는 **convenience** / **performance**.
5. **P0 캡처 백엔드만 다음 투자로 인정하라.** 매트릭스의 Windows Graphics Capture 또는 Desktop Duplication은 같은 MP4 설명의 드롭·CPU 문제다. P2 플러그인과 APNG는 채택하지 않는 쪽이 문장을 지킨다. 주 소유: **performance**. 공급망 조건(라이선스, 롤백)은 **trust**.

## 6. 미확인

- ShareX 화면 녹화 가이드 본문. 인덱스와 녹화 가이드 URL은 404였다. 홈·영역 캡처·핀·Actions만 열렸다. FFmpeg 다운로드 UI의 현재 문구는 이 세션에서 확인하지 못했다.
- `https://www.screentogif.com/features`의 이전 기능 목록. 리다이렉트 대상에 기능 문장이 없었다. 프레임 편집 폭은 GitHub README와 리본 위키로만 확인했다.
- 알캡처 가격·광고·번들. 연 제품 페이지에 금액이 없었다. 무료 여부는 적지 않는다.
- Greenshot 현재 버전 번호와 플러그인 목록 전문. 홈의 기능 문장만 확인했다.
- PicPick 라이선스 금액. FAQ의 개인/업무 구분만 확인했다.
- 캡처 도구의 비디오가 소리 없이 저장되는지. 지원 문서는 녹화 후 Clipchamp에서 오디오를 다룬다고만 한다. 녹화 파일 자체에 트랙이 있는지는 미확인.
- OBS를 감점 기준으로 쓰지 않았으므로, 인코더·장면 개수는 더 열지 않았다.
- 지정 크기 캡처가 설정 어딘가 다른 창에 연결되어 있는지는 트레이 메뉴와 `CaptureFixedSize(` 호출처(정의, self-test)만 봤다. 다른 UI 문자열까지는 미확인.
- 런타임 네트워크가 업데이트·GitHub 첨부 외에 더 있는지는 텔레메트리 문자열 검색과 README 범위만 봤다. 호출 그래프 전체는 미확인.
