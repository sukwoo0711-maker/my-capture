# 아이보스 · 무료 툴 공유

- 게시판: https://www.i-boss.co.kr/ab-6883
- 규칙: https://www.i-boss.co.kr/ab-6884-1
- 게시: D+2 수요일 10:00 KST. D는 월요일 Store 게시 확인일 기준입니다.
- 선택 첨부: `C:\GitRepositories\my-capture\artifacts\launch-4.4.0\redact-before-after-ko-1080x1350.png`

게시 전 확인:

- [ ] 직접 만든 완전 무료 도구인지, 로그인·데이터 수집·외부 서비스 범위를 본문에 적었는지 확인합니다.
- [ ] 이후 업데이트는 이 글이나 댓글에 추가하고 새 소개 글을 반복 등록하지 않습니다.

## 제목

```text
직접 만든 무료 Windows 캡처 앱 MyCapture를 공유합니다
```

## 본문

```text
만든 이유

MyCapture는 제가 만든 무료 Windows 캡처 앱입니다. 개발 업무에서 고객 문의 화면을 이슈로 옮길 때 이메일과 전화번호를 매번 손으로 가렸습니다. 수정할 때는 문의 화면을 옆에 두고, 나중에는 같은 화면을 다시 찾아야 했습니다. 보고서나 블로그에 쓸 화면을 정리하는 작업에도 이 과정이 필요할 것 같아 공유합니다.

만든 방식

.NET과 WPF로 만든 데스크톱 앱입니다. Windows 온디바이스 OCR과 로컬 PP-OCRv5 모델로 이미지 속 글자를 인식하고, 인식 결과에서 정해진 형식을 찾아 가림막을 붙입니다. 녹화에는 Windows Graphics Capture를 사용합니다. Microsoft Store로 배포하며 소스는 GitHub에 MIT 라이선스로 공개했습니다.

해결하는 문제

개인정보 가리기는 이메일, 한국 전화번호, 주민번호 형태의 숫자, 카드번호 등을 찾아 편집 가능한 가림막을 추가합니다. 이름이나 주소는 탐지 대상이 아니므로 공개할 화면은 직접 확인해야 합니다. 광고 검토 화면과 참고할 Excel 표는 핀으로 다른 창 위에 띄울 수 있습니다. 라이브러리에서는 캡처한 창 제목과 이미지 속 글자로 검색하므로, 앱이 제목 표시줄에 넣은 문서명도 찾는 데 사용할 수 있습니다.

사용 방법

Store에서 설치한 뒤 Ctrl+Shift+C로 영역을 캡처합니다. 4.4.0부터 캡처는 클립보드에 복사되고 작은 카드가 기본으로 나타납니다. E로 편집기를 열어 Ctrl+Shift+R로 개인정보 가리기를 실행한 뒤 가림막 위치를 확인합니다. 클립보드의 이미지나 표를 참고하려면 F3으로 핀을 띄웁니다. Ctrl+Shift+Z는 라이브러리이며 단축키는 설정에서 바꿀 수 있습니다.

Windows 11 또는 Windows 10 22H2(빌드 19045) x64에서 사용합니다. ARM64 PC는 Windows 11이 필요합니다. 녹화에는 소리가 없습니다. Store 설치본은 Microsoft가 서명하고 Store에서 업데이트합니다. 대체 배포인 GitHub 설치 파일·ZIP은 서명이 없어 SmartScreen 경고가 날 수 있으며 SHA256SUMS.txt 확인이 필요합니다. 앱 안 업데이트는 제공하지 않습니다.

로그인과 데이터 수집

앱 자체는 로그인 없이 사용하며 무료입니다. 광고, 캡처 횟수 제한, 워터마크가 없습니다. 캡처·편집·녹화·OCR은 PC에서 처리하고 캡처 자동 업로드나 분석 텔레메트리를 통한 데이터 수집은 없습니다. 필요한 OCR 모델이 없으면 시작 시 ModelScope(modelscope.cn)에서 내려받고, 선택 기능인 이미지 확대 모델은 처음 사용할 때 Amazon S3에서 내려받습니다. GitHub 이미지 첨부는 사용자가 직접 실행할 때만 연결하며 본인의 GitHub 로그인이 필요합니다.

Store: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko
소스: https://github.com/sukwoo0711-maker/my-capture
대체 배포: https://github.com/sukwoo0711-maker/my-capture/releases
개인정보처리방침: https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md
```

## 첫 댓글

```text
보고서나 광고 검토 자료를 만들 때 어떤 정보를 가리는지, 가림막을 고치는 데 불편한 점이 있는지 의견을 받습니다. 실제 고객 정보 대신 가상 정보로 바꾼 예시를 주시면 확인에 도움이 됩니다. 사용 중 문제는 https://github.com/sukwoo0711-maker/mycapture-support/issues 에 남길 수 있습니다. 이후 변경 내용은 이 글의 댓글에 이어 적겠습니다.
```
