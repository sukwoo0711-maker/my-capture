# 윈도우 포럼 · 개발 / 자작

- 게시판: https://windowsforum.kr/dev
- 규칙: https://windowsforum.kr/dev/21827413 · https://windowsforum.kr/policy/5310234
- 게시: D+1 화요일 21:00 KST. D는 Store 4.4.0 게시 확인일이며 월요일을 기준으로 합니다.
- 첨부: `C:\GitRepositories\my-capture\artifacts\launch-4.4.0\redact-before-after-ko-1080x1350.png`, `C:\GitRepositories\my-capture\artifacts\launch-4.4.0\MyCapture-vertical-ko-1080x1920.mp4`
- 영상 첨부 불가 시 대체: `C:\GitRepositories\my-capture\artifacts\launch-4.4.0\cover-ko-1080x1920.png`

게시 전 확인:

- [ ] 가입 후 7일이 지났는지 확인합니다.
- [ ] 무료 자작 도구 게시가 현재 규칙에 맞는지 확인합니다. 링크만 올리지 않고 아래 설명을 함께 게시합니다.
- [ ] 영상 첨부 가능 여부를 확인합니다. 영상을 올릴 수 없으면 표지를 첨부합니다. Store 링크는 첨부 형식과 관계없이 `community-ko`를 사용합니다.

## 제목

```text
[자작] MyCapture — 개인정보 가리기와 핀을 함께 쓰는 무료 Windows 캡처 도구
```

## 본문

```text
제가 만든 MyCapture는 개발자를 위한 무료 Windows 캡처·핀 도구입니다. 고객 문의 화면을 이슈로 옮길 때는 이메일과 전화번호를 가려야 하고, 코드를 고치는 동안에는 그 화면을 옆에 두어야 하며, 며칠 뒤에는 같은 화면을 다시 찾아야 합니다. 이 세 가지를 한 도구에서, 이미지를 PC 밖으로 보내지 않고 끝내려고 만들었습니다.

편집기의 개인정보 가리기(Ctrl+Shift+R)는 로컬 OCR로 이메일, 한국 전화번호, 주민번호 형태의 숫자, 카드번호, IPv4 주소와 주요 비밀 키를 찾아 가림막을 추가합니다. 가림막은 저장 전에 옮기거나 지우고 실행 취소할 수 있습니다. 이름이나 주소를 알아서 가리는 기능은 아니며, 글자 인식이 빠진 부분도 확인해야 합니다. 직접 블러나 모자이크를 넣을 수도 있습니다.

캡처 직후 카드에서 P를 누르면 그 이미지를 다른 창 위에 핀으로 띄웁니다. 클립보드의 텍스트와 Excel 표는 트레이 메뉴의 '클립보드를 핀으로 띄우기'로 띄우고, 자주 쓴다면 설정에서 F3 같은 단축키를 지정합니다. 새로 설치했을 때 정해진 단축키는 영역 캡처(Ctrl+Shift+C)와 영역 녹화(Ctrl+Shift+X) 두 가지뿐입니다. 문의 화면을 보며 코드를 수정하거나 표를 옆에 두고 다른 자료와 비교할 때 씁니다. 확대와 투명도를 조절하고 뒤 창을 클릭하도록 설정할 수 있습니다. 닫은 핀을 다시 열 수 있지만 그 기록은 메모리에만 보관합니다.

라이브러리는 이미지와 MP4 녹화를 날짜별로 보여 줍니다. 캡처한 창 제목과 이미지 안에서 인식한 글자로 검색합니다. Excel 파일명이나 VS Code 파일·프로젝트 이름도 해당 앱이 창 제목에 넣어 준 범위에서 찾을 수 있습니다. 폴더로 정리하거나 폴더·ZIP으로 내보낼 수 있습니다.

4.4.0부터 캡처 직후에는 편집기 대신 작은 카드가 기본으로 나타납니다. 이미지는 이미 클립보드에 복사되어 있으며 E는 편집, P는 핀, S는 저장, T는 텍스트 복사, C는 복사입니다. 새 설치의 라이브러리 이미지 보관 기간도 4.4.0부터 무기한이며, 기존 사용자의 설정은 유지합니다.

무료이며 로그인 없이 사용할 수 있습니다. 광고와 캡처 횟수 제한이 없습니다. 캡처·편집·녹화·OCR은 PC에서 처리하며 캡처 자동 업로드나 분석 텔레메트리는 없습니다. 시작 시 없는 OCR 모델은 ModelScope에서, 선택 기능인 이미지 확대 모델은 처음 사용할 때 Amazon S3에서 내려받습니다. GitHub 첨부 연결은 사용자가 직접 실행할 때만 발생합니다.

Windows 11 또는 Windows 10 22H2(빌드 19045) x64용입니다. ARM64 PC는 Windows 11이 필요합니다. 녹화에는 마이크·시스템 소리가 포함되지 않습니다. Store 설치본은 Microsoft가 서명하고 Store로 업데이트합니다. GitHub 설치 파일과 포터블 ZIP은 서명이 없어 SmartScreen 경고가 나올 수 있으며, 함께 제공하는 SHA256SUMS.txt로 확인해야 합니다. GitHub 배포본은 앱 안 업데이트를 받지 않습니다.

Store: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko
소스(MIT): https://github.com/sukwoo0711-maker/my-capture
설치 파일·포터블 ZIP: https://github.com/sukwoo0711-maker/my-capture/releases
문의: https://github.com/sukwoo0711-maker/mycapture-support/issues
```

## 첫 댓글

```text
Windows 캡처 도구나 Snipaste와 무엇이 다른지 물으시면, 영역 캡처와 핀은 비슷하게 쓰고 그 앞뒤를 더했다고 답하겠습니다. 캡처한 화면에서 이메일·전화번호·카드번호·비밀 키를 OCR로 찾아 한 번에 가리는 기능과, 지난 캡처를 창 제목과 이미지 속 글자로 찾는 라이브러리가 제가 가장 신경 쓴 부분입니다. 한 사람이 만드는 무료 앱이라 사용 중 문제가 생기면 재현 순서를 알려 주시면 확인하겠습니다.
```
