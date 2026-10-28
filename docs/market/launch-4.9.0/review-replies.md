# Store 리뷰 답변 초안

매일 미응답 리뷰를 확인하고 48시간 안에 답하는 것을 운영 목표로 둡니다. 2026-10-27 [Microsoft의 리뷰·답변 안내](https://learn.microsoft.com/en-us/windows/apps/publish/analyze-msi-exe/ratings-reviews-performance)를 다시 읽었습니다. 답변은 1,000자 이내이며 광고, 다른 서비스 홍보, 평점 변경 보상을 넣을 수 없습니다. 게시한 답변은 임의 수정할 수 없으므로 실제 리뷰에 맞게 작성한 뒤 확인합니다.

아래는 답변 재료입니다. 대괄호를 실제로 확인한 사실로 채우고, 리뷰의 질문과 관계없는 문장을 덜어 냅니다. 출시 노트의 개선 내역과 특정 사용자의 문제가 해결됐는지는 따로 판단합니다. 게시 직전 최종 글자 수를 확인합니다.

## 핀 사용에 대한 긍정적 의견

```text
[표·코드를 참고한 실제 상황]에 핀을 사용하셨다는 의견 감사합니다. [유용했다고 적은 동작]을 사용 안내와 다음 변경을 검토할 때 참고하겠습니다.
```

```text
Thank you for describing how pins helped with [the actual task]. Your note about [the specific useful behavior] will help me improve the guidance and assess future changes.
```

## F3 동작 문의

```text
표·코드를 핀으로 띄우지 못한 상황을 확인했습니다. 새 설치의 기본 단축키는 F3이며 이전에 저장한 단축키는 유지됩니다. 다른 프로그램이 F3을 먼저 사용하는 경우 시작 안내와 설정의 단축키 항목을 확인해 주십시오. [확인한 상태와 다음 확인 항목]입니다.
```

```text
F3 is the default clipboard-pin shortcut on a new installation; existing saved shortcuts are kept. If another app already uses F3, please check the startup notice and shortcut settings. [Confirmed status and the next check for this review.]
```

## 원문 복사·클릭 허용 문의

```text
원문이 있는 표·코드 핀에 포커스를 두고 Ctrl+C를 누르면 원문과 이미지를 함께 복사합니다. 이미지로만 만든 핀은 같은 원문을 갖고 있지 않을 수 있습니다. 클릭 허용이 켜져 있다면 4.9.0의 핀 오른쪽 위 손잡이로 끈 뒤 확인해 주십시오. [사용자가 설명한 상황에 대한 확인 결과]입니다.
```

## 오류 신고

```text
[구체적인 오류]로 [작업에 생긴 영향]을 겪으신 점 죄송합니다. 현재 [재현 여부와 확인한 조치]입니다. 트레이 메뉴 또는 설정 → 정보의 문의하기에서 진단 정보를 복사하고 GitHub 이슈를 작성할 수 있습니다. [아직 필요한 항목]을 알려 주십시오. 실제 고객 정보와 비밀 키는 제외해 주십시오.
```

```text
I'm sorry [specific error] interrupted [the task]. [Confirmed reproduction status and action.] Contact support in the tray menu or Settings > About provides diagnostic copying and GitHub issue creation. Please include [missing details] and omit customer data and secret keys.
```

## 설치 실패

```text
[오류나 설치 단계]에서 진행하지 못하신 점을 확인했습니다. [사용 경로와 이미 시도한 조치]를 기준으로 살펴보겠습니다. 아직 제공하지 않은 Windows 버전·빌드와 오류 코드를 알려 주십시오. [확인한 지원 환경 또는 다음 조치]입니다.
```

```text
I understand installation stopped at [step or error]. I'll investigate using [the installation route and steps already tried]. Please share the Windows version/build and error code if they weren't included. [Confirmed support information or next action.]
```

## 기능 요청·낮은 평점

```text
[원하는 기능]이 [실제 작업]에 필요하다는 의견을 확인했습니다. 현재 버전은 [가능한 동작과 한계]를 제공합니다. 요청은 [실제 검토 상태]이며 [확인된 다음 조치]입니다. 구체적인 사용 상황을 남겨 주셔서 감사합니다.
```

```text
I understand why [requested capability] matters for [the task]. The current version supports [confirmed behavior and limitation]. [Actual review status and confirmed next action.] Thank you for describing the situation.
```

## 녹화 소리·인식 결과 문의

```text
MyCapture의 영역 녹화에는 마이크와 시스템 소리가 포함되지 않습니다. 소리가 필요한 작업에는 현재 녹화 기능이 맞지 않습니다. 남겨 주신 사용 상황을 기능 검토에 참고하겠습니다.
```

```text
화면 글자 인식은 작은 글자나 흐린 이미지에서 원문과 다른 결과가 나올 수 있습니다. [오류가 난 부분]은 확인 후 사용해 주십시오. 개인정보 가리기를 사용한 이미지도 공유 전 전체 화면과 가림막을 확인해야 합니다. [해당 사례의 확인 결과와 다음 조치]입니다.
```
