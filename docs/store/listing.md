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
- **스크린샷**: `--selftest-store-shots`로 만든 언어별 10장 (만드는 방법은 [`README.md`](README.md#store-스크린샷-만들기)). 캡션은 언어별 "스크린샷 캡션" 절을 순서대로 넣습니다. 영어 7·8번은 영상 편집기 문구가 한국어로 찍히던 검사 결함을 고친 뒤 4.3.0 기준으로 다시 찍었습니다(2026-10-19).
- **트레일러(한국어 목록)**: 30초 1920×1080 60fps MP4(약 9MB). 썸네일은 영상의 한 프레임(1920×1080 PNG)이고 썸네일 제목은 "MyCapture로 고객 문의 화면 정리하기"입니다. 장면에 문구 없이 키 입력만 보여 주므로 자막 파일은 두지 않았습니다. 영어 목록에는 트레일러가 없습니다(2026-10-19).
- **16:9 Super hero art**: 3840×2160 PNG, 두 언어 공통. 제품 이름과 앱 화면 없이 로고 표시만 넣었습니다. 한국어 목록은 이 이미지가 있어 트레일러를 목록 맨 위에 재생하도록 지정했습니다.
- **시스템 요구 사항**: Windows 10 버전 22H2(빌드 19045) 이상, x64. ARM64 x64 에뮬레이션은 Windows 11(빌드 22000) 이상

## 한국어 (ko-KR)

### 짧은 소개

개발자를 위한 무료 Windows 캡처·핀 도구. 오류 화면에 주석을 달고, 참고할 코드와 이미지를 다른 창 위에 띄울 수 있습니다.

### 설명

MyCapture는 개발자를 위한 무료 Windows 캡처·핀 도구입니다. 코드와 참고 화면을 곁에 두고 작업하거나, 오류가 발생한 화면을 캡처해 설명할 때 사용합니다.

기본 단축키는 영역 캡처 Ctrl+Shift+C, 클립보드를 핀으로 띄우기 F3, 영역 녹화 Ctrl+Shift+X입니다. 단축키는 설정에서 바꿀 수 있습니다.

원하는 영역, 창, 전체 화면을 캡처하고 화살표·단계 번호·텍스트로 필요한 부분을 표시할 수 있습니다. 이미지를 자르거나 회전하고, 블러와 모자이크로 일부를 가릴 수 있습니다. 오류 재현 절차나 코드 리뷰에서 설명할 내용을 이미지로 정리할 때 유용합니다.

코드 예제나 비교할 화면은 핀으로 띄워 다른 창을 보면서 참고할 수 있습니다. 캡처한 이미지뿐 아니라 클립보드의 텍스트와 표도 다른 창 위에 표시하며, 닫은 핀을 다시 열 수 있습니다.

OCR로 이미지 속 글자를 텍스트로 복사하고, 표를 CSV·Markdown·탭 구분 형식으로 가져올 수 있습니다. 캡처는 라이브러리에서 검색하고 폴더로 정리하며, 폴더나 ZIP으로 내보낼 수 있습니다.

움직임을 설명해야 할 때는 화면 영역을 MP4로 녹화합니다. 녹화한 영상은 구간을 자르고 텍스트를 추가한 뒤 MP4나 GIF로 내보낼 수 있습니다. 녹화에는 소리가 포함되지 않습니다. Windows 10에서는 녹화하는 동안 화면 가장자리에 노란 테두리가 보일 수 있으며, 이 테두리는 녹화 결과에 포함되지 않습니다.

트레이 메뉴를 지원하며, 한국어·영어 인터페이스와 고대비를 포함한 6가지 테마를 제공합니다.

지원 환경: Windows 11, Windows 10 버전 22H2(x64). ARM64 PC에서는 Windows 11이 필요합니다.

### 기능 목록

- 모니터 경계를 넘나드는 영역 캡처와 창, 전체 화면, 지연, 스크롤 캡처
- 포인터 아래 창을 강조하고 클릭으로 선택하는 창 감지와 색상 값 복사
- 타원·형광펜·단계 번호·모자이크·블러를 포함한 비파괴 주석 편집과 실행 취소·다시 실행
- 클립보드 이미지·텍스트·표를 다른 창 위에 항상 보이는 핀으로 띄우고, 닫은 핀을 다시 열기
- 한국어·영어 혼합 텍스트를 PC 안에서 인식하는 로컬 OCR
- 영역을 선택하면 텍스트만 클립보드에 복사하는 텍스트 캡처
- OCR로 찾은 민감정보를 한 번에 가리는 빠른 가리기
- 이미지와 MP4를 함께 찾고 재생하는 통합 라이브러리
- 일시정지를 지원하는 영역 MP4 녹화와 비파괴 영상 편집
- 미리보기 샘플과 예상 크기, 공유 대상 크기에 맞춘 권장 설정을 보여 주는 GIF 내보내기
- 한국어·영어 인터페이스와 고대비를 포함한 6가지 테마
- 키보드 조작과 스크린 리더 접근성
- 계정, 광고, 캡처 횟수 제한 없음

### 스크린샷 캡션

스크린샷 순서대로 넣습니다. 장마다 200자 이하입니다.

1. 캡처와 녹화가 날짜별로 쌓이는 라이브러리입니다. 창 제목과 이미지 속 글자로 검색합니다.
2. 화살표, 사각형, 텍스트 주석은 레이어로 남아 저장한 뒤에도 고칠 수 있습니다.
3. Ctrl+Shift+R로 이메일, 전화번호, 카드번호를 찾아 한 번에 가립니다.
4. 캡처 직후 카드에서 C 복사, E 편집, P 핀, S 저장, T 텍스트 복사를 키 하나로 실행합니다.
5. F3으로 클립보드의 이미지, 표, 코드를 다른 창 위에 띄웁니다.
6. 이미지 속 한국어와 영어를 이 PC에서 인식해 텍스트로 복사합니다.
7. 녹화한 영상에서 남길 구간을 고르고, 시간에 맞춰 텍스트를 넣습니다.
8. MP4나 GIF로 내보내기 전에 예상 크기와 권장 설정을 확인합니다.
9. 언어, 6가지 테마, 포인트 색, 단축키, 보관 기간을 설정에서 바꿉니다.
10. 밝은 테마의 라이브러리입니다. Windows 11과 Windows 10 22H2에서 같은 기능을 사용합니다.

### 이 버전의 새로운 기능 (4.3.0)

- Windows 10 버전 22H2(빌드 19045) x64에서도 설치하고 사용할 수 있습니다.
- Windows 10에서도 녹화 중 컨트롤 창과 메뉴가 녹화 결과에 찍히지 않습니다.
- 갤러리와 편집기가 화면에 표시되는 영상을 원본과 비교해 밝기와 색 범위를 맞춥니다.
- 프레임 편집과 MP4·GIF 내보내기가 전체 범위 영상의 색을 원본대로 유지합니다.
- 자막이 있는 영상 프레임을 정지 이미지로 편집해도 주석을 나중에 수정할 수 있습니다.

### 검색어

캡처, 스크린샷, OCR, 화면 녹화, GIF, 주석, 핀

## English (en-US)

### Short description

A free Windows screenshot and pinning tool for developers. Annotate error screenshots and keep reference code or images above other windows.

### Description

MyCapture is a free Windows screenshot and pinning tool for developers. Use it to keep code and reference images in view, or capture an error screen when explaining a problem.

The default shortcuts are Ctrl+Shift+C for region capture, F3 to pin clipboard content, and Ctrl+Shift+X for region recording. You can change them in Settings.

Capture a region, a window or the full screen, then mark the details with arrows, step numbers and text. Crop or rotate images, and cover parts with blur or mosaic when preparing a bug report or showing changes in a review.

Pin code examples or screenshots above other windows while you work. Pins support captured images as well as text and tables from the clipboard, and you can reopen pins you have closed.

Use OCR to copy text from an image, or extract a table as CSV, Markdown or tab-separated text. Search captures in the library, organize them into folders, and export folders or ZIP archives.

When a screenshot is not enough to show what happens, record a screen region to MP4. Trim the recording, add text, then export MP4 or GIF. Recordings do not include audio. On Windows 10, a yellow border may appear around the screen while recording; it is not included in the recording.

MyCapture supports a tray menu, Korean and English interfaces, and six themes including high contrast.

Requirements: Windows 11 or Windows 10 version 22H2 (x64). ARM64 PCs need Windows 11.

### Features

- Region capture across monitors, plus window, full screen, delayed and scrolling capture
- Window highlighting with click-to-select and color value copying while selecting
- Non-destructive annotation with ellipse, highlighter, step numbers, mosaic, blur, undo and redo
- Pin clipboard images, text and tables on top of other windows, and reopen closed pins
- On-device OCR for mixed Korean and English text
- Text capture that copies only the recognized text of a selected region
- Quick Redact for sensitive details found by OCR
- One library for images and MP4 videos with in-app playback
- Region MP4 recording with pause and non-destructive video editing
- GIF export with a preview sample, estimated size and settings recommended for the share target's size limit
- Korean and English interface with six themes, including high contrast
- Keyboard and screen reader accessibility
- No account, ads or capture limits

### Screenshot captions

Enter them in screenshot order. Each caption is 200 characters or fewer.

1. The library keeps captures and recordings by date. Search by window title or by text inside images.
2. Arrows, rectangles and text stay as layers, so you can still change them after saving.
3. Press Ctrl+Shift+R to find email addresses, phone numbers and card numbers and cover them at once.
4. Right after a capture, press C to copy, E to edit, P to pin, S to save or T to copy the text.
5. Press F3 to pin clipboard images, tables or code above other windows.
6. Korean and English text in images is recognized on this PC and copied as text.
7. Choose the part of a recording to keep and add text timed to the video.
8. Check the estimated size and recommended settings before exporting MP4 or GIF.
9. Change the language, six themes, accent color, shortcuts and retention in Settings.
10. The library in a light theme. Windows 11 and Windows 10 22H2 get the same features.

### What's new in this version (4.3.0)

- Install and use MyCapture on Windows 10 version 22H2 (build 19045) x64.
- On Windows 10, the recording controls and menus stay out of the recording.
- The gallery and editor compare the displayed video with the source and match its brightness and color range.
- Frame edits and MP4 or GIF exports keep the colors of full-range videos.
- Annotations on still images made from video frames with captions stay editable.

### Search terms

capture, screenshot, OCR, screen recording, GIF, annotation, pin

## 제출 옵션: 제한 기능 사용 사유

Partner Center의 Submission options > Restricted capabilities에 영어로 입력합니다.

> **runFullTrust**: MyCapture is a Win32 desktop screen capture tool built with .NET and WPF. Full trust is required to capture the screen with GDI, register user-configurable global shortcuts with RegisterHotKey, show a notification-area icon, record screen regions with Media Foundation, run on-device OCR, and read the address of the browser page being captured through UI Automation. The app installs no drivers or services, never elevates, and keeps captures on the user's PC.

## 인증 참고 사항 (Notes for certification)

> MyCapture runs in the notification area after launch and opens its library window. Default shortcuts: Ctrl+Shift+C region capture, Ctrl+Shift+X start or stop region recording, Ctrl+Shift+Z open the library, F3 pin clipboard content. Capture, editing, recording and OCR run on the PC. The only automatic network access is downloading missing OCR model files from modelscope.cn at startup; the optional GitHub image attachment requires the user's own GitHub account. Launch at sign-in is off by default and can be turned on in Settings > General. Updates are delivered only through the Microsoft Store; there is no in-app updater, and Settings > Update opens the MyCapture page in the Store app. After at least 10 successful captures over 3 or more days, the Store package calls StoreContext.RequestRateAndReviewAppAsync once and never asks again; Settings > Update also offers a manual review link. The app reads the Windows Print Screen keyboard setting only to show guidance and never changes it.
