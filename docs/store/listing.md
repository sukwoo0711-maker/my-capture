# Microsoft Store 목록 초안

Partner Center의 Store 목록과 제출 옵션에 붙여 넣을 문구입니다. 매니페스트가 한국어(ko-KR)와 영어(en-US)를 선언하므로 두 언어 목록을 모두 작성합니다. 문구는 [디자인 기준](../../design-system/mycapture/MASTER.md)의 브랜드 약속과 목소리 규칙을 따릅니다.

> **보이는 그대로, 내 PC에 남깁니다.** / Keep what you see — on your PC.

## 공통 항목

- **제품 이름**: `MyCapture` (Store ID `9PB33MC095VR`, https://apps.microsoft.com/detail/9PB33MC095VR)
- **범주**: 생산성 또는 사진 및 비디오
- **가격**: 무료
- **개인정보처리방침 URL**: `https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md`
- **지원 연락처**: `https://github.com/sukwoo0711-maker/mycapture-support/issues`
- **웹 사이트**: `https://github.com/sukwoo0711-maker/mycapture-support`
- **저작권**: `Copyright (c) 2026 MyCapture contributors`
- **1:1 앱 타일 아이콘(300 × 300, 권장)**: 패키징 결과물 `StoreListingIcon-300.png`. Store는 패키지 안의 로고보다 이 이미지를 우선 사용합니다.
- **16:9 Super hero art(3840 × 2160, 권장)**: [`assets/hero-3840x2160.png`](assets/hero-3840x2160.png). 원본은 [`hero.svg`](../../design-system/mycapture/store/hero.svg)입니다. Store 지침에 따라 글자와 앱 화면을 넣지 않았고, 핵심 이미지는 위쪽 3분의 2 안에 두었습니다.
- **스크린샷**: `--selftest-store-shots`로 만든 언어별 10장 (만드는 방법은 [`README.md`](README.md#store-스크린샷-만들기)). 스크린샷에는 로고나 홍보 문구를 덧붙이지 않고, 아래 **스크린샷 캡션**을 Partner Center의 캡션 칸(200자 이하)에 넣습니다.
- **시스템 요구 사항**: Windows 11 버전 21H2(빌드 22000) 이상, x64

## 한국어 (ko-KR)

### 짧은 설명

영역을 놓는 순간 내 PC에 남는 캡처 도구입니다. 주석, 화면 고정, 텍스트 인식, 빠른 가리기, 녹화와 GIF까지 PC 안에서 처리합니다.

### 설명

MyCapture는 영역을 놓는 순간 내 PC에 남는 Windows 캡처 도구입니다.

드래그한 영역은 편집기가 열리기 전에 라이브러리에 저장되고 클립보드에 복사됩니다. 편집기를 Esc로 닫아도 원본은 남습니다. 찍은 화면은 날짜와 폴더로 정리되고, 창 이름이나 이미지 속 글자로 다시 찾을 수 있습니다.

캡처와 텍스트 인식은 PC 안에서 처리합니다. 계정, 광고, 자동 업로드가 없으며, 캡처는 직접 보내기 전까지 PC 밖으로 나가지 않습니다. 공유하기 전에는 빠른 가리기로 이메일·전화번호·카드번호 같은 민감정보를 한 번에 가릴 수 있습니다.

주석은 원본 위에 얹는 레이어라서 저장한 뒤에도 다시 열어 고칠 수 있습니다. 화면 영역은 MP4로 녹화하고, 영상 편집기에서 구간을 자르고 시간에 맞춰 텍스트를 얹은 뒤 MP4나 GIF로 내보냅니다. 녹화에는 소리가 포함되지 않습니다.

Ctrl+Shift+C로 캡처하고, Ctrl+Shift+X로 녹화하고, Ctrl+Shift+Z로 라이브러리를 엽니다. 로그인할 때 자동으로 시작하는 기능은 처음에는 꺼져 있으며, 설정에서 켤 수 있습니다.

### 기능 목록

- 영역을 놓으면 편집기가 열리기 전에 라이브러리 저장과 클립보드 복사가 끝납니다
- 모니터 경계를 넘나드는 영역 캡처와 창, 전체 화면, 지연, 스크롤 캡처
- 이미지와 MP4를 날짜·폴더별로 모으고 창 이름과 이미지 속 글자로 찾는 라이브러리
- 사각형·화살표·텍스트·단계 번호·형광펜·모자이크 주석과 실행 취소·다시 실행
- 클립보드의 이미지·텍스트·엑셀 표를 항상 위에 보이는 창으로 고정(F3)
- 한국어·영어를 PC 안에서 읽는 텍스트 인식과 표의 CSV·Markdown 복사
- OCR로 찾은 민감정보를 편집 가능한 가림막으로 한 번에 가리는 빠른 가리기
- 영역 MP4 녹화와 원본을 건드리지 않는 영상 편집, 최대 20초 GIF 내보내기
- 한국어·영어 인터페이스와 고대비를 포함한 6가지 테마
- 키보드 조작과 스크린 리더 지원

### 스크린샷 캡션

`--selftest-store-shots`가 만드는 파일 순서와 같습니다. 각 캡션은 200자 이하입니다.

| 파일 | 캡션 |
|---|---|
| `01-library` | 찍은 화면은 모두 라이브러리에 남습니다. 이미지와 MP4를 날짜별로 모아 두고, 창 이름이나 이미지 속 글자로 다시 찾습니다. |
| `02-annotation` | 사각형, 화살표, 텍스트, 단계 번호로 설명을 더합니다. 주석은 원본 위의 레이어라서 저장한 뒤에도 다시 열어 고칠 수 있습니다. |
| `03-quick-redact` | 공유하기 전에 Ctrl+Shift+R을 누르면 PC 안의 OCR이 이메일·전화번호·카드번호를 찾아 가립니다. 가림막은 옮기거나 지울 수 있습니다. |
| `04-after-capture` | 영역을 놓는 순간 라이브러리에 저장하고 클립보드에 복사합니다. 빠른 작업 카드에서 편집, 화면 고정, 저장으로 바로 이어 갑니다. |
| `05-pins` | F3으로 클립보드의 이미지, 텍스트, 엑셀 표를 다른 창 위에 고정합니다. 확대, 투명도, 클릭 통과를 지원합니다. |
| `06-ocr` | 이미지 속 한국어와 영어를 PC 안에서 읽습니다. 표는 CSV·Markdown으로, 코드는 들여쓰기를 지켜 꺼냅니다. |
| `07-video-editor` | Ctrl+Shift+X로 영역을 MP4로 녹화합니다. 원본은 그대로 두고 구간을 자르고, 시간에 맞춰 텍스트를 얹습니다. 녹화에는 소리가 없습니다. |
| `08-gif-export` | 최대 20초 구간을 GIF로 내보냅니다. 크기와 속도를 고르면 저장하기 전에 실제 파일 크기를 계산해 보여 줍니다. |
| `09-settings` | 단축키, 저장 위치, 보관 기간, 언어와 테마를 한곳에서 정합니다. 설정은 내보내고 가져올 수 있습니다. |
| `10-library-light` | 밝은 테마를 포함한 6가지 테마를 고를 수 있습니다. 화면은 조용하게 두고, 찍은 내용이 가장 잘 보이게 만들었습니다. |

### 이 버전의 새로운 기능 (4.1.0)

- 라이브러리의 날짜별 폴더와 하위 폴더로 캡처를 정리하고 폴더·ZIP으로 내보낼 수 있습니다.
- 폴더에 보관한 캡처는 자동 정리에서 제외되며, 폴더에서 꺼내면 보관 기간이 다시 시작됩니다.
- 선택 도구에서 이미지 가장자리·모서리를 끌어 자르기를 시작하고 여백을 자동으로 제거할 수 있습니다.
- 표·코드·텍스트에 맞춘 추출 화면과 표의 CSV·Markdown·탭 구분 복사를 제공합니다.
- 캡처 가이드 색을 설정할 수 있으며, 첫 실행 안내·단축키 모음·빠른 작업 카드를 다듬었습니다.

새 브랜드가 들어가는 다음 제출에는 다음 문장을 더합니다.

- 새 아이콘과 색으로 바꿨습니다. 선택 영역, 포커스, 주요 버튼이 캡처한 화면의 색과 섞이지 않도록 시그널 마젠타를 사용합니다.

### 검색어

캡처, 스크린샷, OCR, 화면 녹화, GIF, 화면 고정, 개인정보 가리기

## English (en-US)

### Short description

A Windows capture tool that keeps every capture on your PC the moment you let go. Annotate, pin, read text, redact and record, all on your PC.

### Description

MyCapture is a Windows capture tool that keeps every capture on your PC the moment you let go.

A dragged region is saved to the library and copied to the clipboard before the editor opens. Close the editor with Esc and the original is still there. Captures are organized by date and folder, and you can find them again by window name or by the text inside the image.

Captures and text recognition are processed on your PC. There is no account, no ads and no automatic upload, and captures never leave your PC unless you send them. Before you share, Quick Redact covers sensitive details such as email addresses, phone numbers and card numbers in one step.

Annotations are layers over the original, so you can reopen and change them after saving. Record a screen region to MP4, then trim it, add timed text and export MP4 or GIF in the video editor. Recordings have no audio.

Press Ctrl+Shift+C to capture, Ctrl+Shift+X to record and Ctrl+Shift+Z to open the library. Launch at sign-in starts turned off and can be turned on in Settings.

### Features

- The capture is saved to the library and copied to the clipboard before the editor opens
- Region capture across monitors, plus window, full screen, delayed and scrolling capture
- One library for images and MP4 videos, organized by date and folder and searchable by window name and image text
- Rectangle, arrow, text, step number, highlighter and mosaic annotations with undo and redo
- Pin clipboard images, text and Excel tables in always-on-top windows (F3)
- On-device text recognition for Korean and English, with CSV and Markdown table copy
- Quick Redact covers sensitive details found by OCR with editable covers
- Region MP4 recording, non-destructive video editing and GIF export for clips up to 20 seconds
- Korean and English interface with six themes, including high contrast
- Keyboard operation and screen reader support

### Screenshot captions

In the order `--selftest-store-shots` writes the files. Each caption is 200 characters or fewer.

| File | Caption |
|---|---|
| `01-library` | Every capture stays in your library. Images and MP4 videos are grouped by date, and you can find them again by window name or by the text inside. |
| `02-annotation` | Explain with rectangles, arrows, text and step numbers. Annotations are layers over the original, so you can reopen and change them after saving. |
| `03-quick-redact` | Press Ctrl+Shift+R before you share. On-device OCR finds email addresses, phone numbers and card numbers and covers them. Move or remove any cover. |
| `04-after-capture` | The moment you let go, the capture is saved to the library and copied to the clipboard. Continue to edit, pin or save from the Quick Access card. |
| `05-pins` | Press F3 to pin clipboard images, text and Excel tables on top of other windows, with zoom, opacity and click-through. |
| `06-ocr` | Read Korean and English text in images on your PC. Copy tables as CSV or Markdown and keep code indentation intact. |
| `07-video-editor` | Record a region to MP4 with Ctrl+Shift+X. Trim it and add timed text while the original stays untouched. Recordings have no audio. |
| `08-gif-export` | Export clips up to 20 seconds as GIF. Choose the size and speed, and see the calculated file size before you save. |
| `09-settings` | Set shortcuts, save locations, retention, language and theme in one place. Export and import your settings. |
| `10-library-light` | Choose from six themes, including a light theme. The interface stays quiet so your captures stay in front. |

### What's new in this version (4.1.0)

- Organize captures in dated folders and subfolders, and export folders or ZIP archives.
- Captures kept in folders are excluded from automatic cleanup. Their retention period restarts when moved out.
- Start cropping by dragging an empty image edge or corner with the Select tool, or trim blank margins automatically.
- Extract tables, code and text in dedicated views, and copy tables as CSV, Markdown or tab-separated text.
- Customize capture guide colors and use refined first-run guidance, keyboard shortcut help and quick action cards.

Add this line to the next submission that ships the new brand:

- A new icon and colors. Selections, focus and primary buttons use Signal Magenta so they never blend into the colors of what you capture.

### Search terms

screenshot, screen capture, OCR, screen recorder, GIF, pin to screen, redact

## 제출 옵션: 제한 기능 사용 사유

Partner Center의 Submission options > Restricted capabilities에 영어로 입력합니다.

> **runFullTrust**: MyCapture is a Win32 desktop screen capture tool built with .NET and WPF. Full trust is required to capture the screen with GDI, register user-configurable global shortcuts with RegisterHotKey, show a notification-area icon, record screen regions with Media Foundation, run on-device OCR, and read the address of the browser page being captured through UI Automation. The app installs no drivers or services, never elevates, and keeps captures on the user's PC.

## 인증 참고 사항 (Notes for certification)

> MyCapture runs in the notification area after launch and opens its library window. Default shortcuts: Ctrl+Shift+C region capture, Ctrl+Shift+X start or stop region recording, Ctrl+Shift+Z open the library, F3 pin clipboard content. Capture, editing, recording and OCR run on the PC. The only automatic network access is downloading missing OCR model files from modelscope.cn at startup; the optional GitHub image attachment requires the user's own GitHub account. Launch at sign-in is off by default and can be turned on in Settings > General. Updates are delivered only through the Microsoft Store; the in-app GitHub updater is disabled in the Store package.
