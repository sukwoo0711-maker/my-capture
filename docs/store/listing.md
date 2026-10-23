# Microsoft Store 목록 초안

Partner Center의 Store 목록과 제출 옵션에 붙여 넣을 문구입니다. 한국어(ko-KR)와 영어(en-US) 목록을 함께 작성합니다.

2026-10-20 핀 중심으로 다시 썼습니다. MyCapture의 핵심 쓰임은 코딩하는 동안 엑셀 사양 표와 자주 쓰는 코드를 에디터 위에 띄워 두고 원문 그대로 다시 붙여 넣는 것이고, 개인정보 가리기는 공유 전 정리 기능으로 뒤에 둡니다. 핀을 띄우는 키는 F3 하나로 씁니다. 복사한 표·코드도, Ctrl+Shift+C로 캡처한 화면도 F3으로 띄웁니다. 캡처 카드의 P는 소개하지 않습니다.

이 문구는 4.5.0 동작을 기준으로 합니다. 새 설치에서 F3 클립보드 핀, Shift+F3 핀 모두 숨기기·보이기, 핀의 Ctrl+C 원문 복사, 다음 실행 때 열린 핀 다시 띄우기가 4.5.0으로 들어갔습니다.

2026-10-21 Gemini·Grok 외부 평가 합의안 2절에 따라 설명과 기능 목록을 다시 정리했습니다. 설명 첫 단락은 짧은 설명 하나로 줄이고, 단축키와 메뉴는 한 절에 모았습니다. 기능 줄은 단축키가 아니라 하는 일로 시작합니다. 녹화에 소리가 없다는 점은 설명·기능 줄·9번 캡션에 다른 항목과 같은 크기로 적고, 스크롤 캡처 한도(40프레임·32,760픽셀·256MB)를 밝혔습니다. 검색어는 핀·표·코드 단어로 바꿨습니다. 이 변경은 다음 제출 때 Partner Center에 입력합니다.

## 공통 항목

- **제품 이름**: 두 언어 모두 `MyCapture`. 패키지 DisplayName과 같습니다.
- **예약해 둔 이름**: `MyCapture - 캡처·가리기·핀`, `MyCapture - Screenshot, Redact & Pin` (2026-10-20 예약). Store 정책 10.1.1은 제품 이름에 설명 문구나 키워드를 넣지 못하게 하므로 목록에는 쓰지 않습니다. 설명어는 짧은 소개 첫 문장과 검색어에 넣었습니다. 예약한 이름은 3개월 안에 제출에서 쓰지 않으면 풀릴 수 있습니다.
- **Store ID**: `9PB33MC095VR`, https://apps.microsoft.com/detail/9PB33MC095VR
- **게시자 표시 이름**: `MyCapture`. 계정 설정 > Legal info > Contact info의 Publisher name에서 바꿉니다. 바꾸면 계정이 추가 검토를 받고, 이후 패키지는 `package-store.ps1 -PublisherDisplayName 'MyCapture'`로 다시 만들어야 합니다. 2026-10-20에는 4.3.1(제출 12, `sukwoo0711`로 빌드)이 인증 중이라 게시 뒤에 바꿉니다.
- **개발자(Developed by)**: `MyCapture`
- **저작권**: `Copyright (c) 2026 MyCapture`
- **범주**: 생산성 또는 사진 및 비디오
- **가격**: 무료
- **개인정보처리방침 URL**: `https://mimiloaf.com/mycapture/privacy/` (2026-10-24 제출 17부터. 원문은 지원 저장소의 `PRIVACY.md`이고, 사이트 페이지는 그 main 판에서 만듭니다.)
- **지원 연락처**: `support@mimiloaf.com` (문의 창구 안내는 지원 저장소 README에 있습니다)
- **웹 사이트**: `https://mimiloaf.com/mycapture/` (2026-10-24 제출 18부터. 그 전에는 `https://mimiloaf.com`)
- **앱 타일 아이콘(선택)**: 패키징 결과물 `StoreListingIcon-300.png`
- **스크린샷**: `build\store-shots\compose.py`로 만드는 언어별 9장(2560×1440)입니다. 그래파이트 바탕에 장마다 짧은 제목 한 줄과 실제 창을 크게 넣습니다. 순서는 엑셀 레지스터 표 핀과 코드, `#define` 코드 핀 원문 붙여넣기, 뒤 창 클릭 허용, 캡처 후 F3으로 핀, 이미지 핀 글자 복사, 핀 숨기기·다시 열기, 라이브러리 검색, 공유 전 주석·가리기, 'Windows 캡처 도구에 더해지는 것' 비교 카드입니다. 제목은 `build\store-shots\scenes.json`에 있고, 언어별 스크린샷 캡션을 같은 순서로 넣습니다. 결과물은 `artifacts/store/screenshots/ux461/{ko,en}`입니다. 2026-10-23 편집기 점검(#132) 화면으로 다시 렌더해 8번 편집기만 바뀌었고, 나머지 여덟 장은 이전 판(`artifacts/store/screenshots/ux453/{ko,en}`)과 같습니다. 그 이전 판은 2026-10-22 UX 개선(#119) 화면으로 7번 라이브러리와 8번 편집기를 바꾼 것이고, 나머지 일곱 장은 4.5.0 핀 흐름 판(`artifacts/store/screenshots/4.5.0-pin/{ko,en}`)과 같습니다. 2026-10-23 현재 Partner Center에는 둘 다 올리지 않았습니다. 4.4.0 제출(제출 13)에 올린 이전 9장은 `artifacts/store/screenshots/4.4.0/{ko,en}`에 그대로 있습니다.
- **트레일러**: 새 트레일러가 나올 때까지 기존 언어별 30초 1920×1080 60fps MP4(약 9MB)를 사용합니다. 두 영상은 장면, 키 입력, 음악이 같고 앱 화면과 예시 문의 페이지의 언어가 다릅니다. 4.4.0부터 썸네일은 어두운 코드 편집기 위에 밝은 핀이 뜬 22.333초 프레임입니다(4K 원본을 1.10배로 키워 1920×1080으로 자름, 가운데 재생 단추 자리에 핵심 내용 없음). 파일은 `artifacts/promo-2026-10-18/v2/MyCapture-trailer-thumbnail-v3-1920x1080.png`와 `v2-en/MyCapture-trailer-thumbnail-en-v3-1920x1080.png`이고, 고른 근거는 같은 폴더의 `thumb-440/`에 있습니다. 이전 썸네일(19.02초, 밝은 양식 위 검은 가림막)은 Store 재생 단추에 가려 판독되지 않았습니다. 자막 파일은 사용하지 않습니다. 핀 중심 30초 구성(Ctrl+C·F3 사양 표 핀 → 보면서 코딩 → 구문 핀 원문 붙여넣기 → 클릭 허용·Shift+F3 숨기기 → 캡처 후 F3)은 `docs/store/trailer-pin-storyboard.md`에 있습니다.
- **16:9 Super hero art**: 3840×2160 PNG, 두 언어 공통. 제품 이름과 앱 화면 없이 로고만 넣은 기존 이미지를 사용하며, 트레일러를 목록 맨 위에 재생하도록 지정합니다.
- **시스템 요구 사항**: Windows 10 버전 22H2(빌드 19045) 이상, x64. ARM64 PC의 x64 에뮬레이션은 Windows 11(빌드 22000) 이상

## 한국어 (ko-KR)

### 짧은 소개

엑셀 사양 표와 자주 쓰는 코드를 에디터 위에 띄워 두고 원문을 그대로 다시 복사하는 Windows 핀 도구입니다. 광고·계정 없이 무료이고, 캡처와 글자 인식은 이 PC 안에서 처리합니다.

### 설명

MyCapture는 코딩하는 동안 엑셀 사양 표와 자주 쓰는 코드를 다른 창 위에 띄워 두는 Windows 핀 도구입니다.

핀:

- 엑셀 범위는 엑셀이 클립보드에 넣는 그림을 그대로 써서 글꼴·채우기·테두리가 시트와 같게 보입니다. 셀 값은 탭 구분 원문으로 함께 보관합니다.
- 색 정보를 함께 복사하는 에디터에서 가져온 코드는 에디터 색과 들여쓰기를 유지합니다. 그 밖의 텍스트는 글자 그대로 그립니다.
- 핀의 원문을 다시 복사해 에디터나 시트에 그대로 붙여 넣습니다. 캡처한 화면을 띄운 이미지 핀에서는 이 PC에서 인식한 한국어·영어 글자를 복사합니다.
- 반투명(20–100%)으로 두거나 뒤 창 클릭을 허용하면 핀 아래 창에서 계속 입력합니다. 확대·축소는 10–800%입니다.
- 앱을 끄고 다시 실행하면 열려 있던 핀이 다시 뜹니다. 닫은 핀은 최근 20개까지 내용·배율·투명도·위치 그대로 다시 엽니다.
- 핀은 작업 표시줄과 Alt+Tab 목록에 나타나지 않습니다.

단축키와 메뉴:

- F3: 클립보드를 핀으로 띄우기. 엑셀·에디터에서 Ctrl+C 뒤에 누르면 복사한 표·코드가 핀이 되고, 에디터의 입력 위치는 그대로 남습니다.
- Shift+F3: 핀 모두 숨기기·보이기
- Ctrl+Shift+C: 영역 캡처. 캡처가 클립보드에 복사되므로 이어서 F3을 누르면 핀이 됩니다.
- Ctrl+Shift+X: 영역 녹화 시작·끝
- 핀에서 Ctrl+C 또는 Ctrl+더블클릭: 원문 복사(이미지 핀은 인식한 글자)
- 핀에서 Ctrl+휠: 투명도, 휠: 확대·축소, 드래그: 이동
- 핀 오른쪽 클릭 메뉴: 뒤 창 클릭 허용, 원문 텍스트 복사, 닫은 핀 다시 열기, 저장
- 트레이 메뉴: 클립보드를 핀으로 띄우기, 핀 모두 숨기기·보이기, 모든 핀 클릭 허용 해제, 닫은 핀 다시 열기
- 이미지 편집기에서 Ctrl+Shift+R: 자동 가리기

참고) 전역 단축키 네 개는 새로 설치했을 때의 기본값이며 설정에서 바꿉니다. 다른 프로그램이 F3을 이미 쓰고 있으면 실행할 때 알려 줍니다.

텍스트 복사는 캡처한 이미지나 이미지 핀의 한국어·영어를 PC에서 인식해 클립보드에 복사합니다. 표는 CSV·Markdown·탭 구분 형식으로 복사합니다.

라이브러리는 창 제목과 이미지 속 글자로 지난 캡처를 찾으며, 폴더로 정리하고 폴더나 ZIP으로 내보냅니다. 새로 설치하면 보관 기간 제한이 없고, 폴더 밖 항목이 5GB나 1,000개를 넘을 때만 오래된 항목부터 정리합니다. 보관으로 표시하거나 폴더에 넣은 항목은 자동 정리에서 제외합니다.

영역·창·전체 화면·지연·스크롤 캡처를 지원합니다. 스크롤 캡처는 한 번에 최대 40프레임, 세로 32,760픽셀, 256MB까지 이어 붙이고, 한도에 닿으면 거기까지 이어 붙인 이미지를 엽니다. 녹화는 화면만 기록하며 소리는 녹음하지 않습니다. 영역 녹화는 MP4로 저장하고, 영상 편집기에서 구간을 자르고 텍스트를 넣은 뒤 MP4나 GIF로 내보냅니다.

공유하기 전에는 이미지 편집기에서 화살표, 텍스트, 단계 번호를 넣고 블러나 모자이크로 일부를 가립니다. 자동 가리기는 인식한 글자에서 이메일, 국내 전화번호, 카드번호, 주민등록번호, IPv4 주소와 지원 형식의 API 키·토큰을 찾아 가립니다. 주석과 가림막은 비파괴 레이어로 남아 나중에 수정합니다.

광고와 계정이 없고 회사 업무에도 무료로 씁니다. 캡처, 편집, 글자 인식, 녹화는 모두 이 PC 안에서 처리합니다. 소스 코드는 GitHub에 MIT 라이선스로 공개되어 있습니다.

한국어·영어 인터페이스, 키보드 조작과 스크린 리더 접근성을 지원합니다. 테마는 Windows 설정 따르기·밝게·어둡게·고대비의 네 가지입니다.

지원 환경: Windows 11, Windows 10 버전 22H2(x64). ARM64 PC에서는 Windows 11이 필요합니다. Windows 10에서는 녹화 중 노란 테두리가 보일 수 있으며, 녹화 결과에는 포함되지 않습니다.

### 기능 목록

- 복사한 표·코드와 캡처한 화면을 다른 창 위에 핀으로 띄우기(F3)
- 엑셀 범위 핀: 시트에서 보던 모양 그대로 표시, 셀 값은 탭 구분 원문으로 보관
- 코드 핀: 에디터 색과 들여쓰기 유지, 원문 그대로 다시 복사
- 핀 투명도 20–100%, 뒤 창 클릭 허용, 확대·축소 10–800%
- 다음 실행 때 열린 핀 다시 띄우기, 최근 닫은 핀 20개까지 다시 열기
- 캡처와 이미지 핀의 한국어·영어 글자 복사, 표는 CSV·Markdown·탭 구분으로 복사
- 라이브러리: 창 제목·이미지 속 글자 검색, 폴더 정리, 보관, 폴더·ZIP 내보내기
- 영역·창·전체 화면·지연 캡처, 스크롤 캡처(최대 40프레임, 세로 32,760픽셀)
- 영역 녹화(MP4, 소리 없음), 구간 자르기·텍스트 넣기, MP4·GIF 내보내기
- 공유 전 정리: 화살표·텍스트·단계 번호·블러·모자이크, 이메일·카드번호·토큰 자동 가리기
- 광고·계정 없음, 캡처·글자 인식은 이 PC에서 처리, MIT 라이선스 소스 공개
- 한국어·영어, Windows 설정 따르기·밝게·어둡게·고대비 테마, 키보드·스크린 리더 접근성

### 스크린샷 캡션

스크린샷 순서대로 넣습니다. 장마다 200자 이하입니다.

1. 엑셀에서 Ctrl+C, F3으로 레지스터 표를 띄워 두고 코드를 쓰는 화면입니다. 표는 엑셀에서 보던 모양 그대로이고, 셀 값은 원문으로 함께 보관됩니다. Ctrl+휠로 반투명하게 둡니다.
2. 자주 쓰는 #if·#define 구문을 Ctrl+C, F3으로 띄워 두고, 핀을 Ctrl+더블클릭해 원문을 에디터에 붙여 넣은 모습입니다. 핀은 에디터 색과 들여쓰기를 유지합니다.
3. 뒤 창 클릭을 허용한 핀입니다. 핀이 떠 있는 자리에서도 아래 터미널을 클릭해 입력합니다. 핀의 오른쪽 클릭 메뉴에서 켜고, Shift+F3을 두 번 누르면 해제됩니다.
4. Ctrl+Shift+C로 캡처하면 클립보드에 복사되고, F3을 누르면 그 캡처가 핀이 됩니다. 웹 API 문서의 매개변수 표를 코드 옆에 띄운 모습입니다.
5. 데이터시트를 캡처한 이미지 핀에서 Ctrl+더블클릭으로 글자를 복사해 에디터에 붙여 넣은 모습입니다. 한국어·영어 글자를 이 PC에서 인식합니다.
6. Shift+F3으로 핀을 모두 숨기거나 다시 보이고, 트레이 메뉴에서 최근 닫은 핀을 위치·배율·투명도 그대로 다시 엽니다.
7. 이미지 속 오류 문구와 창 제목으로 라이브러리를 검색합니다.
8. 공유하기 전에 단계 번호와 화살표를 넣고, 이메일·토큰·카드번호를 Ctrl+Shift+R로 가린 화면입니다.
9. 영역·창·전체 화면 캡처, 클립보드 복사, 이미지 속 글자 복사는 Windows 캡처 도구와 같습니다. 영역 녹화는 소리 없이 화면만 기록합니다. 여기에 원문을 보관하는 표·코드 핀, 반투명·클릭 허용 핀, 닫은 핀 다시 열기, 스크롤 캡처가 더해집니다.

### 이 버전의 새로운 기능 (4.6.1)

입력 한도는 1,500자입니다. 4.6.0(제출 16)이 게시된 뒤 제출하므로 4.6.1의 변경만 적습니다.

- 새 버전이 나오면 앱이 알려 주고, 카드의 '업데이트'로 바로 설치합니다. 설정 > 정보에서 자동 확인을 끌 수 있습니다.
- 편집기 단추는 평소 아이콘을 크게 보여 주고, Alt를 누르는 동안 단축키가 나타납니다. Alt만 눌렀다 뗀 뒤 다음 키가 무시되던 문제도 고쳤습니다.
- 도구를 바꿔도 이미지 크기가 흔들리지 않고, 회전 단추(Q·W)를 자르기 옆에 두었습니다. 창이 좁아지면 명령 이름을 하나씩 숨깁니다.
- 웹 페이지나 앱에서 찍은 캡처에 출처를 표시하고, 같은 출처의 캡처를 모아 봅니다.
- '정확' 품질의 글자 인식이 두 엔진을 함께 써서 한국어 화면 글자를 더 정확하게 읽고, 짧은 줄을 빠뜨리지 않습니다.
- 툴팁이 열려 있는 동안 재생 위치 표시나 자동 저장이 멈추던 문제를 고쳤습니다.

### 검색어

화면 고정 핀, 항상 위에 고정, 이미지 띄우기, 표 띄우기, 코드 띄우기, 클립보드 핀, 화면 캡처

Partner Center 한도: 7개 검색어, 검색어당 40자 이하, 전체 고유 단어 21개 이하. 위 목록은 검색어 7개, 고유 단어 11개(화면, 고정, 핀, 항상, 위에, 이미지, 띄우기, 표, 코드, 클립보드, 캡처)입니다. 쉼표로 검색어를 나눈 뒤 공백으로 단어를 나누어 중복을 제외했습니다. 2026-10-21 외부 평가 합의(Grok #14)에 따라 캡처 일반명이던 '화면 고정 캡처', '스크린샷 고정', '캡처 프로그램', '캡쳐'를 핀·표·코드 단어로 바꿨습니다. 사람들은 이 기능을 '화면 고정', '항상 위에 고정', '이미지 띄우기'로 찾으므로 이 셋은 남기고, '화면 고정'은 휴대폰 화면 고정과 겹치지 않게 '핀'과 묶었습니다. Store 정책 10.1.3은 검색어에 가격 단어와 다른 제품 이름을 금지하므로 '무료', '엑셀', '캡처 도구'(Windows 캡처 도구의 한국어 제품명)는 넣지 않습니다.

## English (en-US)

### Short description

Keep your Excel spec table and go-to code snippets above the editor, and copy their original text back. Free, with no ads or account; capture and OCR stay on this PC.

### Description

MyCapture is a Windows pinning tool that keeps Excel spec tables and code snippets above your other windows while you code.

Pins:

- Excel ranges use the picture Excel puts on the clipboard, so fonts, fills and borders look as they do in the sheet. Cell values are kept as tab-separated text.
- Code copied from an editor that includes colors keeps the editor's colors and indentation. Other text is drawn as plain text.
- Copy a pin's original text back into the editor or the sheet. Image pins copy Korean and English text recognized on this PC.
- Translucent (20–100%) and click-through pins let you keep typing in the window underneath. Zoom from 10 to 800%.
- Pins open when you quit come back at the next launch. Up to 20 recently closed pins reopen with their content, zoom, opacity and position.
- Pins stay out of the taskbar and the Alt+Tab list.

Shortcuts and menus:

- F3: pin the clipboard. After Ctrl+C in Excel or your editor, the copied table or code becomes a pin and your cursor stays in the editor.
- Shift+F3: hide or show all pins
- Ctrl+Shift+C: capture a region. The capture goes to the clipboard, so F3 pins it.
- Ctrl+Shift+X: start or stop region recording
- Ctrl+C or Ctrl+double-click on a pin: copy the original text (recognized text for an image pin)
- On a pin: Ctrl+wheel for opacity, wheel to zoom, drag to move
- Pin right-click menu: Click through to windows behind, Copy original text, Reopen closed pin, Save
- Tray menu: Pin clipboard, Hide or show all pins, Turn off click through on all pins, Reopen closed pin
- Ctrl+Shift+R in the image editor: Auto-redact

Note: the four global shortcuts are the defaults for new installations; change them in Settings. If another app already uses F3, MyCapture tells you at startup.

Copy text recognizes Korean and English in captures and image pins on this PC. Tables can be copied as CSV, Markdown or tab-separated text.

Search the library by window title or by text inside images. Organize items into folders and export to a folder or ZIP. New installations keep items with no time limit; automatic cleanup removes the oldest items outside folders only after they pass 5 GB or 1,000 items. Items marked Keep or stored in folders are never cleaned up automatically.

Capture a region, window or full screen, or use delayed and scrolling capture. Scrolling capture joins up to 40 frames, 32,760 pixels in height or 256 MB, and opens what it has joined when it reaches a limit. Recording captures the screen only; no sound is recorded. Record a region to MP4, trim it, add text and export MP4 or GIF.

Before you share, add arrows, text and step numbers in the image editor, and blur or mosaic parts of the image. Auto-redact finds email addresses, Korean phone numbers, payment card numbers, Korean resident registration numbers, IPv4 addresses, and supported API key and token formats in recognized text and covers them. Annotations and covers stay on editable, non-destructive layers.

There are no ads and no account, and MyCapture is free for work use too. Capture, editing, text recognition and recording all run on this PC. The source code is published on GitHub under the MIT License.

The interface supports Korean and English, keyboard access and screen readers. Four theme choices are available: follow Windows, light, dark and high contrast.

Requirements: Windows 11 or Windows 10 version 22H2 (x64). ARM64 PCs need Windows 11. On Windows 10, a yellow border may appear during recording; it is not included in the recording.

### Features

- Pin copied tables, code and captures above other windows (F3)
- Excel range pins: shown as they look in the sheet, cell values kept as tab-separated text
- Code pins: the editor's colors and indentation, original text copied back
- Pin opacity from 20 to 100%, click-through, zoom from 10 to 800%
- Open pins return at the next launch; reopen up to 20 recently closed pins
- Copy Korean and English text from captures and image pins; copy tables as CSV, Markdown or tab-separated text
- Library search by window title or image text, folders, Keep, and folder or ZIP export
- Region, window, full screen and delayed capture; scrolling capture up to 40 frames or 32,760 pixels high
- Region recording to MP4, no sound; trim, add text, export MP4 or GIF
- Before sharing: arrows, text, step numbers, blur, mosaic and Auto-redact for emails, card numbers and tokens
- No ads or account; capture and text recognition stay on this PC; MIT-licensed source code
- Korean and English; follow Windows, light, dark or high contrast themes; keyboard and screen reader access

### Screenshot captions

Enter them in screenshot order. Each caption is 200 characters or fewer.

1. Ctrl+C in Excel, then F3: a register table pinned above the code, as it looks in the sheet, with the cell values kept as text. Ctrl+wheel makes it translucent.
2. An #if/#define snippet pinned with Ctrl+C and F3 keeps the editor's colors. Ctrl+double-click copies the original text, pasted here into a new file.
3. A click-through pin. Click and type in the terminal underneath it. Turn it on from the pin's right-click menu; press Shift+F3 twice to turn it off.
4. Ctrl+Shift+C copies a capture to the clipboard and F3 pins it: here, an API parameter table next to the code.
5. Ctrl+double-click a pinned datasheet capture to copy its text, then paste it into the editor. Korean and English text is recognized on this PC.
6. Shift+F3 hides or shows all pins, and the tray menu reopens a closed pin with its position, zoom and opacity.
7. Library search finds an error message inside an image. Window titles are searchable too.
8. Step numbers and an arrow added before sharing, with an email address, token and card number covered by Ctrl+Shift+R.
9. Like Snipping Tool, MyCapture captures, copies to the clipboard and extracts text. It adds table and code pins with original text, click-through pins and scrolling capture; recording is silent.

### What's new in this version (4.6.1)

Limit: 1,500 characters. 4.6.0 (submission 16) is published, so this lists only the 4.6.1 changes.

- MyCapture tells you when a new version is available, and Update on the card installs it. Turn off automatic checks in Settings > About.
- Editor buttons show larger icons, and shortcut keys appear while you hold Alt. Fixed keys being ignored after pressing Alt alone.
- The image no longer resizes when you switch tools, and rotate buttons (Q, W) sit next to Crop. In narrow windows, command names hide one at a time.
- Captures from a web page or app show their source, and you can view all captures from the same source.
- Text recognition at Accurate combines two engines to read Korean screen text more accurately and no longer drops short lines.
- Fixed the playback position and autosave pausing while a tooltip was open.

### Search terms

pin to screen, always on top, pin clipboard, pin table on screen, pin code snippet, floating screenshot, screen capture

Partner Center limits: 7 terms, 40 characters per term, and 21 unique words in total. This list has 7 terms and 13 unique words (pin, to, screen, always, on, top, clipboard, table, code, snippet, floating, screenshot, capture), counted by splitting terms at commas and words at spaces, then removing duplicates without regard to case. Following the 2026-10-21 review consensus (Grok #14), the generic capture terms "pin image on screen", "image always on top", "screenshot tool" and "OCR" were replaced with pin, table and code terms. Store policy 10.1.3 does not allow pricing terms or other products' titles in search terms, so "free", "Excel" and "snipping tool alternative" are not used.

## 제출 옵션: 제한 기능 사용 사유

Partner Center의 Submission options > Restricted capabilities에 영어로 입력합니다.

> **runFullTrust**: MyCapture is a Win32 desktop screen capture tool built with .NET and WPF. Full trust is required to capture the screen with GDI, register user-configurable global shortcuts with RegisterHotKey, show a notification-area icon, record screen regions with Media Foundation, run on-device OCR, and read the address of the browser page being captured through UI Automation. The app installs no drivers or services, never elevates, and keeps captures on the user's PC.

## 인증 참고 사항 (Notes for certification)

Partner Center 제품 단위의 Additional Testing Information(인증 참고)에 붙여 넣습니다. 2026-10-20 현재 그 칸에는 4.4.0과 맞지 않는 'F3 pin clipboard content' 문구가 남아 있습니다. 아래 문구는 F3·Shift+F3이 새 설치 기본값인 4.5.0 패키지 기준이며, 4.5.0 제출에서 바꿉니다.

> MyCapture runs in the notification area. The first-run screen lets the user start a capture or skip to the library. On new installations, Ctrl+Shift+C captures a region, Ctrl+Shift+X starts or stops region recording, F3 pins clipboard content and Shift+F3 hides or shows all pins. The library shortcut is unassigned; existing installations retain their saved shortcuts. To try a pin, copy cells in a spreadsheet or text in an editor and press F3 (Pin clipboard is also in the tray menu). Ctrl+C or Ctrl+double-click on a pin copies its original text (recognized text for an image pin). Ctrl+Shift+R runs Auto-redact inside the image editor. A new installation copies each capture to the clipboard and shows the quick-access card by default. Alt+Q or a click moves focus to the card; its C/E/P/S/T keys then work.
>
> Capture, editing, recording and OCR run on the PC. The only automatic network access is downloading missing OCR model files. The optional GitHub image attachment requires the user's own GitHub account. The package's startup task is disabled at install. The first-run screen offers launch at sign-in with its checkbox selected, and the user can clear it there or change it later in Settings > General.
>
> Updates are delivered through the Microsoft Store. Settings > About lets the user check for and install Store package updates through the Store APIs, with consent before installation. After at least 10 successful captures and 3 days of use, the Store package offers one inline review invitation when the user returns to the app. It calls StoreContext.RequestRateAndReviewAppAsync only after the user chooses Review. Settings > About also offers a manual review link. The app reads the Windows Print Screen keyboard setting only to show guidance and never changes it.
