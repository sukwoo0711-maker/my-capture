# 첫 사용자 모집과 피드백 운영

## 목표와 기간

D는 개발자가 Microsoft Store에서 4.4.0 게시를 확인한 날입니다. D+0부터 첫 주 동안 실제 업무에서 며칠 사용하며 의견을 줄 사용자 30~50명을 모집하는 것이 목표입니다. 목표 인원이며 현재 사용자 수가 아닙니다. 별도 미공개 빌드 대신 Store 공개 버전을 안내합니다.

## 지인용 메시지

```text
제가 만든 MyCapture는 개발자를 위한 무료 Windows 캡처·핀 도구입니다. 첫 사용 의견을 받고 있습니다.
고객 문의 화면을 이슈로 옮길 때 연락처를 가리고, 고치는 동안 그 화면을 코드 옆에 띄워 두려고 만들었습니다.
로컬 OCR 기반 개인정보 가리기와 핀, 창 제목·이미지 속 글자 검색을 묶었으며 계정이나 광고는 없습니다.
Windows 11 또는 Windows 10 22H2 x64용이고 ARM64 PC는 Windows 11이 필요하며, 녹화에는 소리가 없습니다.
실제 업무에서 며칠 사용한 뒤 불편했던 상황을 알려 주시면 도움이 됩니다.
설치 주소는 https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko 입니다.
```

## 커뮤니티용 모집 글

게시판의 모집·자작 도구 규칙에 맞는 글이나 기존 소개 글의 댓글로 사용합니다.

### 제목

```text
MyCapture 실제 사용 피드백 모집
```

### 본문

```text
제가 만든 MyCapture는 개발자를 위한 무료 Windows 캡처·핀 도구입니다. 고객 문의 화면을 이슈로 옮기기 전에 연락처를 가리고, 고치는 동안 그 화면을 코드 옆에 띄워 두고, 나중에 다시 찾는 일을 한 도구에서 하려고 만들었습니다.

이번 첫 주에는 실제 업무에서 며칠 사용하며 의견을 주실 30~50명을 모집하려고 합니다. 개인정보 가리기는 PC에서 OCR로 읽은 이메일·한국 전화번호 등의 형식을 찾아 고칠 수 있는 가림막을 붙입니다. 이름·주소와 인식하지 못한 부분은 직접 확인해야 합니다. 이미지·텍스트·Excel 표는 다른 창 위에 핀으로 띄우고, 라이브러리는 창 제목과 이미지 속 글자로 검색합니다.

계정·광고·캡처 횟수 제한이 없습니다. 캡처·OCR은 로컬에서 처리하지만 모델 파일 다운로드에는 인터넷을 사용합니다. Windows 11 또는 Windows 10 22H2 x64용이고 ARM64 PC는 Windows 11이 필요합니다. 녹화에는 소리가 없습니다. Store는 Microsoft 서명과 업데이트를 제공합니다. GitHub 배포본은 서명이 없으며 앱 안 업데이트도 없습니다.

가림막이 놓친 부분이나 핀 사용 중 불편했던 상황, 4.4.0부터 기본으로 나타나는 캡처 후 카드에 대한 의견을 받고 싶습니다.

설치: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko
피드백: https://github.com/sukwoo0711-maker/mycapture-support/issues
```

## 설치 안내

1. Windows 11 또는 Windows 10 버전 22H2(빌드 19045) x64인지 확인합니다. ARM64 PC는 Windows 11의 x64 에뮬레이션이 필요합니다.
2. https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko 를 열고 Microsoft Store에서 설치합니다. Store 설치본은 Microsoft가 서명하며 업데이트도 Store에서 제공합니다.
3. 앱을 열고 Ctrl+Shift+C로 영역을 캡처합니다. 4.4.0부터 캡처는 클립보드에 복사되고 작은 카드가 기본으로 나타납니다. 카드에서 E로 편집기를 열어 개인정보 가리기(Ctrl+Shift+R)를 확인하고, P로 그 이미지를 다른 창 위에 핀으로 띄웁니다. 클립보드의 텍스트·표 핀과 라이브러리는 트레이 메뉴에서 엽니다. 새 설치에서 정해진 전역 단축키는 영역 캡처(Ctrl+Shift+C)와 영역 녹화(Ctrl+Shift+X)뿐이며, 나머지는 설정에서 지정합니다.

대안으로 https://github.com/sukwoo0711-maker/my-capture/releases 에서 포터블 ZIP과 같은 릴리스의 `SHA256SUMS.txt`를 받습니다. ZIP의 SHA-256을 대조한 뒤 전체를 한 폴더에 풀고 `MyCapture.exe`를 실행합니다. GitHub 설치 파일과 ZIP은 서명이 없어 SmartScreen 경고가 나올 수 있습니다. SHA-256 대조는 다운로드 파일의 일치 여부를 확인하는 절차입니다. 조직에서 실행을 제한한 PC는 허용된 설치 경로를 사용합니다. 이 배포본은 앱 안 업데이트를 받지 않습니다.

제거할 때는 필요한 캡처를 라이브러리에서 폴더나 ZIP으로 내보낸 뒤 트레이 메뉴에서 종료합니다. Store 설치본은 Windows 설정 > 앱 > 설치된 앱(Windows 10은 앱 및 기능)에서 MyCapture의 제거를 선택합니다. 포터블은 종료한 뒤 압축을 풀었던 정확한 폴더를 확인하여 Windows 휴지통으로 이동합니다. 별도로 내보낸 이미지와 녹화 파일은 보존합니다.

## 피드백 받는 방법

지원 이슈는 https://github.com/sukwoo0711-maker/mycapture-support/issues 에서 받습니다. 어떤 작업 중 문제가 생겼는지, 기대한 동작과 실제 결과, 재현 순서를 요청합니다. 예시는 실제 고객 정보나 비밀 키를 가상 값으로 바꾼 자료로 받습니다.

4.4.0부터 트레이 메뉴와 설정 > 정보에 문의하기 창이 있습니다. 앱 버전, Windows 버전·빌드, 화면 배율, UI 언어, 테마로 구성된 진단 정보를 복사하거나 내용이 미리 채워진 GitHub 이슈를 열 수 있습니다. 진단 블록에는 사용자 콘텐츠가 들어가지 않으며 이슈 제출에는 GitHub 계정이 필요합니다. Store 설치본의 문의하기에는 Store 리뷰 연결도 있습니다.

Store 리뷰는 설치 후 실제로 사용한 사람이 원할 때 남깁니다. Store 설치본의 설정 > 업데이트에 수동 리뷰 링크가 있습니다.

## 요청할 사용 방식

며칠 동안 실제 문의 화면이나 오류 화면을 정리하는 데 사용합니다. 민감한 원본은 공유하지 않고 가림막을 고치는 과정, 핀으로 참고 화면을 두는 과정, 라이브러리에서 다시 찾는 과정에서 막힌 상황을 기록합니다. 모든 기능을 한 번에 시험하도록 요구하지 않습니다.

Store 설치본은 3일 이상에 걸쳐 성공한 캡처가 10회 이상이면 평점 요청을 한 번 표시하고 다시 요청하지 않습니다. 조건을 채우기 위해 캡처 횟수를 늘리도록 권하지 않습니다. 리뷰는 사용 후 본인이 원할 때 솔직하게 작성하며 평점이나 긍정적인 내용은 지정하지 않습니다.

## 지킬 것

평점·리뷰와 맞바꾸는 보상, 할인, 선물은 제공하지 않습니다. 별점 5개를 부탁하거나 사용 전에 평가를 요청하지 않습니다. 불편한 경험을 보낸 사람에게 평점을 바꾸는 대가를 제안하지 않습니다. 모집 메시지와 리뷰 요청은 별개이며 리뷰는 모집 참여 조건이 아닙니다.

기준: [Microsoft Store 정책](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies), [평점·리뷰·답변 안내](https://learn.microsoft.com/en-us/windows/apps/publish/analyze-msi-exe/ratings-reviews-performance).

## 기록 표

초대한 사람은 공개 이름 대신 운영자가 알아볼 수 있는 식별명으로 적습니다. 연락처와 실제 고객 화면은 이 표에 넣지 않습니다.

| 초대한 사람 | 채널 | 설치 여부 | 피드백 수신 | 후속 확인일 |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |
|  |  |  |  |  |
|  |  |  |  |  |
|  |  |  |  |  |

## English recruiting message

```text
I built MyCapture, a free Windows screenshot and pinning tool for developers. I'm looking for feedback from people using it in their daily work.
For an issue report, I can check a screenshot for contact details and keep it beside my code while fixing the problem.
It combines local OCR redaction with pins and library search by window title or text inside images, with no app account or ads.
It requires Windows 11 or Windows 10 22H2 x64 (ARM64 PCs need Windows 11), and recordings have no audio.
After a few days of use, I'd appreciate the specific situations that got in your way.
Store: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-en
```
