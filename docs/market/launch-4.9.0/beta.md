# 첫 사용자 모집과 피드백 운영

D는 사용자가 정하는 시작일입니다. D+3부터 실제 업무에서 며칠 사용할 첫 사용자 30~50명을 모집하는 것을 목표로 둡니다. 이 수치는 목표이며 현재 이용자 수가 아닙니다. 별도 시험판 대신 배포된 Store 버전을 안내합니다. 이 문서의 메시지는 발송하지 않았습니다.

## 지인용 메시지

```text
제가 직접 만든 MyCapture는 표와 코드를 다른 창 위에 띄우는 무료 Windows 프로그램입니다. 엑셀 표나 코드를 복사하고 F3을 누르면 핀으로 뜨며, 원문이 있는 핀에 포커스를 두고 Ctrl+C를 누르면 원문을 다시 복사할 수 있습니다.

실제 문서 작업이나 개발 중에 며칠 사용하며 의견을 주실 분을 찾습니다. 4.9.0에서는 처음 사용 안내와 라이브러리·편집기를 다듬었고, 클릭 허용 상태인 핀을 다시 조작할 수 있는 손잡이를 추가했습니다.

참고 자료를 띄우는 과정이나 원문을 다시 붙여 넣는 과정에서 막힌 단계와 기대한 동작을 알려 주십시오. 캡처·화면 글자 복사·영역 녹화와 GIF 내보내기도 사용할 수 있으며 녹화에는 소리가 포함되지 않습니다.

소개: https://mimiloaf.com/mycapture/
설치: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko
```

## 커뮤니티용 모집 글

기존 소개 글의 댓글이나 모집이 허용된 게시판에만 사용합니다. 제목은 `MyCapture 표·코드 핀 사용 피드백 모집`으로 둡니다.

```text
제가 직접 만든 MyCapture는 표·코드를 다른 창 위에 띄우는 무료 Windows 프로그램입니다. 엑셀 표를 보며 문서를 작성하거나 코드를 참고하며 다른 파일을 수정할 때 사용할 수 있습니다.

첫 주에는 며칠 사용하며 의견을 주실 30~50명을 모집하는 것이 목표입니다. 먼저 표나 코드를 복사하고 F3을 누르십시오. 필요한 내용을 다른 창 위에 둔 뒤, 원문이 있는 핀에 포커스를 두고 Ctrl+C로 복사하여 원래 작업에 붙여 넣습니다.

4.9.0의 처음 사용 안내는 캡처·핀·라이브러리 순서로 따라 할 수 있습니다. 라이브러리 카드와 편집기의 저장·복사 동작도 다듬었습니다. 어떤 단계에서 사용법을 찾기 어려웠는지, 실제로 핀을 참고할 때 어떤 조작이 불편했는지 알려 주십시오.

첫날에는 핀만 사용하고, 필요할 때 캡처·OCR·영역 녹화·GIF를 추가로 사용하면 됩니다. 녹화에는 마이크와 시스템 소리가 포함되지 않습니다. 사용 의견은 긍정적인 평가나 Store 리뷰를 조건으로 받지 않습니다.

소개: https://mimiloaf.com/mycapture/
설치: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko
```

## 첫 사용 순서

1. Store의 현재 배포 버전과 지원 환경을 확인하고 설치합니다. 기능 문서가 명시한 환경은 Windows 10 22H2 x64 이상이며 ARM64의 x64 에뮬레이션은 Windows 11 이상입니다.
2. 처음 사용 안내를 따라 캡처·핀·라이브러리를 확인합니다. 안내는 트레이 메뉴와 설정에서 다시 열 수 있습니다.
3. 엑셀에서 작은 표나 편집기에서 짧은 코드를 복사하고 `F3`을 누릅니다. 새 설치의 기본키이며 이전 사용자 설정이나 다른 프로그램과의 충돌은 설정의 단축키 항목을 확인합니다.
4. 원문이 있는 핀에 포커스를 두고 `Ctrl+C`로 복사한 뒤 문서에 붙여 넣습니다. 실제 데이터 대신 샘플 표와 예제 코드를 사용합니다.
5. 클릭 허용을 켠 핀을 다시 조작하려면 오른쪽 위 손잡이로 클릭 허용을 끕니다. 몇 번의 사용 뒤에도 기능을 찾을 수 있는지 의견을 받습니다.

## 피드백 수집

어떤 일을 하려 했는지, 사용 버전·Windows 버전, 기대한 동작과 실제 결과, 재현 순서를 요청합니다. 트레이 메뉴 또는 설정 → 정보의 **문의하기**에서 진단 정보 복사와 GitHub 이슈 작성을 사용할 수 있습니다. 실제 고객 화면·계정 정보·비밀 키는 예시에 넣지 않습니다.

운영자는 설치 여부, F3로 핀을 띄운 경험, 원문 복사 성공 여부, 며칠 뒤에도 사용했는지와 이유를 기록합니다. 모집 참여자에게 전체 기능을 시험하거나 캡처 횟수를 늘리도록 요구하지 않습니다. 평점·리뷰와 맞바꾸는 보상은 제공하지 않습니다.

| 참여자 식별명 | 유입 채널 | 설치 확인 | 핀·원문 복사 경험 | 막힌 단계·피드백 | 후속 확인일 |
| --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |
|  |  |  |  |  |  |

## English invitation

```text
I built MyCapture, a free Windows tool for keeping copied tables and code above other windows. Copy a table or code snippet, press F3 to pin it, then focus a pin with source text and press Ctrl+C to copy the original text again.

I'm looking for feedback from people using this during real document or development work. Version 4.9.0 adds a first-use walkthrough and improves the library, editor and click-through pin handle. Please describe the step that was confusing and what you expected to happen. Recording and GIF export are also available; recordings have no audio.

Overview: https://mimiloaf.com/en/mycapture/
Store: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-en
```
