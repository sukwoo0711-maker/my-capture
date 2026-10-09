# Microsoft Store 목록 초안

Partner Center의 Store 목록과 제출 옵션에 붙여 넣을 문구입니다. 매니페스트가 한국어(ko-KR)와 영어(en-US)를 선언하므로 두 언어 목록을 모두 작성합니다.

## 공통 항목

- **제품 이름**: `MyCapture` (Store ID `9PB33MC095VR`, https://apps.microsoft.com/detail/9PB33MC095VR)
- **범주**: 생산성 또는 사진 및 비디오
- **가격**: 무료
- **개인정보처리방침 URL**: `https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md`
- **지원 연락처**: `https://github.com/sukwoo0711-maker/mycapture-support/issues`
- **웹 사이트**: `https://github.com/sukwoo0711-maker/mycapture-support`
- **저작권**: `Copyright (c) 2026 MyCapture contributors`
- **앱 타일 아이콘(선택)**: 패키징 결과물 `StoreListingIcon-300.png`
- **스크린샷**: `--selftest-store-shots`로 만든 언어별 10장 (만드는 방법은 [`README.md`](README.md#store-스크린샷-만들기))
- **시스템 요구 사항**: Windows 11 버전 21H2(빌드 22000) 이상, x64

## 한국어 (ko-KR)

### 설명

MyCapture는 화면 캡처, 주석 편집, 화면 고정, OCR, 라이브러리, 영역 녹화를 한곳에 모은 Windows용 캡처 도구입니다. 캡처와 텍스트 인식은 PC 안에서 처리하며, 전역 단축키와 트레이 메뉴로 반복 작업을 빠르게 이어 갑니다.

영역을 선택해 캡처하면 라이브러리에 먼저 저장하고 클립보드에 복사한 뒤 편집기를 엽니다. 사각형, 화살표, 연필, 텍스트, 이미지 레이어로 주석을 달고, 로컬 OCR이 찾은 이메일·전화번호·카드번호 같은 민감정보를 빠른 가리기로 한 번에 가릴 수 있습니다. 화면 영역은 MP4로 녹화하고, 영상 편집기에서 구간을 자르고 텍스트를 얹은 뒤 MP4나 GIF로 내보냅니다.

녹화에는 소리가 포함되지 않습니다. 로그인할 때 자동으로 시작하는 기능은 처음에는 꺼져 있으며, 설정에서 켤 수 있습니다.

### 기능 목록

- 모니터 경계를 넘나드는 영역 캡처와 창, 전체 화면, 지연, 스크롤 캡처
- 실행 취소와 다시 실행을 지원하는 비파괴 주석 편집
- 클립보드 이미지·텍스트·표를 항상 위에 보이는 창으로 고정
- 한국어·영어 혼합 텍스트를 PC 안에서 인식하는 로컬 OCR
- OCR로 찾은 민감정보를 한 번에 가리는 빠른 가리기
- 이미지와 MP4를 함께 찾고 재생하는 통합 라이브러리
- 영역 MP4 녹화와 비파괴 영상 편집
- 최대 20초 구간의 GIF 내보내기
- 한국어·영어 인터페이스와 고대비를 포함한 6가지 테마
- 키보드 조작과 스크린 리더 접근성

### 검색어

캡처, 스크린샷, OCR, 화면 녹화, GIF, 주석, 화면 고정

## English (en-US)

### Description

MyCapture brings screenshots, annotation, pinning, OCR, a capture library and region recording together in one Windows capture tool. Captures and text recognition are processed on your PC, and global shortcuts and the tray menu keep repeated work fast.

A region capture is saved to the library and copied to the clipboard before the editor opens. Annotate with rectangle, arrow, pencil, text and image layers, and cover sensitive details such as email addresses, phone numbers and card numbers found by local OCR in one step with Quick Redact. Record a screen region to MP4, then trim it, add timed text and export MP4 or GIF in the video editor.

Recordings have no audio. Launch at sign-in starts turned off and can be turned on in Settings.

### Features

- Region capture across monitors, plus window, full screen, delayed and scrolling capture
- Non-destructive annotation with undo and redo
- Pin clipboard images, text and tables in always-on-top windows
- On-device OCR for mixed Korean and English text
- Quick Redact for sensitive details found by OCR
- One library for images and MP4 videos with in-app playback
- Region MP4 recording with non-destructive video editing
- GIF export for clips up to 20 seconds
- Korean and English interface with six themes, including high contrast
- Keyboard and screen reader accessibility

### Search terms

capture, screenshot, OCR, screen recording, GIF, annotation, pin

## 제출 옵션: 제한 기능 사용 사유

Partner Center의 Submission options > Restricted capabilities에 영어로 입력합니다.

> **runFullTrust**: MyCapture is a Win32 desktop screen capture tool built with .NET and WPF. Full trust is required to capture the screen with GDI, register user-configurable global shortcuts with RegisterHotKey, show a notification-area icon, record screen regions with Media Foundation, run on-device OCR, and read the address of the browser page being captured through UI Automation. The app installs no drivers or services, never elevates, and keeps captures on the user's PC.

## 인증 참고 사항 (Notes for certification)

> MyCapture runs in the notification area after launch and opens its library window. Default shortcuts: Ctrl+Shift+C region capture, Ctrl+Shift+X start or stop region recording, Ctrl+Shift+Z open the library, F3 pin clipboard content. Capture, editing, recording and OCR run on the PC. The only automatic network access is downloading missing OCR model files from modelscope.cn at startup; the optional GitHub image attachment requires the user's own GitHub account. Launch at sign-in is off by default and can be turned on in Settings > General. Updates are delivered only through the Microsoft Store; the in-app GitHub updater is disabled in the Store package.
