# GeekNews · Show

- 등록: https://news.hada.io/show
- 규칙: https://news.hada.io/guidelines
- 링크 입력란: https://github.com/sukwoo0711-maker/my-capture
- 게시: D+8 화요일 08:30 KST. D는 월요일 Store 게시 확인일 기준입니다.
- 첨부: 없음. 저장소 링크로 등록합니다.

게시 전 확인:

- [ ] 뉴스가 아닌 Show에 등록하고 Store·Releases에서 바로 사용 가능한지 확인합니다.
- [ ] 같은 프로젝트의 기존 등록을 확인합니다. 재등록 제한을 고려해 한 번의 소개 글로 준비합니다.
- [ ] 블로그·랜딩 페이지·YouTube 링크로 대체하지 않습니다.

## 제목

```text
Show GN: MyCapture - 로컬 개인정보 가리기와 핀을 묶은 Windows 캡처 앱
```

## 본문

```text
MyCapture는 제가 만든 무료 Windows 캡처 앱입니다. 고객 문의를 이슈로 옮길 때 이메일과 전화번호를 손으로 가리고, 수정하는 동안 화면을 코드 옆에 두었습니다. 나중에 같은 문의 화면을 찾는 일까지 PC 안에서 이어서 처리하려고 만들었습니다.

개인정보 가리기(Ctrl+Shift+R)는 Windows 온디바이스 OCR과 로컬 PP-OCRv5 인식 결과에서 이메일, 한국 전화번호, 주민번호 형태, 카드번호, IPv4 주소, 주요 비밀 키를 찾습니다. 탐지 결과에는 인식 문자열 대신 종류와 위치만 남기며 가림막은 저장 전에 고칠 수 있습니다. 이름·주소와 OCR이 놓친 부분은 별도 확인이 필요합니다.

F3은 클립보드의 이미지·텍스트·표를 다른 창 위에 핀으로 띄웁니다. 지원하는 VS Code HTML은 코드 색상과 들여쓰기를 유지합니다. 라이브러리는 창 제목과 이미지 속 글자로 검색하며, 파일·프로젝트 이름은 원래 앱의 제목 표시줄에 있는 범위에서 사용합니다. F9는 본인이 설정한 GitHub 이슈 페이지에 클립보드 이미지를 첨부하고 user-attachments URL을 복사합니다. 본인의 GitHub 로그인이 필요합니다.

4.4.0부터 캡처 후에는 작은 카드가 기본으로 나타나며 이미 클립보드에 복사된 상태입니다. E로 편집기를 열고 P로 핀을 띄웁니다. .NET/WPF 앱이며 소스는 MIT 라이선스입니다. 계정·광고·캡처 횟수 제한·워터마크가 없습니다.

캡처와 OCR은 로컬에서 처리하고 캡처 자동 업로드·분석 텔레메트리는 없습니다. 없는 OCR 모델은 시작 시 ModelScope에서, 선택 이미지 확대 모델은 첫 사용 시 Amazon S3에서 내려받습니다. GitHub 연결은 직접 첨부 기능을 실행할 때 발생합니다.

Windows 11 또는 Windows 10 22H2 x64용이며 ARM64는 Windows 11이 필요합니다. 녹화에는 소리가 없습니다. Microsoft가 서명한 Store 설치본은 Store로 업데이트합니다. GitHub 설치 파일·ZIP은 서명이 없고 앱 안 업데이트도 없으며, SmartScreen 경고가 날 수 있어 SHA256SUMS.txt로 확인해야 합니다.

Store: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko
소스: https://github.com/sukwoo0711-maker/my-capture
Releases: https://github.com/sukwoo0711-maker/my-capture/releases
```

## 첫 댓글

```text
개인정보 가리기가 놓치는 형식, VS Code에서 복사한 코드의 색상·들여쓰기, 4.4.0부터 기본이 된 캡처 후 카드에 대한 피드백을 받고 싶습니다. 탐지 오류 예시는 실제 키나 개인정보를 가상 값으로 바꿔 주시면 도움이 됩니다. https://github.com/sukwoo0711-maker/mycapture-support/issues 에 재현 순서와 Windows 버전을 남길 수 있습니다.
```
