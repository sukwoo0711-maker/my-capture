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

### 짧은 소개

개발자를 위한 무료 Windows 11 캡처·핀 도구. 오류 화면에 주석을 달고, 참고할 코드와 이미지를 다른 창 위에 띄울 수 있습니다.

### 설명

MyCapture는 개발자를 위한 무료 Windows 11 캡처·핀 도구입니다. 코드와 참고 화면을 곁에 두고 작업하거나, 오류가 발생한 화면을 캡처해 설명할 때 사용합니다.

원하는 영역, 창, 전체 화면을 캡처하고 화살표·단계 번호·텍스트로 필요한 부분을 표시할 수 있습니다. 이미지를 자르거나 회전하고, 블러와 모자이크로 일부를 가릴 수 있습니다. 오류 재현 절차나 코드 리뷰에서 설명할 내용을 이미지로 정리할 때 유용합니다.

코드 예제나 비교할 화면은 핀으로 띄워 다른 창을 보면서 참고할 수 있습니다. 캡처한 이미지뿐 아니라 클립보드의 텍스트와 표도 다른 창 위에 표시하며, 닫은 핀을 다시 열 수 있습니다.

OCR로 이미지 속 글자를 텍스트로 복사하고, 표를 CSV·Markdown·탭 구분 형식으로 가져올 수 있습니다. 캡처는 라이브러리에서 검색하고 폴더로 정리하며, 폴더나 ZIP으로 내보낼 수 있습니다.

움직임을 설명해야 할 때는 화면 영역을 MP4로 녹화합니다. 녹화한 영상은 구간을 자르고 텍스트를 추가한 뒤 MP4나 GIF로 내보낼 수 있습니다. 녹화에는 소리가 포함되지 않습니다.

전역 단축키와 트레이 메뉴를 지원하며, 한국어·영어 인터페이스와 고대비를 포함한 6가지 테마를 제공합니다.

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

### 이 버전의 새로운 기능 (4.2.1)

- 필드별 검색과 조건 칩으로 캡처를 찾고, 여러 캡처의 태그를 한 번에 편집할 수 있습니다.
- PNG·JPEG·MP4를 끌어 놓아 가져오며 중복은 건너뜁니다. 이미지를 PDF로 묶거나 두 장을 비교할 수 있습니다.
- 확정한 텍스트를 다시 수정하고 굵기와 배경 판을 바꿀 수 있습니다. 편집기에서 여러 주석을 드래그로 함께 선택해 편집할 수 있습니다.
- 새 앱 아이콘을 적용하고, 기본 포인트 색을 아이콘의 빨강으로 맞췄습니다. 바이올렛도 고를 수 있습니다.
- 이미지 내보내기에 형식·크기 프리셋과 꾸미기를 추가했습니다. 출처 URL 포함은 기본적으로 꺼져 있습니다.
- 녹화의 여러 구간을 잘라 MP4·GIF로 내보낼 수 있습니다. 저장 진행률·예상 남은 시간·취소와 타임라인 자동 맞춤을 지원합니다.

### 검색어

캡처, 스크린샷, OCR, 화면 녹화, GIF, 주석, 핀

## English (en-US)

### Short description

A free screenshot and pinning tool for developers on Windows 11. Annotate error screenshots and keep reference code or images above other windows.

### Description

MyCapture is a free screenshot and pinning tool for developers on Windows 11. Use it to keep code and reference images in view, or capture an error screen when explaining a problem.

Capture a region, a window or the full screen, then mark the details with arrows, step numbers and text. Crop or rotate images, and cover parts with blur or mosaic when preparing a bug report or showing changes in a review.

Pin code examples or screenshots above other windows while you work. Pins support captured images as well as text and tables from the clipboard, and you can reopen pins you have closed.

Use OCR to copy text from an image, or extract a table as CSV, Markdown or tab-separated text. Search captures in the library, organize them into folders, and export folders or ZIP archives.

When a screenshot is not enough to show what happens, record a screen region to MP4. Trim the recording, add text, then export MP4 or GIF. Recordings do not include audio.

MyCapture supports global shortcuts, a tray menu, Korean and English interfaces, and six themes including high contrast.

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

### What's new in this version (4.2.1)

- Find captures with field search and filter chips. Edit tags for multiple captures at once.
- Drag in PNG, JPEG and MP4 files, skipping duplicates. Arrange images in a PDF or compare two images.
- Reopen text annotations to edit them, and toggle bold text and background plates. Drag to select several annotations and edit them together.
- A new app icon, with the accent color now matching its red pin by default. Violet remains available.
- Export images with format and size presets, margins, rounded corners, backgrounds and shadows. Source URL metadata is off by default.
- Remove multiple recording segments and export MP4 or GIF. Library saving shows progress, estimated time remaining and cancel. Timeline layers snap to nearby boundaries.

### Search terms

capture, screenshot, OCR, screen recording, GIF, annotation, pin

## 제출 옵션: 제한 기능 사용 사유

Partner Center의 Submission options > Restricted capabilities에 영어로 입력합니다.

> **runFullTrust**: MyCapture is a Win32 desktop screen capture tool built with .NET and WPF. Full trust is required to capture the screen with GDI, register user-configurable global shortcuts with RegisterHotKey, show a notification-area icon, record screen regions with Media Foundation, run on-device OCR, and read the address of the browser page being captured through UI Automation. The app installs no drivers or services, never elevates, and keeps captures on the user's PC.

## 인증 참고 사항 (Notes for certification)

> MyCapture runs in the notification area after launch and opens its library window. Default shortcuts: Ctrl+Shift+C region capture, Ctrl+Shift+X start or stop region recording, Ctrl+Shift+Z open the library, F3 pin clipboard content. Capture, editing, recording and OCR run on the PC. The only automatic network access is downloading missing OCR model files from modelscope.cn at startup; the optional GitHub image attachment requires the user's own GitHub account. Launch at sign-in is off by default and can be turned on in Settings > General. Updates are delivered only through the Microsoft Store; there is no in-app updater, and Settings > Update opens the MyCapture page in the Store app. After at least 10 successful captures over 3 or more days, the Store package calls StoreContext.RequestRateAndReviewAppAsync once and never asks again; Settings > Update also offers a manual review link. The app reads the Windows Print Screen keyboard setting only to show guidance and never changes it.
