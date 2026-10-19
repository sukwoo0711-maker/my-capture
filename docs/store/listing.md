# Microsoft Store 목록 초안

Partner Center의 Store 목록과 제출 옵션에 붙여 넣을 4.4.0 문구입니다. 한국어(ko-KR)와 영어(en-US) 목록을 함께 작성합니다.

## 공통 항목

- **제품 이름**: 두 언어 모두 `MyCapture`. 패키지 DisplayName과 같습니다.
- **예약해 둔 이름**: `MyCapture - 캡처·가리기·핀`, `MyCapture - Screenshot, Redact & Pin` (2026-10-20 예약). Store 정책 10.1.1은 제품 이름에 설명 문구나 키워드를 넣지 못하게 하므로 목록에는 쓰지 않습니다. 설명어는 짧은 소개 첫 문장과 검색어에 넣었습니다. 예약한 이름은 3개월 안에 제출에서 쓰지 않으면 풀릴 수 있습니다.
- **Store ID**: `9PB33MC095VR`, https://apps.microsoft.com/detail/9PB33MC095VR
- **게시자 표시 이름**: `MyCapture`. 계정 설정 > Legal info > Contact info의 Publisher name에서 바꿉니다. 바꾸면 계정이 추가 검토를 받고, 이후 패키지는 `package-store.ps1 -PublisherDisplayName 'MyCapture'`로 다시 만들어야 합니다. 2026-10-20에는 4.3.1(제출 12, `sukwoo0711`로 빌드)이 인증 중이라 게시 뒤에 바꿉니다.
- **개발자(Developed by)**: `MyCapture`
- **저작권**: `Copyright (c) 2026 MyCapture`
- **범주**: 생산성 또는 사진 및 비디오
- **가격**: 무료
- **개인정보처리방침 URL**: `https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md`
- **지원 연락처**: `https://github.com/sukwoo0711-maker/mycapture-support/issues`
- **웹 사이트**: `https://sukwoo0711-maker.github.io/my-capture/`
- **앱 타일 아이콘(선택)**: 패키징 결과물 `StoreListingIcon-300.png`
- **스크린샷**: `build\store-shots\compose.py`로 만드는 언어별 9장(2560×1440)입니다. 그래파이트 바탕에 장마다 짧은 제목 한 줄과 실제 창을 크게 잘라 넣습니다. 순서는 개인정보 가리기, 핀, 오류 화면 주석, 캡처 직후 카드, 텍스트 복사, 라이브러리 검색, 영상 편집, GIF 내보내기, 'Windows 캡처 도구에서 옮겨 오면' 비교 카드입니다. 제목은 `build\store-shots\scenes.json`에 있고, 언어별 스크린샷 캡션을 같은 순서로 넣습니다.
- **트레일러**: 기존 언어별 30초 1920×1080 60fps MP4(약 9MB)를 사용합니다. 두 영상은 장면, 키 입력, 음악이 같고 앱 화면과 예시 문의 페이지의 언어가 다릅니다. 4.4.0부터 썸네일은 어두운 코드 편집기 위에 밝은 핀이 뜬 22.333초 프레임입니다(4K 원본을 1.10배로 키워 1920×1080으로 자름, 가운데 재생 단추 자리에 핵심 내용 없음). 파일은 `artifacts/promo-2026-10-18/v2/MyCapture-trailer-thumbnail-v3-1920x1080.png`와 `v2-en/MyCapture-trailer-thumbnail-en-v3-1920x1080.png`이고, 고른 근거는 같은 폴더의 `thumb-440/`에 있습니다. 이전 썸네일(19.02초, 밝은 양식 위 검은 가림막)은 Store 재생 단추에 가려 판독되지 않았습니다. 자막 파일은 사용하지 않습니다.
- **16:9 Super hero art**: 3840×2160 PNG, 두 언어 공통. 제품 이름과 앱 화면 없이 로고만 넣은 기존 이미지를 사용하며, 트레일러를 목록 맨 위에 재생하도록 지정합니다.
- **시스템 요구 사항**: Windows 10 버전 22H2(빌드 19045) 이상, x64. ARM64 PC의 x64 에뮬레이션은 Windows 11(빌드 22000) 이상

## 한국어 (ko-KR)

### 짧은 소개

광고·계정·캡처 횟수 제한이 없는 개발자용 무료 Windows 캡처·핀 도구입니다. 캡처와 글자 인식은 이 PC 안에서 처리합니다.

### 설명

MyCapture는 개발자를 위한 무료 Windows 캡처·핀 도구입니다. 광고와 계정이 없고 캡처 횟수도 제한하지 않습니다. 캡처, 편집, 글자 인식, 녹화는 모두 이 PC 안에서 처리합니다.

단축키:

- Ctrl+Shift+C: 영역 캡처
- Ctrl+Shift+X: 영역 녹화 시작·종료
- Ctrl+Shift+R: 이미지 편집기의 개인정보 가리기

전역 단축키는 설정에서 바꿉니다. 클립보드 내용을 핀으로 띄우는 단축키(예: F3)와 라이브러리 열기 단축키는 처음에는 비어 있으며, 설정에서 지정합니다.

영역·창·전체 화면·지연·스크롤 캡처를 지원합니다. 오류 화면에 화살표, 텍스트, 단계 번호를 넣고 블러나 모자이크로 일부를 가립니다. 주석은 비파괴 레이어로 남아 나중에 수정합니다.

개인정보 가리기는 이미지에서 인식한 글자를 바탕으로 이메일, 국내 전화번호, 카드번호, 주민등록번호, IPv4 주소와 지원 형식의 API 키·토큰을 찾아 가립니다.

핀은 이미지·텍스트·표를 다른 창 위에 띄웁니다. 코드 옆에 참고 화면을 두고 작업할 때 쓰며, 최근 닫은 핀도 다시 엽니다.

텍스트 복사는 이미지나 터미널 화면의 한국어·영어를 PC에서 인식해 클립보드에 복사합니다. 표는 CSV·Markdown·탭 구분 형식으로 복사합니다.

신규 설치에서는 캡처 직후 이미지가 클립보드에 복사되고 작은 카드가 나타납니다. 카드를 클릭하거나 Alt+Q를 누른 뒤 E 편집, P 핀, S 저장, T 텍스트 복사를 키 하나로 실행합니다. C는 다시 복사합니다.

라이브러리는 창 제목과 이미지 속 글자로 검색하며, 폴더로 정리하고 폴더나 ZIP으로 내보냅니다. 신규 설치에서는 보관 기간 제한이 없고, 폴더 밖 캡처가 5 GiB나 1,000개를 넘을 때만 오래된 항목부터 정리합니다. 보관으로 표시하거나 폴더에 넣은 항목은 자동 정리에서 제외합니다.

영역 녹화는 소리 없이 MP4로 저장합니다. 영상 편집기에서 구간을 자르고 텍스트를 넣은 뒤 MP4나 GIF로 내보냅니다. GIF 내보내기에는 예상 크기가 표시됩니다.

한국어·영어 인터페이스, 트레이 메뉴, 키보드 조작과 스크린 리더 접근성을 지원합니다. 테마는 Windows 설정 따르기·밝게·어둡게·고대비의 네 가지입니다.

지원 환경: Windows 11, Windows 10 버전 22H2(x64). ARM64 PC에서는 Windows 11이 필요합니다. Windows 10에서는 녹화 중 노란 테두리가 보일 수 있으며, 녹화 결과에는 포함되지 않습니다.

### 기능 목록

- 광고·계정·캡처 횟수 제한 없음, 캡처·글자 인식은 이 PC에서 처리
- 개인정보 가리기: 이메일, 국내 전화번호, 카드번호, 주민등록번호, IPv4 주소, 지원 형식의 API 키·토큰 탐지
- 핀: 이미지·텍스트·표를 다른 창 위에 표시, 최근 닫은 핀 다시 열기
- 텍스트 복사: 한국어·영어 인식, 표의 CSV·Markdown·탭 구분 형식 복사
- 영역·창·전체 화면·지연·스크롤 캡처
- 화살표·텍스트·단계 번호·블러·모자이크, 비파괴 주석 레이어 편집
- 캡처 직후 클립보드 복사와 카드의 복사·편집·핀·저장·텍스트 복사
- 라이브러리의 창 제목·이미지 속 글자 검색, 폴더 정리, 보관, 폴더·ZIP 내보내기
- 소리 없는 영역 MP4 녹화, 구간 자르기와 텍스트 추가, MP4·GIF 내보내기와 GIF 예상 크기
- 한국어·영어, Windows 설정 따르기·밝게·어둡게·고대비 테마, 키보드·스크린 리더 접근성

### 스크린샷 캡션

스크린샷 순서대로 넣습니다. 장마다 200자 이하입니다.

1. 오류 보고서의 이메일, 토큰, 카드번호를 개인정보 가리기로 가린 화면입니다. 이미지 편집기에서 Ctrl+Shift+R로 실행합니다.
2. 코드 옆에 참고 이미지를 핀으로 띄운 화면입니다. 캡처 직후 카드에서 Alt+Q를 누른 뒤 P를 누르거나, 설정에서 지정한 단축키로 클립보드 내용을 띄웁니다.
3. 오류 화면에 화살표와 단계 번호, 텍스트를 넣은 모습입니다. 주석 레이어는 나중에 수정합니다.
4. 캡처 직후 작은 카드가 뜹니다. 클릭하거나 Alt+Q를 누른 뒤 E 편집, P 핀, S 저장, T 텍스트 복사를 키 하나로 실행합니다.
5. 터미널 화면의 한국어·영어를 텍스트 복사로 읽은 결과입니다. 결과 창에서 Ctrl+C로 복사합니다.
6. 이미지 속 오류 문구와 창 제목으로 라이브러리를 검색합니다.
7. 영상 편집기에서 남길 구간을 고르고 텍스트를 넣은 모습입니다. I와 O로 구간의 시작과 끝을 정합니다.
8. GIF로 내보내기 전 예상 크기를 확인하는 화면입니다. 영상 편집기에서 G로 엽니다.
9. 영역·창·전체 화면 캡처, 클립보드 복사, 이미지 속 글자 복사, 영역 녹화는 Windows 캡처 도구와 같습니다. 여기에 핀, 스크롤 캡처, 블러·모자이크·단계 번호, 글자로 찾는 라이브러리, 카드번호·토큰 자동 탐지가 더해집니다.

### 이 버전의 새로운 기능 (4.4.0)

입력 한도는 1,500자입니다.

> 리드 확정본 대기: 4.4.0 출시 문구가 확정되면 이 자리에 넣습니다.

### 검색어

캡쳐, 캡처 프로그램, 화면 캡처, 스크린샷, 스크롤 캡처, 모자이크, 핀

Partner Center 한도: 7개 검색어, 검색어당 40자 이하, 전체 고유 단어 21개 이하. 위 목록은 검색어 7개, 고유 단어 8개입니다. 쉼표로 검색어를 나눈 뒤 공백으로 단어를 나누어 중복을 제외했습니다. '캡처 도구'는 Windows 캡처 도구(Snipping Tool)의 한국어 제품명이라 Store 정책 10.1.3(다른 제품 이름 금지)에 걸릴 수 있어 '캡처 프로그램'으로 바꿨습니다.

## English (en-US)

### Short description

A free Windows screenshot and pinning tool for developers. No ads, no account and no capture limits. Capture and text recognition run on your PC.

### Description

MyCapture is a free Windows screenshot and pinning tool for developers. There are no ads, no account and no capture limits, and capture, editing, text recognition and recording all run on your PC.

Shortcuts:

- Ctrl+Shift+C: Region capture
- Ctrl+Shift+X: Start or stop region recording
- Ctrl+Shift+R: Redact in the image editor

Global shortcuts can be changed in Settings. The shortcuts for pinning clipboard content (for example F3) and opening the library start unassigned; set them in Settings.

Capture a region, window or full screen, or use delayed and scrolling capture. Annotate error screenshots with arrows, text and step numbers. Blur and mosaic cover parts of an image. Non-destructive layers remain editable.

Redact finds email addresses, Korean phone numbers, payment card numbers, Korean resident registration numbers, IPv4 addresses, and supported API key and token formats in recognized text, then covers them in the image.

Pin keeps images, text and tables above other windows for reference beside your code. Recently closed pins can be reopened.

Copy text recognizes Korean and English in images or terminal screenshots on this PC and copies the result to the clipboard. Tables can be copied as CSV, Markdown or tab-separated text.

On a new installation, a capture goes to the clipboard and a small card appears. Click it or press Alt+Q, then press E (edit), P (Pin), S (save) or T (Copy text). C copies again.

Search the library by window title or text inside images. Organize captures into folders and export to a folder or ZIP. New installations keep captures with no time limit; automatic cleanup removes the oldest captures outside folders only after they pass 5 GiB or 1,000 items. Items marked Keep or stored in folders are never cleaned up automatically.

Record a screen region to MP4 without audio. Trim the video, add text and export MP4 or GIF. GIF export shows an estimated file size.

The interface supports Korean and English, a tray menu, keyboard access and screen readers. Four theme choices are available: follow Windows, light, dark and high contrast.

Requirements: Windows 11 or Windows 10 version 22H2 (x64). ARM64 PCs need Windows 11. On Windows 10, a yellow border may appear during recording; it is not included in the recording.

### Features

- No ads, account or capture limits; captures and text recognition stay on this PC
- Redact: email addresses, Korean phone numbers, payment card numbers, Korean resident registration numbers, IPv4 addresses, and supported API key and token formats
- Pin: images, text and tables above other windows; recently closed pins can be reopened
- Copy text: Korean and English recognition, plus tables as CSV, Markdown or tab-separated text
- Region, window, full screen, delayed and scrolling capture
- Arrows, text, step numbers, blur and mosaic in editable, non-destructive annotation layers
- Clipboard copy after capture, with a card for copy, edit, Pin, save and Copy text
- Library search by window title or image text, folders, Keep, and folder or ZIP export
- Silent region MP4 recording, trimming and text overlays, MP4 or GIF export, and GIF size estimates
- Korean and English; follow Windows, light, dark or high contrast themes; keyboard and screen reader access

### Screenshot captions

Enter them in screenshot order. Each caption is 200 characters or fewer.

1. Redact covers an email address, token and card number in an error report. Press Ctrl+Shift+R in the image editor.
2. A reference image pinned beside the code. Press Alt+Q and then P on the capture card, or pin clipboard content with a shortcut assigned in Settings.
3. An error screenshot with arrows, step numbers and text. Annotation layers remain editable.
4. A small card appears after each capture. Click it or press Alt+Q, then press E to edit, P to pin, S to save or T to copy text.
5. Copy text reads Korean and English from a terminal screenshot. Press Ctrl+C in the result window to copy it.
6. Library search finds an error message inside an image. Window titles are searchable too.
7. The video editor shows a trimmed recording with text. Press I and O to set the start and end of the selected range.
8. Check the estimated size before exporting a GIF. Press G in the video editor to open this view.
9. Windows Snipping Tool and MyCapture both capture, copy to the clipboard, extract text and record regions. MyCapture adds pins, scrolling capture, blur, mosaic, steps, search and card/token detection.

### What's new in this version (4.4.0)

Limit: 1,500 characters.

> Pending the final 4.4.0 release notes.

### Search terms

screenshot, screenshot tool, screen capture, redact, pin screenshot, OCR, print screen

Partner Center limits: 7 terms, 40 characters per term, and 21 unique words in total. This list has 7 terms and 8 unique words, counted by splitting terms at commas and words at spaces, then removing duplicates without regard to case. Store policy 10.1.3 does not allow other products' titles in search terms, so "snipping tool alternative" is not used.

## 제출 옵션: 제한 기능 사용 사유

Partner Center의 Submission options > Restricted capabilities에 영어로 입력합니다.

> **runFullTrust**: MyCapture is a Win32 desktop screen capture tool built with .NET and WPF. Full trust is required to capture the screen with GDI, register user-configurable global shortcuts with RegisterHotKey, show a notification-area icon, record screen regions with Media Foundation, run on-device OCR, and read the address of the browser page being captured through UI Automation. The app installs no drivers or services, never elevates, and keeps captures on the user's PC.

## 인증 참고 사항 (Notes for certification)

> MyCapture runs in the notification area. The first-run screen lets the user start a capture or skip to the library. On new installations, Ctrl+Shift+C captures a region and Ctrl+Shift+X starts or stops region recording. Pin and library global shortcuts are unassigned; existing installations retain their saved shortcuts. Ctrl+Shift+R runs Redact inside the image editor. A new installation copies each capture to the clipboard and shows the quick-access card by default. Alt+Q or a click moves focus to the card; its C/E/P/S/T keys then work.
>
> Capture, editing, recording and OCR run on the PC. The only automatic network access is downloading missing OCR model files. The optional GitHub image attachment requires the user's own GitHub account. The package's startup task is disabled at install. The first-run screen offers launch at sign-in with its checkbox selected, and the user can clear it there or change it later in Settings > General.
>
> Updates are delivered through the Microsoft Store. Settings > About lets the user check for and install Store package updates through the Store APIs, with consent before installation. After at least 10 successful captures and 3 days of use, the Store package offers one inline review invitation when the user returns to the app. It calls StoreContext.RequestRateAndReviewAppAsync only after the user chooses Review. Settings > About also offers a manual review link. The app reads the Windows Print Screen keyboard setting only to show guidance and never changes it.
